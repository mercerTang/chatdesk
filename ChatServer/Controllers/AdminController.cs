using ChatServer.Data;
using ChatServer.Dtos;
using ChatServer.Models;
using ChatServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChatServer.Controllers;

/// <summary>
/// 管理中台：管理员专属。管客服账号、跨客服读会话与工单、强制转派。
/// 整个控制器限定 admin 角色，客服端接口不为此放宽（两套接口互不授受）。
/// </summary>
[ApiController]
[Route("api/admin")]
[Authorize(Roles = Roles.Admin)]
public class AdminController : ControllerBase
{
    private const int MinUsernameLength = 3;
    private const int MaxUsernameLength = 32;
    private const int MinPasswordLength = 6;
    private const int MaxPasswordLength = 64;
    private const int MaxDisplayNameLength = 64;
    private const int MinConcurrentLimit = 1;
    private const int MaxConcurrentLimit = 50;
    private const int DefaultConcurrentLimit = 5;
    private const int MinStatsDays = 1;
    private const int MaxStatsDays = 90;
    private const int DefaultStatsDays = 14;

    private readonly AppDbContext _db;
    private readonly ChatService _chat;
    private readonly ConversationRealtimeService _realtime;
    private readonly AgentPresenceService _presence;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AdminController> _logger;

    public AdminController(
        AppDbContext db,
        ChatService chat,
        ConversationRealtimeService realtime,
        AgentPresenceService presence,
        IConfiguration configuration,
        ILogger<AdminController> logger)
    {
        _db = db;
        _chat = chat;
        _realtime = realtime;
        _presence = presence;
        _configuration = configuration;
        _logger = logger;
    }

    // ---------------------------------------------------------------- 客服账号

    /// <summary>全部客服：在线、活跃会话数、上限、启用状态、创建时间。</summary>
    [HttpGet("agents")]
    public async Task<ActionResult<IReadOnlyList<AdminAgentDto>>> ListAgents(CancellationToken ct)
    {
        var agents = await _db.Accounts.AsNoTracking()
            .Where(a => a.Role == Roles.Agent)
            .OrderBy(a => a.Id)
            .ToListAsync(ct);
        var loads = await _chat.ActiveCountsAsync(ct);

        return Ok(agents.Select(agent => ToDto(agent, LoadOf(loads, agent.Id))).ToList());
    }

