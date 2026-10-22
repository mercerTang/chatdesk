using ChatServer.Dtos;
using ChatServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatServer.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AgentsController : ControllerBase
{
    private readonly ChatService _chat;

    public AgentsController(ChatService chat)
    {
        _chat = chat;
    }

    /// <summary>在线客服列表（含在线状态与当前负载），访客与客服均可读。</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AgentDto>>> GetAll(CancellationToken ct)
        => Ok(await _chat.GetAgentsAsync(ct));
}
