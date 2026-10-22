namespace ChatServer.Models;

/// <summary>账号：访客参加注册，客服仅由种子数据创建。</summary>
public class Account
{
    public int Id { get; set; }

    /// <summary>登录名，全局唯一。</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>PBKDF2 口令哈希，格式：迭代次数.盐(Base64).密钥(Base64)。</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>对外展示名。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>角色，取值见 <see cref="Roles"/>。</summary>
    public string Role { get; set; } = Roles.Visitor;

    /// <summary>在线标记：客服由 SignalR 连接状态维护，访客保留字段。</summary>
    public bool IsOnline { get; set; }

    /// <summary>可同时接待的活跃会话上限，仅客服有效。</summary>
    public int MaxConcurrentConversations { get; set; } = 5;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
