using BuyList.Infrastructure.Keepa;
using static BuyList.Tests.Keepa.KeepaFixtures;

namespace BuyList.Tests.Keepa;

public class KeepaResponseParserTests
{
    private static readonly DateTime Now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
    private static BuyList.Application.Keepa.KeepaProduct One(string product) =>
        KeepaResponseParser.ParseProducts(Doc(Response(100, 3, product)).RootElement, Now).Single();

    [Fact]
    public void Parses_a_full_product()
    {
        var p = One(Product());

        Assert.Equal("B00TESTAAA", p.Asin);
        Assert.Equal("Test Widget 750ml", p.Title);
        Assert.Equal("Norvik", p.Brand);
        Assert.Equal("NV-IB750", p.Mpn);
        Assert.Equal(21.99m, p.Avg90BuyBox);                // avg90[18] = 2199 pence
        Assert.Equal(1234, p.SalesRankCurrent);             // current[3]
        Assert.Equal(1500, p.SalesRankAvg90);               // avg90[3]
        Assert.Equal(55, p.SalesRankDrops30);
        Assert.Equal(4, p.FbaSellerCount);                  // current[34]
        Assert.False(p.FbaSellerCountIsApprox);
        Assert.False(p.AmazonOnListing);                    // current[0] = -1 (Amazon out of stock)
        Assert.Equal(150, p.MonthlySold);
        Assert.Equal(420, p.WeightGrams);
        Assert.Equal(2.89m, p.FbaPickAndPackFee);           // 289 pence, ex tax
        Assert.Equal(Now, p.FetchedAt);
    }

    [Fact]
    public void Ean_and_upc_lists_are_returned_in_canonical_gtin13_form() =>
        Assert.Equal(new[] { "5012345678900", "0036000291452" }, One(Product()).Eans);

    [Fact]
    public void Invalid_codes_from_keepa_are_dropped() =>
        Assert.Equal(new[] { "0036000291452" }, One(Product(eans: "[\"12345\",\"5012345678901\"]")).Eans);

    [Fact]
    public void Image_url_is_built_from_the_first_filename() =>
        Assert.Equal("https://m.media-amazon.com/images/I/71abc.jpg", One(Product()).ImageUrl);

    [Fact]
    public void Suspicious_image_filenames_are_ignored()
    {
        var json = Product().Replace("71abc.jpg,81def.jpg", "../../evil/x.js?a=b");
        Assert.Null(One(json).ImageUrl);
    }

    [Fact]
    public void Minus_one_buy_box_average_means_no_price() =>
        Assert.Null(One(Product(avg90: Arr((3, 1500)))).Avg90BuyBox);

    [Fact]
    public void Amazon_in_stock_flags_amazon_on_listing() =>
        Assert.True(One(Product(current: Arr((0, 1599), (3, 1234), (34, 4)))).AmazonOnListing);

    [Fact]
    public void Fba_count_falls_back_to_total_new_offers_and_is_marked_approximate()
    {
        var p = One(Product(current: Arr((11, 7), (34, -1))));
        Assert.Equal(7, p.FbaSellerCount);
        Assert.True(p.FbaSellerCountIsApprox);
    }

    [Fact]
    public void Zero_fba_sellers_is_real_data()
    {
        var p = One(Product(current: Arr((11, 3), (34, 0))));
        Assert.Equal(0, p.FbaSellerCount);
        Assert.False(p.FbaSellerCountIsApprox);
    }

    [Fact]
    public void Unknown_fba_count_stays_null_rather_than_being_guessed() =>
        Assert.Null(One(Product(current: Arr((3, 100)))).FbaSellerCount);

    [Fact]
    public void Missing_monthly_sold_is_null_not_zero()
    {
        var json = Product().Replace("\"monthlySold\":150,", "");
        Assert.Null(One(json).MonthlySold);
    }

    [Fact]
    public void Keepa_sentinel_minus_one_monthly_sold_is_null() =>
        Assert.Null(One(Product().Replace("\"monthlySold\":150", "\"monthlySold\":-1")).MonthlySold);

    [Fact]
    public void Item_weight_is_used_when_package_weight_is_missing()
    {
        var json = Product().Replace("\"packageWeight\":420", "\"packageWeight\":0,\"itemWeight\":250");
        Assert.Equal(250, One(json).WeightGrams);
    }

    [Fact]
    public void Product_without_stats_still_parses_with_nulls()
    {
        var p = One(Product(stats: false));
        Assert.Null(p.Avg90BuyBox);
        Assert.Null(p.SalesRankCurrent);
        Assert.Null(p.FbaSellerCount);
        Assert.False(p.AmazonOnListing);
        Assert.Equal("B00TESTAAA", p.Asin);
    }

    [Fact]
    public void Short_stats_arrays_do_not_throw() =>
        Assert.Null(One(Product(current: "[1,2]", avg90: "[1]")).Avg90BuyBox);

    [Fact]
    public void Response_without_products_is_empty() =>
        Assert.Empty(KeepaResponseParser.ParseProducts(Doc(TokenOnly(50)).RootElement, Now));

    [Fact]
    public void Entries_without_an_asin_are_skipped() =>
        Assert.Empty(KeepaResponseParser.ParseProducts(Doc(Response(1, 1, "{\"title\":\"x\"}")).RootElement, Now));

    [Fact]
    public void Token_status_and_consumption_are_read()
    {
        var root = Doc(Response(412, 9, Product())).RootElement;
        var s = KeepaResponseParser.ParseStatus(root);
        Assert.Equal(412, s.TokensLeft);
        Assert.Equal(20, s.RefillRatePerMinute);
        Assert.Equal(30000, s.RefillInMs);
        Assert.Equal(9, KeepaResponseParser.ParseTokensConsumed(root));
    }
}
