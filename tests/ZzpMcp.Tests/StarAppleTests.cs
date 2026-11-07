using System.Net;
using System.Text.Json.Nodes;
using ZzpMcp;

namespace ZzpMcp.Tests;

public sealed class StarAppleTests
{
    private static string Html(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "starapple-" + name + ".sanitized.json")))!["html"]!.ToString();
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(action(request));
    }
    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> action) => new(new Handler(action));
    private static HttpResponseMessage Response(string html, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html") };

    [Fact]
    public void Native_pages_preserve_identities_and_do_not_invent_a_total()
    {
        var first = StarAppleSource.ParsePage(Html("list-1"), 1);
        var second = StarAppleSource.ParsePage(Html("list-2"), 2);
        var last = StarAppleSource.ParsePage(Html("list-18"), 18);
        Assert.Equal(20, first.Jobs.Length); Assert.Equal(20, second.Jobs.Length); Assert.Equal(4, last.Jobs.Length);
        Assert.Equal(18, first.Pages); Assert.Equal(18, second.Pages); Assert.Equal(18, last.Pages);
        Assert.Empty(first.Jobs.Select(j => j.SourceId).Intersect(second.Jobs.Select(j => j.SourceId)));
        Assert.All(first.Jobs, j => { Assert.Equal("starapple", j.Source); Assert.Empty(j.Text); Assert.Null(j.Client); });
    }

    [Fact]
    public void Changed_identity_pagination_and_block_pages_fail_explicitly()
    {
        Assert.Throws<SourceException>(() => StarAppleSource.ParsePage(Html("list-1"), 2));
        Assert.Throws<SourceException>(() => StarAppleSource.ParsePage("<h1>Login</h1>", 1));
        Assert.Throws<SourceException>(() => StarAppleSource.ParsePage(Html("list-1").Replace("/page/18/", "/page/51/"), 1));
        Assert.Throws<SourceException>(() => StarAppleSource.ParsePage(Html("list-1").Replace("https://www.starapple.nl/vacatures/medior-net-developer-17/", "https://evil.invalid/vacatures/medior-net-developer-17/"), 1));
        var first = StarAppleSource.ParsePage(Html("list-1"), 1).Jobs;
        Assert.Throws<SourceException>(() => StarAppleSource.ParsePage(Html("list-1").Replace(first[1].Url, first[0].Url), 1));
        Assert.Throws<SourceException>(() => StarAppleSource.ParseDetail("wrong-id", Html("detail")));
        Assert.Throws<SourceException>(() => StarAppleSource.ParseDetail("../bad", Html("detail")));
    }

    [Fact]
    public void Detail_preserves_all_vacancy_sections_and_salary_period_without_guessing_contract_or_employer()
    {
        var job = StarAppleSource.ParseDetail("senior-net-developer-18", Html("detail"));
        Assert.Equal("Senior .NET Developer", job.Title); Assert.Contains("Dordrecht", job.Locations);
        Assert.Equal("40 uur", job.Hours!.Text); Assert.Null(job.Client);
        Assert.Contains("Organisatie & Afdeling", job.Text); Assert.Contains("Functie eisen", job.Text); Assert.Contains("Aanbod", job.Text);
        Assert.Contains("SQL Server", job.Text); Assert.Contains("Eindejaarbonus", job.Text);
        Assert.Contains(job.Requirements, r => r.Contains("C#, .NET, Azure"));
        var salary = Assert.Single(job.Compensation);
        Assert.Equal(4000m, salary.Low); Assert.Equal(7000m, salary.High); Assert.Equal("month", salary.Unit); Assert.Equal("salary", salary.Kind);
        Assert.Contains("bruto per maand", salary.Evidence.Text);
        Assert.Equal(Permission.Unknown, job.Zzp.Status); Assert.Equal(Permission.Unknown, job.Payroll.Status);
        Assert.Null(job.PublishedAt); Assert.Null(job.ExpiresAt); Assert.Equal("{}", job.Raw.GetRawText());
        Assert.DoesNotContain("Formulier weggelaten", job.Text); Assert.DoesNotContain("Direct solliciteren", job.Text);
    }

    [Fact]
    public void Second_detail_variant_parses_salary_and_does_not_mistake_training_budget_for_salary()
    {
        var job = StarAppleSource.ParseDetail("medior-net-developer-17", Html("variant"));
        var salary = Assert.Single(job.Compensation);
        Assert.Equal(3000m, salary.Low); Assert.Equal(4000m, salary.High); Assert.Equal("month", salary.Unit);
        Assert.Contains("Opleidingsbudget", job.Text); Assert.Contains("Barendrecht", job.Locations);
    }

    [Fact]
    public void Open_application_is_explicit_and_missing_real_description_is_never_silently_accepted()
    {
        var job = StarAppleSource.ParseDetail("open-sollicitatie-5948", Html("open"));
        Assert.Equal("Open sollicitatie", job.Title); Assert.Contains(job.Warnings, w => w.Contains("geen concrete vacature"));
        Assert.Empty(job.Compensation); Assert.Empty(job.Requirements);
        Assert.Throws<SourceException>(() => StarAppleSource.ParseDetail("open-sollicitatie-5948", Html("open").Replace("Open sollicitatie", "Developer")));
    }

    [Fact]
    public void Explicit_payroll_permission_is_independent_of_zzp_exclusion()
    {
        var job = StarAppleSource.ParseDetail("senior-net-developer-18", Html("detail").Replace("<p>Deze organisatie", "<p>Geen ZZP toegestaan.</p><p>Payroll toegestaan.</p><p>Deze organisatie"));
        Assert.Equal(Permission.NotAllowed, job.Zzp.Status); Assert.Equal(Permission.Allowed, job.Payroll.Status);
        Assert.NotEmpty(job.Zzp.Evidence); Assert.NotEmpty(job.Payroll.Evidence);
    }

    [Fact]
    public void Salary_period_is_not_inferred_from_the_header_or_other_vacancies()
    {
        var job = StarAppleSource.ParseDetail("senior-net-developer-18", Html("detail").Replace("bruto per maand", "bruto"));
        Assert.Null(Assert.Single(job.Compensation).Unit);
    }

    [Fact]
    public async Task Search_crosses_native_boundary_and_returns_full_text_from_sqlite_without_refetching()
    {
        var calls = new List<string>();
        var db = Path.Combine(Path.GetTempPath(), "starapple-" + Guid.NewGuid().ToString("N"), "jobs.db");
        var store = new JobStore(db);
        var first = StarAppleSource.ParsePage(Html("list-1"), 1);
        var second = StarAppleSource.ParsePage(Html("list-2"), 2);
        foreach (var listing in first.Jobs.Concat(second.Jobs)) store.SaveDetail(listing with { Text = "Volledige opgeslagen vacaturetekst met C#." });
        using var source = new StarAppleSource(Client(r => { calls.Add(r.RequestUri!.AbsolutePath); return Response(Html(r.RequestUri.AbsolutePath.Contains("/2/") ? "list-2" : "list-1")); }), store);
        var result = await source.SearchAsync(new(PageSize: 3), 19, default);
        Assert.Equal(new[] { first.Jobs[19].SourceId, second.Jobs[0].SourceId, second.Jobs[1].SourceId }, result.Jobs.Select(j => j.SourceId));
        Assert.Equal(2, calls.Count); Assert.True(result.HasMore); Assert.Null(result.Total);
        Assert.All(result.Jobs, j => Assert.True(j.Storage!.CacheUsed));
        var service = new JobService([source], store);
        var cached = await service.GetAsync("starapple", first.Jobs[19].SourceId, default);
        Assert.True(cached.Storage!.CacheUsed); Assert.Equal(2, calls.Count);
        var empty = await service.SearchAsync("starapple", new(Query: "no-match-77z", PageSize: 1), default);
        Assert.Empty(empty.Jobs); Assert.NotNull(empty.NextCursor); Assert.Null(empty.LocalMatchTotal); Assert.Null(empty.SourceTotal);
    }

    [Fact]
    public async Task Collection_resumes_in_new_service_and_fetches_only_missing_details()
    {
        var db = Path.Combine(Path.GetTempPath(), "starapple-" + Guid.NewGuid().ToString("N"), "jobs.db");
        var store = new JobStore(db);
        var session = store.StartSession("StarApple hervatten");
        var first = StarAppleSource.ParsePage(Html("list-1"), 1);
        var finalHtml = Html("list-18").Replace("/page/18/", "/page/2/").Replace("/page/17/", "/page/1/").Replace("/page/3/", "/page/2/").Replace("/page/4/", "/page/2/").Replace("/page/5/", "/page/2/");
        var final = StarAppleSource.ParsePage(finalHtml, 2);
        foreach (var listing in first.Jobs.Concat(final.Jobs)) store.SaveDetail(listing with { Text = "Volledige bestaande tekst" });
        var requests = new List<string>();
        HttpClient Http() => Client(r => { requests.Add(r.RequestUri!.AbsolutePath); return Response(r.RequestUri.AbsolutePath == "/vacatures/" ? Html("list-1") : finalHtml); });
        using var source = new StarAppleSource(Http(), store);
        var run = await new JobService([source], store).CollectAsync(session.SessionId, "starapple", null, 1, default);
        Assert.False(run.Complete); Assert.Equal(20L, run.CollectedJobs); Assert.Equal(0, run.FetchedThisBatch);
        var restartedStore = new JobStore(db);
        using var restarted = new StarAppleSource(Http(), restartedStore);
        var done = await new JobService([restarted], restartedStore).CollectAsync(session.SessionId, "starapple", run.Collection.CollectionId, 1, default);
        Assert.True(done.Complete); Assert.Equal(24L, done.CollectedJobs); Assert.Equal(0, done.FetchedThisBatch);
        Assert.Equal(new[] { "/vacatures/", "/vacatures/page/2/" }, requests);
        Assert.Equal(0, done.Collection.RemovedJobs);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "access_denied")]
    [InlineData(HttpStatusCode.TooManyRequests, "source_http")]
    public async Task Access_errors_are_explicit_and_not_retried(HttpStatusCode status, string code)
    {
        var calls = 0;
        using var source = new StarAppleSource(Client(_ => { calls++; return Response("Blocked", status); }));
        Assert.Equal(code, (await Assert.ThrowsAsync<SourceException>(() => source.ReadCollectionPageAsync(0, default))).Code);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Native_last_page_has_no_cursor_and_unsupported_location_never_triggers_network()
    {
        var calls = 0;
        using var source = new StarAppleSource(Client(_ => { calls++; return Response(Html("list-18")); }));
        var page = await source.ReadCollectionPageAsync(17, default);
        Assert.False(page.HasMore); Assert.Null(page.Total); Assert.Equal(4, page.Consumed);
        Assert.Equal("unsupported_filter", (await Assert.ThrowsAsync<SourceException>(() => source.SearchAsync(new(Location: "Utrecht"), 0, default))).Code);
        Assert.Equal(1, calls);
    }
}
