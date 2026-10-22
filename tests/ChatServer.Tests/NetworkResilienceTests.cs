using ChatServer.Dtos;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace ChatServer.Tests;

/// <summary>极端网络：客服离线/断线期间访客发送的消息不丢，恢复后可按水位补齐。</summary>
public class NetworkResilienceTests : IClassFixture<ChatServerFactory>
{
    private readonly ChatServerFactory _factory;

    public NetworkResilienceTests(ChatServerFactory factory)
    {
        _factory = factory;
    }

    private async Task<HubConnection> ConnectAsync(string token)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "/hubs/chat"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        await connection.StartAsync();
        return connection;
    }

    private static Task<SendMessageAck> SendAsync(
        HubConnection connection,
        int conversationId,
        string content)
        => connection.InvokeAsync<SendMessageAck>(
            "SendMessage",
            conversationId,
            content,
            Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task MessagesSentWhileAgentOffline_AreRecoveredByPull()
    {
        var agent = await TestApi.LoginAgentAsync(_factory.CreateClient(), "agent1");
        var visitor = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        var conversation = await TestApi.CreateConversationAsync(
            _factory.CreateClient(visitor.Token),
            agent.User.Id);

        await using var visitorConnection = await ConnectAsync(visitor.Token);
        await visitorConnection.InvokeAsync("JoinConversation", conversation.Id);

        // 客服从未连接：访客连续发送三条，回执序号必须连续
        var first = await SendAsync(visitorConnection, conversation.Id, "客服离线时的消息 1");
        var second = await SendAsync(visitorConnection, conversation.Id, "客服离线时的消息 2");
        var third = await SendAsync(visitorConnection, conversation.Id, "客服离线时的消息 3");
        Assert.Equal(new[] { 1L, 2L, 3L }, new[] { first.Seq, second.Seq, third.Seq });

        var agentClient = _factory.CreateClient(agent.Token);
        var recovered = await TestApi.GetMessagesAsync(agentClient, conversation.Id, 0);
        Assert.Equal(3, recovered.Count);
        Assert.Equal(new[] { 1L, 2L, 3L }, recovered.Select(item => item.Seq));

        // 客服上线后又断开，期间再发一条，恢复后按水位只补这一条
        await using (var agentConnection = await ConnectAsync(agent.Token))
        {
            await agentConnection.InvokeAsync("JoinConversation", conversation.Id);
        }

        var fourth = await SendAsync(visitorConnection, conversation.Id, "客服断线期间的消息");
        Assert.Equal(4, fourth.Seq);

        var missed = await TestApi.GetMessagesAsync(agentClient, conversation.Id, 3);
        var only = Assert.Single(missed);
        Assert.Equal(4, only.Seq);
        Assert.Equal("客服断线期间的消息", only.Content);
    }

    [Fact]
    public async Task AgentReconnect_CanReadMessagesSentWhileDisconnected()
    {
        var agent = await TestApi.LoginAgentAsync(_factory.CreateClient(), "agent2");
        var visitor = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        var conversation = await TestApi.CreateConversationAsync(
            _factory.CreateClient(visitor.Token),
            agent.User.Id);

        await using var visitorConnection = await ConnectAsync(visitor.Token);
        await visitorConnection.InvokeAsync("JoinConversation", conversation.Id);

        var firstConnection = await ConnectAsync(agent.Token);
        await firstConnection.InvokeAsync("JoinConversation", conversation.Id);
        await firstConnection.StopAsync();
        await firstConnection.DisposeAsync();

        var first = await SendAsync(visitorConnection, conversation.Id, "断线消息 1");
        var second = await SendAsync(visitorConnection, conversation.Id, "断线消息 2");
        Assert.Equal(1, first.Seq);
        Assert.Equal(2, second.Seq);

        var agentClient = _factory.CreateClient(agent.Token);
        var missed = await TestApi.GetMessagesAsync(agentClient, conversation.Id, 0);
        Assert.Equal(2, missed.Count);

        await using var reconnected = await ConnectAsync(agent.Token);
        await reconnected.InvokeAsync("JoinConversation", conversation.Id);
        var afterReconnect = await TestApi.GetMessagesAsync(agentClient, conversation.Id, 0);
        Assert.Equal(new[] { 1L, 2L }, afterReconnect.Select(item => item.Seq));
        Assert.Contains(afterReconnect, item => item.Content == "断线消息 1");
        Assert.Contains(afterReconnect, item => item.Content == "断线消息 2");
    }
}
