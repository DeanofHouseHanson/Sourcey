using System.Text;
using BuyList.Application.Import;
using BuyList.Application.Options;
using Microsoft.Extensions.Options;

namespace BuyList.Tests.Import;

public class OfferFileValidatorTests
{
    private const string Header = "retailer,url,ean,mpn,brand,title,price,vat_included,stock\n";
    internal static OfferFileValidator Make() =>
        new(new OfferCsvParser(), new OfferRowValidator(Options.Create(new ProfitOptions())));

    private static ImportPreview Run(string csv) => Make().Validate(new MemoryStream(Encoding.UTF8.GetBytes(csv)));

    [Fact]
    public void Splits_valid_and_rejected_and_loses_nothing()
    {
        var p = Run(Header +
            "Shop,https://a.example/1,,,,Good,1.00,false,\n" +
            "Shop,,,,,No url,1.00,false,\n" +
            "Shop,http://a.example/3,,,,Plain http,1.00,false,\n" +
            "Shop,https://a.example/4,,,,Good too,2.00,true,\n");

        Assert.Equal(2, p.Valid.Count);
        Assert.Equal(2, p.Rejected.Count);
        Assert.Equal(4, p.Valid.Count + p.Rejected.Count);   // every input row accounted for
        Assert.Equal(new[] { 3, 4 }, p.Rejected.Select(r => r.RowNumber));
        Assert.All(p.Rejected, r => Assert.NotEmpty(r.Reasons));
    }

    [Fact]
    public void Missing_url_row_is_listed_with_its_reason()
    {
        var p = Run(Header + "Shop,,,,,No url,1.00,false,\n");
        Assert.Empty(p.Valid);
        Assert.Contains(p.Rejected.Single().Reasons, r => r.Contains("url is required"));
    }

    [Fact]
    public void Duplicate_urls_within_a_file_keep_the_first_only()
    {
        var p = Run(Header +
            "Shop,https://a.example/1,,,,First,1.00,false,\n" +
            "Shop,HTTPS://A.example/1#frag,,,,Second,9.00,false,\n");

        Assert.Equal("First", p.Valid.Single().Title);
        Assert.Contains(p.Rejected.Single().Reasons, r => r.Contains("duplicate of row 2"));
    }

    [Fact]
    public void Extra_values_from_an_unquoted_comma_are_rejected_not_misread()
    {
        var p = Run(Header + "Shop,https://a.example/1,,,Acme,Widget, 2 pack,1.00,false,\n");
        Assert.Empty(p.Valid);
        Assert.Contains(p.Rejected.Single().Reasons, r => r.Contains("quote values containing commas"));
    }

    [Fact]
    public void Fatal_header_problem_is_reported_with_no_rows() =>
        Assert.NotNull(Run("a,b\n1,2\n").FatalError);

    [Fact]
    public void Preview_flags_unmatchable_rows_but_keeps_them()
    {
        var p = Run(Header + "Shop,https://a.example/1,,,,No ids,1.00,false,\n");
        Assert.Equal(1, p.Unmatchable);
        Assert.True(p.CanImport);
    }

    [Fact]
    public void Rejected_csv_escapes_formulas_and_includes_reasons()
    {
        var p = Run(Header + "Shop,,,,,\"=HYPERLINK(\"\"http://evil\"\")\",1.00,false,\n");
        var csv = OfferFileValidator.RejectedToCsv(p.Rejected);
        Assert.Contains("\"'=HYPERLINK(", csv);
        Assert.Contains("url is required", csv);
    }

    // ---- the shipped sample files ----
    private static string SamplePath(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "samples", name))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "samples", name);
    }

    [Fact]
    public void Sample_file_imports_cleanly()
    {
        using var f = File.OpenRead(SamplePath("offers.sample.csv"));
        var p = Make().Validate(f);
        Assert.Null(p.FatalError);
        Assert.Empty(p.Rejected);
        Assert.True(p.Valid.Count >= 30);
        Assert.All(p.Valid, v => Assert.StartsWith("https://", v.SourceUrl));
    }

    [Fact]
    public void Invalid_sample_file_rejects_every_deliberate_error_with_a_reason()
    {
        using var f = File.OpenRead(SamplePath("offers.invalid.csv"));
        var p = Make().Validate(f);
        Assert.Equal(7, p.Rejected.Count);
        Assert.Equal(1, p.Valid.Count);      // the "unmatchable" row imports
        Assert.Equal(1, p.Unmatchable);
        Assert.All(p.Rejected, r => Assert.NotEmpty(r.Reasons));
    }
}
