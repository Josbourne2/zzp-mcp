using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using ZzpMcp;

namespace ZzpMcp.Tests;

public sealed class PublicSourcesTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".sanitized.json"));
    private static string Html(string name) => JsonNode.Parse(Fixture(name))!["html"]!.ToString();
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => action(request);
    }
    private static HttpClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) => new(new Handler(action));
    private static HttpResponseMessage Response(string content, string type = "text/html", HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(content, System.Text.Encoding.UTF8, type) };
    private static PublicJobSource Source(string id, HttpClient http, JobStore? store = null) => id switch {
        "freep" => new FreepSource(http, store), "mipublic" => new MiPublicSource(http, store), "magnit" => new MagnitSource(http, store),
        "liblab" => new LibLabSource(http, store), "werken-in-gelderland" => new WerkenInGelderlandSource(http, store), _ => throw new ArgumentException(id)
    };
    private static string MagnitList(string? id = null, string? token = null, int total = 1)
    {
        var detail = JsonNode.Parse(Fixture("magnit-detail"))!;
        var listing = new JsonObject { ["id"] = id ?? detail["id"]!.ToString(), ["title"] = detail["title"]!.ToString(),
            ["location"] = "'S-Hertogenbosch, NLD", ["clientInfo"] = new JsonObject { ["name"] = "Gemeente" } };
        return new JsonObject { ["jobs"] = new JsonArray(listing), ["totalCount"] = total, ["continuationToken"] = token }.ToJsonString();
    }
    private static Task<HttpResponseMessage> Fake(HttpRequestMessage r)
    {
        var path = r.RequestUri!.AbsolutePath;
        return Task.FromResult(r.RequestUri.Host switch {
            "www.freep.nl" => Response(Html(path == "/" ? "freep-list" : "freep-detail")),
            "mipublic.nl" => Response(Html(path == "/interim-opdrachten" ? "mipublic-list" : "mipublic-detail")),
            "liblab.nl" => Response(Html(path == "/onze-opdrachten/" ? "liblab-list-" + (r.RequestUri.Query.Contains("2") ? "2" : "1") : "liblab-detail")),
            "www.werkeningelderland.nl" => Response(Html(path.StartsWith("/inhuur/") ? "gelderland-list-" + (path.Contains("/2/") ? "2" : "1") : "gelderland-detail")),
            _ => Response(r.Method == HttpMethod.Post ? MagnitList() : Fixture("magnit-detail"), "application/json")
        });
    }
    public static TheoryData<string, string> Sources => new() {
        { "freep", "104289" }, { "mipublic", "test-automation-engineer-java-developer-met-testfocus" },
        { "magnit", "7c249ea8-12d6-415c-8b30-32becfb36415" }, { "liblab", "allround-medewerker-informatiebeheer" },
        { "werken-in-gelderland", "kwaliteitsmedewerker-wmo-jeugd" }
    };

    [Theory, MemberData(nameof(Sources))]
    public async Task Each_public_source_collects_metadata_and_returns_full_text(string sourceId, string expectedId)
    {
        using var source = Source(sourceId, Client(Fake));
        var inventory = await source.ReadCollectionPageAsync(0, default);
        Assert.Equal(sourceId == "liblab", inventory.HasMore); Assert.NotEmpty(inventory.Jobs); Assert.All(inventory.Jobs, j => Assert.Empty(j.Text));
        var search = await source.SearchAsync(new(PageSize: 1), 0, default);
        Assert.True(search.IncludeDetails); Assert.True(search.QueryIsLocal);
        var job = Assert.Single(search.Jobs);
        Assert.Equal(sourceId, job.Source); Assert.Equal(expectedId, job.SourceId);
        Assert.True(job.Text.Length > 1000);
        Assert.Equal("{}", job.Raw.GetRawText());
        Assert.DoesNotContain("cookie", job.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory, MemberData(nameof(Sources))]
    public async Task Every_source_uses_durable_detail_cache_without_refetching(string sourceId, string expectedId)
    {
        var requests = new List<string>();
        var database = Path.Combine(Path.GetTempPath(), "zzp-public-" + Guid.NewGuid().ToString("N") + ".db");
        var store = new JobStore(database);
        using var source = Source(sourceId, Client(r => { requests.Add(r.RequestUri!.AbsoluteUri); return Fake(r); }), store);
        var service = new JobService([source], store);
        var first = await service.SearchAsync(sourceId, new(PageSize: 1), default);
        var detailUrl = first.Jobs[0].Url;
        var before = requests.Count;
        var cached = await service.GetAsync(sourceId, expectedId, default);
        Assert.Equal(before, requests.Count); Assert.True(cached.Storage!.CacheUsed);
        var originalFetchedAt = cached.FetchedAt;
        var second = await service.SearchAsync(sourceId, new(PageSize: 1), default);
        var detail = Assert.Single(second.Details!);
        Assert.True(detail.Storage!.CacheUsed); Assert.Equal(originalFetchedAt, detail.FetchedAt);
        if (sourceId != "magnit") Assert.Equal(1, requests.Count(u => u == detailUrl));
        else Assert.Equal(1, requests.Count(u => u.EndsWith("/details")));
        var freshService = new JobService([source], new JobStore(database));
        Assert.True((await freshService.GetAsync(sourceId, expectedId, default)).Storage!.CacheUsed);
    }

    [Fact]
    public async Task Local_query_uses_full_text_and_empty_page_keeps_its_cursor_and_unknown_match_total()
    {
        using var source = new FreepSource(Client(Fake));
        var service = new JobService([source]);
        var result = await service.SearchAsync("freep", new(Query: ".NET", PageSize: 1), default);
        Assert.Single(result.Jobs); Assert.Contains("Sharepoint", result.Details![0].Text, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.LocalMatchTotal); Assert.Equal(2, result.SourceTotal);
        Assert.Contains(".NET", Json.Write(result.AppliedLocalFilters));
        Assert.DoesNotContain(".NET", Json.Write(result.AppliedSourceFilters));
        var empty = await service.SearchAsync("freep", new(Query: "nonexistent-753bz", PageSize: 1), default);
        Assert.Empty(empty.Jobs); Assert.Equal(1, empty.ConsumedSourceRecords); Assert.NotNull(empty.NextCursor);
        Assert.False(empty.Incomplete); Assert.Null(empty.LocalMatchTotal);
    }

    [Fact]
    public void Freep_references_are_dynamic_and_zero_rate_or_contractor_does_not_mean_zzp_allowed()
    {
        var list = FreepSource.ParseInventory(Html("freep-list"));
        Assert.Equal("104289", list[0].SourceId); Assert.Contains("full-stack", list[0].Url);
        var job = PublicJobData.FromLinkedData("freep", "104289", list[0].Url, Html("freep-detail"), "div.prose-base");
        Assert.Equal("Full stack ontwikkelaar", job.Title); Assert.Empty(job.Compensation);
        Assert.NotEqual(Permission.Allowed, job.Zzp.Status);
        Assert.Contains("Angular", job.Text); Assert.Contains("Wensen", job.Text);
        Assert.Throws<SourceException>(() => FreepSource.ParseInventory("<h1>Blocked</h1>"));
        Assert.Throws<SourceException>(() => FreepSource.ParseInventory(Html("freep-list").Replace("[2, 8]", "[99999, 8]")));
    }

    [Fact]
    public void MiPublic_requires_complete_item_list_and_preserves_requirements_and_actual_place()
    {
        Assert.Equal(2, MiPublicSource.ParseInventory(Html("mipublic-list")).Length);
        Assert.Throws<SourceException>(() => MiPublicSource.ParseInventory(Html("mipublic-list").Replace("\"numberOfItems\": 2", "\"numberOfItems\": 3")));
        Assert.Throws<SourceException>(() => MiPublicSource.ParseInventory(Html("mipublic-list").Replace("https://mipublic.nl/vacature/", "https://other.example/vacature/")));
        var data = JsonNode.Parse(Fixture("mipublic-detail"))!;
        var job = PublicJobData.FromLinkedData("mipublic", data["id"]!.ToString(), data["url"]!.ToString(), data["html"]!.ToString());
        Assert.Contains("De eisen", job.Text); Assert.Contains("De wensen", job.Text);
        Assert.Contains("Den Haag", job.Locations); Assert.Equal(100, Assert.Single(job.Compensation).High);
        Assert.Throws<SourceException>(() => PublicJobData.FromLinkedData("mipublic", "wrong", "https://mipublic.nl/vacature/wrong", data["html"]!.ToString()));
    }

    [Fact]
    public async Task Magnit_cursor_is_consumed_and_missing_repeated_or_changed_pages_never_complete()
    {
        var calls = 0;
        using var source = new MagnitSource(Client(async r => {
            var body = JsonNode.Parse(await r.Content!.ReadAsStringAsync())!;
            Assert.Equal(100, body["pageSize"]!.GetValue<int>());
            calls++;
            if (calls == 1) { Assert.Null(body["continuationToken"]); return Response(MagnitList(token: "cursor-test", total: 2), "application/json"); }
            Assert.Equal("cursor-test", body["continuationToken"]!.ToString());
            return Response(MagnitList("00000000-0000-0000-0000-000000000002", total: 2), "application/json");
        }));
        Assert.Equal(2, (await source.ReadCollectionPageAsync(0, default)).Jobs.Length); Assert.Equal(2, calls);
        using var repeated = new MagnitSource(Client(_ => Task.FromResult(Response(MagnitList(token: "same", total: 2), "application/json"))));
        Assert.Equal("source_repeated_page", (await Assert.ThrowsAsync<SourceException>(() => repeated.ReadCollectionPageAsync(0, default))).Code);
        using var shortList = new MagnitSource(Client(_ => Task.FromResult(Response(MagnitList(total: 2), "application/json"))));
        Assert.Equal("source_schema", (await Assert.ThrowsAsync<SourceException>(() => shortList.ReadCollectionPageAsync(0, default))).Code);
    }

    [Fact]
    public async Task Magnit_details_keep_extra_requirements_but_exclude_assets_and_external_proposal_urls()
    {
        var raw = JsonNode.Parse(Fixture("magnit-detail"))!;
        raw["requiredSkillsAndEducation"] = "<p>Additional mandatory requirement</p>";
        raw["technicalDetails"]!["externalProposalUrl"] = "https://external.example/?token=secret";
        var job = MagnitSource.Map(PublicJobData.ParseJson(raw.ToJsonString()), true);
        Assert.Contains("Additional mandatory requirement", job.Text);
        Assert.Contains("candidateRequirements", job.Text); Assert.Contains("requestId", job.Text);
        Assert.DoesNotContain("secret", job.Text); Assert.DoesNotContain("smallLogoUrl", job.Text);
        using var source = new MagnitSource(Client(_ => Task.FromResult(Response(raw.ToJsonString(), "application/json"))));
        Assert.Equal("source_schema", (await Assert.ThrowsAsync<SourceException>(() => source.GetAsync("00000000-0000-0000-0000-000000000002", default))).Code);
    }

    [Fact]
    public void Html_pagers_and_missing_detail_markup_are_not_treated_as_success()
    {
        Assert.Equal(2, LibLabSource.ParsePage(Html("liblab-list-1"), 1).Total);
        Assert.Throws<SourceException>(() => LibLabSource.ParsePage(Html("liblab-list-1"), 2));
        Assert.Throws<SourceException>(() => LibLabSource.ParsePage("<h1>Login</h1>", 1));
        var page = WerkenInGelderlandSource.ParsePage(Html("gelderland-list-1"));
        Assert.Equal(2, page.Total); Assert.True(page.HasNext(2));
        Assert.Throws<SourceException>(() => WerkenInGelderlandSource.ParsePage("<main class='page-content'>Onderhoud</main>"));
    }

    [Fact]
    public void Gelderland_supports_the_observed_link_around_the_entire_card()
    {
        var html = """
            <main class="page-content">Er zijn 1 opdrachten gevonden
            <a href="https://www.werkeningelderland.nl/opdracht/kwaliteitsmedewerker-wmo-jeugd">
            <article class="vacancy vacancy--large"><h1 class="vacancy__title">Kwaliteitsmedewerker WMO/Jeugd</h1></article></a></main>
            """;
        Assert.Equal("kwaliteitsmedewerker-wmo-jeugd", Assert.Single(WerkenInGelderlandSource.ParsePage(html).Jobs).SourceId);
    }

    [Fact]
    public async Task LibLab_native_pages_keep_different_cached_totals_explicit_instead_of_faking_one_snapshot()
    {
        using var source = new LibLabSource(Client(r => Task.FromResult(Response(r.RequestUri!.Query.Length == 0 ? Html("liblab-list-1") :
            Html("liblab-list-2").Replace("\"total_rows\": 2", "\"total_rows\": 3").Replace("\"total_pages\": 2", "\"total_pages\": 3")))));
        var first = await source.ReadCollectionPageAsync(0, default);
        var second = await source.ReadCollectionPageAsync(1, default);
        Assert.Equal(2, first.Total); Assert.Equal(3, second.Total); Assert.True(second.HasMore);
        Assert.NotEqual(first.Jobs[0].SourceId, second.Jobs[0].SourceId);
        Assert.Contains(second.Warnings!, w => w.Contains("geen snapshot"));
    }

    [Theory, MemberData(nameof(Sources))]
    public async Task Unsupported_filters_and_invalid_ids_do_not_make_network_requests(string id, string unused)
    {
        using var source = Source(id, Client(_ => throw new Exception("No requests expected")));
        source.ValidateId(unused);
        Assert.Equal("unsupported_filter", (await Assert.ThrowsAsync<SourceException>(() => source.SearchAsync(new(Location: "Utrecht"), 0, default))).Code);
        foreach (var invalid in new[] { "../login", "https://other.example/", "id?token=x", "" })
            Assert.Equal("invalid_argument", (await Assert.ThrowsAsync<SourceException>(() => source.GetAsync(invalid, default))).Code);
        Assert.Equal("invalid_argument", (await Assert.ThrowsAsync<SourceException>(() => source.ReadCollectionPageAsync(-1, default))).Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "access_denied")]
    [InlineData(HttpStatusCode.TooManyRequests, "source_http")]
    [InlineData(HttpStatusCode.Redirect, "source_redirect")]
    public async Task Public_http_failures_are_explicit_and_never_retried(HttpStatusCode status, string code)
    {
        var calls = 0;
        using var source = new FreepSource(Client(_ => { calls++; return Task.FromResult(Response("Blocked", status: status)); }));
        Assert.Equal(code, (await Assert.ThrowsAsync<SourceException>(() => source.ReadCollectionPageAsync(0, default))).Code);
        Assert.Equal(1, calls);
    }
}
