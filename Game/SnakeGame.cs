namespace NumberBomb.Game;

/// <summary>贪吃蛇里一条蛇（一个人或一个机器人）。</summary>
public sealed class SnakePlayer
{
    public int Pid { get; set; }
    /// <summary>撞墙/撞肚子自杀</summary>
    public bool Alive { get; set; } = true;
    /// <summary>头在第一位</summary>
    public List<int[]> Cells { get; } = new();
    public int Dx { get; set; } = 1;
    public int Dy { get; set; }
    /// <summary>接下来想转的方向（最多囤两步，转不过来就丢掉）</summary>
    public List<int[]> Pending { get; } = new();
    public int Score { get; set; }
    public int Eaten { get; set; }
    public int ColorIdx { get; set; }
    public bool IsBot { get; set; }
    /// <summary>这一把撞到谁身上挂的（记分用）</summary>
    public int Kills { get; set; }
}

/// <summary>贪吃蛇大乱斗的房间状态。</summary>
public sealed class SnakeState
{
    public const int W = 30;
    public const int H = 22;
    public const int TickMs = 130;
    public const int MaxFoods = 4;
    public const int StartLen = 3;
    /// <summary>一局最长 4 分钟，到点按分数排名收场</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(4);

    public List<SnakePlayer> Snakes { get; } = new();
    public List<int[]> Foods { get; } = new();
    /// <summary>共享金币池：每吃一个食物 +2，结束时全桌平均分</summary>
    public int Pool { get; set; }
    public DateTime EndAt { get; set; }
    public CancellationTokenSource TickCts { get; } = new();
    public bool LoopRunning { get; set; }
}

/// <summary>贪吃蛇的得分与金币规则</summary>
public static class SnakeCoins
{
    /// <summary>每吃一个食物 +1 分</summary>
    public const int FoodPoints = 1;
    /// <summary>每吃一个食物，共享金币池 +2，结束全桌平分</summary>
    public const int PerFood = 2;
    /// <summary>撞在你身上的蛇死了，你 +3 分</summary>
    public const int KillBonus = 3;
    /// <summary>活到最后 +10 分</summary>
    public const int WinBonus = 10;
}
