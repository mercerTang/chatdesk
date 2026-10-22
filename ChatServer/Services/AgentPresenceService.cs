namespace ChatServer.Services;

/// <summary>
/// 客服在线状态：按 SignalR 连接记录连接 ID，最后一个连接断开才算离线。
/// 连接 ID 同时用于转派时的组员迁移（把原客服移出、目标客服并入会话组）。
/// 单实例部署，进程内字典即权威；账号表的 IsOnline 只是便于查询的落地标记。
/// </summary>
public class AgentPresenceService
{
    private readonly object _sync = new();
    private readonly Dictionary<int, HashSet<string>> _connections = new();

    /// <summary>增加一个连接，返回是否由离线变为在线。</summary>
    public bool Connect(int agentId, string connectionId)
    {
        lock (_sync)
        {
            if (!_connections.TryGetValue(agentId, out var set))
            {
                set = new HashSet<string>();
                _connections[agentId] = set;
            }

            set.Add(connectionId);
            return set.Count == 1;
        }
    }

    /// <summary>减少一个连接，返回是否由在线变为离线。</summary>
    public bool Disconnect(int agentId, string connectionId)
    {
        lock (_sync)
        {
            if (!_connections.TryGetValue(agentId, out var set))
            {
                return false;
            }

            set.Remove(connectionId);
            if (set.Count > 0)
            {
                return false;
            }

            _connections.Remove(agentId);
            return true;
        }
    }

    /// <summary>某客服当前所有在线连接 ID 的快照。</summary>
    public IReadOnlyList<string> ConnectionsOf(int agentId)
    {
        lock (_sync)
        {
            return _connections.TryGetValue(agentId, out var set)
                ? set.ToArray()
                : Array.Empty<string>();
        }
    }

    /// <summary>进程重启后清空残留连接。</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _connections.Clear();
        }
    }
}
