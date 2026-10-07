using System.Net.Http.Json;

namespace Game.Client.Services;

/// <summary>
/// Which switchable parts of the game are on, from GET /api/v1/features. The API refuses a switched-off
/// feature anyway; this only hides its buttons. Answers are reused for 30 seconds.
/// </summary>
public class FeatureFlags(HttpClient http)
{
    public const string Duels = "Duels";
    public const string HeroicBoss = "HeroicBoss";
    public const string GoldShop = "GoldShop";

    /// <summary>Recovery email and password reset; on when the deployment has set up email.</summary>
    public const string AccountRecovery = "AccountRecovery";

    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

    private Snapshot? _current;
    private DateTime _fetchedAt;

    /// <summary>The last answer while it's fresh, so a screen can render without waiting.</summary>
    public Snapshot? Fresh => _current is not null && DateTime.UtcNow - _fetchedAt < MaxAge ? _current : null;

    public async Task<Snapshot> GetAsync()
    {
        if (Fresh is { } fresh) return fresh;

        try
        {
            _current = await http.GetFromJsonAsync<Snapshot>("/api/v1/features") ?? Snapshot.Fallback;
            _fetchedAt = DateTime.UtcNow;
        }
        catch (Exception)
        {
            // Can't tell right now: show everything and let the API say no if it must.
            _current ??= Snapshot.Fallback;
        }
        return _current;
    }

    public record Snapshot(bool Duels, bool HeroicBoss, bool GoldShop, bool AccountRecovery = false)
    {
        // Recovery stays hidden until the API says email is set up, so nobody is offered a link that never comes.
        public static readonly Snapshot Fallback = new(true, true, true);

        public bool IsOn(string feature) => feature switch
        {
            FeatureFlags.Duels => Duels,
            FeatureFlags.HeroicBoss => HeroicBoss,
            FeatureFlags.GoldShop => GoldShop,
            FeatureFlags.AccountRecovery => AccountRecovery,
            _ => true
        };
    }
}
