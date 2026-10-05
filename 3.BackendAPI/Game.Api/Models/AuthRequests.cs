namespace Game.Api.Models;

public record RegisterRequest(string Username, string Password);

public record LoginRequest(string Username, string Password);

public record RefreshRequest(string RefreshToken);
