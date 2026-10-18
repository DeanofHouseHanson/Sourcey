using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;

namespace BuyList.Application.Import;

/// <summary>Reads the supplier CSV into raw rows. Header names are case-insensitive and may be in any order.</summary>
public class OfferCsvParser
{
    public static readonly string[] RequiredColumns = { "retailer", "url", "price", "vat_included" };
    public static readonly string[] AllColumns = { "retailer", "url", "ean", "mpn", "brand", "title", "price", "vat_included", "stock" };
    public const int MaxRows = 10_000;

    public CsvParseResult Parse(Stream stream)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = false,   // header handled by hand so we can validate it ourselves
            IgnoreBlankLines = true,
            BadDataFound = null,
            MissingFieldFound = null,
        };

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        using var csv = new CsvReader(reader, config);
        var rows = new List<RawOfferRow>();

        try
        {
            if (!csv.Read()) return new CsvParseResult(rows, "The file is empty.");

            var headers = csv.Parser.Record!.Select(h => h.Trim().ToLowerInvariant()).ToArray();
            var dupes = headers.Where(h => h.Length > 0).GroupBy(h => h).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (dupes.Count > 0) return new CsvParseResult(rows, $"Duplicate column(s) in header: {string.Join(", ", dupes)}.");

            var missing = RequiredColumns.Where(c => !headers.Contains(c)).ToList();
            if (missing.Count > 0)
                return new CsvParseResult(rows,
                    $"Missing required column(s): {string.Join(", ", missing)}. Expected header: {string.Join(",", AllColumns)}.");

            while (csv.Read())
            {
                if (rows.Count >= MaxRows)
                    return new CsvParseResult(rows, $"Too many rows: the limit is {MaxRows:N0} per file.");

                var record = csv.Parser.Record!;
                var fields = new Dictionary<string, string>();
                for (var i = 0; i < headers.Length; i++)
                    if (headers[i].Length > 0)
                        fields[headers[i]] = i < record.Length ? record[i] : string.Empty;

                // More values than headers almost always means an unquoted comma shifted the columns, so nothing
                // in the row can be trusted (even if the surplus values happen to be blank).
                if (record.Length > headers.Length)
                    fields["__shape"] = $"row has {record.Length} values but the header has {headers.Length}; quote values containing commas";

                rows.Add(new RawOfferRow(csv.Parser.RawRow, fields));
            }
        }
        catch (CsvHelperException ex)
        {
            return new CsvParseResult(rows, $"The file is not valid CSV near line {csv.Parser.RawRow}: {ex.Message.Split('\n')[0]}");
        }

        return new CsvParseResult(rows, null);
    }
}
