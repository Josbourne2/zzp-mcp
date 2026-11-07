using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ZzpMcp;

public sealed record StoredJob(long Id, JobSummary Summary, Job? Detail, long? Version,
    DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt, DateTimeOffset? ListFetchedAt, DateTimeOffset? DetailFetchedAt,
    Job? ListRecord);

/// <summary>Durable source data only. No raw payloads, browser sessions or agent assessments.</summary>
public sealed partial class JobStore
{
    private const int SchemaVersion = 3;
    private readonly string _connectionString;

    public JobStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Databasepad ontbreekt.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath, ForeignKeys = true, DefaultTimeout = 5,
            // Short-lived connections also allow deterministic backup and test-file cleanup.
            Pooling = false
        }.ToString();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var version = Convert.ToInt32(Scalar(connection, transaction, "PRAGMA user_version"));
        if (version > SchemaVersion)
            throw new InvalidOperationException($"SQLite-schema {version} is nieuwer dan ondersteund schema {SchemaVersion}; bestand niet gewijzigd.");
        if (version == 0)
        {
            Execute(connection, transaction, """
                CREATE TABLE sources (
                    source TEXT NOT NULL, portal TEXT NOT NULL,
                    PRIMARY KEY(source, portal)
                );
                CREATE TABLE jobs (
                    id INTEGER PRIMARY KEY, source TEXT NOT NULL, portal TEXT NOT NULL, source_id TEXT NOT NULL,
                    summary_json TEXT NOT NULL, list_json TEXT, first_seen_at TEXT NOT NULL, last_seen_at TEXT NOT NULL,
                    list_fetched_at TEXT, detail_fetched_at TEXT,
                    UNIQUE(source, portal, source_id),
                    FOREIGN KEY(source, portal) REFERENCES sources(source, portal)
                );
                CREATE TABLE job_versions (
                    id INTEGER PRIMARY KEY, job_id INTEGER NOT NULL REFERENCES jobs(id),
                    content_hash TEXT NOT NULL, content_json TEXT NOT NULL, fetched_at TEXT NOT NULL
                );
                CREATE INDEX versions_by_job ON job_versions(job_id, id DESC);
                CREATE TABLE searches (
                    id TEXT PRIMARY KEY, request_json TEXT NOT NULL, started_at TEXT NOT NULL
                );
                CREATE TABLE search_pages (
                    id INTEGER PRIMARY KEY, search_id TEXT NOT NULL REFERENCES searches(id),
                    source TEXT NOT NULL, portal TEXT NOT NULL, request_json TEXT NOT NULL,
                    state TEXT NOT NULL, error_code TEXT, observed_at TEXT NOT NULL,
                    source_total INTEGER, consumed INTEGER, next_cursor TEXT, incomplete INTEGER,
                    source_filters_json TEXT, local_filters_json TEXT, warnings_json TEXT,
                    FOREIGN KEY(source, portal) REFERENCES sources(source, portal)
                );
                CREATE INDEX pages_by_search ON search_pages(search_id, id);
                CREATE TABLE search_hits (
                    page_id INTEGER NOT NULL REFERENCES search_pages(id),
                    job_id INTEGER NOT NULL REFERENCES jobs(id), position INTEGER NOT NULL,
                    matched INTEGER NOT NULL CHECK(matched IN (0, 1)),
                    PRIMARY KEY(page_id, job_id), UNIQUE(page_id, position)
                );
                PRAGMA user_version = 1;
                """);
        }
        if (version < 2) MigrateSessions(connection, transaction);
        if (version < 3) MigrateCollections(connection, transaction);
        transaction.Commit();
    }

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZZPMcp", "jobs.db");

    public void SaveSearch(string searchId, string source, string portal, SearchRequest request,
        SourcePage? rawPage, SearchPage? page, bool includesDetails, string state, string? errorCode = null,
        DateTimeOffset? startedAt = null)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        SaveSearchCore(connection, transaction, searchId, source, portal, request, rawPage, page, includesDetails, state, errorCode, startedAt);
        transaction.Commit();
    }

    private static void SaveSearchCore(SqliteConnection connection, SqliteTransaction transaction, string searchId,
        string source, string portal, SearchRequest request, SourcePage? rawPage, SearchPage? page,
        bool includesDetails, string state, string? errorCode, DateTimeOffset? startedAt)
    {
        if (request.SessionId is not null) RequireSession(connection, transaction, request.SessionId);
        var observed = (rawPage?.FetchedAt ?? DateTimeOffset.UtcNow).ToString("O");
        RegisterSource(connection, transaction, source, portal);
        Execute(connection, transaction, """
            INSERT INTO searches(id, request_json, started_at, session_id) VALUES ($id, $request, $at, $session)
            ON CONFLICT(id) DO UPDATE SET started_at = MIN(searches.started_at, excluded.started_at)
            """, ("$id", searchId), ("$request", Json.Write(request with { Cursor = null })),
            ("$at", (startedAt ?? rawPage?.FetchedAt ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString("O")), ("$session", request.SessionId));
        var storedSession = Scalar(connection, transaction, "SELECT session_id FROM searches WHERE id=$id", ("$id", searchId));
        if (!Equals(storedSession is DBNull ? null : storedSession, request.SessionId))
            throw new InvalidOperationException("Zoekactie hoort bij een andere onderzoekssessie.");
        Execute(connection, transaction, """
            INSERT INTO search_pages(search_id, source, portal, request_json, state, error_code, observed_at,
                source_total, consumed, next_cursor, incomplete, source_filters_json, local_filters_json, warnings_json, query_key, request_cursor)
            VALUES ($search, $source, $portal, $request, $state, $error, $at, $total, $consumed, $next,
                $incomplete, $filters, $local, $warnings, $key, $cursor)
            """, ("$search", searchId), ("$source", source), ("$portal", portal), ("$request", Json.Write(request)),
            ("$state", state), ("$error", errorCode), ("$at", observed), ("$total", rawPage?.Total),
            ("$consumed", rawPage?.Consumed), ("$next", page?.NextCursor), ("$incomplete", page is null ? null : page.Incomplete ? 1 : 0),
            ("$filters", page is null ? null : Json.Write(page.AppliedSourceFilters)),
            ("$local", page is null ? null : Json.Write(page.AppliedLocalFilters)),
            ("$warnings", page is null ? null : Json.Write(page.Warnings)),
            ("$key", Json.Write(request with { Cursor = null, SessionId = null })), ("$cursor", request.Cursor));
        var pageId = Convert.ToInt64(Scalar(connection, transaction, "SELECT last_insert_rowid()"));
        var matches = page?.Jobs.Select(j => (j.Source, j.SourceId)).ToHashSet() ?? [];
        var position = 0;
        foreach (var job in (rawPage?.Jobs ?? []).DistinctBy(j => (j.Source, j.SourceId)))
        {
            if (job.Source != source) throw new InvalidOperationException("Vacaturebron wijkt af van de adapteridentiteit.");
            var jobId = Upsert(connection, transaction, job, portal, includesDetails, true);
            if (request.SessionId is not null) AddSessionJob(connection, transaction, request.SessionId, jobId);
            Execute(connection, transaction, "INSERT INTO search_hits VALUES ($page, $job, $position, $matched)",
                ("$page", pageId), ("$job", jobId), ("$position", position++), ("$matched", matches.Contains((job.Source, job.SourceId)) ? 1 : 0));
        }
    }

    public long? SaveDetail(Job job, string portal = "", string? sessionId = null)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var versionId = SaveDetailCore(connection, transaction, job, portal, sessionId);
        transaction.Commit();
        return versionId;
    }

    private static long? SaveDetailCore(SqliteConnection connection, SqliteTransaction transaction, Job job, string portal, string? sessionId)
    {
        if (sessionId is not null) RequireSession(connection, transaction, sessionId);
        RegisterSource(connection, transaction, job.Source, portal);
        var jobId = Upsert(connection, transaction, job, portal, true, false);
        // Associate only the exact returned content, including when a late response matches an older version.
        var version = Scalar(connection, transaction,
            "SELECT id FROM job_versions WHERE job_id=$job AND content_hash=$hash ORDER BY id DESC LIMIT 1",
            ("$job", jobId), ("$hash", ContentHash(job)));
        long? versionId = version is long id ? id : null;
        if (sessionId is not null)
        {
            AddSessionJob(connection, transaction, sessionId, jobId);
            if (versionId.HasValue)
                Execute(connection, transaction, """
                    INSERT INTO session_reads(session_id, job_id, version_id, returned_at)
                    VALUES ($session, $job, $version, $at)
                    ON CONFLICT(session_id, job_id, version_id) DO UPDATE SET returned_at=excluded.returned_at
                    """, ("$session", sessionId), ("$job", jobId), ("$version", versionId), ("$at", DateTimeOffset.UtcNow.ToString("O")));
        }
        return versionId;
    }

    public StoredJob? Read(string source, string sourceId, string portal = "")
    {
        using var connection = Open();
        using var command = Command(connection, null, """
            SELECT j.id, j.summary_json, j.first_seen_at, j.last_seen_at, j.list_fetched_at, j.detail_fetched_at,
                v.id, v.content_json, j.list_json
            FROM jobs j LEFT JOIN job_versions v ON v.id = (SELECT MAX(id) FROM job_versions WHERE job_id = j.id)
            WHERE j.source = $source AND j.portal = $portal AND j.source_id = $id
            """, ("$source", source), ("$portal", portal), ("$id", sourceId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        DateTimeOffset? Date(int ordinal) => reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal), System.Globalization.CultureInfo.InvariantCulture);
        var detailAt = Date(5);
        var detail = reader.IsDBNull(7) ? null : JsonSerializer.Deserialize<Job>(reader.GetString(7), Json.Options)! with { FetchedAt = detailAt!.Value };
        return new(reader.GetInt64(0), JsonSerializer.Deserialize<JobSummary>(reader.GetString(1), Json.Options)!, detail,
            reader.IsDBNull(6) ? null : reader.GetInt64(6), Date(2)!.Value, Date(3)!.Value, Date(4), detailAt,
            reader.IsDBNull(8) ? null : JsonSerializer.Deserialize<Job>(reader.GetString(8), Json.Options));
    }

    private static long Upsert(SqliteConnection connection, SqliteTransaction transaction, Job job, string portal, bool full, bool fromList)
    {
        var at = job.FetchedAt.ToUniversalTime().ToString("O");
        var summary = new JobSummary(job.Source, job.SourceId, job.Identity, job.Url, job.Title, job.Client, job.Locations,
            job.Hours, job.Hybrid, job.Duration, job.Compensation, job.Zzp, job.Payroll, job.FetchedAt);
        Execute(connection, transaction, """
            INSERT INTO jobs(source, portal, source_id, summary_json, list_json, first_seen_at, last_seen_at, list_fetched_at, detail_fetched_at)
            VALUES ($source, $portal, $id, $summary, $listJson, $at, $at, $list, $detail)
            ON CONFLICT(source, portal, source_id) DO UPDATE SET
                summary_json = CASE WHEN excluded.last_seen_at >= jobs.last_seen_at THEN excluded.summary_json ELSE jobs.summary_json END,
                list_json = CASE WHEN $list IS NOT NULL AND (jobs.list_fetched_at IS NULL OR $list >= jobs.list_fetched_at)
                    THEN excluded.list_json ELSE jobs.list_json END,
                first_seen_at = MIN(jobs.first_seen_at, excluded.first_seen_at),
                last_seen_at = MAX(jobs.last_seen_at, excluded.last_seen_at),
                list_fetched_at = CASE WHEN $list IS NOT NULL THEN MAX(COALESCE(jobs.list_fetched_at, $list), $list) ELSE jobs.list_fetched_at END
            """, ("$source", job.Source), ("$portal", portal), ("$id", job.SourceId), ("$summary", Json.Write(summary)),
            ("$at", at), ("$list", fromList ? at : null), ("$detail", full ? at : null),
            ("$listJson", fromList ? Json.Write(job with { Raw = JsonSerializer.SerializeToElement(new { }), Storage = null }) : null));
        var jobId = Convert.ToInt64(Scalar(connection, transaction, "SELECT id FROM jobs WHERE source=$source AND portal=$portal AND source_id=$id",
            ("$source", job.Source), ("$portal", portal), ("$id", job.SourceId)));
        if (!full) return jobId;
        // Ignore late responses so an older fetch cannot replace the current detail version.
        var detailTime = Scalar(connection, transaction, "SELECT detail_fetched_at FROM jobs WHERE id=$id", ("$id", jobId));
        if (detailTime is string previousTime && string.CompareOrdinal(at, previousTime) < 0) return jobId;
        var content = Content(job);
        var hash = ContentHash(job);
        var previousHash = Scalar(connection, transaction, "SELECT content_hash FROM job_versions WHERE job_id=$id ORDER BY id DESC LIMIT 1", ("$id", jobId));
        if (!Equals(previousHash, hash))
            Execute(connection, transaction, "INSERT INTO job_versions(job_id, content_hash, content_json, fetched_at) VALUES ($id, $hash, $content, $at)",
                ("$id", jobId), ("$hash", hash), ("$content", content), ("$at", at));
        Execute(connection, transaction, "UPDATE jobs SET detail_fetched_at=$at WHERE id=$id", ("$at", at), ("$id", jobId));
        return jobId;
    }

    private static string Content(Job job) => Json.Write(job with { FetchedAt = DateTimeOffset.UnixEpoch, Raw = JsonSerializer.SerializeToElement(new { }), Storage = null });
    private static string ContentHash(Job job) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Content(job))));

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private static void RegisterSource(SqliteConnection connection, SqliteTransaction transaction, string source, string portal) =>
        Execute(connection, transaction, "INSERT INTO sources VALUES ($source, $portal) ON CONFLICT DO NOTHING", ("$source", source), ("$portal", portal));

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return command.ExecuteScalar();
    }
}
