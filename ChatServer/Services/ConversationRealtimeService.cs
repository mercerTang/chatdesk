using ChatServer.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace ChatServer.Services;

/// <summary>
/// 转派与账号停用的实时通知。数据库是权威状态，这里的组迁移与事件推送都是尽力而为：
/// 失败只记日志，不把已经提交的业务结果报成失败；重连时 <see cref="ChatHub.OnConnectedAsync"/>
/// 会按当前归属重建组成员。
/// </summary>
public class ConversationRealtimeService
{
    private readonly IHubContext<ChatHub> _hub;
    private readonly AgentPresenceService _presence;
    private readonly ILogger<ConversationRealtimeService> _logger;

    public ConversationRealtimeService(
        IHubContext<ChatHub> hub,
        AgentPresenceService presence,
        ILogger<ConversationRealtimeService> logger)
    {
        _hub = hub;
        _presence = presence;
        _logger = logger;
    }

    /// <summary>转派已提交后：收回原客服的组身份、通知双方，并把目标客服的在线连接并入会话组。</summary>
    public async Task NotifyTransferredAsync(TransferTicketResult result)
    {
        try
        {
            var conversationId = result.Conversation.Id;

            // 先收回原客服的会话组身份，再把转派事件发给会话组与原客服组（访客与原客服都能感知）
            foreach (var connectionId in _presence.ConnectionsOf(result.FromAgentId))
            {
                await _hub.Groups.RemoveFromGroupAsync(
                    connectionId, ChatGroups.Conversation(conversationId), CancellationToken.None);
            }

            var payload = new
            {
                conversationId,
                ticketId = result.Ticket.Id,
                agentId = result.Conversation.AgentId,
                agentName = result.Conversation.AgentName,
                transferredAt = result.TransferredAt
            };
            await _hub.Clients.Group(ChatGroups.Conversation(conversationId))
                .SendAsync("ConversationTransferred", payload, CancellationToken.None);
            await _hub.Clients.Group(ChatGroups.Agent(result.FromAgentId))
                .SendAsync("ConversationTransferred", payload, CancellationToken.None);

            // 目标客服的在线连接并入会话组，并收到可直接上屏的会话视图
            foreach (var connectionId in _presence.ConnectionsOf(result.Conversation.AgentId))
            {
                await _hub.Groups.AddToGroupAsync(
                    connectionId, ChatGroups.Conversation(conversationId), CancellationToken.None);
            }

            await _hub.Clients.Group(ChatGroups.Agent(result.Conversation.AgentId))
                .SendAsync("ConversationAssigned", result.Conversation, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "工单 {TicketNo} 已提交，但转派的实时通知失败", result.Ticket.TicketNo);
        }
    }

    /// <summary>
    /// 账号被停用：向该客服的在线连接推 SessionRevoked，由前端登出。
    /// 不断开连接，避免打断进行中的会话（硬断留作后续）。
    /// </summary>
    public async Task NotifySessionRevokedAsync(int agentId)
    {
        try
        {
            await _hub.Clients.Group(ChatGroups.Agent(agentId))
                .SendAsync("SessionRevoked", new { agentId }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "客服 {AgentId} 已停用，但 SessionRevoked 推送失败", agentId);
        }
    }
}
