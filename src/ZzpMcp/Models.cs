using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZzpMcp;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) } };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
}
[JsonConverter(typeof(JsonStringEnumConverter<Permission>))]
public enum Permission
{
    [JsonStringEnumMemberName("unknown")] Unknown,
    [JsonStringEnumMemberName("allowed")] Allowed,
    [JsonStringEnumMemberName("not_allowed")] NotAllowed
}
public sealed record Evidence(string Field, string Text);
public sealed record ContractPermission(Permission Status, Evidence[] Evidence, bool Conflict = false);
public sealed record Fact(string Text, Evidence Evidence);
public sealed record Money(string Kind, decimal? Low, decimal? High, string? Currency, string? Unit,
    string Text, string? Vat, string? Costs, Evidence Evidence);
public sealed record Category(string Id, string Name, CategoryValue[] Values);
public sealed record CategoryValue(string Id, string Name);
public sealed record Facet(string Id, string Name, FacetValue[] Values);
public sealed record FacetValue(string Id, string Name, int Count);
public sealed record Job(
    string Source, string SourceId, string Identity, string Url, string Title, string? Client,
    string Text, string[] Locations, Fact? Hours, Fact? Hybrid, Fact? Start, Fact? Duration,
    string[] Requirements, string[] Skills, Money[] Compensation,
    DateTimeOffset? CreatedAt, DateTimeOffset? PublishedAt, DateTimeOffset? UpdatedAt, DateTimeOffset? ExpiresAt,
    DateTimeOffset FetchedAt, ContractPermission Zzp, ContractPermission Payroll,
    Category[] Categories, string[] Warnings, JsonElement Raw, StorageResult? Storage = null);
public sealed record JobSummary(string Source, string SourceId, string Identity, string Url, string Title,
    string? Client, string[] Locations, Fact? Hours, Fact? Hybrid, Fact? Duration, Money[] Compensation,
    ContractPermission Zzp, ContractPermission Payroll, DateTimeOffset FetchedAt, string DetailTool = "get_job");
public sealed record SearchRequest(string Query = "", string? Location = null, double RadiusKm = 25,
    Dictionary<string, string[]>? Categories = null, Permission? Zzp = null, Permission? Payroll = null,
    int PageSize = 20, string? Cursor = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SessionId = null);
public sealed record SourcePage(Job[] Jobs, int? Total, int Consumed, Facet[] Facets, DateTimeOffset FetchedAt, bool? HasMore = null, string[]? Warnings = null, bool IncludeDetails = false, bool QueryIsLocal = false);
public sealed record SearchPage(JobSummary[] Jobs, int? SourceTotal, int? LocalMatchTotal,
    int ConsumedSourceRecords, int? NextOffset, string? NextCursor, bool Incomplete, string[] Warnings,
    object AppliedSourceFilters, object AppliedLocalFilters, Facet[] Facets, string FacetScope, StorageResult? Storage = null, Job[]? Details = null);
public sealed record StorageResult(string State, string? SearchId = null, string? Message = null,
    string? SessionId = null, long? VersionId = null, bool CacheUsed = false);
public sealed class SourceException(string code, string message) : Exception(message)
{ public string Code { get; } = code; }

public interface IJobSource
{
    string Id { get; }
    string PortalId => "";
    bool SearchIncludesDetails => false;
    int CollectionPageSize => 0;
    object CollectionFilters => new { };
    Task<SourcePage> ReadCollectionPageAsync(int page, CancellationToken ct) =>
        throw new SourceException("unsupported_operation", "Deze adapter heeft geen verzamelroute.");
    object Capabilities { get; }
    Task<SourceConnection> ConnectAsync(CancellationToken ct) => Task.FromResult(new SourceConnection("ready", true, "Geen login nodig."));
    Task<SourcePage> SearchAsync(SearchRequest request, int offset, CancellationToken cancellationToken);
    Task<Job> GetAsync(string id, CancellationToken cancellationToken);
}
public sealed record SourceConnection(string State, bool Ready, string Message);
public sealed record SourceSearchResult(string Source, string State, SearchPage? Page, string? Error, string? Message);
public sealed record SearchAction(string Source, string Message, string NextTool, string SearchId);
public sealed record AllSourcesSearch(string SearchId, bool Complete, JobSummary[] Jobs, SourceSearchResult[] Sources, SearchAction[] UserActions, string ResumeTool = "resume_search", string? SessionId = null);
