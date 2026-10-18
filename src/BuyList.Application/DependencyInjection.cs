using BuyList.Application.Profit;
using BuyList.Application.Ranking;
using BuyList.Application.Risk;
using Microsoft.Extensions.DependencyInjection;

namespace BuyList.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<IFeeEstimator, ApproximateFeeEstimator>(); // swap for an SP-API implementation later
        services.AddSingleton<ProfitCalculator>();
        services.AddSingleton<RiskClassifier>();
        services.AddSingleton<OpportunityScorer>();
        return services;
    }
}
