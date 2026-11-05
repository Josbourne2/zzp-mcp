using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ZzpMcp;

public static class SourceSanitizer
{
    // Only vacancy fields are retained. Signed asset links and account/matching state are unnecessary.
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal) {
        "id", "title", "state", "description", "conditions", "startDate", "endDate", "publishedAt", "closingDateOffer",
        "hoursPerWeek", "minHoursPerWeek", "typeOfProfessional", "remoteAllowed", "referenceCode", "countryCode",
        "requirements", "wishes", "remunerationType", "remunerationExplanation", "minHourlyRate", "maxHourlyRate", "locationName"
    };
    public static JsonElement Vacancy(JsonElement raw)
    {
        var result = new JsonObject();
        foreach (var property in raw.EnumerateObject())
            if (Allowed.Contains(property.Name)) result[property.Name] = Clean(property.Value);
        if (raw.TryGetProperty("client", out var client) && client.ValueKind == JsonValueKind.Object)
            result["client"] = new JsonObject { ["name"] = JobMapper.String(client, "name") };
        if (raw.TryGetProperty("location", out var location) && location.ValueKind == JsonValueKind.Object)
            result["location"] = Clean(location);
        return JsonSerializer.SerializeToElement(result);
    }
    private static JsonNode? Clean(JsonElement value) => value.ValueKind switch {
        JsonValueKind.Object => new JsonObject(value.EnumerateObject().Where(p => !Regex.IsMatch(p.Name, "token|cookie|authorization|password|avatar|signature", RegexOptions.IgnoreCase)).Select(p => KeyValuePair.Create(p.Name, Clean(p.Value)))),
        JsonValueKind.Array => new JsonArray(value.EnumerateArray().Select(Clean).ToArray()),
        JsonValueKind.String => JsonValue.Create(Regex.Replace(value.GetString()!, @"https?://[^\s\""'<>]+", m => m.Value.Split('?')[0])),
        _ => JsonNode.Parse(value.GetRawText())
    };
}
