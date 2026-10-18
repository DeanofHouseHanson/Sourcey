using BuyList.Domain.Enums;

namespace BuyList.Application.Risk;

/// <param name="NoSalesData">True when monthly sales were missing; the row is then VeryHigh risk by rule, never a guess.</param>
public sealed record RiskAssessment(RiskLevel Level, decimal Multiplier, bool NoSalesData);
