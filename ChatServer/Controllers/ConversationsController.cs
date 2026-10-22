using ChatServer.Dtos;
using ChatServer.Hubs;
using ChatServer.Models;
using ChatServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace ChatServer.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ConversationsController : ControllerBase
{
    private readonly ChatService _chat;
    private readonly IHubContext<ChatHub> _hub;
    private readonly ILogger<ConversationsController> _logger;

    public ConversationsController(ChatService chat, IHubContext<ChatHub> hub, ILogger<ConversationsController> logger)
    {
        _chat = chat;
        _hub = hub;
        _logger = logger;
    }

    /// <summary>访客发起会话：指定客服或自动分配最少负载；已有进行中会话则复用。</summary>
    [HttpPost]
    [Authorize(Roles = Roles.Visitor)]
    public async Task<ActionResult<ConversationDto>> Create([FromBody] CreateConversationRequest? request, CancellationToken ct)
    {
        var accountId = User.GetAccountId();
        if (accountId == 0)
        {
            return Unauthorized(new { error = "登录状态无效" });
        }

        try
        {
            var result = await _chat.CreateOrGetAsync(accountId, request?.AgentId, ct);
            if (result.Created)
            {
                // 客服端会话列表据此新增条目（客服此刻可能已连上实时通道）
                await _hub.Clients.Group(ChatGroups.Agent(result.Conversation.AgentId))
                    .SendAsync("ConversationCreated", result.Conversation, CancellationToken.None);
                return StatusCode(StatusCodes.Status201Created, result.Conversation);
            }

            return Ok(result.Conversation);
        }
        catch (ChatException ex)
        {
            return Error(ex);
        }
    }

    /// <summary>我的会话列表：访客看自己发起的，客服看分配给自己的。</summary>
    [HttpGet("mine")]
    public async Task<ActionResult<IReadOnlyList<ConversationDto>>> Mine(CancellationToken ct)
    {
        var accountId = User.GetAccountId();
        if (accountId == 0)
        {
            return Unauthorized(new { error = "登录状态无效" });
        }

        return Ok(await _chat.GetMineAsync(accountId, User.GetRole(), ct));
    }

    /// <summary>会话详情，仅会话双方可读。</summary>
    [HttpGet("{id:int}")]
    public async Task<ActionResult<ConversationDto>> GetById(int id, CancellationToken ct)
    {
        var accountId = User.GetAccountId();
        if (accountId == 0)
        {
            return Unauthorized(new { error = "登录状态无效" });
        }

        try
        {
            return Ok(await _chat.GetConversationAsync(id, accountId, User.GetRole(), ct));
        }
        catch (ChatException ex)
        {
            return Error(ex);
        }
    }

    /// <summary>增量补拉：返回 Seq &gt; afterSeq 的消息，按 Seq 升序。</summary>
    [HttpGet("{id:int}/messages")]
    public async Task<ActionResult<IReadOnlyList<MessageDto>>> GetMessages(
        int id,
        [FromQuery] long afterSeq = 0,
        [FromQuery] int limit = ChatService.MaxMessagesPerPull,
        CancellationToken ct = default)
    {
        var accountId = User.GetAccountId();
        if (accountId == 0)
        {
            return Unauthorized(new { error = "登录状态无效" });
        }

        if (afterSeq < 0)
        {
            return BadRequest(new { error = "afterSeq 不能为负数" });
        }

        try
        {
            return Ok(await _chat.GetMessagesAsync(id, accountId, User.GetRole(), afterSeq, limit, ct));
        }
        catch (ChatException ex)
        {
            return Error(ex);
        }
    }

    /// <summary>关闭会话，仅会话双方可操作。</summary>
    [HttpPost("{id:int}/close")]
    public async Task<ActionResult<ConversationDto>> Close(int id, CancellationToken ct)
    {
        var accountId = User.GetAccountId();
        if (accountId == 0)
        {
            return Unauthorized(new { error = "登录状态无效" });
        }

        try
        {
            var conversation = await _chat.CloseAsync(id, accountId, User.GetRole(), ct);
            await _hub.Clients.Group(ChatGroups.Conversation(id))
                .SendAsync("ConversationClosed", new { conversationId = id, closedAt = DateTimeOffset.UtcNow }, CancellationToken.None);
            _logger.LogInformation("会话 {ConversationId} 已关闭", id);
            return Ok(conversation);
        }
        catch (ChatException ex)
        {
            return Error(ex);
        }
    }

    private ActionResult Error(ChatException ex) => ex.Error switch
    {
        ChatError.BadRequest => BadRequest(new { error = ex.Message }),
        ChatError.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message }),
        ChatError.NotFound => NotFound(new { error = ex.Message }),
        ChatError.Conflict => Conflict(new { error = ex.Message }),
        _ => StatusCode(StatusCodes.Status500InternalServerError, new { error = ex.Message })
    };
}
