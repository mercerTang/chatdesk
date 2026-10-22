using ChatServer.Data;
using ChatServer.Models;
using Microsoft.EntityFrameworkCore;

namespace ChatServer.Services;

/// <summary>建库、开 WAL、写种子客服、复位在线标记。</summary>
public static class DatabaseInitializer
{
    public const string SeedPassword = "Agent@123456";

    public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var presence = scope.ServiceProvider.GetRequiredService<AgentPresenceService>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseInitializer");

        await db.Database.EnsureCreatedAsync(ct);

        // SQLite 并发读写下 WAL 更稳；等锁超时由连接串的 Default Timeout 控制（见 Program.cs）
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);

        // 种子幂等：只有在一个客服账号都没有时才插入
        if (!await db.Accounts.AnyAsync(a => a.Role == Roles.Agent, ct))
        {
            db.Accounts.AddRange(
                CreateAgent("agent1", "Mia"),
                CreateAgent("agent2", "Leo"),
                CreateAgent("agent3", "Iris"));
            await db.SaveChangesAsync(ct);
            logger.LogInformation("已写入种子客服账号：agent1 / agent2 / agent3");
        }

        // 旧版本种子使用中文展示名；启动时一次性迁移为英文名（仅当仍是旧名时，不覆盖自定义）
        var legacyNames = new (string Username, string OldName, string NewName)[]
        {
            ("agent1", "客服小美", "Mia"),
            ("agent2", "客服小张", "Leo"),
            ("agent3", "客服小李", "Iris")
        };
        foreach (var item in legacyNames)
        {
            await db.Accounts
                .Where(a => a.Username == item.Username && a.DisplayName == item.OldName)
                .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.DisplayName, item.NewName), ct);
        }

        // 兼容已有库：EnsureCreated 不会给已存在的库补新表，这里补建工单表
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "Tickets" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Tickets" PRIMARY KEY AUTOINCREMENT,
                "TicketNo" TEXT NOT NULL,
                "ConversationId" INTEGER NOT NULL,
                "Status" TEXT NOT NULL,
                "CreatedAt" INTEGER NOT NULL,
                "ClosedAt" INTEGER NULL,
                CONSTRAINT "FK_Tickets_Conversations_ConversationId"
                    FOREIGN KEY ("ConversationId") REFERENCES "Conversations" ("Id") ON DELETE CASCADE
            );
            """,
            ct);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Tickets_TicketNo\" ON \"Tickets\" (\"TicketNo\");",
            ct);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Tickets_ConversationId\" ON \"Tickets\" (\"ConversationId\");",
            ct);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_Tickets_CreatedAt\" ON \"Tickets\" (\"CreatedAt\");",
            ct);

        // 历史会话补建工单，保证客服后台能查到全部聊天记录
        var conversationsWithoutTicket = await db.Conversations
            .Where(c => !db.Tickets.Any(t => t.ConversationId == c.Id))
            .ToListAsync(ct);
        if (conversationsWithoutTicket.Count > 0)
        {
            foreach (var conversation in conversationsWithoutTicket)
            {
                db.Tickets.Add(new Ticket
                {
                    TicketNo = $"T-{conversation.CreatedAt:yyyyMMdd}-{conversation.Id:D5}",
                    ConversationId = conversation.Id,
                    Status = conversation.Status == ConversationStatus.Active
                        ? TicketStatus.Open
                        : TicketStatus.Closed,
                    CreatedAt = conversation.CreatedAt,
                    ClosedAt = conversation.ClosedAt
                });
            }

            await db.SaveChangesAsync(ct);
            logger.LogInformation("已补建 {Count} 条历史工单", conversationsWithoutTicket.Count);
        }

        // 进程重启后不存在遗留连接，在线标记归零
        presence.Reset();
        await db.Accounts
            .Where(a => a.IsOnline)
            .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.IsOnline, false), ct);

        logger.LogInformation("数据库初始化完成：{DbPath}", db.Database.GetDbConnection().DataSource);
    }

    private static Account CreateAgent(string username, string displayName) => new()
    {
        Username = username,
        DisplayName = displayName,
        PasswordHash = PasswordHasher.Hash(SeedPassword),
        Role = Roles.Agent,
        MaxConcurrentConversations = 5
    };
}
