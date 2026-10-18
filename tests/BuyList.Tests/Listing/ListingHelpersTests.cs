using BuyList.Application.Formatting;
using BuyList.Application.Listing;
using BuyList.Domain.Enums;

namespace BuyList.Tests.Listing;

public class ListingHelpersTests
{
    private static readonly DateTime Now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(30, "just now")]
    [InlineData(60, "1m ago")]
    [InlineData(59 * 60, "59m ago")]
    [InlineData(3600, "1h ago")]
    [InlineData(23 * 3600 + 3599, "23h ago")]
    [InlineData(24 * 3600, "1d ago")]
    [InlineData(5 * 24 * 3600, "5d ago")]
    [InlineData(-120, "just now")]    // clock skew: a time slightly in the future
    public void Time_ago(int secondsAgo, string expected) => Assert.Equal(expected, TimeAgo.Format(Now.AddSeconds(-secondsAgo), Now));

    [Fact]
    public void Amazon_links_for_a_valid_asin()
    {
        Assert.Equal("https://www.amazon.co.uk/dp/B00TESTAAA", AmazonLinks.AmazonUk("B00TESTAAA"));
        Assert.Equal("https://keepa.com/#!product/2-B00TESTAAA", AmazonLinks.Keepa("B00TESTAAA"));
        Assert.Equal("https://sas.selleramp.com/sas/lookup?SasLookup%5Bsearch_term%5D=B00TESTAAA", AmazonLinks.SellerAmp("B00TESTAAA"));
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("b00testaaa")] [InlineData("B00TEST")] [InlineData("B00TEST/../x")] [InlineData("B00TESTAAA?x=1")]
    public void No_links_are_built_from_anything_that_isnt_an_asin(string? asin)
    {
        Assert.Null(AmazonLinks.AmazonUk(asin));
        Assert.Null(AmazonLinks.Keepa(asin));
        Assert.Null(AmazonLinks.SellerAmp(asin));
    }

    private static BuyListRow Row(string? title = "Widget", decimal profit = 4.5m, int? sales = 60, decimal? days = 30m, decimal? avg = 24m, string? asin = "B00TESTAAA") => new(
        1, asin, "5012345678900", title, "Acme", null, "Shop", "https://shop.example/p/1", 8m, 9.6m, Now.AddHours(-2), false, avg,
        profit, 52.94m, 22.5m, sales, 90m, RiskLevel.Medium, 72m, days, 2, false, false, OpportunityFlags.None, 11.4m, 20, OpportunityStatus.New);

    [Fact]
    public void Csv_has_a_header_and_one_line_per_row_with_plain_numbers()
    {
        var csv = BuyListCsv.Build(new[] { Row() }).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, csv.Length);
        Assert.StartsWith("\"Title\",\"Brand\",\"ASIN\"", csv[0]);
        Assert.Contains("\"Widget\",\"Acme\",\"B00TESTAAA\",\"5012345678900\",\"Shop\",\"https://shop.example/p/1\",\"8.00\",\"9.60\"", csv[1]);
        Assert.Contains("\"4.50\",\"52.94\",\"22.50\",\"60\",\"90.00\",\"Medium\",\"72.00\",\"30.00\"", csv[1]);
        Assert.Contains("\"https://www.amazon.co.uk/dp/B00TESTAAA\"", csv[1]);
    }

    [Fact]
    public void Csv_writes_no_data_for_missing_values()
    {
        var line = BuyListCsv.Build(new[] { Row(sales: null, days: null, avg: null) }).Split(Environment.NewLine)[1];
        Assert.Contains("\"no data\"", line);
        Assert.Equal(3, line.Split("\"no data\"").Length - 1);     // sales, avg price, days
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\")")]
    [InlineData("+cmd")] [InlineData("-cmd")] [InlineData("@SUM(A1)")]
    public void Csv_neutralises_formula_injection_in_text_cells(string title)
    {
        var line = BuyListCsv.Build(new[] { Row(title: title) }).Split(Environment.NewLine)[1];
        Assert.StartsWith("\"'" + title.Replace("\"", "\"\""), line);
    }

    [Fact]
    public void Csv_keeps_negative_numbers_numeric()
    {
        var line = BuyListCsv.Build(new[] { Row(profit: -3.25m) }).Split(Environment.NewLine)[1];
        Assert.Contains("\"-3.25\"", line);
        Assert.DoesNotContain("'-3.25", line);
    }

    [Fact]
    public void Csv_escapes_embedded_quotes() =>
        Assert.Contains("\"12\"\" Pan\"", BuyListCsv.Build(new[] { Row(title: "12\" Pan") }));

    [Fact]
    public void Csv_with_no_rows_is_just_the_header() =>
        Assert.Single(BuyListCsv.Build(Array.Empty<BuyListRow>()).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
}
