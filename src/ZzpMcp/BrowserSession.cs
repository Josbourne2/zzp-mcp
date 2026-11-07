using Microsoft.Playwright;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZzpMcp;

public sealed record BrowserStatus(string State, bool ProtectedReadVerified, string Message, string? Host = null, string? ResourceKind = null, int? RecordCount = null);
public interface IBrowserSession : IAsyncDisposable
{
    Task<BrowserStatus> StartAsync(CancellationToken ct);
    Task<BrowserStatus> CheckAsync(CancellationToken ct);
    Task<BrowserStatus> CloseAsync(CancellationToken ct);
}
public interface IStriiveSession : IBrowserSession
{
    Task<JsonElement> ReadJobsAsync(string queryString, CancellationToken ct);
    Task<JsonElement> ReadJobAsync(string id, CancellationToken ct);
}

// Small practice probe, deliberately not a second job adapter. Secrets never leave this class.
public sealed class StriiveBrowserSession : IStriiveSession
{
    private readonly SemaphoreSlim _gate = new(1);
    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private (string Url, Dictionary<string, string> Headers)? _candidate;
    private (string Url, Dictionary<string, string> Headers)? _verified;
    private string? _resumeUrl;
    private bool _wasConnected;
    private int _disposed;
    private static bool AllowedHost(Uri uri) => uri.Scheme == "https" &&
        (uri.Host == "striive.com" || uri.Host.EndsWith(".striive.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host == "vertage.com" || uri.Host.EndsWith(".vertage.com", StringComparison.OrdinalIgnoreCase));

    public async Task<BrowserStatus> StartAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_context is not null) return new("awaiting_verification", false, "Browser is al geopend; controleer de sessie apart.");
            var channel = Environment.GetEnvironmentVariable("ZZPMCP_BROWSER_CHANNEL") ?? "msedge";
            if (channel is not ("msedge" or "chrome"))
                return new("error", false, "ZZPMCP_BROWSER_CHANNEL ondersteunt alleen msedge of chrome.");
            _playwright?.Dispose();
            _playwright = await Playwright.CreateAsync();
            var profile = channel == "chrome" ? "striive-chrome-profile" : "striive-profile";
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZZPMcp", profile);
            _context = await _playwright.Chromium.LaunchPersistentContextAsync(path, new()
            { Headless = false, Channel = channel, Timeout = 15000, AcceptDownloads = false });
            _context.Response += Observe;
            var page = _context.Pages.FirstOrDefault() ?? await _context.NewPageAsync();
            await page.GotoAsync("https://freelancer.vertage.com/inbox", new() { Timeout = 10000, WaitUntil = WaitUntilState.DOMContentLoaded });
            return new("awaiting_login", false, "Log zelf in en open de opdrachten op freelancer.vertage.com. Controleer daarna met browser_status; bevestiging alleen is geen sessiebewijs.");
        }
        catch (PlaywrightException) { return new("error", false, "Browser/loginpagina niet gestart. Installeer de ingestelde browser (Microsoft Edge of Chrome); sluit eventueel een andere ZZP MCP-browser met hetzelfde profiel."); }
        finally { _gate.Release(); }
    }

    private async void Observe(object? sender, IResponse response)
    {
        try
        {
            var request = response.Request;
            if (request.Method != "GET" || request.ResourceType is not ("xhr" or "fetch") || response.Status != 200 ||
                !Uri.TryCreate(response.Url, UriKind.Absolute, out var uri) || !AllowedHost(uri) ||
                uri.Host != "freelancer.vertage.com" || uri.AbsolutePath != "/api/v2/job-requests") return;
            if (!response.Headers.TryGetValue("content-type", out var contentType) || !contentType.Contains("json", StringComparison.OrdinalIgnoreCase)) return;
            // URLs and headers remain in memory; never log tokens, query strings or response contents.
            var headers = await request.AllHeadersAsync();
            _candidate = (response.Url, headers.Where(x => !x.Key.StartsWith(':') && x.Key is not ("host" or "content-length" or "accept-encoding")).ToDictionary());
        }
        catch (PlaywrightException) { /* A closing page cannot establish a session. */ }
    }

    public async Task<BrowserStatus> CheckAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_context is null || _context.Pages.Count == 0) return new("disconnected", false, "Geen open ZZP MCP-browser.");
            var target = _candidate ?? _verified;
            if (target is null && _context.Pages.FirstOrDefault(p => Uri.TryCreate(p.Url, UriKind.Absolute, out var uri) && uri.Host == "freelancer.vertage.com" && uri.AbsolutePath == "/dashboard") is { } dashboard)
            {
                await dashboard.GotoAsync("https://freelancer.vertage.com/inbox", new() { Timeout = 8000, WaitUntil = WaitUntilState.DOMContentLoaded });
                return new("awaiting_verification", false, "Ingelogd dashboard herkend; opdrachtenlijst geopend. Controleer de sessie opnieuw zodra de lijst geladen is.");
            }
            if (target is null) {
                var loginPage = _context.Pages.Any(p => Uri.TryCreate(p.Url, UriKind.Absolute, out var uri) && (uri.Host.StartsWith("login.", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.Contains("login", StringComparison.OrdinalIgnoreCase)));
                return new(loginPage ? "awaiting_login" : "awaiting_verification", false, loginPage ? "Log zelf in in het aparte ZZP MCP-browservenster en hervat daarna de zoekactie." : "De opdrachtenpagina wordt geladen; hervat de zoekactie zodra de lijst beschikbaar is.");
            }
            var (observedUrl, headers) = target.Value;
            var url = _resumeUrl ?? observedUrl;
            await using var response = await _context.APIRequest.GetAsync(url, new() { Headers = headers, Timeout = 8000, MaxRedirects = 0 });
            var contentType = response.Headers.GetValueOrDefault("content-type", "");
            if (SessionProof.RequiresLogin(response.Status, contentType))
            {
                _candidate = null; _verified = null;
                return new(_wasConnected ? "expired" : "awaiting_login", false, "Beschermde leesactie vereist login; log opnieuw in en herhaal dezelfde actie.");
            }
            if (response.Status != 200 || !contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
                return new("error", false, "Beschermde leesactie faalt; dit is geen lege vacaturelijst.");
            // A public JSON endpoint is not evidence. Verify the identical GET fails without the user's session.
            await using var anonymous = await _playwright!.APIRequest.NewContextAsync();
            await using var withoutSession = await anonymous.GetAsync(url, new() { Timeout = 8000, MaxRedirects = 0, Headers = new Dictionary<string, string> { ["Accept"] = "application/json" } });
            if (!SessionProof.IsProtected(response.Status, contentType, withoutSession.Status, withoutSession.Headers.GetValueOrDefault("content-type", "")))
                return new("awaiting_verification", false, "Deze leesactie is ook zonder sessie toegankelijk; nog geen bewijs van beschermde toegang.");
            // Validate JSON but never return its personal data, only that a protected read succeeded.
            using var json = JsonDocument.Parse(await response.BodyAsync());
            var recordCount = SessionProof.JobCount(json.RootElement);
            if (!recordCount.HasValue) return new("error", false, "Beschermde response bevat geen herkenbare vacaturelijst; geen succesvolle leesproef.");
            _verified = (url, headers); _resumeUrl = url; _wasConnected = true;
            var uri = new Uri(url);
            var kind = Regex.IsMatch(uri.AbsolutePath, "job|assignment|opdracht", RegexOptions.IgnoreCase) ? "jobs" : "account";
            return new("connected", true, "Beschermde Vertage-vacaturelijst bereikbaar; dezelfde request zonder sessie vereist login.", uri.Host, kind, recordCount);
        }
        catch (PlaywrightException ex) {
            var reason = ex.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase) ? "certificate" :
                ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ? "timeout" :
                ex.Message.Contains("header", StringComparison.OrdinalIgnoreCase) ? "headers" :
                ex.Message.Contains("closed", StringComparison.OrdinalIgnoreCase) ? "browser_closed" :
                ex.Message.Contains("redirect", StringComparison.OrdinalIgnoreCase) ? "redirect" : "network";
            return new("error", false, "Beschermde leesactie faalt (" + reason + "); sessie niet als bruikbaar vastgesteld.");
        }
        catch (JsonException) { return new("error", false, "Leesactie gaf ongeldige JSON; sessie niet als bruikbaar vastgesteld."); }
        finally { _gate.Release(); }
    }

    public async Task<BrowserStatus> CloseAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { if (_context is not null) await _context.CloseAsync(); _context = null; _candidate = null; _verified = null; _resumeUrl = null; _wasConnected = false; return new("disconnected", false, "Browser gesloten; lokaal profiel blijft behouden voor herlogin."); }
        finally { _gate.Release(); }
    }
    public async Task<JsonElement> ReadJobsAsync(string queryString, CancellationToken ct)
    {
        if (!queryString.StartsWith('?') || queryString.Contains('#') || queryString.Length > 4000)
            throw new SourceException("invalid_argument", "Ongeldige Vertage-zoekparameters.");
        await _gate.WaitAsync(ct);
        try
        {
            var target = _candidate ?? _verified;
            if (_context is null || target is null || !_wasConnected)
                throw new SourceException("login_required", "Log zelf in bij Vertage in het ZZP MCP-browservenster en hervat de zoekactie.");
            await using var response = await _context.APIRequest.GetAsync("https://freelancer.vertage.com/api/v2/job-requests" + queryString,
                new() { Headers = target.Value.Headers, Timeout = 15000, MaxRedirects = 0 });
            var type = response.Headers.GetValueOrDefault("content-type", "");
            if (SessionProof.RequiresLogin(response.Status, type))
            {
                _candidate = null; _verified = null;
                throw new SourceException("login_required", "Vertage-sessie verlopen. Log opnieuw in en hervat dezelfde zoekactie.");
            }
            if (response.Status != 200 || !type.Contains("json", StringComparison.OrdinalIgnoreCase))
                throw new SourceException("source_http", $"Vertage gaf geen geldige JSON-zoekresponse (HTTP {response.Status}).");
            var body = await response.BodyAsync();
            if (body.Length > 10 * 1024 * 1024) throw new SourceException("response_too_large", "Vertage-response groter dan 10 MiB.");
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
        catch (PlaywrightException) { throw new SourceException("source_network", "Vertage-leesactie niet bereikbaar; sessie of netwerk controleren."); }
        catch (JsonException) { throw new SourceException("source_schema", "Vertage gaf ongeldige JSON."); }
        finally { _gate.Release(); }
    }
    public async Task<JsonElement> ReadJobAsync(string id, CancellationToken ct)
    {
        if (!long.TryParse(id, out var number) || number <= 0) throw new SourceException("invalid_argument", "Vertage-ID moet een positief nummer zijn.");
        await _gate.WaitAsync(ct);
        try
        {
            var target = _candidate ?? _verified;
            if (_context is null || target is null || !_wasConnected) throw new SourceException("login_required", "Vertage vereist login; verbind de bron en herhaal details ophalen.");
            // Exact detail route supplied by the user; reuse only the protected local session.
            await using var response = await _context.APIRequest.GetAsync(DetailUrl(number),
                new() { Headers = target.Value.Headers, Timeout = 15000, MaxRedirects = 0 });
            var type = response.Headers.GetValueOrDefault("content-type", "");
            if (SessionProof.RequiresLogin(response.Status, type)) { _candidate = null; _verified = null; _wasConnected = false; throw new SourceException("login_required", "Vertage-sessie verlopen; log opnieuw in."); }
            if (response.Status == 404) throw new SourceException("not_found", "Vertage-opdracht niet meer gevonden.");
            if (response.Status != 200 || !type.Contains("json", StringComparison.OrdinalIgnoreCase)) throw new SourceException("source_http", $"Vertage-detail gaf geen JSON (HTTP {response.Status}).");
            var body = await response.BodyAsync();
            if (body.Length > 10 * 1024 * 1024) throw new SourceException("response_too_large", "Vertage-detail groter dan 10 MiB.");
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
        catch (PlaywrightException) { throw new SourceException("source_network", "Vertage-detail niet opgehaald; controleer login, beschikbaarheid of gewijzigde bronroute."); }
        catch (JsonException) { throw new SourceException("source_schema", "Vertage-detail gaf ongeldige JSON."); }
        finally { _gate.Release(); }
    }
    public static string DetailUrl(long id)
    {
        if (id <= 0) throw new SourceException("invalid_argument", "Vertage-ID moet een positief nummer zijn.");
        return "https://freelancer.vertage.com/api/job-requests/" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await CloseAsync(CancellationToken.None); _playwright?.Dispose(); _gate.Dispose();
    }
}

public static class SessionProof
{
    public static int? JobCount(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root.EnumerateArray().All(x => x.ValueKind == JsonValueKind.Object) ? root.GetArrayLength() : null;
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _) || root.TryGetProperty("errors", out _)) return null;
        foreach (var key in new[] { "content", "items", "results", "data", "jobRequests", "job_requests" })
            if (root.TryGetProperty(key, out var child) && JobCount(child) is { } count) return count;
        return null;
    }
    public static bool RequiresLogin(int status, string contentType) => status is 401 or 403 || status is >= 300 and < 400 || status == 200 && contentType.Contains("html", StringComparison.OrdinalIgnoreCase);
    public static bool IsProtected(int authenticatedStatus, string authenticatedType, int anonymousStatus, string anonymousType) =>
        authenticatedStatus == 200 && authenticatedType.Contains("json", StringComparison.OrdinalIgnoreCase) && RequiresLogin(anonymousStatus, anonymousType);
}

