using System.Linq.Expressions;
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
    private static Expression<Func<Ticket, TicketDto>> BuildTicketProjection(DateTimeOffset cutoff)
        => ticket => new TicketDto(
            ticket.Id,
            ticket.TicketNo,
            ticket.ConversationId,
            ticket.Status,
            ticket.Conversation!.Visitor != null ? ticket.Conversation.Visitor.DisplayName : string.Empty,
            ticket.Conversation!.Agent != null ? ticket.Conversation.Agent.DisplayName : string.Empty,
            ticket.Conversation.Messages.Count(message => message.SentAt >= cutoff),
            ticket.Conversation.Messages
                .Where(message => message.SentAt >= cutoff)
                .OrderByDescending(message => message.Seq)
                .Select(message => message.Content)
                .FirstOrDefault(),
            ticket.CreatedAt,
            ticket.ClosedAt,
            ticket.Conversation.LastActivityAt);

    private static readonly Expression<Func<Message, MessageDto>> MessageProjection = message => new MessageDto(
        message.Id,
        message.ConversationId,
        message.Seq,
        message.SenderId,
        message.Sender != null ? message.Sender.DisplayName : string.Empty,
        message.SenderRole,
        message.Content,
        message.ClientMessageId,
        message.SentAt,
        message.DeliveredAt,
        message.ReadAt);

    private readonly AppDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TicketsController> _logger;

    public TicketsController(
        AppDbContext db,
        IConfiguration configuration,
        ILogger<TicketsController> logger)
    {
        _db = db;
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
            .Select(BuildTicketProjection(cutoff))
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
            .Select(BuildTicketProjection(cutoff))
            .FirstOrDefaultAsync(ct);
        if (ticket is null)
        {
            return NotFound(new { error = "工单不存在" });
        }

        var messages = await _db.Messages.AsNoTracking()
            .Where(message => message.ConversationId == ticket.ConversationId && message.SentAt >= cutoff)
            .OrderBy(message => message.Seq)
            .Select(MessageProjection)
            .ToListAsync(ct);

        return Ok(new TicketDetailDto(ticket, messages));
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
            .Select(MessageProjection)
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
}
