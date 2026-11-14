using Microsoft.AspNetCore.SignalR;
using NumberBomb.Data;
using NumberBomb.Hubs;
using NumberBomb.Models;
using NumberBomb.Services;

namespace NumberBomb.Game;

/// <summary>用道具的结果。Hint 只回给用的人，别人看不到。</summary>
public sealed record ItemResult(bool Ok, string? Error = null, string? Hint = null);

public sealed record RoomResult(bool Ok, string? Error = null, string? Code = null)
{
    public static RoomResult Fail(string message) => new(false, message);
    public static RoomResult Done(string? code = null) => new(true, null, code);
}

/// <summary>后台房间列表用的小快照（不含炸弹数字）</summary>
public sealed record AdminRoomView(
    string Code,
    string State,
    int Capacity,
    int Humans,
    int Bots,
    int Round,
    List<AdminPlayerView> Players);

public sealed record AdminPlayerView(string Name, int Points, bool IsBot, bool IsOut);

/// <summary>
/// 所有房间都住在这里。房间状态是唯一真相，前端只是画出来。
/// 用一把全局锁串行化所有改动 —— 朋友局这点并发量，简单可靠比花哨更重要。
/// </summary>
public sealed class RoomManager
{
    public const int MaxPlayers = 8;
    public const int MaxSpan = 10_000;
    public static readonly TimeSpan WaitForHumans = TimeSpan.FromSeconds(60);

    private static readonly string[] BotNames = { "小机灵鬼", "阿呆", "铁蛋", "喵喵子", "布丁" };
    private static readonly string[] BotFaces = { "preset:🤖", "preset:🐣", "preset:🐙", "preset:🦊" };

    private readonly object _sync = new();
    private readonly Dictionary<string, BombRoom> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly IHubContext<BombHub> _hub;
    private readonly Db _db;
    private readonly ILogger<RoomManager> _log;

    public RoomManager(IHubContext<BombHub> hub, Db db, ILogger<RoomManager> log)
    {
        _hub = hub;
        _db = db;
        _log = log;
    }

    public static string Key(string code) => "room:" + code;

    /// <summary>让房间里所有人都收一次最新状态（新同学进组后补发用）</summary>
    public void Nudge(string code) => Broadcast(code);

    // ---------------------------------------------------------------- 建房 / 进房

    public RoomResult CreateRoom(UserRow user, string? connectionId, int capacity, int min, int max)
    {
        capacity = Math.Clamp(capacity, 2, MaxPlayers);

        if (max < min) (min, max) = (max, min);
        if (min < 0) return RoomResult.Fail("从 0 开始数比较舒服");
        if (max <= min) return RoomResult.Fail("两个数得不一样哦");
        if (max - min > MaxSpan) return RoomResult.Fail("范围太大啦，收一收（最多一万个数）");

        foreach (var old in LeaveAllExcept(user.Id, null)) Broadcast(old);
        BombRoom room;
        lock (_sync)
        {
            var code = NewCodeLocked();
            room = new BombRoom
            {
                Code = code,
                Capacity = capacity,
                HostUserId = user.Id,
                Min = min,
                Max = max,
                Low = min,
                High = max,
                WaitUntil = DateTime.UtcNow.Add(WaitForHumans)
            };
            room.Players.Add(new RoomPlayer
            {
                UserId = user.Id,
                Name = user.Username,
                Role = Roles.Effective(user.Username, _db.Role(user.Id)),
                Avatar = user.Avatar,
                Points = user.Points,
                Coins = user.Coins,
                ConnectionId = connectionId
            });
            _rooms[code] = room;
            _log.LogInformation("房间 {Code} 建好了，房主 {User}，目标 {Cap} 人，范围 {Min}-{Max}",
                code, user.Username, capacity, min, max);
        }

        ArmWaitTimer(room);
        Broadcast(room.Code);
        return RoomResult.Done(room.Code);
    }

