using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HexFront.Core.Services;
using HexFront.Core;
using Microsoft.Data.Sqlite;

namespace HexFront.Api.Services;

/// <summary>
/// 统一 SQLite 应用库（server/HexFront.Api/app.db，HF_APP_DB 可覆盖）：用户/角色/菜单/会话、
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
        var dbPath = UtilsPath.EnvOrNull("HF_APP_DB") ?? DefaultDbPath();
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

    /// <summary>
    /// server 根目录（含 HexFront.Api 与 data）的解析已统一收敛到 <see cref="HexFront.Core.UtilsPath"/>，
    /// 后端与工具端共用同一套逻辑。本类的 app.db 路径经由 UtilsPath.ServerRoot() 派生，
    /// 确保「库与 data 目录落在同一棵树上」（否则剧本读不到、库却是新的）。
    /// </summary>

    private static string DefaultDbPath()
    {
        // app.db 固定落在 server/HexFront.Api/app.db，与 UtilsPath.ServerRoot() 用同一套解析，
        // 避免两处各自拼路径导致「库和 data 目录不在一棵树上」。
        var apiDir = Path.Combine(UtilsPath.ServerRoot(), "HexFront.Api");
        if (Directory.Exists(apiDir))
            return Path.Combine(apiDir, "app.db");

        // 兜底（目录被改名、server 目录尚未创建等）：从程序集上溯找 HexFront.Api.csproj 同级目录，
        // app.db 与 .csproj 同目录。
        var dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
        while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "HexFront.Api.csproj")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir ?? Directory.GetCurrentDirectory(), "app.db");
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
        // 剧本完整内容（与 scenarios 目录表互补：目录表只存轻量 catalog，本表存全文 JSON）
        Exec("""
            CREATE TABLE IF NOT EXISTS scenario_docs (
                id TEXT PRIMARY KEY,
                doc_json TEXT NOT NULL,
                bytes INTEGER,
                file TEXT,
                updated_at TEXT,
                synced_at TEXT NOT NULL
            )
            """);
        // 推演存档完整内容：每个实例多文件（meta/init/snapshot_tN/turns）→ 统一入库
        Exec("""
            CREATE TABLE IF NOT EXISTS save_docs (
                instance TEXT NOT NULL,
                kind TEXT NOT NULL,        -- meta | init | snapshot | turns
                turn INTEGER,              -- snapshot 的回合号；其余为 NULL
                doc_json TEXT NOT NULL,
                bytes INTEGER,
                updated_at TEXT,
                PRIMARY KEY (instance, kind, turn)
            )
            """);
        Exec("CREATE INDEX IF NOT EXISTS ix_save_docs_instance ON save_docs(instance)");
        // turn=NULL 的行（meta/init 外的 turns）不在主键去重范围内（SQLite 中 NULL 互不相等），
        // 用部分唯一索引保证 INSERT OR REPLACE 对 meta/turns 也生效
        Exec("CREATE UNIQUE INDEX IF NOT EXISTS ux_save_docs_nullturn ON save_docs(instance, kind) WHERE turn IS NULL");
        // 剧本保存前的版本备份（原 data/scenario_backups 磁盘备份的库内化）
        Exec("""
            CREATE TABLE IF NOT EXISTS scenario_backups (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                scenario TEXT NOT NULL,
                json TEXT NOT NULL,
                bytes INTEGER,
                created_at TEXT NOT NULL
            )
            """);
        // 地图包目录：id 与剧本的地图 id（JSON hexgrid / scenarios.map_id）对应；
        // path 为包存储路径（data/hexgrids/<id>.zip），rows/cols 行列数，flat_to_flat 对边距（米）
        Exec("""
            CREATE TABLE IF NOT EXISTS map_packages (
                id TEXT PRIMARY KEY,
                path TEXT,
                rows INTEGER,
                cols INTEGER,
                flat_to_flat REAL,
                updated_at TEXT,
                synced_at TEXT NOT NULL
            )
            """);
        // 列迁移：scenarios.map_id（剧本绑定的地图 id，取自剧本 JSON 的 hexgrid 字段）
        if (!HasColumn("scenarios", "map_id"))
            Exec("ALTER TABLE scenarios ADD COLUMN map_id TEXT");
    }

    /// <summary>幂等检查某表是否已有某列（表不存在返回 false）。</summary>
    private bool HasColumn(string table, string column)
    {
        var row = QueryRow($"SELECT COUNT(*) AS n FROM pragma_table_info('{Esc(table)}') WHERE name='{Esc(column)}'");
        return row != null && Convert.ToInt64(row["n"]) > 0;
    }

    /// <summary>启动初始化：种子数据 → 历史库迁移 → 剧本目录/内容同步 → 地图包目录同步 → 推演存档内容同步。幂等可重复调用。</summary>
    public void Initialize(string? scenarioDir = null, string? saveDir = null, string? hexgridDir = null)
    {
        SeedDefaults();
        MigrateOpsLog();
        MigrateFeedbackFile();
        if (scenarioDir != null)
        {
            SyncScenarios(scenarioDir);
            SyncScenarioDocs(scenarioDir);
        }
        BackfillScenarioMapIds();
        if (hexgridDir != null) SyncMapPackages(hexgridDir);
        if (saveDir != null) SyncSaveDocs(saveDir);
    }

    /// <summary>从 scenario_docs（真源）提取剧本 JSON 的 hexgrid 回填 scenarios.map_id，覆盖仅存库内、磁盘已缺的剧本。</summary>
    private void BackfillScenarioMapIds()
    {
        var n = 0;
        foreach (var r in QueryList("SELECT id, doc_json FROM scenario_docs"))
        {
            try
            {
                string? mapId = null;
                using (var doc = JsonDocument.Parse((string)r["doc_json"]!))
                    if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty("hexgrid", out var hg) &&
                        hg.ValueKind == JsonValueKind.String)
                        mapId = hg.GetString();
                ExecCmd("UPDATE scenarios SET map_id=$m WHERE id=$id", ("$m", mapId), ("$id", (string)r["id"]!));
                n++;
            }
            catch (Exception ex) { _logger.LogWarning(ex, "剧本 map_id 回填失败: {Id}", r["id"]); }
        }
        _logger.LogInformation("剧本地图列回填完成: {N} 条", n);
    }

    // ==================== 地图包目录 ====================
    // id 与剧本的地图 id（JSON hexgrid / scenarios.map_id）对应，记录包路径、行列数、对边距；
    // 真源是 data/hexgrids/<id>.zip，本表是可查询的目录镜像（启动同步 + 增删时维护）。

    /// <summary>启动同步：扫描包目录 zip 入库；目录里已消失的包同步删行。返回条数。</summary>
    public int SyncMapPackages(string hexgridDir)
    {
        var n = 0;
        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var doc in HexGridPackageService.ListAll(hexgridDir))
            {
                UpsertMapPackage(doc.Id, Path.Combine(hexgridDir, doc.Id + ".zip"),
                    doc.Rows, doc.Cols, doc.FlatToFlat);
                seen.Add(doc.Id);
                n++;
            }
            // 目录已删但表里残留的行一并清理
            foreach (var r in QueryList("SELECT id FROM map_packages"))
                if (r["id"] is string id && !seen.Contains(id))
                    DeleteMapPackage(id);
            _logger.LogInformation("地图包目录同步完成: {N} 个", n);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "地图包目录不可读: {Dir}", hexgridDir); }
        return n;
    }

    /// <summary>单条地图包 upsert（幂等）。</summary>
    public void UpsertMapPackage(string id, string? path, int rows, int cols, double flatToFlat) =>
        ExecCmd("INSERT OR REPLACE INTO map_packages(id,path,rows,cols,flat_to_flat,updated_at,synced_at) VALUES($id,$p,$r,$c,$f,$u,$s)",
            ("$id", id), ("$p", path), ("$r", rows), ("$c", cols), ("$f", flatToFlat),
            ("$u", DateTime.UtcNow.ToString("o")), ("$s", Now()));

    /// <summary>按地图 id 取地图包目录行；null 表示未登记。</summary>
    public Dictionary<string, object?>? GetMapPackage(string id) =>
        QueryRow("SELECT * FROM map_packages WHERE id=$id", ("$id", id));

    /// <summary>全部地图包目录行。</summary>
    public List<Dictionary<string, object?>> ListMapPackages() =>
        QueryList("SELECT * FROM map_packages ORDER BY id");

    /// <summary>删除地图包目录行。</summary>
    public bool DeleteMapPackage(string id) =>
        ExecCmd("DELETE FROM map_packages WHERE id=$id", ("$id", id)) > 0;

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
            ("saves", "副本管理", "/saves", 4),
            ("scenarios", "剧本管理", "/scenarios", 5),
            ("system", "系统管理", "/admin", 6),
        };
        foreach (var (code, name, path, sort) in menus)
            Exec($"INSERT OR IGNORE INTO menus(code, name, path, sort, visible) VALUES('{code}','{Esc(name)}','{path}',{sort},1)");

        // 存量迁移：新菜单「副本管理」授予所有已拥有大厅权限的角色（大厅本就展示存档列表）
        ExecCmd("INSERT OR IGNORE INTO role_menus(role_code, menu_code) " +
                "SELECT role_code, 'saves' FROM role_menus WHERE menu_code='lobby'");

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
        "super_admin" => new[] { "lobby", "new_game", "saves", "grid", "scenarios", "system" },
        "director" => new[] { "lobby", "new_game", "saves", "grid", "scenarios" },
        "red" or "blue" => new[] { "lobby", "new_game", "saves", "grid" },
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

    /// <summary>扫描剧本目录同步 catalog（轻量解析，缺字段不报错）。磁盘仅作回填源，真源在 scenario_docs/本表。</summary>
    public int SyncScenarios(string scenarioDir)
    {
        var n = 0;
        try
        {
            foreach (var file in Directory.GetFiles(scenarioDir, "*.json"))
            {
                try
                {
                    if (UpsertScenarioCatalog(
                            Path.GetFileNameWithoutExtension(file), File.ReadAllText(file), file))
                        n++;
                }
                catch (Exception ex) { _logger.LogWarning(ex, "剧本目录同步失败: {File}", file); }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "剧本目录不可读: {Dir}", scenarioDir); }
        return n;
    }

    /// <summary>单条剧本 catalog upsert（从全文 JSON 轻量解析；id 取 JSON 内 id 或传入 id）。</summary>
    public bool UpsertScenarioCatalog(string fallbackId, string json, string? file)
    {
        using var doc = JsonDocument.Parse(json);
        var d = doc.RootElement;
        if (d.ValueKind != JsonValueKind.Object) return false;
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
        var id = S("id") is { Length: > 0 } sid ? sid : fallbackId;
        var mapId = d.TryGetProperty("hexgrid", out var hg) && hg.ValueKind == JsonValueKind.String ? hg.GetString() : null;
        ExecCmd("INSERT OR REPLACE INTO scenarios(id,name,era,scale,max_turns,cell_km,unit_count,red_name,blue_name,description,map_id,file,updated_at,synced_at) " +
                "VALUES($id,$nm,$er,$sc,$mt,$ck,$uc,$rn,$bn,$de,$mi,$f,$up,$sy)",
            ("$id", id),
            ("$nm", S("name")), ("$er", S("era")), ("$sc", S("scale")), ("$mt", I("max_turns")),
            ("$ck", env.ValueKind == JsonValueKind.Object && env.TryGetProperty("cell_km", out var ck) ? ck.GetDouble() : 0),
            ("$uc", unitCount), ("$rn", SideName("red")), ("$bn", SideName("blue")),
            ("$de", S("description")), ("$mi", mapId), ("$f", (object?)file ?? id + ".json"),
            ("$up", DateTime.UtcNow.ToString("o")), ("$sy", Now()));
        return true;
    }

    public List<Dictionary<string, object?>> ListScenarios(int limit = 500) =>
        QueryList("SELECT * FROM scenarios ORDER BY era, id LIMIT $lim", ("$lim", limit));

    /// <summary>扫描剧本目录，把全文 JSON 入库到 scenario_docs（幂等：INSERT OR REPLACE）。</summary>
    public int SyncScenarioDocs(string scenarioDir)
    {
        if (!Directory.Exists(scenarioDir)) return 0;
        var n = 0;
        try
        {
            foreach (var file in Directory.GetFiles(scenarioDir, "*.json"))
            {
                try { if (SyncScenarioDoc(file)) n++; }
                catch (Exception ex) { _logger.LogWarning(ex, "剧本内容入库失败: {File}", file); }
            }
            _logger.LogInformation("剧本完整内容同步完成: {N} 个", n);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "剧本目录不可读: {Dir}", scenarioDir); }
        return n;
    }

    /// <summary>单个剧本文件 → scenario_docs（幂等）。</summary>
    public bool SyncScenarioDoc(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var d = doc.RootElement;
        var id = d.TryGetProperty("id", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()! : Path.GetFileNameWithoutExtension(path);
        var json = File.ReadAllText(path);
        ExecCmd("INSERT OR REPLACE INTO scenario_docs(id, doc_json, bytes, file, updated_at, synced_at) VALUES($id,$d,$b,$f,$u,$s)",
            ("$id", id), ("$d", json), ("$b", json.Length), ("$f", path),
            ("$u", File.GetLastWriteTimeUtc(path).ToString("o")), ("$s", Now()));
        return true;
    }

    /// <summary>取剧本全文（供管理接口/回放；null 表示未入库）。</summary>
    public string? GetScenarioDoc(string id) =>
        (string?)QueryRow("SELECT doc_json FROM scenario_docs WHERE id=$id", ("$id", id))?["doc_json"];

    /// <summary>全部剧本文档（id/文件名/全文），按文件名排序——剧本列表与开局读取的真源。</summary>
    public List<(string Id, string File, string Json)> ListScenarioDocs()
    {
        var outList = new List<(string Id, string File, string Json)>();
        foreach (var r in QueryList("SELECT id, file, doc_json FROM scenario_docs ORDER BY file"))
            outList.Add(((string)r["id"]!, (string?)r["file"] ?? "", (string)r["doc_json"]!));
        return outList;
    }

    /// <summary>按 id 或文件名（含/不含 .json）找剧本文档；null 表示库里没有。</summary>
    public (string Id, string File, string Json)? FindScenarioDoc(string idOrFile)
    {
        if (string.IsNullOrEmpty(idOrFile)) return null;
        var row = QueryRow("SELECT id, file, doc_json FROM scenario_docs WHERE id=$id", ("$id", idOrFile));
        if (row == null)
        {
            var stem = idOrFile.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? idOrFile[..^5] : idOrFile;
            row = QueryRow("SELECT id, file, doc_json FROM scenario_docs WHERE id=$id", ("$id", stem));
        }
        if (row == null) return null;
        return ((string)row["id"]!, (string?)row["file"] ?? "", (string)row["doc_json"]!);
    }

    /// <summary>剧本全文入库（管理保存用；幂等）。</summary>
    public void UpsertScenarioDoc(string id, string json, string? file) =>
        ExecCmd("INSERT OR REPLACE INTO scenario_docs(id, doc_json, bytes, file, updated_at, synced_at) VALUES($id,$d,$b,$f,$u,$s)",
            ("$id", id), ("$d", json), ("$b", json.Length),
            ("$f", string.IsNullOrEmpty(file) ? id + ".json" : file),
            ("$u", DateTime.UtcNow.ToString("o")), ("$s", Now()));

    /// <summary>保存前的旧版本入库备份，返回备份 id。</summary>
    public long AddScenarioBackup(string scenario, string json)
    {
        ExecCmd("INSERT INTO scenario_backups(scenario, json, bytes, created_at) VALUES($s,$j,$b,$c)",
            ("$s", scenario), ("$j", json), ("$b", json.Length), ("$c", Now()));
        return Convert.ToInt64(QueryRow("SELECT last_insert_rowid() AS id")?["id"] ?? 0);
    }

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

    /// <summary>删除副本的目录行与内容行（saves + save_docs），返回受影响行数。
    /// ops / instances 表作为审计历史保留，不随副本删除。</summary>
    public int DeleteSaveRows(string instance) =>
        ExecCmd("DELETE FROM saves WHERE instance=$i", ("$i", instance)) +
        ExecCmd("DELETE FROM save_docs WHERE instance=$i", ("$i", instance));

    // ==================== 推演存档完整内容 ====================

    /// <summary>扫描 data/saves，把每个实例的全部文件（meta/init/snapshot_tN/turns）入库到 save_docs，
    /// 并回填 saves 目录表。幂等可重复调用（每次启动跑一次即可与磁盘对齐）。</summary>
    public int SyncSaveDocs(string saveDir)
    {
        if (!Directory.Exists(saveDir)) return 0;
        var n = 0;
        var instances = 0;
        foreach (var dir in Directory.GetDirectories(saveDir))
        {
            try
            {
                var files = SyncSaveInstance(saveDir, Path.GetFileName(dir));
                if (files > 0) { n += files; instances++; }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "副本内容同步跳过: {Dir}", dir); }
        }
        _logger.LogInformation("推演存档内容同步完成: {Files} 个文件（来自 {Instances} 个实例）", n, instances);
        return n;
    }

    /// <summary>单个实例目录 → save_docs + saves 目录表（幂等）。</summary>
    public int SyncSaveInstance(string saveDir, string instance)
    {
        var meta = StoreService.ReadMeta(instance, saveDir);
        if (meta == null) return 0;
        // 回填 saves 目录表（修复此前仅 live 写入、目录不全的缺口）
        UpsertSave(new StoreSaveMeta(meta.Instance, meta.Name, meta.Scenario, meta.Seed,
            meta.LastTurn, meta.MaxTurns, meta.CreatedAt.ToString("o"), meta.UpdatedAt.ToString("o"), "init"));
        var dir = Path.Combine(saveDir, instance);
        var n = 0;
        StoreSaveFileIf(dir, instance, "meta", null, "meta.json", meta.UpdatedAt, ref n);
        StoreSaveFileIf(dir, instance, "init", 0, "init.json", meta.UpdatedAt, ref n);
        if (Directory.Exists(dir))
            foreach (var f in Directory.GetFiles(dir, "snapshot_t*.json"))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (int.TryParse(name["snapshot_t".Length..], out var t))
                    StoreSaveFileIf(dir, instance, "snapshot", t, Path.GetFileName(f), meta.UpdatedAt, ref n);
            }
        StoreSaveFileIf(dir, instance, "turns", null, "turns.jsonl", meta.UpdatedAt, ref n);
        return n;
    }

    /// <summary>读一个磁盘文件并写入 save_docs（文件不存在则跳过）。供启动时批量回填与实时写入复用。</summary>
    public void StoreSaveFile(string saveDir, string instance, string kind, int? turn, string fileName, DateTime updatedAt)
    {
        var path = Path.Combine(saveDir, instance, fileName);
        if (!File.Exists(path)) return;
        try
        {
            var json = File.ReadAllText(path);
            StoreSaveDoc(instance, kind, turn, json, updatedAt.ToString("o"));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "副本内容入库失败 {Instance}/{File}", instance, fileName); }
    }

    /// <summary>低层写入：一条 save_docs（幂等）。</summary>
    public void StoreSaveDoc(string instance, string kind, int? turn, string docJson, string? updatedAt)
    {
        ExecCmd("INSERT OR REPLACE INTO save_docs(instance, kind, turn, doc_json, bytes, updated_at) VALUES($i,$k,$t,$d,$b,$u)",
            ("$i", instance), ("$k", kind), ("$t", turn.HasValue ? turn.Value : DBNull.Value),
            ("$d", docJson), ("$b", docJson.Length), ("$u", (object?)updatedAt ?? DBNull.Value));
    }

    private void StoreSaveFileIf(string dir, string instance, string kind, int? turn, string fileName, DateTime updatedAt, ref int n)
    {
        var path = Path.Combine(dir, fileName);
        if (!File.Exists(path)) return;
        try
        {
            var json = File.ReadAllText(path);
            StoreSaveDoc(instance, kind, turn, json, updatedAt.ToString("o"));
            n++;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "副本内容入库失败 {Instance}/{File}", instance, fileName); }
    }

    /// <summary>某实例所有已入库内容文件清单（kind/turn/bytes/updated_at）。</summary>
    public List<Dictionary<string, object?>> ListSaveDocs(string instance) =>
        QueryList("SELECT kind, turn, bytes, updated_at FROM save_docs WHERE instance=$i ORDER BY kind, turn", ("$i", instance));

    /// <summary>取某个存档文件原文（meta/init/turns 传 turn=null；snapshot 传具体回合号）。</summary>
    public string? GetSaveDoc(string instance, string kind, int? turn = null)
    {
        if (turn.HasValue)
            return (string?)QueryRow("SELECT doc_json FROM save_docs WHERE instance=$i AND kind=$k AND turn=$t",
                ("$i", instance), ("$k", kind), ("$t", turn.Value))?["doc_json"];
        return (string?)QueryRow("SELECT doc_json FROM save_docs WHERE instance=$i AND kind=$k AND turn IS NULL",
            ("$i", instance), ("$k", kind))?["doc_json"];
    }

    /// <summary>副本目录汇总：saves 表为真源，snapshot_turn/turns_recorded/description 从 save_docs 派生。</summary>
    public List<Dictionary<string, object?>> ListSaveSummaries(int limit = 500)
    {
        var snapTurn = new Dictionary<string, object?>();
        foreach (var r in QueryList("SELECT instance, MAX(turn) AS t FROM save_docs WHERE kind='snapshot' GROUP BY instance"))
            snapTurn[(string)r["instance"]!] = r["t"];

        var turnCounts = new Dictionary<string, int>();
        foreach (var r in QueryList("SELECT instance, doc_json FROM save_docs WHERE kind='turns'"))
        {
            var cnt = 0;
            foreach (var line in ((string?)r["doc_json"] ?? "").Split('\n'))
                if (!string.IsNullOrWhiteSpace(line)) cnt++;
            turnCounts[(string)r["instance"]!] = cnt;
        }

        var descriptions = new Dictionary<string, string?>();
        var scenarioNames = new Dictionary<string, string>();
        foreach (var r in QueryList("SELECT instance, doc_json FROM save_docs WHERE kind='meta'"))
        {
            try
            {
                using var doc = JsonDocument.Parse((string)r["doc_json"]!);
                var d = doc.RootElement;
                descriptions[(string)r["instance"]!] =
                    d.TryGetProperty("description", out var de) && de.ValueKind == JsonValueKind.String ? de.GetString() : null;
                if (d.TryGetProperty("scenario", out var sc) && sc.ValueKind == JsonValueKind.String &&
                    d.TryGetProperty("scenario_name", out var scn) && scn.ValueKind == JsonValueKind.String)
                    scenarioNames[sc.GetString()!] = scn.GetString() ?? "";
            }
            catch { /* 坏 meta 不影响其余 */ }
        }

        var outList = new List<Dictionary<string, object?>>();
        foreach (var s in QueryList("SELECT * FROM saves ORDER BY updated_at DESC LIMIT $lim", ("$lim", limit)))
        {
            var inst = (string)s["instance"]!;
            var scId = s["scenario"] as string;
            outList.Add(new Dictionary<string, object?>
            {
                ["instance"] = inst,
                ["name"] = s["name"],
                ["scenario"] = scId,
                ["scenario_name"] = scId != null && scenarioNames.TryGetValue(scId, out var sn) ? sn : null,
                ["last_turn"] = s["last_turn"],
                ["max_turns"] = s["max_turns"],
                ["seed"] = s["seed"],
                ["created_at"] = s["created_at"],
                ["updated_at"] = s["updated_at"],
                ["source"] = s.TryGetValue("source", out var so) ? so : null,
                ["turns_recorded"] = turnCounts.TryGetValue(inst, out var tc) ? tc : 0,
                ["snapshot_turn"] = snapTurn.TryGetValue(inst, out var st) ? st : 0,
                ["description"] = descriptions.TryGetValue(inst, out var de) ? de : null,
            });
        }
        return outList;
    }

    /// <summary>从 save_docs 组装载入结果（最新快照 + 重放尾部回合）；库中无该副本内容时返回 null。</summary>
    public StoreService.LoadOutcome? LoadSaveOutcome(string instance)
    {
        var init = GetSaveDoc(instance, "init", 0);
        var snapshots = new List<(int Turn, string Json)>();
        foreach (var r in QueryList("SELECT turn, doc_json FROM save_docs WHERE instance=$i AND kind='snapshot' ORDER BY turn",
                     ("$i", instance)))
            snapshots.Add((Convert.ToInt32(r["turn"]), (string)r["doc_json"]!));
        if (init == null && snapshots.Count == 0) return null;

        var turns = GetSaveDoc(instance, "turns");
        var meta = GetSaveDoc(instance, "meta");
        return StoreService.LoadFromDocs(new StoreService.SaveDocSet(meta, init, snapshots, turns), instance);
    }

    // ==================== 副本单位位置编辑（副本管理页） ====================

    /// <summary>一条位置修改：单位 id → 目标格（q,r）。</summary>
    public sealed record SaveUnitEdit(string Unit, int Q, int R);

    /// <summary>位置编辑结果：updated 生效数；unknown 库中不存在的单位 id；invalid 落点在 cells 之外的（单位→目标键）。</summary>
    public sealed record SaveUnitsEditResult(string BaseKind, int BaseTurn, int Updated, List<string> Unknown, List<string> Invalid);

    /// <summary>
    /// 副本载入基准文档：最新快照，无快照回退开局 init —— 与 StoreService.LoadFromDocs 的基准选择同口径，
    /// 保证这里改的位置在下次「载入并导调」时必然生效。
    /// </summary>
    private (string Kind, int Turn, string Json)? BaseSaveDoc(string instance)
    {
        var snap = QueryRow("SELECT turn, doc_json FROM save_docs WHERE instance=$i AND kind='snapshot' ORDER BY turn DESC LIMIT 1",
            ("$i", instance));
        if (snap?["doc_json"] is string sj)
            return ("snapshot", Convert.ToInt32(snap["turn"] ?? 0), sj);
        var init = GetSaveDoc(instance, "init", 0);
        return init == null ? null : ("init", 0, init);
    }

    /// <summary>网格几何（hex(0,0) 中心墨卡托米 + 半径），副本单位标记地图展示用。</summary>
    public sealed record SaveGeo(double Ox, double Oy, double Radius);

    /// <summary>读取基准文档中的双方单位及当前位置。返回 (基准种类, 基准回合, 面宽, 面高, 网格几何, 挖除格, 单位列表)；副本无内容返回 null。</summary>
    public (string BaseKind, int BaseTurn, int Width, int Height, SaveGeo? Geo, List<string>? Holes, List<Dictionary<string, object?>> Units)? GetSaveUnits(string instance)
    {
        var b = BaseSaveDoc(instance);
        if (b == null) return null;
        using var doc = JsonDocument.Parse(b.Value.Json);
        var root = doc.RootElement;

        var units = new List<Dictionary<string, object?>>();
        if (root.TryGetProperty("units", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var u in arr.EnumerateArray())
            {
                var q = 0; var r = 0;
                if (u.TryGetProperty("pos", out var p) && p.ValueKind == JsonValueKind.Object)
                {
                    q = p.TryGetProperty("q", out var qe) ? qe.GetInt32() : 0;
                    r = p.TryGetProperty("r", out var re) ? re.GetInt32() : 0;
                }
                var personnel = u.TryGetProperty("personnel", out var pe) ? pe.GetInt32() : 0;
                units.Add(new Dictionary<string, object?>
                {
                    ["id"] = u.TryGetProperty("id", out var ie) ? ie.GetString() ?? "" : "",
                    ["side"] = u.TryGetProperty("side", out var se) ? se.GetString() ?? "" : "",
                    ["unit_type"] = u.TryGetProperty("unit_type", out var te) ? te.GetString() ?? "" : "",
                    ["is_hq"] = u.TryGetProperty("is_hq", out var he) && he.ValueKind == JsonValueKind.True,
                    ["personnel"] = personnel,
                    ["alive"] = personnel > 0,
                    ["q"] = q,
                    ["r"] = r,
                });
            }
        }

        // 面宽/面高：grid.width / grid.height（与 environment 的宽/高同语义）；
        // 网格几何：grid.ox / grid.oy / grid.radius（引擎 HexGrid 序列化字段，hex(0,0) 中心）
        var width = 0; var height = 0;
        SaveGeo? geo = null;
        if (root.TryGetProperty("grid", out var g) && g.ValueKind == JsonValueKind.Object)
        {
            width = g.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
            height = g.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
            if (g.TryGetProperty("ox", out var oxe) && g.TryGetProperty("oy", out var oye) && g.TryGetProperty("radius", out var rre))
            {
                var gox = oxe.GetDouble(); var goy = oye.GetDouble(); var grad = rre.GetDouble();
                if (grad > 0) geo = new SaveGeo(gox, goy, grad);
            }
        }

        // 挖除格 = 全格 − cells 有效格键（键与 Hex.Key 同格式 "q,r"），标记地图叠层用
        List<string>? holes = null;
        if (root.TryGetProperty("cells", out var cel) && cel.ValueKind == JsonValueKind.Object
            && width > 0 && height > 0)
        {
            var valid = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in cel.EnumerateObject()) valid.Add(p.Name);
            holes = new List<string>(Math.Max(0, width * height - valid.Count));
            for (var rr = 0; rr < height; rr++)
                for (var qq = 0; qq < width; qq++)
                {
                    var key = qq + "," + rr;
                    if (!valid.Contains(key)) holes.Add(key);
                }
        }
        return (b.Value.Kind, b.Value.Turn, width, height, geo, holes, units);
    }

    /// <summary>
    /// 修改基准文档中的单位位置并写回 save_docs（SQLite 真源；改动在下次载入时生效）。
    /// cells 非空时校验落点必须在有效格内（界外/挖除格拒绝）；返回 null 表示副本无内容。
    /// </summary>
    public SaveUnitsEditResult? UpdateSaveUnitPositions(string instance, IEnumerable<SaveUnitEdit> positions)
    {
        var b = BaseSaveDoc(instance);
        if (b == null) return null;

        var root = JsonNode.Parse(b.Value.Json) as JsonObject
            ?? throw new InvalidDataException($"副本 {instance} 基准文档不是 JSON 对象");
        var unknown = new List<string>();
        var invalid = new List<string>();

        // 有效格键集合（cells 非空才校验；键与 Hex.Key 同格式 "q,r"）
        HashSet<string>? cells = null;
        if (root["cells"] is JsonObject co && co.Count > 0)
            cells = new HashSet<string>(co.Select(kv => kv.Key), StringComparer.Ordinal);

        var byId = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (root["units"] is JsonArray ua)
            foreach (var n in ua.OfType<JsonObject>())
            {
                var id = n["id"]?.ToString();
                if (!string.IsNullOrEmpty(id)) byId[id] = n;
            }

        var updated = 0;
        foreach (var p in positions)
        {
            if (!byId.TryGetValue(p.Unit, out var u)) { unknown.Add(p.Unit); continue; }
            var key = p.Q + "," + p.R;
            if (cells != null && !cells.Contains(key)) { invalid.Add($"{p.Unit}→{key}"); continue; }
            u["pos"] = new JsonObject { ["q"] = p.Q, ["r"] = p.R };
            updated++;
        }

        if (updated > 0)
        {
            var kind = b.Value.Kind;
            StoreSaveDoc(instance, kind, kind == "snapshot" ? b.Value.Turn : 0,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
                DateTime.UtcNow.ToString("o"));
        }
        return new SaveUnitsEditResult(b.Value.Kind, b.Value.Turn, updated, unknown, invalid);
    }

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
        var oldPath = Path.Combine(UtilsPath.ServerRoot(), "data", "ops_log.db");
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
        var oldPath = Path.Combine(UtilsPath.ServerRoot(), "data", "scenario_feedback.json");
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
