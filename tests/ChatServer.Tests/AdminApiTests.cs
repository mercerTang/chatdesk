using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatServer.Data;
using ChatServer.Dtos;
using ChatServer.Models;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatServer.Tests;

/// <summary>管理中台：角色鉴权、客服增改停用、跨客服读、强制转派台账（含 OperatorId）。</summary>
public class AdminApiTests : IClassFixture<ChatServerFactory>
{
    private readonly ChatServerFactory _factory;

    public AdminApiTests(ChatServerFactory factory)
    {
        _factory = factory;
    }

    // ---------------------------------------------------------------- 辅助

    /// <summary>用种子管理员登录后的客户端。</summary>
    private async Task<HttpClient> AdminClientAsync()
    {
        var admin = await TestApi.LoginAdminAsync(_factory.CreateClient());
        return _factory.CreateClient(admin.Token);
    }

    private static string UniqueUsername(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    private static async Task<AdminAgentDto> CreateAgentAsync(
        HttpClient admin, int maxConcurrent = 5, string? password = null)
    {
        var response = await admin.PostAsJsonAsync("/api/admin/agents", new
        {
            username = UniqueUsername("adm_agent_"),
            displayName = "后台客服" + Guid.NewGuid().ToString("N")[..4],
            password = password ?? TestApi.AgentPassword,
            maxConcurrentConversations = maxConcurrent
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AdminAgentDto>())!;
    }

    /// <summary>建一个访客会话并直接落一条访客消息（绕过 Hub，专注中台读接口）。</summary>
    private async Task<(ConversationDto Conversation, AuthResponse Visitor)> CreateConversationAsync(
        int agentId, string content)
    {
        var visitor = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        var conversation = await TestApi.CreateConversationAsync(
            _factory.CreateClient(visitor.Token), agentId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tracked = await db.Conversations.FirstAsync(item => item.Id == conversation.Id);
        db.Messages.Add(new Message
        {
            ConversationId = tracked.Id,
            Seq = 1,
            SenderId = visitor.User.Id,
            SenderRole = Roles.Visitor,
            Content = content,
            ClientMessageId = Guid.NewGuid().ToString("N"),
            SentAt = DateTimeOffset.UtcNow
        });
        tracked.LastMessageSeq = 1;
        await db.SaveChangesAsync();

        return (conversation, visitor);
    }

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

    // ---------------------------------------------------------------- 用例

    [Fact]
    public async Task AdminEndpoints_RequireAdminRole()
    {
        var anonymous = _factory.CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/admin/agents")).StatusCode);

        var visitor = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        var visitorClient = _factory.CreateClient(visitor.Token);
        foreach (var path in new[] { "/api/admin/agents", "/api/admin/conversations", "/api/admin/tickets" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await visitorClient.GetAsync(path)).StatusCode);
        }

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await visitorClient.PostAsJsonAsync("/api/admin/agents", new
            {
                username = UniqueUsername("adm_x_"),
                displayName = "越权新增",
                password = TestApi.AgentPassword
            })).StatusCode);

        var agent = await TestApi.LoginAgentAsync(_factory.CreateClient(), "agent1");
        var agentClient = _factory.CreateClient(agent.Token);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await agentClient.GetAsync("/api/admin/agents")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await agentClient.GetAsync("/api/admin/tickets")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await agentClient.PostAsJsonAsync(
                "/api/admin/tickets/1/transfer", new { agentId = agent.User.Id })).StatusCode);
    }

    [Fact]
    public async Task CreateAgent_IsUsableAndRejectsInvalidInput()
    {
        var admin = await AdminClientAsync();

        var created = await CreateAgentAsync(admin);
        Assert.True(created.IsActive);
        Assert.False(created.IsOnline);
        Assert.Equal(0, created.ActiveConversationCount);

        var agents = (await admin.GetFromJsonAsync<List<AdminAgentDto>>("/api/admin/agents"))!;
        Assert.Contains(agents, item => item.Id == created.Id && item.Username == created.Username);

        // 新增即可登录，角色为客服
        var login = await TestApi.LoginAsync(_factory.CreateClient(), created.Username, TestApi.AgentPassword);
        Assert.Equal(Roles.Agent, login.User.Role);
        Assert.Equal(created.Id, login.User.Id);

        var duplicate = await admin.PostAsJsonAsync("/api/admin/agents", new
        {
            username = created.Username,
            displayName = "重复账号",
            password = TestApi.AgentPassword
        });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var weakPassword = await admin.PostAsJsonAsync("/api/admin/agents", new
        {
            username = UniqueUsername("adm_weak_"),
            displayName = "弱密码",
            password = "12345"
        });
        Assert.Equal(HttpStatusCode.BadRequest, weakPassword.StatusCode);

        var shortUsername = await admin.PostAsJsonAsync("/api/admin/agents", new
        {
            username = "ab",
            displayName = "短名",
            password = TestApi.AgentPassword
        });
        Assert.Equal(HttpStatusCode.BadRequest, shortUsername.StatusCode);

        var badLimit = await admin.PostAsJsonAsync("/api/admin/agents", new
        {
            username = UniqueUsername("adm_limit_"),
            displayName = "超限",
            password = TestApi.AgentPassword,
            maxConcurrentConversations = 51
        });
        Assert.Equal(HttpStatusCode.BadRequest, badLimit.StatusCode);

        var longDisplayName = await admin.PostAsJsonAsync("/api/admin/agents", new
        {
            username = UniqueUsername("adm_long_"),
            displayName = new string('名', 65),
            password = TestApi.AgentPassword
        });
        Assert.Equal(HttpStatusCode.BadRequest, longDisplayName.StatusCode);
    }

    [Fact]
    public async Task UpdateAgent_ChangesProfile_ResetsPassword_AndDeactivationBlocksLogin()
    {
        var admin = await AdminClientAsync();
        var created = await CreateAgentAsync(admin);

        var updatedResponse = await admin.PatchAsJsonAsync($"/api/admin/agents/{created.Id}", new
        {
            displayName = "新的展示名",
            maxConcurrentConversations = 9
        });
        Assert.Equal(HttpStatusCode.OK, updatedResponse.StatusCode);
        var updated = (await updatedResponse.Content.ReadFromJsonAsync<AdminAgentDto>())!;
        Assert.Equal("新的展示名", updated.DisplayName);
        Assert.Equal(9, updated.MaxConcurrentConversations);
        Assert.True(updated.IsActive);

        // 重置密码：旧密码失效，新密码可登录
        var reset = await admin.PatchAsJsonAsync(
            $"/api/admin/agents/{created.Id}", new { password = "reset123456" });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new
            {
                username = created.Username,
                password = TestApi.AgentPassword
            })).StatusCode);

        var relogin = await TestApi.LoginAsync(_factory.CreateClient(), created.Username, "reset123456");
        Assert.Equal(created.Id, relogin.User.Id);

        // 停用：登录 403「账号已停用」
        var disable = await admin.PatchAsJsonAsync(
            $"/api/admin/agents/{created.Id}", new { isActive = false });
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        Assert.False((await disable.Content.ReadFromJsonAsync<AdminAgentDto>())!.IsActive);

        var blocked = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new
        {
            username = created.Username,
            password = "reset123456"
        });
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Contains("账号已停用", await blocked.Content.ReadAsStringAsync());

        // 启用后恢复
        var enable = await admin.PatchAsJsonAsync(
            $"/api/admin/agents/{created.Id}", new { isActive = true });
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        var restored = await TestApi.LoginAsync(_factory.CreateClient(), created.Username, "reset123456");
        Assert.Equal(created.Id, restored.User.Id);

        // 非法值
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await admin.PatchAsJsonAsync(
                $"/api/admin/agents/{created.Id}", new { maxConcurrentConversations = 0 })).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await admin.PatchAsJsonAsync(
                $"/api/admin/agents/{created.Id}", new { password = "12345" })).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await admin.PatchAsJsonAsync(
                $"/api/admin/agents/{created.Id}", new { displayName = "   " })).StatusCode);

        // 不存在的客服与访客账号都不能被当作客服改
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await admin.PatchAsJsonAsync(
                "/api/admin/agents/999999", new { displayName = "无此人" })).StatusCode);

        var visitor = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await admin.PatchAsJsonAsync(
                $"/api/admin/agents/{visitor.User.Id}", new { displayName = "越权改访客" })).StatusCode);
    }

    [Fact]
    public async Task Admin_ReadsAcrossAgents_ConversationsAndTickets()
    {
        var admin = await AdminClientAsync();
        var agentA = await CreateAgentAsync(admin);
        var agentB = await CreateAgentAsync(admin);

        var (conversationA, _) = await CreateConversationAsync(agentA.Id, "找客服A的问题");
        var (conversationB, _) = await CreateConversationAsync(agentB.Id, "找客服B的问题");

        var all = (await admin.GetFromJsonAsync<List<AdminConversationDto>>(
            "/api/admin/conversations"))!;
        Assert.Contains(all, item =>
            item.Id == conversationA.Id && item.AgentId == agentA.Id && item.LastMessage == "找客服A的问题");
        Assert.Contains(all, item => item.Id == conversationB.Id && item.AgentId == agentB.Id);

        var byAgent = (await admin.GetFromJsonAsync<List<AdminConversationDto>>(
            $"/api/admin/conversations?agentId={agentA.Id}"))!;
        var row = Assert.Single(byAgent);
        Assert.Equal(conversationA.Id, row.Id);
        Assert.Equal(agentA.DisplayName, row.AgentName);

        var tickets = (await admin.GetFromJsonAsync<List<TicketDto>>("/api/admin/tickets"))!;
        var ticketA = Assert.Single(tickets, item => item.ConversationId == conversationA.Id);
        Assert.Equal(agentA.DisplayName, ticketA.AgentName);
        Assert.Equal(1, ticketA.MessageCount);

        var byAgentTickets = (await admin.GetFromJsonAsync<List<TicketDto>>(
            $"/api/admin/tickets?agentId={agentB.Id}"))!;
        Assert.Contains(byAgentTickets, item => item.ConversationId == conversationB.Id);
        Assert.DoesNotContain(byAgentTickets, item => item.ConversationId == conversationA.Id);

        // 任意工单详情：聊天记录可读、台账为空
        var detail = (await admin.GetFromJsonAsync<TicketDetailDto>(
            $"/api/admin/tickets/{ticketA.Id}"))!;
        Assert.Equal(conversationA.Id, detail.Ticket.ConversationId);
        var message = Assert.Single(detail.Messages);
        Assert.Equal("找客服A的问题", message.Content);
        Assert.Empty(detail.Transfers);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await admin.GetAsync("/api/admin/tickets/999999")).StatusCode);

        // 状态筛选
        var open = (await admin.GetFromJsonAsync<List<TicketDto>>("/api/admin/tickets?status=open"))!;
        Assert.Contains(open, item => item.Id == ticketA.Id);
        var closed = (await admin.GetFromJsonAsync<List<TicketDto>>("/api/admin/tickets?status=closed"))!;
        Assert.DoesNotContain(closed, item => item.Id == ticketA.Id);
    }

    [Fact]
    public async Task ForceTransfer_MigratesOwnership_AndRecordsOperator()
    {
        var adminLogin = await TestApi.LoginAdminAsync(_factory.CreateClient());
        var admin = _factory.CreateClient(adminLogin.Token);
        var agentA = await CreateAgentAsync(admin);
        var agentB = await CreateAgentAsync(admin);

        var (conversation, _) = await CreateConversationAsync(agentA.Id, "需要管理员调度");
        var ticket = Assert.Single(
            (await admin.GetFromJsonAsync<List<TicketDto>>($"/api/admin/tickets?agentId={agentA.Id}"))!);

        var response = await admin.PostAsJsonAsync($"/api/admin/tickets/{ticket.Id}/transfer", new
        {
            agentId = agentB.Id,
            note = "管理员调度"
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<TicketDto>())!;
        Assert.Equal(agentB.DisplayName, updated.AgentName);

        // 归属迁移落在会话上
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tracked = await db.Conversations.AsNoTracking()
                .SingleAsync(item => item.Id == conversation.Id);
            Assert.Equal(agentB.Id, tracked.AgentId);

            // 台账：原客服、目标客服、操作管理员
            var ledger = await db.TicketTransfers.AsNoTracking()
                .SingleAsync(item => item.TicketId == ticket.Id);
            Assert.Equal(agentA.Id, ledger.FromAgentId);
            Assert.Equal(agentB.Id, ledger.ToAgentId);
            Assert.Equal(adminLogin.User.Id, ledger.OperatorId);
            Assert.Equal("管理员调度", ledger.Note);
        }

        // 原客服列表消失，目标客服列表出现
        var agentAClient = _factory.CreateClient(
            (await TestApi.LoginAsync(_factory.CreateClient(), agentA.Username, TestApi.AgentPassword)).Token);
        var agentBClient = _factory.CreateClient(
            (await TestApi.LoginAsync(_factory.CreateClient(), agentB.Username, TestApi.AgentPassword)).Token);
        Assert.DoesNotContain(
            (await agentAClient.GetFromJsonAsync<List<TicketDto>>("/api/tickets"))!,
            item => item.Id == ticket.Id);
        Assert.Contains(
            (await agentBClient.GetFromJsonAsync<List<TicketDto>>("/api/tickets"))!,
            item => item.Id == ticket.Id);

        // 中台详情里的台账带管理员身份
        var detail = (await admin.GetFromJsonAsync<TicketDetailDto>(
            $"/api/admin/tickets/{ticket.Id}"))!;
        var transfer = Assert.Single(detail.Transfers);
        Assert.Equal(agentA.Id, transfer.FromAgentId);
        Assert.Equal(agentA.DisplayName, transfer.FromAgentName);
        Assert.Equal(agentB.Id, transfer.ToAgentId);
        Assert.Equal(adminLogin.User.Id, transfer.OperatorId);
        Assert.Equal(adminLogin.User.DisplayName, transfer.OperatorName);
        Assert.Equal("管理员调度", transfer.Note);

        // 目标已是当前负责人
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync(
                $"/api/admin/tickets/{ticket.Id}/transfer", new { agentId = agentB.Id })).StatusCode);

        // 目标不是客服
        var visitor = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await admin.PostAsJsonAsync(
                $"/api/admin/tickets/{ticket.Id}/transfer", new { agentId = visitor.User.Id })).StatusCode);

        // 工单不存在
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await admin.PostAsJsonAsync(
                "/api/admin/tickets/999999/transfer", new { agentId = agentB.Id })).StatusCode);

        // 已结束的工单不能转派
        var (closedConversation, closedVisitor) = await CreateConversationAsync(agentA.Id, "将被关闭");
        var closedTicket = Assert.Single(
            (await admin.GetFromJsonAsync<List<TicketDto>>($"/api/admin/tickets?agentId={agentA.Id}"))!,
            item => item.ConversationId == closedConversation.Id);
        await _factory.CreateClient(closedVisitor.Token)
            .PostAsync($"/api/conversations/{closedConversation.Id}/close", null);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await admin.PostAsJsonAsync(
                $"/api/admin/tickets/{closedTicket.Id}/transfer", new { agentId = agentB.Id })).StatusCode);

        // 目标客服满员：复用客服转派的容量规则
        var (capacityConversation, _) = await CreateConversationAsync(agentA.Id, "容量验证");
        var capacityTicket = Assert.Single(
            (await admin.GetFromJsonAsync<List<TicketDto>>($"/api/admin/tickets?agentId={agentA.Id}"))!,
            item => item.ConversationId == capacityConversation.Id);
        var fullAgent = await CreateAgentAsync(admin, maxConcurrent: 1);
        await CreateConversationAsync(fullAgent.Id, "占满名额");
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await admin.PostAsJsonAsync(
                $"/api/admin/tickets/{capacityTicket.Id}/transfer", new { agentId = fullAgent.Id })).StatusCode);
    }

    [Fact]
    public async Task DeactivatedAgent_ReceivesSessionRevoked_WithoutHardDisconnect()
    {
        var admin = await AdminClientAsync();
        var agent = await CreateAgentAsync(admin);
        var agentLogin = await TestApi.LoginAsync(
            _factory.CreateClient(), agent.Username, TestApi.AgentPassword);

        await using var connection = await ConnectAsync(agentLogin.Token);
        var revoked = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("SessionRevoked", payload => revoked.TrySetResult(payload));

        var response = await admin.PatchAsJsonAsync(
            $"/api/admin/agents/{agent.Id}", new { isActive = false });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await revoked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(agent.Id, payload.GetProperty("agentId").GetInt32());

        // 停用不硬断：连接仍在，由前端收到事件后自行登出
        Assert.Equal(HubConnectionState.Connected, connection.State);
    }

    /// <summary>停用要真正生效：不能再被分配会话，也不能用未过期的 token 重连 Hub。</summary>
    [Fact]
    public async Task DeactivatedAgent_IsExcludedFromAssignment_AndCannotReconnect()
    {
        var admin = await AdminClientAsync();
        var agent = await CreateAgentAsync(admin);
        var agentLogin = await TestApi.LoginAsync(
            _factory.CreateClient(), agent.Username, TestApi.AgentPassword);

        var response = await admin.PatchAsJsonAsync(
            $"/api/admin/agents/{agent.Id}", new { isActive = false });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 访客显式指定停用客服：与「客服不存在」同口径
        var visitor = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        var create = await _factory.CreateClient(visitor.Token)
            .PostAsJsonAsync("/api/conversations", new { agentId = agent.Id });
        Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);

        // 停用前签发的 token 也建不起可用连接（拒绝可能表现为握手失败或连接随即断开）
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "/hubs/chat"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(agentLogin.Token);
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        try
        {
            await connection.StartAsync();
        }
        catch
        {
            // 握手阶段被拒也算通过
        }

        await Task.Delay(500);
        Assert.NotEqual(HubConnectionState.Connected, connection.State);
        await connection.DisposeAsync();
    }
}
