using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatServer.Data;
using ChatServer.Dtos;
using ChatServer.Models;
using ChatServer.Services;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatServer.Tests;

/// <summary>工单转派：归属迁移与台账、拒绝路径、目标容量、实时组员迁移。</summary>
public class TicketTransferTests : IClassFixture<ChatServerFactory>
{
    private readonly ChatServerFactory _factory;

    public TicketTransferTests(ChatServerFactory factory)
    {
        _factory = factory;
    }

    private sealed record TransferSetup(
        AuthResponse Source,
        AuthResponse Target,
        AuthResponse Visitor,
        ConversationDto Conversation,
        TicketDto Ticket);

    private async Task<HubConnection> ConnectAsync(string token)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "/hubs/chat"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                // TestServer 不支持 WebSocket，改用长轮询
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        await connection.StartAsync();
        return connection;
    }

    /// <summary>插入一个独立客服账号，隔离各用例的归属与并发容量。</summary>
    private async Task<AuthResponse> CreateAgentAsync(string displayName, int maxConcurrent = 5)
    {
        var username = "transfer_agent_" + Guid.NewGuid().ToString("N")[..12];
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Accounts.Add(new Account
            {
                Username = username,
                PasswordHash = PasswordHasher.Hash(TestApi.AgentPassword),
                DisplayName = displayName,
                Role = Roles.Agent,
                MaxConcurrentConversations = maxConcurrent
            });
            await db.SaveChangesAsync();
        }

        return await TestApi.LoginAgentAsync(_factory.CreateClient(), username);
    }

    private async Task<TicketDto> GetTicketAsync(HttpClient agentClient, int conversationId)
    {
        var tickets = (await agentClient.GetFromJsonAsync<List<TicketDto>>("/api/tickets"))!;
        return tickets.Single(item => item.ConversationId == conversationId);
    }

    private Task<HttpResponseMessage> TransferAsync(
        HttpClient client, int ticketId, int agentId, string? note = null)
        => client.PostAsJsonAsync($"/api/tickets/{ticketId}/transfer", new { agentId, note });

    private async Task<TransferSetup> CreateTransferSetupAsync(
        string sourceName = "源客服",
        string targetName = "目标客服",
        int targetMax = 5)
    {
        var source = await CreateAgentAsync(sourceName);
        var target = await CreateAgentAsync(targetName, targetMax);
        var visitor = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        var conversation = await TestApi.CreateConversationAsync(
            _factory.CreateClient(visitor.Token), source.User.Id);
        var ticket = await GetTicketAsync(_factory.CreateClient(source.Token), conversation.Id);
        return new TransferSetup(source, target, visitor, conversation, ticket);
    }

    [Fact]
    public async Task Transfer_MigratesOwnership_AndWritesLedger()
    {
        var setup = await CreateTransferSetupAsync("源客服甲", "目标客服乙");
        var sourceClient = _factory.CreateClient(setup.Source.Token);
        var targetClient = _factory.CreateClient(setup.Target.Token);

        var response = await TransferAsync(
            sourceClient, setup.Ticket.Id, setup.Target.User.Id, "  我先下班，请接手  ");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var updated = (await response.Content.ReadFromJsonAsync<TicketDto>())!;
        Assert.Equal(setup.Target.User.DisplayName, updated.AgentName);

        // 原客服：列表不再包含，详情 404
        var sourceTickets = (await sourceClient.GetFromJsonAsync<List<TicketDto>>("/api/tickets"))!;
        Assert.DoesNotContain(sourceTickets, item => item.Id == setup.Ticket.Id);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await sourceClient.GetAsync($"/api/tickets/{setup.Ticket.Id}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await sourceClient.GetAsync($"/api/tickets/{setup.Ticket.Id}/export?format=txt")).StatusCode);

        // 目标客服：列表出现，详情可读
        var targetTickets = (await targetClient.GetFromJsonAsync<List<TicketDto>>("/api/tickets"))!;
        Assert.Contains(targetTickets, item => item.Id == setup.Ticket.Id);
        var detail = (await targetClient.GetFromJsonAsync<TicketDetailDto>($"/api/tickets/{setup.Ticket.Id}"))!;
        Assert.Equal(setup.Target.User.DisplayName, detail.Ticket.AgentName);

        var transfer = Assert.Single(detail.Transfers);
        Assert.Equal(setup.Source.User.Id, transfer.FromAgentId);
        Assert.Equal(setup.Source.User.DisplayName, transfer.FromAgentName);
        Assert.Equal(setup.Target.User.Id, transfer.ToAgentId);
        Assert.Equal(setup.Target.User.DisplayName, transfer.ToAgentName);
        Assert.Equal("我先下班，请接手", transfer.Note);

        // 归属落在会话上，台账落在 TicketTransfers
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversation = await db.Conversations.AsNoTracking()
            .SingleAsync(item => item.Id == setup.Conversation.Id);
        Assert.Equal(setup.Target.User.Id, conversation.AgentId);

        var row = await db.TicketTransfers.AsNoTracking()
            .SingleAsync(item => item.TicketId == setup.Ticket.Id);
        Assert.Equal(setup.Source.User.Id, row.FromAgentId);
        Assert.Equal(setup.Target.User.Id, row.ToAgentId);
        Assert.Equal("我先下班，请接手", row.Note);
    }

    [Fact]
    public async Task Transfer_ByNonOwner_Returns404_AndKeepsTicket()
    {
        var setup = await CreateTransferSetupAsync("工单归属客服", "另一名客服");
        var stranger = await CreateAgentAsync("非本人客服");

        var response = await TransferAsync(
            _factory.CreateClient(stranger.Token), setup.Ticket.Id, setup.Target.User.Id);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("工单不存在", await response.Content.ReadAsStringAsync());

        var ownerTickets = (await _factory.CreateClient(setup.Source.Token)
            .GetFromJsonAsync<List<TicketDto>>("/api/tickets"))!;
        Assert.Contains(ownerTickets, item => item.Id == setup.Ticket.Id);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversation = await db.Conversations.AsNoTracking()
            .SingleAsync(item => item.Id == setup.Conversation.Id);
        Assert.Equal(setup.Source.User.Id, conversation.AgentId);
        Assert.False(await db.TicketTransfers.AnyAsync(item => item.TicketId == setup.Ticket.Id));
    }

    [Fact]
    public async Task Transfer_ByVisitor_Returns403()
    {
        var setup = await CreateTransferSetupAsync();

        var response = await TransferAsync(
            _factory.CreateClient(setup.Visitor.Token), setup.Ticket.Id, setup.Target.User.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Transfer_ToSelf_Returns400()
    {
        var setup = await CreateTransferSetupAsync();

        var response = await TransferAsync(
            _factory.CreateClient(setup.Source.Token), setup.Ticket.Id, setup.Source.User.Id);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("不能转派给自己", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Transfer_ClosedTicket_Returns409()
    {
        var setup = await CreateTransferSetupAsync();
        var visitorClient = _factory.CreateClient(setup.Visitor.Token);
        var closed = await visitorClient.PostAsync(
            $"/api/conversations/{setup.Conversation.Id}/close", null);
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);

        var response = await TransferAsync(
            _factory.CreateClient(setup.Source.Token), setup.Ticket.Id, setup.Target.User.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("工单已结束", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Transfer_ToMissingOrVisitorTarget_Returns404()
    {
        var setup = await CreateTransferSetupAsync();
        var sourceClient = _factory.CreateClient(setup.Source.Token);

        var missing = await TransferAsync(sourceClient, setup.Ticket.Id, 999_999);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("客服不存在", await missing.Content.ReadAsStringAsync());

        var visitorTarget = await TransferAsync(sourceClient, setup.Ticket.Id, setup.Visitor.User.Id);
        Assert.Equal(HttpStatusCode.NotFound, visitorTarget.StatusCode);
        Assert.Contains("客服不存在", await visitorTarget.Content.ReadAsStringAsync());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversation = await db.Conversations.AsNoTracking()
            .SingleAsync(item => item.Id == setup.Conversation.Id);
        Assert.Equal(setup.Source.User.Id, conversation.AgentId);
        Assert.False(await db.TicketTransfers.AnyAsync(item => item.TicketId == setup.Ticket.Id));
    }

    [Fact]
    public async Task Transfer_ToFullAgent_Returns409_AndKeepsTicket()
    {
        var setup = await CreateTransferSetupAsync(targetMax: 1);

        // 目标客服只有 1 个名额，先用另一名访客占满
        var filler = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        var filling = await TestApi.CreateConversationAsync(
            _factory.CreateClient(filler.Token), setup.Target.User.Id);
        Assert.Equal(setup.Target.User.Id, filling.AgentId);

        var response = await TransferAsync(
            _factory.CreateClient(setup.Source.Token), setup.Ticket.Id, setup.Target.User.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("会话已满", await response.Content.ReadAsStringAsync());

        // 满员拒绝不能留下任何痕迹
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversation = await db.Conversations.AsNoTracking()
            .SingleAsync(item => item.Id == setup.Conversation.Id);
        Assert.Equal(setup.Source.User.Id, conversation.AgentId);

        var ticket = await db.Tickets.AsNoTracking().SingleAsync(item => item.Id == setup.Ticket.Id);
        Assert.Equal(TicketStatus.Open, ticket.Status);
        Assert.False(await db.TicketTransfers.AnyAsync(item => item.TicketId == setup.Ticket.Id));
    }

    [Fact]
    public async Task Transfer_Detail_ReturnsTransfers_InChronologicalOrder()
    {
        var first = await CreateAgentAsync("第一任客服");
        var second = await CreateAgentAsync("第二任客服");
        var third = await CreateAgentAsync("第三任客服");
        var visitor = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        var conversation = await TestApi.CreateConversationAsync(
            _factory.CreateClient(visitor.Token), first.User.Id);
        var ticket = await GetTicketAsync(_factory.CreateClient(first.Token), conversation.Id);

        var toSecond = await TransferAsync(
            _factory.CreateClient(first.Token), ticket.Id, second.User.Id);
        Assert.Equal(HttpStatusCode.OK, toSecond.StatusCode);

        var toThird = await TransferAsync(
            _factory.CreateClient(second.Token), ticket.Id, third.User.Id, "再次转派");
        Assert.Equal(HttpStatusCode.OK, toThird.StatusCode);

        var detail = (await _factory.CreateClient(third.Token)
            .GetFromJsonAsync<TicketDetailDto>($"/api/tickets/{ticket.Id}"))!;

        Assert.Equal(2, detail.Transfers.Count);
        Assert.Equal(first.User.Id, detail.Transfers[0].FromAgentId);
        Assert.Equal(second.User.Id, detail.Transfers[0].ToAgentId);
        Assert.Null(detail.Transfers[0].Note);
        Assert.Equal(second.User.Id, detail.Transfers[1].FromAgentId);
        Assert.Equal(third.User.Id, detail.Transfers[1].ToAgentId);
        Assert.Equal("再次转派", detail.Transfers[1].Note);
        Assert.Equal(third.User.DisplayName, detail.Ticket.AgentName);
    }

    [Fact]
    public async Task Transfer_RealTime_MovesGroups_AndOldAgentStopsReceiving()
    {
        var setup = await CreateTransferSetupAsync("实时源客服", "实时目标客服");
        var sourceClient = _factory.CreateClient(setup.Source.Token);

        await using var sourceConnection = await ConnectAsync(setup.Source.Token);
        await using var targetConnection = await ConnectAsync(setup.Target.Token);
        await using var visitorConnection = await ConnectAsync(setup.Visitor.Token);

        await sourceConnection.InvokeAsync("JoinConversation", setup.Conversation.Id);
        await visitorConnection.InvokeAsync("JoinConversation", setup.Conversation.Id);

        // 转派前目标客服不是会话成员，入组应被拒绝
        await Assert.ThrowsAsync<HubException>(
            () => targetConnection.InvokeAsync("JoinConversation", setup.Conversation.Id));

        var sourceTransferred = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var visitorTransferred = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var targetAssigned = new TaskCompletionSource<ConversationDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        sourceConnection.On<JsonElement>("ConversationTransferred", payload => sourceTransferred.TrySetResult(payload));
        visitorConnection.On<JsonElement>("ConversationTransferred", payload => visitorTransferred.TrySetResult(payload));
        targetConnection.On<ConversationDto>("ConversationAssigned", conversation => targetAssigned.TrySetResult(conversation));

        var sourceMessageCount = 0;
        sourceConnection.On<MessageDto>("ReceiveMessage", _ => Interlocked.Increment(ref sourceMessageCount));
        var targetReceived = new TaskCompletionSource<MessageDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var visitorReceived = new TaskCompletionSource<MessageDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        targetConnection.On<MessageDto>("ReceiveMessage", message =>
        {
            if (message.SenderId == setup.Visitor.User.Id)
            {
                targetReceived.TrySetResult(message);
            }
        });
        visitorConnection.On<MessageDto>("ReceiveMessage", message =>
        {
            if (message.SenderId == setup.Target.User.Id)
            {
                visitorReceived.TrySetResult(message);
            }
        });

        var transferResponse = await TransferAsync(
            sourceClient, setup.Ticket.Id, setup.Target.User.Id, "实时交接");
        Assert.Equal(HttpStatusCode.OK, transferResponse.StatusCode);

        var sourcePayload = await sourceTransferred.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var visitorPayload = await visitorTransferred.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var assigned = await targetAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(setup.Conversation.Id, sourcePayload.GetProperty("conversationId").GetInt32());
        Assert.Equal(setup.Ticket.Id, sourcePayload.GetProperty("ticketId").GetInt32());
        Assert.Equal(setup.Target.User.Id, sourcePayload.GetProperty("agentId").GetInt32());
        Assert.Equal(setup.Target.User.DisplayName, sourcePayload.GetProperty("agentName").GetString());
        Assert.Equal(setup.Target.User.Id, visitorPayload.GetProperty("agentId").GetInt32());
        Assert.Equal(setup.Target.User.DisplayName, visitorPayload.GetProperty("agentName").GetString());
        Assert.Equal(setup.Conversation.Id, assigned.Id);
        Assert.Equal(setup.Target.User.Id, assigned.AgentId);
        Assert.Equal(setup.Target.User.DisplayName, assigned.AgentName);

        // 访客继续发消息：目标客服即时收到，原客服收不到
        var ack = await visitorConnection.InvokeAsync<SendMessageAck>(
            "SendMessage", setup.Conversation.Id, "转派后的新消息", Guid.NewGuid().ToString("N"));
        var delivered = await targetReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ack.Seq, delivered.Seq);
        Assert.Equal("转派后的新消息", delivered.Content);

        // 目标客服可以继续回复，访客能收到
        await targetConnection.InvokeAsync<SendMessageAck>(
            "SendMessage", setup.Conversation.Id, "我是新客服，继续为您服务", Guid.NewGuid().ToString("N"));
        var reply = await visitorReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(setup.Target.User.Id, reply.SenderId);
        Assert.Equal("我是新客服，继续为您服务", reply.Content);

        // 原客服的组身份与接口权限同时收回
        await Assert.ThrowsAsync<HubException>(
            () => sourceConnection.InvokeAsync("JoinConversation", setup.Conversation.Id));
        await Assert.ThrowsAsync<HubException>(
            () => sourceConnection.InvokeAsync<SendMessageAck>(
                "SendMessage", setup.Conversation.Id, "原客服还能发吗", Guid.NewGuid().ToString("N")));

        // 上面的往返是屏障：目标客服已收到新消息，原客服仍然一条都没收到
        Assert.Equal(0, Volatile.Read(ref sourceMessageCount));
    }
}
