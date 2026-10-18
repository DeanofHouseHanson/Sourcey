using BuyList.Application.Import;

namespace BuyList.Tests.Import;

public class GtinTests
{
    // Check digit = (10 - sum mod 10) mod 10, weighting the body digits 3,1,3,1... from the right.
    // e.g. EAN-13 body 501234567890: 0*3+9*1+8*3+7*1+6*3+5*1+4*3+3*1+2*3+1*1+0*3+5*1 = 90 -> check digit 0.
    [Theory]
    [InlineData("5012345678900", "5012345678900")]    // EAN-13 unchanged
    [InlineData("036000291452", "0036000291452")]     // UPC-A -> padded to GTIN-13
    [InlineData("96385074", "96385074")]              // EAN-8 keeps its length
    [InlineData("12345678901231", "12345678901231")]  // GTIN-14 keeps its length
    [InlineData(" 5012345678900 ", "5012345678900")]  // whitespace trimmed
    [InlineData("501 2345-678900", "5012345678900")]  // spaces and hyphens ignored
    public void Valid_codes_normalise(string raw, string expected)
    {
        Assert.True(Gtin.TryNormalise(raw, out var canonical, out var error), error);
        Assert.Equal(expected, canonical);
    }

    [Fact]
    public void Upc_and_ean_forms_of_the_same_product_normalise_identically()
    {
        Assert.Equal(Gtin.NormaliseOrNull("036000291452"), Gtin.NormaliseOrNull("0036000291452"));
    }

    [Theory]
    [InlineData("5012345678901")]   // EAN-13 with the wrong check digit
    [InlineData("036000291453")]    // UPC-A with the wrong check digit
    [InlineData("96385075")]        // EAN-8 with the wrong check digit
    public void Bad_check_digits_are_rejected(string raw)
    {
        Assert.False(Gtin.TryNormalise(raw, out _, out var error));
        Assert.Contains("check digit", error);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("12345", "5 digits")]
    [InlineData("ABC1234567890", "digits only")]
    [InlineData("5.01E+12", "scientific notation")]
    [InlineData("36000291452", "11 digits")]   // UPC whose leading zero was eaten by a spreadsheet
    public void Malformed_codes_are_rejected_with_a_reason(string raw, string fragment)
    {
        Assert.False(Gtin.TryNormalise(raw, out _, out var error));
        Assert.Contains(fragment, error);
    }

    [Fact]
    public void Null_is_not_a_code() => Assert.Null(Gtin.NormaliseOrNull(null));
}
