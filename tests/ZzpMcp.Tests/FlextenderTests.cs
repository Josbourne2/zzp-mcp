using System.Net;
using System.Text.Json;
using ZzpMcp;

namespace ZzpMcp.Tests;

public sealed class FlextenderTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "flextender-" + name + ".sanitized.json"));
    private static string ListJson => Fixture("list");
    private static string ListHtml => JsonDocument.Parse(ListJson).RootElement.GetProperty("resultHtml").GetString()!;
    private static string DetailHtml => JsonDocument.Parse(Fixture("detail")).RootElement.GetProperty("html").GetString()!;
    private const string Form = """
        <div class="widget_flextender_search_jobs"><form class="pc-search">
        <input type="hidden" name="kbs_flx_widget_config" value="fresh-opaque-config">
        <input type="hidden" name="action" value="kbs_flx_searchjobs">
        <input name="kbs_flx_joblsrc_freetext" value="">
        <input type="checkbox" name="region[]" value="opaque-unselected-region">
        </form></div>
        """;
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-06T12:00:00Z");
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => action(request);
    }
    private static HttpResponseMessage Response(string body, string media = "text/html", HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, media) };
    private static FlextenderSource Source(Func<HttpRequestMessage, Task<HttpResponseMessage>> action, JobStore? store = null) => new(new HttpClient(new Handler(action)), store);

    [Fact]
    public void Observed_list_includes_hidden_last_page_and_canonical_ids_without_result_token()
    {
        var jobs = FlextenderMapper.ReadList(ListHtml, At);
        Assert.Equal(2, jobs.Length);
        Assert.Equal("33782", jobs[0].SourceId);
        Assert.NotEqual(jobs[0].SourceId, jobs[1].SourceId);
        Assert.Contains("page_44", ListHtml);
        Assert.Equal("Gemeente Berg en Dal", jobs[0].Client);
        Assert.Equal("Gelderland", Assert.Single(jobs[0].Locations));
        Assert.Equal("24 uur", jobs[0].Hours!.Text);
        Assert.Equal("https://www.flextender.nl/opdracht/?aanvraagnr=33782", jobs[0].Url);
        Assert.Equal(Permission.Unknown, jobs[0].Zzp.Status);
        Assert.Equal("", jobs[0].Text);
        Assert.Equal("{}", jobs[0].Raw.GetRawText());
    }

    [Fact]
    public void Observed_detail_preserves_full_text_and_structured_fields_without_navigation()
    {
        var job = FlextenderMapper.ReadDetail(DetailHtml, "33782", At);
        Assert.Equal("Kwaliteitsmedewerker WMO/Jeugd", job.Title);
        Assert.Contains("gunningscriteria", job.Text);
        Assert.Contains("Uitvoeringsvoorwaarde", job.Text);
        Assert.Contains("12 november 2026", job.Text);
        Assert.DoesNotContain("Reageer op deze opdracht", job.Text);
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.FromHours(1)), job.ExpiresAt);
        Assert.Equal("Gelderland", Assert.Single(job.Locations));
        Assert.Equal("flextender:33782", job.Identity);
    }

    [Fact]
    public void Missing_text_wrong_identity_and_unrecognized_empty_list_are_errors()
    {
        Assert.Equal("source_schema", Assert.Throws<SourceException>(() => FlextenderMapper.ReadDetail(DetailHtml, "33780", At)).Code);
        Assert.Throws<SourceException>(() => FlextenderMapper.ReadDetail("<h1>Login</h1>", "33782", At));
        Assert.Throws<SourceException>(() => FlextenderMapper.ReadDetail(DetailHtml.Replace("flx-job-mce-container", "removed"), "33782", At));
        Assert.Throws<SourceException>(() => FlextenderMapper.ReadList("<p>Onderhoud</p>", At));
        Assert.Throws<SourceException>(() => FlextenderMapper.ReadList(ListHtml + ListHtml, At));
        Assert.Throws<SourceException>(() => FlextenderMapper.ReadList(ListHtml.Replace("https://www.flextender.nl/opdracht?", "https://other.example/opdracht?"), At));
        Assert.Empty(FlextenderMapper.ReadList("<h2>Er zijn geen opdrachten gevonden.</h2>", At));
    }

    [Fact]
    public void Form_uses_fresh_configuration_and_never_submits_unselected_encrypted_filters()
    {
        var fields = FlextenderMapper.ReadForm(Form, "C# & .NET").ToDictionary();
        Assert.Equal("fresh-opaque-config", fields["kbs_flx_widget_config"]);
        Assert.Equal("C# & .NET", fields["kbs_flx_joblsrc_freetext"]);
        Assert.False(fields.ContainsKey("region[]"));
        Assert.Throws<SourceException>(() => FlextenderMapper.ReadForm("<html>Blocked</html>", ""));
    }

    [Fact]
    public async Task Collection_reads_one_complete_list_without_fetching_details()
    {
        var requests = new List<string>();
        using var source = Source(async request => {
            requests.Add(request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Get) return Response(Form);
            var form = await request.Content!.ReadAsStringAsync();
            Assert.Contains("kbs_flx_widget_config=fresh-opaque-config", form);
            Assert.DoesNotContain("opaque-unselected-region", form);
            Assert.Contains("kbs_flx_joblsrc_freetext=&", form);
            return Response(ListJson, "application/json");
        });
        var page = await source.ReadCollectionPageAsync(0, default);
        Assert.Equal(2, page.Total); Assert.Equal(2, page.Consumed); Assert.False(page.HasMore);
        Assert.Equal(new[] { "/opdrachten/", "/wp-admin/admin-ajax.php" }, requests);
        Assert.Equal("invalid_argument", (await Assert.ThrowsAsync<SourceException>(() => source.ReadCollectionPageAsync(1, default))).Code);
    }

    [Fact]
    public async Task Search_fetches_full_text_once_and_reuses_durable_detail_cache()
    {
        var store = new JobStore(Path.Combine(Path.GetTempPath(), "flextender-test-" + Guid.NewGuid().ToString("N") + ".db"));
        var detailRequests = 0;
        using var source = Source(async request => {
            if (request.RequestUri!.AbsolutePath == "/opdrachten/") return Response(Form);
            if (request.Method == HttpMethod.Post)
            {
                Assert.Contains("kbs_flx_joblsrc_freetext=C%23+%26+.NET", await request.Content!.ReadAsStringAsync());
                return Response(ListJson, "application/json");
            }
            detailRequests++;
            return Response(DetailHtml);
        }, store);
        var service = new JobService([source], store);
        var first = await service.SearchAsync("flextender", new(Query: "C# & .NET", PageSize: 1), default);
        Assert.True(first.NextCursor is not null);
        Assert.Contains("gunningscriteria", Assert.Single(first.Details!).Text);
        var second = await service.SearchAsync("flextender", new(Query: "C# & .NET", PageSize: 1), default);
        Assert.True(Assert.Single(second.Details!).Storage!.CacheUsed);
        var cached = await service.GetAsync("flextender", "33782", default);
        Assert.True(cached.Storage!.CacheUsed);
        Assert.Equal(1, detailRequests);
    }

    [Fact]
    public async Task Invalid_filters_and_ids_fail_before_network_and_do_not_fake_geographic_coverage()
    {
        using var source = Source(_ => throw new Exception("No network expected"));
        Assert.Equal("unsupported_filter", (await Assert.ThrowsAsync<SourceException>(() => source.SearchAsync(new(Location: "Utrecht"), 0, default))).Code);
        Assert.Equal("unsupported_filter", (await Assert.ThrowsAsync<SourceException>(() => source.SearchAsync(new(Categories: new() { ["x"] = ["y"] }), 0, default))).Code);
        foreach (var id in new[] { "0", "-1", "01", "1&extra=x", "../login", "" })
            Assert.Equal("invalid_argument", (await Assert.ThrowsAsync<SourceException>(() => source.GetAsync(id, default))).Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "access_denied")]
    [InlineData(HttpStatusCode.Unauthorized, "access_denied")]
    [InlineData(HttpStatusCode.Redirect, "source_redirect")]
    [InlineData(HttpStatusCode.TooManyRequests, "source_http")]
    public async Task Access_failures_are_never_reported_as_empty_success(HttpStatusCode status, string code)
    {
        using var source = Source(_ => Task.FromResult(Response("Blocked", code: status)));
        Assert.Equal(code, (await Assert.ThrowsAsync<SourceException>(() => source.ReadCollectionPageAsync(0, default))).Code);
    }
}
