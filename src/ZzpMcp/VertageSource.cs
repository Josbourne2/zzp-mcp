using System.Globalization;
using System.Text.Json;

namespace ZzpMcp;

public sealed class VertageSource(IStriiveSession session, JobStore? store = null) : IJobSource
{
    public string Id => "vertage";
    public bool SearchIncludesDetails => true;
    public int CollectionPageSize => 1000;
    public object CollectionFilters => new { maxRadius = 100, maxHoursPerWeek = 40, useMatchingScore = true,
        scope = "Door gebruiker opgegeven bronvoorwaarden; matching kan accountafhankelijk zijn." };
    public object Capabilities => new {
        name = "Striive / Vertage", connection = "manual_browser_login", search = new[] { "query", "location", "radiusKm (bronsemantiek)", "zzp (local)", "payroll (local)" },
        pageSize = new { min = 1, max = 100 }, maxOffset = 1000, categories = "niet ondersteund; geen Harvey Nash-categorie-ID's gebruiken",
        detail = "Zoeken haalt voor iedere treffer volledige details op via /api/job-requests/{id}. collect_jobs verzamelt de lijst met size=1000 en haalt alle details in hervatbare batches op.",
        pagination = "pagina-gebaseerd; brontotaal onbekend, volle pagina heeft vervolg",
        querySemantics = "searchText is bronafhankelijk; C# en .NET geven verschillende resultaten. Host gebruikt zo nodig synoniemen."
    };
    public async Task<SourceConnection> ConnectAsync(CancellationToken ct)
    {
        var status = await session.CheckAsync(ct);
        if (status.State == "disconnected") {
            var started = await session.StartAsync(ct);
            if (started.State == "error") return new("error", false, started.Message);
            status = await session.CheckAsync(ct);
        }
        var state = status.ProtectedReadVerified ? "ready" : status.State == "error" ? "error" : status.State is "expired" or "awaiting_login" ? "login_required" : "connecting";
        var message = state switch {
            "ready" => "Beschermde Vertage-sessie bruikbaar.",
            "connecting" => "De opdrachtenpagina wordt geladen. Hervat de zoekactie na kort wachten.",
            "error" => status.Message,
            _ => "Log zelf in bij Striive/Vertage in het aparte ZZP MCP-browservenster. Hervat daarna de bewaarde zoekactie; geef geen wachtwoord in de chat."
        };
        return new(state, status.ProtectedReadVerified, message);
    }
    public async Task<SourcePage> SearchAsync(SearchRequest request, int offset, CancellationToken ct)
    {
        SearchValidation.Validate(request, offset);
        if (request.Categories is { Count: > 0 }) throw new SourceException("unsupported_filter", "Vertage ondersteunt geen Harvey Nash-categorie-ID's.");
        if (offset % request.PageSize != 0) throw new SourceException("invalid_cursor", "Vertage-offset moet bij de paginagrootte horen.");
        var query = $"?page={offset / request.PageSize}&size={request.PageSize}&maxRadius={request.RadiusKm.ToString(CultureInfo.InvariantCulture)}&sortBy=&sortOrder=DESCENDING&clientNames=&professionalTypes=&remoteAllowed=&locations={Uri.EscapeDataString(request.Location ?? "")}&skills=&maxHoursPerWeek=40&useMatchingScore=true&searchText={Uri.EscapeDataString(request.Query)}";
        var raw = await session.ReadJobsAsync(query, ct);
        if (raw.ValueKind != JsonValueKind.Array || raw.GetArrayLength() > request.PageSize)
            throw new SourceException("source_schema", "Vertage gaf geen geldige begrensde vacaturelijst.");
        var fetched = DateTimeOffset.UtcNow;
        var listings = raw.EnumerateArray().Select(j => VertageMapper.Map(j, fetched, false)).DistinctBy(j => j.SourceId).ToArray();
        var jobs = new List<Job>();
        foreach (var listing in listings)
        {
            ct.ThrowIfCancellationRequested();
            var cached = store?.Read(Id, listing.SourceId)?.Detail;
            jobs.Add(cached is null ? await ReadDetailAsync(listing.SourceId, ct) : cached with { Storage = new("saved", CacheUsed: true) });
        }
        return new(jobs.ToArray(), null, raw.GetArrayLength(), [], fetched, raw.GetArrayLength() == request.PageSize,
            ["Vertage-brontotaal onbekend. useMatchingScore=true en maxHoursPerWeek=40 zijn expliciete bronvoorwaarden. Bronlocatie-/afstandsemantiek niet vastgesteld."], IncludeDetails: true);
    }

