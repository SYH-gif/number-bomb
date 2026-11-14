using Microsoft.AspNetCore.SignalR;
using NumberBomb.Data;
using NumberBomb.Hubs;

namespace NumberBomb.Services;

/// <summary>
/// 好友和邮件的实时小喇叭。推给「某个人」的消息统一走 user:{id} 这个组，
/// 这样同一个人开着电脑又开着手机，两边都能收到。
/// </summary>
public sealed class SocialService
{
    private readonly IHubContext<BombHub> _hub;
    private readonly Db _db;

    public SocialService(IHubContext<BombHub> hub, Db db)
    {
        _hub = hub;
        _db = db;
    }

    public static string UserKey(int userId) => "user:" + userId;

    public Task ToUser(int userId, string evt, object payload)
        => _hub.Clients.Group(UserKey(userId)).SendAsync(evt, payload);

    /// <summary>把一件事告诉这个人的所有好友（不在线的收不到，上线时会自己刷新）</summary>
    public async Task ToFriends(int userId, string evt, object payload)
    {
        foreach (var fid in _db.FriendIds(userId))
            await _hub.Clients.Group(UserKey(fid)).SendAsync(evt, payload);
    }

    /// <summary>全站广播（公告、维护通知）</summary>
    public Task ToAll(string evt, object payload)
        => _hub.Clients.All.SendAsync(evt, payload);
}
