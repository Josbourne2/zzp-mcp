using AngleSharp.Html.Parser;
using System.Text.Json;

namespace ZzpMcp;

public sealed class MiPublicSource(HttpClient http, JobStore? store = null) : PublicJobSource(http, store)
{
    public override string Id => "mipublic";
    public override string Origin => "https://mipublic.nl";
    public override string Scope => "Alle vakgebieden uit de publieke MiPublic-lijst /interim-opdrachten; geen beperking tot ICT/Data.";
    protected override async Task<Job[]> ReadInventoryAsync(CancellationToken ct) => ParseInventory(await GetTextAsync(Origin + "/interim-opdrachten", ct));
    public static Job[] ParseInventory(string html)
    {
        var document = new HtmlParser().ParseDocument(html);
        var lists = PublicJobData.LinkedData(document).Where(j => PublicJobData.Text(j, "@type") == "ItemList" && j.TryGetProperty("numberOfItems", out _)).ToArray();
        if (lists.Length != 1) throw new SourceException("source_schema", "MiPublic: geen unieke volledige ItemList.");
        var list = lists[0];
        var items = PublicJobData.Child(list, "itemListElement");
        var total = PublicJobData.Number(list, "numberOfItems");
        if (items.ValueKind != JsonValueKind.Array || !total.HasValue || total < 0 || total != items.GetArrayLength())
            throw new SourceException("source_schema", "MiPublic: opgegeven aantal wijkt af van detail-URL-lijst; geen complete scan geclaimd.");
        if (total > 1000) throw new SourceException("source_limit", "MiPublic: inventaris groter dan 1000.");
        return items.EnumerateArray().Select(item => {
            var url = PublicJobData.Text(item, "url") ?? throw new SourceException("source_schema", "MiPublic: detail-URL ontbreekt.");
            var id = PublicJobData.LinkId(url, "https://mipublic.nl", "/vacature/");
            return PublicJobData.Listing("mipublic", id, "https://mipublic.nl/vacature/" + id, id.Replace('-', ' '));
        }).ToArray();
    }
    protected override async Task<Job> ReadDetailAsync(string id, CancellationToken ct)
    {
        var url = Origin + "/vacature/" + id;
        return PublicJobData.FromLinkedData(Id, id, url, await GetTextAsync(url, ct));
    }
}
