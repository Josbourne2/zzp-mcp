using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ZzpMcp;

public sealed record ResearchSession(string SessionId, string? Name, DateTimeOffset CreatedAt);
public sealed record SessionList(ResearchSession[] Sessions, int? NextOffset);
public sealed record SessionJob(long Id, JobSummary Job, string Portal, long? CurrentVersionId,
    long? ReturnedVersionId, long? ReviewedVersionId, bool CurrentVersionReviewed);
public sealed record SessionSourcePage(long Id, string Source, string Portal, string SearchId, string State,
    string? Error, SearchRequest Request, string? NextCursor, bool Incomplete, bool Pending, DateTimeOffset ObservedAt);
public sealed record SessionView(ResearchSession Session, long CollectedJobs, long DetailsReturned,
    long CurrentVersionsReviewed, long SearchCount, long PendingPages, string Section,
    SessionJob[] Jobs, SessionSourcePage[] Pages, long? NextAfterId);
public sealed record SessionReview(string SessionId, string Source, string SourceId, long VersionId, DateTimeOffset ReviewedAt);

public sealed partial class JobStore
{
    private static void MigrateSessions(SqliteConnection connection, SqliteTransaction transaction) => Execute(connection, transaction, """
        CREATE TABLE research_sessions(id TEXT PRIMARY KEY, name TEXT, created_at TEXT NOT NULL);
        ALTER TABLE searches ADD COLUMN session_id TEXT REFERENCES research_sessions(id);
        CREATE INDEX searches_by_session ON searches(session_id);
        ALTER TABLE search_pages ADD COLUMN query_key TEXT;
        ALTER TABLE search_pages ADD COLUMN request_cursor TEXT;
        CREATE INDEX pages_by_query ON search_pages(source, portal, query_key, request_cursor);
        CREATE TABLE session_jobs(
            id INTEGER PRIMARY KEY, session_id TEXT NOT NULL REFERENCES research_sessions(id),
            job_id INTEGER NOT NULL REFERENCES jobs(id), added_at TEXT NOT NULL,
            UNIQUE(session_id, job_id)
        );
        CREATE UNIQUE INDEX versions_identity ON job_versions(job_id, id);
        CREATE TABLE session_reads(
            session_id TEXT NOT NULL, job_id INTEGER NOT NULL, version_id INTEGER NOT NULL,
            returned_at TEXT NOT NULL, reviewed_at TEXT,
            PRIMARY KEY(session_id, job_id, version_id),
            FOREIGN KEY(session_id, job_id) REFERENCES session_jobs(session_id, job_id),
            FOREIGN KEY(job_id, version_id) REFERENCES job_versions(job_id, id)
        );
        PRAGMA user_version = 2;
        """);

