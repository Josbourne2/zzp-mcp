using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ZzpMcp;

public sealed record CollectionRun(string CollectionId, string SessionId, string Source, string Portal,
    // RemovedJobs is retained for compatibility with historical runs; scans never increase it.
    string State, int NextPage, int RemovedJobs, DateTimeOffset StartedAt);
public sealed record CollectionFailure(string SourceId, string Code, string Message);
public sealed record CollectionResult(CollectionRun Collection, bool Complete, long CollectedJobs, long FullDetails,
    long MissingDetails, int FetchedThisBatch, CollectionFailure[] Errors, string NextTool = "collect_jobs");

public sealed partial class JobStore
{
    private static void MigrateCollections(SqliteConnection connection, SqliteTransaction transaction) => Execute(connection, transaction, """
        CREATE TABLE collection_runs(
            id TEXT PRIMARY KEY, session_id TEXT NOT NULL REFERENCES research_sessions(id),
            source TEXT NOT NULL, portal TEXT NOT NULL, state TEXT NOT NULL,
            next_page INTEGER NOT NULL DEFAULT 0, removed_jobs INTEGER NOT NULL DEFAULT 0,
            started_at TEXT NOT NULL, filters_json TEXT NOT NULL
        );
        CREATE UNIQUE INDEX one_source_scan ON collection_runs(source, portal) WHERE state='listing';
        CREATE TABLE collection_members(
            run_id TEXT NOT NULL REFERENCES collection_runs(id), source_id TEXT NOT NULL,
            detail_error TEXT, attempted_at TEXT, PRIMARY KEY(run_id, source_id)
        );
        PRAGMA user_version=3;
        """);

