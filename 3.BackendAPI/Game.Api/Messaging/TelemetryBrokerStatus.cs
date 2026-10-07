namespace Game.Api.Messaging;

/// <summary>Whether the outbox relay is connected to RabbitMQ, for the readiness check.</summary>
public class TelemetryBrokerStatus
{
    private volatile bool _connected;
    private volatile string? _lastError;

    public bool IsConnected => _connected;
    public string? LastError => _lastError;

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
