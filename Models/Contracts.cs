namespace NumberBomb.Models;

public sealed record GateRequest(string? Password);

public sealed record AuthRequest(string? Username, string? Password, string? Avatar);

public sealed record AvatarRequest(string? Preset, string? DataUrl);

public sealed record AdminLoginRequest(string? Key);

/// <summary>后台改分：给 Delta 就是加减，给 Set 就是直接设成那个数（Set 优先）。</summary>
public sealed record AdminPointsRequest(int? Delta, int? Set);

// ---------------- 好友 / 邮件 / 商店 ----------------

/// <summary>按名字加好友</summary>
public sealed record FriendRequest(string? Name);

/// <summary>处理别人的申请：Id 是对方的账号 id</summary>
public sealed record FriendAnswer(int Id, bool Accept);

/// <summary>通用「对某个人做点什么」</summary>
public sealed record PersonRef(int Id);

public sealed record MailRequest(string? To, string? Subject, string? Body);

public sealed record BuyRequest(string? Kind);

// ---------------- 对话 / 管理 ----------------

public sealed record ChatRequest(int ToId, string? Body);

public sealed record MuteRequest(int UserId, int Minutes);

public sealed record KickRequest(int UserId, string? Code);

public sealed record AnnounceRequest(string? Text);

public sealed record RoleRequest(int UserId, string? Role);

public sealed record ModPointsRequest(int UserId, int? Delta, int? Set, int? Coins);

// ---------------- 个人资料 / 开发者 ----------------

/// <summary>改自己的资料：名字和签名，给哪个改哪个。</summary>
public sealed record ProfileRequest(string? Username, string? Bio);

/// <summary>清空/删除这类操作必须手打确认词，防手滑。</summary>
public sealed record DevWipeRequest(string? Confirm);

public sealed record DeleteUserRequest(int UserId);

public sealed record CloseRoomRequest(string? Code);

public static class AvatarPresets
{
    /// <summary>默认可选头像，都是 emoji，前端直接当字用。</summary>
    public static readonly string[] All =
    {
        "🐱", "🐶", "🦊", "🐼", "🐸", "🐙", "🦄", "🐳", "🍄", "👾", "🦉", "🐝"
    };

    public const string Prefix = "preset:";
    public const string Custom = "custom";

    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        if (value == Custom) return true;
        return value.StartsWith(Prefix, StringComparison.Ordinal) && All.Contains(value[Prefix.Length..]);
    }

    public static string Normalize(string? value)
        => IsValid(value) ? value! : Prefix + All[0];

    public static string Emoji(string avatar)
        => avatar.StartsWith(Prefix, StringComparison.Ordinal) ? avatar[Prefix.Length..] : "🙂";
}
