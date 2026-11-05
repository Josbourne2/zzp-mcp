using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ZzpMcp;

public sealed class HarveyNashSource(HttpClient http) : IJobSource, IDisposable
{
    public string Id => "harvey-nash";
    public object Capabilities => new
    {
        name = "Harvey Nash", connection = "anonymous", status = "configured_not_health_checked",
        search = new[] { "query", "location", "radiusKm", "categories", "zzp (local)", "payroll (local)" },
        pageSize = new { min = 1, max = 100 }, maxOffset = 1000,
        facets = "search_jobs met lege query geeft bronwaargenomen categorie- en waarde-ID's; geen vaste taxonomie.",
        querySemantics = "Bronzoekmachine; synoniemen/AND/OR/rangschikking niet gegarandeerd. Gebruik meerdere zoekrondes.",
        pagination = "Offset, geen snapshot; wijzigingen kunnen overlap of omissies geven."
    };
    private const string Endpoint = "https://www.harveynash.nl/_sf/api/v1/jobs/search.json";

    public async Task<SourcePage> SearchAsync(SearchRequest request, int offset, CancellationToken cancellationToken)
    {
        Validate(request, offset);
        if (request.Categories is { Count: > 0 })
        {
            // Validate against current source-wide facets, not just values seen on this filtered page.
            var options = await FetchAsync(new(PageSize: 1), 0, cancellationToken);
            foreach (var (key, values) in request.Categories)
            {
                var facet = options.Facets.FirstOrDefault(x => x.Id == key);
                if (facet is null || values.Length == 0 || values.Any(v => !facet.Values.Any(x => x.Id == v)))
                    throw new SourceException("invalid_filter", "Onbekende categorie of waarde; vraag actuele facetten op met een lege zoekopdracht.");
            }
        }
        return await FetchAsync(request, offset, cancellationToken);
    }

