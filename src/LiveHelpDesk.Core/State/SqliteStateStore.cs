using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiveHelpDesk.Abstractions;
using Microsoft.Data.Sqlite;

namespace LiveHelpDesk.Core.State;

/// <summary>SQLite-backed <see cref="ITicketStateStore"/>. Not thread-safe: the pipeline owns it from one thread.</summary>
public sealed class SqliteStateStore : ITicketStateStore, IDisposable
{
    /// <summary>Forward-only migrations; the schema version is <c>PRAGMA user_version</c>.</summary>
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE tickets (
            id            TEXT PRIMARY KEY,
            json          TEXT NOT NULL,
            state         INTEGER NOT NULL,
            last_inbound  TEXT NULL,
            last_outbound TEXT NULL
        );
        CREATE INDEX ix_tickets_state ON tickets(state);
        CREATE TABLE messages (
            id         TEXT PRIMARY KEY,
            ticket_id  TEXT NOT NULL,
            created_on TEXT NOT NULL
        );
        CREATE TABLE events (
            seq             INTEGER PRIMARY KEY AUTOINCREMENT,
            dedup_key       TEXT NOT NULL UNIQUE,
            type            TEXT NOT NULL,
            ticket_id       TEXT NULL,
            occurred_at     TEXT NOT NULL,
            json            TEXT NOT NULL,
            acknowledged_at TEXT NULL
        );
        CREATE TABLE kv (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """,
    ];

    private const string BaselineKey = "baseline_at";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SqliteConnection _db;
    private SqliteTransaction? _transaction;

    /// <param name="path">Database file, or <c>:memory:</c> for a throw-away store (tests).</param>
    public SqliteStateStore(string path)
    {
        if (path != ":memory:")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        }

        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Execute("PRAGMA journal_mode = WAL;");
        Migrate();
    }

    public int SchemaVersion => Convert.ToInt32(Scalar("PRAGMA user_version;"), CultureInfo.InvariantCulture);

    public DateTimeOffset? BaselineAt => GetValue(BaselineKey) is { } text ? ParseTime(text) : null;

    public void InTransaction(Action work)
    {
        if (_transaction is not null)
        {
            work();
            return;
        }

        _transaction = _db.BeginTransaction();
        try
        {
            work();
            _transaction.Commit();
        }
        catch
        {
            _transaction.Rollback();
            throw;
        }
        finally
        {
            _transaction.Dispose();
            _transaction = null;
        }
    }

    public TicketSnapshot? GetTicket(Guid id) =>
        Scalar("SELECT json FROM tickets WHERE id = $id;", ("$id", id.ToString("D"))) is string json
            ? JsonSerializer.Deserialize<TicketSnapshot>(json, Json)
            : null;

    public void SaveTicket(TicketSnapshot ticket) =>
        Execute(
            """
            INSERT INTO tickets(id, json, state) VALUES ($id, $json, $state)
            ON CONFLICT(id) DO UPDATE SET json = excluded.json, state = excluded.state;
            """,
            ("$id", ticket.Id.ToString("D")),
            ("$json", JsonSerializer.Serialize(ticket, Json)),
            ("$state", ticket.State));

    public void SaveBaseline(IEnumerable<TicketSnapshot> tickets, DateTimeOffset at) =>
        InTransaction(() =>
        {
            Execute("DELETE FROM tickets;");
            foreach (var ticket in tickets)
            {
                SaveTicket(ticket);
            }

            SetValue(BaselineKey, Format(at));
        });

    public IReadOnlyList<TicketSnapshot> TicketsInState(int state)
    {
        using var cmd = Command("SELECT json FROM tickets WHERE state = $state;", ("$state", state));
        using var reader = cmd.ExecuteReader();
        var result = new List<TicketSnapshot>();
        while (reader.Read())
        {
            result.Add(JsonSerializer.Deserialize<TicketSnapshot>(reader.GetString(0), Json)!);
        }

        return result;
    }

    public bool RecordMessage(MessageSnapshot message)
    {
        var inserted = Execute(
            "INSERT OR IGNORE INTO messages(id, ticket_id, created_on) VALUES ($id, $ticket, $at);",
            ("$id", message.Id.ToString("D")),
            ("$ticket", message.TicketId.ToString("D")),
            ("$at", Format(message.CreatedOn)));
        if (inserted == 0)
        {
            return false;
        }

        var column = message.Incoming ? "last_inbound" : "last_outbound";
        Execute(
            $"UPDATE tickets SET {column} = CASE WHEN {column} IS NULL OR {column} < $at THEN $at ELSE {column} END WHERE id = $ticket;",
            ("$ticket", message.TicketId.ToString("D")),
            ("$at", Format(message.CreatedOn)));
        return true;
    }

    public TicketActivity GetActivity(Guid ticketId)
    {
        using var cmd = Command("SELECT last_inbound, last_outbound FROM tickets WHERE id = $id;", ("$id", ticketId.ToString("D")));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return new TicketActivity(null, null);
        }

        return new TicketActivity(
            reader.IsDBNull(0) ? null : ParseTime(reader.GetString(0)),
            reader.IsDBNull(1) ? null : ParseTime(reader.GetString(1)));
    }

    public bool TryRegisterEvent(TicketEvent evt) =>
        Execute(
            "INSERT OR IGNORE INTO events(dedup_key, type, ticket_id, occurred_at, json) VALUES ($key, $type, $ticket, $at, $json);",
            ("$key", evt.DedupKey),
            ("$type", evt.Type.ToString()),
            ("$ticket", evt.TicketId?.ToString("D")),
            ("$at", Format(evt.OccurredAt)),
            ("$json", JsonSerializer.Serialize(evt, Json))) == 1;

    public IReadOnlyList<TicketEvent> RecentEvents(int count)
    {
        using var cmd = Command("SELECT json FROM events ORDER BY seq DESC LIMIT $n;", ("$n", count));
        using var reader = cmd.ExecuteReader();
        var result = new List<TicketEvent>();
        while (reader.Read())
        {
            result.Add(JsonSerializer.Deserialize<TicketEvent>(reader.GetString(0), Json)!);
        }

        return result;
    }

    public string? GetValue(string key) => Scalar("SELECT value FROM kv WHERE key = $key;", ("$key", key)) as string;

    public void SetValue(string key, string value) =>
        Execute(
            "INSERT INTO kv(key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
            ("$key", key),
            ("$value", value));

    public void Dispose() => _db.Dispose();

    private void Migrate()
    {
        for (var version = SchemaVersion; version < Migrations.Length; version++)
        {
            var script = Migrations[version];
            var next = version + 1;
            InTransaction(() =>
            {
                Execute(script);
                Execute($"PRAGMA user_version = {next};");
            });
        }
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] args)
    {
        var cmd = _db.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return cmd;
    }

    private int Execute(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(sql, args);
        return cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(sql, args);
        var value = cmd.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    // Fixed-length UTC text sorts like the time it represents, so SQL can compare it directly.
    private static string Format(DateTimeOffset time) => time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
