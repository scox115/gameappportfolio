// Wakes the game server while the game is still downloading. The API scales to zero and its database
// pauses when nobody is playing, so the first request after a quiet spell can take a minute. Asking
// for /health/ready straight away starts both waking, at the same time as the browser fetches the
// game, and Layout/ServerWake.razor reads the result to tell the player what's happening.
// The API's address comes from the api-origin meta tag the deploy fills in; without one (running
// locally) there is nothing to wake.
window.gameServer = (() => {
    const origin = document.querySelector('meta[name="api-origin"]')?.content ?? "";
    const startedAt = Date.now();
    const giveUpAfterMs = 3 * 60 * 1000;
    let status = origin ? "waking" : "awake";

    async function check() {
        try {
            // A replica starting from zero holds the request until it's up; one asleep too long is retried.
            const response = await fetch(`${origin}/health/ready`, { cache: "no-store", signal: AbortSignal.timeout(30_000) });
            if (response.ok) {
                status = "awake";
                return;
            }
        } catch {
            // Not up yet, or the network dropped: try again below.
        }
        if (Date.now() - startedAt > giveUpAfterMs) {
            status = "unreachable";
            return;
        }
        setTimeout(check, 3000);
    }

    if (origin) check();

    return {
        // "waking", "awake" or "unreachable", and how long since the page opened.
        state: () => ({ status, seconds: Math.floor((Date.now() - startedAt) / 1000) }),
        retry: () => {
            if (status !== "unreachable") return;
            status = "waking";
            check();
        }
    };
})();