    /// <summary>新增客服：新增后即可登录（除非随后被停用）。</summary>
    [HttpPost("agents")]
    public async Task<ActionResult<AdminAgentDto>> CreateAgent(
        [FromBody] AdminCreateAgentRequest? request,
        CancellationToken ct)
    {
        if (request is null)
        {
            return BadRequest(new { error = "请求体不能为空" });
        }

        var username = (request.Username ?? string.Empty).Trim();
        var password = request.Password ?? string.Empty;

        if (username.Length is < MinUsernameLength or > MaxUsernameLength)
        {
            return BadRequest(new { error = $"登录名长度需在 {MinUsernameLength}-{MaxUsernameLength} 之间" });
        }

        if (username.Any(char.IsWhiteSpace) || username.Any(char.IsControl))
        {
            return BadRequest(new { error = "登录名不能包含空白或控制字符" });
        }

        if (password.Length is < MinPasswordLength or > MaxPasswordLength)
        {
            return BadRequest(new { error = $"密码长度需在 {MinPasswordLength}-{MaxPasswordLength} 之间" });
        }

        var maxConcurrent = request.MaxConcurrentConversations ?? DefaultConcurrentLimit;
        if (maxConcurrent is < MinConcurrentLimit or > MaxConcurrentLimit)
        {
            return BadRequest(new { error = $"并发上限需在 {MinConcurrentLimit}-{MaxConcurrentLimit} 之间" });
        }

        var displayName = (request.DisplayName ?? string.Empty).Trim();
        if (displayName.Length == 0)
        {
            displayName = username;
        }

        if (ValidateDisplayName(displayName) is { } displayError)
        {
            return BadRequest(new { error = displayError });
        }

        if (await _db.Accounts.AnyAsync(a => a.Username == username, ct))
        {
            return Conflict(new { error = "用户名已存在" });
        }

        var account = new Account
        {
            Username = username,
            DisplayName = displayName,
            PasswordHash = PasswordHasher.Hash(password),
            Role = Roles.Agent,
            IsActive = true,
            MaxConcurrentConversations = maxConcurrent
        };

        _db.Accounts.Add(account);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "新增客服用户名冲突：{Username}", username);
            return Conflict(new { error = "用户名已存在" });
        }

        _logger.LogInformation(
            "管理员 {AdminId} 新增客服 {Username}（Id={Id}）",
            User.GetAccountId(), account.Username, account.Id);
        return StatusCode(StatusCodes.Status201Created, ToDto(account, 0));
    }

    /// <summary>改客服资料：展示名、并发上限、启用停用、重置密码，均可选。</summary>
    [HttpPatch("agents/{id:int}")]
    public async Task<ActionResult<AdminAgentDto>> UpdateAgent(
        int id,
        [FromBody] AdminUpdateAgentRequest? request,
        CancellationToken ct)
    {
        if (request is null)
        {
            return BadRequest(new { error = "请求体不能为空" });
        }

        var account = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == id && a.Role == Roles.Agent, ct);
        if (account is null)
        {
            return NotFound(new { error = "客服不存在" });
        }

        if (request.DisplayName is not null)
        {
            var displayName = request.DisplayName.Trim();
            if (displayName.Length == 0)
            {
                return BadRequest(new { error = "展示名不能为空" });
            }

            if (ValidateDisplayName(displayName) is { } displayError)
            {
                return BadRequest(new { error = displayError });
            }

            account.DisplayName = displayName;
        }

        if (request.MaxConcurrentConversations is int limit)
        {
            if (limit is < MinConcurrentLimit or > MaxConcurrentLimit)
            {
                return BadRequest(new { error = $"并发上限需在 {MinConcurrentLimit}-{MaxConcurrentLimit} 之间" });
            }

            account.MaxConcurrentConversations = limit;
        }

        if (request.Password is not null)
        {
            if (request.Password.Length is < MinPasswordLength or > MaxPasswordLength)
            {
                return BadRequest(new { error = $"密码长度需在 {MinPasswordLength}-{MaxPasswordLength} 之间" });
            }

            account.PasswordHash = PasswordHasher.Hash(request.Password);
        }

        var justDeactivated = false;
        if (request.IsActive is bool isActive)
        {
            justDeactivated = account.IsActive && !isActive;
            account.IsActive = isActive;
        }

        await _db.SaveChangesAsync(ct);

        // 停用不硬断连接：只推 SessionRevoked 让前端登出，进行中的会话不被打断
        if (justDeactivated)
        {
            await _realtime.NotifySessionRevokedAsync(account.Id);
            _logger.LogInformation(
                "管理员 {AdminId} 停用客服 {Username}（Id={Id}）",
                User.GetAccountId(), account.Username, account.Id);
        }

        return Ok(ToDto(account, await _chat.CountActiveAsync(account.Id, ct)));
    }

    // ---------------------------------------------------------------- 跨客服读

    /// <summary>全量会话：按状态、客服、访客、创建时间筛选。</summary>
    [HttpGet("conversations")]
    public async Task<ActionResult<IReadOnlyList<AdminConversationDto>>> ListConversations(
        [FromQuery] string? status,
        [FromQuery] int? agentId,
        [FromQuery] int? visitorId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        CancellationToken ct)
    {
        var query = _db.Conversations.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
        {
            var wanted = status.Trim();
            query = query.Where(conversation => conversation.Status == wanted);
        }

        if (agentId is int wantedAgent)
        {
            query = query.Where(conversation => conversation.AgentId == wantedAgent);
        }

        if (visitorId is int wantedVisitor)
        {
            query = query.Where(conversation => conversation.VisitorId == wantedVisitor);
        }

        if (from is DateTimeOffset fromAt)
        {
            query = query.Where(conversation => conversation.CreatedAt >= fromAt);
        }

        if (to is DateTimeOffset toAt)
        {
            query = query.Where(conversation => conversation.CreatedAt <= toAt);
        }

        var rows = await query
            .OrderByDescending(conversation => conversation.LastActivityAt)
            .Select(conversation => new AdminConversationDto(
                conversation.Id,
                conversation.Status,
                conversation.VisitorId,
                conversation.Visitor != null ? conversation.Visitor.DisplayName : string.Empty,
                conversation.AgentId,
                conversation.Agent != null ? conversation.Agent.DisplayName : string.Empty,
                conversation.LastMessageSeq,
                conversation.Messages.OrderByDescending(message => message.Seq)
                    .Select(message => message.Content)
                    .FirstOrDefault(),
                conversation.Messages.OrderByDescending(message => message.Seq)
                    .Select(message => (DateTimeOffset?)message.SentAt)
                    .FirstOrDefault(),
                conversation.CreatedAt,
                conversation.LastActivityAt))
            .ToListAsync(ct);

        return Ok(rows);
    }

    /// <summary>全量工单：按状态、客服、创建时间筛选，投影与客服端工单列表同一份。</summary>
    [HttpGet("tickets")]
    public async Task<ActionResult<IReadOnlyList<TicketDto>>> ListTickets(
        [FromQuery] string? status,
        [FromQuery] int? agentId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        CancellationToken ct)
    {
        var cutoff = RetentionCutoff();
        var query = _db.Tickets.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
        {
            var wanted = status.Trim();
            query = query.Where(ticket => ticket.Status == wanted);
        }

        if (agentId is int wantedAgent)
        {
            query = query.Where(ticket => ticket.Conversation!.AgentId == wantedAgent);
        }

        if (from is DateTimeOffset fromAt)
        {
            query = query.Where(ticket => ticket.CreatedAt >= fromAt);
        }

        if (to is DateTimeOffset toAt)
        {
            query = query.Where(ticket => ticket.CreatedAt <= toAt);
        }

        var tickets = await query
            .OrderByDescending(ticket => ticket.CreatedAt)
            .Select(TicketProjections.Build(cutoff))
            .ToListAsync(ct);

        return Ok(tickets);
    }

    /// <summary>任意工单详情：聊天记录 + 转派台账（含强制转派的管理员）。</summary>
    [HttpGet("tickets/{id:int}")]
    public async Task<ActionResult<TicketDetailDto>> GetTicket(int id, CancellationToken ct)
    {
        var cutoff = RetentionCutoff();
        var ticket = await _db.Tickets.AsNoTracking()
            .Where(item => item.Id == id)
            .Select(TicketProjections.Build(cutoff))
            .FirstOrDefaultAsync(ct);
        if (ticket is null)
        {
            return NotFound(new { error = "工单不存在" });
        }

        var messages = await _db.Messages.AsNoTracking()
            .Where(message => message.ConversationId == ticket.ConversationId && message.SentAt >= cutoff)
            .OrderBy(message => message.Seq)
            .Select(MessageProjections.Projection)
            .ToListAsync(ct);

        var transfers = await _db.TicketTransfers.AsNoTracking()
            .Where(transfer => transfer.TicketId == id)
            .OrderBy(transfer => transfer.CreatedAt)
            .ThenBy(transfer => transfer.Id)
            .Select(TicketTransferProjections.Projection)
            .ToListAsync(ct);

        return Ok(new TicketDetailDto(ticket, messages, transfers));
    }

    /// <summary>强制转派任意进行中的工单：状态与容量规则同客服转派，只跳过归属校验。</summary>
    [HttpPost("tickets/{id:int}/transfer")]
    public async Task<ActionResult<TicketDto>> Transfer(
        int id,
        [FromBody] TransferTicketRequest? request,
        CancellationToken ct)
    {
        var operatorId = User.GetAccountId();
        if (operatorId == 0)
        {
            return Unauthorized(new { error = "登录状态无效" });
        }

        if (request is null)
        {
            return BadRequest(new { error = "请求无效" });
        }

        try
        {
            var result = await _chat.AdminTransferTicketAsync(id, operatorId, request.AgentId, request.Note, ct);

            // 转派已提交：实时通知尽力而为，失败只记日志，不把已生效的转派报成 500
            await _realtime.NotifyTransferredAsync(result);

            _logger.LogInformation(
                "管理员 {OperatorId} 将工单 {TicketNo} 强制转派给客服 {AgentId}",
                operatorId, result.Ticket.TicketNo, result.Conversation.AgentId);
            return Ok(result.Ticket);
        }
        catch (ChatException ex)
        {
            return Error(ex);
        }
    }

    // ---------------------------------------------------------------- 统计

    /// <summary>
    /// 仪表盘统计：概览、按日趋势与按客服维度，全部按需聚合（没有统计表）。
    /// 口径：窗口为 [今天-days+1, 明天)，按服务端本地日期切分；会话与工单按 CreatedAt 落窗，
    /// 消息按 SentAt 落窗；首响只看窗口内新建的会话；在线客服与进行中工单取实时存量，不落窗。
    /// </summary>
    [HttpGet("stats")]
    public async Task<ActionResult<AdminStatsDto>> GetStats([FromQuery] int? days, CancellationToken ct)
    {
        var window = days ?? DefaultStatsDays;
        if (window is < MinStatsDays or > MaxStatsDays)
        {
            return BadRequest(new { error = $"统计天数需在 {MinStatsDays}-{MaxStatsDays} 之间" });
        }

        // 时间列按 Unix 毫秒落库，本地日边界换算成瞬间后比较即可
        var now = DateTimeOffset.Now;
        var todayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
        var windowStart = todayStart.AddDays(-(window - 1));
        var windowEnd = todayStart.AddDays(1);

        var conversations = await _db.Conversations.AsNoTracking()
            .Where(conversation => conversation.CreatedAt >= windowStart && conversation.CreatedAt < windowEnd)
            .Select(conversation => new { conversation.Id, conversation.AgentId, conversation.CreatedAt })
            .ToListAsync(ct);

        var tickets = await _db.Tickets.AsNoTracking()
            .Where(ticket => ticket.CreatedAt >= windowStart && ticket.CreatedAt < windowEnd)
            .Select(ticket => new { ticket.CreatedAt })
            .ToListAsync(ct);

        var transferCount = await _db.TicketTransfers.AsNoTracking()
            .CountAsync(transfer => transfer.CreatedAt >= windowStart && transfer.CreatedAt < windowEnd, ct);

        // 进行中工单是当前存量，不落窗（与在线客服同为即时值）
        var openTicketCount = await _db.Tickets.AsNoTracking()
            .CountAsync(ticket => ticket.Status == TicketStatus.Open, ct);

        // 按窗口取最小列集的两次查询：一是窗口内发出的消息（计数），
        // 二是窗口内新建会话的全部消息（首响要看会话里最早的一问一答，可能晚于窗口边界）
        var windowMessages = await _db.Messages.AsNoTracking()
            .Where(message => message.SentAt >= windowStart && message.SentAt < windowEnd)
            .Select(message => new StatsMessageRow
            {
                ConversationId = message.ConversationId,
                Seq = message.Seq,
                SenderId = message.SenderId,
                SenderRole = message.SenderRole,
                SentAt = message.SentAt
            })
            .ToListAsync(ct);

        var conversationIds = conversations.Select(conversation => conversation.Id).ToList();
        var threadMessages = conversationIds.Count == 0
            ? new List<StatsMessageRow>()
            : await _db.Messages.AsNoTracking()
                .Where(message => conversationIds.Contains(message.ConversationId))
                .Select(message => new StatsMessageRow
                {
                    ConversationId = message.ConversationId,
                    Seq = message.Seq,
                    SenderId = message.SenderId,
                    SenderRole = message.SenderRole,
                    SentAt = message.SentAt
                })
                .ToListAsync(ct);

        // 按日趋势：先按本地日归桶，再补 0 值日，保证每天一行
        var conversationsPerDay = new int[window];
        var messagesPerDay = new int[window];
        var ticketsPerDay = new int[window];

        foreach (var conversation in conversations)
        {
            if (DayIndexOf(conversation.CreatedAt, windowStart, window) is int day)
            {
                conversationsPerDay[day]++;
            }
        }

        foreach (var message in windowMessages)
        {
            if (DayIndexOf(message.SentAt, windowStart, window) is int day)
            {
                messagesPerDay[day]++;
            }
        }

        foreach (var ticket in tickets)
        {
            if (DayIndexOf(ticket.CreatedAt, windowStart, window) is int day)
            {
                ticketsPerDay[day]++;
            }
        }

        var daily = new List<AdminStatsDailyDto>(window);
        for (var offset = 0; offset < window; offset++)
        {
            daily.Add(new AdminStatsDailyDto(
                DateOnly.FromDateTime(windowStart.AddDays(offset).DateTime),
                conversationsPerDay[offset],
                messagesPerDay[offset],
                ticketsPerDay[offset]));
        }

        // 首响：会话内首条 visitor 消息 → 其后首条 agent 消息的秒数；没有客服回复的会话不计样本
        var threads = threadMessages
            .GroupBy(message => message.ConversationId)
            .ToDictionary(group => group.Key, group => group.OrderBy(message => message.Seq).ToList());
        var firstResponseSeconds = new Dictionary<int, double>();
        foreach (var conversation in conversations)
        {
            if (!threads.TryGetValue(conversation.Id, out var thread))
            {
                continue;
            }

            var question = thread.FirstOrDefault(message => message.SenderRole == Roles.Visitor);
            if (question is null)
            {
                continue;
            }

            var reply = thread.FirstOrDefault(message => message.SenderRole == Roles.Agent && message.Seq > question.Seq);
            if (reply is null)
            {
                continue;
            }

            firstResponseSeconds[conversation.Id] = (reply.SentAt - question.SentAt).TotalSeconds;
        }

        var agents = await _db.Accounts.AsNoTracking()
            .Where(account => account.Role == Roles.Agent)
            .OrderBy(account => account.Id)
            .Select(account => new { account.Id, account.DisplayName })
            .ToListAsync(ct);
        var loads = await _chat.ActiveCountsAsync(ct);

        var agentRows = new List<AdminStatsAgentDto>(agents.Count);
        foreach (var agent in agents)
        {
            var agentConversationIds = conversations
                .Where(conversation => conversation.AgentId == agent.Id)
                .Select(conversation => conversation.Id)
                .ToList();
            var samples = agentConversationIds
                .Where(firstResponseSeconds.ContainsKey)
                .Select(id => firstResponseSeconds[id])
                .ToList();

            agentRows.Add(new AdminStatsAgentDto(
                agent.Id,
                agent.DisplayName,
                agentConversationIds.Count,
                windowMessages.Count(message => message.SenderRole == Roles.Agent && message.SenderId == agent.Id),
                samples.Count == 0 ? null : Math.Round(samples.Average(), 1),
                samples.Count,
                LoadOf(loads, agent.Id)));
        }

        agentRows = agentRows
            .OrderByDescending(row => row.ConversationCount)
            .ThenByDescending(row => row.MessageCount)
            .ThenBy(row => row.AgentId)
            .ToList();

        var overview = new AdminStatsOverviewDto(
            conversations.Count,
            windowMessages.Count,
            tickets.Count,
            transferCount,
            agents.Count(agent => _presence.ConnectionsOf(agent.Id).Count > 0),
            openTicketCount,
            firstResponseSeconds.Count == 0 ? null : Math.Round(firstResponseSeconds.Values.Average(), 1),
            firstResponseSeconds.Count);

        return Ok(new AdminStatsDto(overview, daily, agentRows));
    }

    // ---------------------------------------------------------------- 内部

    /// <summary>把瞬间映射到窗口内的本地日偏移；窗口外返回 null，边界口径变化时不越界。</summary>
    private static int? DayIndexOf(DateTimeOffset at, DateTimeOffset windowStart, int days)
    {
        var offset = (at.ToLocalTime().Date - windowStart.Date).Days;
        return offset >= 0 && offset < days ? offset : null;
    }

    /// <summary>统计用的消息行：只取聚合需要的最小列集。</summary>
    private sealed class StatsMessageRow
    {
        public int ConversationId { get; init; }
        public long Seq { get; init; }
        public int SenderId { get; init; }
        public string SenderRole { get; init; } = string.Empty;
        public DateTimeOffset SentAt { get; init; }
    }

    private DateTimeOffset RetentionCutoff()
        => RetentionService.Cutoff(
            DateTimeOffset.UtcNow,
            _configuration.GetValue("Retention:Days", RetentionService.DefaultRetentionDays));

    private static int LoadOf(Dictionary<int, int> loads, int agentId)
        => loads.TryGetValue(agentId, out var count) ? count : 0;

    private static AdminAgentDto ToDto(Account account, int activeCount)
        => new(
            account.Id,
            account.Username,
            account.DisplayName,
            account.IsOnline,
            account.IsActive,
            activeCount,
            account.MaxConcurrentConversations,
            account.CreatedAt);

    private static string? ValidateDisplayName(string displayName)
    {
        if (displayName.Length > MaxDisplayNameLength)
        {
            return $"展示名不能超过 {MaxDisplayNameLength} 个字符";
        }

        return displayName.Any(char.IsControl) ? "展示名不能包含控制字符" : null;
    }

    private ActionResult Error(ChatException ex) => ex.Error switch
    {
        ChatError.BadRequest => BadRequest(new { error = ex.Message }),
        ChatError.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message }),
        ChatError.NotFound => NotFound(new { error = ex.Message }),
        ChatError.Conflict => Conflict(new { error = ex.Message }),
        _ => StatusCode(StatusCodes.Status500InternalServerError, new { error = ex.Message })
    };
}
