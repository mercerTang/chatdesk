using System.Collections.Concurrent;

namespace ChatServer.Services;

/// <summary>
/// 客服在线状态：按 SignalR 连接计数，最后一个连接断开才算离线。
/// 单实例部署，进程内字典即权威；账号表的 IsOnline 只是便于查询的落地标记。
/// </summary>
public class AgentPresenceService
{
    private readonly ConcurrentDictionary<int, int> _connections = new();

    /// <summary>增加一个连接，返回是否由离线变为在线。</summary>
    public bool Connect(int agentId)
    {
        var count = _connections.AddOrUpdate(agentId, 1, (_, current) => current + 1);
        return count == 1;
    }

    /// <summary>减少一个连接，返回是否由在线变为离线。</summary>
    public bool Disconnect(int agentId)
    {
        while (true)
        {
            if (!_connections.TryGetValue(agentId, out var current))
            {
                return false;
            }

            if (current <= 1)
            {
                if (_connections.TryRemove(agentId, out _))
                {
                    return true;
                }

                continue;
            }

            if (_connections.TryUpdate(agentId, current - 1, current))
            {
                return false;
            }
        }
    }

    /// <summary>进程重启后清空残留连接计数。</summary>
    public void Reset() => _connections.Clear();
}
