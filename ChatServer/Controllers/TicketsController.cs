using System.Text;
using ChatServer.Data;
using ChatServer.Dtos;
using ChatServer.Models;
using ChatServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChatServer.Controllers;

/// <summary>工单：客服查看自己会话的聊天记录，并可按 TXT / CSV 导出。</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = Roles.Agent)]
public class TicketsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ChatService _chat;
    private readonly ConversationRealtimeService _realtime;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TicketsController> _logger;

    public TicketsController(
        AppDbContext db,
        ChatService chat,
        ConversationRealtimeService realtime,
        IConfiguration configuration,
        ILogger<TicketsController> logger)
    {
        _db = db;
        _chat = chat;
        _realtime = realtime;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>工单数据保留天数（默认 30 天）。</summary>
    [HttpGet("meta")]
    public ActionResult Meta()
        => Ok(new
        {
            retentionDays = _configuration.GetValue("Retention:Days", RetentionService.DefaultRetentionDays)
        });

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TicketDto>>> List(CancellationToken ct)
    {
        var accountId = User.GetAccountId();
        if (accountId == 0)
        {
            return Unauthorized(new { error = "登录状态无效" });
        }

        var cutoff = RetentionCutoff();
        var tickets = await _db.Tickets.AsNoTracking()
            .Where(ticket => ticket.Conversation!.AgentId == accountId)
            .OrderByDescending(ticket => ticket.CreatedAt)
            .Select(TicketProjections.Build(cutoff))
            .ToListAsync(ct);

        return Ok(tickets);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<TicketDetailDto>> Get(int id, CancellationToken ct)
    {
        var accountId = User.GetAccountId();
        if (accountId == 0)
        {
            return Unauthorized(new { error = "登录状态无效" });
        }

        var cutoff = RetentionCutoff();
        var ticket = await _db.Tickets.AsNoTracking()
            .Where(item => item.Id == id && item.Conversation!.AgentId == accountId)
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

    /// <summary>把当前客服名下进行中的工单转派给另一名客服，并完成实时组员迁移。</summary>
    [HttpPost("{id:int}/transfer")]
    public async Task<ActionResult<TicketDto>> Transfer(
        int id,
        [FromBody] TransferTicketRequest? request,
        CancellationToken ct)
    {
        var accountId = User.GetAccountId();
        if (accountId == 0)
        {
            return Unauthorized(new { error = "登录状态无效" });
        }

        if (request is null)
        {
            return BadRequest(new { error = "请求无效" });
        }

        try
        {
            var result = await _chat.TransferTicketAsync(id, accountId, request.AgentId, request.Note, ct);

            // 转派已提交：实时通知尽力而为，失败只记日志，不把已生效的转派报成 500
            await _realtime.NotifyTransferredAsync(result);

            _logger.LogInformation(
                "工单 {TicketNo} 已转派给客服 {AgentId}",
                result.Ticket.TicketNo, result.Conversation.AgentId);
            return Ok(result.Ticket);
        }
        catch (ChatException ex)
        {
            return Error(ex);
        }
    }

    [HttpGet("{id:int}/export")]
    public async Task<IActionResult> Export(
        int id,
        [FromQuery] string format = "txt",
        CancellationToken ct = default)
    {
        var accountId = User.GetAccountId();
        if (accountId == 0)
        {
            return Unauthorized(new { error = "登录状态无效" });
        }

        var ticket = await _db.Tickets.AsNoTracking()
            .Where(item => item.Id == id && item.Conversation!.AgentId == accountId)
            .Select(item => new ExportTicket(
                item.TicketNo,
                item.Status,
                item.CreatedAt,
                item.ClosedAt,
                item.Conversation!.Visitor != null ? item.Conversation.Visitor.DisplayName : string.Empty,
                item.Conversation!.Agent != null ? item.Conversation.Agent.DisplayName : string.Empty,
                item.ConversationId))
            .FirstOrDefaultAsync(ct);
        if (ticket is null)
        {
            return NotFound(new { error = "工单不存在" });
        }

        var cutoff = RetentionCutoff();
        var messages = await _db.Messages.AsNoTracking()
            .Where(message => message.ConversationId == ticket.ConversationId && message.SentAt >= cutoff)
            .OrderBy(message => message.Seq)
            .Select(MessageProjections.Projection)
            .ToListAsync(ct);

        var normalized = (format ?? "txt").Trim().ToLowerInvariant();
        var isCsv = normalized == "csv";
        var content = isCsv ? BuildCsv(ticket, messages) : BuildText(ticket, messages);
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(content)).ToArray();

        _logger.LogInformation("导出工单 {TicketNo}，格式 {Format}", ticket.TicketNo, normalized);
        return isCsv
            ? File(bytes, "text/csv; charset=utf-8", $"{ticket.TicketNo}.csv")
            : File(bytes, "text/plain; charset=utf-8", $"{ticket.TicketNo}.txt");
    }

    private DateTimeOffset RetentionCutoff()
        => RetentionService.Cutoff(
            DateTimeOffset.UtcNow,
            _configuration.GetValue("Retention:Days", RetentionService.DefaultRetentionDays));

    private static string StatusText(string status)
        => status == TicketStatus.Open ? "进行中" : "已结束";

    private static string BuildText(ExportTicket ticket, IReadOnlyList<MessageDto> messages)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"工单号：{ticket.TicketNo}");
        builder.AppendLine($"访客：{ticket.VisitorName}");
        builder.AppendLine($"客服：{ticket.AgentName}");
        builder.AppendLine($"状态：{StatusText(ticket.Status)}");
        builder.AppendLine($"创建时间：{ticket.CreatedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine(
            $"结束时间：{(ticket.ClosedAt is null ? "—" : ticket.ClosedAt.Value.ToString("yyyy-MM-dd HH:mm:ss zzz"))}");
        builder.AppendLine($"消息数：{messages.Count}");
        builder.AppendLine(new string('-', 48));
        foreach (var message in messages)
        {
            var role = message.SenderRole == Roles.Agent ? "客服" : "访客";
            var content = message.Content.Replace("\r\n", "\n").Replace("\n", " / ");
            builder.AppendLine($"[{message.SentAt:yyyy-MM-dd HH:mm:ss}] {role} {message.SenderName}：{content}");
        }

        return builder.ToString();
    }

    private static string BuildCsv(ExportTicket ticket, IReadOnlyList<MessageDto> messages)
    {
        var builder = new StringBuilder();
        builder.AppendLine("工单号,时间,角色,发送方,内容");
        foreach (var message in messages)
        {
            builder.AppendLine(string.Join(
                ',',
                Csv(ticket.TicketNo),
                Csv(message.SentAt.ToString("yyyy-MM-dd HH:mm:ss")),
                Csv(message.SenderRole == Roles.Agent ? "客服" : "访客"),
                Csv(message.SenderName),
                Csv(message.Content)));
        }

        return builder.ToString();
    }

    private static string Csv(string value)
        => $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";

    private sealed record ExportTicket(
        string TicketNo,
        string Status,
        DateTimeOffset CreatedAt,
        DateTimeOffset? ClosedAt,
        string VisitorName,
        string AgentName,
        int ConversationId);

    private ActionResult Error(ChatException ex) => ex.Error switch
    {
        ChatError.BadRequest => BadRequest(new { error = ex.Message }),
        ChatError.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message }),
        ChatError.NotFound => NotFound(new { error = ex.Message }),
        ChatError.Conflict => Conflict(new { error = ex.Message }),
        _ => StatusCode(StatusCodes.Status500InternalServerError, new { error = ex.Message })
    };
}
