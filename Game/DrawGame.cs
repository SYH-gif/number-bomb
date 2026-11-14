namespace NumberBomb.Game;

/// <summary>画猜里的一笔。坐标归一化到 0~1000，前端自己乘上画布尺寸。</summary>
public sealed class DrawStroke
{
    public int Id { get; set; }
    public int Pid { get; set; }
    public string Color { get; set; } = "#20242c";
    public int Size { get; set; } = 8;
    /// <summary>压平的点序列：x1,y1,x2,y2…</summary>
    public List<int> Points { get; set; } = new();
}

/// <summary>你画我猜的房间状态。</summary>
public sealed class DrawState
{
    /// <summary>choosing = 画手挑词，drawing = 画中，between = 换人间隙</summary>
    public string Phase { get; set; } = "choosing";
    /// <summary>当前画手在 room.Players 里的下标</summary>
    public int DrawerIdx { get; set; }
    /// <summary>当前画手的 id（比下标稳：有人退出时下标会漂）</summary>
    public int DrawerPid { get; set; }
    /// <summary>挑词的三个候选（只发给画手本人）</summary>
    public List<string> Choices { get; } = new();
    public string Word { get; set; } = "";
    /// <summary>这一词已经猜对的人</summary>
    public List<int> GuessedIds { get; } = new();
    public List<DrawStroke> Strokes { get; } = new();
    /// <summary>每人累计（得分，金币），结算时统一算 Delta</summary>
    public Dictionary<int, int> Scores { get; } = new();
    public Dictionary<int, int> CoinsEarned { get; } = new();
    public DateTime PhaseUntil { get; set; }
    /// <summary>轮到第几位画手了（一轮内）</summary>
    public int TurnNo { get; set; } = 1;
    public int NextStrokeId { get; set; }
    public CancellationTokenSource PhaseCts { get; } = new();

    public int AddScore(int pid, int pts, int coins)
    {
        Scores[pid] = Scores.TryGetValue(pid, out var s) ? s + pts : pts;
        if (coins != 0) CoinsEarned[pid] = CoinsEarned.TryGetValue(pid, out var c) ? c + coins : coins;
        return Scores[pid];
    }
}

/// <summary>你画我猜的得分与金币规则</summary>
public static class DrawScores
{
    /// <summary>猜对一次 +10 分</summary>
    public const int Correct = 10;
    /// <summary>画手每有一位猜对 +8 分</summary>
    public const int PerGuesser = 8;
    /// <summary>猜对的人（和对应的画手）各得多少金币</summary>
    public const int CoinsPerCorrect = 10;
}

/// <summary>画猜词库：都是生活中常见的，画得出来也猜得出来。</summary>
public static class DrawWords
{
    public static readonly string[] All =
    {
        // 水果蔬菜
        "苹果","香蕉","西瓜","葡萄","草莓","橘子","梨","桃子","柠檬","菠萝",
        "西红柿","茄子","白菜","萝卜","玉米","南瓜","蘑菇","辣椒","花生","土豆",
        // 吃的
        "汉堡","饺子","包子","火锅","烧烤","冰淇淋","蛋糕","面条","米饭","爆米花",
        "可乐","奶茶","棒棒糖","薯条","月饼","粽子","煎蛋","三明治","寿司","油条",
        // 动物
        "猫","狗","兔子","老虎","狮子","大象","猴子","熊猫","企鹅","长颈鹿",
        "乌龟","金鱼","螃蟹","蝴蝶","蜜蜂","猫头鹰","恐龙","鲨鱼","鲸鱼","青蛙",
        "蜗牛","刺猬","骆驼","袋鼠","孔雀","公鸡","鸭子","老鼠","蛇","蜻蜓",
        // 日常物品
        "雨伞","眼镜","时钟","椅子","桌子","床","台灯","手机","电视","电脑",
        "键盘","书包","铅笔","气球","风筝","雪人","剪刀","口罩","钥匙","袜子",
        "帽子","手套","围巾","牙刷","勺子","筷子","杯子","枕头","梯子","锤子",
        // 交通工具
        "自行车","摩托车","汽车","公交车","救护车","消防车","火车","飞机","火箭","帆船",
        "轮船","潜水艇","热气球","地铁","三轮车","滑板",
        // 自然
        "太阳","月亮","星星","彩虹","云朵","火山","沙滩","大海","高山","森林",
        "闪电","雪花","仙人掌","荷花","玫瑰","向日葵",
        // 动作与场景
        "睡觉","打球","游泳","跑步","唱歌","跳舞","刷牙","照镜子","放风筝","钓鱼",
        "滑雪","骑马","荡秋千","跷跷板","堆雪人","打乒乓球","放鞭炮","吹蜡烛","捉迷藏","拔河"
    };

    /// <summary>随机抽 n 个不重复的词（不与最近用过的重复更好，这里简单随机就够）</summary>
    public static List<string> Pick(int n)
    {
        var pool = All.OrderBy(_ => Random.Shared.Next()).Take(n).ToList();
        return pool;
    }

    public static string PickOne() => All[Random.Shared.Next(All.Length)];
}
