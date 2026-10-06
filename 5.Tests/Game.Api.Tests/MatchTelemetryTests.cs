using System.Reflection;
using Game.Api.Messaging;
using Game.Core.Events;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;

namespace Game.Api.Tests;

public class MatchTelemetryTests
{
    private static MatchCompletedEvent NewEvent() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public async Task Publishing_ReturnsAtOnceWithoutABroker()
    {
        var publisher = new MatchTelemetryPublisher(NullLogger<MatchTelemetryPublisher>.Instance);
        var matchEvent = NewEvent();

        var publish = publisher.PublishAsync(matchEvent);

        Assert.True(publish.IsCompletedSuccessfully);
        Assert.True(publisher.Pending.TryRead(out var queued));
        Assert.Equal(matchEvent, queued);
    }

    [Fact]
    public async Task AFullBacklog_DropsTheOldestEvents()
    {
        var publisher = new MatchTelemetryPublisher(NullLogger<MatchTelemetryPublisher>.Instance);
        var events = Enumerable.Range(0, MatchTelemetryPublisher.Capacity + 5).Select(_ => NewEvent()).ToList();

        foreach (var matchEvent in events) await publisher.PublishAsync(matchEvent);

        Assert.Equal(MatchTelemetryPublisher.Capacity, publisher.Pending.Count);
        Assert.True(publisher.Pending.TryRead(out var oldest));
        Assert.Equal(events[5], oldest);
    }

    [Fact]
    public async Task TheSender_KeepsRetryingWhileTheBrokerIsDown()
    {
        var publisher = new MatchTelemetryPublisher(NullLogger<MatchTelemetryPublisher>.Instance);
        var factory = UnreachableBroker.Create();
        using var sender = new MatchTelemetrySender(publisher, factory, new TelemetryBrokerStatus(), NullLogger<MatchTelemetrySender>.Instance);

        await sender.StartAsync(CancellationToken.None);
        await publisher.PublishAsync(NewEvent());
        await WaitUntilAsync(() => UnreachableBroker.Attempts(factory) >= 1);

        // The sender connects before taking an event, so the event waits in the backlog; the sender keeps running.
        Assert.Equal(1, publisher.Pending.Count);
        Assert.False(sender.ExecuteTask!.IsCompleted);

        await sender.StopAsync(CancellationToken.None);
        Assert.True(sender.ExecuteTask.IsCompleted);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 8)]
    [InlineData(5, 30)]
    [InlineData(50, 30)]
    public void Reconnecting_BacksOffUpToThirtySeconds(int attempt, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), RabbitMqRetry.DelayFor(attempt));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }

    // An IConnectionFactory whose connection attempts always fail, as when RabbitMQ isn't running.
    public class UnreachableBroker : DispatchProxy
    {
        private int _attempts;

        public static IConnectionFactory Create() => DispatchProxy.Create<IConnectionFactory, UnreachableBroker>();

        public static int Attempts(IConnectionFactory factory) => ((UnreachableBroker)(object)factory)._attempts;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IConnectionFactory.CreateConnectionAsync))
            {
                Interlocked.Increment(ref _attempts);
                return Task.FromException<IConnection>(new InvalidOperationException("Broker unreachable"));
            }
            throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