    public RoomResult Join(string code, UserRow user, string? connectionId)
    {
        code = (code ?? "").Trim();
        if (code.Length is < 3 or > 8) return RoomResult.Fail("房间号是 4 个数字哦");

        BombRoom? room;
        lock (_sync)
        {
            if (!_rooms.TryGetValue(code, out room)) return RoomResult.Fail("没找到这间屋子，号码对不对？");

            var mine = room.Players.FirstOrDefault(p => p.UserId == user.Id);
            if (mine is not null)
            {
                // 断线/刷新后回来，重新绑定连接就行
                mine.ConnectionId = connectionId;
                mine.Points = user.Points;
                mine.Coins = user.Coins;
            }
            else
            {
                if (room.State != RoomState.Waiting) return RoomResult.Fail("这局已经开始了，等下一把吧");
                if (room.Players.Count >= room.Capacity) return RoomResult.Fail("这间坐满啦，换一间？");
                room.Players.Add(new RoomPlayer
                {
                    UserId = user.Id,
                    Name = user.Username,
                    Role = Roles.Effective(user.Username, _db.Role(user.Id)),
                    Avatar = user.Avatar,
                    Points = user.Points,
                    Coins = user.Coins,
                    ConnectionId = connectionId
                });
            }
        }

        foreach (var old in LeaveAllExcept(user.Id, code)) Broadcast(old);
        Broadcast(room!.Code);

        if (room.Players.Count >= room.Capacity) TryStartWhenFull(room.Code);
        return RoomResult.Done(code);
    }

    public (bool Ok, string? Code) Resume(int userId)
    {
        lock (_sync)
        {
            var room = _rooms.Values.FirstOrDefault(r => r.Players.Any(p => p.UserId == userId));
            return room is null ? (false, null) : (true, room.Code);
        }
    }

    // ---------------------------------------------------------------- 开局

    public RoomResult Start(string code, int userId)
    {
        string? error = null;
        lock (_sync)
        {
            if (!_rooms.TryGetValue(code, out var room)) return RoomResult.Fail("这间屋已经散了");
            if (room.State != RoomState.Waiting) error = "已经开始了";
            else if (room.HostUserId != userId) error = "等房主点开始哦";
            else if (room.Players.Count < 2) error = "至少两个人才好玩";
            else StartLocked(room);
        }

        if (error is not null) return RoomResult.Fail(error);
        Broadcast(code);
        MaybeScheduleBot(code);
        return RoomResult.Done(code);
    }

    private void TryStartWhenFull(string code)
    {
        lock (_sync)
        {
            if (!_rooms.TryGetValue(code, out var room)) return;
            if (room.State != RoomState.Waiting) return;
            if (room.Players.Count < room.Capacity) return;
            StartLocked(room);
        }
        Broadcast(code);
        MaybeScheduleBot(code);
    }

    private void StartLocked(BombRoom room)
    {
        room.State = RoomState.Playing;
        room.Bomb = Random.Shared.Next(room.Min, room.Max + 1);
        room.Low = room.Min;
        room.High = room.Max;
        room.Guesses.Clear();
        room.Deltas.Clear();
        room.LoserId = null;
        room.Round++;
        foreach (var p in room.Players)
        {
            p.IsOut = false;
            p.GoodGuesses = 0;
            p.GuessCount = 0;
            p.Shielded = false;
            p.UsedItems.Clear();
        }
        room.TurnIndex = Random.Shared.Next(room.Players.Count);
        try { room.WaitCts.Cancel(); } catch { /* 已经结束了 */ }

        _log.LogInformation("房间 {Code} 第 {Round} 局开始，范围 {Min}-{Max}，{Count} 人",
            room.Code, room.Round, room.Min, room.Max, room.Players.Count);
    }

    // ---------------------------------------------------------------- 猜数字

