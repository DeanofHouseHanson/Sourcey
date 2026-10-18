# Buy List

A small, self-hosted Amazon UK arbitrage sourcing tool (ASP.NET Core 8, Razor Pages, EF Core, SQLite). Import supplier prices from CSV, match them to Amazon listings by EAN, enrich with Keepa data, and see a ranked, risk-adjusted list of what to buy. Single user.

> **Status:** Phase 1 of 7 (scaffold, data model, config, auth). The profit engine, import, Keepa and Buy List UI arrive in later phases.

## Layout

```
src/BuyList.Domain          entities + enums (no dependencies)
src/BuyList.Application     options classes, interfaces, calculation logic
src/BuyList.Infrastructure  EF Core DbContext, migrations, Keepa client (later)
src/BuyList.Web             Razor Pages UI, auth, composition root
tests/BuyList.Tests         xUnit tests
```

## Run it

Requires the .NET 8 SDK.

```bash
dotnet run --project src/BuyList.Web
```

The SQLite database (`buylist.db`) is created and migrated automatically on startup.

**Development login:** if no password hash is configured, `admin` / `changeme` works in the Development environment only (a warning is logged). In any other environment the app refuses to start without `Auth:PasswordHash`.

### Set your own password

```bash
dotnet run --project src/BuyList.Web -- hash-password     # prompts, prints a hash
cd src/BuyList.Web
dotnet user-secrets set "Auth:PasswordHash" "<hash>"       # development
# production: set environment variable Auth__PasswordHash=<hash>
```
The username defaults to `admin` (`Auth:Username`). There is no registration page.

## Configuration

All in `src/BuyList.Web/appsettings.json` (override with user secrets / environment variables, `__` as the section separator):

| Section | Purpose |
|---|---|
| `Profit` | VAT registered flag + rate, inbound cost per unit, returns allowance %, target ROI %, referral fee % |
| `Risk:Bands` | monthly-sales bands, risk level and multiplier |
| `Ranking` | budget used to cap units, hours before a source price is "stale" |
| `Keepa` | `UseFake` (default `true`), refresh age, token reserve, batch size |
| `ConnectionStrings:BuyList` | SQLite path |
| `DataProtection:KeysPath` | optional folder to persist cookie keys so logins survive restarts |

### Keepa API key

The key is **never** stored in source or `appsettings.json`, and is never logged.

```bash
# development
cd src/BuyList.Web && dotnet user-secrets set "Keepa:ApiKey" "<your key>"
# production
export Keepa__ApiKey=<your key>
```
Then set `Keepa:UseFake` to `false` (the real client arrives in Phase 7; until then the fake client is used). Amazon data comes from Keepa only; Amazon pages are never scraped.

## Data notes

- Prices are `decimal`; SQLite has no decimal type, so they are stored as integers in 1/10000ths (exact, and sortable/summable in SQL).
- All timestamps are UTC; currency is shown as GBP.
- `Product` is keyed on ASIN (unique) and indexed on EAN/MPN so the schema can be shared with other tools.

## Migrations

```bash
dotnet tool install --global dotnet-ef --version 8.*
dotnet ef migrations add <Name> -p src/BuyList.Infrastructure -s src/BuyList.Web -o Persistence/Migrations
```

## Tests

```bash
dotnet test
```

## Deploying

*(Filled in fully in the final phase.)* In outline: `dotnet publish src/BuyList.Web -c Release`, run behind a TLS-terminating reverse proxy (Caddy/nginx) on a VPS, or deploy to Azure App Service with `Auth__PasswordHash` and `Keepa__ApiKey` as app settings, and put the SQLite file on persistent storage (`ConnectionStrings__BuyList=Data Source=/home/data/buylist.db`).
