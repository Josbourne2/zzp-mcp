using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ZzpMcp;

public sealed class StarAppleSource(HttpClient http, JobStore? store = null) : PublicJobSource(http, store)
{
    private const string Site = "https://www.starapple.nl";
    private const int PageSize = 20;
    private const int MaxPages = 50;
    public override string Id => "starapple";
    public override string Origin => Site;
    public override int CollectionPageSize => PageSize;
    public override string Scope => "Publieke StarApple-vacatures, alle native HTML-pagina's, inclusief herkenbare open sollicitaties. Geen garantie op dekking van StarApple-opdrachten op Striive/Vertage. ZZP en payroll afzonderlijk beoordelen op expliciet vacaturebewijs.";
    public override object Capabilities => new {
        name = "StarApple", connection = "anonymous_http", status = "configured_not_health_checked", scope = Scope,
        collection = "Hervatbare collect_jobs, één native bronpagina per aanroep en alleen ontbrekende details; geen verwijderingen.",
        search = "Lokale letterlijke zoekterm op volledige teksten van opgehaalde pagina; volg nextCursor ook bij nul treffers.",
        location = "Locatie-/kilometer- en categoriefilters niet geverifieerd: expliciet afgewezen.",
        detail = "Zichtbare vacaturesecties zonder sollicitatieformulier; cacheUsed=true betekent historische SQLite-details. Maandsalaris wordt geen uurtarief. Open sollicitaties hebben een waarschuwing.",
        pagination = "Native /vacatures/page/{n}/, 20 vermeldingen per pagina, maximaal 50 pagina's. Geen snapshot of vastgesteld brontotaal."
    };
    protected override Task<Job[]> ReadInventoryAsync(CancellationToken ct) =>
        throw new SourceException("unsupported_operation", "StarApple gebruikt afzonderlijke native bronpagina's.");

    private static string PageUrl(int number) => Site + "/vacatures/" + (number == 1 ? "" : $"page/{number}/");
    private async Task<Page> ReadPageAsync(int number, CancellationToken ct) => ParsePage(await GetTextAsync(PageUrl(number), ct), number);
    public override async Task<SourcePage> ReadCollectionPageAsync(int page, CancellationToken cancellationToken)
    {
        if (page is < 0 or >= MaxPages) throw new SourceException("invalid_argument", "StarApple: ongeldige bronpagina.");
        var result = await ReadPageAsync(page + 1, cancellationToken);
        return new(result.Jobs, null, result.Jobs.Length, [], DateTimeOffset.UtcNow, page + 1 < result.Pages, [Scope]);
    }
    protected override async Task<SourcePage> ReadSearchPageAsync(int pageSize, int offset, CancellationToken ct)
    {
        var number = offset / PageSize + 1;
        var page = await ReadPageAsync(number, ct);
        var jobs = new List<Job>();
        var skip = offset % PageSize;
        bool more;
        do
        {
            var take = Math.Min(pageSize - jobs.Count, Math.Max(0, page.Jobs.Length - skip));
            jobs.AddRange(page.Jobs.Skip(skip).Take(take));
            more = skip + take < page.Jobs.Length || number < page.Pages;
            if (jobs.Count >= pageSize || !more) break;
            number++; skip = 0;
            page = await ReadPageAsync(number, ct);
        } while (number <= MaxPages);
        if (jobs.Select(j => j.SourceId).Distinct().Count() != jobs.Count)
            throw new SourceException("source_repeated_page", "StarApple: herhaalde identiteit binnen zoekpagina.");
        return new(jobs.ToArray(), null, jobs.Count, [], DateTimeOffset.UtcNow, more);
    }

