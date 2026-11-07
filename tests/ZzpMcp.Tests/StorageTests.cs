using System.Text.Json;
using Microsoft.Data.Sqlite;
using ZzpMcp;

namespace ZzpMcp.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "zzp-storage-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_directory, "jobs.db");
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-11-05T10:00:00Z");

    private static Job Vacancy(string id = "a", string source = "test", string text = "Volledige tekst", int minute = 0) =>
        JobMapper.Map(JsonSerializer.SerializeToElement(new { id, title = "Developer", description = text, url_slug = id }), Epoch.AddMinutes(minute))
            with { Source = source, Identity = source + ":" + id };

    private object? Scalar(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, ForeignKeys = true, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    [Fact]
    public void Reopening_preserves_identity_portals_and_detail_without_raw_payloads()
    {
        var store = new JobStore(Database);
        var job = Vacancy() with { Raw = JsonSerializer.SerializeToElement(new { token = "NEVER-PERSIST-THIS" }) };
        store.SaveDetail(job);
        store.SaveDetail(job, "other-portal");
        store.SaveDetail(Vacancy(source: "other-source"));
        var reopened = new JobStore(Database);
        Assert.Equal(3L, Scalar("SELECT COUNT(*) FROM jobs"));
        Assert.Equal(job.Text, reopened.Read("test", "a")!.Detail!.Text);
        Assert.NotEqual(reopened.Read("test", "a")!.Id, reopened.Read("test", "a", "other-portal")!.Id);
        Assert.DoesNotContain("NEVER-PERSIST-THIS", (string)Scalar("SELECT group_concat(content_json) FROM job_versions")!);
        Assert.Equal(3L, Scalar("PRAGMA user_version"));
        Assert.Null(Scalar("PRAGMA foreign_key_check"));
        Assert.Throws<SqliteException>(() => Scalar("INSERT INTO job_versions(job_id,content_hash,content_json,fetched_at) VALUES(999,'x','{}','now')"));
    }

    [Fact]
    public void Versions_ignore_fetch_time_raw_payload_and_late_responses_but_preserve_content_reversions()
    {
        var store = new JobStore(Database);
        store.SaveDetail(Vacancy());
        var firstVersion = store.Read("test", "a")!.Version;
        store.SaveDetail(Vacancy(minute: 1) with { Raw = JsonSerializer.SerializeToElement(new { changed = true }) });
        Assert.Equal(firstVersion, store.Read("test", "a")!.Version);
        Assert.Equal(Epoch.AddMinutes(1), store.Read("test", "a")!.Detail!.FetchedAt);
        store.SaveDetail(Vacancy(text: "Gewijzigde tekst", minute: 2));
        store.SaveDetail(Vacancy(text: "Te laat ontvangen oude tekst", minute: 1));
        Assert.Equal("Gewijzigde tekst", store.Read("test", "a")!.Detail!.Text);
        store.SaveDetail(Vacancy(minute: 3));
        Assert.Equal(3L, Scalar("SELECT COUNT(*) FROM job_versions"));
        Assert.Equal(Vacancy().Text, store.Read("test", "a")!.Detail!.Text);
    }

    private sealed class Source(Job job, bool full = false) : IJobSource
    {
        public string Id => "test";
        public object Capabilities => new { };
        public bool SearchIncludesDetails => full;
        public string? Failure { get; set; }
        public int DetailCalls { get; private set; }
        public Task<SourceConnection> ConnectAsync(CancellationToken ct) => Task.FromResult(Failure is null
            ? new SourceConnection("ready", true, "") : new SourceConnection(Failure, false, "Handmatige login nodig."));
        public Task<SourcePage> SearchAsync(SearchRequest request, int offset, CancellationToken ct) => Task.FromResult(new SourcePage([job], 1, 1, [], job.FetchedAt));
        public async Task<Job> GetAsync(string id, CancellationToken ct)
        {
            DetailCalls++;
            var connection = await ConnectAsync(ct);
            if (!connection.Ready) throw new SourceException(connection.State, connection.Message);
            return job;
        }
    }

    [Fact]
    public async Task Search_relations_survive_reopening_and_list_records_never_replace_details()
    {
        var store = new JobStore(Database);
        store.SaveDetail(Vacancy());
        var source = new Source(Vacancy(text: "Snippet", minute: 1));
        var service = new JobService([source], store);
        var first = await service.SearchAsync("test", new(Query: "developer"), default);
        await service.SearchAsync("test", new(Query: "architect"), default);
        Assert.Equal("saved", first.Storage!.State);
        Assert.NotNull(first.Storage.SearchId);
        var reopened = new JobStore(Database).Read("test", "a")!;
        Assert.Equal(Vacancy().Text, reopened.Detail!.Text);
        Assert.Equal(Epoch, reopened.DetailFetchedAt);
        Assert.Equal(Epoch.AddMinutes(1), reopened.ListFetchedAt);
        Assert.Equal("Snippet", reopened.ListRecord!.Text);
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM jobs"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM job_versions"));
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM searches"));
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM search_hits"));
    }

    [Fact]
    public async Task Local_filter_retains_collected_jobs_and_records_nonmatches()
    {
        var store = new JobStore(Database);
        var service = new JobService([new Source(Vacancy(), true)], store);
        var page = await service.SearchAsync("test", new(Zzp: Permission.Allowed), default);
        Assert.Empty(page.Jobs);
        Assert.NotNull(store.Read("test", "a")!.Detail);
        Assert.Equal(0L, Scalar("SELECT matched FROM search_hits"));
        Assert.Equal(1L, Scalar("SELECT consumed FROM search_pages"));
    }

    [Fact]
    public async Task Resume_records_attempts_in_same_search_and_does_not_refetch_completed_source()
    {
        var store = new JobStore(Database);
        var source = new Source(Vacancy()) { Failure = "login_required" };
        var service = new JobService([source], store);
        var first = await service.SearchAllAsync(new(Query: "developer"), default);
        Assert.False(first.Complete);
        Assert.Equal("login_required", Scalar("SELECT state FROM search_pages"));
        source.Failure = null;
        var resumed = await service.ResumeAsync(first.SearchId, default);
        Assert.True(resumed.Complete);
        Assert.Equal(first.SearchId, resumed.Sources[0].Page!.Storage!.SearchId);
        await service.ResumeAsync(first.SearchId, default);
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM searches"));
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM search_pages"));
        var restarted = new JobService([source], new JobStore(Database));
        var error = await Assert.ThrowsAsync<SourceException>(() => restarted.ResumeAsync(first.SearchId, default));
        Assert.Equal("search_expired", error.Code);
    }

    [Fact]
    public async Task Storage_failure_rolls_back_entire_page_and_keeps_live_results()
    {
        var store = new JobStore(Database);
        Scalar("CREATE TRIGGER fail_hit BEFORE INSERT ON search_hits BEGIN SELECT RAISE(ABORT, 'test failure'); END");
        var service = new JobService([new Source(Vacancy(), true)], store);
        var page = await service.SearchAsync("test", new(), default);
        Assert.Single(page.Jobs);
        Assert.Equal("failed", page.Storage!.State);
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM searches"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM jobs"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM job_versions"));
        Scalar("DROP TRIGGER fail_hit");
        Assert.Equal("saved", (await service.SearchAsync("test", new(), default)).Storage!.State);
    }

    [Fact]
    public async Task Existing_details_are_explicitly_historical_and_do_not_require_a_live_session()
    {
        var source = new Source(Vacancy());
        var store = new JobStore(Database);
        var service = new JobService([source], store);
        Assert.Equal("saved", (await service.GetAsync("test", "a", default)).Storage!.State);
        var cached = await service.GetAsync("test", "a", default);
        Assert.True(cached.Storage!.CacheUsed);
        Assert.Equal(Epoch, cached.FetchedAt);
        source.Failure = "login_required";
        Assert.True((await service.GetAsync("test", "a", default)).Storage!.CacheUsed);
        Assert.Equal("login_required", (await Assert.ThrowsAsync<SourceException>(() => service.GetAsync("test", "missing", default))).Code);
        Assert.Equal(2, source.DetailCalls);
        Assert.NotNull(store.Read("test", "a")!.Detail);
    }

    [Fact]
    public async Task Concurrent_writes_preserve_all_search_relations()
    {
        var store = new JobStore(Database);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
            await new JobService([new Source(Vacancy(), true)], store).SearchAsync("test", new(Query: i.ToString()), default))));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM jobs"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM job_versions"));
        Assert.Equal(8L, Scalar("SELECT COUNT(*) FROM search_hits"));
    }

    [Fact]
    public void Newer_schema_is_rejected_without_rewriting_it()
    {
        _ = new JobStore(Database);
        Scalar("PRAGMA user_version=999");
        Assert.Throws<InvalidOperationException>(() => new JobStore(Database));
        Assert.Equal(999L, Scalar("PRAGMA user_version"));
    }

    [Fact]
    public void Failed_initial_migration_rolls_back_ddl_and_version()
    {
        Directory.CreateDirectory(_directory);
        Scalar("CREATE TABLE jobs(existing_column TEXT)");
        Assert.Throws<SqliteException>(() => new JobStore(Database));
        Assert.Equal(0L, Scalar("PRAGMA user_version"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name='sources'"));
        Assert.Equal("existing_column", Scalar("SELECT name FROM pragma_table_info('jobs')"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
