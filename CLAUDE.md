# CLAUDE.md – Buy List

Self-hosted ASP.NET Core 8 (Razor Pages, EF Core, SQLite) app for Amazon UK arbitrage sourcing. One user. Read
`docs/PLAN.md` before starting any task: it holds the phase plan, contracts for unbuilt phases, design decisions and
current status. **Update the status table in `docs/PLAN.md` when you finish a phase or make a decision.**

## Commands

```bash
dotnet build
dotnet test                                    # must be green before any commit
dotnet run --project src/BuyList.Web           # http://localhost:5235 (Development: admin / changeme)
export PATH="$PATH:$HOME/.dotnet/tools"        # for dotnet-ef (dotnet tool install --global dotnet-ef --version 8.*)
dotnet ef migrations add <Name> -p src/BuyList.Infrastructure -s src/BuyList.Web -o Persistence/Migrations
dotnet run --project src/BuyList.Web -- hash-password
dotnet run --project src/BuyList.Web -- seed | refresh | check-keepa [ASIN...]   # see docs/PLAN.md "Phase 4"
```

## Layout and dependency rule

`Domain` (entities/enums, no deps) <- `Application` (options, interfaces, pure logic) <- `Infrastructure` (EF Core, Keepa, jobs) <- `Web` (Razor Pages, auth, composition root). `tests/BuyList.Tests` references all.
Business logic goes in Application as plain classes with constructor-injected `IOptions<T>`/interfaces, so it is unit-testable without a DB or web host. Razor pages stay thin.

## Hard rules

- **Never commit secrets.** No Keepa key, password hash or `.db` file in source, appsettings.json, logs, tests or docs. Keepa key comes from User Secrets (`Keepa:ApiKey`) or env `Keepa__ApiKey`; never log it or full request URLs (Keepa puts the key in the query string).
- **No scraping of Amazon.** Amazon data comes from Keepa only.
- `SourceOffer.SourceUrl` is mandatory (absolute https). Nothing is silently dropped on import; rejected rows are shown with reasons.
- Match on EAN/GTIN, else brand + MPN. **Never on title alone.**
- Missing monthly-sales data => VeryHigh risk + `NoSalesData` flag; never guess a value.
- Money is `decimal`; times are UTC (`DateTime.UtcNow`); UI shows GBP (`en-GB`).
- **Keepa tokens are real money/limits.** Never loop calls, never raise `MaxTokensPerRun`/batch sizes without asking, and use `check-keepa` (max 3 ASINs) for live checks. `seed` must stay offline.
- Default implementations (`FakeKeepaClient`, `ApproximateFeeEstimator`) sit behind interfaces so real ones drop in via DI/config.

## Conventions and gotchas

- Match the surrounding code's style and comment density; comments explain *why*, not what.
- **Decimals are stored in SQLite as integers (1/10000ths)** via value converters in `BuyListDbContext`, so SQL `ORDER BY`/`SUM` work. Don't change column types to TEXT/REAL. Enums are stored as strings, except `[Flags]` columns (`OpportunityFlags`) which are ints.
- DateTimes come back from SQLite as `Utc` kind through a converter; keep it that way.
- Schema changes need an EF migration; the app applies migrations on startup.
- Every new behaviour needs xUnit tests with worked, hand-checked numbers (show the arithmetic in a comment, as in `ProfitCalculatorTests`).
- Work one phase at a time, on a branch `claude/phase-N-<slug>`, tests green, commit with a clear message, then update `docs/PLAN.md`. Ask the owner before major decisions not covered by the plan.
- Web tests: set config with `builder.UseSetting(...)`, not `ConfigureAppConfiguration` (Program.cs reads config before the host is built).
- Razor Pages are behind a global `AuthorizeFolder("/")`; new public endpoints must be a deliberate exception.
