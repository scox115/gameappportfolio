namespace Game.Client.Services;

public enum GameScreen
{
    LoginMenu,
    CharacterDashboard,
    BattleArena,
    PvpArena,
    Leaderboard 
}

public record SessionTokens(string AccessToken, DateTimeOffset ExpiresAt, string RefreshToken);

public class GameState
{
    public GameScreen CurrentScreen { get; private set; } = GameScreen.LoginMenu;
    public Guid PlayerId { get; private set; }
    // Tokens are kept in memory only, so a page refresh signs the player out.
    public string? AccessToken { get; private set; }
    public DateTimeOffset AccessTokenExpiresAt { get; private set; }
    public string? RefreshToken { get; private set; }
    public string? SignOutReason { get; private set; }
    public string Username { get; private set; } = string.Empty;
    public int Gold { get; private set; }
    public int Level { get; private set; }
    public string AvatarUrl { get; private set; } = string.Empty;

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

    public void UpdateRewards(int newGold, int newLevel)
    {
        Gold = newGold;
        Level = newLevel;
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
        PlayerId = Guid.Empty;
        Username = string.Empty;
        Gold = 0;
        Level = 0;
        AvatarUrl = string.Empty;
        CurrentScreen = GameScreen.LoginMenu;
        NotifyStateChanged();
    }

    private void NotifyStateChanged() => OnStateChanged?.Invoke();
}
