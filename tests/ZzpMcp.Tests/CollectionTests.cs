using System.Text.Json;
using Microsoft.Data.Sqlite;
using ZzpMcp;

namespace ZzpMcp.Tests;

public sealed class CollectionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "zzp-collection-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_directory, "jobs.db");
    private static Job Vacancy(string id, string text = "Volledige detailtekst", string source = "test") =>
        JobMapper.Map(JsonSerializer.SerializeToElement(new { id, title = "Developer", description = text, url_slug = id }),
            DateTimeOffset.UtcNow.AddDays(-1)) with { Source = source, Identity = source + ":" + id };

    private sealed class Source(params string[][] pages) : IJobSource
    {
        public string Id => "test";
        public object Capabilities => new { };
        public int CollectionPageSize => 1000;
        public string Query { get; init; } = "";
        public object CollectionFilters => new { query = Query };
        public Dictionary<string, int> DetailCalls { get; } = [];
        public List<int> PagesRead { get; } = [];
        public int? FailedPage { get; set; }
        public string? FailedDetail { get; set; }
        public Task<SourcePage> SearchAsync(SearchRequest r, int offset, CancellationToken ct) => throw new NotImplementedException();
        public Task<SourcePage> ReadCollectionPageAsync(int page, CancellationToken ct)
        {
            PagesRead.Add(page);
            if (FailedPage == page) throw new SourceException("source_network", "Lijst mislukt.");
            var jobs = pages[page].Select(id => Vacancy(id, "Lijstfragment")).ToArray();
            return Task.FromResult(new SourcePage(jobs, null, jobs.Length, [], DateTimeOffset.UtcNow, page < pages.Length - 1));
        }
        public Task<Job> GetAsync(string id, CancellationToken ct)
        {
            DetailCalls[id] = DetailCalls.GetValueOrDefault(id) + 1;
            if (FailedDetail == id) throw new SourceException("source_network", "Detail mislukt.");
            return Task.FromResult(Vacancy(id));
        }
    }

    [Fact]
    public async Task Security_scan_preserves_dotnet_jobs_versions_search_history_and_review_progress()
    {
        var store = new JobStore(Database);
        var dotnet = store.StartSession(".NET");
        var dotnetService = new JobService([new Source(["dotnet"]) { Query = ".NET" }], store);
        var first = await dotnetService.CollectAsync(dotnet.SessionId, "test", null, 1, default);
        Assert.True(first.Complete);
        var detail = await dotnetService.GetAsync("test", "dotnet", default, dotnet.SessionId);
        var originalVersion = detail.Storage!.VersionId!.Value;
        store.MarkReviewed(dotnet.SessionId, "test", "dotnet", originalVersion);
        Sql("CREATE TRIGGER reject_shared_delete BEFORE DELETE ON jobs BEGIN SELECT RAISE(ABORT, 'shared job deletion'); END");

        var security = store.StartSession("Security Officer");
        var securityService = new JobService([new Source(["security"]) { Query = "Security Officer" }], store);
        var second = await securityService.CollectAsync(security.SessionId, "test", null, 1, default);
        Assert.True(second.Complete);
        Assert.Equal(0, second.Collection.RemovedJobs);
        Assert.Equal(originalVersion, store.Read("test", "dotnet")!.Version);
        Assert.Equal(detail.Text, store.Read("test", "dotnet")!.Detail!.Text);

        var dotnetProgress = store.GetSession(dotnet.SessionId);
        Assert.Equal("dotnet", Assert.Single(dotnetProgress.Jobs).Job.SourceId);
        Assert.Equal(1L, dotnetProgress.DetailsReturned);
        Assert.Equal(1L, dotnetProgress.CurrentVersionsReviewed);
        Assert.Equal(originalVersion, dotnetProgress.Jobs[0].ReviewedVersionId);
        Assert.Equal("security", Assert.Single(store.GetSession(security.SessionId).Jobs).Job.SourceId);
        Assert.Equal(1L, store.GetSession(dotnet.SessionId, "pages").SearchCount);
        Assert.Equal(2L, Sql("SELECT COUNT(*) FROM search_hits"));
        Assert.Equal(2L, Sql("SELECT COUNT(*) FROM job_versions"));
        Assert.Null(Sql("PRAGMA foreign_key_check"));
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
    public async Task Complete_listing_preserves_absent_jobs_details_and_other_sessions()
    {
        var store = new JobStore(Database);
        var session = store.StartSession();
        store.SaveDetail(Vacancy("old"), sessionId: session.SessionId);
        store.SaveDetail(Vacancy("old", source: "other"));
        store.SaveDetail(Vacancy("old"), "other-portal");
        store.SaveDetail(Vacancy("a"));
        var source = new Source(["a", "b"]);
        var service = new JobService([source], store);
        var result = await service.CollectAsync(session.SessionId, "test", null, 10, default);
        Assert.True(result.Complete);
        Assert.Equal(0, result.Collection.RemovedJobs);
        Assert.Equal("Volledige detailtekst", store.Read("test", "old")!.Detail!.Text);
        Assert.Equal(1L, store.GetSession(session.SessionId).DetailsReturned);
        Assert.NotNull(store.Read("other", "old"));
        Assert.NotNull(store.Read("test", "old", "other-portal"));
        Assert.DoesNotContain("a", source.DetailCalls.Keys);
        Assert.Equal(1, source.DetailCalls["b"]);
        Assert.Equal("Volledige detailtekst", store.Read("test", "a")!.Detail!.Text);
        Assert.Equal("Volledige detailtekst", store.Read("test", "b")!.Detail!.Text);
        Assert.Equal(2L, result.FullDetails);
        Assert.Equal(2L, Sql("SELECT COUNT(*) FROM search_hits WHERE matched=1"));
        Assert.Null(Sql("PRAGMA foreign_key_check"));
    }

    [Fact]
    public async Task Failed_or_interrupted_listing_does_not_remove_any_absent_jobs_and_resumes_after_restart()
    {
        var store = new JobStore(Database);
        store.SaveDetail(Vacancy("old"));
        var source = new Source(["a"], ["b"]) { FailedPage = 1 };
        var session = store.StartSession();
        var service = new JobService([source], store);
        var first = await service.CollectAsync(session.SessionId, "test", null, 1, default);
        Assert.False(first.Complete);
        Assert.NotNull(store.Read("test", "old"));
        await Assert.ThrowsAsync<SourceException>(() => service.CollectAsync(session.SessionId, "test", first.Collection.CollectionId, 1, default));
        Assert.NotNull(store.Read("test", "old"));
        source.FailedPage = null;
        var restartedStore = new JobStore(Database);
        var restarted = new JobService([source], restartedStore);
        var resumed = await restarted.CollectAsync(session.SessionId, "test", first.Collection.CollectionId, 1, default);
        Assert.True(resumed.Complete);
        Assert.Equal(new[] { 0, 1, 1 }, source.PagesRead);
        Assert.Equal(1, source.DetailCalls["a"]);
        Assert.NotNull(restartedStore.Read("test", "old")!.Detail);
    }

    [Fact]
    public async Task Detail_backlog_is_durable_and_successful_details_are_never_fetched_again()
    {
        var store = new JobStore(Database);
        var session = store.StartSession();
        var source = new Source(["a", "b", "c"]) { FailedDetail = "a" };
        var service = new JobService([source], store);
        var first = await service.CollectAsync(session.SessionId, "test", null, 2, default);
        Assert.False(first.Complete);
        Assert.Single(first.Errors);
        Assert.Equal(1L, first.FullDetails);
        var restarted = new JobService([source], new JobStore(Database));
        source.FailedDetail = null;
        var second = await restarted.CollectAsync(session.SessionId, "test", first.Collection.CollectionId, 2, default);
        Assert.True(second.Complete);
        Assert.Equal(2, source.DetailCalls["a"]);
        Assert.Equal(1, source.DetailCalls["b"]);
        Assert.Equal(1, source.DetailCalls["c"]);
        Assert.Single(source.PagesRead);
        var detail = await restarted.GetAsync("test", "b", default, session.SessionId);
        Assert.True(detail.Storage!.CacheUsed);
        Assert.Equal(1, source.DetailCalls["b"]);
        restarted.MarkReviewed(session.SessionId, "test", "b", detail.Storage.VersionId!.Value);
    }

    [Fact]
    public async Task Repeated_or_overlapping_pages_report_incomplete_collection()
    {
        var store = new JobStore(Database);
        store.SaveDetail(Vacancy("old"));
        var session = store.StartSession();
        var source = new Source(["a", "b"], ["b", "c"]);
        var service = new JobService([source], store);
        var first = await service.CollectAsync(session.SessionId, "test", null, 2, default);
        var second = await service.CollectAsync(session.SessionId, "test", first.Collection.CollectionId, 2, default);
        Assert.False(second.Complete);
        Assert.Equal("incomplete", second.Collection.State);
        Assert.NotNull(store.Read("test", "old"));
        Assert.Equal(0, second.Collection.RemovedJobs);
    }

    [Fact]
    public async Task Empty_successful_complete_listing_keeps_shared_jobs()
    {
        var store = new JobStore(Database);
        store.SaveDetail(Vacancy("old"));
        var session = store.StartSession();
        var result = await new JobService([new Source(Array.Empty<string>())], store).CollectAsync(session.SessionId, "test", null, 2, default);
        Assert.True(result.Complete);
        Assert.Equal(0, result.Collection.RemovedJobs);
        Assert.Equal(0L, result.CollectedJobs);
        Assert.NotNull(store.Read("test", "old")!.Detail);
    }

    [Fact]
    public void Collection_page_and_membership_roll_back_together_without_affecting_shared_jobs()
    {
        var store = new JobStore(Database);
        var session = store.StartSession();
        store.SaveDetail(Vacancy("old"));
        var run = store.StartCollection(session.SessionId, new Source(["a"]));
        store.SaveDetail(Vacancy("concurrent") with { FetchedAt = run.StartedAt.AddSeconds(1) });
        Sql("CREATE TRIGGER fail_hit BEFORE INSERT ON search_hits BEGIN SELECT RAISE(ABORT, 'test'); END");
        var page = new SourcePage([Vacancy("a")], null, 1, [], DateTimeOffset.UtcNow, false);
        Assert.Throws<SqliteException>(() => store.SaveCollectionPage(run, page, new { }));
        Assert.NotNull(store.Read("test", "old"));
        Assert.Equal(0, store.ReadCollection(run.CollectionId, session.SessionId).NextPage);
        Assert.Equal(0L, Sql("SELECT COUNT(*) FROM collection_members"));
        Sql("DROP TRIGGER fail_hit");
        var saved = store.SaveCollectionPage(run, page, new { });
        Assert.Equal(0, saved.RemovedJobs);
        Assert.NotNull(store.Read("test", "old")!.Detail);
        Assert.NotNull(store.Read("test", "concurrent"));
        Assert.Null(Sql("PRAGMA foreign_key_check"));
    }

    [Fact]
    public async Task Source_scan_can_be_recovered_without_id_after_a_first_request_failure()
    {
        var store = new JobStore(Database);
        var session = store.StartSession();
        var source = new Source(["a"]) { FailedPage = 0 };
        var service = new JobService([source], store);
        await Assert.ThrowsAsync<SourceException>(() => service.CollectAsync(session.SessionId, "test", null, 1, default));
        source.FailedPage = null;
        var result = await service.CollectAsync(session.SessionId, "test", null, 1, default);
        Assert.True(result.Complete);
        Assert.Equal(1L, Sql("SELECT COUNT(*) FROM collection_runs"));
    }

    [Fact]
    public void Migration_from_session_schema_preserves_existing_details()
    {
        var store = new JobStore(Database);
        store.SaveDetail(Vacancy("a"));
        Sql("DROP TABLE collection_members; DROP TABLE collection_runs; PRAGMA user_version=2;");
        var migrated = new JobStore(Database);
        Assert.NotNull(migrated.Read("test", "a")!.Detail);
        Assert.Equal(3L, Sql("PRAGMA user_version"));
    }

    [Fact]
    public void Older_collection_can_finish_details_after_another_sessions_complete_scan()
    {
        var store = new JobStore(Database);
        var session = store.StartSession();
        var first = store.StartCollection(session.SessionId, new Source(["a", "b"]));
        first = store.SaveCollectionPage(first, new([Vacancy("a"), Vacancy("b")], null, 2, [], DateTimeOffset.UtcNow, false), new { });
        var secondSession = store.StartSession();
        var second = store.StartCollection(secondSession.SessionId, new Source(["a"]));
        store.SaveCollectionPage(second, new([Vacancy("a")], null, 1, [], DateTimeOffset.UtcNow, false), new { });
        Assert.NotNull(store.Read("test", "b"));
        store.SaveCollectionDetail(first, Vacancy("a"));
        store.SaveCollectionDetail(first, Vacancy("b"));
        Assert.NotNull(store.Read("test", "b")!.Detail);
        Assert.True(store.CollectionStatus(first, 0, []).Complete);
    }

    [Fact]
    public void Failed_collection_migration_does_not_leave_half_a_schema()
    {
        var store = new JobStore(Database);
        store.SaveDetail(Vacancy("a"));
        Sql("DROP TABLE collection_members; DROP TABLE collection_runs; PRAGMA user_version=2; CREATE TABLE collection_members(existing_column TEXT);");
        Assert.Throws<SqliteException>(() => new JobStore(Database));
        Assert.Equal(2L, Sql("PRAGMA user_version"));
        Assert.Equal(0L, Sql("SELECT COUNT(*) FROM sqlite_master WHERE name='collection_runs'"));
        Assert.Equal(1L, Sql("SELECT COUNT(*) FROM jobs"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
