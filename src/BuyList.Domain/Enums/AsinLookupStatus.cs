namespace BuyList.Domain.Enums;

/// <summary>Outcome of resolving a stub product's EAN to an Amazon ASIN via Keepa.</summary>
public enum AsinLookupStatus
{
    NotTried = 0,
    Resolved = 1,
    NotFound = 2,

    /// <summary>The EAN maps to several ASINs; never auto-picked, because buying the wrong listing costs real money.</summary>
    Ambiguous = 3
}
