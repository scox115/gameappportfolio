namespace Game.Api.Models;

/// <param name="Class">The hero's class; Sorcerer when left out.</param>
public record RegisterRequest(string Username, string Password, Game.Core.Battles.HeroClass? Class = null);

public record LoginRequest(string Username, string Password);

public record RefreshRequest(string RefreshToken);
