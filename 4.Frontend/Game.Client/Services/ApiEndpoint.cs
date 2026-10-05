namespace Game.Client.Services;

/// <summary>Where the API lives, for clients other than HttpClient (such as the SignalR hub).</summary>
public record ApiEndpoint(Uri BaseAddress);
