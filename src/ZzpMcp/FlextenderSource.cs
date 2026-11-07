using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace ZzpMcp;

/// <summary>Uses the public site's observed form/Ajax flow; no supplier credentials or copied tokens.</summary>
public sealed class FlextenderSource(HttpClient http, JobStore? store = null) : IJobSource, IDisposable
{
    public const string Origin = "https://www.flextender.nl";
    public string Id => "flextender";
    public bool SearchIncludesDetails => true;
    public int CollectionPageSize => 1000;
    public object CollectionFilters => new { scope = "Publieke, ongefilterde Flextender-opdrachtenlijst; alle client-side pagina's uit één Ajax-response." };
    public object Capabilities => new {
        name = "Flextender", connection = "public_website_form", search = new[] { "query", "zzp (local)", "payroll (local)" },
        detail = "Iedere zoektreffer krijgt volledige tekst. Bestaande details worden uit SQLite hergebruikt.",
        collection = "collect_jobs(source=flextender) bewaart alle publieke lijstidentiteiten en haalt alleen ontbrekende details op.",
        pagination = "De website levert alle client-side pagina's tegelijk; search_jobs deelt deze lijst lokaal op (maximaal 1000 records).",
        querySemantics = "Zoekterm gaat naar het publieke websiteformulier; volledige-tekstdekking van de bronzoekfunctie is niet vastgesteld. Verzamel breed vóór inhoudelijk filteren.",
        location = "Locatie en kilometerstraal niet ondersteund; geen provincie- of tekstfilter als afstandsfilter gepresenteerd.",
        supplierPortal = "De ingelogde /supplier/jobs/all-zoekfunctie wordt niet gebruikt. Geen login nodig voor deze publieke route."
    };

    public async Task<SourcePage> SearchAsync(SearchRequest request, int offset, CancellationToken cancellationToken)
    {
        SearchValidation.Validate(request, offset);
        if (request.Categories is { Count: > 0 }) throw new SourceException("unsupported_filter", "Flextender ondersteunt geen categorie-ID's via deze adapter.");
        if (!string.IsNullOrWhiteSpace(request.Location))
            throw new SourceException("unsupported_filter", "Flextender heeft via deze route geen geverifieerd locatie-/kilometerfilter. Zoek deze bron zonder location; beoordeel locaties in de volledige tekst.");
        var listings = await ReadListAsync(request.Query, cancellationToken);
        var slice = listings.Skip(offset).Take(request.PageSize).ToArray();
        var details = new List<Job>();
        foreach (var listing in slice)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cached = store?.Read(Id, listing.SourceId)?.Detail;
            details.Add(cached is null ? await GetAsync(listing.SourceId, cancellationToken) : cached with { Storage = new("saved", CacheUsed: true) });
        }
        return new(details.ToArray(), listings.Length, slice.Length, [], DateTimeOffset.UtcNow,
            offset + slice.Length < listings.Length,
            ["Publieke bronzoekfunctie; dekking op volledige tekst niet vastgesteld. Gebruik collect_jobs zonder zoekfilter voor brede verzameling.",
             "Bronpagina's zijn client-side; vervolgaanroepen lezen een nieuwe lijst en vormen geen snapshot."], IncludeDetails: true);
    }

    public async Task<SourcePage> ReadCollectionPageAsync(int page, CancellationToken cancellationToken)
    {
        if (page != 0) throw new SourceException("invalid_argument", "De Flextender-lijst wordt volledig in bronpagina 0 geleverd.");
        var jobs = await ReadListAsync("", cancellationToken);
        return new(jobs, jobs.Length, jobs.Length, [], DateTimeOffset.UtcNow, false);
    }

    private async Task<Job[]> ReadListAsync(string query, CancellationToken ct)
    {
        using var overview = new HttpRequestMessage(HttpMethod.Get, Origin + "/opdrachten/");
        var fields = FlextenderMapper.ReadForm(await ReadAsync(overview, "text/html", ct), query);
        // Fresh successful form controls, including the opaque widget config, are forwarded only to this fixed origin.
        using var search = new HttpRequestMessage(HttpMethod.Post, Origin + "/wp-admin/admin-ajax.php") { Content = new FormUrlEncodedContent(fields) };
        search.Headers.Referrer = new Uri(Origin + "/opdrachten/");
        var json = await ReadAsync(search, "application/json", ct);
        try
        {
            using var response = JsonDocument.Parse(json);
            if (response.RootElement.ValueKind != JsonValueKind.Object || !response.RootElement.TryGetProperty("resultHtml", out var html) || html.ValueKind != JsonValueKind.String)
                throw new SourceException("source_schema", "Flextender-response mist resultHtml.");
            return FlextenderMapper.ReadList(html.GetString()!, DateTimeOffset.UtcNow);
        }
        catch (JsonException) { throw new SourceException("source_schema", "Flextender gaf ongeldige Ajax-JSON."); }
    }

    public async Task<Job> GetAsync(string id, CancellationToken cancellationToken)
    {
        id = FlextenderMapper.ValidateId(id);
        using var request = new HttpRequestMessage(HttpMethod.Get, Origin + "/opdracht/?aanvraagnr=" + id);
        return FlextenderMapper.ReadDetail(await ReadAsync(request, "text/html", cancellationToken), id, DateTimeOffset.UtcNow);
    }

    private async Task<string> ReadAsync(HttpRequestMessage request, string mediaType, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new SourceException("access_denied", "Flextender weigert publieke toegang; dit is geen lege opdrachtenlijst.");
            if (response.StatusCode == HttpStatusCode.NotFound) throw new SourceException("not_found", "Flextender-pagina niet gevonden.");
            if ((int)response.StatusCode is >= 300 and < 400) throw new SourceException("source_redirect", "Flextender verwees naar een andere pagina; geen automatische login of nulresultaat.");
            if (!response.IsSuccessStatusCode) throw new SourceException("source_http", $"Flextender gaf HTTP {(int)response.StatusCode}.");
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, mediaType, StringComparison.OrdinalIgnoreCase))
                throw new SourceException("source_schema", "Flextender gaf een onverwacht responsetype.");
            const int limit = 10 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > limit) throw new SourceException("response_too_large", "Flextender-response groter dan 10 MiB.");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(bytes, deadline.Token)) != 0)
            {
                if (buffer.Length + count > limit) throw new SourceException("response_too_large", "Flextender-response groter dan 10 MiB.");
                buffer.Write(bytes, 0, count);
            }
            var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
            return (string.IsNullOrEmpty(charset) ? System.Text.Encoding.UTF8 : System.Text.Encoding.GetEncoding(charset)).GetString(buffer.ToArray());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new SourceException("timeout", "Flextender reageerde niet binnen 30 seconden."); }
        catch (HttpRequestException) { throw new SourceException("source_network", "Netwerkfout bij Flextender."); }
        catch (ArgumentException) { throw new SourceException("source_schema", "Flextender gaf een onbekende tekstcodering."); }
    }

    public void Dispose() => http.Dispose();
}

