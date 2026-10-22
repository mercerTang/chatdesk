using ChatServer.Data;
using Microsoft.EntityFrameworkCore;

namespace ChatServer.Services;

/// <summary>数据保留策略：聊天记录自发送起保留 30 天，到期逐条删除；工单本身保留。</summary>
public class RetentionService
{
    public const int DefaultRetentionDays = 30;

    /// <summary>按保留天数计算截止时间；小于等于 0 时使用默认 30 天。</summary>
    public static DateTimeOffset Cutoff(DateTimeOffset now, int retentionDays)
        => now.AddDays(-(retentionDays <= 0 ? DefaultRetentionDays : retentionDays));

    private readonly AppDbContext _db;
    private readonly ILogger<RetentionService> _logger;

    public RetentionService(AppDbContext db, ILogger<RetentionService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>删除发送时间超过保留期的聊天记录，返回删除条数。</summary>
    public async Task<int> PurgeExpiredAsync(
        DateTimeOffset now,
        int retentionDays,
        CancellationToken ct = default)
    {
        var days = retentionDays <= 0 ? DefaultRetentionDays : retentionDays;
        var cutoff = Cutoff(now, days);

        var removed = await _db.Messages
            .Where(message => message.SentAt < cutoff)
            .ExecuteDeleteAsync(ct);

        if (removed > 0)
        {
            _logger.LogInformation(
                "清理过期聊天记录 {Count} 条（自发送起保留 {Days} 天，截止 {Cutoff:O}）",
                removed,
                days,
                cutoff);
        }

        return removed;
    }
}
