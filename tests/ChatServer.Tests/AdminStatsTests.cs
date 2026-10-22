using System.Net;
using System.Net.Http.Json;
using ChatServer.Data;
using ChatServer.Dtos;
using ChatServer.Models;
using Microsoft.Extensions.DependencyInjection;

namespace ChatServer.Tests;

/// <summary>
/// 数据统计：按日趋势、首响、按客服维度，以及角色鉴权与 days 边界。
/// 统计断言要精确计数，所以每个用例独立宿主与独立临时库，避免用例之间数据串扰。
/// </summary>
public class AdminStatsTests : IDisposable
{
    private readonly ChatServerFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    // ---------------------------------------------------------------- 辅助

    private async Task<HttpClient> AdminClientAsync()
    {
        var admin = await TestApi.LoginAdminAsync(_factory.CreateClient());
        return _factory.CreateClient(admin.Token);
    }

    private async Task<AdminAgentDto> CreateAgentAsync(HttpClient admin)
    {
        var response = await admin.PostAsJsonAsync("/api/admin/agents", new
        {
            username = "stats_agent_" + Guid.NewGuid().ToString("N")[..10],
            displayName = "统计客服" + Guid.NewGuid().ToString("N")[..4],
            password = TestApi.AgentPassword
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AdminAgentDto>())!;
    }

    /// <summary>窗口内的本地时刻：daysAgo 天前的 hour:minute，与服务端同一本地时区。</summary>
    private static DateTimeOffset LocalDay(int daysAgo, int hour, int minute = 0)
    {
        var now = DateTimeOffset.Now;
        var today = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
        return today.AddDays(-daysAgo).AddHours(hour).AddMinutes(minute);
    }

    private static DateOnly LocalDate(int daysAgo) => DateOnly.FromDateTime(LocalDay(daysAgo, 0).DateTime);

    private async Task<int> SeedConversationAsync(
        int visitorId,
        int agentId,
        DateTimeOffset createdAt,
        string status = ConversationStatus.Active)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversation = new Conversation
        {
            VisitorId = visitorId,
            AgentId = agentId,
            Status = status,
            CreatedAt = createdAt,
            LastActivityAt = createdAt,
            ClosedAt = status == ConversationStatus.Closed ? createdAt : null
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();
        return conversation.Id;
    }

    private async Task<int> SeedTicketAsync(
        int conversationId,
        DateTimeOffset createdAt,
        string status = TicketStatus.Open)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ticket = new Ticket
        {
            TicketNo = "T-" + Guid.NewGuid().ToString("N")[..20],
            ConversationId = conversationId,
            Status = status,
            CreatedAt = createdAt,
            ClosedAt = status == TicketStatus.Closed ? createdAt : null
        };
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();
        return ticket.Id;
    }

