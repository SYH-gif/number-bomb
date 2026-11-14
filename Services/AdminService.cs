using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace NumberBomb.Services;

public sealed record AdminLoginResult(bool Ok, int Remaining = 0, int LockSeconds = 0);

/// <summary>
/// 后台钥匙。和玩家账号是两套东西：
///   · 密钥只存在这台机器上（appsettings 的 AdminKey / 环境变量 NB_ADMIN_KEY / admin.key 文件）
///   · 登录成功后发一张 2 小时的票，HttpOnly + SameSite=Strict，票里带密钥指纹，换密钥即全废
///   · 没票的时候，后台接口一律当"这里什么都没有"处理（404），不告诉对方是权限问题
/// </summary>
public sealed class AdminService
{
    public const string CookieName = "nb_admin";
    private static readonly TimeSpan CookieLife = TimeSpan.FromHours(2);
    private const int MaxTries = 5;
    private static readonly TimeSpan LockoutFor = TimeSpan.FromMinutes(15);

    private sealed class Attempt
    {
        public int Fails;
        public DateTime? LockedUntil;
    }

    private readonly IDataProtector _protector;
    private readonly string _key;
    private readonly string _stamp;
    private readonly Dictionary<string, Attempt> _attempts = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public string KeyFilePath { get; }

    public AdminService(IDataProtectionProvider dp, IConfiguration config, IWebHostEnvironment env, ILogger<AdminService> log)
    {
        _protector = dp.CreateProtector("NumberBomb.Admin.v1");

        var fromConfig = (config["AdminKey"] ?? Environment.GetEnvironmentVariable("NB_ADMIN_KEY") ?? "").Trim();

        KeyFilePath = Path.Combine(env.ContentRootPath, "admin.key");

        if (fromConfig.Length >= 8)
        {
            _key = fromConfig;
            log.LogInformation("后台密钥来自配置（{Len} 位）", _key.Length);
        }
        else if (File.Exists(KeyFilePath) && (File.ReadAllText(KeyFilePath).Trim().Length >= 8))
        {
            _key = File.ReadAllText(KeyFilePath).Trim();
            log.LogInformation("后台密钥来自 {Path}", KeyFilePath);
        }
        else
        {
            _key = NewKey();
            File.WriteAllText(KeyFilePath, _key + Environment.NewLine);
            log.LogWarning("第一次启动，已生成后台密钥并写入 {Path}：{Key}", KeyFilePath, _key);
        }

        _stamp = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_key)))[..12];
    }

    private static string NewKey()
    {
        // 去掉容易看错的 0/O/1/I，方便手抄
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var bytes = RandomNumberGenerator.GetBytes(20);
        var sb = new StringBuilder(20);
        foreach (var b in bytes) sb.Append(alphabet[b % alphabet.Length]);
        return sb.ToString();
    }

    public AdminLoginResult TryLogin(string? key, string clientKey)
    {
        var now = DateTime.UtcNow;
        lock (_sync)
        {
            if (!_attempts.TryGetValue(clientKey, out var att))
            {
                att = new Attempt();
                _attempts[clientKey] = att;
            }

            if (att.LockedUntil is { } until && until > now)
            {
                var left = (int)Math.Ceiling((until - now).TotalSeconds);
                return new AdminLoginResult(false, 0, left);
            }

            if (att.LockedUntil is { } expired && expired <= now)
            {
                att.LockedUntil = null;
                att.Fails = 0;
            }

            if (FixedEquals((key ?? "").Trim(), _key))
            {
                att.Fails = 0;
                att.LockedUntil = null;
                return new AdminLoginResult(true);
            }

            att.Fails++;
            if (att.Fails >= MaxTries)
            {
                att.LockedUntil = now + LockoutFor;
                att.Fails = 0;
                return new AdminLoginResult(false, 0, (int)LockoutFor.TotalSeconds);
            }

            return new AdminLoginResult(false, MaxTries - att.Fails, 0);
        }
    }

    private static bool FixedEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    public void IssueCookie(HttpContext ctx)
    {
        var exp = DateTimeOffset.UtcNow.Add(CookieLife).ToUnixTimeSeconds();
        var token = _protector.Protect($"ok|{exp}|{_stamp}");
        ctx.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = ctx.Request.IsHttps,
            Path = "/",
            Expires = DateTimeOffset.UtcNow.Add(CookieLife)
        });
    }

    public void ClearCookie(HttpContext ctx)
        => ctx.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/", SameSite = SameSiteMode.Strict });

    public bool IsAdmin(HttpContext ctx)
    {
        if (!ctx.Request.Cookies.TryGetValue(CookieName, out var token) || string.IsNullOrEmpty(token))
            return false;
        try
        {
            var parts = _protector.Unprotect(token).Split('|');
            if (parts.Length != 3 || parts[0] != "ok") return false;
            if (parts[2] != _stamp) return false;
            if (!long.TryParse(parts[1], out var exp)) return false;
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() < exp;
        }
        catch
        {
            return false;
        }
    }
}
