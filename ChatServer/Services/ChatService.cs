using System.Collections.Concurrent;
using System.Linq.Expressions;
using ChatServer.Data;
using ChatServer.Dtos;
using ChatServer.Models;
using Microsoft.EntityFrameworkCore;

namespace ChatServer.Services;

/// <summary>
/// 会话与消息的领域服务。可靠性要点：
/// 1) Seq 由服务端权威分配，同会话内串行（进程内闸门）+ 唯一索引 + 冲突重试；
/// 2) ClientMessageId 幂等，重复提交返回原有消息、不新增、不重复广播；
/// 3) 会话创建加闸门，客服并发上限不被并发请求击穿。
/// </summary>
public class ChatService
{
    public const int MaxContentLength = 2000;
    public const int MaxMessagesPerPull = 500;
    private const int MaxSeqAttempts = 5;

    private static readonly ConcurrentDictionary<int, SeqGate> SeqGates = new();
    private static readonly object SeqGateSync = new();
    private static readonly SemaphoreSlim CreateGate = new(1, 1);

    private static readonly Expression<Func<Message, MessageDto>> MessageProjection = m => new MessageDto(
        m.Id,
        m.ConversationId,
        m.Seq,
        m.SenderId,
        m.Sender != null ? m.Sender.DisplayName : string.Empty,
        m.SenderRole,
        m.Content,
        m.ClientMessageId,
        m.SentAt,
        m.DeliveredAt,
        m.ReadAt);

    private readonly AppDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ChatService> _logger;

    public ChatService(AppDbContext db, IConfiguration configuration, ILogger<ChatService> logger)
    {
        _db = db;
        _configuration = configuration;
        _logger = logger;
    }

    // ---------------------------------------------------------------- 客服

    public async Task<IReadOnlyList<AgentDto>> GetAgentsAsync(CancellationToken ct = default)
    {
        var agents = await _db.Accounts.AsNoTracking()
            .Where(a => a.Role == Roles.Agent)
            .OrderBy(a => a.Id)
            .ToListAsync(ct);
        var loads = await ActiveCountsAsync(ct);

        return agents
            .Select(a => new AgentDto(
                a.Id,
                a.Username,
                a.DisplayName,
                a.IsOnline,
                loads.TryGetValue(a.Id, out var count) ? count : 0,
                a.MaxConcurrentConversations))
            .ToList();
    }

    public Task<int> CountActiveAsync(int agentId, CancellationToken ct = default)
        => _db.Conversations.CountAsync(c => c.AgentId == agentId && c.Status == ConversationStatus.Active, ct);

