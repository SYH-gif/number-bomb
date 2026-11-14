using System.Collections.Concurrent;

namespace NumberBomb.Services;

/// <summary>
/// 谁在线。按账号记连接数——同一个人可能开着电脑又开着手机，
/// 只有最后一条连接断了才算下线。
/// </summary>
public sealed class PresenceService
{
    private readonly ConcurrentDictionary<int, int> _conns = new();

    /// <summary>加一条连接。返回 true 表示这个人刚上线（之前是离线）。</summary>
    public bool Online(int userId) => _conns.AddOrUpdate(userId, 1, (_, v) => v + 1) == 1;

    /// <summary>断一条连接。返回 true 表示这个人已经全下线了。</summary>
    public bool Offline(int userId)
    {
        while (true)
        {
            if (!_conns.TryGetValue(userId, out var cur)) return false;

            if (cur <= 1)
            {
                if (_conns.TryRemove(new KeyValuePair<int, int>(userId, cur))) return true;
                continue;   // 被别人抢先改了，重来一次
            }

            if (_conns.TryUpdate(userId, cur - 1, cur)) return false;
        }
    }

    public bool IsOnline(int userId) => _conns.ContainsKey(userId);

    public int OnlineCount => _conns.Count;

    /// <summary>现在在线的账号 id（管理面板用）</summary>
    public IReadOnlyCollection<int> OnlineIds => _conns.Keys.ToArray();
}
