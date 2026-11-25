using System.Globalization;
using Microsoft.Data.Sqlite;

namespace BenchPortal.Api.Data;

/// <summary>SQLite storage of benches, configurations, tests, runs, measurements and release notes (plain SQL).</summary>
public sealed class Database
{
    private readonly string _connectionString;

    public Database(string connectionString)
    {
        _connectionString = connectionString;
        using var c = Open();
        Execute(c, Schema);
        Seed(c);
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS benches (
            id INTEGER PRIMARY KEY, code TEXT NOT NULL UNIQUE, name TEXT NOT NULL, description TEXT NOT NULL DEFAULT '');
        CREATE TABLE IF NOT EXISTS configurations (
            id INTEGER PRIMARY KEY, bench_id INTEGER NOT NULL REFERENCES benches(id), version TEXT NOT NULL,
            description TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'draft', created_at TEXT NOT NULL, deployed_at TEXT,
            UNIQUE (bench_id, version));
        CREATE TABLE IF NOT EXISTS tests (
            id INTEGER PRIMARY KEY, name TEXT NOT NULL, bench_id INTEGER NOT NULL REFERENCES benches(id),
            configuration_id INTEGER REFERENCES configurations(id), planned_date TEXT NOT NULL,
            speed_rpm INTEGER NOT NULL, load_nm INTEGER NOT NULL, duration_s INTEGER NOT NULL,
            status TEXT NOT NULL DEFAULT 'planned', notes TEXT NOT NULL DEFAULT '');
        CREATE TABLE IF NOT EXISTS runs (
            id INTEGER PRIMARY KEY, test_id INTEGER NOT NULL REFERENCES tests(id), started_at TEXT NOT NULL,
            ended_at TEXT, status TEXT NOT NULL, samples INTEGER NOT NULL DEFAULT 0, verdict TEXT, summary_json TEXT);
        CREATE TABLE IF NOT EXISTS measurements (
            run_id INTEGER NOT NULL REFERENCES runs(id), t_s REAL NOT NULL, signal TEXT NOT NULL, value REAL NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_measurements_run_signal ON measurements(run_id, signal, t_s);
        CREATE TABLE IF NOT EXISTS release_notes (
            id INTEGER PRIMARY KEY, version TEXT NOT NULL, date TEXT NOT NULL, component TEXT NOT NULL, notes TEXT NOT NULL);
        """;

    private static void Seed(SqliteConnection c)
    {
        if (Scalar<long>(c, "SELECT COUNT(*) FROM benches") > 0)
        {
            return;
        }
        var now = Now();
        Execute(c, """
            INSERT INTO benches (code, name, description) VALUES
              ('BANC-01', 'Engine bench 1 (simulated)', 'Dynamometer bench driven by bench_sim over Modbus TCP, CAN over UDP');
            INSERT INTO configurations (bench_id, version, description, status, created_at, deployed_at) VALUES
              (1, '1.0.0', 'Initial acquisition configuration: EngineStatus 100 Hz, temperatures and pressures 10 Hz', 'deployed', $now, $now);
            INSERT INTO release_notes (version, date, component, notes) VALUES
              ('1.0.0', $date, 'Acquisition', 'First release: CAN decoding from the shared signal database, Modbus control, run recording and limit checks.');
            """, ("$now", now), ("$date", now[..10]));
    }

    // ------------------------------------------------------------------ queries

    public IReadOnlyList<Bench> Benches() =>
        Query(Open(), "SELECT id, code, name, description FROM benches ORDER BY id",
            r => new Bench(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3)));

    public IReadOnlyList<BenchConfiguration> Configurations() =>
        Query(Open(), "SELECT id, bench_id, version, description, status, created_at, deployed_at FROM configurations ORDER BY id DESC",
            ReadConfiguration);

    public BenchConfiguration? Configuration(long id) =>
        Query(Open(), "SELECT id, bench_id, version, description, status, created_at, deployed_at FROM configurations WHERE id = $id",
            ReadConfiguration, ("$id", id)).FirstOrDefault();

    public BenchConfiguration AddConfiguration(NewConfiguration n)
    {
        using var c = Open();
        var id = Scalar<long>(c, """
            INSERT INTO configurations (bench_id, version, description, created_at) VALUES ($bench, $version, $description, $now)
            RETURNING id
            """, ("$bench", n.BenchId), ("$version", n.Version), ("$description", n.Description), ("$now", Now()));
        return Configuration(id)!;
    }

    /// <summary>Change the lifecycle status. Deploying a configuration retires the one previously deployed on the bench.</summary>
    public BenchConfiguration SetConfigurationStatus(long id, string status)
    {
        var current = Configuration(id) ?? throw new KeyNotFoundException($"Configuration {id} not found");
        if (!ConfigurationStatus.CanMove(current.Status, status))
        {
            throw new InvalidOperationException($"Cannot move a configuration from '{current.Status}' to '{status}'");
        }
        using var c = Open();
        using var tx = c.BeginTransaction();
        if (status == ConfigurationStatus.Deployed)
        {
            Execute(c, tx, "UPDATE configurations SET status = 'validated' WHERE bench_id = $bench AND status = 'deployed'",
                ("$bench", current.BenchId));
        }
        Execute(c, tx, "UPDATE configurations SET status = $status, deployed_at = CASE WHEN $status = 'deployed' THEN $now ELSE deployed_at END WHERE id = $id",
            ("$status", status), ("$now", Now()), ("$id", id));
        tx.Commit();
        return Configuration(id)!;
    }

    public IReadOnlyList<TestDefinition> Tests() =>
        Query(Open(), $"{TestSelect} ORDER BY planned_date, id", ReadTest);

    public TestDefinition? Test(long id) =>
        Query(Open(), $"{TestSelect} WHERE id = $id", ReadTest, ("$id", id)).FirstOrDefault();

    public TestDefinition AddTest(NewTest n)
    {
        using var c = Open();
        var id = Scalar<long>(c, """
            INSERT INTO tests (name, bench_id, configuration_id, planned_date, speed_rpm, load_nm, duration_s, notes)
            VALUES ($name, $bench, $config, $date, $speed, $load, $duration, $notes) RETURNING id
            """,
            ("$name", n.Name), ("$bench", n.BenchId), ("$config", (object?)n.ConfigurationId ?? DBNull.Value), ("$date", n.PlannedDate),
            ("$speed", n.SpeedRpm), ("$load", n.LoadNm), ("$duration", n.DurationS), ("$notes", n.Notes ?? ""));
        return Test(id)!;
    }

    public void SetTestStatus(long id, string status) =>
        Execute(Open(), "UPDATE tests SET status = $status WHERE id = $id", ("$status", status), ("$id", id));

    public TestRun StartRun(long testId)
    {
        using var c = Open();
        var id = Scalar<long>(c, "INSERT INTO runs (test_id, started_at, status) VALUES ($test, $now, 'running') RETURNING id",
            ("$test", testId), ("$now", Now()));
        return Run(id)!;
    }

    public void FinishRun(long runId, string status, string verdict, string summaryJson)
    {
        using var c = Open();
        Execute(c, """
            UPDATE runs SET status = $status, verdict = $verdict, summary_json = $summary, ended_at = $now,
                samples = (SELECT COUNT(DISTINCT t_s) FROM measurements WHERE run_id = $id)
            WHERE id = $id
            """, ("$status", status), ("$verdict", verdict), ("$summary", summaryJson), ("$now", Now()), ("$id", runId));
    }

    public IReadOnlyList<TestRun> Runs(long? testId = null) => testId is null
        ? Query(Open(), $"{RunSelect} ORDER BY id DESC", ReadRun)
        : Query(Open(), $"{RunSelect} WHERE test_id = $test ORDER BY id DESC", ReadRun, ("$test", testId.Value));

    public TestRun? Run(long id) => Query(Open(), $"{RunSelect} WHERE id = $id", ReadRun, ("$id", id)).FirstOrDefault();

    public void InsertMeasurements(long runId, IEnumerable<Measurement> rows)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO measurements (run_id, t_s, signal, value) VALUES ($run, $t, $signal, $value)";
        var run = cmd.Parameters.AddWithValue("$run", runId);
        var t = cmd.Parameters.Add("$t", SqliteType.Real);
        var signal = cmd.Parameters.Add("$signal", SqliteType.Text);
        var value = cmd.Parameters.Add("$value", SqliteType.Real);
        foreach (var m in rows)
        {
            t.Value = m.TimeS;
            signal.Value = m.Signal;
            value.Value = m.Value;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Measurements of one signal, decimated to at most maxPoints for display.</summary>
    public IReadOnlyList<Measurement> Measurements(long runId, string signal, int maxPoints = 2000)
    {
        using var c = Open();
        var count = Scalar<long>(c, "SELECT COUNT(*) FROM measurements WHERE run_id = $run AND signal = $signal",
            ("$run", runId), ("$signal", signal));
        var step = Math.Max(1, (int)Math.Ceiling(count / (double)Math.Max(1, maxPoints)));
        return Query(c, """
            SELECT t_s, signal, value FROM (
                SELECT t_s, signal, value, ROW_NUMBER() OVER (ORDER BY t_s) - 1 AS n
                FROM measurements WHERE run_id = $run AND signal = $signal)
            WHERE n % $step = 0 ORDER BY t_s
            """, r => new Measurement(r.GetDouble(0), r.GetString(1), r.GetDouble(2)),
            ("$run", runId), ("$signal", signal), ("$step", step));
    }

    public IReadOnlyList<ReleaseNote> ReleaseNotes() =>
        Query(Open(), "SELECT id, version, date, component, notes FROM release_notes ORDER BY id DESC",
            r => new ReleaseNote(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));

    public ReleaseNote AddReleaseNote(NewReleaseNote n)
    {
        using var c = Open();
        var id = Scalar<long>(c, "INSERT INTO release_notes (version, date, component, notes) VALUES ($v, $d, $c, $n) RETURNING id",
            ("$v", n.Version), ("$d", Now()[..10]), ("$c", n.Component), ("$n", n.Notes));
        return ReleaseNotes().First(r => r.Id == id);
    }

    // ------------------------------------------------------------------ helpers

    private const string TestSelect =
        "SELECT id, name, bench_id, configuration_id, planned_date, speed_rpm, load_nm, duration_s, status, notes FROM tests";

    private const string RunSelect =
        "SELECT id, test_id, started_at, ended_at, status, samples, verdict, summary_json FROM runs";

    private static BenchConfiguration ReadConfiguration(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6));

    private static TestDefinition ReadTest(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetInt64(3), r.GetString(4),
        r.GetInt32(5), r.GetInt32(6), r.GetInt32(7), r.GetString(8), r.GetString(9));

    private static TestRun ReadRun(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetInt32(5),
        r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7));

    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        Execute(c, "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;");
        return c;
    }

    private static SqliteCommand Command(SqliteConnection c, string sql, (string, object)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value);
        }
        return cmd;
    }

    private static void Execute(SqliteConnection c, string sql, params (string, object)[] args) => Execute(c, null, sql, args);

    private static void Execute(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object)[] args)
    {
        using var cmd = Command(c, sql, args);
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }

    private static T Scalar<T>(SqliteConnection c, string sql, params (string, object)[] args)
    {
        using var cmd = Command(c, sql, args);
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }

    private static List<T> Query<T>(SqliteConnection c, string sql, Func<SqliteDataReader, T> map, params (string, object)[] args)
    {
        using (c)
        using (var cmd = Command(c, sql, args))
        using (var r = cmd.ExecuteReader())
        {
            var list = new List<T>();
            while (r.Read())
            {
                list.Add(map(r));
            }
            return list;
        }
    }
}
