namespace BuyList.Domain.Entities;

/// <summary>
/// An Amazon-facing product. Keyed (uniquely, where present) on ASIN and indexed on EAN and MPN so the
/// same identifiers can be shared with other tools such as a deal/sales tracker.
/// </summary>
public class Product
{
    public int Id { get; set; }
    public string? Asin { get; set; }
    public string? Ean { get; set; }
    public string? Mpn { get; set; }
    public string? Brand { get; set; }
    public string? Title { get; set; }
    public string? ImageUrl { get; set; }

    /// <summary>Manual Gated / Hazmat / IP-complaint toggles (placeholder until SP-API restrictions are wired in).</summary>
    public Enums.OpportunityFlags ManualFlags { get; set; }

    public List<SourceOffer> SourceOffers { get; set; } = new();
    public List<AmazonSnapshot> Snapshots { get; set; } = new();
}
