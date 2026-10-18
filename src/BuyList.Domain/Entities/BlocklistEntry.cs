using BuyList.Domain.Enums;

namespace BuyList.Domain.Entities;

public class BlocklistEntry
{
    public int Id { get; set; }
    public BlocklistType Type { get; set; }
    public string Value { get; set; } = string.Empty;
    public string? Reason { get; set; }
}
