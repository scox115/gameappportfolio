namespace Game.Api.Messaging;

/// <summary>Whether the telemetry sender is connected to RabbitMQ, for the readiness check.</summary>
public class TelemetryBrokerStatus
{
    private volatile bool _connected;
    private volatile string? _lastError;
    private volatile int _heldEvents;

    public bool IsConnected => _connected;
    public string? LastError => _lastError;

    /// <summary>Events the sender has taken off the backlog but not delivered yet (0 or 1).</summary>
    public int HeldEvents => _heldEvents;

    public void Holding(int events) => _heldEvents = events;

    public void Connected()
    {
        _connected = true;
        _lastError = null;
    }

    public void Disconnected(string error)
    {
        _connected = false;
        _lastError = error;
    }
}
