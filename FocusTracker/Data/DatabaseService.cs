using Microsoft.Data.Sqlite;
using System.IO;
using FocusTracker.Models;

namespace FocusTracker.Data;

public class DatabaseService : IDisposable
{
    private readonly string _connectionString;
    private SqliteConnection? _connection;
    private readonly object _dbLock = new();

    public DatabaseService()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FocusTracker");
        Directory.CreateDirectory(dir);
        _connectionString = $"Data Source={Path.Combine(dir, "focustracker.db")}";
        InitializeDatabase();
    }

    private SqliteConnection GetConnection()
    {
        lock (_dbLock)
        {
            if (_connection == null || _connection.State != System.Data.ConnectionState.Open)
            {
                _connection = new SqliteConnection(_connectionString);
                _connection.Open();
                using var p = _connection.CreateCommand();
                p.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
                p.ExecuteNonQuery();
            }
            return _connection;
        }
    }

    private void InitializeDatabase()
    {
        lock (_dbLock)
        {
            var conn = GetConnection();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS Projects (
                        Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                        Name        TEXT NOT NULL,
                        TrackedKeys TEXT NOT NULL DEFAULT '',
                        TotalTimeAlarmSeconds   INTEGER,
                        SessionTimeAlarmSeconds INTEGER
                    );
                    CREATE TABLE IF NOT EXISTS Sessions (
                        Id               INTEGER PRIMARY KEY AUTOINCREMENT,
                        Name             TEXT NOT NULL DEFAULT '',
                        StartTime        TEXT NOT NULL,
                        EndTime          TEXT,
                        TrackedProcesses TEXT NOT NULL,
                        ProjectId        INTEGER REFERENCES Projects(Id)
                    );
                    CREATE TABLE IF NOT EXISTS FocusEvents (
                        Id             INTEGER PRIMARY KEY AUTOINCREMENT,
                        SessionId      INTEGER NOT NULL,
                        ProcessName    TEXT NOT NULL,
                        AppDisplayName TEXT NOT NULL,
                        StartTime      TEXT NOT NULL,
                        EndTime        TEXT NOT NULL,
                        IsUrl          INTEGER NOT NULL DEFAULT 0,
                        IsUnfocus      INTEGER NOT NULL DEFAULT 0,
                        FOREIGN KEY (SessionId) REFERENCES Sessions(Id)
                    );
                    CREATE INDEX IF NOT EXISTS idx_fe_process  ON FocusEvents(ProcessName);
                    CREATE INDEX IF NOT EXISTS idx_fe_start    ON FocusEvents(StartTime);
                    CREATE INDEX IF NOT EXISTS idx_fe_session  ON FocusEvents(SessionId);";
                cmd.ExecuteNonQuery();
            }
        }
        // Migrations for existing DBs
        SafeAlter("Sessions",     "Name",      "TEXT NOT NULL DEFAULT ''");
        SafeAlter("Sessions",     "ProjectId", "INTEGER");
        SafeAlter("FocusEvents",  "IsUrl",     "INTEGER NOT NULL DEFAULT 0");
        SafeAlter("FocusEvents",  "IsUnfocus", "INTEGER NOT NULL DEFAULT 0");
        SafeAlter("Projects",     "TotalTimeAlarmSeconds",   "INTEGER");
        SafeAlter("Projects",     "SessionTimeAlarmSeconds", "INTEGER");
    }

    private void SafeAlter(string table, string column, string definition)
    {
        lock (_dbLock)
        {
            var conn = GetConnection();
            bool exists = false;
            using (var p = conn.CreateCommand())
            {
                p.CommandText = $"PRAGMA table_info({table})";
                using var r = p.ExecuteReader();
                while (r.Read())
                    if (r.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase))
                    { exists = true; break; }
            }
            if (!exists)
            {
                using var a = conn.CreateCommand();
                a.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
                a.ExecuteNonQuery();
            }
        }
    }

    // ── Projects ──────────────────────────────────────────────────────────
    public List<Project> GetAllProjects()
    {
        lock (_dbLock)
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, Name, TrackedKeys, TotalTimeAlarmSeconds, SessionTimeAlarmSeconds FROM Projects ORDER BY Name";
            var list = new List<Project>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new Project 
                { 
                    Id = r.GetInt32(0), 
                    Name = r.GetString(1), 
                    TrackedKeys = r.GetString(2),
                    TotalTimeAlarmSeconds = r.IsDBNull(3) ? null : r.GetInt32(3),
                    SessionTimeAlarmSeconds = r.IsDBNull(4) ? null : r.GetInt32(4)
                });
            return list;
        }
    }

    public int CreateProject(string name, string trackedKeys, int? totalAlarm = null, int? sessionAlarm = null)
    {
        lock (_dbLock)
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO Projects (Name, TrackedKeys, TotalTimeAlarmSeconds, SessionTimeAlarmSeconds) VALUES ($n, $k, $t, $s); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$k", trackedKeys);
            cmd.Parameters.AddWithValue("$t", (object?)totalAlarm   ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$s", (object?)sessionAlarm ?? DBNull.Value);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public void UpdateProject(int id, string name, string trackedKeys, int? totalAlarm = null, int? sessionAlarm = null)
    {
        lock (_dbLock)
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Projects SET Name=$n, TrackedKeys=$k, TotalTimeAlarmSeconds=$t, SessionTimeAlarmSeconds=$s WHERE Id=$id";
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$k", trackedKeys);
            cmd.Parameters.AddWithValue("$t", (object?)totalAlarm   ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$s", (object?)sessionAlarm ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteProject(int id)
    {
        lock (_dbLock)
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            // Unlink sessions from this project
            cmd.CommandText = "UPDATE Sessions SET ProjectId=NULL WHERE ProjectId=$id; DELETE FROM Projects WHERE Id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    // ── Sessions ──────────────────────────────────────────────────────────
    public int CreateSession(DateTime startTime, IEnumerable<string> tracked, string name = "", int? projectId = null)
    {
        lock (_dbLock)
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO Sessions (Name, StartTime, TrackedProcesses, ProjectId)
                                VALUES ($name, $start, $procs, $pid); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$name",  name);
            cmd.Parameters.AddWithValue("$start", startTime.ToString("O"));
            cmd.Parameters.AddWithValue("$procs", string.Join(",", tracked));
            cmd.Parameters.AddWithValue("$pid",   (object?)projectId ?? DBNull.Value);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public void CloseSession(int sessionId, DateTime endTime)
    {
        lock (_dbLock)
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Sessions SET EndTime=$end WHERE Id=$id";
            cmd.Parameters.AddWithValue("$end", endTime.ToString("O"));
            cmd.Parameters.AddWithValue("$id",  sessionId);
            cmd.ExecuteNonQuery();
        }
    }

    public void UpdateSessionName(int sessionId, string name)
    {
        lock (_dbLock)
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Sessions SET Name=$name WHERE Id=$id";
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$id",   sessionId);
            cmd.ExecuteNonQuery();
        }
    }

    public void UpdateSessionProject(int sessionId, int? projectId)
    {
        lock (_dbLock)
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Sessions SET ProjectId=$pid WHERE Id=$id";
            cmd.Parameters.AddWithValue("$pid", (object?)projectId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id",  sessionId);
            cmd.ExecuteNonQuery();
        }
    }

    public List<TrackingSession> GetAllSessions()
    {
        lock (_dbLock)
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, Name, StartTime, EndTime, TrackedProcesses, ProjectId FROM Sessions ORDER BY StartTime DESC";
            var list = new List<TrackingSession>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new TrackingSession
                {
                    Id               = r.GetInt32(0),
                    Name             = r.IsDBNull(1) ? "" : r.GetString(1),
                    StartTime        = DateTime.Parse(r.GetString(2)),
                    EndTime          = r.IsDBNull(3) ? null : DateTime.Parse(r.GetString(3)),
                    TrackedProcesses = r.GetString(4),
                    ProjectId        = r.IsDBNull(5) ? null : r.GetInt32(5)
                });
            return list;
        }
    }

    public void DeleteSession(int sessionId)
    {
        lock (_dbLock)
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM FocusEvents WHERE SessionId=$sid; DELETE FROM Sessions WHERE Id=$sid;";
            cmd.Parameters.AddWithValue("$sid", sessionId);
            cmd.ExecuteNonQuery();
        }
    }

    // ── Focus Events ──────────────────────────────────────────────────────
    public void InsertFocusEvent(int sessionId, string processName, string displayName,
                                 DateTime start, DateTime end, bool isUrl = false, bool isUnfocus = false)
    {
        lock (_dbLock)
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO FocusEvents
                (SessionId, ProcessName, AppDisplayName, StartTime, EndTime, IsUrl, IsUnfocus)
                VALUES ($sid, $proc, $disp, $start, $end, $url, $unfocus)";
            cmd.Parameters.AddWithValue("$sid",     sessionId);
            cmd.Parameters.AddWithValue("$proc",    processName);
            cmd.Parameters.AddWithValue("$disp",    displayName);
            cmd.Parameters.AddWithValue("$start",   start.ToString("O"));
            cmd.Parameters.AddWithValue("$end",     end.ToString("O"));
            cmd.Parameters.AddWithValue("$url",     isUrl ? 1 : 0);
            cmd.Parameters.AddWithValue("$unfocus", isUnfocus ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
    }

    // ── Usage summaries ───────────────────────────────────────────────────
    // Returns one AppUsageSummary per ProcessName with FocusSeconds + UnfocusSeconds merged
    public List<AppUsageSummary> GetUsageSummaries(DateTime from, DateTime to, int? projectId = null)
    {
        lock (_dbLock)
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            var projectJoin = projectId.HasValue
                ? "INNER JOIN Sessions s ON fe.SessionId = s.Id AND s.ProjectId = $pid"
                : "INNER JOIN Sessions s ON fe.SessionId = s.Id";
            cmd.CommandText = $@"
                SELECT fe.ProcessName, fe.AppDisplayName,
                       SUM(ROUND((julianday(fe.EndTime) - julianday(fe.StartTime)) * 86400)),
                       COUNT(DISTINCT fe.SessionId),
                       MAX(fe.IsUrl),
                       fe.IsUnfocus
                FROM FocusEvents fe
                {projectJoin}
                WHERE fe.StartTime >= $from AND fe.EndTime <= $to
                GROUP BY fe.ProcessName, fe.IsUnfocus
                ORDER BY fe.ProcessName, fe.IsUnfocus";
            cmd.Parameters.AddWithValue("$from", from.ToString("O"));
            cmd.Parameters.AddWithValue("$to",   to.ToString("O"));
            if (projectId.HasValue) cmd.Parameters.AddWithValue("$pid", projectId.Value);

            // Read raw rows then merge focus+unfocus per process
            var raw = new List<(string proc, string disp, long secs, int sessions, bool isUrl, bool isUnfocus)>();
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    raw.Add((r.GetString(0), r.GetString(1),
                        r.IsDBNull(2) ? 0 : r.GetInt64(2),
                        r.GetInt32(3), r.GetInt32(4) == 1, r.GetInt32(5) == 1));

            return MergeFocusUnfocus(raw);
        }
    }

    private static List<AppUsageSummary> MergeFocusUnfocus(
        IEnumerable<(string proc, string disp, long secs, int sessions, bool isUrl, bool isUnfocus)> raw)
    {
        var dict = new Dictionary<string, AppUsageSummary>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in raw)
        {
            if (!dict.TryGetValue(row.proc, out var summary))
            {
                summary = new AppUsageSummary
                {
                    ProcessName    = row.proc,
                    AppDisplayName = row.disp,
                    EventCount     = row.sessions,
                    IsUrl          = row.isUrl
                };
                dict[row.proc] = summary;
            }
            if (row.isUnfocus)
                summary.UnfocusSeconds = row.secs;
            else
            {
                summary.FocusSeconds = row.secs;
                summary.EventCount   = row.sessions;
            }
        }
        return dict.Values.OrderByDescending(x => x.FocusSeconds + x.UnfocusSeconds).ToList();
    }

    // Project stats: separate project keys vs extras
    public (List<AppUsageSummary> main, List<AppUsageSummary> extras)
        GetProjectSummaries(Project project, DateTime from, DateTime to)
    {
        var all    = GetUsageSummaries(from, to, project.Id);
        var keys   = new HashSet<string>(project.TrackedKeys.Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
        var main   = all.Where(x => keys.Contains(x.ProcessName)).ToList();
        var extras = all.Where(x => !keys.Contains(x.ProcessName)).Select(x => { x.IsProjectExtra = true; return x; }).ToList();
        return (main, extras);
    }

    public List<AppUsageSummary> GetSessionDetail(int sessionId)
    {
        lock (_dbLock)
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT ProcessName, AppDisplayName,
                       SUM(ROUND((julianday(EndTime)-julianday(StartTime))*86400)),
                       COUNT(*), MAX(IsUrl), IsUnfocus
                FROM FocusEvents WHERE SessionId=$sid
                GROUP BY ProcessName, IsUnfocus
                ORDER BY ProcessName, IsUnfocus";
            cmd.Parameters.AddWithValue("$sid", sessionId);

            var raw = new List<(string proc, string disp, long secs, int sessions, bool isUrl, bool isUnfocus)>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                raw.Add((r.GetString(0), r.GetString(1),
                    r.IsDBNull(2) ? 0 : r.GetInt64(2),
                    r.GetInt32(3), r.GetInt32(4) == 1, r.GetInt32(5) == 1));
            return MergeFocusUnfocus(raw);
        }
    }

    public void DeleteEventsByProcess(string processName)
    {
        lock (_dbLock)
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM FocusEvents WHERE ProcessName=$proc";
            cmd.Parameters.AddWithValue("$proc", processName);
            cmd.ExecuteNonQuery();
        }
    }

    public void ResetDatabase()
    {
        lock (_dbLock)
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                DELETE FROM FocusEvents;
                DELETE FROM Sessions;
                DELETE FROM Projects;
                DELETE FROM sqlite_sequence WHERE name IN ('FocusEvents', 'Sessions', 'Projects');";
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose() => _connection?.Dispose();
}
