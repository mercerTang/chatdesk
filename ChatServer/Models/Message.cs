namespace ChatServer.Models;

/// <summary>会话内消息。序号 Seq 与 ClientMessageId 的唯一性由数据库索引兜底。</summary>
public class Message
{
    public long Id { get; set; }

    public int ConversationId { get; set; }
    public Conversation? Conversation { get; set; }

    /// <summary>会话内单调递增的序号，由服务端权威分配。</summary>
    public long Seq { get; set; }

    public int SenderId { get; set; }
    public Account? Sender { get; set; }

    /// <summary>发送者角色，取值见 <see cref="Roles"/>。</summary>
    public string SenderRole { get; set; } = Roles.Visitor;

    public string Content { get; set; } = string.Empty;

    /// <summary>客户端生成的幂等键，可为空。</summary>
    public string? ClientMessageId { get; set; }

    public DateTimeOffset SentAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>对端客户端确认收到的时刻。</summary>
    public DateTimeOffset? DeliveredAt { get; set; }

    /// <summary>对端确认已读的时刻。</summary>
    public DateTimeOffset? ReadAt { get; set; }
}
