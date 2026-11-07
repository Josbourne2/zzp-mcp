using System.Globalization;
using System.Text.Json;

namespace ZzpMcp;

public sealed class MagnitSource(HttpClient http, JobStore? store = null) : PublicJobSource(http, store)
{
    public const string Api = "https://app-openmarketgateway-prod.azurewebsites.net/api/jobsearch";
    public override string Id => "magnit";
    public override string Origin => "https://magnit-source.magnitglobal.com";
    public override string Scope => "Wereldwijde publieke Magnit Source-lijst met waargenomen statuscode 4 (website: Open). Geen Nederlands landfilter of klantportalen geclaimd.";
    public override void ValidateId(string id)
    {
        if (!Guid.TryParseExact(id, "D", out var parsed) || parsed.ToString("D") != id) throw new SourceException("invalid_argument", "Magnit: gebruik de canonieke UUID uit de bronlijst.");
    }
    protected override async Task<Job[]> ReadInventoryAsync(CancellationToken ct)
    {
        var jobs = new List<Job>(); var ids = new HashSet<string>(); var tokens = new HashSet<string>();
        string? token = null; int? expected = null;
        do
        {
            ct.ThrowIfCancellationRequested();
            var body = new Dictionary<string, object?> { ["searchTerm"] = "", ["selectedStatuses"] = new[] { "4" }, ["pageSize"] = 100,
                ["sortOption"] = new { orderBy = "PublishedDate", direction = "Desc" } };
            if (token is not null) body["continuationToken"] = token;
            var raw = PublicJobData.ParseJson(await PostJsonAsync(Api, body, ct));
            var page = PublicJobData.Child(raw, "jobs"); var total = PublicJobData.Child(raw, "totalCount");
            if (page.ValueKind != JsonValueKind.Array || total.ValueKind != JsonValueKind.Number || !total.TryGetInt32(out var count) || count < 0)
                throw new SourceException("source_schema", "Magnit: ongeldige lijst of totaal.");
            if (expected.HasValue && expected != count) throw new SourceException("source_changed", "Magnit: brontotaal veranderde tijdens inventarisatie; probeer later een nieuwe scan.");
            expected = count;
            if (count > 1000 || page.GetArrayLength() > 100) throw new SourceException("source_limit", "Magnit: bron overschrijdt inventaris-/paginagrens.");
            foreach (var item in page.EnumerateArray())
            {
                var job = Map(item, false);
                if (!ids.Add(job.SourceId)) throw new SourceException("source_repeated_page", "Magnit: herhaalde bronidentiteit; paginering gestopt.");
                jobs.Add(job);
            }
            token = PublicJobData.Text(raw, "continuationToken");
            if (string.IsNullOrEmpty(token)) token = null;
            if (token is not null && (!tokens.Add(token) || page.GetArrayLength() == 0 || jobs.Count >= count))
                throw new SourceException("source_repeated_page", "Magnit: ongeldige vervolgcursus; paginering gestopt.");
            if (jobs.Count > count) throw new SourceException("source_schema", "Magnit: meer identiteiten dan brontotaal.");
        } while (token is not null);
        if (jobs.Count != expected) throw new SourceException("source_schema", "Magnit: inventaris dekt het opgegeven totaal niet.");
        return jobs.ToArray();
    }
    protected override async Task<Job> ReadDetailAsync(string id, CancellationToken ct) => Map(PublicJobData.ParseJson(await GetTextAsync(Api + "/" + id + "/details", ct)), true);
    public static Job Map(JsonElement raw, bool full)
    {
        var id = PublicJobData.Text(raw, "id"); var title = PublicJobData.Text(raw, "title");
        if (id is null || !Guid.TryParseExact(id, "D", out var parsed) || parsed.ToString("D") != id || string.IsNullOrWhiteSpace(title))
            throw new SourceException("source_schema", "Magnit: UUID of titel ontbreekt.");
        var request = PublicJobData.Child(raw, "requestDetails"); var technical = PublicJobData.Child(raw, "technicalDetails");
        var body = PublicJobData.Text(raw, "jobDescription");
        if (full && string.IsNullOrWhiteSpace(body)) throw new SourceException("source_schema", "Magnit: volledige omschrijving ontbreekt.");
        var sections = new List<string>();
        if (body is not null) sections.Add(JobMapper.HtmlText(body));
        foreach (var key in new[] { "jobSkills", "jobEducation", "requiredSkills", "requiredSkillsAndEducation" })
        {
            var field = PublicJobData.Child(raw, key);
            if (field.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)) sections.Add(key + ":\n" + JobMapper.HtmlText(field.ToString()));
        }
        if (PublicJobData.Text(PublicJobData.Child(raw, "clientInfo"), "terms") is { Length: > 0 } terms) sections.Add("Bronvoorwaarden:\n" + JobMapper.HtmlText(terms));
        if (full)
            foreach (var key in new[] { "requestDetails", "candidateRequirements", "technicalDetails" })
            {
                var node = PublicJobData.Child(raw, key);
                if (node.ValueKind != JsonValueKind.Object) continue;
                foreach (var field in node.EnumerateObject().Where(f => f.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) && f.Name is not ("externalProposalUrl" or "showSubcontractedWorkerFields" or "showDuplicatePreventionFields" or "showPreviousExperienceFields")))
                    sections.Add(key + "." + field.Name + ": " + field.Value);
            }
        var location = PublicJobData.Text(full ? request : raw, "location");
        var rate = PublicJobData.Child(request, "rate");
        var amount = PublicJobData.Child(rate, "amount");
        var money = PublicJobData.Number(PublicJobData.Child(raw, "clientInfo"), "rateType") == 2 && amount.ValueKind == JsonValueKind.Number && amount.TryGetDecimal(out var n) && n > 0 ? new[] {
            new Money("rate", null, n, PublicJobData.Text(rate, "currencySymbol"), PublicJobData.Text(rate, "frequency"), rate.ToString(), null, null, new("requestDetails.rate", rate.ToString())) } : [];
        return PublicJobData.Create("magnit", id, "https://magnit-source.magnitglobal.com/browse/job/" + id, title,
            full ? string.Join("\n\n", sections) : "", PublicJobData.Text(PublicJobData.Child(raw, "clientInfo"), "name") ?? PublicJobData.Text(raw, "company"), location is null ? [] : [location],
            PublicJobData.Fact("requestDetails.hoursPerWeek", PublicJobData.Scalar(request, "hoursPerWeek")),
            PublicJobData.Fact("requestDetails.startDate", PublicJobData.Text(request, "startDate")),
            PublicJobData.Fact("requestDetails.endDate", PublicJobData.Text(request, "endDate")), money,
            PublicJobData.Date(technical, "dateFirstPublished"), PublicJobData.Date(request, "submissionDeadline"));
    }
}
