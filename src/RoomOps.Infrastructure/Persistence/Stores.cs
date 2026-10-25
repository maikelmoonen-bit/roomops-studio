using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RoomOps.Application.Abstractions;
using RoomOps.Application.Execution;
using RoomOps.Domain.Changes;
using RoomOps.Domain.Templates;

using RoomOps.Domain.Common;

namespace RoomOps.Infrastructure.Persistence;

/// <summary>Per-user data locations. Everything lives in the user's local profile; nothing is written next to the executable.</summary>
public sealed class AppPaths
{
    public AppPaths(string? overrideRoot = null)
    {
        Root = overrideRoot
            ?? Environment.GetEnvironmentVariable("ROOMOPS_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "RoomOps Studio");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
    }

    public string Root { get; }
    public string Database => Path.Combine(Root, "roomops.db");
    public string Settings => Path.Combine(Root, "settings.json");
    public string Logs => Path.Combine(Root, "logs");
    public string Worker => Path.Combine(Root, "worker");
    public string WebView2 => Path.Combine(Root, "WebView2");
}

public sealed class SqliteDatabase
{
    private readonly string _connectionString;

    public SqliteDatabase(AppPaths paths)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = paths.Database, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS audit_records (
                id TEXT PRIMARY KEY,
                tenant_id TEXT NOT NULL,
                target_id TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                outcome TEXT NOT NULL,
                operation_type TEXT NOT NULL,
                search_text TEXT NOT NULL,
                payload TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_audit_tenant_time ON audit_records (tenant_id, timestamp DESC);
            CREATE INDEX IF NOT EXISTS ix_audit_target ON audit_records (tenant_id, target_id);
            CREATE TABLE IF NOT EXISTS templates (id TEXT PRIMARY KEY, payload TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS template_assignments (
                tenant_id TEXT NOT NULL, room_id TEXT NOT NULL, template_id TEXT NOT NULL, payload TEXT NOT NULL,
                PRIMARY KEY (tenant_id, room_id)
            );
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}

/// <summary>Persistent, application-local audit log for live tenants. Records are sanitized: no tokens, credentials or raw responses.</summary>
public sealed class SqliteAuditStore(SqliteDatabase db) : IAuditStore
{
    public async Task AppendAsync(AuditRecord record, CancellationToken cancellationToken)
    {
        await using var connection = db.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_records (id, tenant_id, target_id, timestamp, outcome, operation_type, search_text, payload)
            VALUES ($id, $tenant, $target, $ts, $outcome, $op, $search, $payload)
            """;
        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$tenant", record.TenantId);
        command.Parameters.AddWithValue("$target", record.TargetId);
        command.Parameters.AddWithValue("$ts", record.Timestamp.UtcDateTime.ToString("o"));
        command.Parameters.AddWithValue("$outcome", record.Outcome.ToString());
        command.Parameters.AddWithValue("$op", record.OperationType);
        command.Parameters.AddWithValue("$search", $"{record.TargetName} {record.OperationLabel} {record.Actor} {record.TargetId}".ToLowerInvariant());
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(record, RoomOpsJson.Options));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static (string Where, List<SqliteParameter> Parameters) Filter(AuditQuery query)
    {
        var clauses = new List<string>();
        var parameters = new List<SqliteParameter>();
        if (query.TenantId is not null) { clauses.Add("tenant_id = $tenant"); parameters.Add(new("$tenant", query.TenantId)); }
        if (query.TargetId is not null) { clauses.Add("target_id = $target"); parameters.Add(new("$target", query.TargetId)); }
        if (query.Outcome is { } outcome) { clauses.Add("outcome = $outcome"); parameters.Add(new("$outcome", outcome.ToString())); }
        if (query.OperationType is not null) { clauses.Add("operation_type = $op"); parameters.Add(new("$op", query.OperationType)); }
        if (query.Since is { } since) { clauses.Add("timestamp >= $since"); parameters.Add(new("$since", since.UtcDateTime.ToString("o"))); }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            clauses.Add("search_text LIKE $search ESCAPE '\\'");
            var escaped = query.Search.Trim().ToLowerInvariant().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
            parameters.Add(new("$search", $"%{escaped}%"));
        }
        return (clauses.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", clauses), parameters);
    }

    public async Task<IReadOnlyList<AuditRecord>> QueryAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        var (where, parameters) = Filter(query);
        await using var connection = db.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT payload FROM audit_records {where} ORDER BY timestamp DESC LIMIT $limit OFFSET $offset";
        command.Parameters.AddRange(parameters);
        command.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 10_000));
        command.Parameters.AddWithValue("$offset", Math.Max(query.Offset, 0));
        var results = new List<AuditRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (JsonSerializer.Deserialize<AuditRecord>(reader.GetString(0), RoomOpsJson.Options) is { } record) results.Add(record);
        }
        return results;
    }

    public async Task<int> CountAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        var (where, parameters) = Filter(query);
        await using var connection = db.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM audit_records {where}";
        command.Parameters.AddRange(parameters);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<AuditRecord?> GetAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = db.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM audit_records WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        var payload = await command.ExecuteScalarAsync(cancellationToken) as string;
        return payload is null ? null : JsonSerializer.Deserialize<AuditRecord>(payload, RoomOpsJson.Options);
    }

    public async Task UpdateVerificationAsync(string id, VerificationResult verification, DateTimeOffset verifiedAt, CancellationToken cancellationToken)
    {
        var record = await GetAsync(id, cancellationToken);
        if (record is null) return;
        await ReplaceAsync(record with { Verification = verification, VerifiedAt = verifiedAt }, cancellationToken);
    }

    public async Task MarkResumedAsync(string id, CancellationToken cancellationToken)
    {
        var record = await GetAsync(id, cancellationToken);
        if (record is null) return;
        await ReplaceAsync(record with { PendingCommands = [], Outcome = AuditOutcome.Succeeded }, cancellationToken);
    }

    private async Task ReplaceAsync(AuditRecord record, CancellationToken cancellationToken)
    {
        await using var connection = db.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE audit_records SET payload = $payload, outcome = $outcome WHERE id = $id";
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(record, RoomOpsJson.Options));
        command.Parameters.AddWithValue("$outcome", record.Outcome.ToString());
        command.Parameters.AddWithValue("$id", record.Id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>Volatile audit store used by demo sessions so synthetic activity never reaches the persistent history.</summary>
public sealed class InMemoryAuditStore : IAuditStore
{
    private readonly ConcurrentDictionary<string, AuditRecord> _records = new();

    public InMemoryAuditStore(IEnumerable<AuditRecord>? seed = null)
    {
        foreach (var record in seed ?? []) _records[record.Id] = record;
    }

    private IEnumerable<AuditRecord> Filter(AuditQuery q) => _records.Values
        .Where(r => q.TenantId is null || r.TenantId == q.TenantId)
        .Where(r => q.TargetId is null || r.TargetId == q.TargetId)
        .Where(r => q.Outcome is null || r.Outcome == q.Outcome)
        .Where(r => q.OperationType is null || r.OperationType == q.OperationType)
        .Where(r => q.Since is null || r.Timestamp >= q.Since)
        .Where(r => string.IsNullOrWhiteSpace(q.Search) || $"{r.TargetName} {r.OperationLabel} {r.Actor} {r.TargetId}".Contains(q.Search.Trim(), StringComparison.OrdinalIgnoreCase));

    public Task AppendAsync(AuditRecord record, CancellationToken cancellationToken)
    {
        _records[record.Id] = record;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditRecord>> QueryAsync(AuditQuery query, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AuditRecord>>(Filter(query).OrderByDescending(r => r.Timestamp).Skip(Math.Max(query.Offset, 0)).Take(Math.Clamp(query.Limit, 1, 10_000)).ToList());

    public Task<int> CountAsync(AuditQuery query, CancellationToken cancellationToken) => Task.FromResult(Filter(query).Count());

    public Task<AuditRecord?> GetAsync(string id, CancellationToken cancellationToken) => Task.FromResult(_records.GetValueOrDefault(id));

    public Task UpdateVerificationAsync(string id, VerificationResult verification, DateTimeOffset verifiedAt, CancellationToken cancellationToken)
    {
        if (_records.TryGetValue(id, out var record)) _records[id] = record with { Verification = verification, VerifiedAt = verifiedAt };
        return Task.CompletedTask;
    }

    public Task MarkResumedAsync(string id, CancellationToken cancellationToken)
    {
        if (_records.TryGetValue(id, out var record)) _records[id] = record with { PendingCommands = [], Outcome = AuditOutcome.Succeeded };
        return Task.CompletedTask;
    }
}

public sealed class SqliteTemplateStore : ITemplateStore
{
    private readonly SqliteDatabase _db;

    public SqliteTemplateStore(SqliteDatabase db, TimeProvider time)
    {
        _db = db;
        SeedExamplesOnce(time.GetUtcNow());
    }

    /// <summary>Illustrative templates are added once on first use; administrators may edit or delete them.</summary>
    private void SeedExamplesOnce(DateTimeOffset now)
    {
        using var connection = _db.Open();
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT value FROM meta WHERE key = 'templates.seeded'";
        if (check.ExecuteScalar() is not null) return;
        foreach (var template in TemplateRules.Examples(now))
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT OR IGNORE INTO templates (id, payload, updated_at) VALUES ($id, $payload, $ts)";
            insert.Parameters.AddWithValue("$id", template.Id);
            insert.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(template, RoomOpsJson.Options));
            insert.Parameters.AddWithValue("$ts", now.UtcDateTime.ToString("o"));
            insert.ExecuteNonQuery();
        }
        using var mark = connection.CreateCommand();
        mark.CommandText = "INSERT INTO meta (key, value) VALUES ('templates.seeded', '1')";
        mark.ExecuteNonQuery();
    }

    public async Task<IReadOnlyList<ConfigurationTemplate>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = _db.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM templates ORDER BY updated_at DESC";
        var list = new List<ConfigurationTemplate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (JsonSerializer.Deserialize<ConfigurationTemplate>(reader.GetString(0), RoomOpsJson.Options) is { } t) list.Add(t);
        }
        return list.OrderBy(t => t.IsExample).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<ConfigurationTemplate?> GetAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = _db.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM templates WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteScalarAsync(cancellationToken) is string payload ? JsonSerializer.Deserialize<ConfigurationTemplate>(payload, RoomOpsJson.Options) : null;
    }

    public async Task SaveAsync(ConfigurationTemplate template, CancellationToken cancellationToken)
    {
        await using var connection = _db.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO templates (id, payload, updated_at) VALUES ($id, $payload, $ts) ON CONFLICT(id) DO UPDATE SET payload = excluded.payload, updated_at = excluded.updated_at";
        command.Parameters.AddWithValue("$id", template.Id);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(template, RoomOpsJson.Options));
        command.Parameters.AddWithValue("$ts", template.UpdatedAt.UtcDateTime.ToString("o"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = _db.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM templates WHERE id = $id; DELETE FROM template_assignments WHERE template_id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TemplateAssignment>> ListAssignmentsAsync(string tenantId, CancellationToken cancellationToken)
    {
        await using var connection = _db.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM template_assignments WHERE tenant_id = $tenant";
        command.Parameters.AddWithValue("$tenant", tenantId);
        var list = new List<TemplateAssignment>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (JsonSerializer.Deserialize<TemplateAssignment>(reader.GetString(0), RoomOpsJson.Options) is { } a) list.Add(a);
        }
        return list;
    }

    public async Task SetAssignmentAsync(TemplateAssignment assignment, CancellationToken cancellationToken)
    {
        await using var connection = _db.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO template_assignments (tenant_id, room_id, template_id, payload) VALUES ($t, $r, $tpl, $p) ON CONFLICT(tenant_id, room_id) DO UPDATE SET template_id = excluded.template_id, payload = excluded.payload";
        command.Parameters.AddWithValue("$t", assignment.TenantId);
        command.Parameters.AddWithValue("$r", assignment.RoomId);
        command.Parameters.AddWithValue("$tpl", assignment.TemplateId);
        command.Parameters.AddWithValue("$p", JsonSerializer.Serialize(assignment, RoomOpsJson.Options));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RemoveAssignmentAsync(string tenantId, string roomId, CancellationToken cancellationToken)
    {
        await using var connection = _db.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM template_assignments WHERE tenant_id = $t AND room_id = $r";
        command.Parameters.AddWithValue("$t", tenantId);
        command.Parameters.AddWithValue("$r", roomId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>Settings persisted as JSON in the user profile. Contains preferences only — never credentials or tokens.</summary>
public sealed class JsonSettingsStore : ISettingsStore, IDisposable
{
    public void Dispose() => _gate.Dispose();

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppSettings _current;

    public JsonSettingsStore(AppPaths paths)
    {
        _path = paths.Settings;
        _current = Load(_path);
        Text.Language = _current.Appearance.Language;
    }

    private static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), RoomOpsJson.Options) ?? new AppSettings();
        }
        catch (JsonException)
        {
            File.Copy(path, path + ".invalid", overwrite: true);
        }
        return new AppSettings();
    }

    public AppSettings Current => _current;

    public async Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var next = update(_current);
            var temp = _path + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(next, new JsonSerializerOptions(RoomOpsJson.Options) { WriteIndented = true }), cancellationToken);
            File.Move(temp, _path, overwrite: true);
            _current = next;
            // Backend text (explanations, findings, previews) follows the interface language from the next request on.
            Text.Language = next.Appearance.Language;
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }
}
