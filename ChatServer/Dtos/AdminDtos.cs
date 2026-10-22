namespace ChatServer.Dtos;

/// <summary>中台客服账号行：在线、活跃会话数、上限、启用状态与创建时间。</summary>
public record AdminAgentDto(
    int Id,
    string Username,
    string DisplayName,
    bool IsOnline,
    bool IsActive,
    int ActiveConversationCount,
    int MaxConcurrentConversations,
    DateTimeOffset CreatedAt);

/// <summary>新增客服请求：登录名、展示名、初始密码、并发上限（缺省 5）。</summary>
public record AdminCreateAgentRequest(
    string? Username,
    string? DisplayName,
    string? Password,
    int? MaxConcurrentConversations);

/// <summary>改客服资料：四个字段均可选，缺省表示不改。</summary>
public record AdminUpdateAgentRequest(
    string? DisplayName,
    int? MaxConcurrentConversations,
    bool? IsActive,
    string? Password);

/// <summary>中台会话行：管理员是旁观者，因此不携带「未读数」。</summary>
public record AdminConversationDto(
    int Id,
    string Status,
    int VisitorId,
    string VisitorName,
    int AgentId,
    string AgentName,
    long LastMessageSeq,
    string? LastMessage,
    DateTimeOffset? LastMessageAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt);

/// <summary>
/// 仪表盘概览：窗口内的会话、消息、工单、转派与首响；在线客服是实时值，不落窗。
/// 首响没有样本时 <see cref="AvgFirstResponseSeconds"/> 为空，样本数单独给出。
/// </summary>
public record AdminStatsOverviewDto(
    int ConversationCount,
    int MessageCount,
    int TicketCount,
    int TransferCount,
    int OnlineAgentCount,
    int OpenTicketCount,
    double? AvgFirstResponseSeconds,
    int FirstResponseSamples);

/// <summary>按日趋势的一行：该日新会话、消息、工单数；无数据日为 0 而不是缺行。</summary>
public record AdminStatsDailyDto(
    DateOnly Date,
    int Conversations,
    int Messages,
    int Tickets);

/// <summary>按客服维度的一行：窗口内接待会话数、本人发出消息数、首响，活跃会话数是当前值。</summary>
public record AdminStatsAgentDto(
    int AgentId,
    string DisplayName,
    int ConversationCount,
    int MessageCount,
    double? AvgFirstResponseSeconds,
    int FirstResponseSamples,
    int ActiveConversationCount);

/// <summary>仪表盘统计：概览 + 按日趋势（按日期升序，含 0 值日）+ 按客服维度（接待量倒序）。</summary>
public record AdminStatsDto(
    AdminStatsOverviewDto Overview,
    IReadOnlyList<AdminStatsDailyDto> Daily,
    IReadOnlyList<AdminStatsAgentDto> Agents);
