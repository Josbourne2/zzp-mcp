using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;

namespace ZzpMcp;

public sealed partial class JobService(IEnumerable<IJobSource> sources, JobStore? store = null)
{
    private readonly Dictionary<string, IJobSource> _sources = sources.ToDictionary(x => x.Id);
    private readonly ConcurrentDictionary<string, PendingSearch> _searches = new();
    private sealed class PendingSearch(SearchRequest request, SourceSearchResult[] results)
    {
        public SearchRequest Request { get; } = request;
        public SourceSearchResult[] Results { get; set; } = results;
        public DateTimeOffset Created { get; } = DateTimeOffset.UtcNow;
        public SemaphoreSlim Gate { get; } = new(1);
    }
    public object ListSources() => _sources.Values.Select(x => new { id = x.Id, capabilities = x.Capabilities }).ToArray();
    private IJobSource Source(string id) => !string.IsNullOrWhiteSpace(id) && _sources.TryGetValue(id, out var s) ? s : throw new SourceException("unknown_source", "Deze bron heeft geen geïmplementeerde adapter.");
    public async Task<Job> GetAsync(string source, string id, CancellationToken ct, string? sessionId = null)
    {
        var adapter = Source(source);
        ValidateSession(sessionId);
        var cached = store?.Read(source, id, adapter.PortalId)?.Detail;
        var job = cached ?? await adapter.GetAsync(id, ct);
        long? versionId = null;
        var storage = Persist(() => versionId = store!.SaveDetail(job, adapter.PortalId, sessionId), sessionId: sessionId);
        return job with { Storage = storage is null ? null : storage with { VersionId = versionId, CacheUsed = cached is not null } };
    }
    public Task<SearchPage> SearchAsync(string source, SearchRequest request, CancellationToken ct) =>
        SearchPageAsync(source, request, Guid.NewGuid().ToString("N"), ct);