    public async Task<SourcePage> ReadCollectionPageAsync(int page, CancellationToken ct)
    {
        if (page < 0) throw new SourceException("invalid_argument", "Bronpagina mag niet negatief zijn.");
        var query = $"?page={page}&size=1000&maxRadius=100&sortBy=&sortOrder=DESCENDING&clientNames=&professionalTypes=&remoteAllowed=&locations=&skills=&maxHoursPerWeek=40&useMatchingScore=true";
        var raw = await session.ReadJobsAsync(query, ct);
        if (raw.ValueKind != JsonValueKind.Array || raw.GetArrayLength() > CollectionPageSize)
            throw new SourceException("source_schema", "Vertage gaf geen geldige lijst van maximaal 1000 vacatures.");
        var at = DateTimeOffset.UtcNow;
        return new(raw.EnumerateArray().Select(j => VertageMapper.Map(j, at, false)).DistinctBy(j => j.SourceId).ToArray(),
            null, raw.GetArrayLength(), [], at, raw.GetArrayLength() == CollectionPageSize);
    }
    public async Task<Job> GetAsync(string id, CancellationToken ct)
    {
        if (!long.TryParse(id, out var numericId) || numericId <= 0) throw new SourceException("invalid_argument", "Vertage-ID moet een positief nummer zijn.");
        id = numericId.ToString(CultureInfo.InvariantCulture);
        var connection = await ConnectAsync(ct);
        if (!connection.Ready) throw new SourceException(connection.State, connection.Message);
        return await ReadDetailAsync(id, ct);
    }
    private async Task<Job> ReadDetailAsync(string id, CancellationToken ct)
    {
        var raw = await session.ReadJobAsync(id, ct);
        var job = VertageMapper.Map(raw, DateTimeOffset.UtcNow, true);
        if (job.SourceId != id) throw new SourceException("source_schema", "Vertage-detail-ID komt niet overeen met de gevraagde vacature.");
        return job;
    }
}

public static class VertageMapper
{
    public static Job Map(JsonElement input, DateTimeOffset fetched, bool full)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("id", out var rawId) || !rawId.TryGetInt64(out var number) || number <= 0 || string.IsNullOrWhiteSpace(JobMapper.String(input, "title")))
            throw new SourceException("source_schema", "Vertage-vacature mist id of titel.");
        var raw = SourceSanitizer.Vacancy(input);
        var id = number.ToString(CultureInfo.InvariantCulture);
        var html = JobMapper.String(raw, "description");
        if (full && string.IsNullOrWhiteSpace(html)) throw new SourceException("source_schema", "Volledige Vertage-vacaturetekst ontbreekt.");
        var text = JobMapper.HtmlText((html ?? "") + "<p>" + (JobMapper.String(raw, "conditions") ?? "") + "</p>");
        var requirements = raw.TryGetProperty("requirements", out var r) && r.ValueKind == JsonValueKind.Array ? r.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : JobMapper.String(x, "requirement") ?? x.GetRawText()).ToArray() : [];
        var skills = raw.TryGetProperty("requirements", out var skillFields) && skillFields.ValueKind == JsonValueKind.Array ? skillFields.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object && JobMapper.String(x, "type") == "COMPETENCE").Select(x => JobMapper.String(x, "requirement")).OfType<string>().ToArray() : [];
        // Requirements outside description are retained as source data, not interpreted as tool instructions.
        if (full && requirements.Length > 0) text += "\n\nEisen (bronvelden):\n" + string.Join("\n", requirements);
        Fact? Fact(string name, string? value) => string.IsNullOrWhiteSpace(value) ? null : new(value, new(name, value));
        string? Value(string name) => raw.TryGetProperty(name, out var p) && p.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? p.ToString() : null;
        DateTimeOffset? Date(string name) => DateTimeOffset.TryParse(Value(name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
        decimal? Amount(string name) => raw.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var n) && n > 0 ? n : null;
        var client = raw.TryGetProperty("client", out var c) ? JobMapper.String(c, "name") : null;
        var location = Value("locationName");
        if (location is null && raw.TryGetProperty("location", out var loc)) location = JobMapper.String(loc, "name") ?? JobMapper.String(loc, "city");
        var low = Amount("minHourlyRate"); var high = Amount("maxHourlyRate");
        var money = low.HasValue || high.HasValue ? new[] { new Money("rate", low, high, null, "hour", $"{low}–{high}", null, null, new("minHourlyRate/maxHourlyRate", $"{low}/{high}")) } : Array.Empty<Money>();
        return new("vertage", id, $"vertage:{id}", $"https://freelancer.vertage.com/inbox/all/{id}", JobMapper.String(raw, "title")!, client, text,
            location is null ? [] : [location], Fact("minHoursPerWeek/hoursPerWeek", Value("hoursPerWeek") is { } hours ? $"{(Value("minHoursPerWeek") is { } min ? min + "–" : "")}{hours} uur/week" : null), Fact("remoteAllowed", Value("remoteAllowed")), Fact("startDate", Value("startDate")),
            Fact("startDate/endDate", Value("endDate") is { } end ? $"{Value("startDate")} tot {end}" : null), requirements, skills, money,
            null, Date("publishedAt"), null, Date("closingDateOffer"), fetched, JobMapper.Contract(text, "zzp"), JobMapper.Contract(text, "payroll"),
            Value("typeOfProfessional") is { } contract ? [new("typeOfProfessional", "Contractvorm (broncode; geen automatische ZZP/payroll-conclusie)", [new(contract, contract)])] : [],
            full ? ["Contractbroncode wordt conservatief bewaard; geen payroll/ZZP-toestemming afgeleid zonder expliciet tekstbewijs."] : ["Zoektreffer bevat nog niet de volledige tekst; gebruik get_job vóór matching."], raw);
    }
}
