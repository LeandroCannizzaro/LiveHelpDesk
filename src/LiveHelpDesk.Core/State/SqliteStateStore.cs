using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiveHelpDesk.Abstractions;
using LiveHelpDesk.Core.Outbox;
using Microsoft.Data.Sqlite;

namespace LiveHelpDesk.Core.State;

/// <summary>SQLite-backed <see cref="ITicketStateStore"/>. Not thread-safe: the pipeline owns it from one thread.</summary>
public sealed class SqliteStateStore : ITicketStateStore, IOutbox, IDisposable
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
        """
        CREATE TABLE outbox (
            id                INTEGER PRIMARY KEY AUTOINCREMENT,
            event_key         TEXT NOT NULL,
            rule_id           TEXT NOT NULL,
            channel           TEXT NOT NULL,
            json              TEXT NOT NULL,
            not_before        TEXT NOT NULL,
            requires_unacked  INTEGER NOT NULL,
            status            TEXT NOT NULL,
            attempts          INTEGER NOT NULL DEFAULT 0,
            last_error        TEXT NULL,
            created_at        TEXT NOT NULL,
            updated_at        TEXT NOT NULL,
            UNIQUE (event_key, rule_id, channel)
        );
        CREATE INDEX ix_outbox_due ON outbox(status, not_before);
        """,
        """
        CREATE INDEX ix_events_ticket ON events(ticket_id, occurred_at);
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

    public IReadOnlyList<TicketEvent> EventsForTicket(Guid ticketId, DateTimeOffset from, DateTimeOffset to)
    {
        using var cmd = Command(
            "SELECT json FROM events WHERE ticket_id = $ticket AND occurred_at >= $from AND occurred_at <= $to ORDER BY seq;",
            ("$ticket", ticketId.ToString("D")),
            ("$from", Format(from)),
            ("$to", Format(to)));
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

    public void RemoveTicket(Guid id) => Execute("DELETE FROM tickets WHERE id = $id;", ("$id", id.ToString("D")));

    public void AcknowledgeEvent(string dedupKey, DateTimeOffset at) =>
        Execute(
            "UPDATE events SET acknowledged_at = COALESCE(acknowledged_at, $at) WHERE dedup_key = $key;",
            ("$key", dedupKey),
            ("$at", Format(at)));

    public bool IsEventAcknowledged(string dedupKey) =>
        Scalar("SELECT acknowledged_at FROM events WHERE dedup_key = $key;", ("$key", dedupKey)) is string;

    public bool TryAdd(OutboxItem item) =>
        Execute(
            """
            INSERT OR IGNORE INTO outbox(event_key, rule_id, channel, json, not_before, requires_unacked, status, attempts, created_at, updated_at)
            VALUES ($event, $rule, $channel, $json, $notBefore, $unacked, $status, $attempts, $created, $created);
            """,
            ("$event", item.EventKey),
            ("$rule", item.RuleId),
            ("$channel", item.Channel),
            ("$json", JsonSerializer.Serialize(item.Notification, Json)),
            ("$notBefore", Format(item.NotBefore)),
            ("$unacked", item.RequiresUnacknowledged ? 1 : 0),
            ("$status", item.Status.ToString()),
            ("$attempts", item.Attempts),
            ("$created", Format(item.CreatedAt))) == 1;

    public IReadOnlyList<OutboxItem> Due(DateTimeOffset now, int max) => DueExcluding([], now, max);

    public IReadOnlyList<OutboxItem> DueExcluding(IReadOnlyCollection<string> excludedChannels, DateTimeOffset now, int max)
    {
        var excluded = excludedChannels.ToList();
        var notIn = excluded.Count == 0
            ? ""
            : " AND channel NOT IN (" + string.Join(",", excluded.Select((_, i) => $"$c{i}")) + ")";
        var args = new List<(string, object?)> { ("$now", Format(now)), ("$max", max) };
        args.AddRange(excluded.Select((c, i) => ($"$c{i}", (object?)c)));

        using var cmd = Command(
            "SELECT id, event_key, rule_id, channel, json, not_before, requires_unacked, status, attempts, created_at " +
            "FROM outbox WHERE status = 'Pending' AND not_before <= $now" + notIn + " ORDER BY id LIMIT $max;",
            [.. args]);
        using var reader = cmd.ExecuteReader();
        var items = new List<OutboxItem>();
        while (reader.Read())
        {
            items.Add(new OutboxItem(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                JsonSerializer.Deserialize<Notification>(reader.GetString(4), Json)!,
                ParseTime(reader.GetString(5)),
                reader.GetInt32(6) != 0,
                Enum.Parse<OutboxStatus>(reader.GetString(7)),
                reader.GetInt32(8),
                ParseTime(reader.GetString(9))));
        }

        return items;
    }

    public void MarkSent(long id, DateTimeOffset at) =>
        Execute(
            "UPDATE outbox SET status = 'Sent', attempts = attempts + 1, updated_at = $at WHERE id = $id;",
            ("$id", id),
            ("$at", Format(at)));

    public void MarkCancelled(long id, string reason, DateTimeOffset at) =>
        Execute(
            "UPDATE outbox SET status = 'Cancelled', last_error = $reason, updated_at = $at WHERE id = $id;",
            ("$id", id),
            ("$reason", reason),
            ("$at", Format(at)));

    public void MarkFailed(long id, string error, DateTimeOffset nextAttempt, bool dead) =>
        Execute(
            "UPDATE outbox SET status = $status, attempts = attempts + 1, last_error = $error, not_before = $next, updated_at = $next WHERE id = $id;",
            ("$id", id),
            ("$status", dead ? nameof(OutboxStatus.Dead) : nameof(OutboxStatus.Pending)),
            ("$error", error),
            ("$next", Format(nextAttempt)));

    public IReadOnlyDictionary<OutboxStatus, int> Counts()
    {
        using var cmd = Command("SELECT status, COUNT(*) FROM outbox GROUP BY status;");
        using var reader = cmd.ExecuteReader();
        var counts = new Dictionary<OutboxStatus, int>();
        while (reader.Read())
        {
            counts[Enum.Parse<OutboxStatus>(reader.GetString(0))] = reader.GetInt32(1);
        }

        return counts;
    }

    public IReadOnlyDictionary<string, int> PendingByChannel()
    {
        using var cmd = Command("SELECT channel, COUNT(*) FROM outbox WHERE status = 'Pending' GROUP BY channel;");
        using var reader = cmd.ExecuteReader();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            counts[reader.GetString(0)] = reader.GetInt32(1);
        }

        return counts;
    }

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
