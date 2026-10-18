namespace BuyList.Application.Enrichment;

public enum ChooseAsinOutcome { Chosen, ProductNotFound, NotACandidate }

/// <summary>Lets the owner settle an EAN that Keepa maps to several ASINs, using the stored options (no tokens spent).</summary>
public interface IAsinResolutionService
{
    /// <summary>Assigns the chosen candidate, saves its market data, and recalculates opportunities.</summary>
    Task<ChooseAsinOutcome> ChooseAsync(int productId, string asin, CancellationToken ct = default);

    /// <summary>"None of these": marks the EAN as not found on Amazon and discards the options.</summary>
    Task<bool> DismissAsync(int productId, CancellationToken ct = default);
}
