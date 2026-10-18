namespace BuyList.Domain.Enums;

[Flags]
public enum OpportunityFlags
{
    None = 0,
    NoSalesData = 1,
    Gated = 2,
    Hazmat = 4,
    IpComplaintProne = 8,
    Blocklisted = 16
}
