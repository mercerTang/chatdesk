using System.Net.Http.Json;
using ChatServer.Dtos;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace ChatServer.Tests;

/// <summary>SignalR 通道：SendMessage 回执含 Seq、先落库后广播、幂等键不重复广播。</summary>
public class ChatHubTests : IClassFixture<ChatServerFactory>
{
    private readonly ChatServerFactory _factory;

    public ChatHubTests(ChatServerFactory factory)
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
                // TestServer 不支持 WebSocket，改用长轮询
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        await connection.StartAsync();
        return connection;
    }

    [Fact]
    public async Task SendMessage_ReturnsAckWithSeq_AndBroadcastsToPeer()
    {
        var agent = await TestApi.LoginAgentAsync(_factory.CreateClient(), "agent1");
        var visitor = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        var conversation = await TestApi.CreateConversationAsync(_factory.CreateClient(visitor.Token), agent.User.Id);

        await using var visitorConnection = await ConnectAsync(visitor.Token);
        await using var agentConnection = await ConnectAsync(agent.Token);

        await visitorConnection.InvokeAsync("JoinConversation", conversation.Id);
        await agentConnection.InvokeAsync("JoinConversation", conversation.Id);

        var received = new TaskCompletionSource<MessageDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        agentConnection.On<MessageDto>("ReceiveMessage", message => received.TrySetResult(message));

        var clientMessageId = Guid.NewGuid().ToString("N");
        var ack = await visitorConnection.InvokeAsync<SendMessageAck>(
            "SendMessage", conversation.Id, "你好，客服在吗？", clientMessageId);

        Assert.Equal(1, ack.Seq);
        Assert.False(ack.Duplicated);
        Assert.Equal(clientMessageId, ack.ClientMessageId);

        var delivered = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ack.Seq, delivered.Seq);
        Assert.Equal(conversation.Id, delivered.ConversationId);
        Assert.Equal("你好，客服在吗？", delivered.Content);
        Assert.Equal(visitor.User.Id, delivered.SenderId);
    }

    [Fact]
    public async Task DuplicateClientMessageId_IsAckedButNotBroadcastTwice()
    {
        var agent = await TestApi.LoginAgentAsync(_factory.CreateClient(), "agent2");
        var visitor = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        var conversation = await TestApi.CreateConversationAsync(_factory.CreateClient(visitor.Token), agent.User.Id);

        await using var visitorConnection = await ConnectAsync(visitor.Token);
        await using var agentConnection = await ConnectAsync(agent.Token);

        await visitorConnection.InvokeAsync("JoinConversation", conversation.Id);
        await agentConnection.InvokeAsync("JoinConversation", conversation.Id);

        var receivedCount = 0;
        agentConnection.On<MessageDto>("ReceiveMessage", _ => Interlocked.Increment(ref receivedCount));

        var clientMessageId = Guid.NewGuid().ToString("N");
        var first = await visitorConnection.InvokeAsync<SendMessageAck>(
            "SendMessage", conversation.Id, "只应出现一次", clientMessageId);
        var second = await visitorConnection.InvokeAsync<SendMessageAck>(
            "SendMessage", conversation.Id, "只应出现一次", clientMessageId);

        Assert.False(first.Duplicated);
        Assert.True(second.Duplicated);
        Assert.Equal(first.Seq, second.Seq);

        // 用一次往返当屏障：同一连接上的服务端消息按序到达，屏障返回时首条广播必已送达
        await agentConnection.InvokeAsync("JoinConversation", conversation.Id);
        Assert.Equal(1, Volatile.Read(ref receivedCount));
    }

    [Fact]
    public async Task AckRead_MarksPeerMessagesRead_AndNotifiesSender()
    {
        var agent = await TestApi.LoginAgentAsync(_factory.CreateClient(), "agent3");
        var visitor = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        var conversation = await TestApi.CreateConversationAsync(_factory.CreateClient(visitor.Token), agent.User.Id);

        await using var visitorConnection = await ConnectAsync(visitor.Token);
        await using var agentConnection = await ConnectAsync(agent.Token);

        await agentConnection.InvokeAsync("JoinConversation", conversation.Id);
        await visitorConnection.InvokeAsync("JoinConversation", conversation.Id);

        var readEvent = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        visitorConnection.On<object>("MessageRead", payload =>
        {
            var upToSeq = ReadUpToSeq(payload);
            readEvent.TrySetResult(upToSeq);
        });

        await visitorConnection.InvokeAsync<SendMessageAck>(
            "SendMessage", conversation.Id, "请确认已读", Guid.NewGuid().ToString("N"));

        await agentConnection.InvokeAsync("AckDelivered", conversation.Id, 1L);
        await agentConnection.InvokeAsync("AckRead", conversation.Id, 1L);

        var upTo = await readEvent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, upTo);

        var messages = (await _factory.CreateClient(visitor.Token)
            .GetFromJsonAsync<List<MessageDto>>($"/api/conversations/{conversation.Id}/messages?afterSeq=0"))!;
        Assert.Single(messages);
        Assert.NotNull(messages[0].DeliveredAt);
        Assert.NotNull(messages[0].ReadAt);
    }

    /// <summary>
    /// 回执必须携带确认方身份，且确认方自己的连接不收回执：
    /// 否则客户端会把自己的消息标成已送达/已读。
    /// </summary>
    [Fact]
    public async Task AckRead_CarriesByAccountId_AndConfirmerDoesNotReceiveOwnAck()
    {
        var agent = await TestApi.LoginAgentAsync(_factory.CreateClient(), "agent1");
        var visitor = await TestApi.RegisterVisitorAsync(_factory.CreateClient());
        var conversation = await TestApi.CreateConversationAsync(_factory.CreateClient(visitor.Token), agent.User.Id);

        await using var visitorConnection = await ConnectAsync(visitor.Token);
        await using var agentConnection = await ConnectAsync(agent.Token);

        await visitorConnection.InvokeAsync("JoinConversation", conversation.Id);
        await agentConnection.InvokeAsync("JoinConversation", conversation.Id);

        var deliveredEvent = new TaskCompletionSource<System.Text.Json.JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readEvent = new TaskCompletionSource<System.Text.Json.JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        visitorConnection.On<System.Text.Json.JsonElement>("MessageDelivered", payload => deliveredEvent.TrySetResult(payload));
        visitorConnection.On<System.Text.Json.JsonElement>("MessageRead", payload => readEvent.TrySetResult(payload));

        // 确认方自己的连接不应收到任何回执
        var confirmerEchoCount = 0;
        agentConnection.On<System.Text.Json.JsonElement>("MessageDelivered", _ => Interlocked.Increment(ref confirmerEchoCount));
        agentConnection.On<System.Text.Json.JsonElement>("MessageRead", _ => Interlocked.Increment(ref confirmerEchoCount));

        await visitorConnection.InvokeAsync<SendMessageAck>(
            "SendMessage", conversation.Id, "回执身份校验", Guid.NewGuid().ToString("N"));

        await agentConnection.InvokeAsync("AckDelivered", conversation.Id, 1L);
        await agentConnection.InvokeAsync("AckRead", conversation.Id, 1L);

        var delivered = await deliveredEvent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var read = await readEvent.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(agent.User.Id, delivered.GetProperty("byAccountId").GetInt32());
        Assert.Equal(agent.User.Id, read.GetProperty("byAccountId").GetInt32());
        Assert.Equal(1, read.GetProperty("upToSeq").GetInt64());

        // AckRead 的调用已返回，且回执走 OthersInGroup，确认方不应收到任何回执
        Assert.Equal(0, Volatile.Read(ref confirmerEchoCount));
    }

    private static long ReadUpToSeq(object payload)
    {
        // 服务端发送的是匿名对象，客户端反序列化为 JsonElement
        var element = System.Text.Json.JsonSerializer.SerializeToElement(payload);
        return element.TryGetProperty("upToSeq", out var value) ? value.GetInt64() : 0;
    }
}
