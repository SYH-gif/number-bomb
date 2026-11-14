using Microsoft.Data.Sqlite;

namespace NumberBomb.Data;

/// <summary>一个账号的数据库快照</summary>
public sealed record UserRow(
    int Id,
    string Username,
    string PasswordHash,
    string Avatar,        // "preset:🦊" 或 "custom"
    string? AvatarData,   // 自画画像的 PNG base64（不含 data: 前缀）
    int Points,
    int Coins);

/// <summary>
/// 后台看的账号摘要。故意不带哈希、不带画像 base64、不带任何凭据相关字段，
/// 就算这张表被人瞄到，也拿不到能冒充别人的东西。
/// </summary>
public sealed record UserSummary(
    int Id,
    string Username,
    int Points,
    string Avatar,
    bool IsCustomAvatar,
    string CreatedAt,
    string? LastLoginAt);

/// <summary>开发者面板里的账号行（比 PersonRow 多注册时间和最后上线时间）</summary>
public sealed record AccountRow(
    int Id,
    string Username,
    string Role,
    int Points,
    int Coins,
    string Avatar,
    bool IsCustomAvatar,
    string CreatedAt,
    string? LastLoginAt);

/// <summary>
/// 好友列表 / 搜索结果里的人。只有公开信息，永远不含密码哈希和画像 base64。
/// </summary>
public sealed record PersonRow(
    int Id,
    string Username,
    string Avatar,
    bool IsCustomAvatar,
    int Points,
    int Coins,
    string Role);

/// <summary>一条聊天消息。to_id = 0 表示大厅公屏。</summary>
public sealed record ChatRow(
    int Id,
    int FromId,
    string FromName,
    string Avatar,
    bool IsCustomAvatar,
    string Role,
    int ToId,
    string Body,
    string CreatedAt,
    bool IsRead);

/// <summary>会话列表里的一行：最后一条消息 + 对方是谁 + 有几条没读</summary>
public sealed record ChatThreadRow(
    int PeerId,
    string PeerName,
    string Avatar,
    bool IsCustomAvatar,
    string Role,
    string Body,
    string CreatedAt,
    bool Mine,
    int Unread);

/// <summary>一封站内信</summary>
public sealed record MailRow(
    int Id,
    int FromId,
    string FromName,
    int ToId,
    string ToName,
    string Subject,
    string Body,
    string CreatedAt,
    bool IsRead);

/// <summary>
/// 极简 SQLite 访问层：一张 users 表，够用就好。
/// 每次操作开一个连接（SQLite 本地文件，开销很小），写操作串行化由 SQLite 自己保证。
/// </summary>
public sealed class Db
{
    private readonly string _cs;
    public string FilePath { get; }

    public Db(IWebHostEnvironment env, ILogger<Db> log)
    {
        FilePath = Path.Combine(env.ContentRootPath, "app.db");
        _cs = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 5
        }.ToString();

