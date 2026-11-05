using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json.Nodes;
using ZzpMcp;

var server = Path.GetFullPath(args.FirstOrDefault(a => a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) ?? "src/ZzpMcp/bin/Debug/net10.0/ZzpMcp.dll");
var configIndex = Array.IndexOf(args, "--config");
var config = configIndex >= 0 ? JsonNode.Parse(File.ReadAllText(args[configIndex + 1]))!["mcpServers"]!["zzp-mcp"] : null;
var serverCommand = config?["command"]?.ToString() ?? "dotnet";
var serverArguments = config?["args"]?.AsArray().Select(x => x!.ToString()).ToArray() ?? [server];
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(args.Contains("--interactive") ? 60 : args.Contains("--browser") || args.Contains("--all-live") ? 30 : 3));
async Task<McpClient> Connect() => await McpClient.CreateAsync(new StdioClientTransport(new() {
    Name = "ZZP MCP smoke", Command = serverCommand, Arguments = serverArguments,
    EnvironmentVariables = new Dictionary<string, string?> { ["ZZPMCP_BROWSER_CHANNEL"] = Environment.GetEnvironmentVariable("ZZPMCP_BROWSER_CHANNEL") }
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
var sources = await Call(client, "list_sources", []);
if (!new[] { "harvey-nash", "vertage" }.All(id => sources.AsArray().Any(s => s!["id"]!.ToString() == id))) throw new Exception("Expected Harvey Nash and Vertage adapters.");
Console.WriteLine("PASS list_sources");
var invalid = await Call(client, "search_jobs", new() { ["pageSize"] = 0 }, true);
if (invalid["error"]?.ToString() != "invalid_argument") throw new Exception("Expected validation error.");
Console.WriteLine("PASS validation");
var unknown = await Call(client, "search_jobs", new() { ["inventedFilter"] = true }, true);
if (unknown["error"]?.ToString() != "invalid_argument") throw new Exception("Unknown filter was ignored.");
Console.WriteLine("PASS unknown filter rejected");
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
