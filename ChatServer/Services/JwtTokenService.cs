using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ChatServer.Models;
using Microsoft.IdentityModel.Tokens;

namespace ChatServer.Services;

/// <summary>签发 JWT（HMAC-SHA256，默认 8 小时有效）。</summary>
public class JwtTokenService
{
    private readonly string _key;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly int _expiryHours;

    public JwtTokenService(IConfiguration configuration)
    {
        _key = configuration["Jwt:Key"] ?? throw new InvalidOperationException("缺少 Jwt:Key 配置");
        _issuer = configuration["Jwt:Issuer"] ?? "ChatServer";
        _audience = configuration["Jwt:Audience"] ?? "ChatClient";
        _expiryHours = int.TryParse(configuration["Jwt:ExpiryHours"], out var hours) ? hours : 8;

        if (Encoding.UTF8.GetByteCount(_key) < 32)
        {
            throw new InvalidOperationException("Jwt:Key 至少需要 32 字节");
        }
    }

    public (string Token, DateTimeOffset ExpiresAt) CreateToken(Account account)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddHours(_expiryHours);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, account.Id.ToString()),
            new("name", account.Username),
            new("role", account.Role),
            new("displayName", account.DisplayName)
        };

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_key)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}
