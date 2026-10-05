// Battle sound effects, synthesized with the Web Audio API so the game ships no audio files.
// The on/off choice is saved in this browser's localStorage.
window.gameSound = (() => {
    const storageKey = "kotca.soundEnabled";
    let context = null;

    const load = () => {
        try { return localStorage.getItem(storageKey) !== "false"; } catch { return true; }
    };
    let enabled = load();

    // Browsers only allow audio after a click or key press, which every sound here follows.
    const audio = () => {
        if (!context) context = new (window.AudioContext || window.webkitAudioContext)();
        if (context.state === "suspended") context.resume();
        return context;
    };

    // A note that glides from one pitch to another and fades out.
    const tone = (type, from, to, start, length, volume = 0.2) => {
        const ctx = audio();
        const osc = ctx.createOscillator();
        const gain = ctx.createGain();
        const t = ctx.currentTime + start;
        osc.type = type;
        osc.frequency.setValueAtTime(from, t);
        osc.frequency.exponentialRampToValueAtTime(to, t + length);
        gain.gain.setValueAtTime(volume, t);
        gain.gain.exponentialRampToValueAtTime(0.001, t + length);
        osc.connect(gain).connect(ctx.destination);
        osc.start(t);
        osc.stop(t + length);
    };

    // A burst of filtered noise, for whooshes and impacts.
    const noise = (start, length, filterFrom, filterTo, volume = 0.3) => {
        const ctx = audio();
        const buffer = ctx.createBuffer(1, Math.floor(ctx.sampleRate * length), ctx.sampleRate);
        const data = buffer.getChannelData(0);
        for (let i = 0; i < data.length; i++) data[i] = Math.random() * 2 - 1;
        const source = ctx.createBufferSource();
        const filter = ctx.createBiquadFilter();
        const gain = ctx.createGain();
        const t = ctx.currentTime + start;
        source.buffer = buffer;
        filter.type = "bandpass";
        filter.frequency.setValueAtTime(filterFrom, t);
        filter.frequency.exponentialRampToValueAtTime(filterTo, t + length);
        gain.gain.setValueAtTime(volume, t);
        gain.gain.exponentialRampToValueAtTime(0.001, t + length);
        source.connect(filter).connect(gain).connect(ctx.destination);
        source.start(t);
    };

    const effects = {
        fireball: () => { noise(0, 0.5, 400, 3000, 0.35); tone("sawtooth", 120, 60, 0.05, 0.4, 0.1); },
        claw: () => { noise(0, 0.15, 3000, 800, 0.4); noise(0.12, 0.15, 3000, 800, 0.4); tone("square", 200, 80, 0.05, 0.2, 0.08); },
        shield: () => { tone("sine", 660, 990, 0, 0.35, 0.18); tone("sine", 990, 1320, 0.08, 0.4, 0.12); },
        hit: () => { noise(0, 0.25, 900, 150, 0.45); tone("triangle", 140, 50, 0, 0.3, 0.25); },
        block: () => { tone("square", 1200, 900, 0, 0.12, 0.12); tone("sine", 1800, 1500, 0.02, 0.3, 0.1); },
        miss: () => { noise(0, 0.3, 1500, 4000, 0.15); tone("sine", 500, 250, 0, 0.3, 0.08); },
        drain: () => { tone("sawtooth", 300, 120, 0, 0.6, 0.08); tone("sine", 200, 400, 0.2, 0.5, 0.1); },
        victory: () => [523, 659, 784, 1047].forEach((f, i) => tone("triangle", f, f, i * 0.15, i === 3 ? 0.6 : 0.18, 0.2)),
        defeat: () => [392, 330, 262, 196].forEach((f, i) => tone("triangle", f, f * 0.98, i * 0.22, i === 3 ? 0.7 : 0.25, 0.18)),
        matchFound: () => { tone("square", 440, 440, 0, 0.12, 0.1); tone("square", 660, 660, 0.14, 0.2, 0.1); },
        yourTurn: () => tone("sine", 880, 880, 0, 0.15, 0.12)
    };

    return {
        isEnabled: () => enabled,
        setEnabled: on => {
            enabled = on;
            try { localStorage.setItem(storageKey, String(on)); } catch { /* private mode: the choice lasts this visit */ }
        },
        play: name => {
            if (!enabled || !effects[name]) return;
            try { effects[name](); } catch { /* no audio device; play silently */ }
        }
    };
})();
