using System.Net;
using System.Net.Http.Json;
using Game.Client.Models;
using Game.Client.Services;
using Microsoft.AspNetCore.Components;

namespace Game.Client.Shared;

// --- ⚔️ SERVER-AUTHORITATIVE TURN-BASED COMBAT ---
// The API owns the battle: we send the chosen card and replay what the server decided.
public partial class BossBattle : IDisposable
{
    [Inject] private GameState State { get; set; } = default!;
    [Inject] private HttpClient Http { get; set; } = default!;
    [Inject] private SoundEffects Sounds { get; set; } = default!;

    /// <summary>The battle to show, as the server started (or resumed) it.</summary>
    [Parameter] public BossBattleStart? Start { get; set; }

    /// <summary>"Fight Again" started a new battle; the page passes it back in as <see cref="Start"/>.</summary>
    [Parameter] public EventCallback<BossBattleStart> OnBattleStarted { get; set; }

    private BossBattleStart? shownStart; // The Start last shown, so a re-render of the page doesn't restart it
    private bool disposed;

    // Arena State (mirrors the server's battle; the server decides every outcome)
    private Guid battleId;
    private int playerHp = 100;
    private int playerMaxHp = 100; // More than 100 when a Battle Elixir was drunk
    private List<BattleCardDto> heldCards = new(); // The cards as upgraded in the Gold Shop
    private int enemyHp = 160;
    private int enemyMaxHp = 160;
    private bool heroic; // Fighting the Heroic boss, for players with every card maxed out
    private string bossName = "The Shadow Overlord";
    private int enrageBelowPercent = 40;
    private bool bossEnraged; // Below 40% health the boss's attacks hit 50% harder
    private int enemyIntentAttack = 15; // Damage of the boss's announced move
    private string bossNextMove = "Slash";
    private string bossNextMoveName = "Slash";
    private string? rechargingCard; // Dragon Claw and Holy Shield can't be played two turns in a row
    private int turnCount = 1;
    private bool isPlayerTurn = true; // Prevents button click spamming
    private bool matchConcluding = false; // Locks inputs during network saves
    private BattleResultView? battleResult; // Set when the server ends the battle
    private List<string> logs = new() { "⚔️ Match Initialized. Select an action card block." };

    /// <summary>
    /// Asks the server for a boss battle, or the one already in progress. Error says why it failed
    /// (both are null when the server answered without a battle). Used by the town and by "Fight Again".
    /// </summary>
    public static async Task<(BossBattleStart? Start, string? Error)> StartAsync(HttpClient http, bool heroic)
    {
        try
        {
            var res = await http.PostAsync(heroic ? "/api/v1/battles/pve?difficulty=Heroic" : "/api/v1/battles/pve", null);
            if (!res.IsSuccessStatusCode)
            {
                return (null, res.StatusCode == HttpStatusCode.BadRequest
                    ? await ValidationProblemDto.ReadMessageAsync(res)
                    : "The arena gates are closed. Please try again.");
            }

            var battle = await res.Content.ReadFromJsonAsync<BattleStateDto>();
            // 200 OK (rather than 201 Created) is the battle that was already in progress.
            return (battle is null ? null : new BossBattleStart(battle, Resumed: res.StatusCode == HttpStatusCode.OK), null);
        }
        catch { return (null, "API Network Link Offline."); }
    }

    protected override void OnParametersSet()
    {
        if (Start is null || ReferenceEquals(Start, shownStart)) return;
        shownStart = Start;
        ResetMatch();
        ApplyBattleState(Start.Battle);
        if (Start.Resumed)
        {
            logs.Add($"↩️ Resuming your battle on turn {Start.Battle.turn}.");
        }
    }

    private async Task FightAgain()
    {
        // A failure here used to be kept for the town screen's message only; the result panel stays as it is.
        var (start, _) = await StartAsync(Http, heroic);
        if (start is not null) await OnBattleStarted.InvokeAsync(start);
    }