    public sealed record Page(Job[] Jobs, int Pages);
    private static IDocument Document(string html, string url)
    {
        var document = new HtmlParser().ParseDocument(html);
        var canonical = document.QuerySelector("link[rel=canonical]")?.GetAttribute("href");
        if (canonical?.TrimEnd('/') != url.TrimEnd('/'))
            throw new SourceException("source_schema", "StarApple: canonical wijkt af van gevraagde pagina; mogelijk genegeerde paginering of onjuiste detailpagina.");
        return document;
    }
    private static string Clean(string value) => Regex.Replace(value, @"\s+", " ").Trim();
    public static Page ParsePage(string html, int expectedPage)
    {
        if (expectedPage is < 1 or > MaxPages) throw new SourceException("invalid_argument", "StarApple: ongeldige bronpagina.");
        var document = Document(html, PageUrl(expectedPage));
        var list = document.QuerySelector("#vacancy-list") ?? throw new SourceException("source_schema", "StarApple: vacaturelijst ontbreekt.");
        var jobs = list.QuerySelectorAll("article").Select(card => {
            var link = card.QuerySelector("a[href]")?.GetAttribute("href") ?? throw new SourceException("source_schema", "StarApple: detaillink ontbreekt.");
            var id = PublicJobData.LinkId(link, Site, "/vacatures/");
            var title = Clean(card.QuerySelector("h2")?.TextContent ?? "");
            if (title.Length == 0) throw new SourceException("source_schema", "StarApple: vacaturetitel ontbreekt.");
            return PublicJobData.Listing("starapple", id, Site + "/vacatures/" + id + "/", title);
        }).ToArray();
        if (jobs.Length > PageSize || jobs.Select(j => j.SourceId).Distinct().Count() != jobs.Length)
            throw new SourceException("source_schema", "StarApple: te veel of dubbele kaarten op bronpagina.");
        var pages = new List<int> { 1 };
        foreach (var link in document.QuerySelectorAll("main nav a[href]"))
        {
            var href = link.GetAttribute("href")!;
            if (!Uri.TryCreate(new Uri(Site), href, out var uri) || uri.Scheme != "https" || uri.Authority != new Uri(Site).Authority || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                throw new SourceException("source_schema", "StarApple: ongeldige paginalink.");
            if (uri.AbsolutePath.TrimEnd('/') == "/vacatures") continue;
            var match = Regex.Match(uri.AbsolutePath, @"^/vacatures/page/([1-9]\d*)/$");
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var number))
                throw new SourceException("source_schema", "StarApple: paginalink gewijzigd.");
            pages.Add(number);
        }
        var last = pages.Max();
        if (last > MaxPages) throw new SourceException("source_limit", "StarApple: meer dan 50 bronpagina's; geen complete scan geclaimd.");
        if (expectedPage > last || (expectedPage < last && jobs.Length != PageSize) || (jobs.Length == PageSize && pages.Count == 1))
            throw new SourceException("source_schema", "StarApple: paginabewijs ontbreekt of aantal kaarten wijkt af.");
        if (jobs.Length == 0 && (expectedPage != 1 || !list.TextContent.Contains("Geen vacatures", StringComparison.OrdinalIgnoreCase)))
            throw new SourceException("source_schema", "StarApple: lege lijst zonder expliciet leeg-resultaatbewijs.");
        return new(jobs, last);
    }

    protected override async Task<Job> ReadDetailAsync(string id, CancellationToken ct) =>
        ParseDetail(id, await GetTextAsync(Site + "/vacatures/" + id + "/", ct));

    public static Job ParseDetail(string id, string html)
    {
        PublicJobData.Slug(id);
        var url = Site + "/vacatures/" + id + "/";
        var document = Document(html, url);
        var hero = document.QuerySelector(".vacancy-hero");
        var heading = hero?.QuerySelector("h1");
        var title = Clean(heading?.TextContent ?? "");
        if (hero is null || heading is null || title.Length == 0)
            throw new SourceException("source_schema", "StarApple: zichtbare vacaturetitel ontbreekt.");
        // This template calls the location 'Company Name'; it is not an employer field.
        var location = Clean(heading.NextElementSibling?.TextContent ?? "");
        var meta = hero.QuerySelectorAll(".vacancy-meta .meta-item").Select(e => Clean(e.TextContent)).Where(t => t.Length > 0).ToArray();
        var sections = new List<string>(); var requirements = new List<string>();
        foreach (var section in document.QuerySelectorAll("section.content-section"))
        {
            var label = Clean(section.QuerySelector("h2")?.TextContent ?? "");
            var blocks = section.QuerySelectorAll(".prose").Where(e => e.ParentElement?.Closest(".prose") is null).ToArray();
            var body = string.Join("\n\n", blocks.Select(e => JobMapper.HtmlText(e.InnerHtml))).Trim();
            if (body.Length == 0) continue;
            sections.Add((label.Length > 0 ? label + "\n" : "") + body);
            if (label.Equals("Functie eisen", StringComparison.OrdinalIgnoreCase))
                requirements.AddRange(blocks.SelectMany(e => e.QuerySelectorAll("li")).Select(e => Clean(e.TextContent)).Where(t => t.Length > 0));
        }
        var open = title.Equals("Open sollicitatie", StringComparison.OrdinalIgnoreCase);
        if (sections.Count == 0 && !open) throw new SourceException("source_schema", "StarApple: volledige vacaturesecties ontbreken.");
        var text = string.Join("\n\n", new[] { title, location, string.Join("\n", meta) }.Concat(sections).Where(t => t.Length > 0));
        var warnings = new List<string> {
            "StarApple is bemiddelaar; uiteindelijke werkgever niet uit sitenaam afleiden. Salaris en arbeidsvoorwaarden bewijzen geen ZZP- of payrolltoestemming. Beoordeel beide afzonderlijk; payroll kan relevant zijn als ZZP uitgesloten is.",
            "Publieke vacaturebron; geen bewijs van volledige dekking van StarApple-opdrachten op Striive/Vertage. WebPage-datums zijn niet als vacaturedatums overgenomen."
        };
        if (open) warnings.Add("Open sollicitatie; geen concrete vacature. Eventueel ontbrekende functieomschrijving wordt niet aangevuld.");
        var hours = meta.FirstOrDefault(t => Regex.IsMatch(t, @"\b\d+(?:\s*[-–]\s*\d+)?\s*uur\b", RegexOptions.IgnoreCase));
        var moneyLines = sections.SelectMany(t => t.Split('\n')).Select(t => t.Trim().TrimStart('-').Trim())
            .Where(t => Regex.IsMatch(t, @"\b(?:salaris|tarief|uurtarief)\b", RegexOptions.IgnoreCase) && t.Contains('€')).ToArray();
        if (moneyLines.Length == 0) moneyLines = meta.Where(t => Regex.IsMatch(t, @"€\s*\d")).ToArray();
        return PublicJobData.Create("starapple", id, url, title, text, locations: location.Length == 0 ? [] : [location],
            hours: PublicJobData.Fact("vacancy-meta", hours), compensation: moneyLines.Select(ParseMoney).ToArray(), warnings: warnings.ToArray())
            with { Requirements = requirements.Distinct().ToArray() };
    }

    private static Money ParseMoney(string line)
    {
        // Dutch amounts in an explicitly Dutch source field; retain the original line as evidence.
        var numbers = Regex.Matches(line, @"€\s*(\d+(?:\.\d{3})*(?:,\d{1,2})?)").Select(m => decimal.TryParse(m.Groups[1].Value, NumberStyles.Number, CultureInfo.GetCultureInfo("nl-NL"), out var n) && n > 0 ? (decimal?)n : null).ToArray();
        var range = Regex.Match(line, @"€\s*(?<low>\d+(?:\.\d{3})*(?:,\d{1,2})?)\s*(?:[-–]|tot)\s*€?\s*(?<high>\d+(?:\.\d{3})*(?:,\d{1,2})?)");
        decimal? low = numbers.Length == 1 ? numbers[0] : null, high = low;
        if (range.Success)
        {
            low = high = null;
            if (decimal.TryParse(range.Groups["low"].Value, NumberStyles.Number, CultureInfo.GetCultureInfo("nl-NL"), out var l) && decimal.TryParse(range.Groups["high"].Value, NumberStyles.Number, CultureInfo.GetCultureInfo("nl-NL"), out var h) && l > 0 && h >= l) { low = l; high = h; }
        }
        string? unit = Regex.IsMatch(line, @"per\s+maand", RegexOptions.IgnoreCase) ? "month" : Regex.IsMatch(line, @"per\s+jaar", RegexOptions.IgnoreCase) ? "year" : Regex.IsMatch(line, @"per\s+uur|uurtarief", RegexOptions.IgnoreCase) ? "hour" : null;
        var kind = Regex.IsMatch(line, @"\b(?:tarief|uurtarief)\b", RegexOptions.IgnoreCase) ? "rate" : Regex.IsMatch(line, @"\bsalaris\b", RegexOptions.IgnoreCase) ? "salary" : "unknown";
        return new(kind, low, high, "EUR", unit, line, null, null, new("visible vacancy compensation", line));
    }
}
