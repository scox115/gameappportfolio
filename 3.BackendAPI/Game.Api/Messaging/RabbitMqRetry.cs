namespace Game.Api.Messaging;

/// <summary>How long to wait before reconnecting to RabbitMQ: 1, 2, 4, 8, 16, then 30 seconds.</summary>
public static class RabbitMqRetry
{
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    public static TimeSpan DelayFor(int attempt) =>
        attempt >= 5 ? MaxDelay : TimeSpan.FromSeconds(Math.Pow(2, attempt));
}
