namespace Game.Client.Services;

public enum GameScreen
{
    LoginMenu,
    CharacterDashboard,
    BattleArena
}

public class GameState
{
    public GameScreen CurrentScreen { get; private set; } = GameScreen.LoginMenu;
    public Guid PlayerId { get; private set; }
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

    public void SetPlayerSession(Guid id, string username, int gold, int level, string? avatar)
    {
        PlayerId = id;
        Username = username;
        Gold = gold;
        Level = level;
        AvatarUrl = avatar ?? string.Empty;
        CurrentScreen = GameScreen.CharacterDashboard;
        NotifyStateChanged();
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

    private void NotifyStateChanged() => OnStateChanged?.Invoke();
}
