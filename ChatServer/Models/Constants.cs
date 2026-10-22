namespace ChatServer.Models;

/// <summary>账号角色常量。</summary>
public static class Roles
{
    public const string Visitor = "visitor";
    public const string Agent = "agent";
}

/// <summary>会话状态常量。</summary>
public static class ConversationStatus
{
    public const string Active = "active";
    public const string Closed = "closed";
}

/// <summary>工单状态常量。</summary>
public static class TicketStatus
{
    public const string Open = "open";
    public const string Closed = "closed";
}
