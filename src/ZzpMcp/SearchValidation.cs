namespace ZzpMcp;

public static class SearchValidation
{
    public static void Validate(SearchRequest request, int offset)
    {
        if (request.Query is null || request.Query.Length > 500 || request.Location?.Length > 200 || request.PageSize is < 1 or > 100 || offset is < 0 or >= 1000 ||
            !double.IsFinite(request.RadiusKm) || request.RadiusKm is <= 0 or > 500)
            throw new SourceException("invalid_argument", "Gebruik query ≤500 tekens, locatie ≤200, pagina 1–100, offset <1000 en radius >0–500 km.");
        if (request.Categories?.Any(x => x.Value is null || x.Value.Length > 20) == true || request.Categories?.Count > 10)
            throw new SourceException("invalid_filter", "Ongeldige categorievoorwaarden.");
        if (request.Zzp.HasValue && !Enum.IsDefined(request.Zzp.Value) || request.Payroll.HasValue && !Enum.IsDefined(request.Payroll.Value))
            throw new SourceException("invalid_filter", "Gebruik allowed, not_allowed of unknown voor ZZP/payroll.");
    }
}
