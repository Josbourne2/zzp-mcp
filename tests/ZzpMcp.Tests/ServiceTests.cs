using System.Text.Json;
using ZzpMcp;

namespace ZzpMcp.Tests;

public sealed class ServiceTests
{
    private static Job Job(string id, Permission permission, bool conflict = false) => JobMapper.Map(JsonSerializer.SerializeToElement(new
    { id, title = "Job", description = "<p>Volledige tekst</p>", url_slug = id }), DateTimeOffset.UtcNow) with { Zzp = new(permission, [], conflict) };
    private sealed class FakeSource(Func<int, SourcePage> pages) : IJobSource
    {
        public string Id => "test";
        public object Capabilities => new { };
        public Task<SourcePage> SearchAsync(SearchRequest request, int offset, CancellationToken ct) => Task.FromResult(pages(offset));
        public Task<Job> GetAsync(string id, CancellationToken ct) => throw new NotImplementedException();
    }
    private static SourcePage Page(Job[] jobs, int total = 3, int consumed = 2) => new(jobs, total, consumed, [], DateTimeOffset.UtcNow);

    [Fact]
    public async Task Empty_local_page_keeps_cursor_and_later_match()
    {
        var service = new JobService([new FakeSource(offset => offset == 0 ? Page([Job("a", Permission.Unknown), Job("b", Permission.Unknown, true)]) : Page([Job("c", Permission.Allowed)], 3, 1))]);
        var request = new SearchRequest(Zzp: Permission.Allowed, PageSize: 2);
        var first = await service.SearchAsync("test", request, default);
        Assert.Empty(first.Jobs); Assert.NotNull(first.NextCursor); Assert.Equal(2, first.NextOffset); Assert.Null(first.LocalMatchTotal);
        var second = await service.SearchAsync("test", request with { Cursor = first.NextCursor }, default);
        Assert.Equal("c", Assert.Single(second.Jobs).SourceId); Assert.Null(second.NextCursor);
        var error = await Assert.ThrowsAsync<SourceException>(() => service.SearchAsync("test", request with { Query = "different", Cursor = first.NextCursor }, default));
        Assert.Equal("invalid_cursor", error.Code);
    }

    [Fact]
    public async Task Repeated_and_empty_pages_are_marked_incomplete()
    {
        var service = new JobService([new FakeSource(_ => Page([Job("a", Permission.Unknown)]))]);
        var first = await service.SearchAsync("test", new(PageSize: 2), default);
        var repeated = await service.SearchAsync("test", new(PageSize: 2, Cursor: first.NextCursor), default);
        Assert.True(repeated.Incomplete); Assert.Null(repeated.NextCursor);
        var empty = await new JobService([new FakeSource(_ => Page([], 3, 0))]).SearchAsync("test", new(), default);
        Assert.True(empty.Incomplete); Assert.Null(empty.NextCursor);
    }

    [Theory]
    [InlineData(401, "application/json", true)]
    [InlineData(403, "application/json", true)]
    [InlineData(302, "", true)]
    [InlineData(200, "text/html", true)]
    [InlineData(200, "application/json", false)]
    [InlineData(500, "application/json", false)]
    [InlineData(500, "text/html", false)]
    public void Session_requires_actual_protected_read_and_invalidates_auth_faults(int code, string type, bool needsLogin)
    {
        Assert.Equal(needsLogin, SessionProof.RequiresLogin(code, type));
        Assert.Equal(needsLogin, SessionProof.IsProtected(200, "application/json", code, type));
        Assert.False(SessionProof.IsProtected(401, "application/json", code, type));
    }

    [Theory]
    [InlineData("{\"content\":[{\"id\":\"a\"}]}", 1)]
    [InlineData("{\"data\":{\"items\":[]}}", 0)]
    [InlineData("[{\"id\":\"a\"},{\"id\":\"b\"}]", 2)]
    [InlineData("{\"error\":\"failure\"}", null)]
    [InlineData("{}", null)]
    [InlineData("[\"wrong shape\"]", null)]
    public void Protected_json_must_contain_a_job_collection(string json, int? expected)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(expected, SessionProof.JobCount(doc.RootElement));
    }
}