    private async Task PlayTurn(string card)
    {
        if (!isPlayerTurn || matchConcluding || enemyHp <= 0 || playerHp <= 0) return;

        isPlayerTurn = false; // One card at a time: the next can be played once the server has answered
        FinishBossTurn();     // Playing on before the boss's turn has been told tells the rest of it at once

        PlayCardResponseDto? result;
        try
        {
            var res = await Http.PostAsJsonAsync($"/api/v1/battles/pve/{battleId}/turns", new { Card = card });
            if (!res.IsSuccessStatusCode)
            {
                logs.Add($"[API Rejection] The move was not accepted: {await res.Content.ReadAsStringAsync()}");
                isPlayerTurn = true;
                return;
            }
            result = await res.Content.ReadFromJsonAsync<PlayCardResponseDto>();
        }
        catch (Exception ex)
        {
            logs.Add($"[Network Error] Could not reach the arena: {ex.Message}");
            isPlayerTurn = true;
            return;
        }
        if (result is null) { isPlayerTurn = true; return; }

        var turn = result.turnResult;

        // 1. SHOW THE PLAYER'S CARD
        if (turn.cardFailed)
        {
            logs.Add($"🌀 Turn {turn.turn}: Your {turn.cardName} was {turn.cardFailedReason} by {bossName}!");
            _ = Sounds.PlayAsync(Sound.Miss);
        }
        else if (turn.damageDealt > 0)
        {
            enemyHp = Math.Max(0, enemyHp - turn.damageDealt);
            logs.Add($"» Turn {turn.turn}: You cast {turn.cardName} dealing {turn.damageDealt} DMG.");
            _ = Sounds.PlayAsync(SoundEffects.ForCard(turn.cardName));
        }
        else if (card == "HolyShield")
        {
            playerHp += turn.healthRestored;
            logs.Add($"» Turn {turn.turn}: You raise {turn.cardName} and recover {turn.healthRestored} HP.");
            _ = Sounds.PlayAsync(Sound.Shield);
        }

        // 2. THE BOSS'S COUNTER-ATTACK, TOLD AFTER A SHORT DRAMATIC PAUSE
        if (turn.bossDamage is int bossDamage)
        {
            logs.Add($"⏳ {bossName} winds up a {turn.bossMoveName}...");
            bossTurn.Enqueue(turn.attackBlocked
                ? new($"🛡️ Your shield blocks the {turn.bossMoveName}! No damage taken.", Sound.Block)
                : new($"💥 {turn.bossMoveName} hits you for {bossDamage} damage!", Sound.Hit));
            if (turn.bossHealed > 0)
            {
                bossTurn.Enqueue(new($"🩸 {bossName} drains {turn.bossHealed} HP back.", Sound.Drain));
            }
        }

        // 3. THE SERVER'S VERDICT: NEW HEALTH, THE BOSS'S NEXT MOVE, OR THE END OF THE FIGHT
        var wasEnraged = bossEnraged;
        bossTurn.Enqueue(new(null, Apply: quietly =>
        {
            ApplyBattleState(result.battle);
            if (result.battle.status != "InProgress")
            {
                HandleMatchResolution(result);
                return;
            }
            if (bossEnraged && !wasEnraged)
            {
                logs.Add($"😡 {bossName} is badly hurt and flies into a rage! Its attacks now hit 50% harder.");
                if (!quietly) _ = Sounds.PlayAsync(Sound.Drain);
            }
            logs.Add($"⚠️ {bossName} prepares {bossNextMoveName} for {enemyIntentAttack} DMG.");
        }));

        // 4. UNLOCK CONTROLS STRAIGHT AWAY: the card just played recharges, and any other can be played
        // while the boss's turn is still being told.
        rechargingCard = result.battle.rechargingCard;
        isPlayerTurn = result.battle.status == "InProgress";
        if (turn.bossDamage is null)
        {
            FinishBossTurn();
            return;
        }
        StateHasChanged(); // show the unlocked cards now, not when the pause is over
        await TellBossTurnAsync();
    }

    /// <summary>A line of the boss's turn, told after the pause; Apply runs first and is told whether sounds are skipped.</summary>
    private sealed record BossBeat(string? Log, Sound? Sound = null, Action<bool>? Apply = null);

    private static readonly TimeSpan BossTurnPause = TimeSpan.FromMilliseconds(900);
    private readonly Queue<BossBeat> bossTurn = new();
    private CancellationTokenSource? bossTurnPause;

    private async Task TellBossTurnAsync()
    {
        if (disposed)
        {
            FinishBossTurn(); // the player left the arena while the turn was on its way; no pause to wait out
            return;
        }
        bossTurnPause = new CancellationTokenSource();
        try
        {
            await Task.Delay(BossTurnPause, bossTurnPause.Token);
        }
        catch (TaskCanceledException)
        {
            return; // the player played on (or left the arena); FinishBossTurn told the rest
        }
        TellBossTurn(quietly: false);
        StateHasChanged();
    }

