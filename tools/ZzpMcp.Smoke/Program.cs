using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json.Nodes;
using ZzpMcp;

var server = Path.GetFullPath(args.FirstOrDefault(a => a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) ?? "src/ZzpMcp/bin/Debug/net10.0/ZzpMcp.dll");
var configIndex = Array.IndexOf(args, "--config");
var config = configIndex >= 0 ? JsonNode.Parse(File.ReadAllText(args[configIndex + 1]))!["mcpServers"]!["zzp-mcp"] : null;
var serverCommand = config?["command"]?.ToString() ?? "dotnet";
var serverArguments = config?["args"]?.AsArray().Select(x => x!.ToString()).ToArray() ?? [server];
var smokeDatabasePath = Environment.GetEnvironmentVariable("ZZPMCP_DATABASE_PATH") ??
    Path.Combine(Path.GetTempPath(), "zzp-mcp-smoke-" + Guid.NewGuid().ToString("N") + ".db");
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(args.Contains("--interactive") ? 60 : args.Contains("--browser") || args.Contains("--all-live") ? 30 : 3));
async Task<McpClient> Connect() => await McpClient.CreateAsync(new StdioClientTransport(new() {
    Name = "ZZP MCP smoke", Command = serverCommand, Arguments = serverArguments,
    EnvironmentVariables = new Dictionary<string, string?> {
        ["ZZPMCP_BROWSER_CHANNEL"] = Environment.GetEnvironmentVariable("ZZPMCP_BROWSER_CHANNEL"),
        ["ZZPMCP_DATABASE_PATH"] = smokeDatabasePath
    }
}), cancellationToken: deadline.Token);
await using var client = await Connect();
var tools = await client.ListToolsAsync(cancellationToken: deadline.Token);
Console.WriteLine("PASS initialize + tools/list: " + string.Join(", ", tools.Select(x => x.Name)));
async Task<JsonNode> Call(McpClient c, string name, Dictionary<string, object?> parameters, bool expectError = false)
{
    var result = await c.CallToolAsync(name, parameters, cancellationToken: deadline.Token);
    var text = result.Content.OfType<TextContentBlock>().First().Text;
    if ((result.IsError == true) != expectError) throw new Exception($"Unexpected result for {name}: {text}");
    return JsonNode.Parse(text)!;
}
// Interactive research uses the configured real database without inserting test sessions or jobs.
if (!args.Contains("--interactive"))
{
var sources = await Call(client, "list_sources", []);
if (!new[] { "harvey-nash", "vertage", "flextender", "freep", "mipublic", "magnit", "liblab", "werken-in-gelderland", "starapple" }.All(id => sources.AsArray().Any(s => s!["id"]!.ToString() == id))) throw new Exception("Expected all nine implemented source adapters.");
Console.WriteLine("PASS list_sources");
var invalid = await Call(client, "search_jobs", new() { ["pageSize"] = 0 }, true);
if (invalid["error"]?.ToString() != "invalid_argument") throw new Exception("Expected validation error.");
Console.WriteLine("PASS validation");
var unknown = await Call(client, "search_jobs", new() { ["inventedFilter"] = true }, true);
if (unknown["error"]?.ToString() != "invalid_argument") throw new Exception("Unknown filter was ignored.");
Console.WriteLine("PASS unknown filter rejected");
if (!tools.Any(t => t.Name == "collect_jobs")) throw new Exception("Missing collect_jobs tool.");
var invalidCollection = await Call(client, "collect_jobs", new() { ["sessionId"] = "invalid", ["batchSize"] = 0 }, true);
if (invalidCollection["error"]?.ToString() != "invalid_argument") throw new Exception("Collection batch validation must precede any source request.");
var session = await Call(client, "start_session", new() { ["name"] = "MCP smokecontrole" });
var sessionId = session["sessionId"]!.ToString();
var emptySession = await Call(client, "get_session", new() { ["sessionId"] = sessionId });
if (emptySession["collectedJobs"]!.GetValue<int>() != 0) throw new Exception("New session must be empty.");
var invalidSession = await Call(client, "search_jobs", new() { ["sessionId"] = "invalid" }, true);
if (invalidSession["error"]?.ToString() != "invalid_session") throw new Exception("Session validation must happen before any source requests.");
var invalidReview = await Call(client, "mark_job_reviewed", new() {
    ["sessionId"] = sessionId, ["source"] = "harvey-nash", ["sourceId"] = "missing", ["versionId"] = 1
}, true);
if (invalidReview["error"]?.ToString() != "version_not_returned") throw new Exception("Unretrieved version must not be marked reviewed.");
var cachedId = Guid.NewGuid().ToString("D");
var cachedAt = DateTimeOffset.UtcNow.AddDays(-1);
var cachedJob = JobMapper.Map(System.Text.Json.JsonSerializer.SerializeToElement(new {
    id = cachedId, title = "Offline MCP-cachecontrole", description = "<p>Volledige opgeslagen tekst.</p>", url_slug = cachedId
}), cachedAt);
new JobStore(smokeDatabasePath).SaveDetail(cachedJob);
var cachedRead = await Call(client, "get_job", new() { ["source"] = "harvey-nash", ["sourceId"] = cachedId, ["sessionId"] = sessionId });
if (cachedRead["cacheUsed"]?.GetValue<bool>() != true || cachedRead["text"]!.ToString() != cachedJob.Text ||
    DateTimeOffset.Parse(cachedRead["fetchedAt"]!.ToString()) != cachedAt) throw new Exception("Cached detail must preserve full text and original fetch time.");
await Call(client, "mark_job_reviewed", new() { ["sessionId"] = sessionId, ["source"] = "harvey-nash", ["sourceId"] = cachedId,
    ["versionId"] = cachedRead["storage"]!["versionId"]!.GetValue<long>() });
await using (var restarted = await Connect())
{
    var restored = await Call(restarted, "get_session", new() { ["sessionId"] = sessionId, ["section"] = "pages" });
    if (restored["session"]!["sessionId"]!.ToString() != sessionId) throw new Exception("Session lost across server processes.");
    if (restored["currentVersionsReviewed"]!.GetValue<int>() != 1) throw new Exception("Review lost across server processes.");
    var sessions = await Call(restarted, "list_sessions", new() { ["pageSize"] = 1 });
    if (sessions["sessions"]![0]!["sessionId"]!.ToString() != sessionId) throw new Exception("Persisted session missing from list_sessions.");
}
Console.WriteLine("PASS collection contract + cached full text + session/review persistence across server processes");
foreach (var (sourceId, jobId) in new[] {
    ("freep", "104289"), ("mipublic", "test-automation-engineer-java-developer-met-testfocus"),
    ("magnit", "7c249ea8-12d6-415c-8b30-32becfb36415"), ("liblab", "allround-medewerker-informatiebeheer"),
    ("werken-in-gelderland", "kwaliteitsmedewerker-wmo-jeugd"), ("starapple", "offline-cache-check") })
{
    var publicJob = PublicJobData.Create(sourceId, jobId, "https://example.invalid/smoke", "Public adapter offline cache check", "Full stored public-source test text.");
    new JobStore(smokeDatabasePath).SaveDetail(publicJob);
    var read = await Call(client, "get_job", new() { ["source"] = sourceId, ["sourceId"] = jobId, ["sessionId"] = sessionId });
    if (read["cacheUsed"]?.GetValue<bool>() != true || read["text"]?.ToString() != publicJob.Text) throw new Exception("Public adapter cache not accessible through MCP.");
    var unsupported = await Call(client, "search_jobs", new() { ["source"] = sourceId, ["location"] = "Utrecht", ["pageSize"] = 1 }, true);
    if (unsupported["error"]?.ToString() != "unsupported_filter") throw new Exception("Public adapter ignored geographic filter.");
}
Console.WriteLine("PASS six public adapters via MCP: cached full texts + explicit unsupported geographic filters, no network requests");
}
if (args.Contains("--interactive"))
{
    Console.WriteLine("MCP client ready. JSON lines: {tool,args,summary}. Exit: exit");
    while (await Console.In.ReadLineAsync() is { } input && input != "exit")
    {
        try {
            var request = JsonNode.Parse(input)!;
            var parameters = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(request["args"]?.ToJsonString() ?? "{}")!;
            var result = await client.CallToolAsync(request["tool"]!.ToString(), parameters, cancellationToken: deadline.Token);
            var payload = JsonNode.Parse(result.Content.OfType<TextContentBlock>().First().Text)!;
            if (request["summary"]?.GetValue<bool>() == true && payload is JsonObject && payload["jobs"] is { } jobs)
                Console.WriteLine(Json.Write(new { isError = result.IsError, searchId = payload["searchId"], complete = payload["complete"], userActions = payload["userActions"],
                    jobs = jobs.AsArray().Select(j => new { source=j!["source"], id=j["sourceId"], title=j["title"], client=j["client"], locations=j["locations"] }),
                    sources = payload["sources"]?.AsArray().Select(s => new { source=s!["source"], state=s["state"], error=s["error"], message=s["message"], nextCursor=s["page"]?["nextCursor"], total=s["page"]?["sourceTotal"] }),
                    nextCursor=payload["nextCursor"], sourceTotal=payload["sourceTotal"] }));
            else Console.WriteLine(payload.ToJsonString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Console.WriteLine(Json.Write(new { clientError = ex.GetType().Name })); }
    }
    await Call(client, "browser_close", []);
}
else if (args.Contains("--browser"))
{
    var readVerified = false;
    Console.WriteLine((await Call(client, "browser_connect", [])).ToJsonString());
    Console.WriteLine("Commands: status, restart, exit. Log zelf in; geen sessiegeheimen in uitvoer.");
    while (await Console.In.ReadLineAsync() is { } command && command != "exit")
    {
        if (command == "restart") { await Call(client, "browser_close", []); Console.WriteLine((await Call(client, "browser_connect", [])).ToJsonString()); }
        else if (command == "status") {
            var status = await Call(client, "browser_status", []);
            readVerified |= status["protectedReadVerified"]?.GetValue<bool>() == true;
            Console.WriteLine(status.ToJsonString());
        }
    }
    await Call(client, "browser_close", []);
    if (!readVerified) { Console.WriteLine("BROWSER CHECK INCOMPLETE: geen beschermde vacatureleesactie bevestigd."); Environment.ExitCode = 2; return; }
}
else if (args.Contains("--all-live"))
{
    var search = await Call(client, "search_jobs", new() { ["location"] = "Utrecht", ["pageSize"] = 3 });
    void Report() => Console.WriteLine(Json.Write(new {
        searchId = search["searchId"], complete = search["complete"], userActions = search["userActions"],
        sources = search["sources"]!.AsArray().Select(s => new { source = s!["source"], state = s["state"], error = s["error"], message = s["message"],
            jobs = s["page"]?["jobs"]?.AsArray().Select(j => new { id = j!["sourceId"], title = j["title"], locations = j["locations"] }), nextCursor = s["page"]?["nextCursor"] })
    }));
    for (var attempt = 0; attempt < 4 && search["sources"]!.AsArray().Any(s => s!["state"]!.ToString() == "connecting"); attempt++)
    { await Task.Delay(1000); search = await Call(client, "resume_search", new() { ["searchId"] = search["searchId"]!.ToString() }); }
    Report();
    Console.WriteLine("Commands: resume, new, detail <source> <id>, exit. 'new' herhaalt zoeken op alle bronnen.");
    while (await Console.In.ReadLineAsync() is { } input && input != "exit")
    {
        if (input == "resume") { search = await Call(client, "resume_search", new() { ["searchId"] = search["searchId"]!.ToString() }); Report(); }
        if (input == "new") { search = await Call(client, "search_jobs", new() { ["location"] = "Utrecht", ["pageSize"] = 3 }); Report(); }
        if (input.StartsWith("detail ", StringComparison.Ordinal)) {
            var parts = input.Split(' ');
            var job = await Call(client, "get_job", new() { ["source"] = parts[1], ["sourceId"] = parts[2] });
            Console.WriteLine($"PASS detail source={job["source"]} id={job["sourceId"]} length={job["text"]!.ToString().Length}");
        }
    }
    await Call(client, "browser_close", []);
    if (search["complete"]?.GetValue<bool>() != true) { Console.WriteLine("ALL-SOURCES CHECK INCOMPLETE: niet alle bronnen doorzocht."); Environment.ExitCode = 2; return; }
}
else if (args.Contains("--starapple-live"))
{
    var session = await Call(client, "start_session", new() { ["name"] = "StarApple live verification" });
    var sid = session["sessionId"]!.ToString();
    var search = await Call(client, "search_jobs", new() { ["source"] = "starapple", ["pageSize"] = 1, ["sessionId"] = sid });
    var firstId = search["jobs"]![0]!["sourceId"]!.ToString();
    var next = await Call(client, "search_jobs", new() { ["source"] = "starapple", ["pageSize"] = 1, ["sessionId"] = sid, ["cursor"] = search["nextCursor"]!.ToString() });
    if (next["jobs"]![0]!["sourceId"]!.ToString() == firstId) throw new Exception("StarApple search cursor did not advance.");
    var detail = await Call(client, "get_job", new() { ["source"] = "starapple", ["sourceId"] = "senior-net-developer-18", ["sessionId"] = sid });
    if (!detail["text"]!.ToString().Contains("Functie eisen") || !detail["compensation"]!.AsArray().Any(m => m!["unit"]?.ToString() == "month") || detail["client"] is not null)
        throw new Exception("StarApple full text, salary period or employer handling incorrect.");
    var collection = await Call(client, "collect_jobs", new() { ["source"] = "starapple", ["sessionId"] = sid, ["batchSize"] = 1 });
    await using var restarted = await Connect();
    var cached = await Call(restarted, "get_job", new() { ["source"] = "starapple", ["sourceId"] = "senior-net-developer-18", ["sessionId"] = sid });
    if (cached["cacheUsed"]?.GetValue<bool>() != true || cached["text"]!.ToString() != detail["text"]!.ToString()) throw new Exception("StarApple detail cache lost after restart.");
    var resumed = await Call(restarted, "collect_jobs", new() { ["source"] = "starapple", ["sessionId"] = sid, ["batchSize"] = 1, ["collectionId"] = collection["collection"]!["collectionId"]!.ToString() });
    if (resumed["collectedJobs"]!.GetValue<int>() <= collection["collectedJobs"]!.GetValue<int>()) throw new Exception("StarApple native collection did not advance after restart.");
    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.All });
    http.DefaultRequestHeaders.UserAgent.ParseAdd("ZZPMcp/0.1");
    using var adapter = new StarAppleSource(http);
    var identities = new HashSet<string>(); var pages = 0;
    for (; pages < 50;)
    {
        var page = await adapter.ReadCollectionPageAsync(pages++, deadline.Token);
        foreach (var job in page.Jobs) if (!identities.Add(job.SourceId)) throw new Exception("StarApple repeated an identity during live inventory.");
        if (page.HasMore == false) break;
        if (pages == 50) throw new Exception("StarApple live inventory hit page limit.");
    }
    Console.WriteLine($"PASS StarApple live: {identities.Count} observed listings across {pages} native pages, full detail length={detail["text"]!.ToString().Length}, monthly salary, distinct search cursor, collection/cache after process restart. Temporary database; no complete detail import.");
}
else if (args.Contains("--flextender-live"))
{
    var session = await Call(client, "start_session", new() { ["name"] = "Flextender live verification" });
    var sid = session["sessionId"]!.ToString();
    var search = await Call(client, "search_jobs", new() { ["source"] = "flextender", ["query"] = "", ["pageSize"] = 1, ["sessionId"] = sid });
    var first = search["details"]![0]!;
    var id = first["sourceId"]!.ToString();
    if (first["text"]!.ToString().Length < 100 || first["locations"]!.AsArray().Count == 0 || search["storage"]!["state"]!.ToString() != "saved")
        throw new Exception("Missing full Flextender text, location or saved search.");
    var next = await Call(client, "search_jobs", new() { ["source"] = "flextender", ["pageSize"] = 1, ["cursor"] = search["nextCursor"]!.ToString(), ["sessionId"] = sid });
    if (next["jobs"]![0]!["sourceId"]!.ToString() == id) throw new Exception("Flextender pagination did not advance.");
    var collection = await Call(client, "collect_jobs", new() { ["source"] = "flextender", ["sessionId"] = sid, ["batchSize"] = 1 });
    if (collection["collectedJobs"]!.GetValue<int>() < 2 || collection["collection"]!["removedJobs"]!.GetValue<int>() != 0)
        throw new Exception("Invalid Flextender collection.");
    await using var restarted = await Connect();
    var cached = await Call(restarted, "get_job", new() { ["source"] = "flextender", ["sourceId"] = id, ["sessionId"] = sid });
    if (cached["cacheUsed"]!.GetValue<bool>() != true || cached["text"]!.ToString() != first["text"]!.ToString())
        throw new Exception("Flextender cache not reused across server processes.");
    var restored = await Call(restarted, "collect_jobs", new() { ["source"] = "flextender", ["sessionId"] = sid,
        ["collectionId"] = collection["collection"]!["collectionId"]!.ToString(), ["batchSize"] = 1 });
    if (restored["collectedJobs"]!.ToString() != collection["collectedJobs"]!.ToString() || restored["fullDetails"]!.GetValue<int>() < collection["fullDetails"]!.GetValue<int>())
        throw new Exception("Flextender collection lost across server processes.");
    Console.WriteLine($"PASS Flextender live: total={search["sourceTotal"]}, fullTextLength={first["text"]!.ToString().Length}, region={first["locations"]}, distinct pagination, saved collection and cached full text after restart");
}
else if (args.Contains("--live"))
{
    var first = await Call(client, "search_jobs", new() { ["source"] = "harvey-nash", ["pageSize"] = 3 });
    var id = first["jobs"]![0]!["sourceId"]!.ToString();
    var second = await Call(client, "search_jobs", new() { ["source"] = "harvey-nash", ["pageSize"] = 3, ["cursor"] = first["nextCursor"]!.ToString() });
    if (second["jobs"]!.AsArray().Count == 0 || second["jobs"]![0]!["sourceId"]!.ToString() == id) throw new Exception("Pagination did not advance.");
    Console.WriteLine($"PASS live pagination: sourceTotal={first["sourceTotal"]}, page1={first["jobs"]!.AsArray().Count}, page2={second["jobs"]!.AsArray().Count}");
    var detail = await Call(client, "get_job", new() { ["source"] = "harvey-nash", ["sourceId"] = id });
    if (detail["text"]!.ToString().Length < 100 || detail["sourceId"]!.ToString() != id || !detail["url"]!.ToString().StartsWith("https://www.harveynash.nl/vacatures/")) throw new Exception("Incomplete details.");
    Console.WriteLine($"PASS live details: id={id}, textLength={detail["text"]!.ToString().Length}, url={detail["url"]}");
    await using var fresh = await Connect();
    var cold = await Call(fresh, "get_job", new() { ["source"] = "harvey-nash", ["sourceId"] = id });
    if (cold["sourceId"]!.ToString() != id || cold["text"]!.ToString() != detail["text"]!.ToString()) throw new Exception("Cold-process details failed or changed during check.");
    Console.WriteLine("PASS details in fresh server without search/cache");
    var empty = await Call(client, "search_jobs", new() { ["source"] = "harvey-nash", ["query"] = "zzp-mcp-no-result-712398zz" });
    if (empty["jobs"]!.AsArray().Count != 0 || empty["sourceTotal"]!.GetValue<int>() != 0) throw new Exception("Expected empty sentinel search.");
    Console.WriteLine("PASS live empty results");
    var facet = first["facets"]!.AsArray().First(x => x!["name"]!.ToString() == "Clients")!;
    var value = facet["values"]![0]!;
    var filtered = await Call(client, "search_jobs", new() { ["source"] = "harvey-nash", ["categories"] = new Dictionary<string, string[]> { [facet["id"]!.ToString()] = [value["id"]!.ToString()] } });
    if (filtered["jobs"]!.AsArray().Any(j => !j!["client"]!.ToString().Contains(value["name"]!.ToString()))) throw new Exception("Source category filter not applied.");
    Console.WriteLine("PASS live category filter using discovered facet IDs");
    var local = await Call(client, "search_jobs", new() { ["source"] = "harvey-nash", ["zzp"] = "not_allowed", ["pageSize"] = 3 });
    if (local["localMatchTotal"] is not null || local["jobs"]!.AsArray().Any(j => j!["zzp"]!["status"]!.ToString() != "not_allowed")) throw new Exception("Local contract filter semantics incorrect.");
    Console.WriteLine("PASS live local contract filter through MCP enum schema");
    var location = await Call(client, "search_jobs", new() { ["source"] = "harvey-nash", ["location"] = "Utrecht", ["radiusKm"] = 8.04672, ["pageSize"] = 3 });
    Console.WriteLine($"PASS live location request: sourceTotal={location["sourceTotal"]}");
}
Console.WriteLine("ALL REQUESTED CHECKS PASSED");