    public RoomResult Guess(string code, int userId, int value)
    {
        List<PointDelta>? pending = null;
        bool ended = false;

        lock (_sync)
        {
            if (!_rooms.TryGetValue(code, out var room)) return RoomResult.Fail("这局已经散啦");
            if (room.State != RoomState.Playing) return RoomResult.Fail("现在还不能猜哦");

            var current = room.Current;
            if (current is null) return RoomResult.Fail("人都走光啦");
            if (current.UserId != userId) return RoomResult.Fail($"轮到 {current.Name} 啦");
            if (value < room.Low || value > room.High)
                return RoomResult.Fail($"要在 {room.Low} ~ {room.High} 之间哦");

            ended = ApplyGuessLocked(room, current, value);
            if (ended) pending = room.Deltas.ToList();
        }

        if (pending is not null) PersistPoints(pending);
        Broadcast(code);
        if (!ended) MaybeScheduleBot(code);
        return RoomResult.Done(code);
    }

    /// <summary>返回 true 表示这一局踩到炸弹结束了</summary>
    private bool ApplyGuessLocked(BombRoom room, RoomPlayer player, int value)
    {
        player.GuessCount++;

        if (value == room.Bomb)
        {
            room.Guesses.Add(new GuessRecord { UserId = player.UserId, Name = player.Name, Value = value, Dir = "boom" });
            EndRoundLocked(room, player);
            return true;
        }

        string dir;
        if (value < room.Bomb)
        {
            room.Low = value + 1;
            dir = "low";
        }
        else
        {
            room.High = value - 1;
            dir = "high";
        }
        player.GoodGuesses++;
        room.Guesses.Add(new GuessRecord { UserId = player.UserId, Name = player.Name, Value = value, Dir = dir });

        AdvanceTurnLocked(room);
        return false;
    }

    private void EndRoundLocked(BombRoom room, RoomPlayer loser)
    {
        room.State = RoomState.Finished;
        room.LoserId = loser.UserId;
        loser.IsOut = true;

        // 赢家：没踩雷的人 +10；缩范围缩得最多的那位再 +5（"按表现"）
        var star = room.Players
            .Where(p => p.UserId != loser.UserId)
            .OrderByDescending(p => p.GoodGuesses)
            .ThenBy(p => p.GuessCount)
            .FirstOrDefault();

        room.Deltas.Clear();
        foreach (var p in room.Players)
        {
            var isLoser = p.UserId == loser.UserId;
            // 戴了免死金牌就不扣分，牌子这一把用完就没了
            var delta = isLoser ? (p.Shielded ? 0 : -5) : 10;
            if (star is not null && p.UserId == star.UserId && p.GoodGuesses > 0) delta += 5;

            // 踩雷的这位扣金币（1~100 随机），赢的人照旧 +30
            var coins = isLoser ? -Shop.RollCoinsLost() : Shop.CoinsForWin;
            p.Coins = Math.Max(0, p.Coins + coins);
            p.Points = Math.Max(0, p.Points + delta);
            room.Deltas.Add(new PointDelta(p.UserId, delta, p.IsBot, coins));
        }
    }

    private void AdvanceTurnLocked(BombRoom room)
    {
        if (room.Players.Count == 0) return;
        for (var step = 1; step <= room.Players.Count; step++)
        {
            var idx = (room.TurnIndex + step) % room.Players.Count;
            if (!room.Players[idx].IsOut)
            {
                room.TurnIndex = idx;
                return;
            }
        }
    }

