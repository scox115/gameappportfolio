namespace Game.Api.Auth;

/// <summary>The roles an account can have, and the authorization policies built on them.</summary>
public static class GameRoles
{
    /// <summary>Runs the game: looks players up, suspends and reinstates them, and corrects gold.</summary>
    public const string Admin = "Admin";

    /// <summary>The policy every /admin endpoint requires: the Admin role, with two-factor sign-in on.</summary>
    public const string AdminPolicy = "Admin";
}
