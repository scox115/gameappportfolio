// Tracks when the player last used the mouse, keyboard or touch screen, so the Blazor app
// can sign out an idle session. Listeners are passive and do no work beyond saving a time.
window.gameIdle = (() => {
    let lastActivity = Date.now();
    const markActive = () => { lastActivity = Date.now(); };

    for (const name of ["mousedown", "mousemove", "keydown", "touchstart", "wheel", "scroll"]) {
        window.addEventListener(name, markActive, { passive: true, capture: true });
    }

    return {
        secondsIdle: () => Math.floor((Date.now() - lastActivity) / 1000),
        markActive
    };
})();