    public async Task<Dictionary<int, int>> ActiveCountsAsync(CancellationToken ct = default)
    {
        var rows = await _db.Conversations.AsNoTracking()
            .Where(c => c.Status == ConversationStatus.Active)
            .GroupBy(c => c.AgentId)
            .Select(g => new { AgentId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return rows.ToDictionary(row => row.AgentId, row => row.Count);
    }

    // ---------------------------------------------------------------- 会话

    public async Task<CreateConversationResult> CreateOrGetAsync(int visitorId, int? agentId, CancellationToken ct = default)
    {
        await CreateGate.WaitAsync(ct);
        try
        {
            var visitor = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == visitorId && a.Role == Roles.Visitor, ct)
                ?? throw new ChatException(ChatError.Forbidden, "仅访客可以发起会话");

            if (agentId is int requestedAgentId)
            {
                var agent = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == requestedAgentId && a.Role == Roles.Agent, ct)
                    ?? throw new ChatException(ChatError.NotFound, "客服不存在");

                // 访客同一时间只能有一个进行中的会话：换客服需先结束当前会话
                var visitorActive = await _db.Conversations.AsNoTracking()
                    .Where(c => c.VisitorId == visitorId && c.Status == ConversationStatus.Active)
                    .OrderByDescending(c => c.LastActivityAt)
                    .FirstOrDefaultAsync(ct);

                if (visitorActive is not null)
                {
                    if (visitorActive.AgentId != agent.Id)
                    {
                        throw new ChatException(ChatError.Conflict, "您已有进行中的会话，请先结束当前会话后再联系其他客服");
                    }

                    return new CreateConversationResult(await LoadDtoAsync(visitorActive.Id, visitorId, ct), false);
                }

                EnsureCapacity(agent, await CountActiveAsync(agent.Id, ct));
                return await CreateAsync(visitor, agent, ct);
            }

            // 自动分配：先复用访客已有的进行中会话
            var active = await _db.Conversations.AsNoTracking()
                .Where(c => c.VisitorId == visitorId && c.Status == ConversationStatus.Active)
                .OrderByDescending(c => c.LastActivityAt)
                .FirstOrDefaultAsync(ct);
            if (active is not null)
            {
                return new CreateConversationResult(await LoadDtoAsync(active.Id, visitorId, ct), false);
            }

            var agents = await _db.Accounts.Where(a => a.Role == Roles.Agent).ToListAsync(ct);
            if (agents.Count == 0)
            {
                throw new ChatException(ChatError.Conflict, "当前没有可用的客服");
            }

            var loads = await ActiveCountsAsync(ct);
            var chosen = agents
                .Where(a => a.IsOnline && LoadOf(loads, a.Id) < a.MaxConcurrentConversations)
                .OrderBy(a => LoadOf(loads, a.Id))
                .ThenBy(a => a.Id)
                .FirstOrDefault()
                ?? throw new ChatException(ChatError.Conflict, "当前没有在线且有余量的客服，请稍后再试");

            return await CreateAsync(visitor, chosen, ct);
        }
        finally
        {
            CreateGate.Release();
        }
    }

    public async Task<IReadOnlyList<ConversationDto>> GetMineAsync(int accountId, string role, CancellationToken ct = default)
    {
        var query = _db.Conversations.AsNoTracking().AsQueryable();
        query = role == Roles.Agent
            ? query.Where(c => c.AgentId == accountId)
            : query.Where(c => c.VisitorId == accountId);

        var ordered = query
            .OrderByDescending(c => c.Status == ConversationStatus.Active)
            .ThenByDescending(c => c.LastActivityAt);

        var rows = await ProjectConversations(ordered, accountId).ToListAsync(ct);
        if (role != Roles.Agent)
        {
            return rows;
        }

        // 客服工作台按「访客」列人：同一访客只保留一行，优先进行中的会话
        var seen = new HashSet<int>();
        return rows.Where(row => seen.Add(row.VisitorId)).ToList();
    }

    public async Task<ConversationDto> GetConversationAsync(int conversationId, int accountId, string role, CancellationToken ct = default)
    {
        await LoadConversationForAsync(conversationId, accountId, role, ct);
        return await LoadDtoAsync(conversationId, accountId, ct);
    }

    public async Task<ConversationDto> CloseAsync(int conversationId, int accountId, string role, CancellationToken ct = default)
    {
        var conversation = await LoadConversationForAsync(conversationId, accountId, role, ct);
        if (conversation.Status == ConversationStatus.Active)
        {
            var now = DateTimeOffset.UtcNow;
            conversation.Status = ConversationStatus.Closed;
            conversation.ClosedAt = now;
            conversation.LastActivityAt = now;

            var ticket = await _db.Tickets.FirstOrDefaultAsync(t => t.ConversationId == conversationId, ct);
            if (ticket is not null)
            {
                ticket.Status = TicketStatus.Closed;
                ticket.ClosedAt = now;
            }

            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("会话 {ConversationId} 已关闭，操作人 {AccountId}", conversationId, accountId);
        }

        return await GetConversationAsync(conversationId, accountId, role, ct);
    }

    // ---------------------------------------------------------------- 消息

