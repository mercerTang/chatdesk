namespace ChatServer.Services;

/// <summary>业务错误类别，控制器与 Hub 据此映射 HTTP 状态码 / Hub 异常。</summary>
public enum ChatError
{
    BadRequest,
    Forbidden,
    NotFound,
    Conflict
}

/// <summary>可预期的业务异常（非系统故障）。</summary>
public class ChatException : Exception
{
    public ChatException(ChatError error, string message) : base(message)
    {
        Error = error;
    }

    public ChatError Error { get; }
}
