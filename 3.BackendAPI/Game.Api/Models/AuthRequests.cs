namespace Game.Api.Models;

/// <param name="Class">The hero's class; Sorcerer when left out.</param>
public record RegisterRequest(string Username, string Password, Game.Core.Battles.HeroClass? Class = null, string? RecoveryEmail = null);

/// <param name="TwoFactorCode">For heroes with two-factor sign-in on: six digits from their app, or a recovery code.</param>
public record LoginRequest(string Username, string Password, string? TwoFactorCode = null);

public record RefreshRequest(string RefreshToken);

/// <param name="Class">The guest hero's class; Sorcerer when left out.</param>
public record GuestRequest(Game.Core.Battles.HeroClass? Class = null);

/// <summary>The name and password a guest keeps their hero under.</summary>
public record KeepGuestRequest(string Username, string Password, string? RecoveryEmail = null);