    private async Task SeedMessageAsync(
        int conversationId,
        int senderId,
        string senderRole,
        long seq,
        DateTimeOffset sentAt)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Messages.Add(new Message
        {
            ConversationId = conversationId,
            Seq = seq,
            SenderId = senderId,
            SenderRole = senderRole,
            Content = "统计用例消息",
            ClientMessageId = Guid.NewGuid().ToString("N"),
            SentAt = sentAt
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedTransferAsync(
        int ticketId,
        int fromAgentId,
        int toAgentId,
        DateTimeOffset createdAt)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.TicketTransfers.Add(new TicketTransfer
        {
            TicketId = ticketId,
            FromAgentId = fromAgentId,
            ToAgentId = toAgentId,
            Note = "统计用例转派",
            CreatedAt = createdAt
        });
        await db.SaveChangesAsync();
    }

    // ---------------------------------------------------------------- 用例

    [Fact]
    public async Task Stats_DailyTrend_CountsPerDayAndKeepsZeroDays()
    {
        var admin = await AdminClientAsync();
        var agent = await CreateAgentAsync(admin);

        var visitorA = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        var visitorB = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        var visitorC = await TestApi.RegisterVisitorAsync(_factory.CreateClient());

        // 3 天前：两个会话，共 3 条消息
        var threeDaysAgo = LocalDay(3, 10);
        var conversationA = await SeedConversationAsync(visitorA.User.Id, agent.Id, threeDaysAgo);
        await SeedMessageAsync(conversationA, visitorA.User.Id, Roles.Visitor, 1, threeDaysAgo.AddMinutes(1));
        await SeedMessageAsync(conversationA, agent.Id, Roles.Agent, 2, threeDaysAgo.AddMinutes(2));

        var conversationB = await SeedConversationAsync(visitorB.User.Id, agent.Id, threeDaysAgo.AddHours(2));
        await SeedMessageAsync(conversationB, visitorB.User.Id, Roles.Visitor, 1, threeDaysAgo.AddHours(2).AddMinutes(1));

        // 4 天前：一个会话 + 一张工单，没有消息
        var fourDaysAgo = LocalDay(4, 9);
        var conversationC = await SeedConversationAsync(visitorC.User.Id, agent.Id, fourDaysAgo);
        await SeedTicketAsync(conversationC, fourDaysAgo);

        var stats = (await admin.GetFromJsonAsync<AdminStatsDto>("/api/admin/stats?days=5"))!;

        // 升序、含首尾日期、无数据日补 0
        Assert.Equal(5, stats.Daily.Count);
        Assert.Equal(LocalDate(4), stats.Daily[0].Date);
        Assert.Equal(LocalDate(0), stats.Daily[^1].Date);
        for (var index = 1; index < stats.Daily.Count; index++)
        {
            Assert.True(stats.Daily[index].Date > stats.Daily[index - 1].Date);
        }

        var thirdDay = stats.Daily.Single(row => row.Date == LocalDate(3));
        Assert.Equal(2, thirdDay.Conversations);
        Assert.Equal(3, thirdDay.Messages);
        Assert.Equal(0, thirdDay.Tickets);

        var fourthDay = stats.Daily.Single(row => row.Date == LocalDate(4));
        Assert.Equal(1, fourthDay.Conversations);
        Assert.Equal(0, fourthDay.Messages);
        Assert.Equal(1, fourthDay.Tickets);

        var emptyDay = stats.Daily.Single(row => row.Date == LocalDate(2));
        Assert.Equal(0, emptyDay.Conversations);
        Assert.Equal(0, emptyDay.Messages);
        Assert.Equal(0, emptyDay.Tickets);
    }

    [Fact]
    public async Task Stats_FirstResponse_AveragesQuestionToReplyGap_AndSkipsUnanswered()
    {
        var admin = await AdminClientAsync();
        var agent = await CreateAgentAsync(admin);
        var baseTime = LocalDay(2, 10);

        // A：访客 10:00 发问，客服 10:15 回复 → 900 秒
        var visitorA = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        var conversationA = await SeedConversationAsync(visitorA.User.Id, agent.Id, baseTime);
        await SeedMessageAsync(conversationA, visitorA.User.Id, Roles.Visitor, 1, baseTime);
        await SeedMessageAsync(conversationA, agent.Id, Roles.Agent, 2, baseTime.AddMinutes(15));

        // B：客服先开口不计，取「首条访客消息之后首条客服消息」→ 60 秒
        var visitorB = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        var conversationB = await SeedConversationAsync(visitorB.User.Id, agent.Id, baseTime);
        await SeedMessageAsync(conversationB, agent.Id, Roles.Agent, 1, baseTime);
        await SeedMessageAsync(conversationB, visitorB.User.Id, Roles.Visitor, 2, baseTime.AddMinutes(5));
        await SeedMessageAsync(conversationB, agent.Id, Roles.Agent, 3, baseTime.AddMinutes(6));

        // C：只有访客消息，没有客服回复 → 不产生样本，也不拉低均值
        var visitorC = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        var conversationC = await SeedConversationAsync(visitorC.User.Id, agent.Id, baseTime);
        await SeedMessageAsync(conversationC, visitorC.User.Id, Roles.Visitor, 1, baseTime);

        var stats = (await admin.GetFromJsonAsync<AdminStatsDto>("/api/admin/stats?days=7"))!;
        var row = stats.Agents.Single(item => item.AgentId == agent.Id);

        Assert.Equal(3, row.ConversationCount);
        Assert.Equal(2, row.FirstResponseSamples);
        Assert.NotNull(row.AvgFirstResponseSeconds);
        Assert.Equal(480, row.AvgFirstResponseSeconds!.Value, 3);

        Assert.Equal(2, stats.Overview.FirstResponseSamples);
        Assert.NotNull(stats.Overview.AvgFirstResponseSeconds);
        Assert.Equal(480, stats.Overview.AvgFirstResponseSeconds!.Value, 3);
    }

    [Fact]
    public async Task Stats_AgentDimension_CountsConversationsMessagesAndActiveLoad()
    {
        var admin = await AdminClientAsync();
        var agentX = await CreateAgentAsync(admin);
        var agentY = await CreateAgentAsync(admin);
        var day = LocalDay(1, 9);

        // X：一活跃一已关两个会话，本人发出 3 条消息
        var visitorX1 = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        var visitorX2 = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        var conversationX1 = await SeedConversationAsync(visitorX1.User.Id, agentX.Id, day);
        var conversationX2 = await SeedConversationAsync(
            visitorX2.User.Id, agentX.Id, day, ConversationStatus.Closed);
        await SeedMessageAsync(conversationX1, visitorX1.User.Id, Roles.Visitor, 1, day.AddMinutes(1));
        await SeedMessageAsync(conversationX1, agentX.Id, Roles.Agent, 2, day.AddMinutes(2));
        await SeedMessageAsync(conversationX1, agentX.Id, Roles.Agent, 3, day.AddMinutes(3));
        await SeedMessageAsync(conversationX2, visitorX2.User.Id, Roles.Visitor, 1, day.AddMinutes(4));
        await SeedMessageAsync(conversationX2, agentX.Id, Roles.Agent, 2, day.AddMinutes(5));

        // Y：一个活跃会话，本人发出 1 条消息
        var visitorY = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        var conversationY = await SeedConversationAsync(visitorY.User.Id, agentY.Id, day);
        await SeedMessageAsync(conversationY, visitorY.User.Id, Roles.Visitor, 1, day.AddMinutes(6));
        await SeedMessageAsync(conversationY, agentY.Id, Roles.Agent, 2, day.AddMinutes(7));

        // 工单与转派台账各一条，用来核对概览的窗口内计数
        var ticket = await SeedTicketAsync(conversationX2, day.AddMinutes(8));
        await SeedTransferAsync(ticket, agentX.Id, agentY.Id, day.AddMinutes(9));

        // 窗口外再放一张进行中工单：进行中工单数是全量存量，不随窗口变化
        var agentZ = await CreateAgentAsync(admin);
        var visitorZ = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        var oldConversation = await SeedConversationAsync(visitorZ.User.Id, agentZ.Id, LocalDay(10, 9));
        await SeedTicketAsync(oldConversation, LocalDay(10, 9));

        var stats = (await admin.GetFromJsonAsync<AdminStatsDto>("/api/admin/stats?days=3"))!;

        var x = stats.Agents.Single(item => item.AgentId == agentX.Id);
        Assert.Equal(2, x.ConversationCount);
        Assert.Equal(3, x.MessageCount);
        Assert.Equal(2, x.FirstResponseSamples);
        Assert.Equal(60, x.AvgFirstResponseSeconds!.Value, 3);
        Assert.Equal(1, x.ActiveConversationCount);

        var y = stats.Agents.Single(item => item.AgentId == agentY.Id);
        Assert.Equal(1, y.ConversationCount);
        Assert.Equal(1, y.MessageCount);
        Assert.Equal(1, y.FirstResponseSamples);
        Assert.Equal(60, y.AvgFirstResponseSeconds!.Value, 3);
        Assert.Equal(1, y.ActiveConversationCount);

        Assert.Equal(3, stats.Overview.ConversationCount);
        Assert.Equal(7, stats.Overview.MessageCount);
        Assert.Equal(1, stats.Overview.TicketCount);
        Assert.Equal(2, stats.Overview.OpenTicketCount);
        Assert.Equal(1, stats.Overview.TransferCount);
    }

    [Fact]
    public async Task Stats_RequiresAdminRole()
    {
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _factory.CreateClient().GetAsync("/api/admin/stats")).StatusCode);

