using System.Linq.Expressions;
using ChatServer.Models;

namespace ChatServer.Dtos;

/// <summary>工单列表项。</summary>
public record TicketDto(
    int Id,
    string TicketNo,
    int ConversationId,
    string Status,
    string VisitorName,
    string AgentName,
    int AgentId,
    int MessageCount,
    string? LastMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ClosedAt,
    DateTimeOffset LastActivityAt);

/// <summary>工单转派记录：原客服、目标客服、备注、强制转派的管理员与时间。</summary>
public record TicketTransferDto(
    int Id,
    int FromAgentId,
    string FromAgentName,
    int ToAgentId,
    string ToAgentName,
    string? Note,
    int? OperatorId,
    string? OperatorName,
    DateTimeOffset CreatedAt);

/// <summary>工单详情：包含按序号正序的聊天记录与按时间正序的转派记录。</summary>
public record TicketDetailDto(
    TicketDto Ticket,
    IReadOnlyList<MessageDto> Messages,
    IReadOnlyList<TicketTransferDto> Transfers);

/// <summary>转派请求：目标客服与可选备注。</summary>
public record TransferTicketRequest(int AgentId, string? Note);

/// <summary>工单列表项投影：列表、详情与转派响应共用同一份字段来源。</summary>
public static class TicketProjections
{
    public static Expression<Func<Ticket, TicketDto>> Build(DateTimeOffset cutoff)
        => ticket => new TicketDto(
            ticket.Id,
            ticket.TicketNo,
            ticket.ConversationId,
            ticket.Status,
            ticket.Conversation!.Visitor != null ? ticket.Conversation.Visitor.DisplayName : string.Empty,
            ticket.Conversation!.Agent != null ? ticket.Conversation.Agent.DisplayName : string.Empty,
            ticket.Conversation.AgentId,
            ticket.Conversation.Messages.Count(message => message.SentAt >= cutoff),
            ticket.Conversation.Messages
                .Where(message => message.SentAt >= cutoff)
                .OrderByDescending(message => message.Seq)
                .Select(message => message.Content)
                .FirstOrDefault(),
            ticket.CreatedAt,
            ticket.ClosedAt,
            ticket.Conversation.LastActivityAt);
}

/// <summary>转派台账投影：客服端与中台共用。</summary>
public static class TicketTransferProjections
{
    public static readonly Expression<Func<TicketTransfer, TicketTransferDto>> Projection = transfer =>
        new TicketTransferDto(
            transfer.Id,
            transfer.FromAgentId,
            transfer.FromAgent != null ? transfer.FromAgent.DisplayName : string.Empty,
            transfer.ToAgentId,
            transfer.ToAgent != null ? transfer.ToAgent.DisplayName : string.Empty,
            transfer.Note,
            transfer.OperatorId,
            transfer.Operator != null ? transfer.Operator.DisplayName : null,
            transfer.CreatedAt);
}
