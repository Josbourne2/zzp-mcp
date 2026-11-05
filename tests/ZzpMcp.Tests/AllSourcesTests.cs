using System.Text.Json;
using ZzpMcp;

namespace ZzpMcp.Tests;

public sealed class AllSourcesTests
{
    private sealed class Source(string id, bool ready = true) : IJobSource
    {
        public string Id => id;
        public object Capabilities => new { };
        public bool Ready { get; set; } = ready;
        public string WaitingState { get; set; } = "login_required";
        public string? Error { get; set; }
        public int Searches { get; private set; }
        public SearchRequest? LastRequest { get; private set; }
        public Task<SourceConnection> ConnectAsync(CancellationToken ct) => Task.FromResult(new SourceConnection(Ready ? "ready" : WaitingState, Ready, "Login nodig"));
        public Task<SourcePage> SearchAsync(SearchRequest request, int offset, CancellationToken ct)
        {
            Searches++; LastRequest = request;
            if (Error is { } error) throw new SourceException(error, "Gecontroleerde bronfout");
            var job = JobMapper.Map(JsonSerializer.SerializeToElement(new { id = "job", title = "Job", description = "Volledig", url_slug = "job" }), DateTimeOffset.UtcNow) with { Source = id, Identity = id + ":job" };
            return Task.FromResult(new SourcePage([job], 1, 1, [], DateTimeOffset.UtcNow));
        }
        public Task<Job> GetAsync(string id, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Login_pauses_only_one_source_and_resume_preserves_query_and_finished_results()
    {
        var publicSource = new Source("public"); var privateSource = new Source("private", false);
        var service = new JobService([publicSource, privateSource]);
        var request = new SearchRequest(Query: "C#", Location: "Utrecht", RadiusKm: 15, PageSize: 3);
        var pending = await service.SearchAllAsync(request, default);
        Assert.False(pending.Complete); Assert.Single(pending.Jobs);
        var action = Assert.Single(pending.UserActions);
        Assert.Equal("private", action.Source); Assert.Equal(pending.SearchId, action.SearchId);
        Assert.Equal("resume_search", action.NextTool); Assert.Equal(0, privateSource.Searches);
        privateSource.Ready = true;
        var completed = await service.ResumeAsync(pending.SearchId, default);
        Assert.True(completed.Complete); Assert.Equal(2, completed.Jobs.Length); Assert.Empty(completed.UserActions);
        Assert.Equal(request, privateSource.LastRequest); Assert.Equal(1, publicSource.Searches);
        await service.ResumeAsync(pending.SearchId, default);
        Assert.Equal(1, privateSource.Searches); // Repeated resume is idempotent for completed sources.
    }

    [Fact]
    public async Task A_failing_source_is_not_a_successful_empty_list_and_can_be_retried()
    {
        var broken = new Source("broken") { Error = "source_network" };
        var service = new JobService([new Source("ok"), broken]);
        var result = await service.SearchAllAsync(new(), default);
        Assert.False(result.Complete); Assert.Single(result.Jobs);
        Assert.Equal("error", result.Sources.Single(x => x.Source == "broken").State);
        broken.Error = null;
        Assert.True((await service.ResumeAsync(result.SearchId, default)).Complete);
    }

    [Fact]
    public async Task Connecting_does_not_ask_for_unnecessary_human_login()
    {
        var source = new Source("private", false) { WaitingState = "connecting" };
        var result = await new JobService([source]).SearchAllAsync(new(), default);
        Assert.Empty(result.UserActions); Assert.Equal("connecting", Assert.Single(result.Sources).State);
    }

    [Fact]
    public async Task Session_expiry_during_actual_search_returns_a_login_action()
    {
        var source = new Source("private") { Error = "login_required" };
        var service = new JobService([source]);
        var result = await service.SearchAllAsync(new(), default);
        Assert.Single(result.UserActions); Assert.False(result.Complete);
        source.Error = null;
        Assert.True((await service.ResumeAsync(result.SearchId, default)).Complete);
    }

    [Fact]
    public async Task Invalid_filters_do_not_open_connections_and_unknown_resume_is_explicit()
    {
        var service = new JobService([new Source("ok")]);
        Assert.Equal("invalid_argument", (await Assert.ThrowsAsync<SourceException>(() => service.SearchAllAsync(new(PageSize: 0), default))).Code);
        Assert.Equal("unsupported_filter", (await Assert.ThrowsAsync<SourceException>(() => service.SearchAllAsync(new(Categories: new() { ["native"] = ["id"] }), default))).Code);
        Assert.Equal("search_expired", (await Assert.ThrowsAsync<SourceException>(() => service.ResumeAsync("unknown", default))).Code);
    }
}
