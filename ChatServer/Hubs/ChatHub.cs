using ChatServer.Data;
using ChatServer.Dtos;
using ChatServer.Models;
using ChatServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ChatServer.Hubs;

/// <summary>
/// 实时通道。可靠性约定：
/// 1) SendMessage 先落库（ChatService），成功后回执含服务端 Seq，再向会话组广播；
/// 2) 命中幂等键的重复提交只回执、不再广播；
/// 3) 回执事件携带确认方 byAccountId，且只发给组内其它连接（OthersInGroup），
///    客户端据此过滤，绝不把确认方自己的消息标成已送达/已读；
/// 4) 扇出广播使用 CancellationToken.None：调用方断开不应中断发给其它客户端的推送；
/// 5) 客服连接/断开维护在线状态，并广播给所有客户端。
/// </summary>
[Authorize]
public class ChatHub : Hub
{
    private readonly AppDbContext _db;
    private readonly ChatService _chat;
    private readonly AgentPresenceService _presence;
    private readonly ILogger<ChatHub> _logger;

    public ChatHub(AppDbContext db, ChatService chat, AgentPresenceService presence, ILogger<ChatHub> logger)
    {
        _db = db;
        _chat = chat;
        _presence = presence;
        _logger = logger;
    }

    private int CurrentAccountId => Context.User.GetAccountId();

    public override async Task OnConnectedAsync()
    {
        var account = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == CurrentAccountId, Context.ConnectionAborted);
        if (account is { Role: Roles.Agent, IsActive: false })
        {
            // 停用账号不允许重连：未过期的 JWT 也不能建实时连接
            _logger.LogWarning("停用客服 {Username} 尝试建立实时连接，已拒绝", account.Username);
            Context.Abort();
            return;
        }

        if (account is { Role: Roles.Agent })
        {
            _presence.Connect(account.Id, Context.ConnectionId);
            await Groups.AddToGroupAsync(Context.ConnectionId, ChatGroups.Agent(account.Id));

            // 客服连上即加入自己所有进行中的会话，避免新消息漏收
            var conversationIds = await _db.Conversations.AsNoTracking()
                .Where(c => c.AgentId == account.Id && c.Status == ConversationStatus.Active)
                .Select(c => c.Id)
                .ToListAsync(Context.ConnectionAborted);
            foreach (var conversationId in conversationIds)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, ChatGroups.Conversation(conversationId));
            }

            account.IsOnline = true;
            await _db.SaveChangesAsync(Context.ConnectionAborted);

            _logger.LogInformation(
                "客服 {Username} 已连接，加入 {Count} 个进行中会话",
                account.Username, conversationIds.Count);
            await BroadcastPresenceAsync(account.Id, true);
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var account = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == CurrentAccountId, CancellationToken.None);
        if (account is { Role: Roles.Agent })
        {
            var wentOffline = _presence.Disconnect(account.Id, Context.ConnectionId);
            if (wentOffline)
            {
                account.IsOnline = false;
                await _db.SaveChangesAsync();
                _logger.LogInformation("客服 {Username} 已离线", account.Username);
                await BroadcastPresenceAsync(account.Id, false);
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>客户端加入某个会话的接收组，返回当前水位用于补拉。</summary>
    public async Task<ConversationJoinResult> JoinConversation(int conversationId)
    {
        try
        {
            var account = await RequireAccountAsync();
            var conversation = await _chat.GetConversationAsync(conversationId, account.Id, account.Role, Context.ConnectionAborted);
            await Groups.AddToGroupAsync(Context.ConnectionId, ChatGroups.Conversation(conversationId));
            return new ConversationJoinResult(conversationId, conversation.Status, conversation.LastMessageSeq);
        }
        catch (ChatException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    /// <summary>发送消息：先落库，回执含服务端权威 Seq，成功后再广播。</summary>
    public async Task<SendMessageAck> SendMessage(int conversationId, string? content, string? clientMessageId)
    {
        try
        {
            var account = await RequireAccountAsync();
            var result = await _chat.AppendMessageAsync(conversationId, account, content, clientMessageId, Context.ConnectionAborted);

            if (result.IsNew)
            {
                await Clients.Group(ChatGroups.Conversation(conversationId))
                    .SendAsync("ReceiveMessage", result.Message, CancellationToken.None);
            }

            var message = result.Message;
            return new SendMessageAck(
                message.Id,
                message.ConversationId,
                message.Seq,
                message.ClientMessageId,
                message.SentAt,
                Duplicated: !result.IsNew);
        }
        catch (ChatException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    /// <summary>确认收到对端消息（Seq ≤ upToSeq）。</summary>
    public async Task AckDelivered(int conversationId, long upToSeq)
    {
        try
        {
            var account = await RequireAccountAsync();
            var confirmedAt = await _chat.MarkDeliveredAsync(conversationId, account.Id, account.Role, upToSeq, Context.ConnectionAborted);
            if (confirmedAt is not null)
            {
                // OthersInGroup：确认方自己的连接不收回执；同一账号的其它连接由客户端按 byAccountId 过滤
                await Clients.OthersInGroup(ChatGroups.Conversation(conversationId))
                    .SendAsync(
                        "MessageDelivered",
                        new { conversationId, upToSeq, byAccountId = account.Id, confirmedAt },
                        CancellationToken.None);
            }
        }
        catch (ChatException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    /// <summary>确认已读对端消息（Seq ≤ upToSeq）。</summary>
    public async Task AckRead(int conversationId, long upToSeq)
    {
        try
        {
            var account = await RequireAccountAsync();
            var confirmedAt = await _chat.MarkReadAsync(conversationId, account.Id, account.Role, upToSeq, Context.ConnectionAborted);
            if (confirmedAt is not null)
            {
                await Clients.OthersInGroup(ChatGroups.Conversation(conversationId))
                    .SendAsync(
                        "MessageRead",
                        new { conversationId, upToSeq, byAccountId = account.Id, confirmedAt },
                        CancellationToken.None);
            }
        }
        catch (ChatException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    private async Task<Account> RequireAccountAsync()
    {
        var accountId = CurrentAccountId;
        var account = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, Context.ConnectionAborted)
            ?? throw new ChatException(ChatError.Forbidden, "登录状态无效");
        if (account.Role == Roles.Agent && !account.IsActive)
        {
            throw new ChatException(ChatError.Forbidden, "账号已停用");
        }
        return account;
    }

    private async Task BroadcastPresenceAsync(int agentId, bool isOnline)
    {
        var activeCount = await _chat.CountActiveAsync(agentId, Context.ConnectionAborted);
        await Clients.All.SendAsync(
            "AgentPresenceChanged",
            new { agentId, isOnline, activeConversationCount = activeCount },
            CancellationToken.None);
    }
}
