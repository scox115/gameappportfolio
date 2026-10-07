// Browser telemetry: sends page views (one per game screen), page load timing, API calls and
// front-end errors to Application Insights, next to the API's own traces. It uses Microsoft's
// JavaScript SDK, served from lib/applicationinsights so the Content Security Policy needs no CDN.
//
// The deploy writes the connection string and the API's origin into the two meta tags in
// index.html (see infra/set-client-csp.py). Without a connection string, as in local runs, the SDK
// isn't started and every call below does nothing. The Blazor side is Services/BrowserTelemetry.cs.
window.gameTelemetry = (() => {
    const meta = name => document.querySelector(`meta[name="${name}"]`)?.content.trim() ?? "";
    const connectionString = meta("telemetry-connection-string");
    const apiOrigin = meta("telemetry-api-origin");
    const sdk = window.Microsoft?.ApplicationInsights;

    const off = { enabled: false, start() {}, trackView() {}, setPlayer() {}, trackError() {}, flush() {}, sessionId: () => "" };
    // An ad blocker may stop the SDK from loading; the game works the same without it.
    if (!connectionString || !sdk) return off;

    let version = "";
    const appInsights = new sdk.ApplicationInsights({
        config: {
            connectionString,
            // No tracking cookies, so no consent banner: a session lasts one page load.
            disableCookiesUsage: true,
            // The game switches screens without changing the URL, so Blazor reports each screen itself.
            enableAutoRouteTracking: false,
            // Adds trace headers to API calls, so a slow screen leads to the API request behind it.
            enableCorsCorrelation: true,
            correlationHeaderDomains: apiOrigin ? [new URL(apiOrigin).host] : [],
            // Blazor downloads its runtime with fetch; those aren't API calls worth recording.
            excludeRequestFromAutoTrackingPatterns: [/\/_framework\//, /\/appsettings[^/]*\.json/],
            enableUnhandledPromiseRejectionTracking: true,
            // Keep the SDK from calling Microsoft's CDN for its settings or sending its own usage
            // stats; the policy only lets it reach this resource's ingestion endpoint.
            featureOptIn: { sdkStats: { mode: 2 } },
            extensionConfig: { AppInsightsCfgSyncPlugin: { cfgUrl: "", blkCdnCfg: true } },
        },
    });

    try {
        appInsights.loadAppInsights();
    } catch {
        return off; // a malformed connection string must not break the game
    }

    // Recovery links carry a one-time token in the query string; addresses are sent without it.
    const withoutQuery = url => {
        try {
            const parsed = new URL(url, location.href);
            return parsed.origin + parsed.pathname;
        } catch {
            return url;
        }
    };

    appInsights.addTelemetryInitializer(item => {
        for (const field of ["uri", "refUri", "url"]) {
            if (typeof item.baseData?.[field] === "string") item.baseData[field] = withoutQuery(item.baseData[field]);
        }
        item.tags = item.tags ?? {};
        // Shows the browser as its own node, calling the API, on the Application Map.
        item.tags["ai.cloud.role"] = "card-arena-client";
        if (version) item.tags["ai.application.ver"] = version;
    });

    return {
        enabled: true,
        /** Called once Blazor is running, with the build's version. */
        start(appVersion) {
            version = appVersion;
        },
        /** A page view; the first one also carries how long the page took to load. */
        trackView(name) {
            appInsights.trackPageView({ name, uri: withoutQuery(location.href) });
        },
        /** Ties telemetry to a player (their id, never their name), or clears it on sign-out. */
        setPlayer(playerId) {
            if (playerId) appInsights.setAuthenticatedUserContext(playerId, undefined, false);
            else appInsights.clearAuthenticatedUserContext();
        },
        /** An error Blazor logged: an exception when there is one, otherwise an error trace. */
        trackError(category, message, type, stack) {
            const properties = { category };
            if (type) {
                const error = new Error(message);
                error.name = type;
                if (stack) error.stack = `${type}: ${message}\n${stack}`;
                appInsights.trackException({ exception: error, severityLevel: 3, properties });
            } else {
                appInsights.trackTrace({ message, severityLevel: 3, properties });
            }
        },
        /** The id shared by everything this page load sends; the browser tests look for their own by it. */
        sessionId() {
            return appInsights.context.getSessionId();
        },
        /** Sends anything still queued; the browser tests use it instead of waiting for a batch. */
        flush() {
            appInsights.flush();
        },
    };
})();