    public ResearchSession StartSession(string? name = null)
    {
        if (name?.Length > 200) throw new SourceException("invalid_argument", "Sessienaam mag maximaal 200 tekens bevatten.");
        var session = new ResearchSession(Guid.NewGuid().ToString("N"), string.IsNullOrWhiteSpace(name) ? null : name.Trim(), DateTimeOffset.UtcNow);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "INSERT INTO research_sessions VALUES ($id, $name, $at)",
            ("$id", session.SessionId), ("$name", session.Name), ("$at", session.CreatedAt.ToString("O")));
        transaction.Commit();
        return session;
    }

    public SessionList ListSessions(int pageSize = 20, int offset = 0)
    {
        ValidateSessionPage(pageSize, offset);
        using var connection = Open();
        using var command = Command(connection, null,
            "SELECT id, name, created_at FROM research_sessions ORDER BY rowid DESC LIMIT $limit OFFSET $offset",
            ("$limit", pageSize + 1), ("$offset", offset));
        using var reader = command.ExecuteReader();
        var sessions = new List<ResearchSession>();
        while (reader.Read()) sessions.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), ParseDate(reader.GetString(2))));
        return new(sessions.Take(pageSize).ToArray(), sessions.Count > pageSize ? offset + pageSize : null);
    }

    public void ValidateSession(string sessionId)
    {
        using var connection = Open();
        RequireSession(connection, null, sessionId);
    }

    private static ResearchSession RequireSession(SqliteConnection connection, SqliteTransaction? transaction, string sessionId)
    {
        if (!Guid.TryParseExact(sessionId, "N", out _)) throw new SourceException("invalid_session", "Gebruik de sessionId uit start_session of list_sessions.");
        using var command = Command(connection, transaction, "SELECT id, name, created_at FROM research_sessions WHERE id=$id", ("$id", sessionId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new SourceException("unknown_session", "Onderzoekssessie niet gevonden in deze database. Gebruik start_session of list_sessions.");
        return new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), ParseDate(reader.GetString(2)));
    }

    private static void AddSessionJob(SqliteConnection connection, SqliteTransaction transaction, string sessionId, long jobId) =>
        Execute(connection, transaction, """
            INSERT INTO session_jobs(session_id, job_id, added_at) VALUES ($session, $job, $at)
            ON CONFLICT(session_id, job_id) DO NOTHING
            """, ("$session", sessionId), ("$job", jobId), ("$at", DateTimeOffset.UtcNow.ToString("O")));

    // A page needs attention until its failure/incompleteness is superseded, or its continuation has been fetched.
    // Match both query and input cursor: a retry or later query is not proof that another page was consumed.
    private const string PageProgressSql = """
        SELECT p.*,
            NOT EXISTS (
                SELECT 1 FROM search_pages later JOIN searches ls ON ls.id=later.search_id
                WHERE ls.session_id=$session AND later.source=p.source AND later.portal=p.portal
                    AND later.query_key=p.query_key AND later.request_cursor IS p.request_cursor
                    AND later.id>p.id AND later.state='completed'
            ) AND (
                p.state!='completed' OR p.incomplete=1 OR (p.next_cursor IS NOT NULL AND NOT EXISTS (
                    SELECT 1 FROM search_pages following JOIN searches fs ON fs.id=following.search_id
                    WHERE fs.session_id=$session AND following.source=p.source AND following.portal=p.portal
                        AND following.query_key=p.query_key AND following.request_cursor=p.next_cursor
                        AND following.state='completed'
                ))
            ) AS pending
        FROM search_pages p JOIN searches s ON s.id=p.search_id WHERE s.session_id=$session
        """;

    public SessionView GetSession(string sessionId, string section = "jobs", int pageSize = 20, long afterId = 0)
    {
        ValidateSessionPage(pageSize, afterId);
        if (section is not ("jobs" or "pages")) throw new SourceException("invalid_argument", "section moet jobs of pages zijn.");
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var session = RequireSession(connection, transaction, sessionId);
        long Count(string sql) => Convert.ToInt64(Scalar(connection, transaction, sql, ("$session", sessionId)));
        var collected = Count("SELECT COUNT(*) FROM session_jobs WHERE session_id=$session");
        var returned = Count("SELECT COUNT(DISTINCT job_id) FROM session_reads WHERE session_id=$session");
        var reviewed = Count("""
            SELECT COUNT(*) FROM session_jobs sj WHERE sj.session_id=$session AND EXISTS (
                SELECT 1 FROM session_reads r WHERE r.session_id=sj.session_id AND r.job_id=sj.job_id
                    AND r.reviewed_at IS NOT NULL AND r.version_id=(SELECT MAX(id) FROM job_versions WHERE job_id=sj.job_id))
            """);
        var searches = Count("SELECT COUNT(*) FROM searches WHERE session_id=$session");
        var pending = Count($"SELECT COUNT(*) FROM ({PageProgressSql}) WHERE pending=1");
        var jobs = new List<SessionJob>();
        var pages = new List<SessionSourcePage>();
        using (var command = Command(connection, transaction, section == "jobs" ? """
            SELECT sj.id, j.summary_json, j.portal,
                (SELECT MAX(id) FROM job_versions WHERE job_id=j.id),
                (SELECT MAX(version_id) FROM session_reads WHERE session_id=$session AND job_id=j.id),
                (SELECT MAX(version_id) FROM session_reads WHERE session_id=$session AND job_id=j.id AND reviewed_at IS NOT NULL)
            FROM session_jobs sj JOIN jobs j ON j.id=sj.job_id
            WHERE sj.session_id=$session AND sj.id>$after ORDER BY sj.id LIMIT $limit
            """ : $"SELECT * FROM ({PageProgressSql}) WHERE id>$after ORDER BY id LIMIT $limit",
            ("$session", sessionId), ("$after", afterId), ("$limit", pageSize + 1)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (section == "jobs")
                {
                    long? Version(int index) => reader.IsDBNull(index) ? null : reader.GetInt64(index);
                    var current = Version(3);
                    var lastReviewed = Version(5);
                    jobs.Add(new(reader.GetInt64(0), JsonSerializer.Deserialize<JobSummary>(reader.GetString(1), Json.Options)!,
                        reader.GetString(2), current, Version(4), lastReviewed, current.HasValue && current == lastReviewed));
                }
                else
                {
                    string? Text(string column) => reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetString(reader.GetOrdinal(column));
                    pages.Add(new(reader.GetInt64(reader.GetOrdinal("id")), Text("source")!, Text("portal")!, Text("search_id")!,
                        Text("state")!, Text("error_code"), JsonSerializer.Deserialize<SearchRequest>(Text("request_json")!, Json.Options)!,
                        Text("next_cursor"), !reader.IsDBNull(reader.GetOrdinal("incomplete")) && reader.GetInt64(reader.GetOrdinal("incomplete")) != 0,
                        reader.GetInt64(reader.GetOrdinal("pending")) != 0, ParseDate(Text("observed_at")!)));
                }
            }
        }
        transaction.Commit();
        long? next = section == "jobs" ? (jobs.Count > pageSize ? jobs[pageSize - 1].Id : null)
            : (pages.Count > pageSize ? pages[pageSize - 1].Id : null);
        return new(session, collected, returned, reviewed, searches, pending, section,
            jobs.Take(pageSize).ToArray(), pages.Take(pageSize).ToArray(), next);
    }

    public SessionReview MarkReviewed(string sessionId, string source, string sourceId, long versionId, string portal = "")
    {
        if (versionId <= 0) throw new SourceException("invalid_argument", "versionId moet positief zijn en afkomstig zijn uit get_job.storage.versionId.");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        RequireSession(connection, transaction, sessionId);
        var reviewedAt = DateTimeOffset.UtcNow;
        using var command = Command(connection, transaction, """
            UPDATE session_reads SET reviewed_at=$at
            WHERE session_id=$session AND version_id=$version AND job_id=(
                SELECT id FROM jobs WHERE source=$source AND portal=$portal AND source_id=$id)
            """, ("$at", reviewedAt.ToString("O")), ("$session", sessionId), ("$version", versionId),
            ("$source", source), ("$portal", portal), ("$id", sourceId));
        if (command.ExecuteNonQuery() == 0)
            throw new SourceException("version_not_returned", "Deze exacte vacatureversie is nog niet via get_job aan deze sessie geleverd; geen beoordeling geregistreerd.");
        transaction.Commit();
        return new(sessionId, source, sourceId, versionId, reviewedAt);
    }

    private static DateTimeOffset ParseDate(string text) => DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
    private static void ValidateSessionPage(int pageSize, long position)
    {
        if (pageSize is < 1 or > 100 || position < 0)
            throw new SourceException("invalid_argument", "pageSize moet 1–100 zijn en de paginapositie mag niet negatief zijn.");
    }
}
