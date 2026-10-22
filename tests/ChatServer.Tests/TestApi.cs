using System.Net;
using System.Net.Http.Json;
using ChatServer.Dtos;
using ChatServer.Services;

namespace ChatServer.Tests;

internal static class TestApi
{
    public const string AgentPassword = "Agent@123456";
    public const string AdminPassword = "Admin@123456";
    public const string VisitorPassword = "pass123456";

    public static string NewVisitorName() => "visitor_" + Guid.NewGuid().ToString("N")[..10];

    public static async Task<AuthResponse> RegisterVisitorAsync(HttpClient client, string? username = null)
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            username = username ?? NewVisitorName(),
            password = VisitorPassword,
            displayName = "访客" + Guid.NewGuid().ToString("N")[..4]
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    public static async Task<AuthResponse> LoginAsync(HttpClient client, string username, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    public static async Task<AuthResponse> LoginAgentAsync(HttpClient client, string username = "agent1")
        => await LoginAsync(client, username, AgentPassword);

    public static async Task<AuthResponse> LoginAdminAsync(HttpClient client)
        => await LoginAsync(client, DatabaseInitializer.AdminUsername, AdminPassword);

    public static async Task<ConversationDto> CreateConversationAsync(HttpClient client, int? agentId)
    {
        var response = await client.PostAsJsonAsync("/api/conversations", new { agentId });
        Assert.True(
            response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
            $"创建会话应成功，实际 {(int)response.StatusCode}");
        return (await response.Content.ReadFromJsonAsync<ConversationDto>())!;
    }

    public static async Task<List<MessageDto>> GetMessagesAsync(HttpClient client, int conversationId, long afterSeq)
    {
        var response = await client.GetAsync($"/api/conversations/{conversationId}/messages?afterSeq={afterSeq}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<MessageDto>>()) ?? new List<MessageDto>();
    }
}
