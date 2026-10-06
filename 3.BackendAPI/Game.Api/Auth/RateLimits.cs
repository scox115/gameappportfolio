namespace Game.Api.Auth;

/// <summary>Rate-limit policy names; the limits come from <see cref="Options.AntiCheatOptions"/>.</summary>
public static class RateLimits
{
    public const string Registration = "registration";
    public const string SignIn = "sign-in";
}
