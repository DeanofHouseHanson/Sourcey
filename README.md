# Buy List

A small, self-hosted Amazon UK arbitrage sourcing tool (ASP.NET Core 8, Razor Pages, EF Core, SQLite). Import supplier prices from CSV, match them to Amazon listings by EAN, enrich with Keepa data, and see a ranked, risk-adjusted list of what to buy. Single user.

> **Status:** the original build (scaffold, auth, profit/risk/ranking, CSV import + matching, Keepa client + enrichment, Buy List page) is complete. A new plan for **automated, Amazon-first deal finding** is in `docs/PLAN.md` (draft v2.1); the old plan is archived in `docs/archive/`. See `CLAUDE.md` for contributor/agent instructions.

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

### Keepa API key and token safety

The key is **never** stored in source or `appsettings.json`, and is never logged (the client strips HttpClient logging and never puts URLs in messages).

```bash
# development
cd src/BuyList.Web && dotnet user-secrets set "Keepa:ApiKey" "<your key>"
# production / EC2: environment (an `export` only lasts for that shell; for the service use the systemd EnvironmentFile, mode 600)
export Keepa__ApiKey=<your key>
export Keepa__UseFake=false        # default is true = offline demo data, no key needed
```
With `Keepa:UseFake=false` the app refuses to start without a key. Nothing calls Keepa at startup. Amazon data comes from Keepa only; Amazon pages are never scraped.

**How tokens are protected** (all configurable under `Keepa`): the balance is read from the free `/token` endpoint before any spend; `MinTokensReserve` (20) is never touched;
each refresh run spends at most `MaxTokensPerRun` (100); each product costs about `TokensPerProduct` (3: 1 + 2 for Buy Box data), raised automatically if Keepa charges more;
data younger than `RefreshAgeHours` (24) is never re-fetched; EANs Keepa can't find are retried only after `UnresolvedRetryDays` (7); anything over budget waits for the next run.

**First live check (costs ~3 tokens per ASIN):**
```bash
dotnet run --project src/BuyList.Web -- check-keepa              # balance only (free)
dotnet run --project src/BuyList.Web -- check-keepa B0XXXXXXXX   # up to 3 ASINs, prints what was parsed
dotnet run --project src/BuyList.Web -- refresh                  # match -> look up/refresh within budget -> recalculate
dotnet run --project src/BuyList.Web -- seed                     # demo data into an EMPTY db (offline, never uses tokens)
```
In Development (with `UseFake=true`) an empty database is seeded with demo data on startup (`Seed:OnStartup`).

## The Buy List (home page)

Dark-themed, sortable and filterable table of the best things to buy, ranked by risk-adjusted monthly profit (toggle **Raw** for unadjusted). By default it hides loss-making items, items Amazon sells itself,
prices older than 24h, ignored and bought items; a line under the filters says how many each is hiding. Missing data reads "no data". Per row: set status (New / Checked / Bought / Ignored), open the source page, Amazon UK, Keepa or SellerAmp.
**Export CSV** downloads exactly what the filters show. **Refresh now** matches new offers, looks up Amazon data from Keepa (within the token cap) and recalculates. If the page is empty it tells you why and what to do next.

## Importing supplier prices (CSV)

Open **Import**, upload a `.csv`, review the preview, then confirm. Nothing is saved until you confirm, and every rejected row is listed with the reason(s).
Samples: `samples/offers.sample.csv` (clean) and `samples/offers.invalid.csv` (deliberate errors), also downloadable from the page.

| Column | Required | Notes |
|---|---|---|
| `retailer` | yes | Shown on the Buy List |
| `url` | yes | Absolute `https://` link to the seller's product page. Rows without one are rejected |
| `price` | yes | e.g. `12.50`, `£12.50`, `1,234.50` (a decimal comma like `12,50` is rejected) |
| `vat_included` | yes | `true`/`false` (also yes/no/1/0): whether `price` includes VAT |
| `ean` | no | EAN-8/UPC-12/EAN-13/GTIN-14 with a valid check digit. Keep the column formatted as **text** in Excel (it eats leading zeros and turns long numbers into `5.01E+12`) |
| `mpn`, `brand`, `title` | no | Brand + MPN is the fallback match when there's no EAN. Quote values containing commas |
| `stock` | no | in stock / low stock / out of stock (anything else = unknown) |

Re-importing a URL updates its price and "last checked" time. Offers match to products by EAN, else exact brand + MPN, never by title.

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
