namespace NumberBomb.Game;

public enum RoomState
{
    Waiting,
    Playing,
    Finished
}

public sealed class RoomPlayer
{
    public int UserId { get; init; }
    public string Name { get; set; } = "";
    /// <summary>dev / admin / user，只用来画名字后面的小牌子</summary>
    public string Role { get; init; } = "user";
    public string Avatar { get; init; } = "preset:🙂";
    public bool IsBot { get; init; }
    public int Points { get; set; } = 100;
    public string? ConnectionId { get; set; }
    public bool IsOut { get; set; }
    public int GoodGuesses { get; set; }
    public int GuessCount { get; set; }
    /// <summary>这一把有没有戴免死金牌</summary>
    public bool Shielded { get; set; }
    /// <summary>这一把用过的道具，画面上标一下</summary>
    public List<string> UsedItems { get; } = new();
    public int Coins { get; set; }
}

public sealed class GuessRecord
{
    public int UserId { get; init; }
    public string Name { get; init; } = "";
    public int Value { get; init; }
    /// <summary>low = 报小了，high = 报大了，boom = 踩雷，skip = 用道具跳过</summary>
    public string Dir { get; init; } = "";
}

public sealed record PointDelta(int UserId, int Delta, bool IsBot, int Coins = 0, int? FinalPoints = null);

/// <summary>一个房间的全部状态。炸弹数字只留在这个对象里，从不发给前端。</summary>
public sealed class BombRoom
{
    public string Code { get; init; } = "";
    /// <summary>bomb = 数字炸弹，draw = 你画我猜，snake = 贪吃蛇大乱斗</summary>
    public string GameType { get; set; } = "bomb";
    public RoomState State { get; set; } = RoomState.Waiting;
    public int Capacity { get; init; } = 2;
    public int HostUserId { get; set; }
    public List<RoomPlayer> Players { get; } = new();

    public int Min { get; set; } = 1;
    public int Max { get; set; } = 100;
    public int Bomb { get; set; }
    public int Low { get; set; } = 1;
    public int High { get; set; } = 100;
    public int TurnIndex { get; set; }

    public List<GuessRecord> Guesses { get; } = new();
    public int? LoserId { get; set; }
    public List<PointDelta> Deltas { get; } = new();

    public DateTime WaitUntil { get; set; } = DateTime.UtcNow.AddSeconds(60);
    public int Round { get; set; }
    public CancellationTokenSource WaitCts { get; } = new();

    public bool BotAdded { get; set; }

    /// <summary>画猜的状态（只有画猜房有）</summary>
    public DrawState? Draw { get; set; }
    /// <summary>贪吃蛇的状态（只有贪吃蛇房有）</summary>
    public SnakeState? Snake { get; set; }
    /// <summary>画猜结算时的第一名（画猜没有"输家"，用这个字段顶住结果弹窗）</summary>
    public int? DrawWinnerId { get; set; }
    /// <summary>贪吃蛇结算时的第一名</summary>
    public int? SnakeWinnerId { get; set; }

    public RoomPlayer? Current => Players.Count == 0 ? null : Players[Math.Clamp(TurnIndex, 0, Players.Count - 1)];
}