    public CollectionRun StartCollection(string sessionId, IJobSource source)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        RequireSession(connection, transaction, sessionId);
        var resumable = Scalar(connection, transaction, """
            SELECT id FROM collection_runs WHERE session_id=$session AND source=$source AND portal=$portal
                AND state IN ('listing','details') ORDER BY started_at DESC LIMIT 1
            """, ("$session", sessionId), ("$source", source.Id), ("$portal", source.PortalId));
        if (resumable is string existing)
        {
            var resumed = RequireCollection(connection, transaction, existing, sessionId);
            transaction.Commit();
            return resumed;
        }
        using (var command = Command(connection, transaction,
            "SELECT id FROM collection_runs WHERE source=$source AND portal=$portal AND state='listing'",
            ("$source", source.Id), ("$portal", source.PortalId)))
        {
            if (command.ExecuteScalar() is string active)
                throw new SourceException("collection_in_progress", $"Er loopt al een volledige bronscan. Hervat collectionId={active}; er is niets gewist.");
        }
        var run = new CollectionRun(Guid.NewGuid().ToString("N"), sessionId, source.Id, source.PortalId, "listing", 0, 0, DateTimeOffset.UtcNow);
        Execute(connection, transaction, """
            INSERT INTO collection_runs(id, session_id, source, portal, state, started_at, filters_json)
            VALUES ($id, $session, $source, $portal, 'listing', $at, $filters)
            """, ("$id", run.CollectionId), ("$session", sessionId), ("$source", source.Id), ("$portal", source.PortalId),
            ("$at", run.StartedAt.ToString("O")), ("$filters", Json.Write(source.CollectionFilters)));
        transaction.Commit();
        return run;
    }

    public CollectionRun ReadCollection(string collectionId, string sessionId)
    {
        using var connection = Open();
        return RequireCollection(connection, null, collectionId, sessionId);
    }

    private static CollectionRun RequireCollection(SqliteConnection connection, SqliteTransaction? transaction, string id, string sessionId)
    {
        RequireSession(connection, transaction, sessionId);
        using var command = Command(connection, transaction, """
            SELECT id, session_id, source, portal, state, next_page, removed_jobs, started_at
            FROM collection_runs WHERE id=$id AND session_id=$session
            """, ("$id", id), ("$session", sessionId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new SourceException("unknown_collection", "Verzameling hoort niet bij deze onderzoekssessie of bestaat niet.");
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetInt32(5), reader.GetInt32(6), ParseDate(reader.GetString(7)));
    }

    // A scan records its own observed members; absence never removes shared source data or session history.
    public CollectionRun SaveCollectionPage(CollectionRun expected, SourcePage page, object filters)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var run = RequireCollection(connection, transaction, expected.CollectionId, expected.SessionId);
        if (run.State != "listing" || run.NextPage != expected.NextPage)
            throw new SourceException("collection_changed", "Verzamelvoortgang is gewijzigd; hervat met dezelfde collectionId.");
        if (page.Jobs.Any(j => j.Source != run.Source) || page.Consumed < page.Jobs.Length || page.Consumed < 0)
            throw new SourceException("source_schema", "Ongeldige bronpagina; er is niets gewist.");
        var newIds = 0;
        foreach (var job in page.Jobs.DistinctBy(j => j.SourceId))
        {
            using var insert = Command(connection, transaction, "INSERT OR IGNORE INTO collection_members(run_id,source_id) VALUES ($run,$id)",
                ("$run", run.CollectionId), ("$id", job.SourceId));
            newIds += insert.ExecuteNonQuery();
        }
        if (run.NextPage > 0 && newIds < page.Jobs.Select(j => j.SourceId).Distinct().Count() || page.Consumed == 0 && page.HasMore != false)
        {
            Execute(connection, transaction, "UPDATE collection_runs SET state='incomplete' WHERE id=$id", ("$id", run.CollectionId));
            transaction.Commit();
            return run with { State = "incomplete" };
        }
        var hasMore = page.HasMore ?? true;
        var request = new SearchRequest(RadiusKm: 100, PageSize: 1000, Cursor: $"collect:{run.CollectionId}:{run.NextPage}", SessionId: run.SessionId);
        var summaries = page.Jobs.Select(j => new JobSummary(j.Source, j.SourceId, j.Identity, j.Url, j.Title,
            j.Client, j.Locations, j.Hours, j.Hybrid, j.Duration, j.Compensation, j.Zzp, j.Payroll, j.FetchedAt)).ToArray();
        var summary = new SearchPage(summaries, page.Total, null, page.Consumed, null,
            hasMore ? $"collect:{run.CollectionId}:{run.NextPage + 1}" : null, false, page.Warnings ?? [], filters,
            new { scope = "volledige broncollectie volgens opgegeven URL; geen lokale voorkeurfilters" }, [], "geen facetten");
        SaveSearchCore(connection, transaction, Guid.NewGuid().ToString("N"), run.Source, run.Portal, request,
            page, summary, false, "completed", null, run.StartedAt);
        var state = hasMore ? "listing" : "details";
        Execute(connection, transaction, "UPDATE collection_runs SET state=$state, next_page=next_page+1 WHERE id=$id",
            ("$state", state), ("$id", run.CollectionId));
        transaction.Commit();
        return run with { State = state, NextPage = run.NextPage + 1 };
    }

    private const string MissingCollectionSql = """
        FROM collection_members m JOIN collection_runs c ON c.id=m.run_id
        LEFT JOIN jobs j ON j.source=c.source AND j.portal=c.portal AND j.source_id=m.source_id
        WHERE m.run_id=$run AND NOT EXISTS (SELECT 1 FROM job_versions v WHERE v.job_id=j.id)
        """;

    public string[] MissingCollectionDetails(CollectionRun run, int limit)
    {
        using var connection = Open();
        using var command = Command(connection, null, $"SELECT m.source_id {MissingCollectionSql} ORDER BY m.attempted_at IS NOT NULL, m.attempted_at, m.source_id LIMIT $limit",
            ("$run", run.CollectionId), ("$limit", limit));
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids.ToArray();
    }

    public void SaveCollectionDetail(CollectionRun expected, Job job)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var run = RequireCollection(connection, transaction, expected.CollectionId, expected.SessionId);
        if (job.Source != run.Source || Scalar(connection, transaction,
            "SELECT 1 FROM collection_members WHERE run_id=$run AND source_id=$id", ("$run", run.CollectionId), ("$id", job.SourceId)) is null)
            throw new SourceException("source_schema", "Detail hoort niet bij deze bronverzameling.");
        SaveDetailCore(connection, transaction, job, run.Portal, null);
        Execute(connection, transaction, "UPDATE collection_members SET detail_error=NULL, attempted_at=$at WHERE run_id=$run AND source_id=$id",
            ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$run", run.CollectionId), ("$id", job.SourceId));
        transaction.Commit();
    }

    public void SaveCollectionError(CollectionRun run, string sourceId, string code)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "UPDATE collection_members SET detail_error=$code, attempted_at=$at WHERE run_id=$run AND source_id=$id",
            ("$code", code), ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$run", run.CollectionId), ("$id", sourceId));
        transaction.Commit();
    }

    public CollectionResult CollectionStatus(CollectionRun expected, int fetched, CollectionFailure[] errors)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var run = RequireCollection(connection, transaction, expected.CollectionId, expected.SessionId);
        var total = Convert.ToInt64(Scalar(connection, transaction, "SELECT COUNT(*) FROM collection_members WHERE run_id=$run", ("$run", run.CollectionId)));
        var missing = Convert.ToInt64(Scalar(connection, transaction, $"SELECT COUNT(*) {MissingCollectionSql}", ("$run", run.CollectionId)));
        var complete = run.State is "details" or "completed" && missing == 0;
        if (complete)
        {
            Execute(connection, transaction, "UPDATE collection_runs SET state='completed' WHERE id=$id", ("$id", run.CollectionId));
            run = run with { State = "completed" };
        }
        transaction.Commit();
        return new(run, complete, total, total - missing, missing, fetched, errors);
    }
}

