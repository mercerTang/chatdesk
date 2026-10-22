using System.Net;
using System.Net.Http.Json;
using ChatServer.Dtos;
using ChatServer.Models;

namespace ChatServer.Tests;

/// <summary>登录、角色鉴权、SignalR 协商的鉴权边界。</summary>
public class AuthApiTests : IClassFixture<ChatServerFactory>
{
    private readonly ChatServerFactory _factory;

    public AuthApiTests(ChatServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Register_ThenLogin_ReturnsTokenAndVisitorProfile()
    {
        var username = TestApi.NewVisitorName();

        var registered = await TestApi.RegisterVisitorAsync(_factory.CreateClient(), username);
        Assert.False(string.IsNullOrWhiteSpace(registered.Token));
        Assert.Equal(Roles.Visitor, registered.User.Role);
        Assert.Equal(username, registered.User.Username);

        var loggedIn = await TestApi.LoginAsync(_factory.CreateClient(), username, TestApi.VisitorPassword);
        Assert.Equal(registered.User.Id, loggedIn.User.Id);

        var me = await _factory.CreateClient(loggedIn.Token).GetFromJsonAsync<UserDto>("/api/auth/me");
        Assert.NotNull(me);
        Assert.Equal(username, me!.Username);
        Assert.Equal(Roles.Visitor, me.Role);
    }

    [Fact]
    public async Task Register_DuplicateUsername_Returns409()
    {
        var username = TestApi.NewVisitorName();
        await TestApi.RegisterVisitorAsync(_factory.CreateClient(), username);

        var response = await _factory.CreateClient().PostAsJsonAsync("/api/auth/register", new
        {
            username,
            password = TestApi.VisitorPassword
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Login_WithWrongPassword_Returns401()
    {
        var username = TestApi.NewVisitorName();
        await TestApi.RegisterVisitorAsync(_factory.CreateClient(), username);

        var response = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new
        {
            username,
            password = "wrong-password"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AgentCredentials_AreSeededAndUsable()
    {
        var agent = await TestApi.LoginAgentAsync(_factory.CreateClient(), "agent1");
        Assert.Equal(Roles.Agent, agent.User.Role);
        Assert.Equal("Mia", agent.User.DisplayName);
    }

    [Fact]
    public async Task ProtectedEndpoints_WithoutToken_Return401()
    {
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/agents")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/conversations/mine")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/conversations/1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/conversations/1/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Register_RejectsDisplayNameLongerThan64_OrWithControlChars()
    {
        var tooLong = await _factory.CreateClient().PostAsJsonAsync("/api/auth/register", new
        {
            username = TestApi.NewVisitorName(),
            password = TestApi.VisitorPassword,
            displayName = new string('名', 65)
        });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Contains("64", await tooLong.Content.ReadAsStringAsync());

        var withControl = await _factory.CreateClient().PostAsJsonAsync("/api/auth/register", new
        {
            username = TestApi.NewVisitorName(),
            password = TestApi.VisitorPassword,
            displayName = "访客\u0007"
        });
        Assert.Equal(HttpStatusCode.BadRequest, withControl.StatusCode);

        // 边界值 64 个字符合法
        var boundary = await _factory.CreateClient().PostAsJsonAsync("/api/auth/register", new
        {
            username = TestApi.NewVisitorName(),
            password = TestApi.VisitorPassword,
            displayName = new string('名', 64)
        });
        Assert.Equal(HttpStatusCode.OK, boundary.StatusCode);
    }

    [Fact]
    public async Task Agent_CannotCreateConversation_Returns403()
    {
        var agent = await TestApi.LoginAgentAsync(_factory.CreateClient());

        var response = await _factory.CreateClient(agent.Token)
            .PostAsJsonAsync("/api/conversations", new { agentId = agent.User.Id });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SignalRNegotiate_RequiresToken()
    {
        var anonymous = await _factory.CreateClient().PostAsync("/hubs/chat/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var visitor = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        var authorized = await _factory.CreateClient(visitor.Token)
            .PostAsync("/hubs/chat/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
    }
}
