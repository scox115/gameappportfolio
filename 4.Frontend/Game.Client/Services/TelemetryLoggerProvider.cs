using Microsoft.JSInterop;

namespace Game.Client.Services;

/// <summary>
/// Sends errors the client logs to Application Insights through wwwroot/js/telemetry.js. Blazor logs
/// every unhandled exception, such as one thrown while rendering, at Critical, so this is how a crash
/// in a player's browser reaches the same place as the API's errors.
/// </summary>
public sealed class TelemetryLoggerProvider(IJSRuntime js) : ILoggerProvider
{
    // Sending happens on the browser's only thread; this stops a failed send from logging itself again.
    private bool _sending;

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose() { }

    private void Send(string category, string message, Exception? exception)
    {
        if (_sending || js is not IJSInProcessRuntime browser) return;
        _sending = true;
        try
        {
            browser.InvokeVoid("gameTelemetry.trackError", category, message,
                exception?.GetType().FullName, exception?.StackTrace);
        }
        catch (JSException)
        {
            // The telemetry script isn't there; nothing more to do.
        }
        finally
        {
            _sending = false;
        }
    }

    private sealed class Logger(TelemetryLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            provider.Send(category, exception?.Message ?? formatter(state, exception), exception);
        }
    }
}
