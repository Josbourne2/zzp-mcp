using AngleSharp.Html.Parser;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace ZzpMcp;

public sealed class LibLabSource(HttpClient http, JobStore? store = null) : PublicJobSource(http, store)
{
    public override string Id => "liblab";
    public override string Origin => "https://liblab.nl";
    public override int CollectionPageSize => 12;
    public override string Scope => "Publieke LibLab-opdrachten uit alle FacetWP-pagina's, inclusief detachering. Mogelijke overlap met andere bronnen; geen samenvoeging op titel.";
    protected override Task<Job[]> ReadInventoryAsync(CancellationToken ct) =>
        throw new SourceException("unsupported_operation", "LibLab gebruikt afzonderlijke native bronpagina's, geen schijnbaar atomische totaalopname.");
    private async Task<(Job[] Jobs, int Total, int Pages, int Size)> ReadNativePageAsync(int number, CancellationToken ct)
    {
        var result = ParsePage(await GetTextAsync(Origin + "/onze-opdrachten/" + (number == 1 ? "" : "?_paged=" + number), ct), number);
        if (result.Total > 1000 || result.Pages > 100) throw new SourceException("source_limit", "LibLab: bron overschrijdt de scanlimiet.");
        return result;
    }
    public override async Task<SourcePage> ReadCollectionPageAsync(int page, CancellationToken cancellationToken)
    {
        if (page is < 0 or >= 100) throw new SourceException("invalid_argument", "LibLab: ongeldige bronpagina.");
        var result = await ReadNativePageAsync(page + 1, cancellationToken);
        return new(result.Jobs, result.Total, result.Jobs.Length, [], DateTimeOffset.UtcNow, page + 1 < result.Pages,
            ["LibLab-paginatotalen zijn geen snapshot en kunnen per gecachete pagina verschillen. Alleen waargenomen leden worden vastgelegd; geen verwijderingen."]);
    }
    protected override async Task<SourcePage> ReadSearchPageAsync(int pageSize, int offset, CancellationToken ct)
    {
        var first = await ReadNativePageAsync(1, ct);
        var number = offset / first.Size + 1;
        if (number > first.Pages) return new([], first.Total, 0, [], DateTimeOffset.UtcNow, false);
        var page = number == 1 ? first : await ReadNativePageAsync(number, ct);
        var jobs = new List<Job>(); var skip = offset % first.Size; var total = (int?)first.Total; bool more;
        do
        {
            if (page.Size != first.Size) throw new SourceException("source_changed", "LibLab: native paginagrootte veranderde tijdens zoeken.");
            if (page.Total != first.Total) total = null;
            var take = Math.Min(pageSize - jobs.Count, page.Jobs.Length - skip);
            jobs.AddRange(page.Jobs.Skip(skip).Take(take));
            more = skip + take < page.Jobs.Length || number < page.Pages;
            if (jobs.Count >= pageSize || !more) break;
            number++; skip = 0;
            page = await ReadNativePageAsync(number, ct);
        } while (number <= 100);
        if (jobs.Select(j => j.SourceId).Distinct().Count() != jobs.Count) throw new SourceException("source_repeated_page", "LibLab: herhaalde identiteit binnen zoekpagina.");
        return new(jobs.ToArray(), total, jobs.Count, [], DateTimeOffset.UtcNow, more);
    }
    public static (Job[] Jobs, int Total, int Pages, int Size) ParsePage(string html, int expectedPage)
    {
        var document = new HtmlParser().ParseDocument(html);
        var script = document.QuerySelectorAll("script").SingleOrDefault(s => s.TextContent.Contains("window.FWP_JSON =", StringComparison.Ordinal))
            ?? throw new SourceException("source_schema", "LibLab: FacetWP-paginabewijs ontbreekt.");
        var match = Regex.Match(script.TextContent, @"window\.FWP_JSON\s*=\s*(\{.*\});\s*$", RegexOptions.Singleline);
        if (!match.Success) throw new SourceException("source_schema", "LibLab: FacetWP-configuratie gewijzigd.");
        var config = PublicJobData.ParseJson(match.Groups[1].Value);
        var pager = PublicJobData.Child(PublicJobData.Child(PublicJobData.Child(config, "preload_data"), "settings"), "pager");
        int Number(string name) => PublicJobData.Child(pager, name).ValueKind == JsonValueKind.Number && PublicJobData.Child(pager, name).TryGetInt32(out var n) ? n : -1;
        var total = Number("total_rows"); var pages = Number("total_pages"); var size = Number("per_page");
        if (Number("page") != expectedPage || total < 0 || size < 1 || pages < 0 || pages != (int)Math.Ceiling(total / (double)size))
            throw new SourceException("source_schema", "LibLab: ongeldige paginastatus of genegeerde pagina.");
        var template = document.QuerySelector(".facetwp-template[data-name=jobs]") ?? throw new SourceException("source_schema", "LibLab: opdrachtenlijst ontbreekt.");
        var jobs = template.QuerySelectorAll("a[href]").Select(a => {
            var id = PublicJobData.LinkId(a.GetAttribute("href")!, "https://liblab.nl", "/opdrachten/");
            var title = a.QuerySelector(".job-title")?.TextContent.Trim();
            if (string.IsNullOrWhiteSpace(title)) throw new SourceException("source_schema", "LibLab: opdrachttitel ontbreekt.");
            return PublicJobData.Listing("liblab", id, "https://liblab.nl/opdrachten/" + id + "/", title, a.QuerySelector(".job-subtitle")?.TextContent.Trim());
        }).ToArray();
        if (jobs.Length != Math.Min(size, Math.Max(0, total - (expectedPage - 1) * size))) throw new SourceException("source_schema", "LibLab: aantal kaarten wijkt af van paginabewijs.");
        if (jobs.Select(j => j.SourceId).Distinct().Count() != jobs.Length) throw new SourceException("source_schema", "LibLab: dubbele identiteiten binnen bronpagina.");
        return (jobs, total, pages, size);
    }
    protected override async Task<Job> ReadDetailAsync(string id, CancellationToken ct)
    {
        var url = Origin + "/opdrachten/" + id + "/";
        return PublicJobData.FromLinkedData(Id, id, url, await GetTextAsync(url, ct), ".job-info-wrapper", ".jobs-disclaimer");
    }
}
