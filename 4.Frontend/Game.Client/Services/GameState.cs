namespace Game.Client.Services;

public enum GameScreen
{
    LoginMenu,
    CharacterDashboard,
    BattleArena,
    PvpArena,
    Shop,
    Leaderboard 
}

public record SessionTokens(string AccessToken, DateTimeOffset ExpiresAt, string RefreshToken);

public class GameState
{
    /// <summary>Shown when signing in on another browser ended this one's session.</summary>
    public const string SignedInElsewhereMessage = "You signed in on another browser, so you were signed out here.";

    /// <summary>Shown when an admin suspended the account while it was signed in.</summary>
    public const string SuspendedMessage = "Your hero was suspended by an admin. Sign in again to see why.";

    // Set by the API on a 401 when the token was retired by a newer sign-in or a suspension.
    public const string SessionEndedHeader = "X-Session-Ended";

    /// <summary>Why the API refused the session, for the sign-in screen.</summary>
    public static string EndedMessage(HttpResponseMessage response) =>
        response.Headers.TryGetValues(SessionEndedHeader, out var values)
            ? values.FirstOrDefault() == "suspended" ? SuspendedMessage : SignedInElsewhereMessage
            : "Your session expired. Please sign in again.";

    public GameScreen CurrentScreen { get; private set; } = GameScreen.LoginMenu;
    public Guid PlayerId { get; private set; }
    // Tokens are kept in memory only, so a page refresh signs the player out.
    public string? AccessToken { get; private set; }
    public DateTimeOffset AccessTokenExpiresAt { get; private set; }
    public string? RefreshToken { get; private set; }
    public string? SignOutReason { get; private set; }

    /// <summary>True when the account has the Admin role, which shows the admin tools.</summary>
    public bool IsAdmin { get; private set; }
    public string Username { get; private set; } = string.Empty;

    /// <summary>A guest hero: a made-up name and no password, until the player keeps it.</summary>
    public bool IsGuest { get; private set; }
    public int Gold { get; private set; }
    public int Level { get; private set; }
    public string AvatarUrl { get; private set; } = string.Empty;

    /// <summary>The avatar frame and card skin the player wears (Gold Shop cosmetics), or null.</summary>
    public string? Frame { get; private set; }
    public string? CardSkin { get; private set; }

    public event Action? OnStateChanged;

    public void ChangeScreen(GameScreen newScreen)
    {
        CurrentScreen = newScreen;
        NotifyStateChanged();
    }

    public void SetPlayerSession(SessionTokens tokens, Guid id, string username, int gold, int level, string? avatar)
    {
        UpdateTokens(tokens);
        SignOutReason = null;
        PlayerId = id;
        Username = username;
        Gold = gold;
        Level = level;
        AvatarUrl = avatar ?? string.Empty;
        CurrentScreen = GameScreen.CharacterDashboard;
        NotifyStateChanged();
    }

    /// <summary>Swaps in a renewed access token and refresh token.</summary>
    public void UpdateTokens(SessionTokens tokens)
    {
        AccessToken = tokens.AccessToken;
        AccessTokenExpiresAt = tokens.ExpiresAt;
        RefreshToken = tokens.RefreshToken;
    }

    /// <summary>The account's roles, from the sign-in response.</summary>
    public void UpdateRoles(IEnumerable<string>? roles)
    {
        IsAdmin = roles?.Contains("Admin") == true;
        NotifyStateChanged();
    }

    public void UpdateRewards(int newGold, int newLevel)
    {
        Gold = newGold;
        Level = newLevel;
        NotifyStateChanged();
    }

    public void UpdateGold(int newGold)
    {
        Gold = newGold;
        NotifyStateChanged();
    }

    public void UpdateCosmetics(string? frame, string? cardSkin)
    {
        Frame = frame;
        CardSkin = cardSkin;
        NotifyStateChanged();
    }

    /// <summary>The hero's class, such as "Paladin".</summary>
    public string HeroClass { get; private set; } = "Sorcerer";

    public void UpdateClass(string heroClass)
    {
        HeroClass = heroClass;
        NotifyStateChanged();
    }

    public void UpdateGuest(bool isGuest)
    {
        IsGuest = isGuest;
        NotifyStateChanged();
    }

    /// <summary>The guest hero now has a name and password of the player's own.</summary>
    public void KeptAs(string username)
    {
        Username = username;
        IsGuest = false;
        NotifyStateChanged();
    }

    public void UpdateAvatar(string url)
    {
        AvatarUrl = url;
        NotifyStateChanged();
    }

    public void SignOut(string? reason = null)
    {
        AccessToken = null;
        RefreshToken = null;
        SignOutReason = reason;
        IsAdmin = false;
        PlayerId = Guid.Empty;
        Username = string.Empty;
        IsGuest = false;
        Gold = 0;
        Level = 0;
        AvatarUrl = string.Empty;
        Frame = null;
        CardSkin = null;
        CurrentScreen = GameScreen.LoginMenu;
        NotifyStateChanged();
    }

    private void NotifyStateChanged() => OnStateChanged?.Invoke();
}

