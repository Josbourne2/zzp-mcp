using AngleSharp.Html.Parser;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace ZzpMcp;

public sealed class FreepSource(HttpClient http, JobStore? store = null) : PublicJobSource(http, store)
{
    private readonly ConcurrentDictionary<string, string> _slugs = new();
    public override string Id => "freep";
    public override string Origin => "https://www.freep.nl";
    public override string Scope => "Alle opdrachten uit de publieke Nuxt-lijst van Freep; geen voorkeur- of profielselectie.";
    public override void ValidateId(string id) => FlextenderMapper.ValidateId(id);
    protected override async Task<Job[]> ReadInventoryAsync(CancellationToken ct)
    {
        var jobs = ParseInventory(await GetTextAsync(Origin + "/", ct));
        foreach (var job in jobs) _slugs[job.SourceId] = PublicJobData.LinkId(job.Url, Origin, "/opdracht/");
        return jobs;
    }
    public static Job[] ParseInventory(string html)
    {
        var script = new HtmlParser().ParseDocument(html).QuerySelector("script#__NUXT_DATA__")
            ?? throw new SourceException("source_schema", "Freep: Nuxt-lijst ontbreekt; mogelijk blokkade of gewijzigde website.");
        var root = PublicJobData.ParseJson(script.TextContent);
        if (root.ValueKind != JsonValueKind.Array) throw new SourceException("source_schema", "Freep: payload is geen verwijzingsarray.");
        var nodes = root.EnumerateArray().ToArray();
        JsonElement Ref(JsonElement index)
        {
            if (index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out var n) || n < 0 || n >= nodes.Length) throw new SourceException("source_schema", "Freep: ongeldige payloadverwijzing.");
            return nodes[n];
        }
        var candidates = new List<Job[]>();
        foreach (var node in nodes.Where(n => n.ValueKind == JsonValueKind.Array && n.GetArrayLength() > 0))
        {
            var indices = node.EnumerateArray().ToArray();
            if (indices.Any(i => i.ValueKind != JsonValueKind.Number)) continue;
            var entries = indices.Select(Ref).ToArray();
            if (!entries.All(j => j.ValueKind == JsonValueKind.Object && j.TryGetProperty("id", out _) && j.TryGetProperty("slug", out _) && j.TryGetProperty("title", out _) && j.TryGetProperty("company_name", out _))) continue;
            if (entries.Length > 1000) throw new SourceException("source_limit", "Freep: meer dan 1000 opdrachten; geen complete scan geclaimd.");
            string Field(JsonElement job, string key) => job.TryGetProperty(key, out var reference) ? Ref(reference).ToString() : "";
            candidates.Add(entries.Select(j => {
                var id = Field(j, "id"); var slug = Field(j, "slug"); var title = Field(j, "title");
                try { FlextenderMapper.ValidateId(id); PublicJobData.Slug(slug); }
                catch (SourceException) { throw new SourceException("source_schema", "Freep: ongeldige bronidentiteit."); }
                if (string.IsNullOrWhiteSpace(title)) throw new SourceException("source_schema", "Freep: lege titel.");
                var location = Field(j, "location_province");
                return PublicJobData.Listing("freep", id, "https://www.freep.nl/opdracht/" + slug, title, Field(j, "company_name"), location.Length == 0 ? [] : [location]);
            }).ToArray());
        }
        if (candidates.Count != 1) throw new SourceException("source_schema", "Freep: geen unieke complete opdrachtenarray vastgesteld; geen nulresultaat geclaimd.");
        return candidates[0];
    }
    protected override async Task<Job> ReadDetailAsync(string id, CancellationToken ct)
    {
        var known = Store?.Read(Id, id)?.Summary.Url;
        if (!_slugs.TryGetValue(id, out var slug) && known is not null) slug = PublicJobData.LinkId(known, Origin, "/opdracht/");
        if (slug is null)
        {
            await ReadInventoryAsync(ct);
            if (!_slugs.TryGetValue(id, out slug)) throw new SourceException("not_found", "Freep: aanvraagnummer niet in huidige publieke lijst gevonden; geen sluiting afgeleid.");
        }
        var url = Origin + "/opdracht/" + slug;
        return PublicJobData.FromLinkedData(Id, id, url, await GetTextAsync(url, ct), "div.prose-base");
    }
}
