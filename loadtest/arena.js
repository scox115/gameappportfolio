// Load test for Kings of the Card Arena, run with Grafana k6 (free and open source).
//
// Every virtual user (VU) is one player. Boss fighters register a hero, then fight the boss
// over the REST API at human speed (about a second between cards) and look at the town screen
// between fights. Duelists connect to the SignalR arena hub over a WebSocket, queue for a
// friendly duel, and play Fireball whenever it is their turn.
//
// Settings (environment variables, all optional):
//   BASE_URL   the API address                    default http://localhost:5005
//   PROFILE    smoke | load | stress               default load
//   BOSS_VUS   peak boss fighters (load/stress)    default 40 / 150
//   DUEL_VUS   duelists, kept even (load/stress)   default 20 / 50
//   RESULTS    folder for summary.json/summary.md  default the current folder
//
// The API's per-address sign-up limit (3 an hour) stops this from a single machine, so point
// it at a copy of the API with AntiCheat:RegistrationsPerHour lifted (loadtest/compose.yaml
// does that), never at the live game.

import http from 'k6/http';
import { check, fail, sleep } from 'k6';
import { Counter, Rate, Trend } from 'k6/metrics';
import { WebSocket } from 'k6/websockets';
import { setTimeout, clearTimeout, setInterval, clearInterval } from 'k6/timers';

const BASE_URL = (__ENV.BASE_URL || 'http://localhost:5005').replace(/\/$/, '');
const PROFILE = __ENV.PROFILE || 'load';
const PASSWORD = 'Load-Test-1';

const bossFights = new Counter('boss_fights_completed');
const bossFightTime = new Trend('boss_fight_duration', true);
const duels = new Counter('duels_completed');
const duelWait = new Trend('duel_matchmaking_wait', true);
const duelTurn = new Trend('duel_turn_latency', true);
const duelFailed = new Rate('duel_failed');

const profiles = {
  smoke: { boss: [{ duration: '30s', target: 2 }], duelVus: 2, duelTime: '30s' },
  load: {
    boss: [
      { duration: '1m', target: int('BOSS_VUS', 40) },
      { duration: '3m', target: int('BOSS_VUS', 40) },
      { duration: '30s', target: 0 },
    ],
    duelVus: even(int('DUEL_VUS', 20)),
    duelTime: '4m30s',
  },
  // Keeps adding players until something gives, to find where the API tops out.
  stress: {
    boss: [
      { duration: '2m', target: Math.round(int('BOSS_VUS', 150) / 3) },
      { duration: '2m', target: Math.round((int('BOSS_VUS', 150) * 2) / 3) },
      { duration: '2m', target: int('BOSS_VUS', 150) },
      { duration: '1m', target: 0 },
    ],
    duelVus: even(int('DUEL_VUS', 50)),
    duelTime: '7m',
  },
};

const profile = profiles[PROFILE] || fail(`Unknown PROFILE "${PROFILE}". Use smoke, load or stress.`);

export const options = {
  scenarios: {
    boss: { executor: 'ramping-vus', exec: 'bossFighter', startVUs: 0, stages: profile.boss, gracefulRampDown: '30s' },
    duel: { executor: 'constant-vus', exec: 'duelist', vus: profile.duelVus, duration: profile.duelTime },
  },
  // What "the game feels fine" means. A run that breaks one of these fails.
  thresholds: {
    'http_req_failed{kind:game}': ['rate<0.01'],
    'http_reqs{kind:game}': ['count>0'],
    'http_req_duration{kind:game}': ['p(95)<500'],
    duel_turn_latency: ['p(95)<500'],
    duel_failed: ['rate<0.05'],
    checks: ['rate>0.99'],
  },
  summaryTrendStats: ['avg', 'med', 'p(95)', 'p(99)', 'max'],
};

// Waits for the API (a fresh container runs its database migrations first).
export function setup() {
  for (let attempt = 0; attempt < 60; attempt++) {
    const res = http.get(`${BASE_URL}/health/ready`, {
      tags: { kind: 'setup' },
      responseCallback: http.expectedStatuses(200, 503),
    });
    if (res.status === 200) return;
    sleep(2);
  }
  fail(`${BASE_URL}/health/ready never answered 200.`);
}

// Each VU signs up once and keeps its hero for the whole run.
let hero = null;

