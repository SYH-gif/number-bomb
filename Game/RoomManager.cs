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

    public RoomResult CreateRoom(UserRow user, string? connectionId, int capacity, int min, int max, string gameType = "bomb")
    {
        capacity = Math.Clamp(capacity, 2, MaxPlayers);
        gameType = (gameType ?? "bomb").Trim().ToLowerInvariant();
        if (gameType is not ("bomb" or "draw" or "snake")) gameType = "bomb";

        if (gameType == "bomb")
        {
            if (max < min) (min, max) = (max, min);
            if (min < 0) return RoomResult.Fail("从 0 开始数比较舒服");
            if (max <= min) return RoomResult.Fail("两个数得不一样哦");
            if (max - min > MaxSpan) return RoomResult.Fail("范围太大啦，收一收（最多一万个数）");
        }

        foreach (var old in LeaveAllExcept(user.Id, null)) Broadcast(old);
        BombRoom room;
        lock (_sync)
        {
            var code = NewCodeLocked();
            room = new BombRoom
            {
                Code = code,
                GameType = gameType,
                Capacity = capacity,
                HostUserId = user.Id,
                Min = gameType == "bomb" ? min : 0,
                Max = gameType == "bomb" ? max : 100,
                Low = gameType == "bomb" ? min : 0,
                High = gameType == "bomb" ? max : 100,
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
        room.Round++;
        room.Deltas.Clear();
        room.LoserId = null;
        room.BotAdded = false;
        foreach (var p in room.Players)
        {
            p.IsOut = false;
            p.GoodGuesses = 0;
            p.GuessCount = 0;
            p.Shielded = false;
            p.UsedItems.Clear();
        }
        CancelTimersLocked(room);

        switch (room.GameType)
        {
            case "draw":
                StartDrawLocked(room);
                break;
            case "snake":
                StartSnakeLocked(room);
                break;
            default:
                StartBombLocked(room);
                break;
        }

        _log.LogInformation("房间 {Code} 第 {Round} 局开始（{Type}），{Count} 人",
            room.Code, room.Round, room.GameType, room.Players.Count);
    }

    private void StartBombLocked(BombRoom room)
    {
        room.Bomb = Random.Shared.Next(room.Min, room.Max + 1);
        room.Low = room.Min;
        room.High = room.Max;
        room.Guesses.Clear();
        room.TurnIndex = Random.Shared.Next(room.Players.Count);
    }

    // ---------------------------------------------------------------- 你画我猜

    private const int DrawChooseSeconds = 15;
    private const int DrawSeconds = 80;
    private const int DrawBetweenSeconds = 4;

    private void StartDrawLocked(BombRoom room)
    {
        room.Draw = new DrawState
        {
            DrawerIdx = Random.Shared.Next(room.Players.Count)
        };
        room.Draw.DrawerPid = room.Players[room.Draw.DrawerIdx].UserId;
        BeginChoosingLocked(room);
    }

    /// <summary>进入"画手挑词"阶段，15 秒不挑就随机选一个</summary>
    private void BeginChoosingLocked(BombRoom room)
    {
        var draw = room.Draw!;
        draw.Phase = "choosing";
        draw.Choices.Clear();
        draw.Choices.AddRange(DrawWords.Pick(3));
        draw.Word = "";
        draw.GuessedIds.Clear();
        draw.Strokes.Clear();
        draw.PhaseUntil = DateTime.UtcNow.AddSeconds(DrawChooseSeconds);

        ArmDrawTimer(room.Code, DrawChooseSeconds);
    }

    private void BeginDrawingLocked(BombRoom room, string word)
    {
        var draw = room.Draw!;
        draw.Phase = "drawing";
        draw.Word = word;
        draw.Choices.Clear();
        draw.GuessedIds.Clear();
        draw.Strokes.Clear();
        draw.PhaseUntil = DateTime.UtcNow.AddSeconds(DrawSeconds);

        ArmDrawTimer(room.Code, DrawSeconds);
    }

    private void ArmDrawTimer(string code, int seconds)
    {
        BombRoom? room;
        CancellationToken token;
        lock (_sync)
        {
            if (!_rooms.TryGetValue(code, out room) || room.Draw is null) return;
            token = room.Draw.PhaseCts.Token;
        }

        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), token); }
            catch (OperationCanceledException) { return; }
            OnDrawPhaseTimeout(code);
        });
    }

    private void OnDrawPhaseTimeout(string code)
    {
        bool movedOn = false;
        lock (_sync)
        {
            if (!_rooms.TryGetValue(code, out var room) || room.Draw is null || room.State != RoomState.Playing) return;
            var draw = room.Draw;

            // 旧计时器到点了，但阶段早就翻篇（比如画手提前挑了词）。PhaseUntil 被推后了就不动。
            if (DateTime.UtcNow < draw.PhaseUntil) return;

            switch (draw.Phase)
            {
                case "choosing":
                    // 挑词超时，随便替他选一个
                    BeginDrawingLocked(room, draw.Choices.Count > 0 ? draw.Choices[0] : DrawWords.PickOne());
                    movedOn = true;
                    break;

                case "drawing":
                    // 画的时间到了，没人全猜对也翻篇
                    EndDrawTurnLocked(room);
                    movedOn = true;
                    break;
            }
        }
        if (movedOn) AfterDrawTurnChanged(code);
    }

    /// <summary>一个词画完（全猜对 / 超时 / 人走了）：算分、翻到下一位或收场</summary>
    private void EndDrawTurnLocked(BombRoom room)
    {
        var draw = room.Draw!;
        var drawer = room.Players.FirstOrDefault(p => p.UserId == draw.DrawerPid);

        // 画手每有一个猜对的 +4 分 +5 金币；猜对的人各 +5 分 +5 金币（猜错 -2）
        var correctCount = draw.GuessedIds.Count;
        if (drawer is not null && correctCount > 0 && !drawer.IsBot)
            draw.AddScore(drawer.UserId, DrawScores.PerGuesser * correctCount, DrawScores.CoinsPerCorrect * correctCount);

        foreach (var gid in draw.GuessedIds)
        {
            draw.AddScore(gid, DrawScores.Correct, DrawScores.CoinsPerCorrect);
        }

        var total = room.Players.Count(p => !p.IsBot);
        if (draw.TurnNo >= total || room.Players.Count < 2)
        {
            FinishDrawLocked(room);
            return;
        }

        draw.TurnNo++;
        // 顺位换下一位画手
        for (var step = 1; step <= room.Players.Count; step++)
        {
            var idx = (draw.DrawerIdx + step) % room.Players.Count;
            if (room.Players[idx].UserId != draw.DrawerPid || step == room.Players.Count)
            {
                draw.DrawerIdx = idx;
                break;
            }
        }
        draw.DrawerPid = room.Players[draw.DrawerIdx].UserId;
        BeginChoosingLocked(room);
    }

    private void FinishDrawLocked(BombRoom room)
    {
        room.State = RoomState.Finished;
        var draw = room.Draw!;
        try { draw.PhaseCts.Cancel(); } catch { }

        var winner = draw.Scores.Count > 0
            ? draw.Scores.OrderByDescending(kv => kv.Value).First().Key
            : (int?)null;
        room.LoserId = null;

        foreach (var p in room.Players)
        {
            var pts = draw.Scores.TryGetValue(p.UserId, out var s) ? s : 0;
            var coins = draw.CoinsEarned.TryGetValue(p.UserId, out var c) ? c : 0;
            p.Points = Math.Max(0, p.Points + pts);
            if (!p.IsBot) p.Coins = Math.Max(0, p.Coins + coins);
            room.Deltas.Add(new PointDelta(p.UserId, pts, p.IsBot, coins));
        }
        room.DrawWinnerId = winner;
        PersistPoints(room.Deltas);
        Broadcast(room.Code);
    }

    /// <summary>画猜轮换后：广播状态，并单独把词/候选词塞给画手本人</summary>
    private void AfterDrawTurnChanged(string code)
    {
        Broadcast(code);
        SendDrawerSecrets(code);
    }

    /// <summary>把当前词（画中）或候选词（挑词）私下发给画手</summary>
    private void SendDrawerSecrets(string code)
    {
        lock (_sync)
        {
            if (!_rooms.TryGetValue(code, out var room) || room.Draw is null) return;
            var draw = room.Draw;
            var drawer = room.Players.FirstOrDefault(pp => pp.UserId == draw.DrawerPid);
            if (drawer is null) return;
            if (drawer.IsBot) return;

            if (draw.Phase == "choosing")
            {
                _ = _hub.Clients.Group(SocialService.UserKey(drawer.UserId))
                    .SendAsync("drawChoices", new { words = draw.Choices.ToArray() });
            }
            else if (draw.Phase == "drawing")
            {
                _ = _hub.Clients.Group(SocialService.UserKey(drawer.UserId))
                    .SendAsync("drawWord", new { word = draw.Word });
            }
        }
    }

    /// <summary>画手挑词。必须是当前画手本人。</summary>
    public RoomResult ChooseWord(string code, int userId, int idx)
    {
        bool began = false;
        lock (_sync)
        {
            if (!_rooms.TryGetValue(code, out var room) || room.Draw is null) return RoomResult.Fail("这局已经散啦");
            if (room.State != RoomState.Playing) return RoomResult.Fail("现在不是挑词的时候");
            var draw = room.Draw;
            var drawer = room.Players.FirstOrDefault(pp => pp.UserId == draw.DrawerPid);
            if (drawer is null) return RoomResult.Fail("画手不在了，等下一轮");
            if (drawer.UserId != userId) return RoomResult.Fail("现在不是你画哦");
            if (draw.Phase != "choosing") return RoomResult.Fail("已经挑过啦");
            if (idx < 0 || idx >= draw.Choices.Count) return RoomResult.Fail("挑一个给出的词哦");

            BeginDrawingLocked(room, draw.Choices[idx]);
            began = true;
        }
        if (began)
        {
            AfterDrawTurnChanged(code);
            // 画手也要马上知道词
            SendDrawerSecrets(code);
        }
        return RoomResult.Done(code);
    }

    /// <summary>画手落笔。一笔上限 240 个点，整幅画最多 500 笔。</summary>
    public RoomResult AddStroke(string code, int userId, int size, string color, int[] points)
    {
        DrawStroke? stroke = null;
        lock (_sync)
        {
            if (!_rooms.TryGetValue(code, out var room) || room.Draw is null) return RoomResult.Fail("这局已经散啦");
            if (room.State != RoomState.Playing) return RoomResult.Fail("现在还不能画");
            var draw = room.Draw;
            var drawer = room.Players.FirstOrDefault(pp => pp.UserId == draw.DrawerPid);
            if (drawer is null) return RoomResult.Fail("画手不在了，等下一轮");
            if (drawer.UserId != userId) return RoomResult.Fail("只有画的人才能动笔哦");
            if (draw.Phase != "drawing") return RoomResult.Fail("还没开始画呢");

            if (points is null || points.Length < 2 || points.Length > 480) return RoomResult.Fail("这一笔有点问题");
            if (draw.Strokes.Count >= 500) return RoomResult.Fail("画得太满了，先清一清");

            stroke = new DrawStroke
            {
                Id = ++draw.NextStrokeId,
                Pid = userId,
                Color = string.IsNullOrWhiteSpace(color) ? "#20242c" : color[..Math.Min(color.Length, 16)],
                Size = Math.Clamp(size, 2, 40),
                Points = points.ToList()
            };
            draw.Strokes.Add(stroke);
        }
        _ = _hub.Clients.Group(Key(code)).SendAsync("drawAdd", new
        {
            sid = stroke!.Id,
            pid = stroke.Pid,
            color = stroke.Color,
            size = stroke.Size,
            pts = stroke.Points
        });
        return RoomResult.Done(code);
    }

    /// <summary>画手撤销最后一笔</summary>
    public RoomResult UndoStroke(string code, int userId)
    {
        int? sid = null;
        lock (_sync)
        {
            if (!_rooms.TryGetValue(code, out var room) || room.Draw is null) return RoomResult.Fail("这局已经散啦");
            var draw = room.Draw;
            var drawer = room.Players.FirstOrDefault(pp => pp.UserId == draw.DrawerPid);
            if (drawer is null) return RoomResult.Fail("画手不在了，等下一轮");
            if (drawer.UserId != userId) return RoomResult.Fail("只有画的人能撤");
            if (draw.Strokes.Count == 0) return RoomResult.Fail("没有可以撤的了");

            var last = draw.Strokes[^1];
            draw.Strokes.RemoveAt(draw.Strokes.Count - 1);
            sid = last.Id;
        }
        _ = _hub.Clients.Group(Key(code)).SendAsync("drawDel", new { sid });
        return RoomResult.Done(code);
    }

    /// <summary>画手清空画布</summary>
    public RoomResult ClearCanvas(string code, int userId)
    {
        lock (_sync)
        {
            if (!_rooms.TryGetValue(code, out var room) || room.Draw is null) return RoomResult.Fail("这局已经散啦");
            var draw = room.Draw;
            var drawer = room.Players.FirstOrDefault(pp => pp.UserId == draw.DrawerPid);
            if (drawer is null) return RoomResult.Fail("画手不在了，等下一轮");
            if (drawer.UserId != userId) return RoomResult.Fail("只有画的人能清");
            draw.Strokes.Clear();
        }
        _ = _hub.Clients.Group(Key(code)).SendAsync("drawClear");
        return RoomResult.Done(code);
    }

    /// <summary>猜词。猜对当场广播，全桌猜对就翻篇。</summary>
    public async Task<RoomResult> GuessWord(string code, int userId, string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return RoomResult.Fail("先写点什么");

        bool correct = false;
        string? guesserName = null;
        bool turnEnded = false;
        string? word = null;

        lock (_sync)
        {
            if (!_rooms.TryGetValue(code, out var room) || room.Draw is null) return RoomResult.Fail("这局已经散啦");
            if (room.State != RoomState.Playing) return RoomResult.Fail("现在不能猜");
            var draw = room.Draw;
            if (draw.Phase != "drawing") return RoomResult.Fail("现在不能猜");

            var drawer = room.Players.FirstOrDefault(pp => pp.UserId == draw.DrawerPid);
            if (drawer is null) return RoomResult.Fail("画手不在了，等下一轮");
            if (drawer.UserId == userId) return RoomResult.Fail("你画你猜，哪有这种好事");
            var me = room.Players.FirstOrDefault(p => p.UserId == userId);
            if (me is null) return RoomResult.Fail("你不在这桌上");
            if (me.IsBot) return RoomResult.Fail("机器人不参与猜词");
            if (draw.GuessedIds.Contains(userId)) return RoomResult.Fail("你已经猜对啦，等别人");

            guesserName = me.Name;
            word = draw.Word;
            if (string.Equals(text.Trim(), draw.Word.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                draw.GuessedIds.Add(userId);
                correct = true;
                var guessers = room.Players.Count(p => !p.IsBot && p.UserId != drawer.UserId);
                if (draw.GuessedIds.Count >= guessers) turnEnded = true;
            }
            else
            {
                // 猜错扣 2 分，别瞎喊
                draw.AddScore(userId, DrawScores.WrongPenalty, 0);
            }
        }

        if (correct)
        {
            await _hub.Clients.Group(Key(code)).SendAsync("drawCorrect", new { pid = userId, name = guesserName });
            if (turnEnded)
            {
                lock (_sync)
                {
                    if (_rooms.TryGetValue(code, out var room) && room.Draw is not null && room.State == RoomState.Playing)
                    {
                        EndDrawTurnLocked(room);
                    }
                }
                AfterDrawTurnChanged(code);
            }
            return RoomResult.Done(code);
        }

        // 猜错了：把这句当成房内闲聊广播，给画手提示别画到答案
        await _hub.Clients.Group(Key(code)).SendAsync("drawWrong", new { pid = userId, name = guesserName, text });
        return RoomResult.Done(code);
    }

    // ---------------------------------------------------------------- 猜数字

    public RoomResult Guess(string code, int userId, int value)
    {
        List<PointDelta>? pending = null;
        bool ended = false;

        lock (_sync)
        {
            if (!_rooms.TryGetValue(code, out var room)) return RoomResult.Fail("这局已经散啦");
            if (room.GameType != "bomb") return RoomResult.Fail("这间房玩的是别的，不是猜数字哦");
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
            var delta = isLoser ? (p.Shielded ? 0 : -5) : 5;
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
            if (room.GameType != "bomb") return new ItemResult(false, "这间房用不上道具");
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
                    CancelTimersLocked(room);
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
                CancelTimersLocked(room);
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
                CancelTimersLocked(room);
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
            CancelTimersLocked(room);
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
                    CancelTimersLocked(room);
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
        // 画猜：离开的人是不是正在画的那位（移除前先记下来）
        var wasDrawer = room.State == RoomState.Playing && room.Draw is not null
            && room.Draw.DrawerIdx == idx;
        room.Players.RemoveAt(idx);

        if (room.TurnIndex > idx) room.TurnIndex--;
        if (room.TurnIndex >= room.Players.Count) room.TurnIndex = 0;

        if (room.HostUserId == player.UserId && room.Players.Count > 0)
            room.HostUserId = room.Players.FirstOrDefault(p => !p.IsBot)?.UserId ?? room.Players[0].UserId;

        if (room.State == RoomState.Playing)
        {
            // 画猜：画的人走了就翻到下一位；猜的人走了少算一个
            if (room.GameType == "draw" && room.Draw is not null)
            {
                var draw = room.Draw;
                var humans = room.Players.Count(p => !p.IsBot);
                if (humans < 2 || room.Players.Count < 2)
                {
                    room.State = RoomState.Finished;
                    room.LoserId = null;
                    room.Deltas.Clear();
                    try { draw.PhaseCts.Cancel(); } catch { }
                    return;
                }
                if (draw.DrawerIdx >= room.Players.Count) draw.DrawerIdx = 0;
                if (wasDrawer)
                {
                    EndDrawTurnLocked(room);
                }
                return;
            }

            // 贪吃蛇：这个人下场，身子变食物
            if (room.GameType == "snake" && room.Snake is not null)
            {
                var sp = room.Snake.Snakes.FirstOrDefault(s => s.Pid == player.UserId);
                if (sp is { Alive: true })
                {
                    sp.Alive = false;
                    foreach (var c in sp.Cells)
                    {
                        if (room.Snake.Foods.Count < 40 && !room.Snake.Foods.Any(f => f[0] == c[0] && f[1] == c[1]))
                            room.Snake.Foods.Add(new[] { c[0], c[1] });
                    }
                    sp.Cells.Clear();
                }

                var humansLeft = room.Players.Count(p => !p.IsBot);
                if (room.Players.Count < 2 || humansLeft == 0)
                {
                    FinishSnakeLocked(room);
                    return;
                }
                return;
            }

            var bombHumansLeft = room.Players.Count(p => !p.IsBot && p.ConnectionId is not null);
            if (room.Players.Count < 2 || bombHumansLeft == 0)
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
        bool rearm = false;
        lock (_sync)
        {
            if (!_rooms.TryGetValue(code, out var room) || room.State != RoomState.Waiting) return;

            if (room.GameType == "draw")
            {
                // 画猜没有机器人能顶（画不了画也猜不了词），继续等真人
                if (room.Players.Count < 2)
                {
                    room.WaitUntil = DateTime.UtcNow.Add(WaitForHumans);
                    rearm = true;
                }
            }
            else if (room.Players.Count < 2)
            {
                botName = AddBotLocked(room);
            }

            if (!rearm) StartLocked(room);
        }

        if (rearm)
        {
            BombRoom? room2;
            lock (_sync) { _rooms.TryGetValue(code, out room2); }
            if (room2 is not null) ArmWaitTimer(room2);
            Notice(code, "还差一位真人，画猜房等得起，别急 🎨");
            Broadcast(code);
            return;
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

    /// <summary>房间收摊或开局时，把所有挂着的定时器一起掐掉</summary>
    private static void CancelTimersLocked(BombRoom room)
    {
        try { room.WaitCts.Cancel(); } catch { /* 已取消 */ }
        if (room.Draw is not null) { try { room.Draw.PhaseCts.Cancel(); } catch { } }
        if (room.Snake is not null) { try { room.Snake.TickCts.Cancel(); } catch { } }
    }

    private void MaybeScheduleBot(string code)
    {
        lock (_sync)
        {
            if (!_rooms.TryGetValue(code, out var room)) return;
            if (room.GameType != "bomb") return;   // 只有数字炸弹有回合制机器人
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

    // ---------------------------------------------------------------- 贪吃蛇大乱斗

    private static readonly string[] SnakeColors = { "#e8590c", "#1971c2", "#2f9e44", "#9c36b5", "#e03131", "#f59f00", "#0c8599", "#7048e8" };

    private void StartSnakeLocked(BombRoom room)
    {
        var snake = room.Snake = new SnakeState { EndAt = DateTime.UtcNow.Add(SnakeState.MaxDuration) };
        snake.Snakes.Clear();
        snake.Foods.Clear();
        snake.Pool = 0;

        // 出生点均匀撒在边缘一圈，朝向场地中心
        var n = room.Players.Count;
        for (var i = 0; i < n; i++)
        {
            var p = room.Players[i];
            var (x, y, dx, dy) = SpawnPoint(i, n);
            var sp = new SnakePlayer
            {
                Pid = p.UserId,
                ColorIdx = i % SnakeColors.Length,
                IsBot = p.IsBot,
                Dx = dx,
                Dy = dy
            };
            for (var k = 0; k < SnakeState.StartLen; k++)
                sp.Cells.Add(new[] { x - dx * k, y - dy * k });
            snake.Snakes.Add(sp);
        }

        for (var f = 0; f < SnakeState.MaxFoods; f++) SpawnFoodLocked(snake);
        ArmSnakeLoop(room.Code);
    }

    /// <summary>第 i 个玩家的出生点：围一圈，头朝里</summary>
    private static (int X, int Y, int Dx, int Dy) SpawnPoint(int i, int n)
    {
        var w = SnakeState.W;
        var h = SnakeState.H;
        var perimeter = 2 * (w + h);
        var pos = (int)((long)i * perimeter / Math.Max(1, n));
        if (pos < w) return (pos, 0, 0, 1);                       // 上边，向下
        pos -= w;
        if (pos < h) return (w - 1, pos, -1, 0);                  // 右边，向左
        pos -= h;
        if (pos < w) return (w - 1 - pos, h - 1, 0, -1);          // 下边，向上
        pos -= w;
        return (0, h - 1 - pos, 1, 0);                            // 左边，向右
    }

    private static void SpawnFoodLocked(SnakeState snake)
    {
        for (var t = 0; t < 200; t++)
        {
            var x = Random.Shared.Next(SnakeState.W);
            var y = Random.Shared.Next(SnakeState.H);
            if (IsOccupied(snake, x, y)) continue;
            if (snake.Foods.Any(f => f[0] == x && f[1] == y)) continue;
            snake.Foods.Add(new[] { x, y });
            return;
        }
    }

    private static bool IsOccupied(SnakeState snake, int x, int y)
        => snake.Snakes.Any(s => s.Alive && s.Cells.Any(c => c[0] == x && c[1] == y));

    private void ArmSnakeLoop(string code)
    {
        SnakeState? mySnake;
        CancellationToken token;
        lock (_sync)
        {
            if (!_rooms.TryGetValue(code, out var room) || room.Snake is null) return;
            if (room.Snake.LoopRunning) return;
            room.Snake.LoopRunning = true;
            mySnake = room.Snake;
            token = mySnake.TickCts.Token;
        }

        _ = Task.Run(async () =>
        {
            while (true)
            {
                try { await Task.Delay(SnakeState.TickMs, token); }
                catch (OperationCanceledException) { return; }

                var end = false;
                lock (_sync)
                {
                    if (!_rooms.TryGetValue(code, out var room) || room.Snake is null || !ReferenceEquals(room.Snake, mySnake)
                        || room.State != RoomState.Playing)
                        return;
                    end = TickSnakeLocked(room);
                }
                Broadcast(code);
                if (end) return;
            }
        });
    }

    /// <summary>走一步。返回 true = 这一局结束了。</summary>
    private bool TickSnakeLocked(BombRoom room)
    {
        var snake = room.Snake!;
        var alive = snake.Snakes.Where(s => s.Alive).ToList();
        if (alive.Count == 0) { FinishSnakeLocked(room); return true; }

        // 机器人先想好这一步往哪走
        foreach (var bot in alive.Where(s => s.IsBot))
            BotSteerLocked(snake, bot);

        // 应用转方向（不许原地掉头）
        foreach (var s in alive)
        {
            while (s.Pending.Count > 0)
            {
                var d = s.Pending[0];
                s.Pending.RemoveAt(0);
                if (d[0] == -s.Dx && d[1] == -s.Dy && s.Cells.Count > 1) continue;
                if (d[0] == s.Dx && d[1] == s.Dy) continue;
                s.Dx = d[0]; s.Dy = d[1];
                break;
            }
        }

        // 先算所有人要去的格子
        var newHeads = new Dictionary<SnakePlayer, int[]>();
        foreach (var s in alive)
        {
            var head = s.Cells[0];
            newHeads[s] = new[] { head[0] + s.Dx, head[1] + s.Dy };
        }

        // 判死刑：撞墙 / 撞肚子（任何蛇的身子，含自己）/ 头对头
        var dead = new HashSet<SnakePlayer>();
        foreach (var (s, head) in newHeads)
        {
            if (head[0] < 0 || head[0] >= SnakeState.W || head[1] < 0 || head[1] >= SnakeState.H)
            {
                dead.Add(s);
                continue;
            }

            foreach (var other in alive)
            {
                // 自己的格子要排除掉尾巴最后那格（它会挪走）
                var cells = other.Cells;
                var skipTail = other == s && cells.Count > 1;
                var limit = cells.Count - (skipTail ? 1 : 0);
                for (var i = 0; i < limit; i++)
                {
                    if (cells[i][0] == head[0] && cells[i][1] == head[1])
                    {
                        dead.Add(s);
                        if (other != s) other.Kills++;
                        break;
                    }
                }
                if (dead.Contains(s)) break;
            }
        }

        // 头对头：两个头撞同一格，一起走
        var headGroups = newHeads.Where(kv => !dead.Contains(kv.Key))
            .GroupBy(kv => $"{kv.Value[0]},{kv.Value[1]}")
            .Where(g => g.Count() > 1);
        foreach (var g in headGroups)
            foreach (var kv in g)
            {
                var s = kv.Key;
                dead.Add(s);
                s.Kills += 1; // 同归于尽也各记一下
            }

        // 死亡处理：身子变食物，全场都看得到
        foreach (var s in dead)
        {
            s.Alive = false;
            foreach (var c in s.Cells)
            {
                if (snake.Foods.Count < 40 && !snake.Foods.Any(f => f[0] == c[0] && f[1] == c[1]))
                    snake.Foods.Add(new[] { c[0], c[1] });
            }
            s.Cells.Clear();
        }

        // 幸存者前进
        foreach (var (s, head) in newHeads)
        {
            if (dead.Contains(s)) continue;
            s.Cells.Insert(0, head);

            var foodIdx = snake.Foods.FindIndex(f => f[0] == head[0] && f[1] == head[1]);
            if (foodIdx >= 0)
            {
                snake.Foods.RemoveAt(foodIdx);
                s.Score++;
                s.Eaten++;
                snake.Pool += SnakeCoins.PerFood;      // 共享金币池又厚了一点
                SpawnFoodLocked(snake);
            }
            else if (s.Cells.Count > SnakeState.StartLen + s.Eaten)
            {
                // 没吃到就缩尾（长度 = 起始 + 吃的个数）
                s.Cells.RemoveAt(s.Cells.Count - 1);
            }
        }

        // 场上最多剩一条活的就收场；或者时间到
        var left = snake.Snakes.Count(s => s.Alive);
        if (left <= 1 || DateTime.UtcNow >= snake.EndAt)
        {
            FinishSnakeLocked(room);
            return true;
        }
        return false;
    }

    /// <summary>机器人找吃的：优先安全 + 朝最近食物走</summary>
    private void BotSteerLocked(SnakeState snake, SnakePlayer bot)
    {
        int[] Want(int dx, int dy) => new[] { dx, dy };

        var head = bot.Cells[0];
        var options = new List<(int[] Dir, int Dist, bool Safe)>();
        foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
        {
            if (dx == -bot.Dx && dy == -bot.Dy) continue; // 不掉头
            var nx = head[0] + dx;
            var ny = head[1] + dy;
            var safe = nx >= 0 && nx < SnakeState.W && ny >= 0 && ny < SnakeState.H
                && !snake.Snakes.Any(s => s.Alive && s.Cells.Any(c => c[0] == nx && c[1] == ny));
            var dist = snake.Foods.Count == 0 ? 99
                : snake.Foods.Min(f => Math.Abs(f[0] - nx) + Math.Abs(f[1] - ny));
            options.Add((Want(dx, dy), dist, safe));
        }

        var best = options.Where(o => o.Safe).OrderBy(o => o.Dist).FirstOrDefault();
        if (best.Dir is not null)
        {
            bot.Pending.Clear();
            bot.Pending.Add(best.Dir);
        }
        else
        {
            // 全都不安全，随便挑一个等死
            bot.Pending.Clear();
            bot.Pending.Add(options[Random.Shared.Next(options.Count)].Dir);
        }
    }

    private void FinishSnakeLocked(BombRoom room)
    {
        var snake = room.Snake!;
        room.State = RoomState.Finished;
        try { snake.TickCts.Cancel(); } catch { }
        snake.LoopRunning = false;

        var winnerId = snake.Snakes
            .Where(s => s.Alive)
            .OrderByDescending(s => s.Score)
            .FirstOrDefault()?.Pid;

        // 共享金币池平分：池子 ÷ 全桌人数，机器人那份不要
        var humans = room.Players.Count(p => !p.IsBot);
        var share = humans > 0 ? snake.Pool / humans : 0;

        foreach (var p in room.Players)
        {
            var sp = snake.Snakes.FirstOrDefault(s => s.Pid == p.UserId);
            var pts = sp?.Score ?? 0;
            if (winnerId is not null && p.UserId == winnerId) pts += SnakeCoins.WinBonus;
            pts += sp?.Kills * SnakeCoins.KillBonus ?? 0;
            if (sp is { Alive: false }) pts += SnakeCoins.DiedPenalty; // 死了倒扣
            var coins = p.IsBot ? 0 : share;
            p.Points = Math.Max(0, p.Points + pts);
            if (!p.IsBot) p.Coins = Math.Max(0, p.Coins + coins);
            room.Deltas.Add(new PointDelta(p.UserId, pts, p.IsBot, coins));
        }

        room.SnakeWinnerId = winnerId;
        PersistPoints(room.Deltas);
    }

    /// <summary>玩家转向。</summary>
    public RoomResult SnakeDir(string code, int userId, int dx, int dy)
    {
        lock (_sync)
        {
            if (!_rooms.TryGetValue(code, out var room) || room.Snake is null) return RoomResult.Fail("这局已经散啦");
            if (room.State != RoomState.Playing) return RoomResult.Fail("这局不在进行中");
            var s = room.Snake.Snakes.FirstOrDefault(x => x.Pid == userId);
            if (s is null || !s.Alive) return RoomResult.Fail("你的蛇已经下场啦");

            var valid = (dx, dy) is (1, 0) or (-1, 0) or (0, 1) or (0, -1);
            if (!valid) return RoomResult.Fail("方向不对哦");
            if (s.Pending.Count < 2) s.Pending.Add(new[] { dx, dy });
        }
        return RoomResult.Done(code);
    }

    // ---------------------------------------------------------------- 广播

    private void Broadcast(string code)
    {
        bool isDraw = false;
        object? snapshot = null;
        lock (_sync)
        {
            if (_rooms.TryGetValue(code, out var room))
            {
                snapshot = Snapshot(room);
                isDraw = room.GameType == "draw";
            }
        }
        if (snapshot is not null)
        {
            _ = _hub.Clients.Group(Key(code)).SendAsync("state", snapshot);
            // 画猜的词是私货：状态广播完，单独塞给画手本人
            if (isDraw) SendDrawerSecrets(code);
        }
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
            type = room.GameType,
            state = room.State.ToString().ToLowerInvariant(),
            capacity = room.Capacity,
            hostId = room.HostUserId,
            min = room.Min,
            max = room.Max,
            low = room.Low,
            high = room.High,
            turnUserId = room.State == RoomState.Playing && room.GameType == "bomb" && room.Current is not null ? room.Current.UserId : (int?)null,
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
            bomb = room.GameType == "bomb" && room.State == RoomState.Finished ? room.Bomb : (int?)null,
            loserId = room.LoserId,
            deltas = room.Deltas.Select(d => new { id = d.UserId, delta = d.Delta, coins = d.Coins, isBot = d.IsBot }).ToArray(),
            canStart = room.State == RoomState.Waiting && room.Players.Count >= 2,
            botJoined = room.BotAdded,
            // ---- 你画我猜 ----
            draw = room.Draw is null ? null : new
            {
                phase = room.Draw.Phase,
                drawerId = room.Draw.DrawerPid,
                turnNo = room.Draw.TurnNo,
                turnTotal = room.Players.Count,
                wordLen = room.Draw.Word.Length,
                // 只在收场时公开答案，画的过程里谁也别想偷看
                word = room.State == RoomState.Finished ? room.Draw.Word : "",
                guessedIds = room.Draw.GuessedIds.ToArray(),
                strokes = room.Draw.Strokes.Select(st => new
                {
                    sid = st.Id,
                    pid = st.Pid,
                    color = st.Color,
                    size = st.Size,
                    pts = st.Points
                }).ToArray(),
                phaseLeft = room.State == RoomState.Playing
                    ? Math.Max(0, (int)Math.Ceiling((room.Draw.PhaseUntil - DateTime.UtcNow).TotalSeconds))
                    : 0,
                scores = room.Draw.Scores.Select(kv => new { id = kv.Key, pts = kv.Value }).ToArray(),
                winnerId = room.DrawWinnerId
            },
            // ---- 贪吃蛇 ----
            snake = room.Snake is null ? null : new
            {
                w = SnakeState.W,
                h = SnakeState.H,
                tickMs = SnakeState.TickMs,
                pool = room.Snake.Pool,
                endIn = room.State == RoomState.Playing
                    ? Math.Max(0, (int)Math.Ceiling((room.Snake.EndAt - DateTime.UtcNow).TotalSeconds))
                    : 0,
                foods = room.Snake.Foods,
                snakes = room.Snake.Snakes.Select(s => new
                {
                    id = s.Pid,
                    color = SnakeColors[s.ColorIdx % SnakeColors.Length],
                    cells = s.Cells,
                    score = s.Score,
                    alive = s.Alive,
                    isBot = s.IsBot
                }).ToArray(),
                winnerId = room.SnakeWinnerId
            }
        };
    }
}