public sealed partial class JobService
{
    public async Task<CollectionResult> CollectAsync(string sessionId, string source, string? collectionId, int batchSize, CancellationToken ct)
    {
        if (batchSize is < 1 or > 20) throw new SourceException("invalid_argument", "batchSize moet 1–20 zijn.");
        ValidateSession(sessionId);
        var adapter = Source(source);
        if (adapter.CollectionPageSize == 0) throw new SourceException("unsupported_operation", "Deze bron ondersteunt geen volledige verzamelroute.");
        var run = SessionOperation(s => collectionId is null ? s.StartCollection(sessionId, adapter) : s.ReadCollection(collectionId, sessionId));
        if (run.Source != source || run.Portal != adapter.PortalId) throw new SourceException("invalid_argument", "collectionId hoort bij een andere bron of portaal.");
        var errors = new List<CollectionFailure>();
        if (run.State == "listing")
        {
            var connected = await adapter.ConnectAsync(ct);
            if (!connected.Ready) throw new SourceException(connected.State, $"{connected.Message} Hervat met collectionId={run.CollectionId}.");
            SourcePage page;
            try { page = await adapter.ReadCollectionPageAsync(run.NextPage, ct); }
            catch (SourceException ex) { throw new SourceException(ex.Code, $"{ex.Message} Hervat met collectionId={run.CollectionId}."); }
            ct.ThrowIfCancellationRequested();
            run = SessionOperation(s => s.SaveCollectionPage(run, page, adapter.CollectionFilters));
        }
        if (run.State == "incomplete")
            return SessionOperation(s => s.CollectionStatus(run, 0, [new("", "source_repeated_page", "Bron herhaalt pagina's; niets verwijderd. Start later een nieuwe verzameling.")]));
        var fetched = 0;
        foreach (var id in SessionOperation(s => s.MissingCollectionDetails(run, batchSize)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Re-check before the source request: another worker may have just stored the full text.
                if (store!.Read(source, id, adapter.PortalId)?.Detail is not null) continue;
                var detail = await adapter.GetAsync(id, ct);
                if (detail.SourceId != id) throw new SourceException("source_schema", "Detail-ID komt niet overeen met de vacature.");
                SessionOperation(s => { s.SaveCollectionDetail(run, detail); return true; });
                fetched++;
            }
            catch (SourceException ex) when (ex.Code != "storage_error")
            {
                SessionOperation(s => { s.SaveCollectionError(run, id, ex.Code); return true; });
                errors.Add(new(id, ex.Code, ex.Message));
                if (ex.Code is "login_required" or "connecting") break;
            }
        }
        return SessionOperation(s => s.CollectionStatus(run, fetched, errors.ToArray()));
    }
}