    private async Task<SearchPage> SearchPageAsync(string source, SearchRequest request, string searchId, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        SearchValidation.Validate(request, 0);
        var adapter = Source(source);
        ValidateSession(request.SessionId);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source + Json.Write(request with { Cursor = null }))));
        var offset = 0;
        string? previousPage = null;
        if (request.Cursor is not null)
        {
            var pieces = request.Cursor.Split(':');
            if (pieces.Length != 3 || pieces[0] != fingerprint || !int.TryParse(pieces[1], out offset) || offset is < 0 or >= 1000)
                throw new SourceException("invalid_cursor", "Cursor hoort niet bij deze bron/zoekparameters, is ongeldig of overschrijdt de grens.");
            previousPage = pieces[2];
        }
        SourcePage page;
        try
        {
            var connection = await adapter.ConnectAsync(ct);
            if (!connection.Ready) throw new SourceException(connection.State, connection.Message);
            page = await adapter.SearchAsync(request, offset, ct);
        }
        catch (SourceException ex)
        {
            var saved = Persist(() => store!.SaveSearch(searchId, source, adapter.PortalId, request, null, null,
                false, ex.Code is "login_required" or "connecting" ? ex.Code : "error", ex.Code, startedAt), searchId, request.SessionId);
            if (saved?.State == "failed") throw new SourceException(ex.Code, ex.Message + " Ook het opslaan van deze bronstatus is mislukt.");
            throw;
        }
        static bool Matches(ContractPermission value, Permission? requested) => !requested.HasValue ||
            value.Status == requested.Value && (requested == Permission.Unknown || !value.Conflict);
        var filtered = page.Jobs.Where(j => Matches(j.Zzp, request.Zzp) && Matches(j.Payroll, request.Payroll) &&
            (!page.QueryIsLocal || string.IsNullOrWhiteSpace(request.Query) ||
             string.Join('\n', j.Title, j.Client, j.Text).Contains(request.Query.Trim(), StringComparison.OrdinalIgnoreCase))).ToArray();
        var consumedOffset = offset + page.Consumed;
        var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(',', page.Jobs.Select(j => j.SourceId).Order()))));
        var hasMore = page.HasMore ?? (page.Total.HasValue ? consumedOffset < page.Total.Value : page.Consumed == request.PageSize);
        var incomplete = page.Consumed == 0 && hasMore || consumedOffset >= 1000 && hasMore || previousPage == signature;
        int? next = !incomplete && hasMore ? consumedOffset : null;
        var local = request.Zzp.HasValue || request.Payroll.HasValue || page.QueryIsLocal && !string.IsNullOrWhiteSpace(request.Query);
        var result = new SearchPage(filtered.Select(j => new JobSummary(j.Source, j.SourceId, j.Identity, j.Url, j.Title, j.Client, j.Locations, j.Hours, j.Hybrid, j.Duration, j.Compensation, j.Zzp, j.Payroll, j.FetchedAt)).ToArray(),
            page.Total, local ? null : page.Total, page.Consumed, next, next.HasValue ? $"{fingerprint}:{next}:{signature}" : null, incomplete,
            (page.Warnings ?? []).Concat(incomplete ? ["Brondekking onvolledig: lege/herhaalde pagina of scanlimiet vóór het brontotaal.", "Paginering is geen snapshot; bronwijzigingen kunnen overlap en omissies geven."] : new[] { "Paginering is geen snapshot; bronwijzigingen kunnen overlap en omissies geven." }).ToArray(),
            page.QueryIsLocal ? (object)new { scope = "ongefilterde openbare bronlijst; geen bronzoekterm of kilometerfilter" } : new { request.Query, request.Location, request.RadiusKm, request.Categories },
            new { request.Zzp, request.Payroll, query = page.QueryIsLocal ? request.Query : null,
                scope = page.QueryIsLocal ? "letterlijke zoekterm in titel, opdrachtgever en volledige tekst van alleen deze bronpagina; vervolg ook bij nul treffers" : "alleen deze bronpagina; onbekend voldoet niet aan allowed" },
            page.Facets, page.Facets.Length > 0 ? "bronwaargenomen histogram voor deze zoekopdracht; geen gegarandeerd volledige taxonomie" : "Deze bron publiceert via deze adapter geen facetten.");
        return result with { Details = page.IncludeDetails ? filtered : null, Storage = Persist(() => store!.SaveSearch(searchId, source, adapter.PortalId, request,
            page, result, adapter.SearchIncludesDetails, "completed", startedAt: startedAt), searchId, request.SessionId) };
    }

    public async Task<AllSourcesSearch> SearchAllAsync(SearchRequest request, CancellationToken ct)
    {
        SearchValidation.Validate(request, 0);
        ValidateSession(request.SessionId);
        if (request.Cursor is not null) throw new SourceException("invalid_cursor", "Gebruik vervolgcursors per bron, met source en dezelfde zoekparameters.");
        if (request.Categories is { Count: > 0 }) throw new SourceException("unsupported_filter", "Categorie-ID's zijn bronspecifiek; zoek daarmee met een expliciete source.");
        var id = Guid.NewGuid().ToString("N");
        var pending = new PendingSearch(request, []);
        lock (_searches)
        {
            foreach (var entry in _searches.Where(x => DateTimeOffset.UtcNow - x.Value.Created > TimeSpan.FromMinutes(30))) _searches.TryRemove(entry.Key, out _);
            if (_searches.Count >= 32) throw new SourceException("search_limit", "Te veel bewaarde zoekacties; hervat een bestaande actie of wacht tot die verlopen is.");
            _searches[id] = pending;
        }
        try { pending.Results = await Task.WhenAll(_sources.Keys.Select(source => SearchOneAsync(source, request, id, ct))); }
        catch { _searches.TryRemove(id, out _); throw; }
        return Result(id, pending);
    }

    public async Task<AllSourcesSearch> ResumeAsync(string id, CancellationToken ct)
    {
        if (!_searches.TryGetValue(id, out var pending) || DateTimeOffset.UtcNow - pending.Created > TimeSpan.FromMinutes(30))
            throw new SourceException("search_expired", "Deze bewaarde zoekactie is onbekend, verlopen of de server is herstart. Start opnieuw met search_jobs.");
        await pending.Gate.WaitAsync(ct);
        try
        {
            pending.Results = await Task.WhenAll(pending.Results.Select(result => result.State == "completed" ? Task.FromResult(result) : SearchOneAsync(result.Source, pending.Request, id, ct)));
            return Result(id, pending);
        }
        finally { pending.Gate.Release(); }
    }

    private async Task<SourceSearchResult> SearchOneAsync(string source, SearchRequest request, string searchId, CancellationToken ct)
    {
        try
        {
            return new(source, "completed", await SearchPageAsync(source, request, searchId, ct), null, null);
        }
        catch (SourceException ex) { return new(source, ex.Code is "login_required" or "connecting" ? ex.Code : "error", null, ex.Code, ex.Message); }
    }

    public ResearchSession StartSession(string? name = null) => SessionOperation(s => s.StartSession(name));
    public SessionList ListSessions(int pageSize = 20, int offset = 0) => SessionOperation(s => s.ListSessions(pageSize, offset));
    public SessionView GetSession(string sessionId, string section = "jobs", int pageSize = 20, long afterId = 0) =>
        SessionOperation(s => s.GetSession(sessionId, section, pageSize, afterId));
    public SessionReview MarkReviewed(string sessionId, string source, string sourceId, long versionId) =>
        SessionOperation(s => s.MarkReviewed(sessionId, source, sourceId, versionId, Source(source).PortalId));

    private void ValidateSession(string? sessionId)
    {
        if (sessionId is not null) SessionOperation(s => { s.ValidateSession(sessionId); return true; });
    }

    private T SessionOperation<T>(Func<JobStore, T> action)
    {
        if (store is null) throw new SourceException("storage_unavailable", "Onderzoekssessies vereisen duurzame SQLite-opslag.");
        try { return action(store); }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { throw new SourceException("storage_error", "Onderzoekssessie kon niet worden gelezen of opgeslagen. Er is geen succesvolle mutatie bevestigd."); }
    }

    private StorageResult? Persist(Action save, string? searchId = null, string? sessionId = null)
    {
        if (store is null) return null;
        try { save(); return new("saved", searchId, SessionId: sessionId); }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new("failed", searchId, "Bron is gelezen, maar duurzame opslag is mislukt; deze actie is teruggedraaid. De bronresultaten blijven beschikbaar.", sessionId);
        }
    }

    private static AllSourcesSearch Result(string id, PendingSearch search) => new(id,
        search.Results.All(x => x.State == "completed" && x.Page?.Incomplete != true),
        search.Results.Where(x => x.Page is not null).SelectMany(x => x.Page!.Jobs).DistinctBy(j => j.Identity).ToArray(),
        search.Results,
        search.Results.Where(x => x.State == "login_required").Select(x => new SearchAction(x.Source, x.Message ?? "Handmatige login nodig.", "resume_search", id)).ToArray(),
        SessionId: search.Request.SessionId);
}
