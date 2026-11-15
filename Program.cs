using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Data.Sqlite;
using NumberBomb.Data;
using NumberBomb.Game;
using NumberBomb.Hubs;
using NumberBomb.Models;
using NumberBomb.Services;

const int HttpPort = 5080;      // 主入口（永远都开）
const int ShortPort = 80;       // 有空就顺手开一个，朋友少敲五个字符
const int HttpsPort = 5443;     // 只有显式打开 Https 开关时才用

// 一个永远对不上的哈希，用来让"名字不存在"和"密码错了"花掉一样的时间
const string DummyHash =
    "pbkdf2$600000$AAAAAAAAAAAAAAAAAAAAAA==$" +
    "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

var builder = WebApplication.CreateBuilder(args);

// 名字后面那两个标志：谁在名单里，谁就有。名单在 appsettings.json 的 Owner:Names。
Roles.Configure(builder.Configuration["Owner:Names"]);

// ---------------- 监听端口 ----------------
// 默认就是纯 http：手机点开链接直接进，不用装证书、不用点"继续访问"。
// 想加密的话，把 appsettings.json 里的 "Https" 改成 true（先跑 make-cert.ps1），
// 那时会多开一个 https://:5443，但两边都能用，不强制跳转。
var pfxPath = Path.Combine(builder.Environment.ContentRootPath, "certs", "server.pfx");
var httpsReady = builder.Configuration.GetValue("Https", false) && File.Exists(pfxPath);
var shortReady = CanBind(ShortPort);       // 80 被别的软件占了就安静跳过

// 云平台（Render 之类）会用环境变量 PORT 告诉程序监听哪个口，没设就用本地的 5080
var mainPort = Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } rawPort
    && int.TryParse(rawPort, out var parsedPort) && parsedPort is > 0 and < 65536
    ? parsedPort
    : HttpPort;

builder.WebHost.ConfigureKestrel(o =>
{
    o.AddServerHeader = false;                    // 不对外报 "Kestrel/8.x"
    o.ListenAnyIP(mainPort);
    if (shortReady)
    {
        o.ListenAnyIP(ShortPort);
    }
    if (httpsReady)
    {
        o.ListenAnyIP(HttpsPort, listen => listen.UseHttps(pfxPath, (string?)null));
    }
});

// ---------------- 服务注册 ----------------
builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<SecretHasher>();
builder.Services.AddSingleton<GateService>();
builder.Services.AddSingleton<AdminService>();
builder.Services.AddSingleton<RoomManager>();
builder.Services.AddSingleton<PresenceService>();
builder.Services.AddSingleton<SocialService>();
builder.Services.AddSignalR();

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "nb_user";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;   // 走 https 就自动带 Secure
        options.ExpireTimeSpan = TimeSpan.FromDays(90);
        options.SlidingExpiration = true;
        // 这是接口和页面，不需要 302 到登录页，直接给 401
        options.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();

// ---------------- 传输压缩 ----------------
// 手机流量下最要紧的是少传字节：CSS/JS/JSON 统一 Brotli，老浏览器自动退 gzip。
// 家里上行带宽有限，压缩后从这台机器发出去的字节大约只有原来的四分之一。
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;          // 走隧道时这边看到的是 http，其实对端是 https，得强制开着
    o.Providers.Add<BrotliCompressionProvider>();
    o.Providers.Add<GzipCompressionProvider>();
    o.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[]
    {
        "application/json", "image/svg+xml", "application/manifest+json"
    });
});
// 静态资源浏览器会存一年，压缩只算一次，所以用 Optimal 换更小的体积，别用 Fastest
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Optimal);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

