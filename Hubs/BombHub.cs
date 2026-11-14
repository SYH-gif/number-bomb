using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using NumberBomb.Data;
using NumberBomb.Game;
using NumberBomb.Models;
using NumberBomb.Services;

namespace NumberBomb.Hubs;

/// <summary>
/// 实时通道：建房、进房、开局、猜数字、再来一把。
/// 所有判定都在服务端做，前端只负责画。
/// </summary>
[Authorize]
public sealed class BombHub : Hub
{
    private readonly RoomManager _rooms;
    private readonly Db _db;
    private readonly PresenceService _presence;
    private readonly SocialService _social;

    public BombHub(RoomManager rooms, Db db, PresenceService presence, SocialService social)
    {
        _rooms = rooms;
        _db = db;
        _presence = presence;
        _social = social;
    }

    private int? UserId => int.TryParse(Context.UserIdentifier, out var id) ? id : null;

    private UserRow? Me()
    {
        var id = UserId;
        return id is null ? null : _db.FindById(id.Value);
    }

    public override async Task OnConnectedAsync()
    {
        var id = UserId;
        if (id is not null)
        {
            // 每个人都有自己的信箱组，好友/邮件的推送都往这儿投
            await Groups.AddToGroupAsync(Context.ConnectionId, SocialService.UserKey(id.Value));

            // 从离线变在线才广播，刷新页面这种抖动不打扰别人
            if (_presence.Online(id.Value))
                await _social.ToFriends(id.Value, "presence", new { id = id.Value, online = true });
        }
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var id = UserId;
        if (id is not null)
        {
            _rooms.Leave(id.Value, Context.ConnectionId);

            // 最后一条连接断了才算下线
            if (_presence.Offline(id.Value))
                await _social.ToFriends(id.Value, "presence", new { id = id.Value, online = false });
        }
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>刷新页面后回来自动找自己的房间</summary>
    public object Resume()
    {
        var id = UserId;
        if (id is null) return new { ok = false };
        var (ok, code) = _rooms.Resume(id.Value);
        return ok ? new { ok = true, code } : new { ok = false };
    }

    public async Task<object> CreateRoom(int capacity, int min, int max)
    {
        var me = Me();
        if (me is null) return new { ok = false, error = "登录状态过期了，刷新一下" };

        var res = _rooms.CreateRoom(me, Context.ConnectionId, capacity, min, max);
        if (!res.Ok) return new { ok = false, error = res.Error };

        await Groups.AddToGroupAsync(Context.ConnectionId, RoomManager.Key(res.Code!));
        _rooms.Nudge(res.Code!);
        return new { ok = true, code = res.Code };
    }

    public async Task<object> JoinRoom(string code)
    {
        var me = Me();
        if (me is null) return new { ok = false, error = "登录状态过期了，刷新一下" };

        var res = _rooms.Join(code, me, Context.ConnectionId);
        if (!res.Ok) return new { ok = false, error = res.Error };

        await Groups.AddToGroupAsync(Context.ConnectionId, RoomManager.Key(res.Code!));
        _rooms.Nudge(res.Code!);
        return new { ok = true, code = res.Code };
    }

    public Task<object> StartGame()
    {
        var id = UserId;
        if (id is null) return Task.FromResult<object>(new { ok = false, error = "登录状态过期了" });

        var (ok, code) = _rooms.Resume(id.Value);
        if (!ok) return Task.FromResult<object>(new { ok = false, error = "这间屋已经散了" });

        var res = _rooms.Start(code!, id.Value);
        return Task.FromResult<object>(new { ok = res.Ok, error = res.Error });
    }

    public Task<object> Guess(int value)
    {
        var id = UserId;
        if (id is null) return Task.FromResult<object>(new { ok = false, error = "登录状态过期了" });

        var (ok, code) = _rooms.Resume(id.Value);
        if (!ok) return Task.FromResult<object>(new { ok = false, error = "这间屋已经散了" });

        var res = _rooms.Guess(code!, id.Value, value);
        return Task.FromResult<object>(new { ok = res.Ok, error = res.Error });
    }

    public Task<object> PlayAgain(int? min, int? max)
    {
        var id = UserId;
        if (id is null) return Task.FromResult<object>(new { ok = false, error = "登录状态过期了" });

        var (ok, code) = _rooms.Resume(id.Value);
        if (!ok) return Task.FromResult<object>(new { ok = false, error = "这间屋已经散了" });

        var res = _rooms.PlayAgain(code!, id.Value, min, max);
        return Task.FromResult<object>(new { ok = res.Ok, error = res.Error });
    }

    /// <summary>轮到你的时候用道具。提示只回给用的人。</summary>
    public Task<object> UseItem(string kind)
    {
        var id = UserId;
        if (id is null) return Task.FromResult<object>(new { ok = false, error = "登录状态过期了" });

        var (ok, code) = _rooms.Resume(id.Value);
        if (!ok) return Task.FromResult<object>(new { ok = false, error = "这间屋已经散了" });

        var r = _rooms.UseItem(code!, id.Value, kind);
        return Task.FromResult<object>(new { ok = r.Ok, error = r.Error, hint = r.Hint });
    }

    public async Task<object> LeaveRoom()
    {
        var id = UserId;
        if (id is null) return new { ok = true };

        var (ok, code) = _rooms.Resume(id.Value);
        _rooms.Leave(id.Value, Context.ConnectionId);
        if (ok && code is not null)
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, RoomManager.Key(code));

        return new { ok = true };
    }

