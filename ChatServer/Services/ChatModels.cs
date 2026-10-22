namespace ChatServer.Services;

/// <summary>消息落库结果：IsNew=false 表示命中幂等键，未新增记录。</summary>
public record AppendResult(Dtos.MessageDto Message, bool IsNew);

/// <summary>创建会话结果：Created=false 表示复用了进行中的会话。</summary>
public record CreateConversationResult(Dtos.ConversationDto Conversation, bool Created);