// ---------------- 限流 ----------------
// 门禁、注册登录、后台登录各一条线，按 IP 分开算。真人打字没那么快。
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (ctx, token) =>
    {
        ctx.HttpContext.Response.ContentType = "application/json; charset=utf-8";
        await ctx.HttpContext.Response.WriteAsync(
            "{\"ok\":false,\"error\":\"手有点快，歇一分钟再来\"}", token);
    };

    static string Ip(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    o.AddPolicy("gate", ctx => RateLimitPartition.GetFixedWindowLimiter("gate:" + Ip(ctx), _ =>
        new FixedWindowRateLimiterOptions { PermitLimit = 12, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter("auth:" + Ip(ctx), _ =>
        new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

    o.AddPolicy("admin", ctx => RateLimitPartition.GetFixedWindowLimiter("admin:" + Ip(ctx), _ =>
        new FixedWindowRateLimiterOptions { PermitLimit = 8, Window = TimeSpan.FromMinutes(5), QueueLimit = 0 }));
    // 后台登录后的常规操作（查列表/删号/调分）：放宽到 60/分，不然清几个号就把额度吃光
    o.AddPolicy("adminOps", ctx => RateLimitPartition.GetFixedWindowLimiter("adminOps:" + Ip(ctx), _ =>
        new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var app = builder.Build();

var gate = app.Services.GetRequiredService<GateService>();
var admin = app.Services.GetRequiredService<AdminService>();
var db = app.Services.GetRequiredService<Db>();          // 顺便把表建好
var rooms = app.Services.GetRequiredService<RoomManager>();

// ---------------- 隧道 / 反向代理 ----------------
// 走隧道时，请求是 cloudflared 从 127.0.0.1 转进来的，原始访客 IP 在 X-Forwarded-For、
// 协议在 X-Forwarded-Proto。只信任本机转发，别的一概不认——否则局域网里谁都能伪造个头绕限流。
// 在云平台上（NB_TRUST_PROXY=1）只有平台的反向代理能碰到程序，外面进不来，谁转来的都信。
var trustAnyProxy = Environment.GetEnvironmentVariable("NB_TRUST_PROXY") == "1";
var fwdOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    ForwardLimit = trustAnyProxy ? null : 1,
    KnownProxies = { IPAddress.Loopback, IPAddress.IPv6Loopback }
};
if (trustAnyProxy)
{
    fwdOptions.KnownProxies.Clear();
    fwdOptions.KnownNetworks.Clear();     // 不圈定转发者 = 平台代理转来的都认
}
app.UseForwardedHeaders(fwdOptions);

// ---------------- 兜底异常 ----------------
// 出错只回一句人话，堆栈只写进控制台，不给外面看。
app.UseExceptionHandler(errApp => errApp.Run(async ctx =>
{
    var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    var log = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Unhandled");
    log.LogError(ex, "请求出错：{Method} {Path}", ctx.Request.Method, ctx.Request.Path);

    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
    ctx.Response.ContentType = "application/json; charset=utf-8";
    await ctx.Response.WriteAsync("{\"ok\":false,\"error\":\"刚刚有点卡，再来一次？\"}");
}));

// ---------------- 安全响应头 ----------------
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
    h["X-Robots-Tag"] = "noindex, nofollow";
    // 资源只从本站来；WS 给 SignalR 用；样式允许行内（画板、进度条要写 style）
    h["Content-Security-Policy"] =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob:; " +
        "connect-src 'self' ws: wss:; " +
        "font-src 'self'; " +
        "base-uri 'none'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'; " +
        "object-src 'none'";
    await next();
});

// 说明：这里故意不发 HSTS。
// HSTS 会让浏览器记住"这个域名以后只能走 https"，一旦记住，http://192.168.1.7:5080
// 就会被强行升级成 https://…:5080（那儿没服务），朋友反而打不开。局域网小站不值这个风险。


app.UseRateLimiter();

// 压缩要放在静态文件前面，不然 css/js 就不经过它了
app.UseResponseCompression();

// ---------------- 首屏样式内联 ----------------
// 手机上打开慢，一半时间花在"等第二次往返"：HTML 到了，浏览器再去要 css。
// 这里把 style.css 直接嵌进 index.html，首屏少一个来回。样式源文件还是 css/style.css，
// 改样式不用动这里；文件改了就自动重新嵌（按修改时间判断）。
var webRoot = app.Environment.WebRootPath;
(long htmlStamp, long cssStamp, string html) pageCache = (0, 0, "");
var pageLock = new object();

string RenderIndex()
{
    string htmlPath = Path.Combine(webRoot, "index.html");
    string cssPath = Path.Combine(webRoot, "css", "style.css");
    var htmlStamp = File.GetLastWriteTimeUtc(htmlPath).Ticks;
    var cssStamp = File.Exists(cssPath) ? File.GetLastWriteTimeUtc(cssPath).Ticks : 0;

    lock (pageLock)
    {
        if (pageCache.htmlStamp == htmlStamp && pageCache.cssStamp == cssStamp && pageCache.html.Length > 0)
            return pageCache.html;

        var html = File.ReadAllText(htmlPath);
        if (cssStamp != 0)
        {
            var css = File.ReadAllText(cssPath);
            // 把 <link rel="stylesheet" href="/css/style.css?v=…" /> 整条换掉
            html = System.Text.RegularExpressions.Regex.Replace(
                html,
                "<link[^>]*rel=\"stylesheet\"[^>]*style\\.css[^>]*>",
                "<style>\n" + css + "\n</style>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        pageCache = (htmlStamp, cssStamp, html);
        return html;
    }
}

app.Use(async (ctx, next) =>
{
    var isIndex = ctx.Request.Path == "/" ||
                  ctx.Request.Path.Equals("/index.html", StringComparison.OrdinalIgnoreCase);
    if (isIndex && HttpMethods.IsGet(ctx.Request.Method))
    {
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        await ctx.Response.WriteAsync(RenderIndex());
        return;
    }
    await next();
});

// ---------------- 静态文件 ----------------
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var name = ctx.File.Name;
        if (name.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || name == "version.json")
        {
            // 入口页面和版本号永远不要缓存，不然发了新版别人看不到
            ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            ctx.Context.Response.Headers["Pragma"] = "no-cache";
        }
        else
        {
            // css/js 的引用都带 ?v=版本号，版本一变地址就变，所以可以放心让浏览器存一年。
            // 手机流量下这一条最值钱：第二次进来这些文件一个字节都不用重新下载。
            ctx.Context.Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
        }
    }
});

// ---------------- 后台门禁 ----------------
// 没票的时候，/admin/api/* 一律 404 —— 不告诉外面"这儿有个后台，只是你没权限"。
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path;
    if (path.StartsWithSegments("/admin/api") && !path.StartsWithSegments("/admin/api/login"))
    {
        if (!admin.IsAdmin(ctx))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
    }
    await next();
});

// ---------------- 访问密码闸门 ----------------
// 除了"验密码"这个接口本身，所有 /api 和 /hubs 都要先过闸机。
// /admin 走自己的密钥，不跟朋友的访问密码混在一起。
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path;
    var needsGate =
        (path.StartsWithSegments("/api") && !path.StartsWithSegments("/api/gate")
                                          && !path.StartsWithSegments("/api/boot")) ||
        path.StartsWithSegments("/hubs");

    if (needsGate && !gate.IsUnlocked(ctx))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsJsonAsync(new { ok = false, error = "gate" });
        return;
    }

    await next();
});

app.UseAuthentication();
app.UseAuthorization();

// ---------------- 门禁 ----------------
app.MapPost("/api/gate/check", (HttpContext ctx, GateRequest req) =>
{
    var key = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var result = gate.TryUnlock(req.Password, key);

    if (result.Ok)
    {
        gate.IssueCookie(ctx);
        return Results.Json(new { ok = true });
    }

    if (result.LockSeconds > 0)
        return Results.Json(new { ok = false, locked = result.LockSeconds }, statusCode: StatusCodes.Status429TooManyRequests);

    return Results.Json(new { ok = false, left = result.Remaining }, statusCode: StatusCodes.Status400BadRequest);
}).RequireRateLimiting("gate");

app.MapGet("/api/gate/state", (HttpContext ctx) => Results.Json(new { unlocked = gate.IsUnlocked(ctx) }));

// 进门只要一次往返：门禁状态和「我是谁」一起给。
// 外网每个来回 200ms 起，少一次就是少等半秒——这是手机上最能感觉到的一处优化。
app.MapGet("/api/boot", (HttpContext ctx) =>
{
    ctx.Response.Headers["Cache-Control"] = "no-store";
    var unlocked = gate.IsUnlocked(ctx);
    // 没解锁就什么都不多说，省得外面靠这个接口试出谁登着号
    var user = unlocked && ctx.User.Identity?.IsAuthenticated == true ? CurrentUser(ctx) : null;
    return Results.Json(new
    {
        unlocked,
        me = user is null ? null : MeDto(user)
    });
});

// ---------------- 账号 ----------------
app.MapPost("/api/auth/register", async (HttpContext ctx, AuthRequest req, SecretHasher hasher) =>
{
    var name = (req.Username ?? "").Trim();
    var pwd = req.Password ?? "";

    if (name.Length is < 2 or > 12)
        return Results.Json(new { ok = false, error = "名字 2~12 个字就好" }, statusCode: 400);
    if (pwd.Length < 4)
        return Results.Json(new { ok = false, error = "密码至少四位，别太好猜" }, statusCode: 400);
    if (db.FindByName(name) is not null)
        return Results.Json(new { ok = false, error = "这个名字有人用啦，换一个？" }, statusCode: 400);

    int id;
    try
    {
        id = db.Create(name, hasher.Hash(pwd), AvatarPresets.Normalize(req.Avatar));
    }
    catch (SqliteException)
    {
        return Results.Json(new { ok = false, error = "这个名字有人用啦，换一个？" }, statusCode: 400);
    }

    db.TouchLogin(id);
    var user = db.FindById(id)!;
    await SignIn(ctx, user);
    return Results.Json(new { ok = true, me = MeDto(user) });
}).RequireRateLimiting("auth");

