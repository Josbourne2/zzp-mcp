using System.Text.Json;
using ZzpMcp;

namespace ZzpMcp.Tests;

public sealed class VertageTests
{
    private static JsonElement Fixture => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "vertage.sanitized.json"))).RootElement.Clone();
    private sealed class Session : IStriiveSession
    {
        public BrowserStatus Status { get; set; } = new("connected", true, "Connected");
        public string? Query { get; private set; }
        public Task<BrowserStatus> StartAsync(CancellationToken ct) => Task.FromResult(new BrowserStatus("awaiting_login", false, "Login"));
        public Task<BrowserStatus> CheckAsync(CancellationToken ct) => Task.FromResult(Status);
        public Task<BrowserStatus> CloseAsync(CancellationToken ct) => Task.FromResult(new BrowserStatus("disconnected", false, "Closed"));
        public Task<JsonElement> ReadJobsAsync(string queryString, CancellationToken ct) { Query = queryString; return Task.FromResult(JsonSerializer.SerializeToElement(new[] { Fixture })); }
        public Task<JsonElement> ReadJobAsync(string id, CancellationToken ct) => Task.FromResult(Fixture);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void Actual_fixture_maps_details_without_inventing_contract_permission()
    {
        var job = VertageMapper.Map(Fixture, DateTimeOffset.UtcNow, true);
        Assert.Equal("vertage", job.Source); Assert.Equal("395387", job.SourceId);
        Assert.Contains("C#", job.Text); Assert.Contains("C#", job.Skills); Assert.Equal("Den Haag", Assert.Single(job.Locations));
        Assert.Equal(75, Assert.Single(job.Compensation).Low); Assert.Equal(85, job.Compensation[0].High);
        Assert.Equal("hour", job.Compensation[0].Unit); Assert.Null(job.Compensation[0].Currency);
        Assert.Contains("24", job.Hours!.Text); Assert.Contains("36", job.Hours.Text);
        Assert.Equal(Permission.Unknown, job.Payroll.Status); Assert.Equal(Permission.Unknown, job.Zzp.Status);
        Assert.DoesNotContain("X-Amz-", job.Raw.GetRawText());
    }

    [Fact]
    public async Task Search_uses_native_query_location_and_page_and_keeps_unknown_total()
    {
        var session = new Session(); var source = new VertageSource(session);
        var result = await source.SearchAsync(new(Query: "C#", Location: "Utrecht", PageSize: 1), 2, default);
        Assert.Contains("page=2", session.Query); Assert.Contains("searchText=C%23", session.Query);
        Assert.Contains("locations=Utrecht", session.Query); Assert.Contains("useMatchingScore=false", session.Query);
        Assert.Null(result.Total); Assert.True(result.HasMore);
        var page = await new JobService([source]).SearchAsync("vertage", new(PageSize: 1), default);
        Assert.Null(page.SourceTotal); Assert.NotNull(page.NextCursor);
    }

    [Fact]
    public async Task Login_and_invalid_native_filters_are_explicit()
    {
        var session = new Session { Status = new("awaiting_login", false, "Login") };
        var source = new VertageSource(session);
        Assert.Equal("login_required", (await source.ConnectAsync(default)).State);
        Assert.Equal("unsupported_filter", (await Assert.ThrowsAsync<SourceException>(() => source.SearchAsync(new(Categories: new() { ["hn"] = ["value"] }), 0, default))).Code);
    }

    [Fact]
    public void Sanitizer_removes_signed_assets_and_account_fields_recursively()
    {
        var raw = JsonSerializer.SerializeToElement(new { id = 1, title = "Job", client = new { name = "Client", avatar = new { full = "https://example.org/logo?X-Amz-Security-Token=fake-test" } }, favorite = true,
            description = "<a href='https://example.org/file?signature=fake-test'>Bijlage</a>", requirements = new[] { new { requirement = "C#", token = "fake-test" } } });
        var clean = SourceSanitizer.Vacancy(raw).GetRawText();
        Assert.DoesNotContain("fake-test", clean); Assert.DoesNotContain("favorite", clean); Assert.DoesNotContain("avatar", clean);
        Assert.Contains("Client", clean); Assert.Contains("C#", clean);
    }
}
