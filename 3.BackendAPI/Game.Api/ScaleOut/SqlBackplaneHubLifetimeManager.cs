using Microsoft.AspNetCore.SignalR;

namespace Game.Api.ScaleOut;

/// <summary>
/// SignalR's own in-process hub lifetime manager, plus one step: a message for a user (or everyone) is
/// also handed to <see cref="SqlBackplane"/>, which passes it to the other replicas. Messages that arrive
/// from other replicas are delivered here through the base class, to this replica's connections only.
/// </summary>
/// <remarks>
/// The game only sends to users (Clients.User and Clients.Users) and never uses groups, so those are the
/// calls passed on. Groups and single connections stay local, as connection ids only mean something on
/// the replica that holds them.
/// </remarks>
public class SqlBackplaneHubLifetimeManager<THub> : DefaultHubLifetimeManager<THub> where THub : Hub
{
    private static readonly string HubName = typeof(THub).Name;

    private readonly SqlBackplane _backplane;

    public SqlBackplaneHubLifetimeManager(SqlBackplane backplane, ILogger<DefaultHubLifetimeManager<THub>> logger) : base(logger)
    {
        _backplane = backplane;
        backplane.Subscribe(HubName, DeliverAsync);
    }

    public override async Task OnConnectedAsync(HubConnectionContext connection)
    {
        await base.OnConnectedAsync(connection);
        _backplane.ConnectionOpened();
    }

    public override async Task OnDisconnectedAsync(HubConnectionContext connection)
    {
        await base.OnDisconnectedAsync(connection);
        _backplane.ConnectionClosed();
    }

    public override async Task SendUserAsync(string userId, string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        await base.SendUserAsync(userId, methodName, args, cancellationToken);
        await _backplane.PublishAsync(HubName, methodName, [userId], args, cancellationToken);
    }

    public override async Task SendUsersAsync(IReadOnlyList<string> userIds, string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        await base.SendUsersAsync(userIds, methodName, args, cancellationToken);
        await _backplane.PublishAsync(HubName, methodName, userIds, args, cancellationToken);
    }

    public override async Task SendAllAsync(string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        await base.SendAllAsync(methodName, args, cancellationToken);
        await _backplane.PublishAsync(HubName, methodName, null, args, cancellationToken);
    }

    // A message from another replica: deliver it here only, never pass it on again.
    private Task DeliverAsync(string methodName, IReadOnlyList<string>? userIds, object?[] args, CancellationToken cancellationToken) =>
        userIds is null
            ? base.SendAllAsync(methodName, args, cancellationToken)
            : base.SendUsersAsync(userIds, methodName, args, cancellationToken);
}