    public async Task<IReadOnlyList<MessageDto>> GetMessagesAsync(
        int conversationId,
        int accountId,
        string role,
        long afterSeq,
        int limit = MaxMessagesPerPull,
        CancellationToken ct = default)
    {
        await LoadConversationForAsync(conversationId, accountId, role, ct);
        var take = Math.Clamp(limit, 1, MaxMessagesPerPull);
        var cutoff = RetentionService.Cutoff(
            DateTimeOffset.UtcNow,
            _configuration.GetValue("Retention:Days", RetentionService.DefaultRetentionDays));

        return await _db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.Seq > afterSeq && m.SentAt >= cutoff)
            .OrderBy(m => m.Seq)
            .Take(take)
            .Select(MessageProjection)
            .ToListAsync(ct);
    }

    /// <summary>
    /// 追加一条消息。先落库，调用方只有在拿到结果后才广播。
    /// 同一 ClientMessageId 重复提交返回既有消息（IsNew=false），不新增、不重排。
    /// </summary>
    public async Task<AppendResult> AppendMessageAsync(
        int conversationId,
        Account sender,
        string? content,
        string? clientMessageId,
        CancellationToken ct = default)
    {
        var text = (content ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            throw new ChatException(ChatError.BadRequest, "消息内容不能为空");
        }

        if (text.Length > MaxContentLength)
        {
            throw new ChatException(ChatError.BadRequest, $"消息内容不能超过 {MaxContentLength} 个字符");
        }

        var clientId = string.IsNullOrWhiteSpace(clientMessageId) ? null : clientMessageId.Trim();
        if (clientId is { Length: > 100 })
        {
            throw new ChatException(ChatError.BadRequest, "ClientMessageId 不能超过 100 个字符");
        }

        var gate = AcquireSeqGate(conversationId);
        var entered = false;
        try
        {
            await gate.Semaphore.WaitAsync(ct);
            entered = true;
            if (clientId is not null)
            {
                var duplicate = await FindByClientMessageIdAsync(conversationId, clientId, sender, ct);
                if (duplicate is not null)
                {
                    _logger.LogInformation(
                        "会话 {ConversationId} 命中幂等键 {ClientMessageId}，返回既有消息 Seq={Seq}",
                        conversationId, clientId, duplicate.Message.Seq);
                    return duplicate;
                }
            }

            for (var attempt = 1; attempt <= MaxSeqAttempts; attempt++)
            {
                try
                {
                    var conversation = await _db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId, ct)
                        ?? throw new ChatException(ChatError.NotFound, "会话不存在");

                    EnsureParticipant(conversation, sender);
                    if (conversation.Status != ConversationStatus.Active)
                    {
                        throw new ChatException(ChatError.Conflict, "会话已关闭，无法继续发送");
                    }

                    var seq = conversation.LastMessageSeq + 1;
                    var now = DateTimeOffset.UtcNow;
                    var message = new Message
                    {
                        ConversationId = conversationId,
                        Seq = seq,
                        SenderId = sender.Id,
                        SenderRole = sender.Role,
                        Content = text,
                        ClientMessageId = clientId,
                        SentAt = now
                    };

                    _db.Messages.Add(message);
                    conversation.LastMessageSeq = seq;
                    conversation.LastActivityAt = now;
                    await _db.SaveChangesAsync(ct);

                    var dto = new MessageDto(
                        message.Id,
                        message.ConversationId,
                        message.Seq,
                        message.SenderId,
                        sender.DisplayName,
                        message.SenderRole,
                        message.Content,
                        message.ClientMessageId,
                        message.SentAt,
                        message.DeliveredAt,
                        message.ReadAt);

                    return new AppendResult(dto, true);
                }
                catch (DbUpdateException ex) when (attempt < MaxSeqAttempts)
                {
                    _db.ChangeTracker.Clear();
                    _logger.LogWarning(ex, "会话 {ConversationId} 序号冲突，第 {Attempt} 次重试", conversationId, attempt);
                    await Task.Delay(5 * attempt, ct);
                }
            }

            throw new ChatException(ChatError.Conflict, "消息序号分配冲突，请重试");
        }
        finally
        {
            if (entered)
            {
                gate.Semaphore.Release();
            }

            ReleaseSeqGate(conversationId, gate);
        }
    }

    /// <summary>标记对端消息已送达，返回确认时刻（无更新则返回 null）。</summary>
    public async Task<DateTimeOffset?> MarkDeliveredAsync(
        int conversationId, int accountId, string role, long upToSeq, CancellationToken ct = default)
    {
        await LoadConversationForAsync(conversationId, accountId, role, ct);
        var now = DateTimeOffset.UtcNow;
        var updated = await _db.Messages
            .Where(m => m.ConversationId == conversationId
                        && m.Seq <= upToSeq
                        && m.SenderId != accountId
                        && m.DeliveredAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.DeliveredAt, (DateTimeOffset?)now), ct);

        return updated > 0 ? now : null;
    }

    /// <summary>标记对端消息已读，返回确认时刻（无更新则返回 null）。</summary>
    public async Task<DateTimeOffset?> MarkReadAsync(
        int conversationId, int accountId, string role, long upToSeq, CancellationToken ct = default)
    {
        await LoadConversationForAsync(conversationId, accountId, role, ct);
        var now = DateTimeOffset.UtcNow;
        var updated = await _db.Messages
            .Where(m => m.ConversationId == conversationId
                        && m.Seq <= upToSeq
                        && m.SenderId != accountId
                        && m.ReadAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(m => m.ReadAt, (DateTimeOffset?)now)
                    .SetProperty(m => m.DeliveredAt, m => m.DeliveredAt ?? (DateTimeOffset?)now),
                ct);

        return updated > 0 ? now : null;
    }

    // ---------------------------------------------------------------- 内部

    /// <summary>
    /// 会话级发送闸门。引用计数归零即从字典摘除，避免服务长期运行后按会话数无界增长。
    /// </summary>
    private static SeqGate AcquireSeqGate(int conversationId)
    {
        lock (SeqGateSync)
        {
            var gate = SeqGates.GetOrAdd(conversationId, _ => new SeqGate());
            gate.References++;
            return gate;
        }
    }

    private static void ReleaseSeqGate(int conversationId, SeqGate gate)
    {
        lock (SeqGateSync)
        {
            gate.References--;
            if (gate.References == 0
                && SeqGates.TryRemove(KeyValuePair.Create(conversationId, gate)))
            {
                gate.Semaphore.Dispose();
            }
        }
    }

    /// <summary>会话内串行闸门：同一会话的 Seq 分配串行，不同会话互不影响。</summary>
    private sealed class SeqGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        /// <summary>持有 + 等待该闸门的调用数，仅在 <see cref="SeqGateSync"/> 锁内读写。</summary>
        public int References { get; set; }
    }

    private async Task<AppendResult?> FindByClientMessageIdAsync(
        int conversationId, string clientMessageId, Account sender, CancellationToken ct)
    {
        var existing = await _db.Messages.AsNoTracking()
            .FirstOrDefaultAsync(m => m.ConversationId == conversationId && m.ClientMessageId == clientMessageId, ct);
        if (existing is null)
        {
            return null;
        }

        if (existing.SenderId != sender.Id)
        {
            throw new ChatException(ChatError.Forbidden, "该 ClientMessageId 已被其他用户使用");
        }

        var conversation = await _db.Conversations.AsNoTracking().FirstAsync(c => c.Id == conversationId, ct);
        var dto = new MessageDto(
            existing.Id,
            existing.ConversationId,
            existing.Seq,
            existing.SenderId,
            sender.DisplayName,
            existing.SenderRole,
            existing.Content,
            existing.ClientMessageId,
            existing.SentAt,
            existing.DeliveredAt,
            existing.ReadAt);

        return new AppendResult(dto, false);
    }

    private static int LoadOf(Dictionary<int, int> loads, int agentId)
        => loads.TryGetValue(agentId, out var count) ? count : 0;

    private void EnsureCapacity(Account agent, int activeCount)
    {
        if (activeCount >= agent.MaxConcurrentConversations)
        {
            throw new ChatException(
                ChatError.Conflict,
                $"{agent.DisplayName} 当前会话已满（{activeCount}/{agent.MaxConcurrentConversations}），请稍后再试或选择其他客服");
        }
    }

    private async Task<CreateConversationResult> CreateAsync(Account visitor, Account agent, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var conversation = new Conversation
        {
            VisitorId = visitor.Id,
            AgentId = agent.Id,
            Status = ConversationStatus.Active,
            CreatedAt = now,
            LastActivityAt = now
        };

        _db.Conversations.Add(conversation);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            _db.ChangeTracker.Clear();
            var existing = await FindActiveAsync(visitor.Id, agent.Id, ct);
            if (existing is null)
            {
                _logger.LogError(ex, "创建会话失败：访客 {VisitorId} 客服 {AgentId}", visitor.Id, agent.Id);
                throw new ChatException(ChatError.Conflict, "创建会话失败，请重试");
            }

            _logger.LogWarning(ex, "创建会话冲突，复用进行中的会话 {ConversationId}", existing.Id);
            return new CreateConversationResult(await LoadDtoAsync(existing.Id, visitor.Id, ct), false);
        }

        _logger.LogInformation(
            "创建会话 {ConversationId}：访客 {VisitorId} ↔ 客服 {AgentId}",
            conversation.Id, visitor.Id, agent.Id);

        // 会话对应一张工单，供客服后台回溯与导出；失败不影响会话本身，启动时会补建
        _db.Tickets.Add(new Ticket
        {
            TicketNo = BuildTicketNo(conversation),
            ConversationId = conversation.Id,
            Status = TicketStatus.Open,
            CreatedAt = conversation.CreatedAt
        });
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            _db.ChangeTracker.Clear();
            _logger.LogWarning(ex, "工单创建失败，启动时会补建：会话 {ConversationId}", conversation.Id);
        }

        return new CreateConversationResult(await LoadDtoAsync(conversation.Id, visitor.Id, ct), true);
    }

    private static string BuildTicketNo(Conversation conversation)
        => $"T-{conversation.CreatedAt:yyyyMMdd}-{conversation.Id:D5}";

    private async Task<Conversation> LoadConversationForAsync(int conversationId, int accountId, string role, CancellationToken ct)
    {
        var conversation = await _db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId, ct)
            ?? throw new ChatException(ChatError.NotFound, "会话不存在");

        if (role == Roles.Agent ? conversation.AgentId != accountId : conversation.VisitorId != accountId)
        {
            throw new ChatException(ChatError.Forbidden, "无权访问该会话");
        }

        return conversation;
    }

    private static void EnsureParticipant(Conversation conversation, Account sender)
    {
        if (conversation.VisitorId != sender.Id && conversation.AgentId != sender.Id)
        {
            throw new ChatException(ChatError.Forbidden, "无权在该会话中发言");
        }
    }

    private Task<Conversation?> FindActiveAsync(int visitorId, int agentId, CancellationToken ct)
        => _db.Conversations
            .FirstOrDefaultAsync(
                c => c.VisitorId == visitorId && c.AgentId == agentId && c.Status == ConversationStatus.Active,
                ct);

    private async Task<ConversationDto> LoadDtoAsync(int conversationId, int viewerId, CancellationToken ct)
        => await ProjectConversations(_db.Conversations.AsNoTracking().Where(c => c.Id == conversationId), viewerId)
            .FirstAsync(ct);

    private static IQueryable<ConversationDto> ProjectConversations(IQueryable<Conversation> query, int viewerId)
        => query.Select(c => new ConversationDto(
            c.Id,
            c.Status,
            c.VisitorId,
            c.Visitor != null ? c.Visitor.DisplayName : string.Empty,
            c.AgentId,
            c.Agent != null ? c.Agent.DisplayName : string.Empty,
            c.LastMessageSeq,
            c.Messages.OrderByDescending(m => m.Seq).Select(m => m.Content).FirstOrDefault(),
            c.Messages.OrderByDescending(m => m.Seq).Select(m => (DateTimeOffset?)m.SentAt).FirstOrDefault(),
            c.Messages.Count(m => m.SenderId != viewerId && m.ReadAt == null),
            c.CreatedAt,
            c.LastActivityAt));
}
