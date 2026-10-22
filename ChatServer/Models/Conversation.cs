namespace ChatServer.Models;

/// <summary>一次访客与客服之间的会话。</summary>
public class Conversation
{
    public int Id { get; set; }

    public int VisitorId { get; set; }
    public Account? Visitor { get; set; }

    public int AgentId { get; set; }
    public Account? Agent { get; set; }

    /// <summary>会话状态，取值见 <see cref="ConversationStatus"/>。</summary>
    public string Status { get; set; } = ConversationStatus.Active;

    /// <summary>服务端权威序号水位：该会话已分配的最大 Seq。</summary>
    public long LastMessageSeq { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset LastActivityAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? ClosedAt { get; set; }

    public ICollection<Message> Messages { get; set; } = new List<Message>();
}
