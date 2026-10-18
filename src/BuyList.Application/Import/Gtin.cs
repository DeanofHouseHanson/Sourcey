namespace BuyList.Application.Import;

/// <summary>
/// GTIN validation and canonical form. Canonical = GTIN-13 for EAN-13 and UPC-A (a UPC is an EAN with a leading 0),
/// so the same product matches whichever form a supplier or Amazon uses. EAN-8 and GTIN-14 (case/pack level) keep
/// their own length because they identify different things and cannot be converted safely.
/// </summary>
public static class Gtin
{
    public static bool TryNormalise(string? raw, out string canonical, out string? error)
    {
        canonical = string.Empty;
        error = null;
        var digits = (raw ?? string.Empty).Trim().Replace(" ", "").Replace("-", "");

        if (digits.Length == 0) { error = "EAN is empty"; return false; }
        if (digits.Any(c => c is < '0' or > '9'))
        {
            error = digits.Contains('E', StringComparison.OrdinalIgnoreCase)
                ? $"EAN '{raw}' looks like spreadsheet scientific notation; format the column as text"
                : $"EAN '{raw}' must contain digits only";
            return false;
        }
        if (digits.Length is not (8 or 12 or 13 or 14))
        {
            error = $"EAN '{raw}' has {digits.Length} digits; expected 8, 12, 13 or 14 (spreadsheets often drop leading zeros)";
            return false;
        }
        if (!HasValidCheckDigit(digits))
        {
            error = $"EAN '{raw}' has an invalid check digit";
            return false;
        }

        canonical = digits.Length == 12 ? "0" + digits : digits;
        return true;
    }

    /// <summary>Convenience for matching: canonical GTIN or null if absent/invalid.</summary>
    public static string? NormaliseOrNull(string? raw) => TryNormalise(raw, out var c, out _) ? c : null;

    // GS1 mod-10: from the right (excluding the check digit) weights alternate 3,1,3,1...
    private static bool HasValidCheckDigit(string digits)
    {
        var sum = 0;
        for (var i = digits.Length - 2; i >= 0; i--)
        {
            var weight = (digits.Length - 2 - i) % 2 == 0 ? 3 : 1;
            sum += (digits[i] - '0') * weight;
        }
        return (10 - sum % 10) % 10 == digits[^1] - '0';
    }
}