function signUp(heroClass) {
  const username = `lt${__VU}x${Math.random().toString(36).slice(2, 10)}`;
  const res = http.post(
    `${BASE_URL}/api/auth/register`,
    JSON.stringify({ username, password: PASSWORD, class: heroClass }),
    { headers: { 'Content-Type': 'application/json' }, tags: { kind: 'signup', name: 'POST /api/auth/register' } },
  );
  if (!check(res, { 'hero created': (r) => r.status === 201 })) {
    fail(`Sign-up failed with ${res.status}: ${res.body}`);
  }
  return { username, token: res.json('accessToken') };
}

function api(method, path, body) {
  const params = {
    headers: { Authorization: `Bearer ${hero.token}`, 'Content-Type': 'application/json' },
    tags: { kind: 'game', name: `${method} ${path.replace(/[0-9a-f-]{36}/g, '{id}')}` },
  };
  return http.request(method, `${BASE_URL}${path}`, body === undefined ? null : JSON.stringify(body), params);
}

export function bossFighter() {
  if (!hero) hero = signUp('Sorcerer');

  // The town screen.
  check(api('GET', '/api/players/me'), { 'profile loaded': (r) => r.status === 200 });
  api('GET', '/api/players/leaderboard');
  api('GET', '/api/players/me/matches?limit=5');

  const started = Date.now();
  let res = api('POST', '/api/battles/pve');
  if (!check(res, { 'battle started': (r) => r.status === 200 || r.status === 201 })) return;
  let battle = res.json();

  // Dragon Claw when it is ready, Fireball while it recharges; Holy Shield when the next hit would be fatal.
  for (let turn = 0; turn < 60 && battle.status === 'InProgress'; turn++) {
    sleep(0.8 + Math.random() * 0.8);
    const card = battle.bossNextAttack >= battle.playerHp && battle.rechargingCard !== 'HolyShield'
      ? 'HolyShield'
      : battle.rechargingCard === 'DragonClaw' ? 'Fireball' : 'DragonClaw';
    res = api('POST', `/api/battles/pve/${battle.id}/turns`, { card });
    if (!check(res, { 'card played': (r) => r.status === 200 })) return;
    battle = res.json('battle');
  }

  if (battle.status !== 'InProgress') {
    bossFights.add(1);
    bossFightTime.add(Date.now() - started);
  }
  sleep(2 + Math.random() * 3);
}

// SignalR's JSON protocol: every message ends with the 0x1e record separator.
const RS = '\u001e';
const frame = (message) => JSON.stringify(message) + RS;

export async function duelist() {
  if (!hero) hero = signUp('Paladin');

  const negotiate = http.post(`${BASE_URL}/hubs/arena/negotiate?negotiateVersion=1`, null, {
    headers: { Authorization: `Bearer ${hero.token}` },
    tags: { kind: 'game', name: 'POST /hubs/arena/negotiate' },
  });
  if (!check(negotiate, { 'hub negotiated': (r) => r.status === 200 })) {
    duelFailed.add(1);
    return;
  }
  const connectionToken = negotiate.json('connectionToken');
  const wsUrl = `${BASE_URL.replace(/^http/, 'ws')}/hubs/arena?id=${connectionToken}&access_token=${hero.token}`;

  const outcome = await new Promise((resolve) => {
    const ws = new WebSocket(wsUrl);
    const queuedAt = Date.now();
    let invocation = 0;
    let cardSentAt = 0;
    let lastTurn = -1;
    let matched = false;
    let pinger = null;

    // Both players time out after the 30-second turn timer, so give up well after that.
    const giveUp = setTimeout(() => finish('timed out'), 90_000);

    function finish(result) {
      clearTimeout(giveUp);
      if (pinger) clearInterval(pinger);
      ws.close();
      resolve(result);
    }

    function invoke(target, ...args) {
      ws.send(frame({ type: 1, invocationId: String(++invocation), target, arguments: args }));
    }

    function onUpdate(update) {
      const b = update.battle;
      if (!matched) {
        matched = true;
        duelWait.add(Date.now() - queuedAt);
      }
      if (cardSentAt && b.turn !== lastTurn) {
        duelTurn.add(Date.now() - cardSentAt);
        cardSentAt = 0;
      }
      lastTurn = b.turn;
      if (b.status !== 'InProgress') {
        finish('finished');
        return;
      }
      if (b.yourTurn && !cardSentAt) {
        // A person takes a moment to pick a card.
        setTimeout(() => {
          cardSentAt = Date.now();
          invoke('PlayCard', b.id, 'Fireball');
        }, 800 + Math.random() * 800);
      }
    }

    ws.addEventListener('open', () => {
      ws.send(frame({ protocol: 'json', version: 1 }));
      pinger = setInterval(() => ws.send(frame({ type: 6 })), 10_000);
      invoke('FindOpponent');
    });

    ws.addEventListener('message', (event) => {
      for (const raw of String(event.data).split(RS)) {
        if (!raw) continue;
        const message = JSON.parse(raw);
        if (message.type === 1 && (message.target === 'MatchFound' || message.target === 'BattleUpdated')) {
          onUpdate(message.arguments[0]);
        } else if (message.type === 3 && message.error) {
          finish(`hub error: ${message.error}`);
        } else if (message.type === 7) {
          finish(`closed by server: ${message.error || 'no reason'}`);
        }
      }
    });

    ws.addEventListener('error', (e) => finish(`socket error: ${e.error}`));
  });

  const ok = check(outcome, { 'duel finished': (o) => o === 'finished' });
  duelFailed.add(!ok);
  if (ok) duels.add(1);
  else console.warn(`VU ${__VU}: duel ${outcome}`);
  sleep(2 + Math.random() * 3);
}

