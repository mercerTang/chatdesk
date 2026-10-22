using ChatServer.Data;
using ChatServer.Models;
using ChatServer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatServer.Tests;

/// <summary>数据保留：聊天记录自发送起 30 天后逐条清理，工单与会话保留。</summary>
public class RetentionServiceTests : IClassFixture<ChatServerFactory>
{
    private readonly ChatServerFactory _factory;

    public RetentionServiceTests(ChatServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Purge_RemovesMessagesOlderThanRetention_AndKeepsTicket()
    {
        var agent = await TestApi.LoginAgentAsync(_factory.CreateClient(), "agent3");
        var visitor = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        var conversation = await TestApi.CreateConversationAsync(
            _factory.CreateClient(visitor.Token),
            agent.User.Id);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tracked = await db.Conversations.FirstAsync(item => item.Id == conversation.Id);
            var now = DateTimeOffset.UtcNow;
            db.Messages.AddRange(
                new Message
                {
                    ConversationId = tracked.Id,
                    Seq = 1,
                    SenderId = visitor.User.Id,
                    SenderRole = Roles.Visitor,
                    Content = "31 天前的消息",
                    SentAt = now.AddDays(-31)
                },
                new Message
                {
                    ConversationId = tracked.Id,
                    Seq = 2,
                    SenderId = agent.User.Id,
                    SenderRole = Roles.Agent,
                    Content = "今天的新消息",
                    SentAt = now
                });
            tracked.LastMessageSeq = 2;
            await db.SaveChangesAsync();
        }

        int removed;
        using (var scope = _factory.Services.CreateScope())
        {
            var retention = scope.ServiceProvider.GetRequiredService<RetentionService>();
            removed = await retention.PurgeExpiredAsync(DateTimeOffset.UtcNow, 30);
        }

        Assert.True(removed >= 1);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var remaining = await db.Messages
                .Where(item => item.ConversationId == conversation.Id)
                .OrderBy(item => item.Seq)
                .ToListAsync();

            // 只删过期的那条，今天的消息、会话与工单都保留
            var message = Assert.Single(remaining);
            Assert.Equal(2, message.Seq);
            Assert.Equal("今天的新消息", message.Content);
            Assert.True(await db.Conversations.AnyAsync(item => item.Id == conversation.Id));
            Assert.True(await db.Tickets.AnyAsync(item => item.ConversationId == conversation.Id));
        }
    }
}
