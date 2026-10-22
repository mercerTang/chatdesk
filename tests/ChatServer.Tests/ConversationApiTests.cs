using System.Net;
using System.Net.Http.Json;
using ChatServer.Data;
using ChatServer.Dtos;
using ChatServer.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatServer.Tests;

/// <summary>会话创建、复用、自动分配、并发上限、越权访问与关闭。</summary>
public class ConversationApiTests : IClassFixture<ChatServerFactory>
{
    private readonly ChatServerFactory _factory;

    public ConversationApiTests(ChatServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Create_ReturnsActiveConversation_AndReusesForSameVisitor()
    {
        var agent = await TestApi.LoginAgentAsync(_factory.CreateClient(), "agent1");
        var visitor = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        var client = _factory.CreateClient(visitor.Token);

        var first = await TestApi.CreateConversationAsync(client, agent.User.Id);
        Assert.Equal(ConversationStatus.Active, first.Status);
        Assert.Equal(agent.User.Id, first.AgentId);
        Assert.Equal(visitor.User.Id, first.VisitorId);
        Assert.Equal(0, first.LastMessageSeq);

        var secondResponse = await client.PostAsJsonAsync("/api/conversations", new { agentId = agent.User.Id });
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var second = (await secondResponse.Content.ReadFromJsonAsync<ConversationDto>())!;

        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task Create_WithoutAgentId_AutoAssignsAnAgentWithCapacity()
    {
        // 自动分配只挑选在线客服；正常流程由 SignalR 连接维护在线标记，这里直接置位
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Accounts.Where(a => a.Role == Roles.Agent)
                .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.IsOnline, true));
        }

        var visitor = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        var client = _factory.CreateClient(visitor.Token);

        var conversation = await TestApi.CreateConversationAsync(client, null);

        Assert.Equal(ConversationStatus.Active, conversation.Status);
        var agents = (await client.GetFromJsonAsync<List<AgentDto>>("/api/agents"))!;
        Assert.Contains(agents, agent => agent.Id == conversation.AgentId);
        Assert.False(string.IsNullOrWhiteSpace(conversation.AgentName));
    }

    [Fact]
    public async Task AgentCapacity_Returns409_WhenAllSlotsTaken()
    {
        var agent = await TestApi.LoginAgentAsync(_factory.CreateClient(), "agent2");
        var agentList = (await _factory.CreateClient(agent.Token).GetFromJsonAsync<List<AgentDto>>("/api/agents"))!;
        Assert.Equal(5, agentList.Single(item => item.Id == agent.User.Id).MaxConcurrentConversations);

        for (var i = 0; i < 5; i++)
        {
            var visitor = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
            var client = _factory.CreateClient(visitor.Token);
            var created = await client.PostAsJsonAsync("/api/conversations", new { agentId = agent.User.Id });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        var overflowVisitor = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        var blocked = await _factory.CreateClient(overflowVisitor.Token)
            .PostAsJsonAsync("/api/conversations", new { agentId = agent.User.Id });

        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        var body = await blocked.Content.ReadAsStringAsync();
        Assert.Contains("会话已满", body);
    }

    [Fact]
    public async Task Visitor_CannotAccessAnotherVisitorsConversation_Returns403()
    {
        var agent = await TestApi.LoginAgentAsync(_factory.CreateClient(), "agent3");
        var owner = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        var conversation = await TestApi.CreateConversationAsync(_factory.CreateClient(owner.Token), agent.User.Id);

        var stranger = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        var strangerClient = _factory.CreateClient(stranger.Token);

        Assert.Equal(HttpStatusCode.Forbidden, (await strangerClient.GetAsync($"/api/conversations/{conversation.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await strangerClient.GetAsync($"/api/conversations/{conversation.Id}/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await strangerClient.PostAsync($"/api/conversations/{conversation.Id}/close", null)).StatusCode);
    }

    [Fact]
    public async Task Close_SetsStatusClosed()
    {
        var agent = await TestApi.LoginAgentAsync(_factory.CreateClient(), "agent1");
        var visitor = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        var client = _factory.CreateClient(visitor.Token);
        var conversation = await TestApi.CreateConversationAsync(client, agent.User.Id);

        var response = await client.PostAsync($"/api/conversations/{conversation.Id}/close", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var closed = (await response.Content.ReadFromJsonAsync<ConversationDto>())!;
        Assert.Equal(ConversationStatus.Closed, closed.Status);

        var detail = await client.GetFromJsonAsync<ConversationDto>($"/api/conversations/{conversation.Id}");
        Assert.Equal(ConversationStatus.Closed, detail!.Status);
    }

    [Fact]
    public async Task Mine_ReturnsOwnConversationsOnly()
    {
        var agent = await TestApi.LoginAgentAsync(_factory.CreateClient(), "agent3");
        var visitor = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        var conversation = await TestApi.CreateConversationAsync(_factory.CreateClient(visitor.Token), agent.User.Id);

        var visitorMine = (await _factory.CreateClient(visitor.Token)
            .GetFromJsonAsync<List<ConversationDto>>("/api/conversations/mine"))!;
        Assert.Contains(visitorMine, item => item.Id == conversation.Id);

        var stranger = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        var strangerMine = (await _factory.CreateClient(stranger.Token)
            .GetFromJsonAsync<List<ConversationDto>>("/api/conversations/mine"))!;
        Assert.DoesNotContain(strangerMine, item => item.Id == conversation.Id);
    }

    [Fact]
    public async Task Create_WithAnotherAgent_WhileActive_Returns409_AndAllowsAfterClose()
    {
        var agent1 = await TestApi.LoginAgentAsync(_factory.CreateClient(), "agent1");
        var agent3 = await TestApi.LoginAgentAsync(_factory.CreateClient(), "agent3");
        var visitor = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        var client = _factory.CreateClient(visitor.Token);

        var conversation = await TestApi.CreateConversationAsync(client, agent1.User.Id);

        // 访客已有进行中的会话时，不允许同时联系另一个客服
        var blocked = await client.PostAsJsonAsync("/api/conversations", new { agentId = agent3.User.Id });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);

        var closed = await client.PostAsync($"/api/conversations/{conversation.Id}/close", null);
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);

        // 结束当前会话后可以联系其他客服
        var allowed = await client.PostAsJsonAsync("/api/conversations", new { agentId = agent3.User.Id });
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
    }

    [Fact]
    public async Task Mine_ForAgent_ListsOneRowPerVisitor()
    {
        var agent = await TestApi.LoginAgentAsync(_factory.CreateClient(), "agent3");
        var visitor = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        var client = _factory.CreateClient(visitor.Token);

        var first = await TestApi.CreateConversationAsync(client, agent.User.Id);
        await client.PostAsync($"/api/conversations/{first.Id}/close", null);
        var second = await TestApi.CreateConversationAsync(client, agent.User.Id);

        var mine = (await _factory.CreateClient(agent.Token)
            .GetFromJsonAsync<List<ConversationDto>>("/api/conversations/mine"))!;
        var rows = mine.Where(item => item.VisitorId == visitor.User.Id).ToList();

        // 同一访客只出现一行，且优先进行中的会话
        Assert.Single(rows);
        Assert.Equal(second.Id, rows[0].Id);
        Assert.Equal(ConversationStatus.Active, rows[0].Status);
    }


}