export function handleSummary(data) {
  const folder = (__ENV.RESULTS || '.').replace(/\/$/, '');
  const markdown = summaryMarkdown(data);
  return {
    stdout: markdown,
    [`${folder}/summary.md`]: markdown,
    [`${folder}/summary.json`]: JSON.stringify(data, null, 2),
  };
}

function summaryMarkdown(data) {
  const m = data.metrics;
  const ms = (metric, stat) => (m[metric] ? `${Math.round(m[metric].values[stat])} ms` : 'n/a');
  const count = (metric) => (m[metric] ? m[metric].values.count : 0);
  const pct = (metric) => (m[metric] ? `${(m[metric].values.rate * 100).toFixed(2)}%` : 'n/a');
  const minutes = data.state.testRunDurationMs / 60000;
  const failed = Object.entries(m).filter(([, v]) => v.thresholds && Object.values(v.thresholds).some((t) => !t.ok));

  const lines = [
    `## Load test: ${PROFILE} profile against ${BASE_URL}`,
    '',
    `${failed.length === 0 ? '✅ All thresholds passed.' : `❌ Thresholds broken: ${failed.map(([name]) => name).join(', ')}.`}`,
    '',
    '| Measure | Result |',
    '|---|---|',
    `| Peak players (VUs) | ${m.vus_max ? m.vus_max.values.max : 'n/a'} |`,
    `| Game requests | ${count('http_reqs{kind:game}')} (${(m['http_reqs{kind:game}'] ? m['http_reqs{kind:game}'].values.rate : 0).toFixed(1)}/s) |`,
    `| Request time, median / p95 / p99 | ${ms('http_req_duration{kind:game}', 'med')} / ${ms('http_req_duration{kind:game}', 'p(95)')} / ${ms('http_req_duration{kind:game}', 'p(99)')} |`,
    `| Failed requests | ${pct('http_req_failed{kind:game}')} |`,
    `| Boss fights finished | ${count('boss_fights_completed')} (${(count('boss_fights_completed') / minutes).toFixed(1)}/min) |`,
    `| Duels finished | ${count('duels_completed') / 2} (${(count('duels_completed') / 2 / minutes).toFixed(1)}/min) |`,
    `| Duel turn, server round trip p95 | ${ms('duel_turn_latency', 'p(95)')} |`,
    `| Duel matchmaking wait p95 | ${ms('duel_matchmaking_wait', 'p(95)')} |`,
    `| Failed duels | ${pct('duel_failed')} |`,
    `| Checks passed | ${m.checks ? (m.checks.values.rate * 100).toFixed(2) : 'n/a'}% |`,
    '',
  ];
  return lines.join('\n');
}

function int(name, fallback) {
  const value = parseInt(__ENV[name], 10);
  return Number.isNaN(value) ? fallback : value;
}

function even(n) {
  return Math.max(2, n + (n % 2));
}
