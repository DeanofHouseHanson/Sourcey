namespace BuyList.Application.Import;

/// <summary>Neutralises spreadsheet formula injection in CSV we generate (cells starting with = + - @ tab or CR).</summary>
public static class CsvSafe
{
    public static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;
    }
}
