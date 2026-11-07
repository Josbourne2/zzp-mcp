using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZzpMcp;

public abstract class PublicJobSource(HttpClient http, JobStore? store) : IJobSource, IDisposable
{
    protected JobStore? Store => store;
    public abstract string Id { get; }
    public abstract string Origin { get; }
    public virtual string Scope => "Openbare bronlijst, zonder profiel- of voorkeurfilters.";
    public bool SearchIncludesDetails => true;
    public virtual int CollectionPageSize => 1000;
    public object CollectionFilters => new { scope = Scope, limit = CollectionPageSize };
    public virtual object Capabilities => new {
        name = Id, connection = "anonymous_http", status = "configured_not_health_checked", collection = "collect_jobs bewaart de publieke lijst en haalt alleen ontbrekende details op; geen verwijderingen",
        scope = Scope, search = "Lokale letterlijke zoekterm op titel, opdrachtgever en volledige tekst van de opgehaalde pagina. Vervolg alle cursors, ook bij nul treffers; geen bronglobaal matchtotaal.",
        location = "Locatie-/kilometer- en categoriefilters niet geverifieerd: expliciet afgewezen.",
        detail = "Volledige tekst, bestaande details uit SQLite; cacheUsed=true is historisch.",
        pagination = "Lijstmetadata worden breed opgehaald; search_jobs deelt lokaal op. Nieuwe lijst per aanroep, geen snapshot. Maximaal 1000 identiteiten."
    };
    protected abstract Task<Job[]> ReadInventoryAsync(CancellationToken ct);
    protected abstract Task<Job> ReadDetailAsync(string id, CancellationToken ct);
    public async Task<SourcePage> SearchAsync(SearchRequest request, int offset, CancellationToken cancellationToken)
    {
        SearchValidation.Validate(request, offset);
        if (request.Categories is { Count: > 0 } || !string.IsNullOrWhiteSpace(request.Location))
            throw new SourceException("unsupported_filter", $"{Id}: locatie-/kilometer- en categoriefilters zijn niet vastgesteld. Verzamel deze bron zonder die filters en lees de volledige tekst.");
        var listingPage = await ReadSearchPageAsync(request.PageSize, offset, cancellationToken);
        var selected = listingPage.Jobs;
        var details = new List<Job>();
        foreach (var item in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cached = store?.Read(Id, item.SourceId)?.Detail;
            details.Add(cached is null ? await GetAsync(item.SourceId, cancellationToken) : cached with { Storage = new("saved", CacheUsed: true) });
        }
        return new(details.ToArray(), listingPage.Total, selected.Length, [], DateTimeOffset.UtcNow, listingPage.HasMore,
            [Scope, "Zoekterm wordt lokaal op de volledige teksten van alleen deze pagina toegepast; doorloop nextCursor ook bij nul treffers. Locaties zijn bronvelden, geen berekende afstand."], true, true);
    }
    protected virtual async Task<SourcePage> ReadSearchPageAsync(int pageSize, int offset, CancellationToken ct)
    {
        var inventory = ValidateInventory(await ReadInventoryAsync(ct));
        var selected = inventory.Skip(offset).Take(pageSize).ToArray();
        return new(selected, inventory.Length, selected.Length, [], DateTimeOffset.UtcNow, offset + selected.Length < inventory.Length);
    }
    public virtual async Task<SourcePage> ReadCollectionPageAsync(int page, CancellationToken cancellationToken)
    {
        if (page != 0) throw new SourceException("invalid_argument", $"{Id}: de begrensde openbare inventaris wordt in bronpagina 0 vastgelegd.");
        var jobs = ValidateInventory(await ReadInventoryAsync(cancellationToken));
        return new(jobs, jobs.Length, jobs.Length, [], DateTimeOffset.UtcNow, false, [Scope]);
    }
    private Job[] ValidateInventory(Job[] jobs)
    {
        if (jobs.Length > 1000) throw new SourceException("source_limit", $"{Id}: inventaris groter dan 1000; geen complete scan geclaimd.");
        if (jobs.Any(j => j.Source != Id || string.IsNullOrWhiteSpace(j.SourceId)) || jobs.Select(j => j.SourceId).Distinct().Count() != jobs.Length)
            throw new SourceException("source_schema", $"{Id}: ongeldige of dubbele identiteiten in de inventaris.");
        return jobs;
    }
    public async Task<Job> GetAsync(string id, CancellationToken cancellationToken)
    {
        ValidateId(id);
        var job = await ReadDetailAsync(id, cancellationToken);
        if (job.Source != Id || job.SourceId != id || string.IsNullOrWhiteSpace(job.Text))
            throw new SourceException("source_schema", $"{Id}: volledige tekst of gevraagde identiteit ontbreekt.");
        return job;
    }
    public virtual void ValidateId(string id) => PublicJobData.Slug(id);
    protected Task<string> GetTextAsync(string url, CancellationToken ct) => SendAsync(HttpMethod.Get, url, null, ct);
    protected Task<string> PostJsonAsync(string url, object body, CancellationToken ct) => SendAsync(HttpMethod.Post, url, Json.Write(body), ct);
    private async Task<string> SendAsync(HttpMethod method, string url, string? json, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            using var request = new HttpRequestMessage(method, url);
            if (json is not null) request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new SourceException("access_denied", $"{Id}: HTTP {(int)response.StatusCode}; toegang geweigerd, geen lege lijst. Geen automatische herhaling.");
            if ((int)response.StatusCode is >= 300 and < 400) throw new SourceException("source_redirect", $"{Id}: onverwachte doorverwijzing; geen login of nulresultaat geclaimd.");
            if (response.StatusCode == HttpStatusCode.NotFound) throw new SourceException("not_found", $"{Id}: bronpagina niet gevonden.");
            if (!response.IsSuccessStatusCode) throw new SourceException("source_http", $"{Id}: HTTP {(int)response.StatusCode}; geen automatische herhaling.");
            var type = response.Content.Headers.ContentType?.MediaType;
            if (type is not ("text/html" or "application/json")) throw new SourceException("source_schema", $"{Id}: onverwacht responsetype.");
            const int max = 10 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > max) throw new SourceException("response_too_large", $"{Id}: bronresponse groter dan 10 MiB.");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192]; int read;
            while ((read = await stream.ReadAsync(bytes, deadline.Token)) > 0)
            {
                if (buffer.Length + read > max) throw new SourceException("response_too_large", $"{Id}: bronresponse groter dan 10 MiB.");
                buffer.Write(bytes, 0, read);
            }
            var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
            return (string.IsNullOrWhiteSpace(charset) ? System.Text.Encoding.UTF8 : System.Text.Encoding.GetEncoding(charset)).GetString(buffer.ToArray());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new SourceException("timeout", $"{Id}: leesactie duurde langer dan 25 seconden."); }
        catch (HttpRequestException) { throw new SourceException("source_network", $"{Id}: netwerkfout bij bronleesactie."); }
        catch (System.IO.IOException) { throw new SourceException("source_network", $"{Id}: bronresponse afgebroken."); }
        catch (ArgumentException) { throw new SourceException("source_schema", $"{Id}: onbekende tekstcodering."); }
    }
    public void Dispose() => http.Dispose();
}