    // ---------------------------------------------------------------- 对话

    /// <summary>私聊：只给好友发，禁言中发不出去</summary>
    public async Task<object> SendChat(int toId, string body)
    {
        var me = Me();
        if (me is null) return new { ok = false, error = "登录状态过期了，刷新一下" };

        var until = _db.MuteUntil(me.Id);
        if (until is not null && until.Value > DateTime.UtcNow)
            return new { ok = false, error = $"你被禁言到 {until.Value.ToLocalTime():HH:mm}，先安静一会儿" };

        body = (body ?? "").Trim();
        if (body.Length == 0) return new { ok = false, error = "说点什么吧" };
        if (body.Length > 500) body = body[..500];

        var peer = _db.FindById(toId);
        if (peer is null) return new { ok = false, error = "这个人不在了" };
        if (!_db.AreFriends(me.Id, toId)) return new { ok = false, error = "先加好友才能聊" };

        var id = _db.SendChat(me.Id, toId, body);
        var role = Roles.Effective(me.Username, _db.Role(me.Id));
        var msg = new
        {
            id,
            fromId = me.Id,
            fromName = me.Username,
            role,
            badges = Roles.BadgesFor(me.Username, role),
            avatar = me.Avatar,
            isCustomAvatar = me.Avatar == AvatarPresets.Custom,
            toId,
            body,
            at = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
            mine = false
        };

        await _social.ToUser(toId, "chat", msg);
        return new { ok = true, message = msg, unread = _db.UnreadChats(toId) };
    }

    /// <summary>大厅公屏：所有在线的人都能看见</summary>
    public async Task<object> SendLobby(string body)
    {
        var me = Me();
        if (me is null) return new { ok = false, error = "登录状态过期了，刷新一下" };

        var until = _db.MuteUntil(me.Id);
        if (until is not null && until.Value > DateTime.UtcNow)
            return new { ok = false, error = $"你被禁言到 {until.Value.ToLocalTime():HH:mm}，先安静一会儿" };

        body = (body ?? "").Trim();
        if (body.Length == 0) return new { ok = false, error = "说点什么吧" };
        if (body.Length > 300) body = body[..300];

        var id = _db.SendChat(me.Id, 0, body);
        var role = Roles.Effective(me.Username, _db.Role(me.Id));
        var msg = new
        {
            id,
            fromId = me.Id,
            fromName = me.Username,
            role,
            badges = Roles.BadgesFor(me.Username, role),
            avatar = me.Avatar,
            isCustomAvatar = me.Avatar == AvatarPresets.Custom,
            toId = 0,
            body,
            at = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
            mine = false
        };

        await Clients.All.SendAsync("lobbyChat", msg);
        return new { ok = true, message = msg };
    }

    /// <summary>「对方正在输入」——只转发，不落库</summary>
    public Task Typing(int toId)
    {
        var id = UserId;
        if (id is null) return Task.CompletedTask;
        return Clients.Group(SocialService.UserKey(toId)).SendAsync("typing", new { fromId = id.Value });
    }
}
