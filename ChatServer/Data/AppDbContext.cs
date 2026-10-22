using ChatServer.Models;
using Microsoft.EntityFrameworkCore;

namespace ChatServer.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Account> Accounts { get; set; }
    public DbSet<Conversation> Conversations { get; set; }
    public DbSet<Message> Messages { get; set; }
    public DbSet<Ticket> Tickets { get; set; }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite 不支持 DateTimeOffset 的 ORDER BY，统一转 Unix 毫秒存储
        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<DateTimeOffsetToUnixMillisecondsConverter>();
        configurationBuilder.Properties<DateTimeOffset?>()
            .HaveConversion<NullableDateTimeOffsetToUnixMillisecondsConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Account>(entity =>
        {
            entity.HasIndex(a => a.Username).IsUnique();
            entity.Property(a => a.Username).HasMaxLength(64).IsRequired();
            entity.Property(a => a.DisplayName).HasMaxLength(64).IsRequired();
            entity.Property(a => a.Role).HasMaxLength(16).IsRequired();
            entity.Property(a => a.PasswordHash).HasMaxLength(256).IsRequired();
            entity.HasIndex(a => a.Role);
        });

        modelBuilder.Entity<Conversation>(entity =>
        {
            entity.HasOne(c => c.Visitor)
                .WithMany()
                .HasForeignKey(c => c.VisitorId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(c => c.Agent)
                .WithMany()
                .HasForeignKey(c => c.AgentId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.Property(c => c.Status).HasMaxLength(16).IsRequired();

            // 同一访客与同一客服同时只允许存在一个进行中的会话（创建接口的幂等兜底）
            entity.HasIndex(c => new { c.VisitorId, c.AgentId })
                .IsUnique()
                .HasFilter("\"Status\" = 'active'");

            // 访客同一时间只允许一个进行中的会话
            entity.HasIndex(c => c.VisitorId)
                .IsUnique()
                .HasFilter("\"Status\" = 'active'");

            entity.HasIndex(c => new { c.AgentId, c.Status });
            entity.HasIndex(c => c.LastActivityAt);
        });

        modelBuilder.Entity<Message>(entity =>
        {
            entity.HasOne(m => m.Conversation)
                .WithMany(c => c.Messages)
                .HasForeignKey(m => m.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(m => m.Sender)
                .WithMany()
                .HasForeignKey(m => m.SenderId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.Property(m => m.Content).HasMaxLength(2000).IsRequired();
            entity.Property(m => m.SenderRole).HasMaxLength(16).IsRequired();
            entity.Property(m => m.ClientMessageId).HasMaxLength(100);

            // 服务端权威序号：会话内唯一，冲突时上层重试
            entity.HasIndex(m => new { m.ConversationId, m.Seq }).IsUnique();

            // 幂等兜底：同一会话内同一 ClientMessageId 只落一条（非空时）
            entity.HasIndex(m => new { m.ConversationId, m.ClientMessageId })
                .IsUnique()
                .HasFilter("\"ClientMessageId\" IS NOT NULL");
        });

        modelBuilder.Entity<Ticket>(entity =>
        {
            entity.HasOne(t => t.Conversation)
                .WithOne()
                .HasForeignKey<Ticket>(t => t.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(t => t.TicketNo).HasMaxLength(32).IsRequired();
            entity.Property(t => t.Status).HasMaxLength(16).IsRequired();

            entity.HasIndex(t => t.TicketNo).IsUnique();
            entity.HasIndex(t => t.ConversationId).IsUnique();
            entity.HasIndex(t => t.CreatedAt);
        });
    }
}
