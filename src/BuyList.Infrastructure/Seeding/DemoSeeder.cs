using BuyList.Application.Enrichment;
using BuyList.Application.Import;
using BuyList.Application.Opportunities;
using BuyList.Application.Options;
using BuyList.Infrastructure.Enrichment;
using BuyList.Infrastructure.Keepa;
using BuyList.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BuyList.Infrastructure.Seeding;

public sealed record SeedResult(bool Seeded, string Message, ImportSummary? Import = null, EnrichmentSummary? Enrichment = null, RecalcSummary? Opportunities = null);

/// <summary>
/// Loads samples/offers.sample.csv through the real import path, enriches it from the FAKE Keepa catalogue (never the
/// live API, so seeding costs no tokens whatever Keepa:UseFake says), and calculates opportunities.
/// Only seeds an empty database. Don't seed a database you intend to point at live Keepa data.
/// </summary>
public class DemoSeeder
{
    private readonly BuyListDbContext _db;
    private readonly OfferFileValidator _validator;
    private readonly IOfferImportService _importer;
    private readonly IOpportunityService _opportunities;
    private readonly IOptions<KeepaOptions> _keepaOptions;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _logs;

    public DemoSeeder(BuyListDbContext db, OfferFileValidator validator, IOfferImportService importer, IOpportunityService opportunities,
        IOptions<KeepaOptions> keepaOptions, TimeProvider time, ILoggerFactory logs)
    {
        _db = db;
        _validator = validator;
        _importer = importer;
        _opportunities = opportunities;
        _keepaOptions = keepaOptions;
        _time = time;
        _logs = logs;
    }

    public async Task<SeedResult> SeedAsync(string? csvPath = null, CancellationToken ct = default)
    {
        if (await _db.SourceOffers.AnyAsync(ct))
            return new SeedResult(false, "The database already has offers; nothing seeded.");

        csvPath ??= Path.Combine(AppContext.BaseDirectory, "samples", "offers.sample.csv");
        if (!File.Exists(csvPath))
            return new SeedResult(false, $"Sample file not found at {csvPath}.");

        await using var file = File.OpenRead(csvPath);
        var preview = _validator.Validate(file);
        if (!preview.CanImport)
            return new SeedResult(false, preview.FatalError ?? "The sample file has no valid rows.");

        var import = await _importer.ImportAsync(preview.Valid, preview.Rejected.Count, ct);

        var fakeEnrichment = new EnrichmentService(_db, new FakeKeepaClient(_time), _keepaOptions, _time, _logs.CreateLogger<EnrichmentService>());
        var enrich = await fakeEnrichment.EnrichAsync(ct);
        var opps = await _opportunities.RecalculateAsync(ct);

        return new SeedResult(true, $"Seeded {import.Added} offers and {opps.Created} opportunities from the demo data.", import, enrich, opps);
    }
}