        var visitor = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await _factory.CreateClient(visitor.Token).GetAsync("/api/admin/stats")).StatusCode);

        var agent = await TestApi.LoginAgentAsync(_factory.CreateClient(), "agent1");
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await _factory.CreateClient(agent.Token).GetAsync("/api/admin/stats?days=7")).StatusCode);
    }

    [Fact]
    public async Task Stats_RejectsDaysOutsideRange()
    {
        var admin = await AdminClientAsync();

        foreach (var days in new[] { 0, -1, 91, 1000 })
        {
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (await admin.GetAsync($"/api/admin/stats?days={days}")).StatusCode);
        }

        // 缺省 14 天；边界值 1 与 90 可用
        var defaults = (await admin.GetFromJsonAsync<AdminStatsDto>("/api/admin/stats"))!;
        Assert.Equal(14, defaults.Daily.Count);
        Assert.Equal(LocalDate(13), defaults.Daily[0].Date);
        Assert.Equal(LocalDate(0), defaults.Daily[^1].Date);

        var single = (await admin.GetFromJsonAsync<AdminStatsDto>("/api/admin/stats?days=1"))!;
        Assert.Single(single.Daily);

        var max = (await admin.GetFromJsonAsync<AdminStatsDto>("/api/admin/stats?days=90"))!;
        Assert.Equal(90, max.Daily.Count);
    }
}
