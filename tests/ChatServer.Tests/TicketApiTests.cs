using System.Net;
using System.Net.Http.Json;
using ChatServer.Data;
using ChatServer.Dtos;
using ChatServer.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatServer.Tests;

/// <summary>工单：随会话自动创建、关闭同步、记录可查、可导出、按客服隔离。</summary>
public class TicketApiTests : IClassFixture<ChatServerFactory>
{
    private readonly ChatServerFactory _factory;

    public TicketApiTests(ChatServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Conversation_CreatesTicket_AndCloseUpdatesStatus()
    {
        var agent = await TestApi.LoginAgentAsync(_factory.CreateClient(), "agent1");
        var visitor = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        var visitorClient = _factory.CreateClient(visitor.Token);
        var conversation = await TestApi.CreateConversationAsync(visitorClient, agent.User.Id);

        var agentClient = _factory.CreateClient(agent.Token);
        var tickets = (await agentClient.GetFromJsonAsync<List<TicketDto>>("/api/tickets"))!;
        var ticket = Assert.Single(tickets.Where(item => item.ConversationId == conversation.Id));
        Assert.StartsWith("T-", ticket.TicketNo);
        Assert.Equal(TicketStatus.Open, ticket.Status);

        var closeResponse = await visitorClient.PostAsync($"/api/conversations/{conversation.Id}/close", null);
        Assert.Equal(HttpStatusCode.OK, closeResponse.StatusCode);

        var afterClose = (await agentClient.GetFromJsonAsync<List<TicketDto>>("/api/tickets"))!;
        var closed = Assert.Single(afterClose.Where(item => item.ConversationId == conversation.Id));
        Assert.Equal(TicketStatus.Closed, closed.Status);
        Assert.NotNull(closed.ClosedAt);
    }

    [Fact]
    public async Task Detail_ReturnsChronologicalMessages_AndExportContainsTranscript()
    {
        var agent = await TestApi.LoginAgentAsync(_factory.CreateClient(), "agent2");
        var visitor = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        var conversation = await TestApi.CreateConversationAsync(_factory.CreateClient(visitor.Token), agent.User.Id);

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
                    Content = "第一条咨询",
                    ClientMessageId = "ticket-test-1",
                    SentAt = now
                },
                new Message
                {
                    ConversationId = tracked.Id,
                    Seq = 2,
                    SenderId = agent.User.Id,
                    SenderRole = Roles.Agent,
                    Content = "第一条回复",
                    ClientMessageId = "ticket-test-2",
                    SentAt = now.AddSeconds(1)
                });
            tracked.LastMessageSeq = 2;
            await db.SaveChangesAsync();
        }

        var agentClient = _factory.CreateClient(agent.Token);
        var tickets = (await agentClient.GetFromJsonAsync<List<TicketDto>>("/api/tickets"))!;
        var ticket = tickets.Single(item => item.ConversationId == conversation.Id);

        var detail = await agentClient.GetFromJsonAsync<TicketDetailDto>($"/api/tickets/{ticket.Id}");
        Assert.NotNull(detail);
        Assert.Equal(2, detail!.Messages.Count);
        Assert.Equal(1, detail.Messages[0].Seq);
        Assert.Equal("第一条咨询", detail.Messages[0].Content);
        Assert.Equal(2, detail.Messages[1].Seq);

        var textResponse = await agentClient.GetAsync($"/api/tickets/{ticket.Id}/export?format=txt");
        Assert.Equal(HttpStatusCode.OK, textResponse.StatusCode);
        Assert.Equal("text/plain", textResponse.Content.Headers.ContentType?.MediaType);
        var text = await textResponse.Content.ReadAsStringAsync();
        Assert.Contains("工单号", text);
        Assert.Contains("第一条咨询", text);
        Assert.Contains("第一条回复", text);

        var csvResponse = await agentClient.GetAsync($"/api/tickets/{ticket.Id}/export?format=csv");
        Assert.Equal(HttpStatusCode.OK, csvResponse.StatusCode);
        Assert.Equal("text/csv", csvResponse.Content.Headers.ContentType?.MediaType);
        var csv = await csvResponse.Content.ReadAsStringAsync();
        Assert.Contains("工单号,时间,角色,发送方,内容", csv);
        Assert.Contains("第一条回复", csv);
    }

    [Fact]
    public async Task Tickets_AreScopedToAgent_AndVisitorForbidden()
    {
        var agent1 = await TestApi.LoginAgentAsync(_factory.CreateClient(), "agent1");
        var agent2 = await TestApi.LoginAgentAsync(_factory.CreateClient(), "agent2");
        var visitor = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        var visitorClient = _factory.CreateClient(visitor.Token);
        var conversation = await TestApi.CreateConversationAsync(visitorClient, agent1.User.Id);

        var otherAgentTickets =
            (await _factory.CreateClient(agent2.Token).GetFromJsonAsync<List<TicketDto>>("/api/tickets"))!;
        Assert.DoesNotContain(otherAgentTickets, item => item.ConversationId == conversation.Id);

        var visitorResponse = await visitorClient.GetAsync("/api/tickets");
        Assert.Equal(HttpStatusCode.Forbidden, visitorResponse.StatusCode);
    }
}