    public async Task<Job> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var parsedId)) throw new SourceException("invalid_argument", "Harvey Nash-bron-ID moet een UUID zijn.");
        id = parsedId.ToString("D");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var direct = await FetchAsync(new(Query: id, PageSize: 100), 0, deadline.Token);
            if (direct.Jobs.FirstOrDefault(j => j.SourceId == id) is { } exact) return exact;
            var seen = new HashSet<string>();
            for (int offset = 0; offset < 1000;)
            {
                var page = await FetchAsync(new(PageSize: 100), offset, deadline.Token);
                if (page.Jobs.FirstOrDefault(j => j.SourceId == id) is { } found) return found;
                if (page.Consumed == 0 && offset < page.Total) throw new SourceException("search_incomplete", "Lege bronpagina vóór het brontotaal; afwezigheid niet bewezen.");
                if (page.Jobs.Length > 0 && page.Jobs.All(j => seen.Contains(j.SourceId))) throw new SourceException("search_incomplete", "De bron herhaalt pagina's; afwezigheid niet bewezen.");
                foreach (var job in page.Jobs) seen.Add(job.SourceId);
                offset += page.Consumed;
                if (offset >= page.Total) throw new SourceException("not_found", "Vacature niet aangetroffen in de huidige volledige bronzoekactie; mogelijk verwijderd.");
            }
            throw new SourceException("search_incomplete", "Terugzoeklimiet van 1000 bronrecords bereikt; afwezigheid niet bewezen.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new SourceException("timeout", "Details niet opgehaald binnen 60 seconden."); }
    }

    public static void Validate(SearchRequest request, int offset)
        => SearchValidation.Validate(request, offset);

    private async Task<SourcePage> FetchAsync(SearchRequest request, int offset, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(http.Timeout == Timeout.InfiniteTimeSpan ? TimeSpan.FromSeconds(20) : TimeSpan.FromMilliseconds(Math.Min(20000, http.Timeout.TotalMilliseconds)));
        var requestToken = deadline.Token;
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = JsonContent.Create(new { job_search = new {
                    query = request.Query,
                    location = new { address = request.Location ?? "", radius = request.RadiusKm / 1.609344, region = "NL", radius_units = "miles" },
                    filters = request.Categories ?? [], commute_filter = new { }, offset, jobs_per_page = request.PageSize
                } })
            };
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, requestToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new SourceException("access_denied", $"Harvey Nash weigert anonieme toegang (HTTP {(int)response.StatusCode}); geen lege vacaturelijst.");
            if (!response.IsSuccessStatusCode) throw new SourceException("source_http", $"Harvey Nash gaf HTTP {(int)response.StatusCode}.");
            if (response.RequestMessage?.RequestUri?.AbsolutePath.Contains("login", StringComparison.OrdinalIgnoreCase) == true)
                throw new SourceException("login_required", "De bron verwees naar een loginpagina.");
            if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
                throw new SourceException("source_schema", "De bron gaf geen JSON; mogelijk een login- of storingspagina.");
            await using var stream = await response.Content.ReadAsStreamAsync(requestToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk, requestToken)) != 0)
            {
                if (buffer.Length + count > 10 * 1024 * 1024) throw new SourceException("response_too_large", "Bronresponse groter dan 10 MiB; verlaag de paginagrootte.");
                buffer.Write(chunk, 0, count);
            }
            using var json = JsonDocument.Parse(buffer.ToArray());
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array ||
                !root.TryGetProperty("total_size", out var total) || !total.TryGetInt32(out var totalSize) || totalSize < 0)
                throw new SourceException("source_schema", "De JSON-response mist geldige results/total_size.");
            if (results.GetArrayLength() > request.PageSize)
                throw new SourceException("source_schema", "De bron overschrijdt de gevraagde paginagrootte.");
            if (!string.IsNullOrWhiteSpace(request.Location) &&
                (!root.TryGetProperty("metadata", out var metadata) || !metadata.TryGetProperty("location_filters", out var locationFilters) || locationFilters.ValueKind != JsonValueKind.Array || locationFilters.GetArrayLength() == 0))
                throw new SourceException("filter_not_applied", "De bron bevestigt het gevraagde locatiefilter niet; geen gefilterd succes geclaimd.");
            var fetchedAt = DateTimeOffset.UtcNow;
            var jobs = new List<Job>();
            foreach (var result in results.EnumerateArray())
            {
                if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("job", out var job) || job.ValueKind != JsonValueKind.Object)
                    throw new SourceException("source_schema", "Een zoektreffer bevat geen geldig job-object.");
                jobs.Add(JobMapper.Map(job, fetchedAt));
            }
            if (request.Categories is { Count: > 0 } && jobs.Any(job => request.Categories.Any(filter =>
                !job.Categories.Any(category => category.Id == filter.Key && category.Values.Any(value => filter.Value.Contains(value.Id))))))
                throw new SourceException("filter_not_applied", "De bron leverde een vacature buiten de gevraagde categorievoorwaarden; geen gefilterd succes geclaimd.");
            var facets = new List<Facet>();
            if (root.TryGetProperty("histogram_results", out var histogram) && histogram.ValueKind == JsonValueKind.Array)
                foreach (var h in histogram.EnumerateArray())
                    if (h.TryGetProperty("counts", out var counts) && counts.ValueKind == JsonValueKind.Array)
                        facets.Add(new(JobMapper.String(h, "category_id") ?? "", JobMapper.String(h, "category_name") ?? "",
                            counts.EnumerateArray().Select(c => new FacetValue(JobMapper.String(c, "id") ?? "", JobMapper.String(c, "name") ?? "",
                                c.TryGetProperty("count", out var n) && n.TryGetInt32(out var number) ? number : 0)).ToArray()));
            return new(jobs.DistinctBy(j => j.SourceId).ToArray(), totalSize, results.GetArrayLength(), facets.ToArray(), fetchedAt);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new SourceException("timeout", "Harvey Nash reageerde niet binnen de HTTP-timeout."); }
        catch (HttpRequestException) { throw new SourceException("source_network", "Netwerkfout bij Harvey Nash."); }
        catch (JsonException) { throw new SourceException("source_schema", "De bron gaf ongeldige JSON."); }
        catch (InvalidOperationException) { throw new SourceException("source_schema", "De bron gaf ongeldige veldtypen."); }
    }
    public void Dispose() => http.Dispose();
}
