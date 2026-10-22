using ChatServer.Data;
using ChatServer.Dtos;
using ChatServer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatServer.Tests;

/// <summary>可靠性核心：并发序号、幂等重发、afterSeq 增量补拉、关闭后拒收。</summary>
public class MessageReliabilityTests : IClassFixture<ChatServerFactory>
{
    private readonly ChatServerFactory _factory;

    public MessageReliabilityTests(ChatServerFactory factory)
    {
        _factory = factory;
    }

    private async Task<(ConversationDto Conversation, AuthResponse Visitor)> CreateConversationAsync(string agentUsername)
    {
        var agent = await TestApi.LoginAgentAsync(_factory.CreateClient(), agentUsername);
        var visitor = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        var conversation = await TestApi.CreateConversationAsync(_factory.CreateClient(visitor.Token), agent.User.Id);
        return (conversation, visitor);
    }

    private async Task<AppendResult> AppendAsync(int conversationId, int senderId, string content, string? clientMessageId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var chat = scope.ServiceProvider.GetRequiredService<ChatService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sender = await db.Accounts.FirstAsync(a => a.Id == senderId);
        return await chat.AppendMessageAsync(conversationId, sender, content, clientMessageId);
    }

    [Fact]
    public async Task ConcurrentSends_AssignStrictlyIncreasingSeq_WithoutGaps()
    {
        var (conversation, visitor) = await CreateConversationAsync("agent1");

        var results = await Task.WhenAll(
            Enumerable.Range(0, 20)
                .Select(index => Task.Run(() => AppendAsync(conversation.Id, visitor.User.Id, $"并发消息 {index}"))));

        var seqs = results.Select(result => result.Message.Seq).OrderBy(seq => seq).ToList();
        Assert.Equal(Enumerable.Range(1, 20).Select(index => (long)index).ToList(), seqs);
        Assert.All(results, result => Assert.True(result.IsNew));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Messages
            .Where(message => message.ConversationId == conversation.Id)
            .Select(message => message.Seq)
            .ToListAsync();

        Assert.Equal(20, stored.Count);
        Assert.Equal(20, stored.Distinct().Count());

        var storedConversation = await db.Conversations.AsNoTracking().SingleAsync(item => item.Id == conversation.Id);
        Assert.Equal(20, storedConversation.LastMessageSeq);
    }

    [Fact]
    public async Task DuplicateClientMessageId_ReturnsSameMessage_WithoutNewRow()
    {
        var (conversation, visitor) = await CreateConversationAsync("agent2");

        var first = await AppendAsync(conversation.Id, visitor.User.Id, "重复提交的消息", "cmid-fixed-1");
        var second = await AppendAsync(conversation.Id, visitor.User.Id, "重复提交的消息", "cmid-fixed-1");

        Assert.True(first.IsNew);
        Assert.False(second.IsNew);
        Assert.Equal(first.Message.Seq, second.Message.Seq);
        Assert.Equal(first.Message.Id, second.Message.Id);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.Messages.CountAsync(message =>
            message.ConversationId == conversation.Id && message.ClientMessageId == "cmid-fixed-1");

        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task GetMessages_WithAfterSeq_ReturnsOnlyNewerMessages()
    {
        var (conversation, visitor) = await CreateConversationAsync("agent3");

        for (var index = 1; index <= 5; index++)
        {
            await AppendAsync(conversation.Id, visitor.User.Id, $"第 {index} 条");
        }

        var client = _factory.CreateClient(visitor.Token);

        var all = await TestApi.GetMessagesAsync(client, conversation.Id, 0);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, all.Select(message => message.Seq).ToArray());

        var tail = await TestApi.GetMessagesAsync(client, conversation.Id, 3);
        Assert.Equal(new long[] { 4, 5 }, tail.Select(message => message.Seq).ToArray());
        Assert.Equal("第 4 条", tail[0].Content);
    }

    [Fact]
    public async Task ClosedConversation_RejectsNewMessages()
    {
        var (conversation, visitor) = await CreateConversationAsync("agent1");
        await AppendAsync(conversation.Id, visitor.User.Id, "关闭前的消息");

        var closeResponse = await _factory.CreateClient(visitor.Token)
            .PostAsync($"/api/conversations/{conversation.Id}/close", null);
        Assert.True(closeResponse.IsSuccessStatusCode);

        var error = await Assert.ThrowsAsync<ChatException>(
            () => AppendAsync(conversation.Id, visitor.User.Id, "关闭后的消息"));

        Assert.Equal(ChatError.Conflict, error.Error);
    }

    [Fact]
    public async Task AppendMessage_TrimsContent_AndRejectsEmpty()
    {
        var (conversation, visitor) = await CreateConversationAsync("agent2");

        var appended = await AppendAsync(conversation.Id, visitor.User.Id, "  有内容  ");
        Assert.Equal("有内容", appended.Message.Content);

        var blank = await Assert.ThrowsAsync<ChatException>(
            () => AppendAsync(conversation.Id, visitor.User.Id, "   "));
        Assert.Equal(ChatError.BadRequest, blank.Error);
    }
}
