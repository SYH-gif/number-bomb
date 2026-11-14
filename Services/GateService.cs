using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace NumberBomb.Services;

public sealed record GateResult(bool Ok, int Remaining = 0, int LockSeconds = 0, string? Message = null);

/// <summary>
/// 门禁：默认是敞开的 —— appsettings.json 里 AccessPassword 留空，谁点开都能玩。
/// 想只给朋友玩的时候，把那一行填上（4 位以上）重启即可恢复：
/// 同一个 IP 连错 5 次歇 10 分钟，密码对了发一个 30 天有效的签名 cookie。
/// </summary>
public sealed class GateService
{
    public const string CookieName = "nb_gate";
    private const int MaxTries = 5;
    private static readonly TimeSpan LockoutFor = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CookieLife = TimeSpan.FromDays(30);

    private sealed class Attempt
    {
        public int Fails;
        public DateTime? LockedUntil;
    }

    private readonly IDataProtector _protector;
    private readonly string _password;
    private readonly string _stamp;          // 口令指纹，写进 cookie，换口令就自动把旧 cookie 作废
    private readonly bool _open;
    private readonly Dictionary<string, Attempt> _attempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public GateService(IDataProtectionProvider dp, IConfiguration config, ILogger<GateService> log)
    {
        _protector = dp.CreateProtector("NumberBomb.Gate.v1");
        _password = (config["AccessPassword"] ?? "").Trim();
        _open = _password.Length == 0;
        if (!_open && _password.Length < 4)
            log.LogWarning("AccessPassword 只有 {Len} 个字符，太短了，建议至少 4 位", _password.Length);
        _stamp = _open
            ? ""
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_password.ToUpperInvariant())))[..12];

        if (_open) log.LogInformation("没设访问密码，门口是开的：谁点开都能玩");
        else log.LogInformation("访问密码已从配置读取（{Len} 个字符）", _password.Length);
    }

    /// <summary>门口开着的时候，所有校验直接放行</summary>
    public bool IsOpen => _open;

    public GateResult TryUnlock(string? password, string clientKey)
    {
        if (_open) return new GateResult(true);

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
                return new GateResult(false, 0, left, "先歇一会儿");
            }

            if (att.LockedUntil is { } expired && expired <= now)
            {
                att.LockedUntil = null;
                att.Fails = 0;
            }

            var given = (password ?? "").Trim();
            // 门口这道是给朋友用的，大小写别为难人（账号密码那边照旧区分）
            if (FixedEquals(given.ToUpperInvariant(), _password.ToUpperInvariant()))
            {
                att.Fails = 0;
                att.LockedUntil = null;
                return new GateResult(true);
            }

            att.Fails++;
            if (att.Fails >= MaxTries)
            {
                att.LockedUntil = now + LockoutFor;
                att.Fails = 0;
                return new GateResult(false, 0, (int)LockoutFor.TotalSeconds, "歇一会儿再来");
            }

            return new GateResult(false, MaxTries - att.Fails, 0, "不太对");
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
        if (_open) return;

        var exp = DateTimeOffset.UtcNow.Add(CookieLife).ToUnixTimeSeconds();
        var token = _protector.Protect($"ok|{exp}|{_stamp}");
        ctx.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = ctx.Request.IsHttps,      // 走 https 就带 Secure，浏览器不会再往明文连接上送
            Path = "/",
            Expires = DateTimeOffset.UtcNow.Add(CookieLife)
        });
    }

    public bool IsUnlocked(HttpContext ctx)
    {
        if (_open) return true;

        if (!ctx.Request.Cookies.TryGetValue(CookieName, out var token) || string.IsNullOrEmpty(token))
            return false;
        try
        {
            var raw = _protector.Unprotect(token);
            var parts = raw.Split('|');
            if (parts.Length != 3 || parts[0] != "ok") return false;
            if (parts[2] != _stamp) return false;     // 口令换过了，这张票过期作废
            if (!long.TryParse(parts[1], out var exp)) return false;
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() < exp;
        }
        catch
        {
            return false;
        }
    }
}
