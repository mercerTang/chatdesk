namespace ChatServer.Dtos;

/// <summary>客服列表项：在线状态与当前负载。</summary>
public record AgentDto(
    int Id,
    string Username,
    string DisplayName,
    bool IsOnline,
    int ActiveConversationCount,
    int MaxConcurrentConversations);

/// <summary>会话摘要，访客端与客服端共用。</summary>
public record ConversationDto(
    int Id,
    string Status,
    int VisitorId,
    string VisitorName,
    int AgentId,
    string AgentName,
    long LastMessageSeq,
    string? LastMessage,
    DateTimeOffset? LastMessageAt,
    int UnreadCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt);

public record CreateConversationRequest(int? AgentId);

/// <summary>消息，含回执时间点。</summary>
public record MessageDto(
    long Id,
    int ConversationId,
    long Seq,
    int SenderId,
    string SenderName,
    string SenderRole,
    string Content,
    string? ClientMessageId,
    DateTimeOffset SentAt,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? ReadAt);

/// <summary>SendMessage 的发送回执：含服务端分配的权威 Seq。</summary>
public record SendMessageAck(
    long MessageId,
    int ConversationId,
    long Seq,
    string? ClientMessageId,
    DateTimeOffset SentAt,
    bool Duplicated);

/// <summary>重新加入会话时返回的会话水位。</summary>
public record ConversationJoinResult(int ConversationId, string Status, long LastMessageSeq);
