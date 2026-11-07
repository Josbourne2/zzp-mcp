using System.Text.Json;
using Microsoft.Data.Sqlite;
using ZzpMcp;

namespace ZzpMcp.Tests;

public sealed class SessionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "zzp-sessions-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_directory, "jobs.db");
    private static Job Vacancy(string id = "a", string text = "Volledige tekst", int minute = 0) =>
        JobMapper.Map(JsonSerializer.SerializeToElement(new { id, title = "Developer", description = text, url_slug = id }),
            DateTimeOffset.Parse("2026-11-05T10:00:00Z").AddMinutes(minute)) with { Source = "test", Identity = "test:" + id };

    private sealed class Source : IJobSource
    {
        public string Id => "test";
        public object Capabilities => new { };
        public int Calls { get; private set; }
        public string? Failure { get; set; }
        public bool SearchIncludesDetails { get; set; }
        public Job Detail { get; set; } = Vacancy();
        public int Total { get; set; } = 1;
        public Task<SourcePage> SearchAsync(SearchRequest request, int offset, CancellationToken ct)
        {
            Calls++;
            if (Failure is not null) throw new SourceException(Failure, "Bron tijdelijk onbeschikbaar.");
            return Task.FromResult(new SourcePage([Vacancy(offset == 0 ? "a" : "b")], Total, 1, [], Detail.FetchedAt));
        }
        public Task<Job> GetAsync(string id, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Detail);
        }
    }

    private object? Sql(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, ForeignKeys = true, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    [Fact]
    public async Task New_sessions_are_empty_and_do_not_delete_or_inherit_previous_research()
    {
        var store = new JobStore(Database);
        var source = new Source();
        var service = new JobService([source], store);
        var first = service.StartSession("Eerste zoektocht");
        await service.SearchAsync("test", new(SessionId: first.SessionId), default);
        var second = service.StartSession("Tweede zoektocht");
        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.Equal(1L, service.GetSession(first.SessionId).CollectedJobs);
        Assert.Equal(0L, service.GetSession(second.SessionId).CollectedJobs);
        Assert.Equal(0L, service.GetSession(second.SessionId).SearchCount);
        Assert.NotNull(store.Read("test", "a"));
        await service.SearchAsync("test", new(SessionId: second.SessionId), default);
        Assert.Equal(1L, service.GetSession(second.SessionId).CollectedJobs);
        Assert.Equal(1L, Sql("SELECT COUNT(*) FROM jobs"));
        Assert.Equal(2L, Sql("SELECT COUNT(*) FROM session_jobs"));
    }

    [Fact]
    public async Task Explicit_session_ids_isolate_interleaved_calls_and_unscoped_legacy_calls()
    {
        var service = new JobService([new Source()], new JobStore(Database));
        var a = service.StartSession();
        var b = service.StartSession();
        await service.SearchAsync("test", new(Query: "architect", SessionId: a.SessionId), default);
        await service.GetAsync("test", "a", default, b.SessionId);
        await service.SearchAsync("test", new(Query: "legacy"), default);
        Assert.Equal(1L, service.GetSession(a.SessionId).SearchCount);
        Assert.Equal(0L, service.GetSession(a.SessionId).DetailsReturned);
        Assert.Equal(0L, service.GetSession(b.SessionId).SearchCount);
        Assert.Equal(1L, service.GetSession(b.SessionId).DetailsReturned);
        Assert.Equal(2L, Sql("SELECT COUNT(*) FROM searches"));
    }

    [Fact]
    public async Task Details_and_reviews_are_version_bound_and_never_implied_by_searching()
    {
        var store = new JobStore(Database);
        var source = new Source { SearchIncludesDetails = true };
        var service = new JobService([source], store);
        var session = service.StartSession();
        await service.SearchAsync("test", new(SessionId: session.SessionId), default);
        Assert.Equal(0L, service.GetSession(session.SessionId).DetailsReturned);
        var currentVersion = Assert.Single(service.GetSession(session.SessionId).Jobs).CurrentVersionId!.Value;
        Assert.Equal("version_not_returned", Assert.Throws<SourceException>(() =>
            service.MarkReviewed(session.SessionId, "test", "a", currentVersion)).Code);
        var job = await service.GetAsync("test", "a", default, session.SessionId);
        var version = job.Storage!.VersionId!.Value;
        Assert.Equal(session.SessionId, job.Storage.SessionId);
        Assert.Equal(1L, service.GetSession(session.SessionId).DetailsReturned);
        Assert.Equal(0L, service.GetSession(session.SessionId).CurrentVersionsReviewed);
        service.MarkReviewed(session.SessionId, "test", "a", version);
        Assert.Equal(1L, service.GetSession(session.SessionId).CurrentVersionsReviewed);
        var other = service.StartSession();
        Assert.Equal("version_not_returned", Assert.Throws<SourceException>(() =>
            service.MarkReviewed(other.SessionId, "test", "a", version)).Code);
        source.Detail = Vacancy(text: "Nieuwe inhoud", minute: 1);
        store.SaveDetail(source.Detail);
        var changed = await service.GetAsync("test", "a", default, session.SessionId);
        Assert.NotEqual(version, changed.Storage!.VersionId);
        var progress = service.GetSession(session.SessionId);
        Assert.Equal(0L, progress.CurrentVersionsReviewed);
        Assert.False(Assert.Single(progress.Jobs).CurrentVersionReviewed);
        service.MarkReviewed(session.SessionId, "test", "a", version);
        Assert.Equal(0L, service.GetSession(session.SessionId).CurrentVersionsReviewed);
        service.MarkReviewed(session.SessionId, "test", "a", changed.Storage.VersionId!.Value);
        Assert.Equal(1L, service.GetSession(session.SessionId).CurrentVersionsReviewed);
    }

    [Fact]
    public async Task Session_survives_restart_and_keeps_pending_page_parameters_and_reviews()
    {
        var source = new Source { Total = 2 };
        var service = new JobService([source], new JobStore(Database));
        var session = service.StartSession("Herstartbare zoektocht");
        var first = await service.SearchAllAsync(new(Query: "developer", PageSize: 1, SessionId: session.SessionId), default);
        Assert.Equal(session.SessionId, first.SessionId);
        var job = await service.GetAsync("test", "a", default, session.SessionId);
        service.MarkReviewed(session.SessionId, "test", "a", job.Storage!.VersionId!.Value);
        var restarted = new JobService([source], new JobStore(Database));
        var restored = restarted.GetSession(session.SessionId, "pages");
        Assert.Equal(1L, restored.PendingPages);
        Assert.Equal(1L, restored.CurrentVersionsReviewed);
        var page = Assert.Single(restored.Pages);
        Assert.True(page.Pending);
        Assert.Equal("developer", page.Request.Query);
        Assert.NotNull(page.NextCursor);
        Assert.Equal("search_expired", (await Assert.ThrowsAsync<SourceException>(() => restarted.ResumeAsync(first.SearchId, default))).Code);
        await restarted.SearchAsync("test", page.Request with { Cursor = page.NextCursor }, default);
        var completed = restarted.GetSession(session.SessionId, "pages");
        Assert.Equal(0L, completed.PendingPages);
        Assert.All(completed.Pages, p => Assert.False(p.Pending));
        Assert.Equal(2L, completed.CollectedJobs);
        Assert.Equal(session.SessionId, Assert.Single(restarted.ListSessions().Sessions).SessionId);
    }

    [Fact]
    public async Task Failed_pages_remain_pending_until_the_same_query_and_cursor_succeed()
    {
        var source = new Source { Failure = "login_required" };
        var service = new JobService([source], new JobStore(Database));
        var session = service.StartSession();
        var request = new SearchRequest(Query: "developer", SessionId: session.SessionId);
        var pending = await service.SearchAllAsync(request, default);
        Assert.Equal(1L, service.GetSession(session.SessionId).PendingPages);
        source.Failure = null;
        await service.SearchAsync("test", request with { Query = "architect" }, default);
        Assert.Equal(1L, service.GetSession(session.SessionId).PendingPages);
        await service.ResumeAsync(pending.SearchId, default);
        Assert.Equal(0L, service.GetSession(session.SessionId).PendingPages);
        Assert.Equal(2L, service.GetSession(session.SessionId).SearchCount);
    }

    [Fact]
    public async Task Unknown_session_is_rejected_before_network_requests()
    {
        var source = new Source();
        var service = new JobService([source], new JobStore(Database));
        var unknown = Guid.NewGuid().ToString("N");
        Assert.Equal("unknown_session", (await Assert.ThrowsAsync<SourceException>(() =>
            service.SearchAllAsync(new(SessionId: unknown), default))).Code);
        Assert.Equal("invalid_session", (await Assert.ThrowsAsync<SourceException>(() =>
            service.GetAsync("test", "a", default, "invalid"))).Code);
        Assert.Equal(0, source.Calls);
        Assert.Equal("storage_unavailable", Assert.Throws<SourceException>(() => new JobService([source]).StartSession()).Code);
    }

    [Fact]
    public async Task Session_failure_rolls_back_membership_and_keeps_live_results()
    {
        var store = new JobStore(Database);
        var service = new JobService([new Source()], store);
        var session = service.StartSession();
        Sql("CREATE TRIGGER fail_session BEFORE INSERT ON session_jobs BEGIN SELECT RAISE(ABORT, 'test'); END");
        var page = await service.SearchAsync("test", new(SessionId: session.SessionId), default);
        Assert.Single(page.Jobs);
        Assert.Equal("failed", page.Storage!.State);
        Assert.Equal(session.SessionId, page.Storage.SessionId);
        Assert.Equal(0L, service.GetSession(session.SessionId).CollectedJobs);
        Assert.Equal(0L, Sql("SELECT COUNT(*) FROM searches"));
        var detail = await service.GetAsync("test", "a", default, session.SessionId);
        Assert.Equal("failed", detail.Storage!.State);
        Assert.Null(detail.Storage.VersionId);
        Assert.Equal(0L, Sql("SELECT COUNT(*) FROM job_versions"));
    }

    [Fact]
    public void Bounded_membership_pagination_does_not_repeat_or_skip_new_members()
    {
        var store = new JobStore(Database);
        var service = new JobService([new Source()], store);
        var session = service.StartSession();
        store.SaveDetail(Vacancy("a"), sessionId: session.SessionId);
        store.SaveDetail(Vacancy("b"), sessionId: session.SessionId);
        var first = service.GetSession(session.SessionId, pageSize: 1);
        Assert.Equal("a", Assert.Single(first.Jobs).Job.SourceId);
        store.SaveDetail(Vacancy("c"), sessionId: session.SessionId);
        var second = service.GetSession(session.SessionId, pageSize: 1, afterId: first.NextAfterId!.Value);
        Assert.Equal("b", Assert.Single(second.Jobs).Job.SourceId);
        var third = service.GetSession(session.SessionId, pageSize: 1, afterId: second.NextAfterId!.Value);
        Assert.Equal("c", Assert.Single(third.Jobs).Job.SourceId);
        Assert.Null(third.NextAfterId);
        Assert.Throws<SourceException>(() => service.GetSession(session.SessionId, pageSize: 101));
        Assert.Throws<SourceException>(() => service.GetSession(session.SessionId, section: "invented"));
        Assert.Throws<SourceException>(() => service.ListSessions(offset: -1));
        Assert.Throws<SourceException>(() => service.StartSession(new string('a', 201)));
    }

    private void DowngradeToVersionOne()
    {
        Sql("""
            DROP TABLE collection_members;
            DROP TABLE collection_runs;
            DROP TABLE session_reads;
            DROP TABLE session_jobs;
            DROP INDEX searches_by_session;
            ALTER TABLE searches DROP COLUMN session_id;
            DROP TABLE research_sessions;
            DROP INDEX pages_by_query;
            ALTER TABLE search_pages DROP COLUMN query_key;
            ALTER TABLE search_pages DROP COLUMN request_cursor;
            DROP INDEX versions_identity;
            PRAGMA user_version=1;
            """);
    }

    [Fact]
    public async Task Migration_preserves_version_one_data_and_leaves_old_searches_unassigned()
    {
        var old = new JobStore(Database);
        old.SaveDetail(Vacancy());
        await new JobService([new Source()], old).SearchAsync("test", new(), default);
        DowngradeToVersionOne();
        var migrated = new JobStore(Database);
        Assert.Equal(3L, Sql("PRAGMA user_version"));
        Assert.NotNull(migrated.Read("test", "a")!.Detail);
        Assert.Equal(1L, Sql("SELECT COUNT(*) FROM searches WHERE session_id IS NULL"));
        var session = migrated.StartSession();
        Assert.Equal(0L, migrated.GetSession(session.SessionId).CollectedJobs);
        Assert.Null(Sql("PRAGMA foreign_key_check"));
    }

    [Fact]
    public void Failed_session_migration_is_atomic()
    {
        _ = new JobStore(Database);
        DowngradeToVersionOne();
        Sql("CREATE TABLE session_jobs(existing_column TEXT)");
        Assert.Throws<SqliteException>(() => new JobStore(Database));
        Assert.Equal(1L, Sql("PRAGMA user_version"));
        Assert.Equal(0L, Sql("SELECT COUNT(*) FROM sqlite_master WHERE name='research_sessions'"));
        Assert.Equal(0L, Sql("SELECT COUNT(*) FROM pragma_table_info('searches') WHERE name='session_id'"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
