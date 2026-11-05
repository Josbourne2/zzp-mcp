using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using ZzpMcp;

namespace ZzpMcp.Tests;

public sealed class SourceTests
{
    private static string Fixture => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "harvey-nash.sanitized.json"));
    private static JsonObject Raw => JsonNode.Parse(Fixture)!["results"]![0]!["job"]!.AsObject();
    private static Job Map(JsonObject raw) => JobMapper.Map(JsonSerializer.SerializeToElement(raw), DateTimeOffset.Parse("2026-11-05T18:00:00Z"));
    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK, string media = "application/json") => new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, media) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request, cancellationToken); }
    private sealed class StalledStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    }
    private static HarveyNashSource Source(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action, TimeSpan? timeout = null) => new(new HttpClient(new Handler(action)) { Timeout = timeout ?? TimeSpan.FromSeconds(2) });

    [Fact]
    public async Task Fixture_maps_full_text_identity_client_and_deduplicates_by_id()
    {
        var root = JsonNode.Parse(Fixture)!;
        root["results"]!.AsArray().Add(root["results"]![0]!.DeepClone());
        var source = Source((_, _) => Task.FromResult(Response(root.ToJsonString())));
        var page = await source.SearchAsync(new(), 0, default);
        Assert.Equal(4, page.Consumed);
        Assert.Equal(3, page.Jobs.Length);
        var job = page.Jobs[0];
        Assert.Equal("Enexis", job.Client);
        Assert.Equal("harvey-nash:" + job.SourceId, job.Identity);
        Assert.Contains("Dit breng je mee", job.Text);
        Assert.Contains("- HBO", job.Text);
        Assert.NotEmpty(job.Requirements);
        Assert.Equal(Permission.NotAllowed, job.Zzp.Status);
        Assert.Equal(Permission.Unknown, job.Payroll.Status);
        Assert.Equal(78.85m, Assert.Single(job.Compensation).Low);
        Assert.Null(job.Compensation[0].Unit);
        Assert.Equal("excluded", job.Compensation[0].Vat);
        Assert.DoesNotContain("<strong>", job.Text);
    }

    [Theory]
    [InlineData("ZZP mogelijk: Nee", Permission.NotAllowed, false)]
    [InlineData("GEEN ZZP mogelijk, enkel detachering", Permission.NotAllowed, false)]
    [InlineData("Employment type: Interim", Permission.Unknown, false)]
    [InlineData("Algemene Inhuurvoorwaarden Zelfstandige Professionals", Permission.Unknown, false)]
    [InlineData("ZZP mogelijk: Ja", Permission.Allowed, false)]
    [InlineData("ZZP mogelijk: Nee\nZZP mogelijk: Ja", Permission.Unknown, true)]
    [InlineData("ZZP mogelijk: Nee\nAlgemene Inhuurvoorwaarden Zelfstandige Professionals", Permission.NotAllowed, false)]
    public void Contract_interpretation_is_evidenced_and_conservative(string text, Permission status, bool conflict)
    {
        var result = JobMapper.Contract(text, "zzp");
        Assert.Equal(status, result.Status); Assert.Equal(conflict, result.Conflict);
        if (status != Permission.Unknown) Assert.NotEmpty(result.Evidence);
        Assert.Equal(Permission.Unknown, JobMapper.Contract(text, "payroll").Status);
    }

    [Fact]
    public void Payroll_is_independent_from_zzp_and_detachering()
    {
        var text = "GEEN ZZP mogelijk, enkel detachering\nPayroll mogelijk: Ja";
        Assert.Equal(Permission.NotAllowed, JobMapper.Contract(text, "zzp").Status);
        Assert.Equal(Permission.Allowed, JobMapper.Contract(text, "payroll").Status);
    }

    [Theory]
    [InlineData("Richttarief 115 EUR all-in ex BTW", 115)]
    [InlineData("Tarief €78,85", 78.85)]
    [InlineData("Tarief €78.85", 78.85)]
    public void Rates_preserve_amount_and_do_not_invent_hourly_unit(string package, double amount)
    {
        var raw = Raw; raw["description"] = "<p>Andere functie</p>"; raw["salary_package"] = package;
        var money = Assert.Single(Map(raw).Compensation);
        Assert.Equal((decimal)amount, money.Low); Assert.Null(money.Unit); Assert.Equal("EUR", money.Currency);
    }

    [Fact]
    public void Zero_salary_and_negotiable_rate_remain_unknown()
    {
        var raw = Raw; raw["description"] = "<p>Andere functie</p>"; raw["salary_package"] = "Tarief in overleg";
        var money = Assert.Single(Map(raw).Compensation);
        Assert.Null(money.Low); Assert.Null(money.Unit); Assert.Equal("Tarief in overleg", money.Text);
        raw["salary_package"] = null;
        Assert.Empty(Map(raw).Compensation);
    }

    [Fact]
    public void Salary_range_preserves_bounds_and_time_unit()
    {
        var raw = Raw; raw["description"] = "<p>Functie</p>"; raw["salary_package"] = "€5415 - €7366 per maand + 13e maand en vakantiegeld";
        var money = Assert.Single(Map(raw).Compensation);
        Assert.Equal(5415, money.Low); Assert.Equal(7366, money.High); Assert.Equal("month", money.Unit); Assert.Equal("salary", money.Kind);
    }

    [Fact]
    public async Task Body_limit_is_an_explicit_error()
    {
        var source = Source((_, _) => Task.FromResult(Response(new string('x', 10 * 1024 * 1024 + 1))));
        var error = await Assert.ThrowsAsync<SourceException>(() => source.SearchAsync(new(), 0, default));
        Assert.Equal("response_too_large", error.Code);
    }

    [Fact]
    public async Task Timeout_also_bounds_a_body_that_stalls_after_headers()
    {
        var source = Source((_, _) => {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) };
            response.Content.Headers.ContentType = new("application/json");
            return Task.FromResult(response);
        }, TimeSpan.FromMilliseconds(40));
        var error = await Assert.ThrowsAsync<SourceException>(() => source.SearchAsync(new(), 0, default));
        Assert.Equal("timeout", error.Code);
    }

    [Fact]
    public async Task Unconfirmed_location_is_not_reported_as_applied()
    {
        var source = Source((_, _) => Task.FromResult(Response(Fixture)));
        var error = await Assert.ThrowsAsync<SourceException>(() => source.SearchAsync(new(Location: "Unknown"), 0, default));
        Assert.Equal("filter_not_applied", error.Code);
    }

    [Fact]
    public void Html_preserves_structure_links_and_ignores_executable_content()
    {
        var text = JobMapper.HtmlText("<h2>Eisen</h2><ul><li>C# &amp; SQL</li><li><a href='https://example.org/x'>Bijlage</a></li></ul><script>delete()</script><p>Laatste alinea</p>");
        Assert.Contains("- C# & SQL", text); Assert.Contains("https://example.org/x", text); Assert.EndsWith("Laatste alinea", text); Assert.DoesNotContain("delete()", text);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"results\":[],\"total_size\":\"no\"}")]
    [InlineData("{\"results\":[{}],\"total_size\":1}")]
    [InlineData("{\"results\":[{\"job\":{}}],\"total_size\":1}")]
    [InlineData("<html>login</html>")]
    [InlineData("{\"error\":\"failure\"}")]
    public async Task Malformed_200_is_not_a_successful_empty_list(string body)
    {
        var error = await Assert.ThrowsAsync<SourceException>(() => Source((_, _) => Task.FromResult(Response(body))).SearchAsync(new(), 0, default));
        Assert.Equal("source_schema", error.Code);
    }

    [Theory]
    [InlineData(500, "source_http")]
    [InlineData(429, "source_http")]
    [InlineData(401, "access_denied")]
    [InlineData(403, "access_denied")]
    public async Task Source_faults_are_distinct_from_empty(int status, string code)
    {
        var error = await Assert.ThrowsAsync<SourceException>(() => Source((_, _) => Task.FromResult(Response("{}", (HttpStatusCode)status))).SearchAsync(new(), 0, default));
        Assert.Equal(code, error.Code);
    }

    [Fact]
    public async Task Empty_is_a_valid_success()
    {
        var page = await Source((_, _) => Task.FromResult(Response("{\"results\":[],\"total_size\":0}"))).SearchAsync(new(), 0, default);
        Assert.Empty(page.Jobs); Assert.Equal(0, page.Total);
    }

    [Fact]
    public async Task Unknown_filter_is_not_ignored()
    {
        var fixture = JsonNode.Parse(Fixture)!;
        fixture["results"] = new JsonArray(fixture["results"]![0]!.DeepClone());
        var source = Source((_, _) => Task.FromResult(Response(fixture.ToJsonString())));
        var error = await Assert.ThrowsAsync<SourceException>(() => source.SearchAsync(new(Categories: new() { ["bogus"] = ["unknown"] }), 0, default));
        Assert.Equal("invalid_filter", error.Code);
    }

    [Fact]
    public async Task Cancellation_propagates_but_http_timeout_is_a_typed_fault()
    {
        var source = Source(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Response("{}"); }, TimeSpan.FromMilliseconds(40));
        var timeout = await Assert.ThrowsAsync<SourceException>(() => source.SearchAsync(new(), 0, default));
        Assert.Equal("timeout", timeout.Code);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.SearchAsync(new(), 0, canceled.Token));
    }

    [Fact]
    public async Task Request_converts_km_to_miles_without_sending_personal_profile()
    {
        var source = Source(async (message, _) => {
            var body = JsonNode.Parse(await message.Content!.ReadAsStringAsync())!["job_search"]!;
            Assert.Equal("Utrecht", body["location"]!["address"]!.ToString());
            Assert.Equal(5, body["location"]!["radius"]!.GetValue<double>(), 8);
            Assert.Equal("miles", body["location"]!["radius_units"]!.ToString());
            Assert.Equal("SQL", body["query"]!.ToString());
            Assert.Equal(2, body["offset"]!.GetValue<int>());
            var fixture = JsonNode.Parse(Fixture)!;
            fixture["metadata"] = new JsonObject { ["location_filters"] = new JsonArray(new JsonObject { ["location_type"] = "LOCALITY" }) };
            return Response(fixture.ToJsonString());
        });
        await source.SearchAsync(new(Query: "SQL", Location: "Utrecht", RadiusKm: 8.04672), 2, default);
    }

    [Fact]
    public async Task Details_use_exact_id_and_do_not_require_cached_search()
    {
        var source = Source((_, _) => Task.FromResult(Response(Fixture)));
        Assert.Equal(Raw["id"]!.ToString(), (await source.GetAsync(Raw["id"]!.ToString(), default)).SourceId);
    }

    [Fact]
    public async Task Premature_empty_detail_scan_is_incomplete_not_not_found()
    {
        var source = Source((_, _) => Task.FromResult(Response("{\"results\":[],\"total_size\":42}")));
        var error = await Assert.ThrowsAsync<SourceException>(() => source.GetAsync(Guid.NewGuid().ToString(), default));
        Assert.Equal("search_incomplete", error.Code);
    }

    [Fact]
    public async Task Complete_empty_detail_scan_is_not_found()
    {
        var source = Source((_, _) => Task.FromResult(Response("{\"results\":[],\"total_size\":0}")));
        var error = await Assert.ThrowsAsync<SourceException>(() => source.GetAsync(Guid.NewGuid().ToString(), default));
        Assert.Equal("not_found", error.Code);
    }
}
