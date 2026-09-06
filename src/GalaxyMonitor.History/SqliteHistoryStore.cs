using GalaxyMonitor.Core;
using Microsoft.Data.Sqlite;

namespace GalaxyMonitor.History;

public sealed class SqliteHistoryStore(string databasePath) : IHistoryStore
{
    private readonly SqliteConnection _connection = new($"Data Source={databasePath};Mode=ReadWriteCreate;Cache=Shared");

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            CREATE TABLE IF NOT EXISTS samples (
                captured_at INTEGER NOT NULL,
                metric_id TEXT NOT NULL,
                value REAL NOT NULL,
                PRIMARY KEY (captured_at, metric_id)
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS ix_samples_metric_time ON samples(metric_id, captured_at);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask AppendAsync(MonitorSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        using var transaction = _connection.BeginTransaction();
        foreach (var metric in snapshot.Metrics.Where(x => x.Status == MetricStatus.Available && x.Value.HasValue))
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT OR REPLACE INTO samples(captured_at, metric_id, value) VALUES($time, $id, $value)";
            command.Parameters.AddWithValue("$time", snapshot.CapturedAt.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$id", metric.Id);
            command.Parameters.AddWithValue("$value", metric.Value!.Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        transaction.Commit();
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync().ConfigureAwait(false);
}
