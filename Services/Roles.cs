namespace NumberBomb.Services;

/// <summary>
/// 身份与权限。三层：开发者（dev）&gt; 管理员（admin）&gt; 普通玩家（user）。
/// 开发者名单写在 appsettings.json 的 "Owner:Names" 里（逗号分隔），改完重启生效；
/// 管理员由开发者在「管理」面板里任命，存在 users.role 里。
/// </summary>
public static class Roles
{
    public const string Dev = "dev";
    public const string Admin = "admin";
    public const string User = "user";

    private static readonly string[] DevBadges = { "dev", "admin" };
    private static readonly string[] AdminBadges = { "admin" };
    private static readonly string[] NoBadges = Array.Empty<string>();

    private static string[] _owners = Array.Empty<string>();

    public static void Configure(string? names)
    {
        _owners = (names ?? "")
            .Split(new[] { ',', '，', ';', '；', '|' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>名字在开发者名单里？大小写不敏感，两边空格不算数。</summary>
    public static bool IsDevName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || _owners.Length == 0) return false;
        var target = name.Trim();
        foreach (var owner in _owners)
        {
            if (string.Equals(owner, target, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static string Normalize(string? role) => role switch
    {
        Dev => Dev,
        Admin => Admin,
        _ => User
    };

    /// <summary>最终角色：名字在开发者名单里就是 dev，否则看数据库里存的</summary>
    public static string Effective(string? name, string? storedRole)
        => IsDevName(name) ? Dev : Normalize(storedRole);

    public static string[] BadgesFor(string? name, string? storedRole) => Effective(name, storedRole) switch
    {
        Dev => DevBadges,
        Admin => AdminBadges,
        _ => NoBadges
    };

    /// <summary>能不能管人（踢人、禁言、发公告、删聊天）</summary>
    public static bool CanModerate(string? name, string? storedRole)
        => Effective(name, storedRole) != User;

    /// <summary>能不能任命管理员、调分这类「只有开发者有」的事</summary>
    public static bool IsDev(string? name, string? storedRole)
        => Effective(name, storedRole) == Dev;

    public static string Label(string role) => Normalize(role) switch
    {
        Dev => "开发者",
        Admin => "管理员",
        _ => "玩家"
    };
}