app.MapPost("/api/auth/login", async (HttpContext ctx, AuthRequest req, SecretHasher hasher) =>
{
    var name = (req.Username ?? "").Trim();
    var pwd = req.Password ?? "";
    var user = db.FindByName(name);

    // 名字不存在也照样算一次哈希，别让人靠响应快慢试出哪些名字是真的
    if (user is null)
    {
        hasher.Verify(pwd, DummyHash);
        return Results.Json(new { ok = false, error = "名字或密码不太对，再想一下？" }, statusCode: 400);
    }

    if (!hasher.Verify(pwd, user.PasswordHash))
        return Results.Json(new { ok = false, error = "名字或密码不太对，再想一下？" }, statusCode: 400);

    // 老库里的哈希用的是低迭代，登进来的这一刻顺手换成现在的强度
    if (SecretHasher.NeedsRehash(user.PasswordHash))
        db.UpdatePasswordHash(user.Id, hasher.Hash(pwd));

    db.TouchLogin(user.Id);
    await SignIn(ctx, user);
    return Results.Json(new { ok = true, me = MeDto(user) });
}).RequireRateLimiting("auth");

app.MapPost("/api/auth/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Json(new { ok = true });
});

// ---------------- 我的资料 ----------------
app.MapGet("/api/me", (HttpContext ctx) =>
{
    var user = CurrentUser(ctx);
    return user is null
        ? Results.Json(new { ok = false, error = "还没登录" }, statusCode: 401)
        : Results.Json(new { ok = true, me = MeDto(user) });
}).RequireAuthorization();

