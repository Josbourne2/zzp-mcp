using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZzpMcp;

public static class JobMapper
{
    public static string? String(JsonElement node, string key) => node.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    public static Job Map(JsonElement raw, DateTimeOffset fetchedAt)
    {
        var id = String(raw, "id");
        var title = String(raw, "title");
        var html = String(raw, "description");
        var slug = String(raw, "url_slug");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title) || html is null || string.IsNullOrWhiteSpace(slug))
            throw new SourceException("source_schema", "De bronvacature mist id, titel, description of url_slug.");
        var text = HtmlText(html);
        var categories = new List<Category>();
        if (raw.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array)
            foreach (var c in cats.EnumerateArray())
                categories.Add(new(String(c, "id") ?? "", String(c, "name") ?? "", c.TryGetProperty("values", out var vs) && vs.ValueKind == JsonValueKind.Array
                    ? vs.EnumerateArray().Select(v => new CategoryValue(String(v, "id") ?? "", String(v, "name") ?? "")).ToArray() : []));
        var locations = raw.TryGetProperty("addresses", out var addresses) && addresses.ValueKind == JsonValueKind.Array
            ? addresses.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray() : [];
        if (locations.Length == 0 && String(raw, "original_location") is { } original) locations = [original];
        var zzp = Contract(text, "zzp");
        var payroll = Contract(text, "payroll");
        var warnings = new List<string> { "Eisen zijn alleen uit herkenbare kopjes gehaald; vaardigheden zijn niet apart geëxtraheerd. Lees de volledige tekst; lege lijsten bewijzen geen afwezigheid." };
        if (zzp.Conflict || payroll.Conflict) warnings.Add("Tegenstrijdig contractbewijs: beoordeel de volledige tekst.");
        if (Date(raw, "expires_at") is { } expiry && expiry < fetchedAt) warnings.Add("De bronvervaldatum ligt vóór de ophaaltijd; beschikbaarheid onzeker.");
        var money = ParseMoney(text, String(raw, "salary_package"), raw);
        if (money.Count(x => x.Low.HasValue) > 1) warnings.Add("Meerdere geldvermeldingen; vergelijk bronvoorwaarden, bedragen zijn niet samengevoegd.");
        return new("harvey-nash", id, $"harvey-nash:{id}", "https://www.harveynash.nl/vacatures/" + Uri.EscapeDataString(slug),
            title.Trim(), categories.FirstOrDefault(x => x.Name == "Clients") is { } client ? string.Join(", ", client.Values.Select(x => x.Name)) : null,
            text, locations, Label(text, "Aantal uren per week|Uren per week|Aantal uren"),
            Label(text, "Op locatie of vanuit huis|Hybride|Thuiswerken"), Label(text, "Verwachte startdatum|Startdatum"),
            Label(text, "Duur van de opdracht|Duur opdracht|Duur"), Section(text, "Dit breng je mee|Functie.?eisen|Eisen|Vereisten"), [],
            money, Date(raw, "created_at"), Date(raw, "published_at"), Date(raw, "updated_at"), Date(raw, "expires_at"), fetchedAt,
            zzp, payroll, categories.ToArray(), warnings.ToArray(), raw.Clone());
    }

    public static string HtmlText(string html)
    {
        var document = new HtmlParser().ParseDocument(html);
        var sb = new StringBuilder();
        void Walk(INode node)
        {
            if (node is IText t) { sb.Append(t.Data); return; }
            if (node is IElement element)
            {
                if (element.LocalName is "script" or "style" or "noscript") return;
                var block = element.LocalName is "p" or "div" or "ul" or "ol" or "li" or "h1" or "h2" or "h3" or "h4" or "tr";
                if (block || element.LocalName == "br") sb.Append('\n');
                if (element.LocalName == "li") sb.Append("- ");
                foreach (var child in node.ChildNodes) Walk(child);
                if (element.LocalName == "a" && element.GetAttribute("href") is { } href && Uri.TryCreate(href, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
                    sb.Append(" (").Append(href).Append(')');
                if (element.LocalName is "td" or "th") sb.Append(" | ");
                if (block) sb.Append('\n');
                return;
            }
            foreach (var child in node.ChildNodes) Walk(child);
        }
        Walk(document.Body!);
        return Regex.Replace(Regex.Replace(sb.ToString().Replace('\u00a0', ' '), @"[^\S\n]+", " "), @"\n[ \t]*\n+", "\n\n").Trim();
    }

    public static ContractPermission Contract(string text, string term)
    {
        var evidence = new List<(Permission Status, Evidence Evidence)>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // Only explicit vacancy statements, never infer from 'interim', 'detachering' or boilerplate terms.
            var negative = Regex.IsMatch(line, $@"\b(?:geen|zonder)\s+{term}\b|\b{term}\s*(?:mogelijk\s*[:?]?\s*(?:nee|niet)|(?:is\s+)?niet\s+(?:mogelijk|toegestaan))", RegexOptions.IgnoreCase);
            var positive = Regex.Matches(line, $@"\b{term}\s*(?:mogelijk\s*[:?]?\s*ja|[:]?\s+(?:is\s+)?toegestaan)\b", RegexOptions.IgnoreCase)
                .Any(match => !Regex.IsMatch(line[..match.Index], @"\b(?:geen|zonder)\s*$", RegexOptions.IgnoreCase));
            if (negative) evidence.Add((Permission.NotAllowed, new("description", line.Trim())));
            if (positive) evidence.Add((Permission.Allowed, new("description", line.Trim())));
        }
        var states = evidence.Select(x => x.Status).Distinct().ToArray();
        return new(states.Length == 1 ? states[0] : Permission.Unknown, evidence.Select(x => x.Evidence).Distinct().ToArray(), states.Length > 1);
    }

    private static Fact? Label(string text, string labels)
    {
        var m = Regex.Match(text, $@"(?:^|\n)\s*(?:{labels})\s*:\s*([^\n]+)", RegexOptions.IgnoreCase);
        return m.Success ? new(m.Groups[1].Value.Trim(), new("description", m.Value.Trim())) : null;
    }
    private static string[] Section(string text, string headers)
    {
        var m = Regex.Match(text, $@"(?:^|\n)(?:{headers})\s*:?\s*\n(?<items>(?:\s*\n?\s*- [^\n]+\n?)+)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups["items"].Value.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().TrimStart('-').Trim()).Where(x => x.Length > 0).ToArray() : [];
    }
    private static DateTimeOffset? Date(JsonElement raw, string name) => raw.TryGetProperty(name, out var p) && p.TryGetInt64(out var seconds) && seconds is >= -62135596800 and <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    private static Money[] ParseMoney(string text, string? package, JsonElement raw)
    {
        var candidates = new List<(string Field, string Text)>();
        if (!string.IsNullOrWhiteSpace(package)) candidates.Add(("salary_package", package.Trim()));
        candidates.AddRange(text.Split('\n').Where(l => Regex.IsMatch(l, @"\b(?:richttarief|max\.? tarief|uurtarief|tarief)\b", RegexOptions.IgnoreCase) && Regex.IsMatch(l, @"€|\bEUR\b|\beuro\b", RegexOptions.IgnoreCase)).Select(l => ("description", l.Trim())));
        var result = new List<Money>();
        foreach (var (field, line) in candidates.Distinct())
        {
            var matches = Regex.Matches(line, @"(?:€\s*(?<n>\d[\d.,]*)|(?<n>\d[\d.,]*)\s*(?:EUR|euro)\b)", RegexOptions.IgnoreCase);
            decimal? amount = matches.Count == 1 ? Number(matches[0].Groups["n"].Value) : null;
            var range = Regex.Match(line, @"(?:€\s*)?(?<low>\d[\d.,]*)\s*[-–]\s*(?:€\s*)?(?<high>\d[\d.,]*)", RegexOptions.IgnoreCase);
            var hasCurrency = Regex.IsMatch(line, @"€|\bEUR\b|\beuro\b", RegexOptions.IgnoreCase);
            var candidateLow = range.Success && hasCurrency ? Number(range.Groups["low"].Value) : amount;
            var candidateHigh = range.Success && hasCurrency ? Number(range.Groups["high"].Value) : amount;
            var kind = Regex.IsMatch(line, @"\btarief\b|\brichttarief\b|\buurtarief\b", RegexOptions.IgnoreCase) ? "rate" : Regex.IsMatch(line, @"salaris|per maand|per jaar", RegexOptions.IgnoreCase) ? "salary" : "unknown";
            string? unit = Regex.IsMatch(line, @"per\s+uur|/\s*uur|uurtarief", RegexOptions.IgnoreCase) ? "hour" : Regex.IsMatch(line, "per maand", RegexOptions.IgnoreCase) ? "month" : Regex.IsMatch(line, "per jaar", RegexOptions.IgnoreCase) ? "year" : null;
            result.Add(new(kind, candidateLow, candidateHigh, hasCurrency ? "EUR" : null, unit, line,
                Regex.IsMatch(line, @"(?:ex\.?|excl\.?|exclusief)\s*btw", RegexOptions.IgnoreCase) ? "excluded" : Regex.IsMatch(line, @"(?:incl\.?|inclusief)\s*btw", RegexOptions.IgnoreCase) ? "included" : null,
                Regex.IsMatch(line, "all-in", RegexOptions.IgnoreCase) ? line : null, new(field, line)));
        }
        // Nonzero numeric fields are evidence, without inventing currency or time unit.
        decimal? Numeric(string n) => raw.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var value) && value != 0 ? value : null;
        var low = Numeric("salary_low"); var high = Numeric("salary_high");
        if (low.HasValue || high.HasValue) result.Add(new("unknown", low, high, null, null, "Numerieke bronvelden", null, null, new("salary_low/salary_high", $"{low}/{high}")));
        return result.ToArray();
    }
    private static decimal? Number(string value)
    {
        if (value.Contains(',') && value.Contains('.')) value = value.LastIndexOf(',') > value.LastIndexOf('.') ? value.Replace(".", "").Replace(',', '.') : value.Replace(",", "");
        else if (value.Contains(',')) value = value.Replace(',', '.');
        // A single separator followed by three digits is ambiguous: retain raw text rather than guess.
        if (Regex.IsMatch(value, @"^\d+[.]\d{3}$")) return null;
        return decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) ? n : null;
    }
}
