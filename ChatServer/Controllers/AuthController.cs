using ChatServer.Data;
using ChatServer.Dtos;
using ChatServer.Models;
using ChatServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChatServer.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private const int MinUsernameLength = 3;
    private const int MaxUsernameLength = 32;
    private const int MinPasswordLength = 6;
    private const int MaxPasswordLength = 64;
    private const int MaxDisplayNameLength = 64;

    private readonly AppDbContext _db;
    private readonly JwtTokenService _tokens;
    private readonly ILogger<AuthController> _logger;

    public AuthController(AppDbContext db, JwtTokenService tokens, ILogger<AuthController> logger)
    {
        _db = db;
        _tokens = tokens;
        _logger = logger;
    }

    /// <summary>访客注册，成功后直接返回登录令牌。客服不开放自助注册。</summary>
    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResponse>> Register([FromBody] RegisterRequest? request, CancellationToken ct)
    {
        if (request is null)
        {
            return BadRequest(new { error = "请求体不能为空" });
        }

        var username = (request.Username ?? string.Empty).Trim();
        var password = request.Password ?? string.Empty;

        if (username.Length is < MinUsernameLength or > MaxUsernameLength)
        {
            return BadRequest(new { error = $"用户名长度需在 {MinUsernameLength}-{MaxUsernameLength} 之间" });
        }

        if (username.Any(char.IsWhiteSpace) || username.Any(char.IsControl))
        {
            return BadRequest(new { error = "用户名不能包含空白或控制字符" });
        }

        if (password.Length is < MinPasswordLength or > MaxPasswordLength)
        {
            return BadRequest(new { error = $"密码长度需在 {MinPasswordLength}-{MaxPasswordLength} 之间" });
        }

        if (await _db.Accounts.AnyAsync(a => a.Username == username, ct))
        {
            return Conflict(new { error = "用户名已存在" });
        }

        var displayName = (request.DisplayName ?? string.Empty).Trim();
        if (displayName.Length == 0)
        {
            displayName = username;
        }

        if (displayName.Length > MaxDisplayNameLength)
        {
            return BadRequest(new { error = $"展示名不能超过 {MaxDisplayNameLength} 个字符" });
        }

        if (displayName.Any(char.IsControl))
        {
            return BadRequest(new { error = "展示名不能包含控制字符" });
        }

        var account = new Account
        {
            Username = username,
            DisplayName = displayName,
            PasswordHash = PasswordHasher.Hash(password),
            Role = Roles.Visitor
        };

        _db.Accounts.Add(account);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "注册用户名冲突：{Username}", username);
            return Conflict(new { error = "用户名已存在" });
        }

        _logger.LogInformation("访客注册成功：{Username}（Id={Id}）", account.Username, account.Id);
        return Ok(BuildAuthResponse(account));
    }

    /// <summary>访客与客服共用登录入口，角色由账号本身决定。</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResponse>> Login([FromBody] LoginRequest? request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password))
        {
            return BadRequest(new { error = "用户名和密码不能为空" });
        }

        var username = request.Username.Trim();
        var account = await _db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Username == username, ct);
        if (account is null || !PasswordHasher.Verify(request.Password, account.PasswordHash))
        {
            _logger.LogWarning("登录失败：{Username}", username);
            return Unauthorized(new { error = "用户名或密码错误" });
        }

        _logger.LogInformation("登录成功：{Username}（{Role}）", account.Username, account.Role);
        return Ok(BuildAuthResponse(account));
    }

    /// <summary>返回当前令牌对应的账号信息，前端据此做角色守卫。</summary>
    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<UserDto>> Me(CancellationToken ct)
    {
        var accountId = User.GetAccountId();
        var account = await _db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, ct);
        if (account is null)
        {
            return Unauthorized(new { error = "登录状态无效" });
        }

        return Ok(new UserDto(account.Id, account.Username, account.DisplayName, account.Role));
    }

    private AuthResponse BuildAuthResponse(Account account)
    {
        var (token, expiresAt) = _tokens.CreateToken(account);
        return new AuthResponse(
            token,
            expiresAt,
            new UserDto(account.Id, account.Username, account.DisplayName, account.Role));
    }
}
