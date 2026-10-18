namespace BuyList.Application.Import;

/// <summary>
/// Parse + validate a whole file with no side effects (this is the "preview" step). Rows that fail are returned with
/// every reason; nothing is silently dropped. Repeated URLs within the file are rejected after the first occurrence.
/// </summary>
public class OfferFileValidator
{
    private readonly OfferCsvParser _parser;
    private readonly OfferRowValidator _rows;

    public OfferFileValidator(OfferCsvParser parser, OfferRowValidator rows)
    {
        _parser = parser;
        _rows = rows;
    }

    public ImportPreview Validate(Stream csv)
    {
        var parsed = _parser.Parse(csv);
        if (parsed.FatalError is not null)
            return new ImportPreview(Array.Empty<ValidOffer>(), Array.Empty<RejectedRow>(), parsed.FatalError);

        var valid = new List<ValidOffer>();
        var rejected = new List<RejectedRow>();
        var firstSeen = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var row in parsed.Rows)
        {
            var (offer, errors) = _rows.Validate(row);
            var reasons = errors.ToList();
            if (row.Fields.TryGetValue("__shape", out var shape)) { reasons.Add(shape); offer = null; }

            if (offer is not null && reasons.Count == 0)
            {
                if (firstSeen.TryGetValue(offer.SourceUrl, out var firstRow))
                {
                    reasons.Add($"duplicate of row {firstRow} (same url); only the first is imported");
                    offer = null;
                }
                else firstSeen[offer.SourceUrl] = row.RowNumber;
            }

            if (offer is not null && reasons.Count == 0) valid.Add(offer);
            else rejected.Add(new RejectedRow(row.RowNumber,
                row.Fields.Where(f => f.Key != "__shape").ToDictionary(f => f.Key, f => f.Value), reasons));
        }

        return new ImportPreview(valid, rejected, null);
    }

    /// <summary>The rejected rows as CSV with a trailing "reasons" column, safe to open in a spreadsheet.</summary>
    public static string RejectedToCsv(IEnumerable<RejectedRow> rejected)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Join(",", OfferCsvParser.AllColumns.Append("row").Append("reasons")));
        foreach (var r in rejected)
        {
            var cells = OfferCsvParser.AllColumns.Select(c => r.Get(c))
                .Append(r.RowNumber.ToString()).Append(string.Join("; ", r.Reasons));
            sb.AppendLine(string.Join(",", cells.Select(Quote)));
        }
        return sb.ToString();
    }

    private static string Quote(string v) => "\"" + CsvSafe.Cell(v).Replace("\"", "\"\"") + "\"";
}
