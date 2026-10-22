namespace ChatServer.Hubs;

/// <summary>SignalR 分组名，服务端与控制器共用，避免各处拼写不一致。</summary>
public static class ChatGroups
{
    public static string Conversation(int conversationId) => $"conversation-{conversationId}";

    public static string Agent(int agentId) => $"agent-{agentId}";
}
