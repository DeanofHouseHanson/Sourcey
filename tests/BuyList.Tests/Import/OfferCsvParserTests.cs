using System.Text;
using BuyList.Application.Import;

namespace BuyList.Tests.Import;

public class OfferCsvParserTests
{
    private const string Header = "retailer,url,ean,mpn,brand,title,price,vat_included,stock";
    private static CsvParseResult Parse(string csv, bool bom = false)
    {
        var bytes = (bom ? Encoding.UTF8.GetPreamble() : Array.Empty<byte>()).Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
        return new OfferCsvParser().Parse(new MemoryStream(bytes));
    }

    [Fact]
    public void Reads_rows_with_physical_line_numbers()
    {
        var r = Parse($"{Header}\nShop,https://a.example/1,,,,One,1.00,false,\nShop,https://a.example/2,,,,Two,2.00,false,\n");
        Assert.Null(r.FatalError);
        Assert.Equal(new[] { 2, 3 }, r.Rows.Select(x => x.RowNumber));
        Assert.Equal("Two", r.Rows[1].Get("title"));
    }

    [Fact]
    public void Quoted_fields_with_commas_and_quotes_survive()
    {
        var r = Parse($"{Header}\nShop,https://a.example/1,,,Acme,\"Widget, 2 pack \"\"Pro\"\"\",1.00,false,\n");
        Assert.Equal("Widget, 2 pack \"Pro\"", r.Rows[0].Get("title"));
    }

    [Fact]
    public void Header_is_case_insensitive_and_any_order()
    {
        var r = Parse("PRICE, Vat_Included ,URL,Retailer\n5.00,true,https://a.example/1,Shop\n");
        Assert.Null(r.FatalError);
        Assert.Equal("5.00", r.Rows[0].Get("price"));
        Assert.Equal("Shop", r.Rows[0].Get("retailer"));
    }

    [Fact]
    public void Byte_order_mark_is_ignored() =>
        Assert.Null(Parse($"{Header}\nShop,https://a.example/1,,,,T,1.00,false,\n", bom: true).FatalError);

    [Fact]
    public void Blank_lines_are_skipped() =>
        Assert.Single(Parse($"{Header}\n\nShop,https://a.example/1,,,,T,1.00,false,\n\n").Rows);

    [Fact]
    public void Missing_required_columns_are_named()
    {
        var r = Parse("retailer,price\nShop,1.00\n");
        Assert.Contains("url", r.FatalError);
        Assert.Contains("vat_included", r.FatalError);
    }

    [Fact]
    public void Duplicate_header_columns_are_fatal() =>
        Assert.Contains("Duplicate", Parse("retailer,url,url,price,vat_included\n").FatalError);

    [Fact]
    public void Empty_file_is_fatal() => Assert.NotNull(Parse("").FatalError);

    [Fact]
    public void Unknown_extra_columns_are_ignored() =>
        Assert.Null(Parse("retailer,url,price,vat_included,notes\nShop,https://a.example/1,1,true,hello\n").FatalError);

    [Fact]
    public void Row_limit_is_enforced()
    {
        var sb = new StringBuilder(Header + "\n");
        for (var i = 0; i <= OfferCsvParser.MaxRows; i++) sb.Append($"Shop,https://a.example/{i},,,,T,1.00,false,\n");
        Assert.Contains("Too many rows", Parse(sb.ToString()).FatalError);
    }
}