public static class PublicJobData
{
    public static void Slug(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 240 || !Regex.IsMatch(id, @"^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant))
            throw new SourceException("invalid_argument", "Gebruik de bron-ID uit de lijst; slashes, queryparameters en vrije URL's zijn niet toegestaan.");
    }
    public static JsonElement ParseJson(string text)
    {
        try { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }
        catch (JsonException) { throw new SourceException("source_schema", "Bron gaf ongeldige JSON."); }
    }
    public static IEnumerable<JsonElement> Objects(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            yield return node;
            foreach (var property in node.EnumerateObject())
                foreach (var item in Objects(property.Value)) yield return item;
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var child in node.EnumerateArray())
                foreach (var item in Objects(child)) yield return item;
    }
    public static JsonElement[] LinkedData(IDocument html) => html.QuerySelectorAll("script[type='application/ld+json']")
        .Select(e => ParseJson(e.TextContent)).SelectMany(Objects).ToArray();
    public static string? Text(JsonElement node, string key) => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    public static string? Scalar(JsonElement node, string key) => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : null;
    public static JsonElement Child(JsonElement node, string key) => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(key, out var value) ? value : default;
    public static int? Number(JsonElement node, string key) => Child(node, key) is var value && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : null;
    public static Fact? Fact(string field, string? text) => string.IsNullOrWhiteSpace(text) ? null : new(text.Trim(), new(field, text.Trim()));
    public static DateTimeOffset? Date(JsonElement node, string key)
    {
        var text = Text(node, key);
        // A date without offset does not silently acquire the execution host's timezone.
        return text is not null && Regex.IsMatch(text, @"T.*(?:Z|[+-]\d\d:\d\d)$") && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }
    public static Job Listing(string source, string id, string url, string title, string? client = null, string[]? locations = null) =>
        Create(source, id, url, title, "", client, locations ?? [], warnings: ["Lijstmetadata; volledige tekst vereist vóór matching."]);
    public static Job Create(string source, string id, string url, string title, string text, string? client = null, string[]? locations = null,
        Fact? hours = null, Fact? start = null, Fact? duration = null, Money[]? compensation = null, DateTimeOffset? published = null, DateTimeOffset? expires = null, string[]? warnings = null) =>
        new(source, id, source + ":" + id, url, title, client, Regex.Replace(text, @"https?://[^\s\""'<>]+", m => m.Value.Split('?')[0]), locations ?? [], hours, null, start, duration, [], [], compensation ?? [], null, published, null, expires,
            DateTimeOffset.UtcNow, JobMapper.Contract(text, "zzp"), JobMapper.Contract(text, "payroll"), [],
            warnings ?? ["Lees volledige tekst voor eisen en contractvormen; algemene broncodes zoals CONTRACTOR bewijzen geen ZZP-toestemming. Locaties zijn bronvermeldingen, geen berekende afstand."], JsonSerializer.SerializeToElement(new { }));
    public static string LinkId(string href, string origin, string prefix)
    {
        if (!Uri.TryCreate(new Uri(origin), href, out var url) || url.Scheme != "https" || url.Authority != new Uri(origin).Authority ||
            url.Query.Length > 0 || url.Fragment.Length > 0 || !url.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
            throw new SourceException("source_schema", "Onverwachte detail-URL in bronlijst.");
        var id = url.AbsolutePath[prefix.Length..].TrimEnd('/');
        try { Slug(id); } catch (SourceException) { throw new SourceException("source_schema", "Ongeldige bron-ID in detail-URL."); }
        return id;
    }
    public static Job FromLinkedData(string source, string id, string url, string html, string? bodySelector = null, string? extraSelector = null)
    {
        var document = new HtmlParser().ParseDocument(html);
        var postings = LinkedData(document).Where(j => Text(j, "@type") == "JobPosting").ToArray();
        if (postings.Length != 1 || document.QuerySelector("h1") is not { } heading)
            throw new SourceException("source_schema", "Detailpagina mist een unieke JobPosting of zichtbare opdrachttitel; mogelijk login of blokkade.");
        var posting = postings[0];
        var canonical = document.QuerySelector("link[rel=canonical]")?.GetAttribute("href") ?? Text(posting, "url");
        if (canonical is null || !Uri.TryCreate(canonical, UriKind.Absolute, out var actual) || actual.GetLeftPart(UriPartial.Path).TrimEnd('/') != url.TrimEnd('/'))
            throw new SourceException("source_schema", "Detailpagina verwijst niet naar de gevraagde opdracht.");
        if (string.IsNullOrWhiteSpace(Text(posting, "description"))) throw new SourceException("source_schema", "Volledige beschrijving ontbreekt in JobPosting.");
        var text = bodySelector is null ? JobMapper.HtmlText(Text(posting, "description")!) :
            string.Join("\n\n", document.QuerySelectorAll(bodySelector).Select(e => JobMapper.HtmlText(e.InnerHtml)));
        if (string.IsNullOrWhiteSpace(text)) throw new SourceException("source_schema", "Zichtbare detailtekst ontbreekt.");
        if (extraSelector is not null) text = string.Join("\n\n", document.QuerySelectorAll(extraSelector).Select(e => JobMapper.HtmlText(e.InnerHtml))) + "\n\n" + text;
        var location = Child(Child(posting, "jobLocation"), "address");
        var locations = new[] { Text(location, "addressLocality"), Text(location, "addressRegion") }.OfType<string>().Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
        var salary = Child(posting, "baseSalary"); var value = Child(salary, "value");
        decimal? Amount(string field) => decimal.TryParse(Scalar(value, field), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) && amount > 0 ? amount : null;
        var low = Amount("minValue"); var high = Amount("maxValue");
        var money = low.HasValue || high.HasValue ? new[] { new Money("rate", low, high, Text(salary, "currency"), Text(value, "unitText"), value.ToString(), null, null, new("baseSalary", value.ToString())) } : [];
        var title = heading.TextContent.Trim();
        if (title.Length == 0) throw new SourceException("source_schema", "Zichtbare titel is leeg.");
        if (Text(posting, "datePosted") is { } datePosted && Date(posting, "datePosted") is null)
            text += "\n\nPublicatiedatum (bron, zonder vastgesteld tijdstip): " + datePosted;
        if (Scalar(Child(posting, "identifier"), "value") is { } identifier && identifier != id)
            text += "\n\nBronidentifier (JSON-LD): " + identifier;
        return Create(source, id, url, title, text, Text(Child(posting, "hiringOrganization"), "name"), locations,
            start: Fact("jobStartDate", Text(posting, "jobStartDate")), compensation: money, published: Date(posting, "datePosted"), expires: Date(posting, "validThrough"));
    }
}
