namespace NumberBomb.Models;

/// <summary>商店里的一件道具。价格用金币，金币只能靠赢局赚。</summary>
public sealed record ShopItem(string Kind, string Name, string Emoji, string Desc, int Price, string Where);

public static class Shop
{
    public static readonly ShopItem Shield = new(
        "shield", "免死金牌", "🛡️",
        "这一把踩到炸弹也不扣分，白踩一下", 60, "对局里随时可用");

    public static readonly ShopItem Scan = new(
        "scan", "探测器", "🔍",
        "悄悄告诉你炸弹在左半边还是右半边，只你自己看得见", 40, "轮到你的时候用");

    public static readonly ShopItem Skip = new(
        "skip", "金蝉脱壳", "💨",
        "这一轮跳过，不用猜，直接让给下一位", 30, "轮到你的时候用");

    public static readonly ShopItem[] All = { Shield, Scan, Skip };

    public static ShopItem? Find(string? kind) =>
        All.FirstOrDefault(i => string.Equals(i.Kind, kind, StringComparison.OrdinalIgnoreCase));

    /// <summary>赢一局给多少金币</summary>
    public const int CoinsForWin = 30;

    /// <summary>踩雷最少扣多少金币</summary>
    public const int CoinsLostMin = 1;

    /// <summary>踩雷最多扣多少金币</summary>
    public const int CoinsLostMax = 100;

    /// <summary>踩雷这一把掉多少金币（1~100 随机，返回正数，用的时候取负）</summary>
    public static int RollCoinsLost() => Random.Shared.Next(CoinsLostMin, CoinsLostMax + 1);
}
