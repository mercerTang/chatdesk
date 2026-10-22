using System.Data;
using ChatServer.Data;
using ChatServer.Models;
using Microsoft.EntityFrameworkCore;

namespace ChatServer.Services;

/// <summary>建库、开 WAL、补列、写种子账号、复位在线标记。</summary>
public static class DatabaseInitializer
{
    public const string SeedPassword = "Agent@123456";
    public const string AdminUsername = "admin";
    public const string AdminPassword = "Admin@123456";

    public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var presence = scope.ServiceProvider.GetRequiredService<AgentPresenceService>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseInitializer");

        await db.Database.EnsureCreatedAsync(ct);

        // SQLite 并发读写下 WAL 更稳；等锁超时由连接串的 Default Timeout 控制（见 Program.cs）
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);

        // 兼容已有库：EnsureCreated 不会给已存在的表补列，按 PRAGMA table_info 检测后补列
        await EnsureColumnAsync(
            db, "Accounts", "IsActive", "\"IsActive\" INTEGER NOT NULL DEFAULT 1", ct);

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

        // 种子管理员：幂等，已存在（哪怕同名访客）就不写
        if (!await db.Accounts.AnyAsync(a => a.Username == AdminUsername, ct))
        {
            db.Accounts.Add(new Account
            {
                Username = AdminUsername,
                DisplayName = "管理员",
                PasswordHash = PasswordHasher.Hash(AdminPassword),
                Role = Roles.Admin
            });
            await db.SaveChangesAsync(ct);
            logger.LogInformation("已写入种子管理员账号：{Username}", AdminUsername);
        }
        else if (!await db.Accounts.AnyAsync(a => a.Username == AdminUsername && a.Role == Roles.Admin, ct))
        {
            // 同名账号被访客占用时管理后台没有入口：必须让运维在升级时看见
            logger.LogError("用户名 {Username} 已被非管理员账号占用，管理后台没有可用管理员", AdminUsername);
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

        // 兼容已有库：补建工单转派台账表（与 Tickets 表同一启动补建范式）
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "TicketTransfers" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_TicketTransfers" PRIMARY KEY AUTOINCREMENT,
                "TicketId" INTEGER NOT NULL,
                "FromAgentId" INTEGER NOT NULL,
                "ToAgentId" INTEGER NOT NULL,
                "Note" TEXT NULL,
                "OperatorId" INTEGER NULL,
                "CreatedAt" INTEGER NOT NULL,
                CONSTRAINT "FK_TicketTransfers_Tickets_TicketId"
                    FOREIGN KEY ("TicketId") REFERENCES "Tickets" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_TicketTransfers_Accounts_FromAgentId"
                    FOREIGN KEY ("FromAgentId") REFERENCES "Accounts" ("Id") ON DELETE RESTRICT,
                CONSTRAINT "FK_TicketTransfers_Accounts_ToAgentId"
                    FOREIGN KEY ("ToAgentId") REFERENCES "Accounts" ("Id") ON DELETE RESTRICT,
                CONSTRAINT "FK_TicketTransfers_Accounts_OperatorId"
                    FOREIGN KEY ("OperatorId") REFERENCES "Accounts" ("Id") ON DELETE RESTRICT
            );
            """,
            ct);

        // 兼容 b002 已建好的台账表：补列 OperatorId（强制转派的管理员）
        await EnsureColumnAsync(
            db,
            "TicketTransfers",
            "OperatorId",
            "\"OperatorId\" INTEGER NULL REFERENCES \"Accounts\" (\"Id\")",
            ct);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_TicketTransfers_TicketId_CreatedAt\" "
            + "ON \"TicketTransfers\" (\"TicketId\", \"CreatedAt\");",
            ct);

        // 兼容已有库：补建上传文件表（与上面同一范式）
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "FileAssets" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_FileAssets" PRIMARY KEY AUTOINCREMENT,
                "StoredName" TEXT NOT NULL,
                "OriginalName" TEXT NOT NULL,
                "ContentType" TEXT NOT NULL,
                "Size" INTEGER NOT NULL,
                "UploaderId" INTEGER NOT NULL,
                "CreatedAt" INTEGER NOT NULL,
                CONSTRAINT "FK_FileAssets_Accounts_UploaderId"
                    FOREIGN KEY ("UploaderId") REFERENCES "Accounts" ("Id") ON DELETE CASCADE
            );
            """,
            ct);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_FileAssets_StoredName\" ON \"FileAssets\" (\"StoredName\");",
            ct);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_FileAssets_CreatedAt\" ON \"FileAssets\" (\"CreatedAt\");",
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

    /// <summary>确保表上有指定列；没有则 ALTER TABLE 补上（SQLite 不支持 IF NOT EXISTS 加列）。</summary>
    private static async Task EnsureColumnAsync(
        AppDbContext db,
        string table,
        string column,
        string definition,
        CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            // PRAGMA table_info 的第 2 列（下标 1）是列名
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        await reader.CloseAsync();

        // 表名/列定义都来自本类的固定常量，不拼入外部输入
        var sql = $"ALTER TABLE \"{table}\" ADD COLUMN {definition};";
        await db.Database.ExecuteSqlRawAsync(sql, ct);
    }
}
