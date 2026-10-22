namespace ChatServer.Models;

/// <summary>工单：与一次会话一一对应，保留可追溯的聊天记录。</summary>
public class Ticket
{
    public int Id { get; set; }

    /// <summary>工单号，形如 T-20261021-00010。</summary>
    public string TicketNo { get; set; } = string.Empty;

    public int ConversationId { get; set; }
    public Conversation? Conversation { get; set; }

    /// <summary>工单状态，取值见 <see cref="TicketStatus"/>。</summary>
    public string Status { get; set; } = TicketStatus.Open;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? ClosedAt { get; set; }
}
