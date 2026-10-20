using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HexFront.Api.Services;

/// <summary>
/// 统一 SQLite 应用库（data/app.db，HF_APP_DB 可覆盖）：用户/角色/菜单/会话、
/// 登录日志/查询日志/错误日志/用户反馈、推演操作流水、剧本目录、房间、已存副本。
/// 全部管理类数据一个文件，写入失败只告警不打断主流程。
/// </summary>
public sealed class AppDbService : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _gate = new();
    private readonly ILogger<AppDbService> _logger;

    public AppDbService(ILogger<AppDbService> logger)
    {
        _logger = logger;
        var dbPath = Environment.GetEnvironmentVariable("HF_APP_DB") ?? DefaultDbPath();
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL");
        Exec("PRAGMA synchronous=NORMAL");
        CreateSchema();
        _logger.LogInformation("统一应用库就绪: {Db}", dbPath);
    }

    private static string DefaultDbPath()
    {
        var assemblyPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        var binDir = Path.GetDirectoryName(assemblyPath);
        if (string.IsNullOrEmpty(binDir)) return Path.Combine("data", "app.db");
        var root = Path.GetFullPath(Path.Combine(binDir, "..", "..", "..", "..", "..", ".."));
        return Path.Combine(root, "data", "app.db");
    }

    // ==================== Schema ====================

    private void CreateSchema()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS roles (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                code TEXT NOT NULL UNIQUE,
                name TEXT NOT NULL,
                description TEXT
            )
            """);
        Exec("""
            CREATE TABLE IF NOT EXISTS users (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                username TEXT NOT NULL UNIQUE,
                password_hash TEXT NOT NULL,
                salt TEXT NOT NULL,
                display_name TEXT,
                role_code TEXT NOT NULL DEFAULT 'spectator',
                status TEXT NOT NULL DEFAULT 'active',      -- active | disabled
                created_at TEXT NOT NULL,
                last_login_at TEXT
            )
            """);
        Exec("""
            CREATE TABLE IF NOT EXISTS menus (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                code TEXT NOT NULL UNIQUE,
                name TEXT NOT NULL,
                path TEXT,
                sort INTEGER NOT NULL DEFAULT 0,
                visible INTEGER NOT NULL DEFAULT 1
            )
            """);
        Exec("""
            CREATE TABLE IF NOT EXISTS role_menus (
                role_code TEXT NOT NULL,
                menu_code TEXT NOT NULL,
                PRIMARY KEY (role_code, menu_code)
            )
            """);
        Exec("""
            CREATE TABLE IF NOT EXISTS sessions (
                token TEXT PRIMARY KEY,
                user_id INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                last_seen_at TEXT NOT NULL
            )
            """);
        Exec("""
            CREATE TABLE IF NOT EXISTS login_logs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ts TEXT NOT NULL,
                username TEXT,
                user_id INTEGER,
                action TEXT NOT NULL,          -- login | logout | failed
                ok INTEGER NOT NULL,
                ip TEXT,
                user_agent TEXT,
                detail TEXT
            )
            """);
        Exec("""
            CREATE TABLE IF NOT EXISTS feedback (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ts TEXT NOT NULL,
                scenario TEXT NOT NULL,
                kind TEXT NOT NULL,
                detail TEXT NOT NULL,
                contact TEXT,
                username TEXT,
                status TEXT NOT NULL DEFAULT 'open'   -- open | resolved
            )
            """);
        Exec("""
            CREATE TABLE IF NOT EXISTS query_logs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ts TEXT NOT NULL,
                method TEXT NOT NULL,
                path TEXT NOT NULL,
                status INTEGER,
                duration_ms INTEGER,
                username TEXT,
                ip TEXT
            )
            """);
        Exec("""
            CREATE TABLE IF NOT EXISTS error_logs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ts TEXT NOT NULL,
                source TEXT NOT NULL,          -- request | service
                path TEXT,
                message TEXT NOT NULL,
                stack TEXT
            )
            """);
        // 推演操作流水（自 ops_log.db 并入）
        Exec("""
            CREATE TABLE IF NOT EXISTS instances (
                id TEXT PRIMARY KEY,
                scenario TEXT NOT NULL,
                name TEXT,
                seed INTEGER,
                source TEXT NOT NULL,
                created_at TEXT NOT NULL
            )
            """);
        Exec("""
            CREATE TABLE IF NOT EXISTS ops (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                instance TEXT NOT NULL,
                ts TEXT NOT NULL,
                stage TEXT NOT NULL,
                actor TEXT,
                turn INTEGER,
                payload TEXT NOT NULL
            )
            """);
        Exec("CREATE INDEX IF NOT EXISTS ix_ops_instance ON ops(instance, id)");
        Exec("CREATE INDEX IF NOT EXISTS ix_ops_stage ON ops(instance, stage, id)");
        Exec("""
            CREATE TABLE IF NOT EXISTS scenarios (
                id TEXT PRIMARY KEY,
                name TEXT,
                era TEXT,
                scale TEXT,
                max_turns INTEGER,
                cell_km REAL,
                unit_count INTEGER,
                red_name TEXT,
                blue_name TEXT,
                description TEXT,
                file TEXT,
                updated_at TEXT,
                synced_at TEXT NOT NULL
            )
            """);
        Exec("""
            CREATE TABLE IF NOT EXISTS rooms (
                code TEXT PRIMARY KEY,
                instance TEXT NOT NULL,
                scenario TEXT,
                name TEXT,
                turn INTEGER,
                opened_at TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'open'
            )
            """);
        Exec("""
            CREATE TABLE IF NOT EXISTS saves (
                instance TEXT PRIMARY KEY,
                name TEXT,
                scenario TEXT,
                seed INTEGER,
                last_turn INTEGER,
                max_turns INTEGER,
                created_at TEXT,
                updated_at TEXT,
                source TEXT                       -- init | snapshot | manual
            )
            """);
    }

    /// <summary>启动初始化：种子数据 → 历史库迁移 → 剧本目录同步。幂等可重复调用。</summary>
    public void Initialize(string? scenarioDir = null)
    {
        SeedDefaults();
        MigrateOpsLog();
        MigrateFeedbackFile();
        if (scenarioDir != null) SyncScenarios(scenarioDir);
    }

    // ==================== 种子数据 ====================

    private void SeedDefaults()
    {
        var roles = new[]
        {
            ("super_admin", "超级管理员", "全部权限：用户/角色/菜单/日志/剧本/导调"),
            ("director", "导演部", "导调、强制结算、回放帧"),
            ("red", "红军", "对抗方红军席位"),
            ("blue", "蓝军", "对抗方蓝军席位"),
            ("spectator", "观战", "只读观战席位"),
        };
        foreach (var (code, name, desc) in roles)
        {
            // 菜单授权只在角色首次创建时写入默认值，之后以管理页配置为准（重启不覆盖）
            if (QueryRow("SELECT id FROM roles WHERE code=$c", ("$c", code)) != null) continue;
            ExecCmd("INSERT INTO roles(code, name, description) VALUES($c,$n,$d)",
                ("$c", code), ("$n", name), ("$d", desc));
            GrantMenus(code, DefaultMenusFor(code));
        }

        // 旧版 admin 角色 → super_admin（幂等迁移：admin 角色不存在时跳过）
        if (QueryRow("SELECT id FROM roles WHERE code='admin'") != null)
        {
            ExecCmd("UPDATE users SET role_code='super_admin' WHERE role_code='admin'");
            ExecCmd("INSERT OR IGNORE INTO role_menus(role_code, menu_code) " +
                    "SELECT 'super_admin', menu_code FROM role_menus WHERE role_code='admin'");
            ExecCmd("DELETE FROM role_menus WHERE role_code='admin'");
            ExecCmd("DELETE FROM roles WHERE code='admin'");
            _logger.LogInformation("已将旧 admin 角色迁移为 super_admin");
        }

        var menus = new (string code, string name, string path, int sort)[]
        {
            ("lobby", "大厅", "/lobby", 1),
            ("new_game", "创建副本", "/new", 2),
            ("grid", "六角格管理", "/grid", 3),
            ("scenarios", "剧本管理", "/scenarios", 4),
            ("system", "系统管理", "/admin", 5),
        };
        foreach (var (code, name, path, sort) in menus)
            Exec($"INSERT OR IGNORE INTO menus(code, name, path, sort, visible) VALUES('{code}','{Esc(name)}','{path}',{sort},1)");

        // 默认管理员（首次启动创建；密码建议尽快修改）
        if (Scalar("SELECT COUNT(*) FROM users WHERE username='admin'") == 0)
        {
            var (hash, salt) = HashPassword("admin123");
            Exec($"INSERT INTO users(username, password_hash, salt, display_name, role_code, status, created_at) " +
                 $"VALUES('admin','{hash}','{salt}','系统管理员','super_admin','active','{Now()}')");
            _logger.LogInformation("已创建默认管理员 admin/admin123");
        }
    }

    /// <summary>角色默认可见菜单（仅角色首次创建时授一次，之后由管理页维护）。</summary>
    private static IEnumerable<string> DefaultMenusFor(string role) => role switch
    {
        "super_admin" => new[] { "lobby", "new_game", "grid", "scenarios", "system" },
        "director" => new[] { "lobby", "new_game", "grid", "scenarios" },
        "red" or "blue" => new[] { "lobby", "new_game", "grid" },
        _ => new[] { "lobby" },
    };

    private void GrantMenus(string role, IEnumerable<string> codes)
    {
        foreach (var c in codes)
            ExecCmd("INSERT OR IGNORE INTO role_menus(role_code, menu_code) VALUES($r,$c)", ("$r", role), ("$c", c));
    }

    // ==================== 认证 ====================

    public static (string hash, string salt) HashPassword(string password)
    {
        var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(salt + ":" + password)));
        return (hash, salt);
    }

    public static string HashPassword(string password, string salt) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(salt + ":" + password)));

    /// <summary>登录校验；成功返回用户行，失败返回 null（登录日志由调用方记）。</summary>
    public Dictionary<string, object?>? VerifyUser(string username, string password)
    {
        var u = QueryRow("SELECT * FROM users WHERE username=$u AND status='active'", ("$u", username));
        if (u == null) return null;
        if ((string)u["password_hash"]! != HashPassword(password, (string)u["salt"]!)) return null;
        return u;
    }

    public string CreateSession(int userId)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        ExecCmd("INSERT INTO sessions(token, user_id, created_at, last_seen_at) VALUES($t,$u,$c,$l)",
            ("$t", token), ("$u", userId), ("$c", Now()), ("$l", Now()));
        return token;
    }

    public void DropSession(string token) =>
        ExecCmd("DELETE FROM sessions WHERE token=$t", ("$t", token));

    /// <summary>会话 → 用户（含角色名与可见菜单）；无效返回 null。</summary>
    public Dictionary<string, object?>? UserByToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var s = QueryRow("SELECT * FROM sessions WHERE token=$t", ("$t", token));
        if (s == null) return null;
        ExecCmd("UPDATE sessions SET last_seen_at=$l WHERE token=$t", ("$l", Now()), ("$t", token));
        var u = QueryRow("SELECT * FROM users WHERE id=$id AND status='active'", ("$id", s["user_id"]));
        if (u == null) return null;
        u.Remove("password_hash");
        u.Remove("salt");
        u["menus"] = QueryList(
            "SELECT m.code, m.name, m.path FROM role_menus rm JOIN menus m ON m.code = rm.menu_code " +
            "WHERE rm.role_code=$r AND m.visible=1 ORDER BY m.sort", ("$r", u["role_code"]));
        return u;
    }

    public bool CreateUser(string username, string password, string? displayName, string roleCode)
    {
        if (QueryRow("SELECT id FROM users WHERE username=$u", ("$u", username)) != null) return false;
        var (hash, salt) = HashPassword(password);
        ExecCmd("INSERT INTO users(username, password_hash, salt, display_name, role_code, status, created_at) " +
                "VALUES($u,$h,$s,$d,$r,'active',$c)",
            ("$u", username), ("$h", hash), ("$s", salt), ("$d", (object?)displayName ?? DBNull.Value),
            ("$r", roleCode), ("$c", Now()));
        return true;
    }

    public bool SetUserStatus(int userId, string status) =>
        ExecCmd("UPDATE users SET status=$s WHERE id=$id", ("$s", status), ("$id", userId)) > 0;

    public void UpdateLastLogin(int userId) =>
        ExecCmd("UPDATE users SET last_login_at=$t WHERE id=$id", ("$t", Now()), ("$id", userId));

    public Dictionary<string, object?>? UserById(int userId) =>
        QueryRow("SELECT id, username, display_name, role_code, status, created_at, last_login_at FROM users WHERE id=$id", ("$id", userId));

    /// <summary>更新用户（非 null 字段生效）；角色/状态/密码变更后吊销其全部会话。</summary>
    public bool UpdateUser(int userId, string? displayName, string? roleCode, string? status, string? newPassword)
    {
        var sets = new List<string>();
        var ps = new List<(string Name, object? Value)>();
        if (displayName != null) { sets.Add("display_name=$d"); ps.Add(("$d", displayName)); }
        if (roleCode != null) { sets.Add("role_code=$r"); ps.Add(("$r", roleCode)); }
        if (status != null) { sets.Add("status=$s"); ps.Add(("$s", status)); }
        if (newPassword != null)
        {
            var (h, sl) = HashPassword(newPassword);
            sets.Add("password_hash=$h"); sets.Add("salt=$sl");
            ps.Add(("$h", h)); ps.Add(("$sl", sl));
        }
        if (sets.Count == 0) return UserById(userId) != null;
        ps.Add(("$id", userId));
        var n = ExecCmd($"UPDATE users SET {string.Join(", ", sets)} WHERE id=$id", ps.ToArray());
        if (n > 0 && (roleCode != null || status != null || newPassword != null)) DropUserSessions(userId);
        return n > 0;
    }

    /// <summary>删除用户并吊销会话。是否允许删（最后一个超管/自己）由控制器判断。</summary>
    public bool DeleteUser(int userId) =>
        ExecCmd("DELETE FROM users WHERE id=$id", ("$id", userId)) > 0;

    public void DropUserSessions(int userId) =>
        ExecCmd("DELETE FROM sessions WHERE user_id=$u", ("$u", userId));

    public long CountUsersInRole(string roleCode) =>
        (long)(QueryRow("SELECT COUNT(*) AS n FROM users WHERE role_code=$r", ("$r", roleCode))?["n"] ?? 0L);

    // ==================== 角色 / 菜单管理 ====================

    public bool CreateRole(string code, string name, string? description)
    {
        if (QueryRow("SELECT id FROM roles WHERE code=$c", ("$c", code)) != null) return false;
        return ExecCmd("INSERT INTO roles(code, name, description) VALUES($c,$n,$d)",
            ("$c", code), ("$n", name), ("$d", (object?)description ?? DBNull.Value)) > 0;
    }

    public bool UpdateRole(string code, string name, string? description)
    {
        if (description != null)
            return ExecCmd("UPDATE roles SET name=$n, description=$d WHERE code=$c",
                ("$n", name), ("$d", description), ("$c", code)) > 0;
        return ExecCmd("UPDATE roles SET name=$n WHERE code=$c", ("$n", name), ("$c", code)) > 0;
    }

    /// <summary>删除角色及其菜单授权。有用户引用时由控制器拒绝。</summary>
    public bool DeleteRole(string code)
    {
        if (ExecCmd("DELETE FROM roles WHERE code=$c", ("$c", code)) == 0) return false;
        ExecCmd("DELETE FROM role_menus WHERE role_code=$c", ("$c", code));
        return true;
    }

    /// <summary>整体重设角色可见菜单（先清后插）。</summary>
    public void SetRoleMenus(string roleCode, IEnumerable<string> menuCodes)
    {
        ExecCmd("DELETE FROM role_menus WHERE role_code=$r", ("$r", roleCode));
        GrantMenus(roleCode, menuCodes);
    }

    public bool CreateMenu(string code, string name, string? path, int sort, bool visible)
    {
        if (QueryRow("SELECT id FROM menus WHERE code=$c", ("$c", code)) != null) return false;
        return ExecCmd("INSERT INTO menus(code, name, path, sort, visible) VALUES($c,$n,$p,$s,$v)",
            ("$c", code), ("$n", name), ("$p", (object?)path ?? DBNull.Value), ("$s", sort), ("$v", visible ? 1 : 0)) > 0;
    }

    public bool UpdateMenu(int id, string name, string? path, int sort, bool visible) =>
        ExecCmd("UPDATE menus SET name=$n, path=$p, sort=$s, visible=$v WHERE id=$id",
            ("$n", name), ("$p", (object?)path ?? DBNull.Value), ("$s", sort), ("$v", visible ? 1 : 0), ("$id", id)) > 0;

    public bool DeleteMenu(int id)
    {
        var m = QueryRow("SELECT code FROM menus WHERE id=$id", ("$id", id));
        if (m == null) return false;
        ExecCmd("DELETE FROM role_menus WHERE menu_code=$c", ("$c", m["code"]));
        ExecCmd("DELETE FROM menus WHERE id=$id", ("$id", id));
        return true;
    }

    /// <summary>通用单行查询（供控制器做角色存在性等轻量校验）。</summary>
    public Dictionary<string, object?>? QueryRowPublic(string sql, params (string Name, object? Value)[] ps) =>
        QueryRow(sql, ps);

    /// <summary>通用多行查询（管理端点列表用）。</summary>
    public List<Dictionary<string, object?>> QueryListPublic(string sql, params (string Name, object? Value)[] ps) =>
        QueryList(sql, ps);

    // ==================== 日志 ====================

    public void LogLogin(string? username, int? userId, string action, bool ok, string? ip, string? ua, string? detail = null) =>
        ExecCmd("INSERT INTO login_logs(ts, username, user_id, action, ok, ip, user_agent, detail) VALUES($ts,$un,$uid,$ac,$ok,$ip,$ua,$de)",
            ("$ts", Now()), ("$un", (object?)username ?? DBNull.Value), ("$uid", userId.HasValue ? userId.Value : DBNull.Value),
            ("$ac", action), ("$ok", ok ? 1 : 0), ("$ip", (object?)ip ?? DBNull.Value),
            ("$ua", (object?)ua ?? DBNull.Value), ("$de", (object?)detail ?? DBNull.Value));

    public void LogQuery(string method, string path, int? status, long durationMs, string? username, string? ip) =>
        ExecCmd("INSERT INTO query_logs(ts, method, path, status, duration_ms, username, ip) VALUES($ts,$m,$p,$s,$d,$u,$i)",
            ("$ts", Now()), ("$m", method), ("$p", path), ("$s", status.HasValue ? status.Value : DBNull.Value),
            ("$d", durationMs), ("$u", (object?)username ?? DBNull.Value), ("$i", (object?)ip ?? DBNull.Value));

    public void LogError(string source, string? path, string message, string? stack) =>
        ExecCmd("INSERT INTO error_logs(ts, source, path, message, stack) VALUES($ts,$s,$p,$m,$st)",
            ("$ts", Now()), ("$s", source), ("$p", (object?)path ?? DBNull.Value),
            ("$m", message), ("$st", (object?)stack ?? DBNull.Value));

    // ==================== 用户反馈 ====================

    public long AddFeedback(string scenario, string kind, string detail, string? contact, string? username)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO feedback(ts, scenario, kind, detail, contact, username, status) VALUES($ts,$sc,$k,$d,$c,$u,'open'); " +
                              "SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$ts", Now());
            cmd.Parameters.AddWithValue("$sc", scenario);
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$d", detail);
            cmd.Parameters.AddWithValue("$c", (object?)contact ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$u", (object?)username ?? DBNull.Value);
            return (long)cmd.ExecuteScalar()!;
        }
    }

    public List<Dictionary<string, object?>> ListFeedback(string? scenario = null, string? status = null, int limit = 200)
    {
        var where = "1=1";
        if (!string.IsNullOrEmpty(scenario)) where += " AND scenario=$sc";
        if (!string.IsNullOrEmpty(status)) where += " AND status=$st";
        return QueryList($"SELECT * FROM feedback WHERE {where} ORDER BY id DESC LIMIT $lim",
            ("$sc", scenario), ("$st", status), ("$lim", limit));
    }

    public Dictionary<string, int> FeedbackCounts()
    {
        var rows = QueryList("SELECT scenario, COUNT(*) AS n FROM feedback GROUP BY scenario");
        return rows.ToDictionary(r => (string)r["scenario"]!, r => (int)(long)r["n"]!);
    }

    public bool ResolveFeedback(long id, string status) =>
        ExecCmd("UPDATE feedback SET status=$s WHERE id=$id", ("$s", status), ("$id", id)) > 0;

    public bool DeleteFeedback(long id) => ExecCmd("DELETE FROM feedback WHERE id=$id", ("$id", id)) > 0;

    // ==================== 剧本目录 ====================

    /// <summary>扫描剧本目录同步 catalog（轻量解析，缺字段不报错）。</summary>
    public int SyncScenarios(string scenarioDir)
    {
        var n = 0;
        try
        {
            foreach (var file in Directory.GetFiles(scenarioDir, "*.json"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var d = doc.RootElement;
                    string S(string k) => d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    int I(string k) => d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
                    var env = d.TryGetProperty("environment", out var e) ? e : default;
                    string SideName(string side) =>
                        d.TryGetProperty("sides", out var ss) && ss.TryGetProperty(side, out var sv) && sv.TryGetProperty("name", out var nv)
                            ? nv.GetString() ?? "" : "";
                    var unitCount = 0;
                    if (d.TryGetProperty("sides", out var sides) && sides.ValueKind == JsonValueKind.Object)
                        foreach (var side in sides.EnumerateObject())
                            if (side.Value.TryGetProperty("oob", out var oob) && oob.ValueKind == JsonValueKind.Array)
                                foreach (var u in oob.EnumerateArray())
                                    unitCount += u.TryGetProperty("qty", out var q) && q.ValueKind == JsonValueKind.Number ? q.GetInt32() : 1;
                    ExecCmd("INSERT OR REPLACE INTO scenarios(id,name,era,scale,max_turns,cell_km,unit_count,red_name,blue_name,description,file,updated_at,synced_at) " +
                            "VALUES($id,$nm,$er,$sc,$mt,$ck,$uc,$rn,$bn,$de,$f,$up,$sy)",
                        ("$id", S("id") is { Length: > 0 } id ? id : Path.GetFileNameWithoutExtension(file)),
                        ("$nm", S("name")), ("$er", S("era")), ("$sc", S("scale")), ("$mt", I("max_turns")),
                        ("$ck", env.ValueKind == JsonValueKind.Object && env.TryGetProperty("cell_km", out var ck) ? ck.GetDouble() : 0),
                        ("$uc", unitCount), ("$rn", SideName("red")), ("$bn", SideName("blue")),
                        ("$de", S("description")), ("$f", file),
                        ("$up", File.GetLastWriteTimeUtc(file).ToString("o")), ("$sy", Now()));
                    n++;
                }
                catch (Exception ex) { _logger.LogWarning(ex, "剧本目录同步失败: {File}", file); }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "剧本目录不可读: {Dir}", scenarioDir); }
        return n;
    }

    public List<Dictionary<string, object?>> ListScenarios(int limit = 500) =>
        QueryList("SELECT * FROM scenarios ORDER BY era, id LIMIT $lim", ("$lim", limit));

    // ==================== 房间 / 副本 ====================

    public void UpsertRoom(string code, string instance, string? scenario, string? name, int turn) =>
        ExecCmd("INSERT INTO rooms(code, instance, scenario, name, turn, opened_at, status) VALUES($c,$i,$sc,$nm,$t,$o,'open') " +
                "ON CONFLICT(code) DO UPDATE SET turn=$t, name=COALESCE($nm, name)",
            ("$c", code), ("$i", instance), ("$sc", scenario), ("$nm", name), ("$t", turn), ("$o", Now()));

    public List<Dictionary<string, object?>> ListRooms(int limit = 200) =>
        QueryList("SELECT * FROM rooms ORDER BY opened_at DESC LIMIT $lim", ("$lim", limit));

    public void UpsertSave(StoreSaveMeta meta) =>
        ExecCmd("INSERT OR REPLACE INTO saves(instance,name,scenario,seed,last_turn,max_turns,created_at,updated_at,source) " +
                "VALUES($i,$nm,$sc,$sd,$lt,$mt,$ca,$ua,$so)",
            ("$i", meta.Instance), ("$nm", meta.Name), ("$sc", meta.Scenario), ("$sd", meta.Seed),
            ("$lt", meta.LastTurn), ("$mt", meta.MaxTurns), ("$ca", meta.CreatedAt), ("$ua", meta.UpdatedAt), ("$so", meta.Source));

    public List<Dictionary<string, object?>> ListSaves(int limit = 200) =>
        QueryList("SELECT * FROM saves ORDER BY updated_at DESC LIMIT $lim", ("$lim", limit));

    // ==================== 推演操作流水（原 OpsLogService 职能） ====================

    public void OpsLogInstance(string instanceId, string scenario, string? name, int? seed, string source)
    {
        try
        {
            ExecCmd("INSERT OR IGNORE INTO instances(id, scenario, name, seed, source, created_at) VALUES($i,$sc,$nm,$sd,$src,$ts)",
                ("$i", instanceId), ("$sc", scenario), ("$nm", (object?)name ?? DBNull.Value),
                ("$sd", seed.HasValue ? seed.Value : DBNull.Value), ("$src", source), ("$ts", Now()));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "实例登记写入失败 {Instance}", instanceId); }
    }

    public void OpsInsert(string instanceId, string ts, string stage, string? actor, int? turn, string payloadJson)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO ops(instance, ts, stage, actor, turn, payload) VALUES($i,$ts,$st,$ac,$tu,$pl)";
            cmd.Parameters.AddWithValue("$i", instanceId);
            cmd.Parameters.AddWithValue("$ts", ts);
            cmd.Parameters.AddWithValue("$st", stage);
            cmd.Parameters.AddWithValue("$ac", (object?)actor ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$tu", turn.HasValue ? turn.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("$pl", payloadJson);
            cmd.ExecuteNonQuery();
        }
    }

    public List<Dictionary<string, object?>> OpsInstances(int limit) =>
        QueryList("""
            SELECT i.id, i.scenario, i.name, i.seed, i.source, i.created_at,
                   COUNT(o.id) AS op_count, MAX(o.ts) AS last_at
            FROM instances i LEFT JOIN ops o ON o.instance = i.id
            GROUP BY i.id ORDER BY i.created_at DESC LIMIT $lim
            """, ("$lim", limit));

    public List<Dictionary<string, object?>> OpsQuery(string instanceId, string? stage, int? turn, int limit)
    {
        var where = "instance = $i";
        if (!string.IsNullOrEmpty(stage)) where += " AND stage = $st";
        if (turn.HasValue) where += " AND turn = $tu";
        var rows = QueryList($"""
            SELECT id, ts, stage, actor, turn, payload FROM (
                SELECT id, ts, stage, actor, turn, payload FROM ops WHERE {where} ORDER BY id DESC LIMIT $lim
            ) ORDER BY id ASC
            """, ("$i", instanceId), ("$st", stage), ("$tu", turn), ("$lim", limit));
        foreach (var r in rows)
        {
            try { r["payload"] = JsonDocument.Parse((string)r["payload"]!).RootElement.Clone(); }
            catch { /* 保留字符串 */ }
        }
        return rows;
    }

    public Dictionary<string, object?> OpsStats(string instanceId)
    {
        var rows = QueryList("SELECT stage, COUNT(*) AS n FROM ops WHERE instance=$i GROUP BY stage", ("$i", instanceId));
        return new Dictionary<string, object?>
        {
            ["instance"] = instanceId,
            ["stages"] = rows.ToDictionary(r => (string)r["stage"]!, r => (int)(long)r["n"]!),
        };
    }

    // ==================== 历史迁移 ====================

    /// <summary>ops_log.db（旧推演流水库）→ app.db，成功后改名 .migrated。</summary>
    private void MigrateOpsLog()
    {
        var assemblyPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        var binDir = Path.GetDirectoryName(assemblyPath);
        if (string.IsNullOrEmpty(binDir)) return;
        var root = Path.GetFullPath(Path.Combine(binDir, "..", "..", "..", "..", "..", ".."));
        var oldPath = Path.Combine(root, "data", "ops_log.db");
        if (!File.Exists(oldPath)) return;
        try
        {
            using var old = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = oldPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
            }.ToString());
            old.Open();
            int ni = 0, no = 0;
            using (var q = old.CreateCommand())
            {
                q.CommandText = "SELECT id, scenario, name, seed, source, created_at FROM instances";
                using var r = q.ExecuteReader();
                while (r.Read())
                {
                    ExecCmd("INSERT OR IGNORE INTO instances(id, scenario, name, seed, source, created_at) VALUES($i,$sc,$nm,$sd,$src,$ts)",
                        ("$i", r.GetString(0)), ("$sc", r.GetString(1)),
                        ("$nm", r.IsDBNull(2) ? DBNull.Value : r.GetValue(2)),
                        ("$sd", r.IsDBNull(3) ? DBNull.Value : r.GetValue(3)),
                        ("$src", r.GetString(4)), ("$ts", r.GetString(5)));
                    ni++;
                }
            }
            using (var q = old.CreateCommand())
            {
                q.CommandText = "SELECT id, instance, ts, stage, actor, turn, payload FROM ops ORDER BY id";
                using var r = q.ExecuteReader();
                while (r.Read())
                {
                    ExecCmd("INSERT OR IGNORE INTO ops(id, instance, ts, stage, actor, turn, payload) VALUES($id,$i,$ts,$st,$ac,$tu,$pl)",
                        ("$id", r.GetInt64(0)), ("$i", r.GetString(1)), ("$ts", r.GetString(2)),
                        ("$st", r.GetString(3)), ("$ac", r.IsDBNull(4) ? DBNull.Value : r.GetValue(4)),
                        ("$tu", r.IsDBNull(5) ? DBNull.Value : r.GetValue(5)), ("$pl", r.GetString(6)));
                    no++;
                }
            }
            Exec("UPDATE sqlite_sequence SET seq=(SELECT MAX(id) FROM ops) WHERE name='ops'");
            File.Move(oldPath, oldPath + ".migrated", overwrite: true);
            foreach (var suffix in new[] { "-wal", "-shm" })
                if (File.Exists(oldPath + suffix)) File.Delete(oldPath + suffix);
            _logger.LogInformation("ops_log.db 迁移完成: instances={I} ops={O}", ni, no);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ops_log.db 迁移失败（保留原文件，下次启动重试）");
        }
    }

    /// <summary>scenario_feedback.json（旧反馈文件）→ feedback 表，成功后改名 .migrated。</summary>
    private void MigrateFeedbackFile()
    {
        var assemblyPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        var binDir = Path.GetDirectoryName(assemblyPath);
        if (string.IsNullOrEmpty(binDir)) return;
        var root = Path.GetFullPath(Path.Combine(binDir, "..", "..", "..", "..", "..", ".."));
        var oldPath = Path.Combine(root, "data", "scenario_feedback.json");
        if (!File.Exists(oldPath)) return;
        try
        {
            var arr = JsonDocument.Parse(File.ReadAllText(oldPath)).RootElement;
            int n = 0;
            if (arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    string S(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    ExecCmd("INSERT OR IGNORE INTO feedback(ts, scenario, kind, detail, contact, username, status) VALUES($ts,$sc,$k,$d,$c,$u,$st)",
                        ("$ts", S("ts") is { Length: > 0 } ts ? ts : Now()), ("$sc", S("scenario")),
                        ("$k", S("kind")), ("$d", S("detail")), ("$c", S("contact")),
                        ("$u", S("username")), ("$st", S("status") is { Length: > 0 } st ? st : "open"));
                    n++;
                }
            }
            File.Move(oldPath, oldPath + ".migrated", overwrite: true);
            _logger.LogInformation("scenario_feedback.json 迁移完成: {N} 条", n);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "scenario_feedback.json 迁移失败（保留原文件）");
        }
    }

    // ==================== 基础设施 ====================

    private static string Now() => DateTime.UtcNow.ToString("o");
    private static string Esc(string s) => s.Replace("'", "''");

    private void Exec(string sql)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    /// <returns>受影响行数</returns>
    private int ExecCmd(string sql, params (string Name, object? Value)[] ps)
    {
        try
        {
            lock (_gate)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = sql;
                foreach (var (name, value) in ps)
                    cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
                return cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SQL 执行失败: {Sql}", sql);
            return 0;
        }
    }

    private long Scalar(string sql)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            return (long)cmd.ExecuteScalar()!;
        }
    }

    private Dictionary<string, object?>? QueryRow(string sql, params (string Name, object? Value)[] ps)
    {
        var rows = QueryList(sql, ps);
        return rows.Count > 0 ? rows[0] : null;
    }

    private List<Dictionary<string, object?>> QueryList(string sql, params (string Name, object? Value)[] ps)
    {
        var rows = new List<Dictionary<string, object?>>();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in ps)
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < r.FieldCount; i++)
                    row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
                rows.Add(row);
            }
        }
        return rows;
    }

    public void Dispose()
    {
        try { _conn.Dispose(); } catch { }
    }
}

/// <summary>saves 表的轻量行（与 StoreService.SaveMetadata 解耦）。</summary>
public sealed record StoreSaveMeta(
    string Instance, string? Name, string Scenario, long Seed,
    int LastTurn, int MaxTurns, string CreatedAt, string UpdatedAt, string Source);