    private void PersistPoints(List<PointDelta> deltas)
    {
        try
        {
            _db.ApplyDeltas(deltas.Where(d => !d.IsBot).Select(d => (d.UserId, d.Delta, d.Coins)));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "积分落库失败");
        }
    }

    // ---------------------------------------------------------------- 再来一把

    public RoomResult PlayAgain(string code, int userId, int? min, int? max)
    {
        string? error = null;
        lock (_sync)
        {
            if (!_rooms.TryGetValue(code, out var room)) return RoomResult.Fail("这间屋已经散了");
            if (room.HostUserId != userId) error = "等房主开下一把哦";
            else if (room.State != RoomState.Finished) error = "这局还没结束";
            else
            {
                if (min.HasValue && max.HasValue)
                {
                    var lo = Math.Min(min.Value, max.Value);
                    var hi = Math.Max(min.Value, max.Value);
                    if (lo < 0) error = "从 0 开始数比较舒服";
                    else if (hi - lo > MaxSpan) error = "范围太大啦，收一收";
                    else if (hi - lo < 1) error = "两个数得不一样哦";
                    else { room.Min = lo; room.Max = hi; }
                }

                if (error is null)
                {
                    // 把中途走掉的人清出去
                    room.Players.RemoveAll(p => !p.IsBot && p.ConnectionId is null);
                    if (room.Players.Count < 2) error = "人不够啦，回大厅再凑一桌？";
                    else StartLocked(room);
                }
            }
        }

        if (error is not null) return RoomResult.Fail(error);
        Broadcast(code);
        MaybeScheduleBot(code);
        return RoomResult.Done(code);
    }

    // ---------------------------------------------------------------- 道具

    /// <summary>
    /// 轮到你的时候用道具。判定和扣货都在锁里做，扣不到货就当没用过。
    /// Hint 只回给用的人，别人收到的是一句"谁用了什么"。
    /// </summary>
    public ItemResult UseItem(string code, int userId, string kind)
    {
        var item = Shop.Find(kind);
        if (item is null) return new ItemResult(false, "没这个道具");

        ItemResult result;
        string who;

        lock (_sync)
        {
            if (!_rooms.TryGetValue(code, out var room)) return new ItemResult(false, "这局已经散啦");
            if (room.State != RoomState.Playing) return new ItemResult(false, "现在还用不上道具");

            var p = room.Players.FirstOrDefault(x => x.UserId == userId);
            if (p is null) return new ItemResult(false, "你不在这桌上");
            if (p.IsOut) return new ItemResult(false, "这一把已经结束了");
            if (room.Current?.UserId != userId) return new ItemResult(false, "等轮到你再用");
            if (p.UsedItems.Contains(item.Kind)) return new ItemResult(false, "这一把已经用过了");

            if (!_db.TakeItem(userId, item.Kind))
                return new ItemResult(false, "包里没有这个道具，去商店带一个？");

            p.UsedItems.Add(item.Kind);
            string? hint = null;

            switch (item.Kind)
            {
                case "shield":
                    p.Shielded = true;
                    break;

                case "scan":
                    var mid = (room.Low + room.High) / 2;
                    hint = room.Bomb <= mid
                        ? $"炸弹在 {room.Low} ~ {mid} 这半边"
                        : $"炸弹在 {mid + 1} ~ {room.High} 这半边";
                    break;

                case "skip":
                    room.Guesses.Add(new GuessRecord { UserId = userId, Name = p.Name, Value = 0, Dir = "skip" });
                    AdvanceTurnLocked(room);
                    break;
            }

            who = p.Name;
            result = new ItemResult(true, null, hint);
            _log.LogInformation("{User} 在房间 {Code} 用了 {Item}", who, room.Code, item.Kind);
        }

        Notice(code, $"{who} 用了 {item.Emoji}{item.Name}");
        Broadcast(code);
        MaybeScheduleBot(code);
        return result;
    }

    // ---------------------------------------------------------------- 后台用

    /// <summary>把某个人从所有房间里请出去（后台删号时用）。</summary>
    public void RemoveUser(int userId)
    {
        var codes = new List<string>();
        lock (_sync)
        {
            foreach (var room in _rooms.Values.ToList())
            {
                var player = room.Players.FirstOrDefault(p => p.UserId == userId);
                if (player is null) continue;

                RemovePlayerLocked(room, player);
                if (room.Players.Count == 0)
                {
                    try { room.WaitCts.Cancel(); } catch { }
                    _rooms.Remove(room.Code);
                }
                else
                {
                    codes.Add(room.Code);
                }
            }
        }
        foreach (var code in codes)
        {
            Broadcast(code);
            MaybeScheduleBot(code);
        }
    }

    /// <summary>后台看的房间概况：只给人数、状态、玩家名字，炸弹数字不出这个类。</summary>
    public List<AdminRoomView> AdminRooms()
    {
        lock (_sync)
        {
            return _rooms.Values
                .OrderBy(r => r.Code, StringComparer.OrdinalIgnoreCase)
                .Select(r => new AdminRoomView(
                    r.Code,
                    r.State.ToString().ToLowerInvariant(),
                    r.Capacity,
                    r.Players.Count(p => !p.IsBot),
                    r.Players.Count(p => p.IsBot),
                    r.Round,
                    r.Players.Select(p => new AdminPlayerView(p.Name, p.Points, p.IsBot, p.IsOut)).ToList()))
                .ToList();
        }
    }

    // ---------------------------------------------------------------- 离开 / 掉线

    public void Leave(int userId, string? connectionId)
    {
        string? code = null;
        var changed = false;

        lock (_sync)
        {
            var room = _rooms.Values.FirstOrDefault(r => r.Players.Any(p => p.UserId == userId));
            if (room is null) return;

            var player = room.Players.First(p => p.UserId == userId);
            if (connectionId is not null && player.ConnectionId is not null && player.ConnectionId != connectionId)
                return; // 是旧的连接在说拜拜，忽略

            RemovePlayerLocked(room, player);
            code = room.Code;
            changed = true;

            if (room.Players.Count == 0)
            {
                try { room.WaitCts.Cancel(); } catch { }
                _rooms.Remove(room.Code);
                code = null;
            }
        }

        if (changed && code is not null)
        {
            Broadcast(code);
            MaybeScheduleBot(code);
        }
    }

    /// <summary>管理员把人请出房间。返回房间号；人本来就不在房间里返回 null。</summary>
    public string? Kick(int userId)
    {
        string? code;
        lock (_sync)
        {
            var room = _rooms.Values.FirstOrDefault(r => r.Players.Any(p => p.UserId == userId));
            if (room is null) return null;

            code = room.Code;
            RemovePlayerLocked(room, room.Players.First(p => p.UserId == userId));
            if (room.Players.Count == 0)
            {
                try { room.WaitCts.Cancel(); } catch { }
                _rooms.Remove(code);
            }
        }

        // 房间空了也要广播一次：被踢的人还挂在房间组里，得让他回大厅
        Broadcast(code);
        MaybeScheduleBot(code);
        return code;
    }

    /// <summary>解散房间：所有真人都会被送回大厅。返回房间里的用户 id。</summary>
    public List<int> CloseRoom(string code)
    {
        List<int> people;
        lock (_sync)
        {
            if (!_rooms.TryGetValue(code, out var room)) return new List<int>();
            people = room.Players.Where(p => !p.IsBot).Select(p => p.UserId).ToList();
            try { room.WaitCts.Cancel(); } catch { }
            _rooms.Remove(code);
        }
        Broadcast(code);
        return people;
    }

    /// <summary>玩家改名：牌桌上的名字跟着换，不用重开一局。</summary>
    public void Rename(int userId, string name)
    {
        string? code = null;
        lock (_sync)
        {
            var room = _rooms.Values.FirstOrDefault(r => r.Players.Any(p => p.UserId == userId));
            if (room is null) return;
            var player = room.Players.First(p => p.UserId == userId);
            if (player.Name == name) return;
            player.Name = name;
            code = room.Code;
        }
        if (code is not null) Broadcast(code);
    }

    /// <summary>清号之后把残留的人从牌桌上抹掉（只留 keep 里的人）。</summary>
    public List<int> EjectAllExcept(IReadOnlyCollection<int> keep)
    {
        var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gone = new List<int>();
        lock (_sync)
        {
            foreach (var room in _rooms.Values.ToList())
            {
                var leftovers = room.Players
                    .Where(p => !p.IsBot && !keep.Contains(p.UserId))
                    .ToList();
                if (leftovers.Count == 0) continue;

                foreach (var p in leftovers)
                {
                    gone.Add(p.UserId);
                    RemovePlayerLocked(room, p);
                }

                if (room.Players.Count == 0)
                {
                    try { room.WaitCts.Cancel(); } catch { }
                    _rooms.Remove(room.Code);
                }
                else affected.Add(room.Code);
            }
        }

        foreach (var code in affected) Broadcast(code);
        return gone;
    }

    private List<string> LeaveAllExcept(int userId, string? keepCode)
    {
        var affected = new List<string>();
        lock (_sync)
        {
            var rooms = _rooms.Values
                .Where(r => !string.Equals(r.Code, keepCode, StringComparison.OrdinalIgnoreCase))
                .Where(r => r.Players.Any(p => p.UserId == userId))
                .ToList();
            foreach (var room in rooms)
            {
                RemovePlayerLocked(room, room.Players.First(p => p.UserId == userId));
                if (room.Players.Count == 0) _rooms.Remove(room.Code);
                else affected.Add(room.Code);
            }
        }
        return affected;
    }

    private void RemovePlayerLocked(BombRoom room, RoomPlayer player)
    {
        var idx = room.Players.IndexOf(player);
        if (idx < 0) return;

        var wasTurn = room.State == RoomState.Playing && room.TurnIndex == idx;
        room.Players.RemoveAt(idx);

        if (room.TurnIndex > idx) room.TurnIndex--;
        if (room.TurnIndex >= room.Players.Count) room.TurnIndex = 0;

        if (room.HostUserId == player.UserId && room.Players.Count > 0)
            room.HostUserId = room.Players.FirstOrDefault(p => !p.IsBot)?.UserId ?? room.Players[0].UserId;

        if (room.State == RoomState.Playing)
        {
            var humansLeft = room.Players.Count(p => !p.IsBot && p.ConnectionId is not null);
            if (room.Players.Count < 2 || humansLeft == 0)
            {
                room.State = RoomState.Finished;
                room.LoserId = null;
                room.Deltas.Clear();
                return;
            }
            if (wasTurn) AdvanceTurnLocked(room);
        }
    }

    // ---------------------------------------------------------------- 一分钟等不到人 → 机器人

    private string NewCodeLocked()
    {
        for (var i = 0; i < 200; i++)
        {
            var code = Random.Shared.Next(1000, 10000).ToString();
            if (!_rooms.ContainsKey(code)) return code;
        }
        return Random.Shared.Next(1000, 10000).ToString();
    }

    private void ArmWaitTimer(BombRoom room)
    {
        var token = room.WaitCts.Token;
        var code = room.Code;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(WaitForHumans, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            OnWaitTimeout(code);
        });
    }

    private void OnWaitTimeout(string code)
    {
        string? botName = null;
        lock (_sync)
        {
            if (!_rooms.TryGetValue(code, out var room) || room.State != RoomState.Waiting) return;

            if (room.Players.Count < 2)
            {
                botName = AddBotLocked(room);
            }
            StartLocked(room);
        }

        if (botName is not null) Notice(code, $"一分钟了，{botName} 先顶上陪你玩 🤖");
        Broadcast(code);
        MaybeScheduleBot(code);
    }

    private string AddBotLocked(BombRoom room)
    {
        var name = BotNames[Random.Shared.Next(BotNames.Length)];
        if (room.Players.Any(p => p.Name == name))
            name += "二号";

        room.Players.Add(new RoomPlayer
        {
            UserId = -Random.Shared.Next(1000, 999_999),   // 负数 = 机器人，不对应任何账号
            Name = name,
            Avatar = BotFaces[Random.Shared.Next(BotFaces.Length)],
            IsBot = true,
            Points = 100,
            Coins = 0
        });
        room.BotAdded = true;
        return name;
    }

    private void MaybeScheduleBot(string code)
    {
        lock (_sync)
        {
            if (!_rooms.TryGetValue(code, out var room)) return;
            if (room.State != RoomState.Playing) return;
            if (room.Current is not { IsBot: true }) return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(Random.Shared.Next(1100, 2400));
            BotTurn(code);
        });
    }

    private void BotTurn(string code)
    {
        bool ended;
        lock (_sync)
        {
            if (!_rooms.TryGetValue(code, out var room) || room.State != RoomState.Playing) return;
            if (room.Current is not { IsBot: true } bot) return;

            var guess = BotPick(room, bot);
            ended = ApplyGuessLocked(room, bot, guess);
        }

        Broadcast(code);
        if (!ended) MaybeScheduleBot(code);
    }

    /// <summary>机器人不知道炸弹在哪儿，只按"中间偏一点"来猜，偶尔手抖猜偏。</summary>
    private static int BotPick(BombRoom room, RoomPlayer bot)
    {
        var low = room.Low;
        var high = room.High;
        if (low >= high) return low;

        var span = high - low;
        var roll = Random.Shared.NextDouble();
        int guess;

        if (roll < 0.18) guess = low + Random.Shared.Next(0, Math.Max(1, span / 4 + 1));
        else if (roll < 0.36) guess = high - Random.Shared.Next(0, Math.Max(1, span / 4 + 1));
        else guess = (low + high) / 2 + Random.Shared.Next(-span / 8, span / 8 + 1);

        var last = room.Guesses.LastOrDefault(g => g.UserId == bot.UserId);
        if (last is not null && last.Value == guess)
            guess = guess < high ? guess + 1 : guess - 1;

        return Math.Clamp(guess, low, high);
    }

    // ---------------------------------------------------------------- 广播

    private void Broadcast(string code)
    {
        object? snapshot = null;
        lock (_sync)
        {
            if (_rooms.TryGetValue(code, out var room)) snapshot = Snapshot(room);
        }
        if (snapshot is not null)
            _ = _hub.Clients.Group(Key(code)).SendAsync("state", snapshot);
    }

    private void Notice(string code, string text)
        => _ = _hub.Clients.Group(Key(code)).SendAsync("notice", text);

    private object Snapshot(BombRoom room)
    {
        var waitLeft = room.State == RoomState.Waiting
            ? Math.Max(0, (int)Math.Ceiling((room.WaitUntil - DateTime.UtcNow).TotalSeconds))
            : 0;

        return new
        {
            code = room.Code,
            state = room.State.ToString().ToLowerInvariant(),
            capacity = room.Capacity,
            hostId = room.HostUserId,
            min = room.Min,
            max = room.Max,
            low = room.Low,
            high = room.High,
            turnUserId = room.State == RoomState.Playing && room.Current is not null ? room.Current.UserId : (int?)null,
            waitLeft,
            round = room.Round,
            players = room.Players.Select(p => new
            {
                id = p.UserId,
                name = p.Name,
                badges = Roles.BadgesFor(p.Name, p.Role),
                avatar = p.Avatar,
                points = p.Points,
                coins = p.Coins,
                shielded = p.Shielded,
                items = p.UsedItems.ToArray(),
                isBot = p.IsBot,
                isOut = p.IsOut,
                isHost = p.UserId == room.HostUserId,
                good = p.GoodGuesses
            }).ToArray(),
            guesses = room.Guesses.Select(g => new
            {
                id = g.UserId,
                name = g.Name,
                value = g.Value,
                dir = g.Dir
            }).ToArray(),
            bomb = room.State == RoomState.Finished ? room.Bomb : (int?)null,
            loserId = room.LoserId,
            deltas = room.Deltas.Select(d => new { id = d.UserId, delta = d.Delta, coins = d.Coins, isBot = d.IsBot }).ToArray(),
            canStart = room.State == RoomState.Waiting && room.Players.Count >= 2,
            botJoined = room.BotAdded
        };
    }
}
