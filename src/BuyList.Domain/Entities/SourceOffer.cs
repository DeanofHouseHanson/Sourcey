using BuyList.Domain.Enums;

namespace BuyList.Domain.Entities;

public class SourceOffer
{
    public int Id { get; set; }

    /// <summary>Null until the offer has been matched to a product.</summary>
    public int? ProductId { get; set; }
    public Product? Product { get; set; }

    public string RetailerName { get; set; } = string.Empty;

    /// <summary>Mandatory absolute https:// link to the seller page.</summary>
    public string SourceUrl { get; set; } = string.Empty;

    public decimal BuyPriceExVat { get; set; }
    public decimal BuyPriceIncVat { get; set; }
    public StockStatus StockStatus { get; set; }

    /// <summary>UTC time the price was last checked.</summary>
    public DateTime CheckedAt { get; set; }

    public string? RawEan { get; set; }
    public string? RawMpn { get; set; }
    public string? RawBrand { get; set; }
    public string? RawTitle { get; set; }
}