        Init();
        log.LogInformation("SQLite 就绪：{Path}", FilePath);
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_cs);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private void Init()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS users (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                username      TEXT    NOT NULL COLLATE NOCASE UNIQUE,
                password_hash TEXT    NOT NULL,
                avatar        TEXT    NOT NULL DEFAULT 'preset:🐱',
                avatar_data   TEXT    NULL,
                points        INTEGER NOT NULL DEFAULT 100,
                created_at    TEXT    NOT NULL DEFAULT (datetime('now')),
                last_login_at TEXT    NULL
            );
            CREATE INDEX IF NOT EXISTS idx_users_name ON users(username);

            -- 好友：存成 (小的 id, 大的 id) 一对，天然去重，不用管谁加谁
            CREATE TABLE IF NOT EXISTS friendships (
                user_a     INTEGER NOT NULL,
                user_b     INTEGER NOT NULL,
                created_at TEXT    NOT NULL DEFAULT (datetime('now')),
                PRIMARY KEY (user_a, user_b)
            );
            CREATE TABLE IF NOT EXISTS friend_requests (
                from_id    INTEGER NOT NULL,
                to_id      INTEGER NOT NULL,
                created_at TEXT    NOT NULL DEFAULT (datetime('now')),
                PRIMARY KEY (from_id, to_id)
            );
            CREATE TABLE IF NOT EXISTS messages (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                from_id    INTEGER NOT NULL,
                to_id      INTEGER NOT NULL,
                subject    TEXT    NOT NULL,
                body       TEXT    NOT NULL,
                created_at TEXT    NOT NULL DEFAULT (datetime('now')),
                read_at    TEXT    NULL
            );
            CREATE INDEX IF NOT EXISTS idx_msg_to ON messages(to_id, id DESC);
            CREATE TABLE IF NOT EXISTS inventory (
                user_id INTEGER NOT NULL,
                kind    TEXT    NOT NULL,
                qty     INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (user_id, kind)
            );

            -- 聊天：to_id = 0 是大厅公屏，>0 是私聊。只留最近的东西，不做已读回执的双表。
            CREATE TABLE IF NOT EXISTS chats (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                from_id    INTEGER NOT NULL,
                to_id      INTEGER NOT NULL DEFAULT 0,
                body       TEXT    NOT NULL,
                created_at TEXT    NOT NULL DEFAULT (datetime('now')),
                read_at    TEXT    NULL
            );
            CREATE INDEX IF NOT EXISTS idx_chat_to   ON chats(to_id, id DESC);
            CREATE INDEX IF NOT EXISTS idx_chat_from ON chats(from_id, id DESC);
            """;
        cmd.ExecuteNonQuery();

        // 老库没有这几列，补上（SQLite 没有 ADD COLUMN IF NOT EXISTS，先查一下）
        if (!HasColumn(conn, "users", "last_login_at"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE users ADD COLUMN last_login_at TEXT NULL";
            alter.ExecuteNonQuery();
        }
        if (!HasColumn(conn, "users", "coins"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE users ADD COLUMN coins INTEGER NOT NULL DEFAULT 0";
            alter.ExecuteNonQuery();
        }
        if (!HasColumn(conn, "users", "role"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE users ADD COLUMN role TEXT NOT NULL DEFAULT 'user'";
            alter.ExecuteNonQuery();
        }
        if (!HasColumn(conn, "users", "mute_until"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE users ADD COLUMN mute_until TEXT NULL";
            alter.ExecuteNonQuery();
        }
        if (!HasColumn(conn, "users", "bio"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE users ADD COLUMN bio TEXT NOT NULL DEFAULT ''";
            alter.ExecuteNonQuery();
        }
    }

    private static bool HasColumn(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static UserRow Read(SqliteDataReader r) => new(
        r.GetInt32(0),
        r.GetString(1),
        r.GetString(2),
        r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4),
        r.GetInt32(5),
        r.GetInt32(6));

    private const string Cols = "id, username, password_hash, avatar, avatar_data, points, coins";

    public UserRow? FindByName(string username)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM users WHERE username = $n LIMIT 1";
        cmd.Parameters.AddWithValue("$n", username);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    public UserRow? FindById(int id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM users WHERE id = $id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    public int Create(string username, string passwordHash, string avatar)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO users (username, password_hash, avatar, points, coins)
            VALUES ($n, $p, $a, 100, 100);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$n", username);
        cmd.Parameters.AddWithValue("$p", passwordHash);
        cmd.Parameters.AddWithValue("$a", avatar);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void SetPresetAvatar(int id, string preset)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE users SET avatar = $a, avatar_data = NULL WHERE id = $id";
        cmd.Parameters.AddWithValue("$a", preset);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void SetCustomAvatar(int id, string base64Png)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE users SET avatar = 'custom', avatar_data = $d WHERE id = $id";
        cmd.Parameters.AddWithValue("$d", base64Png);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>加分/扣分，最低停在 0。返回扣完之后的积分。</summary>
    public int AddPoints(int id, int delta)
    {
        using var conn = Open();
        using var upd = conn.CreateCommand();
        upd.CommandText = "UPDATE users SET points = MAX(0, points + $d) WHERE id = $id";
        upd.Parameters.AddWithValue("$d", delta);
        upd.Parameters.AddWithValue("$id", id);
        upd.ExecuteNonQuery();

        using var sel = conn.CreateCommand();
        sel.CommandText = "SELECT points FROM users WHERE id = $id";
        sel.Parameters.AddWithValue("$id", id);
        return Convert.ToInt32(sel.ExecuteScalar() ?? 0);
    }

    /// <summary>一局结束后批量结算（积分 + 金币，一个事务里做完）</summary>
    public void ApplyDeltas(IEnumerable<(int UserId, int Delta, int Coins)> deltas)
    {
        var list = deltas.ToList();
        if (list.Count == 0) return;

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE users SET points = MAX(0, points + $d), coins = MAX(0, coins + $c) WHERE id = $id";
        var pd = cmd.CreateParameter(); pd.ParameterName = "$d"; cmd.Parameters.Add(pd);
        var pc = cmd.CreateParameter(); pc.ParameterName = "$c"; cmd.Parameters.Add(pc);
        var pi = cmd.CreateParameter(); pi.ParameterName = "$id"; cmd.Parameters.Add(pi);
        foreach (var (uid, delta, coins) in list)
        {
            pd.Value = delta;
            pc.Value = coins;
            pi.Value = uid;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // ---------------------------------------------------------------- 后台用
    // 这里所有查询都不碰 password_hash / avatar_data，后台只用来管人，不需要碰凭据。

    public void TouchLogin(int id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE users SET last_login_at = datetime('now') WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void UpdatePasswordHash(int id, string passwordHash)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE users SET password_hash = $p WHERE id = $id";
        cmd.Parameters.AddWithValue("$p", passwordHash);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public List<UserSummary> ListUsers(string? keyword, int take, int skip)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var hasKeyword = !string.IsNullOrWhiteSpace(keyword);
        cmd.CommandText = $"""
            SELECT id, username, points, avatar, avatar_data IS NOT NULL, created_at,
                   COALESCE(last_login_at, '')
            FROM users
            {(hasKeyword ? "WHERE username LIKE $q" : "")}
            ORDER BY id DESC
            LIMIT $take OFFSET $skip
            """;
        if (hasKeyword) cmd.Parameters.AddWithValue("$q", "%" + keyword!.Trim() + "%");
        cmd.Parameters.AddWithValue("$take", Math.Clamp(take, 1, 200));
        cmd.Parameters.AddWithValue("$skip", Math.Max(0, skip));

        var list = new List<UserSummary>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new UserSummary(
                r.GetInt32(0),
                r.GetString(1),
                r.GetInt32(2),
                r.GetString(3),
                r.GetInt32(4) == 1,
                r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6)));
        }
        return list;
    }

    public int CountUsers(string? keyword)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var hasKeyword = !string.IsNullOrWhiteSpace(keyword);
        cmd.CommandText = hasKeyword
            ? "SELECT COUNT(*) FROM users WHERE username LIKE $q"
            : "SELECT COUNT(*) FROM users";
        if (hasKeyword) cmd.Parameters.AddWithValue("$q", "%" + keyword!.Trim() + "%");
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    public (int Total, int Points, int NewToday, int WithCustomAvatar) Stats()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*),
                   COALESCE(SUM(points), 0),
                   COALESCE(SUM(CASE WHEN date(created_at) = date('now', 'localtime') THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN avatar_data IS NOT NULL THEN 1 ELSE 0 END), 0)
            FROM users
            """;
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return (0, 0, 0, 0);
        return (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3));
    }

    /// <summary>把积分直接设成某个值（后台纠错用），最低 0。</summary>
    public int SetPoints(int id, int value)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE users SET points = MAX(0, $v) WHERE id = $id";
        cmd.Parameters.AddWithValue("$v", value);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();

        using var sel = conn.CreateCommand();
        sel.CommandText = "SELECT points FROM users WHERE id = $id";
        sel.Parameters.AddWithValue("$id", id);
        return Convert.ToInt32(sel.ExecuteScalar() ?? 0);
    }

    public bool DeleteUser(int id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM users WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        var gone = cmd.ExecuteNonQuery() > 0;

        // 顺手把人从别人的好友名单、待办请求、收件箱里清干净
        using var clean = conn.CreateCommand();
        clean.CommandText = """
            DELETE FROM friendships      WHERE user_a = $id OR user_b = $id;
            DELETE FROM friend_requests  WHERE from_id = $id OR to_id = $id;
            DELETE FROM messages         WHERE from_id = $id OR to_id = $id;
            DELETE FROM chats            WHERE from_id = $id OR to_id = $id;
            DELETE FROM inventory        WHERE user_id = $id;
            """;
        clean.Parameters.AddWithValue("$id", id);
        clean.ExecuteNonQuery();
        return gone;
    }

    // ---------------------------------------------------------------- 好友

    private const string PersonCols =
        "u.id, u.username, u.avatar, u.avatar_data IS NOT NULL, u.points, u.coins, u.role";

    private static PersonRow ReadPerson(SqliteDataReader r) => new(
        r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetInt32(3) == 1, r.GetInt32(4), r.GetInt32(5),
        r.IsDBNull(6) ? "user" : r.GetString(6));

    public List<PersonRow> Friends(int userId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {PersonCols}
            FROM users u
            JOIN (
                SELECT CASE WHEN user_a = $me THEN user_b ELSE user_a END AS fid
                FROM friendships
                WHERE user_a = $me OR user_b = $me
            ) f ON f.fid = u.id
            ORDER BY u.username
            """;
        cmd.Parameters.AddWithValue("$me", userId);
        return ReadPeople(cmd);
    }

    public List<int> FriendIds(int userId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT CASE WHEN user_a = $me THEN user_b ELSE user_a END
            FROM friendships WHERE user_a = $me OR user_b = $me
            """;
        cmd.Parameters.AddWithValue("$me", userId);
        var ids = new List<int>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add(r.GetInt32(0));
        return ids;
    }

    public bool AreFriends(int a, int b)
    {
        if (a == b) return false;
        var (lo, hi) = a < b ? (a, b) : (b, a);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM friendships WHERE user_a = $a AND user_b = $b LIMIT 1";
        cmd.Parameters.AddWithValue("$a", lo);
        cmd.Parameters.AddWithValue("$b", hi);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>发好友申请。返回 "" 表示成功，否则是给人看的原因。</summary>
    public string AddRequest(int from, int to)
    {
        if (from == to) return "不能加自己哦";
        if (AreFriends(from, to)) return "你们已经是好友了";

        using var conn = Open();
        using var check = conn.CreateCommand();
        check.CommandText = "SELECT from_id FROM friend_requests WHERE (from_id = $a AND to_id = $b) OR (from_id = $b AND to_id = $a)";
        check.Parameters.AddWithValue("$a", from);
        check.Parameters.AddWithValue("$b", to);
        using (var r = check.ExecuteReader())
        {
            if (r.Read())
            {
                var who = r.GetInt32(0);
                return who == from ? "已经发过了，等对方点同意" : "对方也在等你同意，去上面点一下通过吧";
            }
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO friend_requests (from_id, to_id) VALUES ($f, $t)";
        cmd.Parameters.AddWithValue("$f", from);
        cmd.Parameters.AddWithValue("$t", to);
        cmd.ExecuteNonQuery();
        return "";
    }

    public bool AcceptRequest(int from, int to)
    {
        var (lo, hi) = from < to ? (from, to) : (to, from);
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using var del = conn.CreateCommand();
        del.Transaction = tx;
        del.CommandText = "DELETE FROM friend_requests WHERE from_id = $f AND to_id = $t";
        del.Parameters.AddWithValue("$f", from);
        del.Parameters.AddWithValue("$t", to);
        var removed = del.ExecuteNonQuery() > 0;

        if (removed)
        {
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR IGNORE INTO friendships (user_a, user_b) VALUES ($a, $b)";
            ins.Parameters.AddWithValue("$a", lo);
            ins.Parameters.AddWithValue("$b", hi);
            ins.ExecuteNonQuery();
        }
        tx.Commit();
        return removed;
    }

    public void DropRequest(int from, int to)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM friend_requests WHERE from_id = $f AND to_id = $t";
        cmd.Parameters.AddWithValue("$f", from);
        cmd.Parameters.AddWithValue("$t", to);
        cmd.ExecuteNonQuery();
    }

    public void RemoveFriend(int a, int b)
    {
        var (lo, hi) = a < b ? (a, b) : (b, a);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM friendships WHERE user_a = $a AND user_b = $b";
        cmd.Parameters.AddWithValue("$a", lo);
        cmd.Parameters.AddWithValue("$b", hi);
        cmd.ExecuteNonQuery();
    }

    public List<PersonRow> IncomingRequests(int userId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {PersonCols}
            FROM friend_requests fr JOIN users u ON u.id = fr.from_id
            WHERE fr.to_id = $me
            ORDER BY fr.created_at DESC
            """;
        cmd.Parameters.AddWithValue("$me", userId);
        return ReadPeople(cmd);
    }

    public List<PersonRow> OutgoingRequests(int userId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {PersonCols}
            FROM friend_requests fr JOIN users u ON u.id = fr.to_id
            WHERE fr.from_id = $me
            ORDER BY fr.created_at DESC
            """;
        cmd.Parameters.AddWithValue("$me", userId);
        return ReadPeople(cmd);
    }

    /// <summary>按名字找人，名字开头或包含都算，已加的和自己跳过。</summary>
    public List<PersonRow> SearchPeople(string keyword, int me, int take = 8)
    {
        keyword = (keyword ?? "").Trim();
        if (keyword.Length == 0) return new List<PersonRow>();

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {PersonCols}
            FROM users u
            WHERE u.id <> $me
              AND u.username LIKE $q
              AND u.id NOT IN (
                    SELECT CASE WHEN user_a = $me THEN user_b ELSE user_a END
                    FROM friendships WHERE user_a = $me OR user_b = $me)
            ORDER BY CASE WHEN u.username LIKE $prefix THEN 0 ELSE 1 END, u.username
            LIMIT $take
            """;
        cmd.Parameters.AddWithValue("$me", me);
        cmd.Parameters.AddWithValue("$q", "%" + keyword + "%");
        cmd.Parameters.AddWithValue("$prefix", keyword + "%");
        cmd.Parameters.AddWithValue("$take", Math.Clamp(take, 1, 20));
        return ReadPeople(cmd);
    }

    private static List<PersonRow> ReadPeople(SqliteCommand cmd)
    {
        var list = new List<PersonRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadPerson(r));
        return list;
    }

    // ---------------------------------------------------------------- 站内信

    public int SendMail(int from, int to, string subject, string body)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO messages (from_id, to_id, subject, body) VALUES ($f, $t, $s, $b);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$f", from);
        cmd.Parameters.AddWithValue("$t", to);
        cmd.Parameters.AddWithValue("$s", subject);
        cmd.Parameters.AddWithValue("$b", body);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>box: "in" 收件箱 / "out" 发件箱</summary>
    public List<MailRow> Mail(int userId, bool inbox, int take = 50)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT m.id, m.from_id, uf.username, m.to_id, ut.username,
                   m.subject, m.body, m.created_at, m.read_at IS NOT NULL
            FROM messages m
            JOIN users uf ON uf.id = m.from_id
            JOIN users ut ON ut.id = m.to_id
            WHERE m.{(inbox ? "to_id" : "from_id")} = $me
            ORDER BY m.id DESC
            LIMIT $take
            """;
        cmd.Parameters.AddWithValue("$me", userId);
        cmd.Parameters.AddWithValue("$take", Math.Clamp(take, 1, 100));

        var list = new List<MailRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new MailRow(
                r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetInt32(3), r.GetString(4),
                r.GetString(5), r.GetString(6), r.GetString(7), r.GetInt32(8) == 1));
        }
        return list;
    }

    public MailRow? GetMail(int id, int userId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT m.id, m.from_id, uf.username, m.to_id, ut.username,
                   m.subject, m.body, m.created_at, m.read_at IS NOT NULL
            FROM messages m
            JOIN users uf ON uf.id = m.from_id
            JOIN users ut ON ut.id = m.to_id
            WHERE m.id = $id AND (m.to_id = $me OR m.from_id = $me)
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$me", userId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new MailRow(
            r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetInt32(3), r.GetString(4),
            r.GetString(5), r.GetString(6), r.GetString(7), r.GetInt32(8) == 1);
    }

    public void MarkMailRead(int id, int userId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE messages SET read_at = datetime('now') WHERE id = $id AND to_id = $me AND read_at IS NULL";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$me", userId);
        cmd.ExecuteNonQuery();
    }

    public bool DeleteMail(int id, int userId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM messages WHERE id = $id AND (to_id = $me OR from_id = $me)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$me", userId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public int UnreadMail(int userId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM messages WHERE to_id = $me AND read_at IS NULL";
        cmd.Parameters.AddWithValue("$me", userId);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    // ---------------------------------------------------------------- 金币 / 道具

    public int Coins(int userId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT coins FROM users WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", userId);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    public int AddCoins(int userId, int delta)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE users SET coins = MAX(0, coins + $d) WHERE id = $id";
        cmd.Parameters.AddWithValue("$d", delta);
        cmd.Parameters.AddWithValue("$id", userId);
        cmd.ExecuteNonQuery();
        return Coins(userId);
    }

    /// <summary>买道具：金币够了就扣钱加货，返回 (是否成功, 剩余金币)</summary>
    public (bool Ok, int Coins) BuyItem(int userId, string kind, int price)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using var pay = conn.CreateCommand();
        pay.Transaction = tx;
        pay.CommandText = "UPDATE users SET coins = coins - $p WHERE id = $id AND coins >= $p";
        pay.Parameters.AddWithValue("$p", price);
        pay.Parameters.AddWithValue("$id", userId);
        var paid = pay.ExecuteNonQuery() > 0;

        if (paid)
        {
            using var add = conn.CreateCommand();
            add.Transaction = tx;
            add.CommandText = """
                INSERT INTO inventory (user_id, kind, qty) VALUES ($id, $k, 1)
                ON CONFLICT(user_id, kind) DO UPDATE SET qty = qty + 1
                """;
            add.Parameters.AddWithValue("$id", userId);
            add.Parameters.AddWithValue("$k", kind);
            add.ExecuteNonQuery();
        }
        tx.Commit();

        return (paid, Coins(userId));
    }

    /// <summary>用掉一个道具。包里没有就返回 false。</summary>
    public bool TakeItem(int userId, string kind)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE inventory SET qty = qty - 1 WHERE user_id = $id AND kind = $k AND qty > 0";
        cmd.Parameters.AddWithValue("$id", userId);
        cmd.Parameters.AddWithValue("$k", kind);
        return cmd.ExecuteNonQuery() > 0;
    }

    public Dictionary<string, int> Inventory(int userId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT kind, qty FROM inventory WHERE user_id = $id AND qty > 0";
        cmd.Parameters.AddWithValue("$id", userId);
        var map = new Dictionary<string, int>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetString(0)] = r.GetInt32(1);
        return map;
    }

    // ---------------------------------------------------------------- 角色 / 禁言

    public string Role(int userId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT role FROM users WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", userId);
        return cmd.ExecuteScalar() as string ?? "user";
    }

    public void SetRole(int userId, string role)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE users SET role = $r WHERE id = $id";
        cmd.Parameters.AddWithValue("$r", role);
        cmd.Parameters.AddWithValue("$id", userId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>禁言到什么时候（UTC）。没禁言就是 null。</summary>
    public DateTime? MuteUntil(int userId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT mute_until FROM users WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", userId);
        var raw = cmd.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt)
            ? dt
            : null;
    }

    /// <summary>禁言 minutes 分钟；0 = 解除。</summary>
    public void Mute(int userId, int minutes)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = minutes <= 0
            ? "UPDATE users SET mute_until = NULL WHERE id = $id"
            : "UPDATE users SET mute_until = datetime('now', $m) WHERE id = $id";
        if (minutes > 0) cmd.Parameters.AddWithValue("$m", $"+{minutes} minutes");
        cmd.Parameters.AddWithValue("$id", userId);
        cmd.ExecuteNonQuery();
    }

    public bool IsMuted(int userId)
    {
        var until = MuteUntil(userId);
        return until is not null && until.Value > DateTime.UtcNow;
    }

    /// <summary>带角色的人（给开发者看的任命名单）</summary>
    public List<PersonRow> Staff()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {PersonCols} FROM users u
            WHERE u.role IN ('admin', 'dev')
            ORDER BY CASE u.role WHEN 'dev' THEN 0 ELSE 1 END, u.username
            """;
        return ReadPeople(cmd);
    }

    /// <summary>按 id 批量取人（在线列表用）</summary>
    public List<PersonRow> PeopleByIds(IEnumerable<int> ids)
    {
        var list = ids.Distinct().Take(200).ToArray();
        if (list.Length == 0) return new List<PersonRow>();

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var names = new List<string>();
        for (var i = 0; i < list.Length; i++)
        {
            names.Add("$p" + i);
            cmd.Parameters.AddWithValue("$p" + i, list[i]);
        }
        cmd.CommandText = $"SELECT {PersonCols} FROM users u WHERE u.id IN ({string.Join(",", names)}) ORDER BY u.username";
        return ReadPeople(cmd);
    }

    // ---------------------------------------------------------------- 聊天

    public int SendChat(int from, int to, string body)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO chats (from_id, to_id, body) VALUES ($f, $t, $b);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$f", from);
        cmd.Parameters.AddWithValue("$t", to);
        cmd.Parameters.AddWithValue("$b", body);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private const string ChatCols = """
        c.id, c.from_id, u.username, u.avatar, u.avatar_data IS NOT NULL, u.role,
        c.to_id, c.body, c.created_at, c.read_at IS NOT NULL
        """;

    /// <summary>两个人之间的聊天记录，倒序取一页再翻过来</summary>
    public List<ChatRow> ChatHistory(int me, int peer, int beforeId, int take = 60)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {ChatCols}
            FROM chats c JOIN users u ON u.id = c.from_id
            WHERE c.to_id <> 0
              AND ((c.from_id = $me AND c.to_id = $peer) OR (c.from_id = $peer AND c.to_id = $me))
              AND c.id < $before
            ORDER BY c.id DESC
            LIMIT $take
            """;
        cmd.Parameters.AddWithValue("$me", me);
        cmd.Parameters.AddWithValue("$peer", peer);
        cmd.Parameters.AddWithValue("$before", beforeId <= 0 ? int.MaxValue : beforeId);
        cmd.Parameters.AddWithValue("$take", Math.Clamp(take, 1, 100));

        var list = new List<ChatRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadChat(r));
        list.Reverse();
        return list;
    }

    /// <summary>大厅公屏</summary>
    public List<ChatRow> Lobby(int beforeId, int take = 50)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {ChatCols}
            FROM chats c JOIN users u ON u.id = c.from_id
            WHERE c.to_id = 0 AND c.id < $before
            ORDER BY c.id DESC
            LIMIT $take
            """;
        cmd.Parameters.AddWithValue("$before", beforeId <= 0 ? int.MaxValue : beforeId);
        cmd.Parameters.AddWithValue("$take", Math.Clamp(take, 1, 100));

        var list = new List<ChatRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadChat(r));
        list.Reverse();
        return list;
    }

    private static ChatRow ReadChat(SqliteDataReader r) => new(
        r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetInt32(4) == 1,
        r.IsDBNull(5) ? "user" : r.GetString(5),
        r.GetInt32(6), r.GetString(7), r.GetString(8), r.GetInt32(9) == 1);

    /// <summary>会话列表：每个聊过的人取最后一条</summary>
    public List<ChatThreadRow> Threads(int me, int take = 40)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT g.peer, u.username, u.avatar, u.avatar_data IS NOT NULL, u.role,
                   t.body, t.created_at,
                   t.from_id = $me,
                   (SELECT COUNT(*) FROM chats x
                     WHERE x.from_id = g.peer AND x.to_id = $me AND x.read_at IS NULL)
            FROM (
                SELECT MAX(id) AS mid,
                       CASE WHEN from_id = $me THEN to_id ELSE from_id END AS peer
                FROM chats
                WHERE to_id <> 0 AND (from_id = $me OR to_id = $me)
                GROUP BY peer
            ) g
            JOIN chats t ON t.id = g.mid
            JOIN users u ON u.id = g.peer
            ORDER BY t.id DESC
            LIMIT $take
            """;
        cmd.Parameters.AddWithValue("$me", me);
        cmd.Parameters.AddWithValue("$take", Math.Clamp(take, 1, 100));

        var list = new List<ChatThreadRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new ChatThreadRow(
                r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetInt32(3) == 1,
                r.IsDBNull(4) ? "user" : r.GetString(4),
                r.GetString(5), r.GetString(6), r.GetInt32(7) == 1, r.GetInt32(8)));
        }
        return list;
    }

    public int UnreadChats(int me)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM chats WHERE to_id = $me AND read_at IS NULL";
        cmd.Parameters.AddWithValue("$me", me);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    public int UnreadFrom(int me, int peer)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM chats WHERE from_id = $peer AND to_id = $me AND read_at IS NULL";
        cmd.Parameters.AddWithValue("$me", me);
        cmd.Parameters.AddWithValue("$peer", peer);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    public void MarkChatRead(int me, int peer)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE chats SET read_at = datetime('now') WHERE from_id = $peer AND to_id = $me AND read_at IS NULL";
        cmd.Parameters.AddWithValue("$me", me);
        cmd.Parameters.AddWithValue("$peer", peer);
        cmd.ExecuteNonQuery();
    }

    /// <summary>删一条聊天（管理员及以上）</summary>
    public bool DeleteChat(int id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM chats WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>把某个人的聊天记录清干净（管理员及以上）</summary>
    public int PurgeChat(int userId, int peer)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM chats
            WHERE (from_id = $u AND to_id = $p) OR (from_id = $p AND to_id = $u)
            """;
        cmd.Parameters.AddWithValue("$u", userId);
        cmd.Parameters.AddWithValue("$p", peer);
        return cmd.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------- 个人资料 / 开发者工具

    public string Bio(int userId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT bio FROM users WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", userId);
        return cmd.ExecuteScalar() as string ?? "";
    }

    public void SetBio(int userId, string bio)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE users SET bio = $b WHERE id = $id";
        cmd.Parameters.AddWithValue("$b", bio);
        cmd.Parameters.AddWithValue("$id", userId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>改名字。名字被别人占了就返回 false。</summary>
    public bool SetUsername(int userId, string name)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE users SET username = $n WHERE id = $id AND NOT EXISTS (SELECT 1 FROM users WHERE username = $n AND id <> $id)";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$id", userId);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>开发者面板用：所有账号，最近上线的排在前面。</summary>
    public List<AccountRow> AllAccounts(int limit = 500)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, username, role, points, coins, avatar, avatar_data IS NOT NULL,
                   created_at, last_login_at
            FROM users
            ORDER BY COALESCE(last_login_at, created_at) DESC
            LIMIT $lim
            """;
        cmd.Parameters.AddWithValue("$lim", Math.Clamp(limit, 1, 5000));
        var list = new List<AccountRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new AccountRow(
                r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? "user" : r.GetString(2),
                r.GetInt32(3), r.GetInt32(4), r.GetString(5), r.GetInt32(6) == 1,
                r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8)));
        }
        return list;
    }

    /// <summary>清空公屏（管理员及以上）</summary>
    public int ClearLobby()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM chats WHERE to_id = 0";
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 把除了 keep 之外的所有账号删掉（开发者专用）。顺带清掉这些人留下的
    /// 好友关系、待办请求、站内信、私聊和道具，公屏上我说过的话保留。
    /// </summary>
    public int WipeAccountsExcept(IReadOnlyCollection<int> keep)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        var ids = keep.Count == 0
            ? "SELECT id FROM users"
            : "SELECT id FROM users WHERE id NOT IN (" + string.Join(",", keep) + ")";

        int gone;
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = $"DELETE FROM users WHERE id IN ({ids})";
            gone = del.ExecuteNonQuery();
        }

        var keepList = keep.Count == 0 ? "-1" : string.Join(",", keep);
        using (var clean = conn.CreateCommand())
        {
            clean.Transaction = tx;
            clean.CommandText = $"""
                DELETE FROM friendships     WHERE user_a NOT IN ({keepList}) OR user_b NOT IN ({keepList});
                DELETE FROM friend_requests WHERE from_id NOT IN ({keepList}) OR to_id NOT IN ({keepList});
                DELETE FROM messages        WHERE from_id NOT IN ({keepList}) OR to_id NOT IN ({keepList});
                DELETE FROM chats           WHERE from_id NOT IN ({keepList}) OR (to_id <> 0 AND to_id NOT IN ({keepList}));
                DELETE FROM inventory       WHERE user_id NOT IN ({keepList});
                """;
            clean.ExecuteNonQuery();
        }

        tx.Commit();
        return gone;
    }

    /// <summary>把游戏数据清空，只留账号（开发者「重置游戏」用）。</summary>
    public void ResetGameData()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM friendships;
            DELETE FROM friend_requests;
            DELETE FROM messages;
            DELETE FROM chats;
            DELETE FROM inventory;
            UPDATE users SET points = 100, coins = 0;
            """;
        cmd.ExecuteNonQuery();
    }
}
