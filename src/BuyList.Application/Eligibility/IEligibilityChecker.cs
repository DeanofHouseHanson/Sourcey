using BuyList.Domain.Entities;
using BuyList.Domain.Enums;

namespace BuyList.Application.Eligibility;

/// <summary>
/// Reports risk flags (Gated, Hazmat, IpComplaintProne, Blocklisted) for a product. Today these come from manual
/// toggles and the blocklist; an SP-API listing-restrictions implementation can replace it later.
/// </summary>
public interface IEligibilityChecker
{
    OpportunityFlags Check(Product product);
}

/// <summary>Uses the manual toggles on the product. The blocklist is added to this in phase 6.</summary>
public class ManualEligibilityChecker : IEligibilityChecker
{
    private const OpportunityFlags Manual = OpportunityFlags.Gated | OpportunityFlags.Hazmat | OpportunityFlags.IpComplaintProne;
    public OpportunityFlags Check(Product product) => product.ManualFlags & Manual;
}