app.MapPost("/api/me/avatar", (HttpContext ctx, AvatarRequest req) =>
{
    var user = CurrentUser(ctx);
    if (user is null) return Results.Json(new { ok = false, error = "还没登录" }, statusCode: 401);

    // 选现成的
    if (!string.IsNullOrWhiteSpace(req.Preset))
    {
        if (!AvatarPresets.IsValid(req.Preset))
            return Results.Json(new { ok = false, error = "这个头像不认识" }, statusCode: 400);
        db.SetPresetAvatar(user.Id, req.Preset!);
        return Results.Json(new { ok = true, me = MeDto(db.FindById(user.Id)!) });
    }

    // 自己画的
    var dataUrl = req.DataUrl ?? "";
    const string prefix = "data:image/png;base64,";
    if (!dataUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        return Results.Json(new { ok = false, error = "画好像没存上，再点一次完成？" }, statusCode: 400);

    var base64 = dataUrl[prefix.Length..];
    if (!IsSquarePng(base64, out _))
        return Results.Json(new { ok = false, error = "这张图不太对，重画一张试试？" }, statusCode: 400);

    db.SetCustomAvatar(user.Id, base64);
    return Results.Json(new { ok = true, me = MeDto(db.FindById(user.Id)!) });
}).RequireAuthorization();

app.MapGet("/api/avatar/{id:int}", (int id, HttpContext ctx) =>
{
    var user = db.FindById(id);
    if (user?.AvatarData is null) return Results.NotFound();
    if (!IsSquarePng(user.AvatarData, out var bytes)) return Results.NotFound();

    // 头像几乎不变，让别人那边存一会儿；自己改头像时前端会带 ?t= 绕开缓存
    ctx.Response.Headers["Cache-Control"] = "public, max-age=600";
    return Results.File(bytes, "image/png");
});

/// <summary>改自己的名字和签名。名字改了，牌桌、好友那边一起跟着变。</summary>
app.MapPost("/api/me/profile", async (HttpContext ctx, ProfileRequest req, SocialService social) =>
{
    var me = CurrentUser(ctx);
    if (me is null) return Results.Json(new { ok = false, error = "还没登录" }, statusCode: 401);

    var name = (req.Username ?? me.Username).Trim();
    var bio = (req.Bio ?? db.Bio(me.Id)).Trim();

    if (name.Length is < 2 or > 12)
        return Results.Json(new { ok = false, error = "名字 2 到 12 个字" }, statusCode: 400);
    if (name.Any(char.IsControl))
        return Results.Json(new { ok = false, error = "名字里别放换行这类字符" }, statusCode: 400);
    if (bio.Length > 60) bio = bio[..60];

    if (!string.Equals(name, me.Username, StringComparison.Ordinal))
    {
        // 名单里的开发者改名等于把身份弄丢了，直接拦住
        if (Roles.IsDevName(me.Username) && !Roles.IsDevName(name))
            return Results.Json(new { ok = false, error = "开发者名字写在名单里，改名得先改配置" }, statusCode: 400);

        if (db.FindByName(name) is not null)
            return Results.Json(new { ok = false, error = "这个名字已经有人用了" }, statusCode: 400);

        if (!db.SetUsername(me.Id, name))
            return Results.Json(new { ok = false, error = "这个名字已经有人用了" }, statusCode: 400);

        rooms.Rename(me.Id, name);
        await social.ToFriends(me.Id, "friend", new { id = me.Id, name });
    }

    db.SetBio(me.Id, bio);
    return Results.Json(new { ok = true, me = MeDto(db.FindById(me.Id)!) });
}).RequireAuthorization();

// ---------------- 好友 ----------------
// 这里返回的只有公开信息（名字、头像、积分、金币、在线与否），永远不带密码哈希。

app.MapGet("/api/friends", (HttpContext ctx, PresenceService presence) =>
{
    var me = CurrentUser(ctx);
    if (me is null) return Results.Json(new { ok = false }, statusCode: 401);

    return Results.Json(new
    {
        ok = true,
        friends = db.Friends(me.Id).Select(p => PersonJson(p, presence)).ToArray(),
        incoming = db.IncomingRequests(me.Id).Select(p => PersonJson(p, presence)).ToArray(),
        outgoing = db.OutgoingRequests(me.Id).Select(p => PersonJson(p, presence)).ToArray()
    });
}).RequireAuthorization();

app.MapGet("/api/people", (HttpContext ctx, string? q, PresenceService presence) =>
{
    var me = CurrentUser(ctx);
    if (me is null) return Results.Json(new { ok = false }, statusCode: 401);

    var people = db.SearchPeople(q ?? "", me.Id).Select(p => PersonJson(p, presence)).ToArray();
    return Results.Json(new { ok = true, people });
}).RequireAuthorization();

app.MapPost("/api/friends/request", async (HttpContext ctx, FriendRequest req, SocialService social) =>
{
    var me = CurrentUser(ctx);
    if (me is null) return Results.Json(new { ok = false }, statusCode: 401);

    var target = db.FindByName((req.Name ?? "").Trim());
    if (target is null) return Results.Json(new { ok = false, error = "没找到这个人，名字对不对？" }, statusCode: 400);

    var err = db.AddRequest(me.Id, target.Id);
    if (err.Length > 0) return Results.Json(new { ok = false, error = err }, statusCode: 400);

    await social.ToUser(target.Id, "friend", new { kind = "request", from = me.Username });
    return Results.Json(new { ok = true });
}).RequireAuthorization();

app.MapPost("/api/friends/answer", async (HttpContext ctx, FriendAnswer req, SocialService social) =>
{
    var me = CurrentUser(ctx);
    if (me is null) return Results.Json(new { ok = false }, statusCode: 401);
    if (req.Id == me.Id) return Results.Json(new { ok = false, error = "自己加自己？" }, statusCode: 400);

    if (!req.Accept)
    {
        db.DropRequest(req.Id, me.Id);
        return Results.Json(new { ok = true });
    }

    if (!db.AcceptRequest(req.Id, me.Id))
        return Results.Json(new { ok = false, error = "这条申请失效了，刷新看看？" }, statusCode: 400);

    await social.ToUser(req.Id, "friend", new { kind = "accepted", by = me.Username });
    return Results.Json(new { ok = true });
}).RequireAuthorization();

app.MapPost("/api/friends/remove", async (HttpContext ctx, PersonRef req, SocialService social) =>
{
    var me = CurrentUser(ctx);
    if (me is null) return Results.Json(new { ok = false }, statusCode: 401);

    db.RemoveFriend(me.Id, req.Id);
    await social.ToUser(req.Id, "friend", new { kind = "removed" });
    return Results.Json(new { ok = true });
}).RequireAuthorization();

// ---------------- 站内信 ----------------

app.MapGet("/api/mail", (HttpContext ctx, string? box) =>
{
    var me = CurrentUser(ctx);
    if (me is null) return Results.Json(new { ok = false }, statusCode: 401);

    var inbox = !string.Equals(box, "out", StringComparison.OrdinalIgnoreCase);
    return Results.Json(new
    {
        ok = true,
        unread = db.UnreadMail(me.Id),
        mails = db.Mail(me.Id, inbox).Select(m => MailJson(m, me.Id)).ToArray()
    });
}).RequireAuthorization();

app.MapGet("/api/mail/{id:int}", (HttpContext ctx, int id) =>
{
    var me = CurrentUser(ctx);
    if (me is null) return Results.Json(new { ok = false }, statusCode: 401);

    var m = db.GetMail(id, me.Id);
    if (m is null) return Results.Json(new { ok = false }, statusCode: 404);

    if (m.ToId == me.Id && !m.IsRead) db.MarkMailRead(id, me.Id);
    return Results.Json(new
    {
        ok = true,
        unread = db.UnreadMail(me.Id),
        mail = new
        {
            id = m.Id,
            fromId = m.FromId,
            fromName = m.FromName,
            fromBadges = Roles.BadgesFor(m.FromName, db.Role(m.FromId)),
            toName = m.ToName,
            toBadges = Roles.BadgesFor(m.ToName, db.Role(m.ToId)),
            subject = m.Subject,
            body = m.Body,
            createdAt = m.CreatedAt,
            mine = m.FromId == me.Id
        }
    });
}).RequireAuthorization();

app.MapPost("/api/mail", async (HttpContext ctx, MailRequest req, SocialService social) =>
{
    var me = CurrentUser(ctx);
    if (me is null) return Results.Json(new { ok = false }, statusCode: 401);

    var toName = (req.To ?? "").Trim();
    if (toName.Length == 0) return Results.Json(new { ok = false, error = "写给谁呀？" }, statusCode: 400);

    var target = db.FindByName(toName);
    if (target is null) return Results.Json(new { ok = false, error = "没找到这个人，名字对不对？" }, statusCode: 400);
    if (target.Id == me.Id) return Results.Json(new { ok = false, error = "给自己写信有点寂寞" }, statusCode: 400);

    var body = (req.Body ?? "").Trim();
    if (body.Length == 0) return Results.Json(new { ok = false, error = "信纸是空的" }, statusCode: 400);
    if (body.Length > 2000) body = body[..2000];

    var subject = (req.Subject ?? "").Trim();
    if (subject.Length == 0) subject = "（没有标题）";
    if (subject.Length > 40) subject = subject[..40];

    var id = db.SendMail(me.Id, target.Id, subject, body);
    await social.ToUser(target.Id, "mail", new { unread = db.UnreadMail(target.Id), from = me.Username });
    return Results.Json(new { ok = true, id });
}).RequireAuthorization();

app.MapDelete("/api/mail/{id:int}", (HttpContext ctx, int id) =>
{
    var me = CurrentUser(ctx);
    if (me is null) return Results.Json(new { ok = false }, statusCode: 401);

    var gone = db.DeleteMail(id, me.Id);
    return Results.Json(new { ok = gone, unread = db.UnreadMail(me.Id) });
}).RequireAuthorization();

// ---------------- 道具商店 ----------------

app.MapGet("/api/shop", (HttpContext ctx) =>
{
    var me = CurrentUser(ctx);
    if (me is null) return Results.Json(new { ok = false }, statusCode: 401);

    var inv = db.Inventory(me.Id);
    return Results.Json(new
    {
        ok = true,
        coins = db.Coins(me.Id),
        items = Shop.All.Select(i => new
        {
            kind = i.Kind,
            name = i.Name,
            emoji = i.Emoji,
            desc = i.Desc,
            price = i.Price,
            where = i.Where,
            owned = inv.GetValueOrDefault(i.Kind)
        }).ToArray()
    });
}).RequireAuthorization();

app.MapPost("/api/shop/buy", (HttpContext ctx, BuyRequest req) =>
{
    var me = CurrentUser(ctx);
    if (me is null) return Results.Json(new { ok = false }, statusCode: 401);

    var item = Shop.Find(req.Kind);
    if (item is null) return Results.Json(new { ok = false, error = "没这个道具" }, statusCode: 400);

    var (ok, coins) = db.BuyItem(me.Id, item.Kind, item.Price);
    return ok
        ? Results.Json(new { ok = true, coins, owned = db.Inventory(me.Id).GetValueOrDefault(item.Kind) })
        : Results.Json(new { ok = false, error = $"金币不够，还差 {item.Price - coins} 个。赢一把能赚 {Shop.CoinsForWin}", coins }, statusCode: 400);
}).RequireAuthorization();

// ---------------- 对话 ----------------
// 私聊只对好友开放；大厅公屏谁都能说。禁言由管理员/开发者设置，两个地方都拦。

object ChatJson(ChatRow c, int meId) => new
{
    id = c.Id,
    fromId = c.FromId,
    fromName = c.FromName,
    role = Roles.Effective(c.FromName, c.Role),
    badges = Roles.BadgesFor(c.FromName, c.Role),
    avatar = c.Avatar,
    isCustomAvatar = c.IsCustomAvatar,
    toId = c.ToId,
    body = c.Body,
    at = c.CreatedAt,
    mine = c.FromId == meId
};

object PeerJson(PersonRow p, PresenceService presence) => new
{
    id = p.Id,
    name = p.Username,
    role = Roles.Effective(p.Username, p.Role),
    badges = Roles.BadgesFor(p.Username, p.Role),
    avatar = p.Avatar,
    isCustomAvatar = p.IsCustomAvatar,
    online = presence.IsOnline(p.Id)
};

app.MapGet("/api/chat/threads", (HttpContext ctx, PresenceService presence) =>
{
    var me = CurrentUser(ctx);
    if (me is null) return Results.Json(new { ok = false }, statusCode: 401);

    var threads = db.Threads(me.Id).Select(t => new
    {
        id = t.PeerId,
        name = t.PeerName,
        role = Roles.Effective(t.PeerName, t.Role),
        badges = Roles.BadgesFor(t.PeerName, t.Role),
        avatar = t.Avatar,
        isCustomAvatar = t.IsCustomAvatar,
        last = t.Body,
        at = t.CreatedAt,
        mine = t.Mine,
        unread = t.Unread,
        online = presence.IsOnline(t.PeerId)
    }).ToArray();

    return Results.Json(new
    {
        ok = true,
        unread = db.UnreadChats(me.Id),
        muted = db.IsMuted(me.Id),
        threads
    });
}).RequireAuthorization();

app.MapGet("/api/chat/lobby", (HttpContext ctx, int? before) =>
{
    var me = CurrentUser(ctx);
    if (me is null) return Results.Json(new { ok = false }, statusCode: 401);

    return Results.Json(new
    {
        ok = true,
        muted = db.IsMuted(me.Id),
        messages = db.Lobby(before ?? 0).Select(c => ChatJson(c, me.Id)).ToArray()
    });
}).RequireAuthorization();

app.MapGet("/api/chat/{peerId:int}", (HttpContext ctx, int peerId, int? before, PresenceService presence) =>
{
    var me = CurrentUser(ctx);
    if (me is null) return Results.Json(new { ok = false }, statusCode: 401);
    if (peerId == me.Id) return Results.Json(new { ok = false, error = "和自己聊天有点寂寞" }, statusCode: 400);

    var peer = db.FindById(peerId);
    if (peer is null) return Results.Json(new { ok = false, error = "这个人不在了" }, statusCode: 404);

    var history = db.ChatHistory(me.Id, peerId, before ?? 0);
    if (history.Count == 0 && !db.AreFriends(me.Id, peerId))
        return Results.Json(new { ok = false, error = "先加好友才能聊" }, statusCode: 403);

    db.MarkChatRead(me.Id, peerId);
    return Results.Json(new
    {
        ok = true,
        peer = PeerJson(new PersonRow(peer.Id, peer.Username, peer.Avatar, peer.Avatar == AvatarPresets.Custom, peer.Points, peer.Coins, db.Role(peer.Id)), presence),
        messages = history.Select(c => ChatJson(c, me.Id)).ToArray(),
        unread = db.UnreadChats(me.Id)
    });
}).RequireAuthorization();

app.MapPost("/api/chat/{peerId:int}/read", (HttpContext ctx, int peerId) =>
{
    var me = CurrentUser(ctx);
    if (me is null) return Results.Json(new { ok = false }, statusCode: 401);

    db.MarkChatRead(me.Id, peerId);
    return Results.Json(new { ok = true, unread = db.UnreadChats(me.Id) });
}).RequireAuthorization();

// ---------------- 管理（管理员 + 开发者） ----------------

app.MapGet("/api/mod/overview", (HttpContext ctx, PresenceService presence) =>
{
    var me = CurrentUser(ctx);
    if (me is null) return Results.Json(new { ok = false }, statusCode: 401);

    var role = Roles.Effective(me.Username, db.Role(me.Id));
    if (role == Roles.User) return Results.Json(new { ok = false, error = "这扇门不对你开" }, statusCode: 403);

    var online = db.PeopleByIds(presence.OnlineIds).Select(p => new
    {
        id = p.Id,
        name = p.Username,
        role = Roles.Effective(p.Username, p.Role),
        badges = Roles.BadgesFor(p.Username, p.Role),
        avatar = p.Avatar,
        isCustomAvatar = p.IsCustomAvatar,
        muted = db.IsMuted(p.Id),
        online = true
    }).ToArray();

    return Results.Json(new
    {
        ok = true,
        me = new { id = me.Id, role, badges = Roles.BadgesFor(me.Username, role) },
        online,
        rooms = rooms.AdminRooms(),
        staff = role == Roles.Dev ? db.Staff().Select(p => PersonJson(p, presence)).ToArray() : null
    });
}).RequireAuthorization();

app.MapPost("/api/mod/mute", async (HttpContext ctx, MuteRequest req, SocialService social) =>
{
    var me = CurrentUser(ctx);
    if (me is null) return Results.Json(new { ok = false }, statusCode: 401);

    var myRole = Roles.Effective(me.Username, db.Role(me.Id));
    if (myRole == Roles.User) return Results.Json(new { ok = false, error = "这扇门不对你开" }, statusCode: 403);
    if (req.UserId == me.Id) return Results.Json(new { ok = false, error = "别禁自己" }, statusCode: 400);

    var target = db.FindById(req.UserId);
    if (target is null) return Results.Json(new { ok = false, error = "这个人不在了" }, statusCode: 404);

    // 管理员动不了管理员和开发者，只有开发者能管同级以下所有人
    var targetRole = Roles.Effective(target.Username, db.Role(target.Id));
    if (myRole == Roles.Admin && targetRole != Roles.User)
        return Results.Json(new { ok = false, error = "这个人和你平级，动不了" }, statusCode: 403);

    var minutes = Math.Clamp(req.Minutes, 0, 7 * 24 * 60);
    db.Mute(target.Id, minutes);

    await social.ToUser(target.Id, "notice", minutes > 0
        ? $"你被禁言 {minutes} 分钟，先安静一会儿"
        : "禁言解除了，可以说话了");
    await social.ToUser(target.Id, "mute", new
    {
        minutes,
        until = minutes > 0 ? db.MuteUntil(target.Id)?.ToString("yyyy-MM-dd HH:mm:ss") : null
    });
    return Results.Json(new { ok = true, minutes, muted = minutes > 0 });
}).RequireAuthorization();

app.MapPost("/api/mod/kick", async (HttpContext ctx, KickRequest req, SocialService social) =>
{
    var me = CurrentUser(ctx);
    if (me is null) return Results.Json(new { ok = false }, statusCode: 401);

    var myRole = Roles.Effective(me.Username, db.Role(me.Id));
    if (myRole == Roles.User) return Results.Json(new { ok = false, error = "这扇门不对你开" }, statusCode: 403);
    if (req.UserId == me.Id) return Results.Json(new { ok = false, error = "自己踢自己？" }, statusCode: 400);

    var target = db.FindById(req.UserId);
    if (target is null) return Results.Json(new { ok = false, error = "这个人不在了" }, statusCode: 404);

    var targetRole = Roles.Effective(target.Username, db.Role(target.Id));
    if (myRole == Roles.Admin && targetRole != Roles.User)
        return Results.Json(new { ok = false, error = "这个人和你平级，动不了" }, statusCode: 403);

    var code = rooms.Kick(target.Id);
    if (code is null) return Results.Json(new { ok = false, error = "他不在任何房间里" }, statusCode: 400);

    await social.ToUser(target.Id, "kicked", new { code, by = me.Username });
    return Results.Json(new { ok = true, code });
}).RequireAuthorization();

app.MapPost("/api/mod/announce", async (HttpContext ctx, AnnounceRequest req, SocialService social) =>
{
    var me = CurrentUser(ctx);
    if (me is null) return Results.Json(new { ok = false }, statusCode: 401);

    var role = Roles.Effective(me.Username, db.Role(me.Id));
    if (role == Roles.User) return Results.Json(new { ok = false, error = "这扇门不对你开" }, statusCode: 403);

    var text = (req.Text ?? "").Trim();
    if (text.Length == 0) return Results.Json(new { ok = false, error = "公告不能是空的" }, statusCode: 400);
    if (text.Length > 120) text = text[..120];

    await social.ToAll("announce", new { text, by = me.Username, role });
    return Results.Json(new { ok = true, text });
}).RequireAuthorization();

// ---------------- 开发者专属：任命 / 调分 ----------------

app.MapPost("/api/roles/set", async (HttpContext ctx, RoleRequest req, SocialService social, PresenceService presence) =>
{
    var me = CurrentUser(ctx);
    if (me is null) return Results.Json(new { ok = false }, statusCode: 401);

    var myRole = Roles.Effective(me.Username, db.Role(me.Id));
    if (myRole != Roles.Dev) return Results.Json(new { ok = false, error = "只有开发者能任命" }, statusCode: 403);
    if (req.UserId == me.Id) return Results.Json(new { ok = false, error = "你自己就是开发者" }, statusCode: 400);

    var target = db.FindById(req.UserId);
    if (target is null) return Results.Json(new { ok = false, error = "这个人不在了" }, statusCode: 404);

    var wanted = Roles.Normalize(req.Role);
    if (wanted == Roles.Dev) return Results.Json(new { ok = false, error = "开发者是写死的，不能任命" }, statusCode: 400);

    // 名单里的人天生就是 dev，别想着降级
    if (Roles.IsDevName(target.Username))
        return Results.Json(new { ok = false, error = "他是写在名单里的开发者，改不了" }, statusCode: 400);

    db.SetRole(target.Id, wanted);
    await social.ToUser(target.Id, "role", new
    {
        role = wanted,
        badges = Roles.BadgesFor(target.Username, wanted),
        text = wanted == Roles.Admin ? "你现在是管理员了，大厅顶栏多了个「管理」" : "你的管理员身份被取消了"
    });
    return Results.Json(new
    {
        ok = true,
        person = PersonJson(new PersonRow(target.Id, target.Username, target.Avatar, target.Avatar == AvatarPresets.Custom, target.Points, target.Coins, wanted), presence)
    });
}).RequireAuthorization();

app.MapPost("/api/mod/points", (HttpContext ctx, ModPointsRequest req) =>
{
    var me = CurrentUser(ctx);
    if (me is null) return Results.Json(new { ok = false }, statusCode: 401);

    var role = Roles.Effective(me.Username, db.Role(me.Id));
    if (role != Roles.Dev) return Results.Json(new { ok = false, error = "只有开发者能调分" }, statusCode: 403);

    var target = db.FindById(req.UserId);
    if (target is null) return Results.Json(new { ok = false, error = "这个人不在了" }, statusCode: 404);

    var points = req.Set is { } set ? db.SetPoints(target.Id, set) : db.AddPoints(target.Id, Math.Clamp(req.Delta ?? 0, -10000, 10000));
    var coins = req.Coins is { } dc ? db.AddCoins(target.Id, Math.Clamp(dc, -10000, 10000)) : db.Coins(target.Id);
    return Results.Json(new { ok = true, points, coins });
}).RequireAuthorization();

app.MapGet("/api/mod/find", (HttpContext ctx, string? q, PresenceService presence) =>
{
    var me = CurrentUser(ctx);
    if (me is null) return Results.Json(new { ok = false }, statusCode: 401);

    var role = Roles.Effective(me.Username, db.Role(me.Id));
    if (role == Roles.User) return Results.Json(new { ok = false, error = "这扇门不对你开" }, statusCode: 403);

    var u = db.FindByName((q ?? "").Trim());
    if (u is null) return Results.Json(new { ok = false, error = "没找到这个人" }, statusCode: 404);

    var row = new PersonRow(u.Id, u.Username, u.Avatar, u.Avatar == AvatarPresets.Custom, u.Points, u.Coins, db.Role(u.Id));
    return Results.Json(new { ok = true, person = PersonJson(row, presence) });
}).RequireAuthorization();

// ---------------- 管理员加料：解散房间 / 清空公屏 ----------------

app.MapPost("/api/mod/close-room", async (HttpContext ctx, CloseRoomRequest req, SocialService social) =>
{
    var me = CurrentUser(ctx);
    if (me is null) return Results.Json(new { ok = false }, statusCode: 401);
    if (Roles.Effective(me.Username, db.Role(me.Id)) == Roles.User)
        return Results.Json(new { ok = false, error = "这扇门不对你开" }, statusCode: 403);

    var code = (req.Code ?? "").Trim().ToUpperInvariant();
    var people = rooms.CloseRoom(code);
    if (people.Count == 0) return Results.Json(new { ok = false, error = "这个房间已经不在了" }, statusCode: 404);

    foreach (var id in people)
        await social.ToUser(id, "kicked", new { code, by = me.Username });
    return Results.Json(new { ok = true, code, people = people.Count });
}).RequireAuthorization();

app.MapPost("/api/mod/clear-lobby", async (HttpContext ctx, SocialService social) =>
{
    var me = CurrentUser(ctx);
    if (me is null) return Results.Json(new { ok = false }, statusCode: 401);
    if (Roles.Effective(me.Username, db.Role(me.Id)) == Roles.User)
        return Results.Json(new { ok = false, error = "这扇门不对你开" }, statusCode: 403);

    var deleted = db.ClearLobby();
    await social.ToAll("lobbyCleared", new { by = me.Username });
    return Results.Json(new { ok = true, deleted });
}).RequireAuthorization();

// ---------------- 开发者专属：账号台账 / 删号 / 清空 / 重置 ----------------

app.MapGet("/api/mod/accounts", (HttpContext ctx, PresenceService presence) =>
{
    var me = CurrentUser(ctx);
    if (me is null) return Results.Json(new { ok = false }, statusCode: 401);
    if (Roles.Effective(me.Username, db.Role(me.Id)) != Roles.Dev)
        return Results.Json(new { ok = false, error = "只有开发者能看账号台账" }, statusCode: 403);

    var rows = db.AllAccounts().Select(a => new
    {
        id = a.Id,
        name = a.Username,
        role = Roles.Effective(a.Username, a.Role),
        badges = Roles.BadgesFor(a.Username, a.Role),
        points = a.Points,
        coins = a.Coins,
        avatar = a.Avatar,
        isCustomAvatar = a.IsCustomAvatar,
        createdAt = a.CreatedAt,
        lastLoginAt = a.LastLoginAt,
        online = presence.IsOnline(a.Id),
        dev = Roles.IsDevName(a.Username),
        self = a.Id == me.Id
    }).ToArray();

    return Results.Json(new { ok = true, accounts = rows, total = rows.Length });
}).RequireAuthorization();

app.MapPost("/api/mod/delete-user", async (HttpContext ctx, DeleteUserRequest req, SocialService social, PresenceService presence) =>
{
    var me = CurrentUser(ctx);
    if (me is null) return Results.Json(new { ok = false }, statusCode: 401);
    if (Roles.Effective(me.Username, db.Role(me.Id)) != Roles.Dev)
        return Results.Json(new { ok = false, error = "删号只有开发者能做" }, statusCode: 403);
    if (req.UserId == me.Id) return Results.Json(new { ok = false, error = "不能删自己" }, statusCode: 400);

    var target = db.FindById(req.UserId);
    if (target is null) return Results.Json(new { ok = false, error = "这个人不在了" }, statusCode: 404);
    if (Roles.IsDevName(target.Username))
        return Results.Json(new { ok = false, error = "名单里的开发者删不得" }, statusCode: 400);

    rooms.Kick(target.Id);
    await social.ToUser(target.Id, "bye", new { text = "你的账号被开发者删除了" });
    db.DeleteUser(target.Id);
    await social.ToAll("accountsChanged", new { by = me.Username });
    return Results.Json(new { ok = true });
}).RequireAuthorization();

app.MapPost("/api/dev/wipe-users", async (HttpContext ctx, DevWipeRequest req, SocialService social) =>
{
    var me = CurrentUser(ctx);
    if (me is null) return Results.Json(new { ok = false }, statusCode: 401);
    if (Roles.Effective(me.Username, db.Role(me.Id)) != Roles.Dev)
        return Results.Json(new { ok = false, error = "只有开发者能清场" }, statusCode: 403);
    if ((req.Confirm ?? "").Trim() != "确认清空")
        return Results.Json(new { ok = false, error = "要清空得先打「确认清空」" }, statusCode: 400);

    var keep = db.AllAccounts()
        .Where(a => a.Id == me.Id || Roles.IsDevName(a.Username))
        .Select(a => a.Id)
        .ToList();

    await social.ToAll("bye", new { text = "服务器被开发者清场了，账号已删除" });
    rooms.EjectAllExcept(keep);
    var gone = db.WipeAccountsExcept(keep);
    return Results.Json(new { ok = true, deleted = gone, kept = keep.Count });
}).RequireAuthorization();

app.MapPost("/api/dev/reset-game", async (HttpContext ctx, DevWipeRequest req, SocialService social) =>
{
    var me = CurrentUser(ctx);
    if (me is null) return Results.Json(new { ok = false }, statusCode: 401);
    if (Roles.Effective(me.Username, db.Role(me.Id)) != Roles.Dev)
        return Results.Json(new { ok = false, error = "只有开发者能重置" }, statusCode: 403);
    if ((req.Confirm ?? "").Trim() != "重置游戏")
        return Results.Json(new { ok = false, error = "要重置得先打「重置游戏」" }, statusCode: 400);

    foreach (var room in rooms.AdminRooms()) rooms.CloseRoom(room.Code);
    db.ResetGameData();
    await social.ToAll("gameReset", new { by = me.Username });
    return Results.Json(new { ok = true });
}).RequireAuthorization();

// ---------------- 后台 ----------------
app.MapGet("/admin", () => Results.Redirect("/admin/index.html"));

app.MapPost("/admin/api/login", (HttpContext ctx, AdminLoginRequest req) =>
{
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var r = admin.TryLogin(req.Key, ip);

    if (r.Ok)
    {
        admin.IssueCookie(ctx);
        return Results.Json(new { ok = true });
    }

    if (r.LockSeconds > 0)
        return Results.Json(new { ok = false, locked = r.LockSeconds }, statusCode: 429);

    // 不说"密钥错了"，只说"没对上"，也不透露还剩几次以外的信息
    return Results.Json(new { ok = false, left = r.Remaining }, statusCode: 400);
}).RequireRateLimiting("admin");

app.MapPost("/admin/api/logout", (HttpContext ctx) =>
{
    admin.ClearCookie(ctx);
    return Results.Json(new { ok = true });
});

app.MapGet("/admin/api/overview", () =>
{
    var s = db.Stats();
    var roomList = rooms.AdminRooms();
    return Results.Json(new
    {
        ok = true,
        stats = new
        {
            total = s.Total,
            points = s.Points,
            newToday = s.NewToday,
            customAvatars = s.WithCustomAvatar,
            rooms = roomList.Count,
            playing = roomList.Count(r => r.State == "playing")
        },
        rooms = roomList
    });
}).RequireRateLimiting("adminOps");

app.MapGet("/admin/api/users", (string? q, int? skip, int? take) =>
{
    var pageSize = Math.Clamp(take ?? 30, 1, 100);
    var offset = Math.Max(0, skip ?? 0);
    var list = db.ListUsers(q, pageSize, offset);
    return Results.Json(new
    {
        ok = true,
        total = db.CountUsers(q),
        skip = offset,
        take = pageSize,
        users = list
    });
}).RequireRateLimiting("adminOps");

app.MapPost("/admin/api/users/{id:int}/points", (int id, AdminPointsRequest req) =>
{
    var user = db.FindById(id);
    if (user is null) return Results.NotFound();

    var points = req.Set is not null
        ? db.SetPoints(id, req.Set.Value)
        : db.AddPoints(id, Math.Clamp(req.Delta ?? 0, -100_000, 100_000));

    return Results.Json(new { ok = true, id, points });
}).RequireRateLimiting("adminOps");

app.MapDelete("/admin/api/users/{id:int}", (int id) =>
{
    rooms.RemoveUser(id);                 // 先把人从牌桌上请下去
    var deleted = db.DeleteUser(id);
    return deleted ? Results.Json(new { ok = true }) : Results.NotFound();
}).RequireRateLimiting("adminOps");

// ---------------- 实时通道 ----------------
app.MapHub<BombHub>("/hubs/game");

// 根证书下载：手机装信任用的。公钥本身不是秘密，放在闸机外面，没过门禁也能下。
app.MapGet("/root-ca.cer", () =>
{
    var cer = Path.Combine(app.Environment.ContentRootPath, "certs", "root-ca.cer");
    if (!File.Exists(cer)) return Results.NotFound();
    return Results.File(cer, "application/x-x509-ca-cert", "number-bomb-root-ca.cer");
});

// 启动后把能打开的地址打出来，省得去 ipconfig 里翻
var lanIps = NetworkInterface.GetAllNetworkInterfaces()
    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
    .Select(a => a.Address)
    .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
    .Select(a => a.ToString())
    .Distinct()
    .ToList();

app.Lifetime.ApplicationStarted.Register(() =>
{
    var port = shortReady ? "" : ":" + HttpPort;
    Console.WriteLine();
    Console.WriteLine("  数字炸弹开张啦：");
    Console.WriteLine($"  电脑上： http://localhost{port}");
    foreach (var ip in lanIps)
        Console.WriteLine($"  手机上： http://{ip}{port}   （同一个 WiFi，直接输这串就行）");
    Console.WriteLine(shortReady
        ? $"  备用：   http://localhost:{HttpPort}（万一 80 端口不灵）"
        : $"  （80 端口被别的软件占着，所以地址后面要带 {HttpPort}）");
    Console.WriteLine($"  后台：   http://localhost{port}/admin   密钥在 admin.key");
    if (httpsReady) Console.WriteLine($"  加密入口（可选）： https://localhost:{HttpsPort}");
    Console.WriteLine("  不玩了就把这个窗口关掉。");
    Console.WriteLine();
});

app.Run();

// ---------------- 小工具 ----------------

/// <summary>看看这个端口现在能不能用。80 端口经常被别的软件占着，占着就绕开。</summary>
static bool CanBind(int port)
{
    try
    {
        var probe = new TcpListener(IPAddress.Any, port);
        probe.Start();
        probe.Stop();
        return true;
    }
    catch
    {
        return false;
    }
}

static UserRow? CurrentUser(HttpContext ctx)
{
    var raw = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (!int.TryParse(raw, out var id)) return null;
    var db = ctx.RequestServices.GetRequiredService<Db>();
    return db.FindById(id);
}

/// <summary>只认方形 PNG，尺寸不超过 1024，体积不超过 400KB。别的一律当没画。</summary>
static bool IsSquarePng(string base64, out byte[] bytes)
{
    bytes = Array.Empty<byte>();
    if (string.IsNullOrWhiteSpace(base64) || base64.Length > 700_000) return false;

    try
    {
        bytes = Convert.FromBase64String(base64);
    }
    catch
    {
        return false;
    }

    if (bytes.Length is < 64 or > 400_000) return false;

    ReadOnlySpan<byte> signature = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
    if (!bytes.AsSpan(0, 8).SequenceEqual(signature)) return false;

    var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
    var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
    return width is > 0 and <= 1024 && height is > 0 and <= 1024 && width == height;
}

object PersonJson(PersonRow p, PresenceService presence) => new
{
    id = p.Id,
    name = p.Username,
    role = Roles.Effective(p.Username, p.Role),
    badges = Roles.BadgesFor(p.Username, p.Role),
    avatar = p.Avatar,
    isCustomAvatar = p.IsCustomAvatar,
    points = p.Points,
    coins = p.Coins,
    online = presence.IsOnline(p.Id)
};

/// <summary>列表里只给摘要，正文点开才取，省流量</summary>
object MailJson(MailRow m, int meId) => new
{
    id = m.Id,
    fromName = m.FromName,
    fromBadges = Roles.BadgesFor(m.FromName, db.Role(m.FromId)),
    toName = m.ToName,
    toBadges = Roles.BadgesFor(m.ToName, db.Role(m.ToId)),
    subject = m.Subject,
    preview = m.Body.Length > 60 ? m.Body[..60] + "…" : m.Body,
    createdAt = m.CreatedAt,
    isRead = m.IsRead,
    mine = m.FromId == meId
};

object MeDto(UserRow u)
{
    var role = Roles.Effective(u.Username, db.Role(u.Id));
    var muted = db.MuteUntil(u.Id);
    return new
    {
        id = u.Id,
        name = u.Username,
        role,
        badges = Roles.BadgesFor(u.Username, role),
        points = u.Points,
        coins = u.Coins,
        bio = db.Bio(u.Id),
        avatar = u.Avatar,
        emoji = u.Avatar.StartsWith(AvatarPresets.Prefix, StringComparison.Ordinal)
            ? u.Avatar[AvatarPresets.Prefix.Length..]
            : "🙂",
        isCustomAvatar = u.Avatar == AvatarPresets.Custom,
        avatarUrl = u.Avatar == AvatarPresets.Custom ? $"/api/avatar/{u.Id}" : null,
        mutedUntil = muted is { } until && until > DateTime.UtcNow ? until.ToString("yyyy-MM-dd HH:mm:ss") : null
    };
}

static async Task SignIn(HttpContext ctx, UserRow user)
{
    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new(ClaimTypes.Name, user.Username)
    };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity),
        new AuthenticationProperties
        {
            IsPersistent = true,
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(90)
        });
}
