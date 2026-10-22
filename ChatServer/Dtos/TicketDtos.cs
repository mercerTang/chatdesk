namespace ChatServer.Dtos;

/// <summary>工单列表项。</summary>
public record TicketDto(
    int Id,
    string TicketNo,
    int ConversationId,
    string Status,
    string VisitorName,
    string AgentName,
    int MessageCount,
    string? LastMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ClosedAt,
    DateTimeOffset LastActivityAt);

/// <summary>工单详情：包含按序号正序的聊天记录。</summary>
public record TicketDetailDto(TicketDto Ticket, IReadOnlyList<MessageDto> Messages);
