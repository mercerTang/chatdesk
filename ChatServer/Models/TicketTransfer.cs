namespace ChatServer.Models;

/// <summary>工单转派台账：每次转派一条，记录原客服、目标客服、备注与时间。</summary>
public class TicketTransfer
{
    public int Id { get; set; }

    public int TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    public int FromAgentId { get; set; }
    public Account? FromAgent { get; set; }

    public int ToAgentId { get; set; }
    public Account? ToAgent { get; set; }

    /// <summary>转派备注，可空，最长 200。</summary>
    public string? Note { get; set; }

    /// <summary>强制转派的管理员，可空（客服自助转派为空）。</summary>
    public int? OperatorId { get; set; }
    public Account? Operator { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