    /// <summary>Tells whatever is left of the boss's turn at once, without its sounds.</summary>
    private void FinishBossTurn()
    {
        bossTurnPause?.Cancel();
        bossTurnPause = null;
        TellBossTurn(quietly: true);
    }

    private void TellBossTurn(bool quietly)
    {
        while (bossTurn.TryDequeue(out var beat))
        {
            beat.Apply?.Invoke(quietly);
            if (beat.Log is not null) logs.Add(beat.Log);
            if (!quietly && beat.Sound is { } sound) _ = Sounds.PlayAsync(sound);
        }
    }

    private void ApplyBattleState(BattleStateDto battle)
    {
        battleId = battle.id;
        playerHp = battle.playerHp;
        playerMaxHp = battle.playerMaxHp;
        heldCards = battle.cards;
        enemyHp = battle.bossHp;
        enemyMaxHp = battle.bossMaxHp;
        heroic = battle.difficulty == "Heroic";
        bossName = battle.bossName;
        enrageBelowPercent = battle.enrageBelowPercent;
        bossEnraged = battle.bossEnraged;
        enemyIntentAttack = battle.bossNextAttack;
        bossNextMove = battle.bossNextMove;
        bossNextMoveName = battle.bossNextMoveName;
        rechargingCard = battle.rechargingCard;
        turnCount = battle.turn;
    }

    private void HandleMatchResolution(PlayCardResponseDto result)
    {
        matchConcluding = true; // Lock all inputs

        var won = result.battle.status == "Won";
        _ = Sounds.PlayAsync(won ? Sound.Victory : Sound.Defeat);
        logs.Add(won
            ? "🏆 VICTORY! The server confirmed your win."
            : $"💀 DEFEAT! {bossName} prevails.");

        var levelBefore = State.Level;
        if (result.reward is { } reward)
        {
            logs.Add($"🪙 +{reward.goldEarned} gold, +{reward.experienceEarned} XP.");
            if (reward.streakBonus > 0) logs.Add($"🔥 Win streak bonus: +{reward.streakBonus} gold.");
            foreach (var bounty in reward.bountiesCompleted) logs.Add($"📜 Bounty complete: {bounty.name} (+{bounty.reward} gold).");
            // Sync the header with the balances the server just saved
            State.UpdateRewards(reward.player.gold, reward.player.level);
        }

        // Stay in the arena and show the result until the player chooses what to do next
        battleResult = new BattleResultView(
            won,
            result.reward?.goldEarned ?? 0,
            result.reward?.experienceEarned ?? 0,
            State.Level > levelBefore,
            State.Level,
            result.reward?.streakBonus ?? 0,
            result.reward?.bountiesCompleted.Select(b => new RewardBonuses.BountyLine(b.name, b.reward)).ToList() ?? [],
            result.reward?.reducedBossReward ?? false,
            result.reward?.fullRewardBossWinsLeft);
        StateHasChanged();
    }

    private void ReturnToTown()
    {
        ResetMatch();
        State.ChangeScreen(GameScreen.CharacterDashboard);
    }

    private record BattleResultView(bool won, int goldEarned, int experienceEarned, bool leveledUp, int newLevel,
        int streakBonus, IReadOnlyList<RewardBonuses.BountyLine> bounties, bool reducedBossReward, int? fullRewardWinsLeft);

    private void ResetMatch()
    {
        bossTurnPause?.Cancel();
        bossTurnPause = null;
        bossTurn.Clear();
        battleId = Guid.Empty;
        playerHp = 100;
        enemyHp = 160;
        bossEnraged = false;
        enemyIntentAttack = 15;
        bossNextMove = "Slash";
        bossNextMoveName = "Slash";
        rechargingCard = null;
        turnCount = 1;
        isPlayerTurn = true;
        matchConcluding = false;
        battleResult = null;
        logs = new() { "⚔️ Match Initialized. Select an action card block." };
    }

    private static string BossMoveIcon(string move) => move switch
    {
        "CrushingBlow" => "🔨",
        "LifeDrain" => "🩸",
        _ => "⚔️"
    };

    // Leaving the arena mid-turn (the header's Town button) cancels the boss's pause and settles the rest
    // of its turn at once, so the rewards of a finished fight still reach the header.
    void IDisposable.Dispose()
    {
        disposed = true;
        FinishBossTurn();
    }
}
