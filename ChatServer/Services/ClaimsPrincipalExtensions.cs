using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;

namespace ChatServer.Services;

public static class ClaimsPrincipalExtensions
{
    /// <summary>读取账号 Id（sub 声明），读不到返回 0。</summary>
    public static int GetAccountId(this ClaimsPrincipal? user)
    {
        if (user is null)
        {
            return 0;
        }

        var raw = user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                  ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(raw, out var id) ? id : 0;
    }

    /// <summary>读取角色声明。</summary>
    public static string GetRole(this ClaimsPrincipal? user)
        => user?.FindFirst("role")?.Value
           ?? user?.FindFirst(ClaimTypes.Role)?.Value
           ?? string.Empty;
}
