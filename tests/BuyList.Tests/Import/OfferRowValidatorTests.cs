using BuyList.Application.Import;
using BuyList.Application.Options;
using BuyList.Domain.Enums;
using Microsoft.Extensions.Options;

namespace BuyList.Tests.Import;

public class OfferRowValidatorTests
{
    private static readonly OfferRowValidator Validator = new(Options.Create(new ProfitOptions { VatRate = 0.20m }));

    private static RawOfferRow Row(params (string Key, string Value)[] overrides)
    {
        var f = new Dictionary<string, string>
        {
            ["retailer"] = "Shop", ["url"] = "https://shop.example/p/1", ["ean"] = "5012345678900", ["mpn"] = "M-1",
            ["brand"] = "Acme", ["title"] = "Widget", ["price"] = "12.00", ["vat_included"] = "false", ["stock"] = "in stock"
        };
        foreach (var (k, v) in overrides) f[k] = v;
        return new RawOfferRow(2, f);
    }

    private static IReadOnlyList<string> Errors(params (string, string)[] o) => Validator.Validate(Row(o)).Errors;

    [Fact]
    public void A_good_row_is_accepted()
    {
        var (offer, errors) = Validator.Validate(Row());
        Assert.Empty(errors);
        Assert.NotNull(offer);
        Assert.Equal("5012345678900", offer!.Ean);
        Assert.Equal(StockStatus.InStock, offer.Stock);
        Assert.True(offer.Matchable);
    }

    // ---- the mandatory source link ----
    [Theory]
    [InlineData("")]                                      // missing
    [InlineData("   ")]
    [InlineData("shop.example/p/1")]                      // no scheme
    [InlineData("/products/relative")]                    // relative
    [InlineData("http://shop.example/p/1")]               // not https
    [InlineData("ftp://shop.example/p/1")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://")]                              // no host
    [InlineData("https://user:pass@shop.example/p/1")]    // embedded credentials
    [InlineData("not a url")]
    public void Missing_or_invalid_urls_are_rejected(string url)
    {
        var (offer, errors) = Validator.Validate(Row(("url", url)));
        Assert.Null(offer);
        Assert.Contains(errors, e => e.Contains("url", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Missing_url_error_explains_why_it_matters() =>
        Assert.Contains(Errors(("url", "")), e => e.Contains("source link"));

    [Fact]
    public void Urls_are_normalised_so_spelling_variants_dedupe()
    {
        var a = Validator.Validate(Row(("url", "HTTPS://Shop.Example/p/1#reviews"))).Offer!;
        var b = Validator.Validate(Row(("url", "https://shop.example/p/1"))).Offer!;
        Assert.Equal(b.SourceUrl, a.SourceUrl);
    }

    // ---- other required fields ----
    [Fact] public void Retailer_is_required() => Assert.Contains(Errors(("retailer", "")), e => e.Contains("retailer"));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("12,50")]       // European decimal comma must not be read as 1250
    [InlineData("1.2.3")]
    [InlineData("1,23,456")]
    [InlineData("200000")]
    public void Bad_prices_are_rejected(string price) =>
        Assert.Contains(Errors(("price", price)), e => e.Contains("price"));

    [Theory]
    [InlineData("12.50", "12.50")]
    [InlineData("£12.50", "12.50")]
    [InlineData("£ 1,234.50", "1234.50")]
    [InlineData("7", "7")]
    public void Prices_are_parsed(string raw, string expected) =>
        Assert.Equal(decimal.Parse(expected), Validator.Validate(Row(("price", raw))).Offer!.BuyPriceExVat);

    [Theory]
    [InlineData("")]
    [InlineData("maybe")]
    public void Vat_flag_is_required_and_strict(string flag) =>
        Assert.Contains(Errors(("vat_included", flag)), e => e.Contains("vat_included"));

    [Theory]
    [InlineData("true")] [InlineData("TRUE")] [InlineData("yes")] [InlineData("1")] [InlineData("Y")]
    public void Vat_flag_true_forms(string flag) => Assert.NotNull(Validator.Validate(Row(("vat_included", flag))).Offer);

    // ---- VAT derivation: £12.00 ex VAT -> £14.40 inc;  £12.00 inc VAT -> £10.00 ex ----
    [Fact]
    public void Ex_vat_price_derives_inc_vat()
    {
        var o = Validator.Validate(Row(("price", "12.00"), ("vat_included", "false"))).Offer!;
        Assert.Equal(12.00m, o.BuyPriceExVat);
        Assert.Equal(14.40m, o.BuyPriceIncVat);
    }

    [Fact]
    public void Inc_vat_price_derives_ex_vat()
    {
        var o = Validator.Validate(Row(("price", "12.00"), ("vat_included", "true"))).Offer!;
        Assert.Equal(10.00m, o.BuyPriceExVat);
        Assert.Equal(12.00m, o.BuyPriceIncVat);
    }

    // ---- EAN ----
    [Fact]
    public void Invalid_ean_rejects_the_row() =>
        Assert.Contains(Errors(("ean", "5012345678901")), e => e.Contains("check digit"));

    [Fact]
    public void Ean_is_optional_when_brand_and_mpn_are_present()
    {
        var o = Validator.Validate(Row(("ean", ""))).Offer!;
        Assert.Null(o.Ean);
        Assert.True(o.Matchable);
    }

    [Fact]
    public void Row_with_no_identifiers_is_valid_but_unmatchable()
    {
        var o = Validator.Validate(Row(("ean", ""), ("mpn", ""), ("brand", ""))).Offer!;
        Assert.False(o.Matchable);
    }

    [Fact]
    public void Brand_without_mpn_is_not_matchable() =>
        Assert.False(Validator.Validate(Row(("ean", ""), ("mpn", ""))).Offer!.Matchable);

    [Fact]
    public void Upc_is_stored_in_canonical_gtin13_form_but_raw_value_is_kept()
    {
        var o = Validator.Validate(Row(("ean", "036000291452"))).Offer!;
        Assert.Equal("0036000291452", o.Ean);
        Assert.Equal("036000291452", o.RawEan);
    }

    // ---- stock ----
    [Theory]
    [InlineData("In Stock", StockStatus.InStock)]
    [InlineData("in_stock", StockStatus.InStock)]
    [InlineData("low stock", StockStatus.LowStock)]
    [InlineData("OOS", StockStatus.OutOfStock)]
    [InlineData("out-of-stock", StockStatus.OutOfStock)]
    [InlineData("", StockStatus.Unknown)]
    [InlineData("backordered 3 weeks", StockStatus.Unknown)]   // unknown text never rejects a row
    public void Stock_text_maps_to_a_status(string text, StockStatus expected) =>
        Assert.Equal(expected, Validator.Validate(Row(("stock", text))).Offer!.Stock);

    // ---- every problem is reported, not just the first ----
    [Fact]
    public void All_errors_on_a_row_are_reported_together()
    {
        var (offer, errors) = Validator.Validate(Row(("url", "http://x.example"), ("price", "abc"), ("retailer", ""), ("vat_included", "")));
        Assert.Null(offer);
        Assert.True(errors.Count >= 4, string.Join(" | ", errors));
    }

    [Fact]
    public void Over_long_text_is_rejected_not_truncated() =>
        Assert.Contains(Errors(("title", new string('x', 501))), e => e.Contains("title"));
}