public static class FlextenderMapper
{
    public static string ValidateId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Any(c => c is < '0' or > '9') || !long.TryParse(id, out var number) || number <= 0 || id != number.ToString(CultureInfo.InvariantCulture))
            throw new SourceException("invalid_argument", "Flextender-ID moet een canoniek positief aanvraagnummer zijn.");
        return id;
    }

    public static KeyValuePair<string, string>[] ReadForm(string html, string query)
    {
        var document = new HtmlParser().ParseDocument(html);
        var form = document.QuerySelector(".widget_flextender_search_jobs form.pc-search")
            ?? throw new SourceException("source_schema", "Publiek Flextender-zoekformulier ontbreekt; mogelijk een login- of blokkadepagina.");
        if (form.QuerySelector("input[name=action]")?.GetAttribute("value") != "kbs_flx_searchjobs" ||
            string.IsNullOrWhiteSpace(form.QuerySelector("input[name=kbs_flx_widget_config]")?.GetAttribute("value")) ||
            form.QuerySelector("input[name=kbs_flx_joblsrc_freetext]") is null)
            throw new SourceException("source_schema", "Flextender-zoekformulier is gewijzigd.");
        var fields = form.QuerySelectorAll("input[name]")
            .Where(e => !e.HasAttribute("disabled") && e.GetAttribute("type") is not ("checkbox" or "radio" or "submit" or "button" or "file" or "password"))
            .Select(e => KeyValuePair.Create(e.GetAttribute("name")!, e.GetAttribute("name") == "kbs_flx_joblsrc_freetext" ? query : e.GetAttribute("value") ?? ""))
            .Append(KeyValuePair.Create("_charset_", "UTF-8")).ToArray();
        return fields;
    }

    public static Job[] ReadList(string html, DateTimeOffset fetchedAt)
    {
        var document = new HtmlParser().ParseDocument(html);
        var cards = document.QuerySelectorAll(".css-foundjob");
        if (cards.Length > 1000) throw new SourceException("source_limit", "Meer dan 1000 Flextender-opdrachten; geen complete scan geclaimd.");
        if (cards.Length == 0 && !document.QuerySelectorAll("h2").Any(h => h.TextContent.Trim() == "Er zijn geen opdrachten gevonden."))
            throw new SourceException("source_schema", "Flextender-lijst ontbreekt zonder expliciete melding van nul opdrachten.");
        var jobs = cards.Select(card => {
            if (!Uri.TryCreate(card.GetAttribute("data-kbslinkurl"), UriKind.Absolute, out var url) ||
                url.Scheme != "https" || url.Host != "www.flextender.nl" || !url.IsDefaultPort || url.AbsolutePath is not ("/opdracht" or "/opdracht/"))
                throw new SourceException("source_schema", "Flextender-lijst bevat een onverwachte detail-URL.");
            var ids = url.Query.TrimStart('?').Split('&').Where(x => x.StartsWith("aanvraagnr=", StringComparison.Ordinal)).Select(x => x[11..]).ToArray();
            if (ids.Length != 1) throw new SourceException("source_schema", "Flextender-detail-URL mist een uniek aanvraagnummer.");
            string id;
            try { id = ValidateId(ids[0]); }
            catch (SourceException) { throw new SourceException("source_schema", "Flextender-lijst bevat een ongeldig aanvraagnummer."); }
            return Map(card, id, fetchedAt, false);
        }).ToArray();
        if (jobs.Select(j => j.SourceId).Distinct().Count() != jobs.Length)
            throw new SourceException("source_schema", "Dubbele Flextender-identiteiten in de bronlijst; geen complete scan geclaimd.");
        return jobs;
    }

    public static Job ReadDetail(string html, string id, DateTimeOffset fetchedAt)
    {
        ValidateId(id);
        var document = new HtmlParser().ParseDocument(html);
        var root = document.QuerySelector(".flextender_job_details_widget")
            ?? throw new SourceException("source_schema", "Volledige Flextender-detailpagina ontbreekt; mogelijk gesloten of geblokkeerd.");
        return Map(root, id, fetchedAt, true);
    }

    private static Job Map(IElement root, string id, DateTimeOffset at, bool full)
    {
        var title = root.QuerySelector(".css-jobtitle")?.TextContent.Trim();
        if (string.IsNullOrWhiteSpace(title)) throw new SourceException("source_schema", "Flextender-opdracht mist een titel.");
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in root.QuerySelectorAll(".css-summaryrow"))
        {
            var key = row.QuerySelector(".css-caption")?.TextContent.Trim();
            var value = row.QuerySelector(".css-value");
            if (key is null || value is null) continue;
            var copy = (IElement)value.Clone(true);
            foreach (var link in copy.QuerySelectorAll("a")) link.Remove();
            var fieldText = copy.TextContent.Trim();
            if (fields.TryGetValue(key, out var previous) && previous != fieldText)
                throw new SourceException("source_schema", "Flextender bevat tegenstrijdige samenvattingsvelden.");
            fields[key] = fieldText;
        }
        if (!fields.TryGetValue("Aanvraagnummer", out var observedId) || observedId != id)
            throw new SourceException("source_schema", "Flextender-detail-ID komt niet overeen met het aanvraagnummer.");
        var body = root.QuerySelector(".flx-job-mce-container");
        var textBody = body is null ? "" : JobMapper.HtmlText(body.InnerHtml);
        if (full && string.IsNullOrWhiteSpace(textBody)) throw new SourceException("source_schema", "Volledige Flextender-opdrachttekst ontbreekt.");
        var text = full ? string.Join("\n", fields.Select(f => f.Key + ": " + f.Value)) + "\n\n" + textBody : "";
        Fact? Fact(string key) => fields.TryGetValue(key, out var value) && value.Length > 0 ? new(value, new(key, value)) : null;
        var location = Fact("Regio")?.Text;
        DateTimeOffset? expires = null;
        if (fields.TryGetValue("Einde inschrijfdatum", out var end) && fields.TryGetValue("Eindtijd", out var time) &&
            DateTime.TryParseExact(end + " " + time, "d MMMM yyyy HH:mm 'uur'", CultureInfo.GetCultureInfo("nl-NL"), DateTimeStyles.None, out var local))
            expires = new DateTimeOffset(local, TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam").GetUtcOffset(local));
        return new("flextender", id, "flextender:" + id, FlextenderSource.Origin + "/opdracht/?aanvraagnr=" + id,
            title, root.QuerySelector(".css-customer")?.TextContent.Trim(), text, location is null ? [] : [location],
            Fact("Uren per week"), null, Fact("Start"), Fact("Duur"), [], [], [], null, null, null, expires, at,
            JobMapper.Contract(text, "zzp"), JobMapper.Contract(text, "payroll"), [],
            [full ? "Lees volledige tekst voor eisen, tarief en contractvoorwaarden; deze worden niet allemaal apart geëxtraheerd. Regio is een provincie, geen standplaats of kilometerafstand."
                  : "Alleen lijstmetadata; volledige tekst vereist vóór matching."], JsonSerializer.SerializeToElement(new { }));
    }
}
