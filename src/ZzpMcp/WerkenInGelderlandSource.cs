using AngleSharp.Html.Parser;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ZzpMcp;

public sealed class WerkenInGelderlandSource(HttpClient http, JobStore? store = null) : PublicJobSource(http, store)
{
    public override string Id => "werken-in-gelderland";
    public override string Origin => "https://www.werkeningelderland.nl";
    public override string Scope => "Publiek inhuuroverzicht van Werken in Gelderland, alle HTML-pagina's. Reageren verloopt via Flextender; bronidentiteiten blijven afzonderlijk.";
    protected override async Task<Job[]> ReadInventoryAsync(CancellationToken ct)
    {
        var jobs = new List<Job>(); var ids = new HashSet<string>(); int? total = null;
        for (var page = 1; page <= 100; page++)
        {
            var result = ParsePage(await GetTextAsync(Origin + "/inhuur/" + (page == 1 ? "" : "page/" + page + "/"), ct));
            if (total.HasValue && total != result.Total) throw new SourceException("source_changed", "Werken in Gelderland: brontotaal veranderde tijdens scan.");
            total = result.Total;
            if (total > 1000) throw new SourceException("source_limit", "Werken in Gelderland: meer dan 1000 opdrachten.");
            foreach (var job in result.Jobs)
            {
                if (!ids.Add(job.SourceId)) throw new SourceException("source_repeated_page", "Werken in Gelderland: herhaalde identiteit; paginering gestopt.");
                jobs.Add(job);
            }
            if (jobs.Count == total) return jobs.ToArray();
            if (result.Jobs.Length == 0 || jobs.Count > total || !result.HasNext(page + 1))
                throw new SourceException("source_schema", "Werken in Gelderland: lijst dekt het brontotaal niet; paginering onvolledig.");
        }
        throw new SourceException("source_limit", "Werken in Gelderland: paginalimiet bereikt.");
    }
    public sealed record Page(Job[] Jobs, int Total, string[] Links)
    {
        public bool HasNext(int number) => Links.Any(l => l.TrimEnd('/').EndsWith("/inhuur/page/" + number, StringComparison.Ordinal));
    }
    public static Page ParsePage(string html)
    {
        var document = new HtmlParser().ParseDocument(html);
        var main = document.QuerySelector("main.page-content") ?? throw new SourceException("source_schema", "Werken in Gelderland: hoofdlijst ontbreekt.");
        var match = Regex.Match(main.TextContent, @"Er\s+zijn\s+(\d+)\s+opdrachten\s+gevonden", RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var total)) throw new SourceException("source_schema", "Werken in Gelderland: brontotaal ontbreekt.");
        var jobs = main.QuerySelectorAll("article.vacancy--large").Select(card => {
            var href = card.QuerySelector("a[href*='/opdracht/']")?.GetAttribute("href") ?? card.Closest("a[href*='/opdracht/']")?.GetAttribute("href")
                ?? throw new SourceException("source_schema", "Werken in Gelderland: detaillink ontbreekt.");
            var id = PublicJobData.LinkId(href, "https://www.werkeningelderland.nl", "/opdracht/");
            var title = card.QuerySelector(".vacancy__title")?.TextContent.Trim();
            if (string.IsNullOrWhiteSpace(title)) throw new SourceException("source_schema", "Werken in Gelderland: lege titel.");
            return PublicJobData.Listing("werken-in-gelderland", id, "https://www.werkeningelderland.nl/opdracht/" + id + "/", title);
        }).ToArray();
        if (jobs.Length == 0 && total > 0 || jobs.Length > 10) throw new SourceException("source_schema", "Werken in Gelderland: ongeldige lijstpagina.");
        return new(jobs, total, main.QuerySelectorAll("a[href]").Select(a => a.GetAttribute("href")!).ToArray());
    }
    protected override async Task<Job> ReadDetailAsync(string id, CancellationToken ct)
    {
        var url = Origin + "/opdracht/" + id + "/";
        var document = new HtmlParser().ParseDocument(await GetTextAsync(url, ct));
        var canonical = document.QuerySelector("link[rel=canonical]")?.GetAttribute("href");
        if (canonical?.TrimEnd('/') != url.TrimEnd('/')) throw new SourceException("source_schema", "Werken in Gelderland: detail-URL wijkt af.");
        var root = document.QuerySelector("article.single-vacancy");
        var title = root?.QuerySelector(".single-vacancy__title")?.TextContent.Trim();
        var content = root?.QuerySelector(".single-vacancy__content .content__main");
        if (root is null || string.IsNullOrWhiteSpace(title) || content is null || string.IsNullOrWhiteSpace(content.TextContent))
            throw new SourceException("source_schema", "Werken in Gelderland: volledige opdrachttekst ontbreekt.");
        var fields = new Dictionary<string, string>();
        foreach (var row in root.QuerySelectorAll("li.single-vacancy__detail"))
        {
            var key = row.QuerySelector(".single-vacancy__detail__title")?.TextContent.Trim();
            var value = row.QuerySelector(".single-vacancy__detail__value");
            if (key is null || value is null) continue;
            var clone = (AngleSharp.Dom.IElement)value.Clone(true);
            foreach (var days in clone.QuerySelectorAll(".days")) days.Remove();
            fields[key] = Regex.Replace(clone.TextContent, @"\s+", " ").Trim();
        }
        var text = string.Join("\n", fields.Select(f => f.Key + ": " + f.Value)) + "\n\n" + JobMapper.HtmlText(content.InnerHtml);
        if (root.QuerySelector("a[href^='https://app.flextender.nl/login/require/applyforjob/']")?.GetAttribute("href") is { } originLink)
            text += "\n\nBronverwijzing voor reageren (Flextender): " + originLink.Split('?')[0];
        var location = root.QuerySelector(".single-vacancy__location")?.TextContent.Trim();
        DateTimeOffset? expires = null;
        if (fields.TryGetValue("Reageren t/m", out var end) && DateTime.TryParseExact(end, "d MMMM yyyy HH:mm", CultureInfo.GetCultureInfo("nl-NL"), DateTimeStyles.None, out var local))
            expires = new(local, TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam").GetUtcOffset(local));
        return PublicJobData.Create(Id, id, url, title, text, locations: string.IsNullOrWhiteSpace(location) ? [] : [location],
            hours: fields.TryGetValue("Uren per week", out var hours) ? PublicJobData.Fact("Uren per week", hours) : null,
            start: fields.TryGetValue("Aanvang opdracht", out var start) ? PublicJobData.Fact("Aanvang opdracht", start) : null,
            duration: fields.TryGetValue("Einde opdracht", out var duration) ? PublicJobData.Fact("Einde opdracht", duration) : null,
            expires: expires, warnings: ["Werken in Gelderland verwijst voor reageren naar Flextender. Een ontbrekende locatie/opdrachtgever wordt niet uit de sitenaam afgeleid. Lees alle voorwaarden in de volledige tekst."]);
    }
}
