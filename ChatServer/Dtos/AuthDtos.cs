namespace ChatServer.Dtos;

public record RegisterRequest(string? Username, string? Password, string? DisplayName);

public record LoginRequest(string? Username, string? Password);

public record UserDto(int Id, string Username, string DisplayName, string Role);

public record AuthResponse(string Token, DateTimeOffset ExpiresAt, UserDto User);
