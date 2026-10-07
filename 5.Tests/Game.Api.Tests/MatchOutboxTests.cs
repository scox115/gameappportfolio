using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Game.Api.Messaging;
using Game.Core.Events;
using Game.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;

namespace Game.Api.Tests;

public class MatchOutboxTests
{
    private static MatchCompletedEvent NewEvent(DateTime? at = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()) { OccurredAt = at ?? DateTime.UtcNow, Kind = MatchKind.Duel, Turns = 4 };

    [Fact]
    public async Task AnEvent_IsSavedOnlyWithTheCallersSaveChanges()
    {
        using var factory = new GameApiFactory();
        var matchEvent = NewEvent();

        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            factory.Services.GetRequiredService<MatchOutbox>().Add(dbContext, matchEvent);
            // Nothing is written until the caller saves; a request that fails before then leaves no event.
        }
        Assert.Empty(await OutboxAsync(factory));

        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            factory.Services.GetRequiredService<MatchOutbox>().Add(dbContext, matchEvent);
            await dbContext.SaveChangesAsync();
        }

        var saved = Assert.Single(await OutboxAsync(factory));
        Assert.Equal(matchEvent.MatchId, saved.Id);
        Assert.Equal(MatchOutbox.MatchCompletedQueue, saved.Queue);
        var roundTrip = JsonSerializer.Deserialize<MatchCompletedEvent>(saved.Payload, MatchEventJson.Options)!;
        Assert.Equal((matchEvent.MatchId, MatchKind.Duel, 4), (roundTrip.MatchId, roundTrip.Kind, roundTrip.Turns));
    }

    [Fact]
    public async Task Waiting_EndsAtOnceWhenNotified_OrAfterTheTimeout()
    {
        var outbox = new MatchOutbox(TimeProvider.System);

        outbox.Notify();
        outbox.Notify(); // a second ring while one is waiting is dropped
        var notified = outbox.WaitAsync(TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.True(notified.IsCompletedSuccessfully);

        var started = DateTime.UtcNow;
        await outbox.WaitAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None);
        Assert.InRange(DateTime.UtcNow - started, TimeSpan.FromMilliseconds(80), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TheRelay_SendsOldestFirst_AndDeletesWhatTheBrokerTook()
    {
        using var factory = new GameApiFactory();
        var first = NewEvent(DateTime.UtcNow.AddMinutes(-2));
        var second = NewEvent(DateTime.UtcNow.AddMinutes(-1));
        await SaveAsync(factory, first);
        factory.Clock.Advance(TimeSpan.FromSeconds(1));
        await SaveAsync(factory, second);
        var broker = new FakeBroker();

        await RunRelayUntilAsync(factory, broker, async () => (await OutboxAsync(factory)).Count == 0);

        Assert.Equal([first.MatchId, second.MatchId], broker.Published.Select(m => JsonSerializer.Deserialize<MatchCompletedEvent>(m, MatchEventJson.Options)!.MatchId));
        Assert.True(broker.ConfirmsEnabled);
    }

    [Fact]
    public async Task WhenTheBrokerRefusesAMessage_ItStaysInTheOutbox()
    {
        using var factory = new GameApiFactory();
        var first = NewEvent();
        var second = NewEvent();
        await SaveAsync(factory, first);
        factory.Clock.Advance(TimeSpan.FromSeconds(1));
        await SaveAsync(factory, second);
        var broker = new FakeBroker { FailAfter = 1 };

        await RunRelayUntilAsync(factory, broker, async () => (await OutboxAsync(factory)).Any(m => m.Attempts > 0));

        var left = Assert.Single(await OutboxAsync(factory));
        Assert.Equal(second.MatchId, left.Id);
        Assert.Equal("Broker said no", left.LastError);
        Assert.Single(broker.Published);
    }

    [Fact]
    public async Task WhileTheBrokerIsDown_EventsWaitInTheOutbox_AndReadinessCountsThem()
    {
        using var factory = new GameApiFactory();
        await SaveAsync(factory, NewEvent());
        var broker = new FakeBroker { Reachable = false };

        await RunRelayUntilAsync(factory, broker, () => Task.FromResult(broker.ConnectionAttempts >= 2));

        Assert.Single(await OutboxAsync(factory));
        var ready = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/health/ready");
        var check = ready.GetProperty("checks").GetProperty("message-broker");
        Assert.Equal("Degraded", check.GetProperty("status").GetString());
        Assert.Contains("1 match event(s) waiting in the outbox", check.GetProperty("description").GetString());
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

    private static async Task SaveAsync(GameApiFactory factory, MatchCompletedEvent matchEvent)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        factory.Services.GetRequiredService<MatchOutbox>().Add(dbContext, matchEvent);
        await dbContext.SaveChangesAsync();
    }

    private static async Task<List<Infrastructure.Messaging.OutboxMessage>> OutboxAsync(GameApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().OutboxMessages.AsNoTracking().ToListAsync();
    }

    private static async Task RunRelayUntilAsync(GameApiFactory factory, FakeBroker broker, Func<Task<bool>> done)
    {
        using var relay = new MatchOutboxRelay(
            factory.Services.GetRequiredService<MatchOutbox>(),
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            broker.Factory,
            factory.Services.GetRequiredService<TelemetryBrokerStatus>(),
            NullLogger<MatchOutboxRelay>.Instance);

        await relay.StartAsync(CancellationToken.None);
        for (var i = 0; i < 200 && !await done(); i++) await Task.Delay(20);
        Assert.True(await done());
        Assert.False(relay.ExecuteTask!.IsCompleted);

        await relay.StopAsync(CancellationToken.None);
        Assert.True(relay.ExecuteTask.IsCompleted);
    }

    /// <summary>Stands in for RabbitMQ: records what is published, or refuses connections or messages.</summary>
    public class FakeBroker
    {
        public bool Reachable { get; init; } = true;

        /// <summary>Messages accepted before every further publish fails.</summary>
        public int FailAfter { get; init; } = int.MaxValue;

        public List<string> Published { get; } = [];
        public int ConnectionAttempts;
        public bool ConfirmsEnabled { get; private set; }

        public IConnectionFactory Factory => Proxy<IConnectionFactory>.Create((name, args) =>
        {
            if (name != nameof(IConnectionFactory.CreateConnectionAsync)) throw new NotSupportedException(name);
            Interlocked.Increment(ref ConnectionAttempts);
            return Reachable
                ? Task.FromResult(Connection())
                : Task.FromException<IConnection>(new InvalidOperationException("Broker unreachable"));
        });

        private IConnection Connection() => Proxy<IConnection>.Create((name, args) => name switch
        {
            nameof(IConnection.CreateChannelAsync) => CreateChannel((CreateChannelOptions?)args[0]),
            nameof(IAsyncDisposable.DisposeAsync) => ValueTask.CompletedTask,
            nameof(IDisposable.Dispose) => null,
            _ when name.StartsWith("add_") || name.StartsWith("remove_") => null,
            _ => throw new NotSupportedException(name)
        });

        private Task<IChannel> CreateChannel(CreateChannelOptions? options)
        {
            ConfirmsEnabled = options is { PublisherConfirmationsEnabled: true, PublisherConfirmationTrackingEnabled: true };
            return Task.FromResult(Proxy<IChannel>.Create((name, args) => name switch
            {
                "get_IsOpen" => true,
                nameof(IChannel.QueueDeclareAsync) => Task.FromResult(new QueueDeclareOk((string)args[0]!, 0, 0)),
                nameof(IChannel.BasicPublishAsync) => Publish((ReadOnlyMemory<byte>)args[4]!),
                nameof(IAsyncDisposable.DisposeAsync) => ValueTask.CompletedTask,
                nameof(IDisposable.Dispose) => null,
                _ => throw new NotSupportedException(name)
            }));
        }

        private ValueTask Publish(ReadOnlyMemory<byte> body)
        {
            if (Published.Count >= FailAfter) return ValueTask.FromException(new InvalidOperationException("Broker said no"));
            Published.Add(Encoding.UTF8.GetString(body.Span));
            return ValueTask.CompletedTask;
        }
    }

    public class Proxy<T> : DispatchProxy where T : class
    {
        private Func<string, object?[], object?> _handler = null!;

        public static T Create(Func<string, object?[], object?> handler)
        {
            var proxy = DispatchProxy.Create<T, Proxy<T>>();
            ((Proxy<T>)(object)proxy)._handler = handler;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            _handler(targetMethod!.Name, args ?? []);
    }
}
