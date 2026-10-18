using BuyList.Domain.Enums;

namespace BuyList.Application.Import;

/// <summary>One CSV record. <paramref name="RowNumber"/> is the physical line in the file (header = 1).</summary>
public sealed record RawOfferRow(int RowNumber, IReadOnlyDictionary<string, string> Fields)
{
    public string Get(string column) => Fields.TryGetValue(column, out var v) ? v.Trim() : string.Empty;
}

public sealed record CsvParseResult(IReadOnlyList<RawOfferRow> Rows, string? FatalError);

/// <param name="Ean">Canonical GTIN, or null if the row had none.</param>
/// <param name="RawEan">The EAN exactly as it appeared in the file.</param>
/// <param name="Matchable">True if the row has an EAN, or both brand and MPN.</param>
public sealed record ValidOffer(
    int RowNumber,
    string Retailer,
    string SourceUrl,
    string? Ean,
    string? RawEan,
    string? Mpn,
    string? Brand,
    string? Title,
    decimal BuyPriceExVat,
    decimal BuyPriceIncVat,
    StockStatus Stock,
    bool Matchable);

public sealed record RejectedRow(int RowNumber, IReadOnlyDictionary<string, string> Fields, IReadOnlyList<string> Reasons)
{
    public string Get(string column) => Fields.TryGetValue(column, out var v) ? v : string.Empty;
}

/// <summary>Everything the user sees before confirming an import. Producing it never touches the database.</summary>
public sealed record ImportPreview(
    IReadOnlyList<ValidOffer> Valid,
    IReadOnlyList<RejectedRow> Rejected,
    string? FatalError)
{
    public int Unmatchable => Valid.Count(v => !v.Matchable);
    public bool CanImport => FatalError is null && Valid.Count > 0;
}

public sealed record MatchSummary(
    int MatchedByEan,
    int MatchedByBrandMpn,
    int ProductsCreated,
    int AmbiguousSkipped,
    int NoProductFound,
    int Unmatchable)
{
    public int TotalMatched => MatchedByEan + MatchedByBrandMpn + ProductsCreated;
}

public sealed record ImportSummary(int Added, int Updated, int Refreshed, int Rejected, MatchSummary Matching);
