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
            _current = await http.GetFromJsonAsync<Snapshot>("/api/v1/features") ?? Snapshot.AllOn;
            _fetchedAt = DateTime.UtcNow;
        }
        catch (Exception)
        {
            // Can't tell right now: show everything and let the API say no if it must.
            _current ??= Snapshot.AllOn;
        }
        return _current;
    }

    public record Snapshot(bool Duels, bool HeroicBoss, bool GoldShop)
    {
        public static readonly Snapshot AllOn = new(true, true, true);

        public bool IsOn(string feature) => feature switch
        {
            FeatureFlags.Duels => Duels,
            FeatureFlags.HeroicBoss => HeroicBoss,
            FeatureFlags.GoldShop => GoldShop,
            _ => true
        };
    }
}
