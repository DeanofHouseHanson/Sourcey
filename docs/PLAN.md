# Buy List – delivery plan

Owner brief, condensed: import supplier prices (CSV) -> match to Amazon UK ASINs (EAN first) -> enrich with Keepa -> compute
real net profit, ROI, monthly profit, risk and rank -> show a sortable, filterable **Buy List** to decide purchases.
Personal use, single login, self-hosted (small VPS / EC2 / Azure App Service).

## Status

| Phase | Scope | Status | Branch |
|---|---|---|---|
| 1 | Scaffold, data model, migrations, config, auth, README | **Done** | `claude/phase-1-scaffold` |
| 2 | Profit calculator, fee estimator, risk banding, scoring, ranking + tests | **Done** | `claude/phase-2-profit-risk-ranking` |
| 3 | CSV import with validation, EAN/brand+MPN matching | **Done** | `claude/phase-3-import-matching` |
| 4 | `IKeepaClient` + `FakeKeepaClient`, enrichment, opportunity calculation, seed data | Next | |
| 5 | Buy List page: filters, sorting, links, risk badges, status actions, CSV export | | |
| 6 | Budget mode, blocklist page + eligibility, refresh job | | |
| 7 | Real `KeepaClient` (token-aware, cached) behind `Keepa:UseFake` | | |

Phases are cumulative: each branch is based on the previous one. Test count after phase 3: 198, all passing.

## Architecture

```
Domain          Product, SourceOffer, AmazonSnapshot, Opportunity, BlocklistEntry + enums
Application     Options/*  Profit/*  Risk/*  Ranking/*  (+ later: Import/*, Matching/*, Budget/*, Keepa abstractions)
Infrastructure  Persistence/BuyListDbContext + Migrations (+ later: Keepa clients, refresh job, seeding)
Web             Program.cs (composition root), Security/*, Pages/*
```

Pipeline (what each phase adds):
`CSV -> SourceOffer (unmatched) -> Matcher -> Product -> Keepa -> AmazonSnapshot -> ProfitCalculator + OpportunityScorer -> Opportunity -> Buy List`

## Done: Phases 1-2 (reference)

### Phase 1
Entities/migration `InitialCreate`; cookie login (single user, Identity `PasswordHasher`, `hash-password` command, dev fallback
`admin`/`changeme` in Development only, app refuses to start in other environments without `Auth:PasswordHash`);
options classes in `Application/Options`; Bootstrap served locally.
Key schema facts: `SourceUrl` NOT NULL + unique; `Product.Asin` unique (multiple NULLs allowed), indexes on Ean/Mpn/Brand;
`Opportunity.SourceOfferId` unique (one opportunity per offer); `BlocklistEntry (Type, Value)` unique.
Additions beyond the original brief: `SourceOffer.RawBrand`, `Product.ManualFlags`, `Opportunity.CalculatedAt`.

### Phase 2 – formulas (all in `src/BuyList.Application`, covered by `tests/BuyList.Tests/Application`)

**`Profit/ProfitCalculator`** – per unit; sell price = 90-day average Buy Box, VAT-inclusive.

| | VAT registered | Not VAT registered |
|---|---|---|
| Revenue | sell / 1.2 (output VAT remitted) | sell |
| Cost of goods | buy ex VAT (input VAT reclaimed) | buy inc VAT |
| Amazon fees, inbound | ex VAT | x 1.2 (VAT is a real cost) |
| Returns allowance | % of revenue | % of revenue |

`NetProfit = Revenue - CostOfGoods - Referral - FBA - VatOnFees - Inbound - Returns`;
`ROI = NetProfit / (CostOfGoods + Inbound)`; `Margin = NetProfit / Revenue`;
`MaxBuyPrice` (returned ex-VAT and inc-VAT, floored to the penny) = `A/(1+T) - Inbound` where `A = Revenue - Fees - Returns`,
`T` = target ROI. Money/percent outputs rounded to 2dp (away from zero) at the end only. Worked examples are in the tests.
`Opportunity.MaxBuyPrice` should store the **inc-VAT** value (the price at the till; compare with `BuyPriceIncVat`).

**`Profit/IFeeEstimator`** -> `ApproximateFeeEstimator`: referral = `Profit:ReferralFeePercent` of the VAT-inclusive price;
FBA = first `Fba:Tiers` entry whose `MaxWeightGrams` covers the weight (unknown weight -> `Fba:DefaultWeightGrams`; heavier than all ->
top tier). **The shipped FBA fees are placeholders** – verify against Seller Central. Future SP-API estimator replaces it in
`Application/DependencyInjection.cs`.

**`Risk/RiskClassifier`** – bands from `Risk:Bands` (falls back to `RiskOptions.DefaultBands` if none): 0-9 VeryHigh x0.25, 10-29 High x0.5,
30-99 Medium x0.8, 100-299 Low x1.0, 300+ VeryLow x1.0. `null`/negative sales => VeryHigh + `NoSalesData` (0 is real data, not flagged).
`RiskClassifier.IsWithin(level, maxRisk)` implements the "max risk" filter (`RiskLevel` enum is ordered VeryHigh=0 ... VeryLow=4).

**`Ranking/OpportunityScorer`**:
`MonthlyUnits = MonthlySales / (FbaSellerCount + 1)`; `EffectiveUnits = min(MonthlyUnits, floor(budget / BuyCashPerUnit))`;
`EstMonthlyProfit = NetProfit * EffectiveUnits`; `RiskAdjusted = Est * multiplier`;
`SuggestedQty = clamp(floor(MonthlyUnits * TargetDaysOfCover/30), 1, min(MaxSuggestedQty, affordable))` (0 if confirmed zero sales);
`DaysToSell = SuggestedQty / MonthlyUnits * 30` (null without sales data). No sales data => Est = 0, qty = 1 (test buy, if affordable).
`BuyCashPerUnit` = `BuyPriceIncVat` (cash at checkout). `Score(input, budgetOverride)` supports Budget mode.

**`Ranking/OpportunityRanker.Order(items, RankingMode)`** – RiskAdjusted (default) or Raw desc; ties: ROI desc, NetProfit desc, Id asc.

**Not yet wired:** nothing persists `Opportunity` rows yet; weight/size are not on `Product` (calculator takes optional `WeightGrams`;
add package weight to the Keepa domain model and pass it through in phase 4).

## Done: Phase 3 – CSV import and matching (reference)

Pure logic in `Application/Import`; DB-backed services in `Infrastructure/Import`; UI at `Pages/Import` (nav link "Import").

**Flow:** upload (`.csv`, max 5 MB, max 10,000 rows) -> **Preview** (parse + validate, *no DB writes*; bytes cached in `IMemoryCache` for 30 min under
a GUID token) -> **Confirm** (the same bytes are re-validated server-side, never trusting the client) -> upsert offers -> match -> summary +
rejected-rows table + "download rejected rows as CSV".

| Piece | Responsibility |
|---|---|
| `OfferCsvParser` (CsvHelper) | Header case-insensitive/any order/BOM-safe; required columns `retailer,url,price,vat_included`; optional `ean,mpn,brand,title,stock`; unknown columns ignored; duplicate headers, empty file, >10k rows are fatal. Records the physical line number (header = line 1). A row with **more values than headers** is rejected (unquoted comma shifts columns). |
| `OfferRowValidator` | Collects **every** error per row. URL: required, absolute, scheme exactly `https`, has host, no `user:pass@`, <=2048 chars; stored normalised (lower-case scheme/host, fragment removed). Price: `£` and thousands commas allowed (`1,234.50`); `12,50` is **rejected**, never read as 1250; >0 and <=£100k. `vat_included` required: true/false/yes/no/y/n/1/0. EAN (optional) must be a valid GTIN or the row is rejected. Over-long text is rejected, never truncated. Unknown `stock` text => `Unknown` (never rejects). Derives ex/inc VAT using `Profit:VatRate`. |
| `Gtin` | Valid lengths 8/12/13/14 + GS1 check digit. **Canonical form = GTIN-13** (UPC-A is padded with a leading 0) so UPC and EAN spellings of one product match; EAN-8 and GTIN-14 keep their length. Specific errors for spreadsheet damage (scientific notation, eaten leading zero). |
| `OfferFileValidator` | Parse + validate whole file, no side effects. Repeated URLs in one file: first kept, later ones rejected with "duplicate of row N". `RejectedToCsv` escapes formula injection (`CsvSafe`). |
| `OfferImportService` | Upsert by normalised `SourceUrl`: **Added** / **Updated** (something changed) / **Refreshed** (unchanged, but `CheckedAt` still bumped - re-importing a price re-confirms it). Changing EAN/MPN/brand clears `ProductId` so it is re-matched. Then runs the matcher. |
| `ProductMatcher` (`IProductMatcher.MatchPendingAsync`) | See below. Idempotent; reused by the refresh job in phase 6. |

**Matching rules** (title is never used): 1) canonical EAN == `Product.Ean`; 2) else exact brand (case-insensitive) + MPN (trimmed, case-insensitive).
Brand+MPN is **refused** if >1 product matches (ambiguous) or if the product has a *different* EAN (a variant). Brand+MPN matching a product with no EAN
**teaches** it the offer's EAN. A valid EAN with no product creates a **stub `Product`** (EAN, brand, MPN, title; no ASIN) for the Keepa lookup in phase 4.
Brand+MPN alone never creates a product (Keepa cannot look those up) - such offers stay unmatched ("no product found"). No EAN and no brand+MPN =>
"unmatchable" (imported, flagged, never matched).

Other additions: `samples/offers.sample.csv` (38 clean rows, fictional brands, valid GTINs - phase 4's fake Keepa data should key on these EANs),
`samples/offers.invalid.csv` (7 deliberate errors + 1 unmatchable row), both downloadable from the Import page and copied to the build output;
`Application/Formatting/Gbp` (ICU-independent `£1,234.56` formatter for phase 5); `TimeProvider.System` registered for testable clocks.

Tests (`tests/BuyList.Tests/Import`, `Web/ImportPageTests`): GTIN matrix, full validation matrix (missing/relative/http/ftp/javascript/no-host/credentials URLs,
price/VAT/EAN/stock forms, multi-error rows), parser edge cases, file-level accounting (valid + rejected == input rows), matcher precedence/ambiguity/
variant/learning/idempotency, import upsert + re-match, and end-to-end page tests through the real host (login, preview writes nothing, confirm, rejected
download, expired token, non-csv refused, links open with `rel="noopener noreferrer"`).

## Phase 4 – Keepa abstraction, fake client, enrichment, opportunities, seed data

- **Domain model** (`Application/Keepa`): `KeepaProduct { Asin, Title, Brand, ImageUrl, Ean, Mpn, Avg90BuyBox (decimal?), SalesRank (int?),
  SalesRankSummary, FbaSellerCount, AmazonOnListing, MonthlySold (int?), WeightGrams (int?), FetchedAt }`.
  `IKeepaClient`: `Task<IReadOnlyList<KeepaProduct>> GetByAsinsAsync(IEnumerable<string>, ct)` and
  `GetByCodesAsync(IEnumerable<string> eansOrUpcs, ct)`; surface token state via `KeepaTokenStatus` (remaining, refill rate) if useful.
  Missing `MonthlySold` stays **null**.
- `FakeKeepaClient` (Infrastructure): deterministic seeded catalogue (~40 products) with a spread of risk levels, some Amazon-on-listing,
  some with no sales data, some with no avg price. Selected when `Keepa:UseFake` is true (default); needs no key.
- **Snapshots**: write `AmazonSnapshot` rows from `KeepaProduct`; a product's "current" snapshot = latest. Respect `Keepa:RefreshAgeHours`
  (don't refetch fresh snapshots).
- **`OpportunityService.RecalculateAsync`**: for each matched, in-stock-or-unknown `SourceOffer` with a snapshot that has `Avg90BuyBox`:
  `ProfitCalculator` -> `OpportunityScorer` -> upsert `Opportunity` (key = `SourceOfferId`; **preserve `Status`** across recalcs).
  Set `Flags`: `NoSalesData`, plus `Gated/Hazmat/IpComplaintProne/Blocklisted` from `IEligibilityChecker` (phase 6 supplies the real one;
  a no-op stub now). Delete opportunities whose offer/product no longer qualifies.
- **Seed**: `dotnet run --project src/BuyList.Web -- seed` (and automatic in Development when the DB is empty) loads the sample CSV
  through the real import path + fake Keepa + recalculation, so the Buy List is populated on first run. Idempotent.
- **Tests**: enrichment skips fresh snapshots; opportunity upsert keeps Status; missing sales => flagged VeryHigh; missing price => no opportunity;
  fake client determinism.

## Phase 5 – Buy List page (home)

`Pages/Index` becomes the Buy List. Query-string driven (bookmarkable), server-rendered, Bootstrap, minimal JS.

- **Columns**: image, title/brand, buy price + retailer, source link (`target="_blank" rel="noopener noreferrer"`), Amazon avg price,
  net profit, ROI, est. monthly sales, est. monthly profit, risk badge (red->green: VeryHigh `danger`, High `warning`+orange, Medium `warning`,
  Low `success`-light, VeryLow `success`), days to sell, competition (FBA count + Amazon-on-listing), flags (incl. "no sales data"),
  max buy price, suggested qty, status, price last-checked, quick links: Amazon UK (`https://www.amazon.co.uk/dp/{asin}`), Keepa chart
  (`https://keepa.com/#!product/2-{asin}`), SellerAmp (`https://sas.selleramp.com/sas/lookup?SasLookup[search_term]={asin}`), source page.
- **Only rows with a SourceUrl** (always true by schema; keep the guard in the query).
- **Default exclusions**: NetProfit < 0; Amazon on listing; source price older than `Ranking:StaleAfterHours`; `Ignored` status; blocklisted.
  "Include stale" shows stale rows greyed out. "Bought" is a separate view.
- **Filters**: min ROI, min net profit, min monthly sales, max risk (`RiskClassifier.IsWithin`), hide Amazon-on-listing, hide flagged,
  max unit cost, retailer, include stale, status. **Ranking toggle** Risk-adjusted (default) / Raw. All columns sortable (whitelist the
  sort keys – never concatenate user input into queries). Paging (default 50).
- **Row actions** (POST, antiforgery): set status New/Checked/Bought/Ignored.
- **Export**: current filtered+sorted list as CSV (`text/csv`, UTF-8 with BOM for Excel, formula-injection safe: prefix cells starting with `= + - @` with `'`).
- Display: GBP via `en-GB` culture (`£1,234.56`); `DateTime` in UTC with relative "checked 3h ago".
- **Tests**: a query-object/service layer (not the page model) holds filter+sort logic and is tested against in-memory SQLite: each filter,
  default exclusions, stale handling, sort keys, ranking mode, paging, CSV export content + injection escaping.

## Phase 6 – Budget mode, blocklist/eligibility, refresh job

- **Budget mode** (`Application/Budget/BudgetPlanner`, page `Pages/Budget`): input £ budget -> greedy selection ranked by
  *risk-adjusted monthly profit per £ spent* (`NetProfit * multiplier / BuyCashPerUnit`), buying up to each item's suggested qty while
  money remains (final item may be reduced to fit); use `OpportunityScorer.Score(..., budgetOverride)` semantics. Output: item, qty, spend,
  expected risk-adjusted monthly profit, running total, leftover cash. Respects the same default exclusions. Document that greedy is a
  heuristic (not an exact knapsack) unless the owner wants better.
- **Blocklist page** (`Pages/Blocklist`): add/remove Brand or ASIN entries with reason; brand match case-insensitive; blocked rows hidden
  (or shown flagged via a filter toggle).
- **`IEligibilityChecker`** (Application): `Check(Product) -> OpportunityFlags`. Default `ManualEligibilityChecker` = blocklist (-> `Blocklisted`)
  + `Product.ManualFlags` (Gated/Hazmat/IpComplaintProne). Leave a clear seam for SP-API listing restrictions. Add UI to toggle the manual
  flags per product.
- **Refresh job**: hosted `BackgroundService` (simple, no extra DB; Hangfire only if the owner asks). Steps: match new offers -> select
  ASINs/codes needing a refresh (age > `RefreshAgeHours`, prioritising unmatched/never-fetched) -> fetch from `IKeepaClient` in batches
  honouring token limits -> write snapshots -> recalculate opportunities. Single-flight lock (no overlapping runs), configurable interval
  (`Refresh:IntervalMinutes`, 0 = manual only), logs counts but **never** keys/URLs with keys. "Refresh now" button triggers the same
  routine and shows last-run summary.
- **Tests**: planner (never exceeds budget, ordering by value density, partial last item, nothing affordable, excluded rows skipped),
  eligibility, refresh orchestration with fake client (batching, skips fresh, single-flight).

## Phase 7 – Real KeepaClient

`Infrastructure/Keepa/KeepaClient` using `IHttpClientFactory` (typed client). `GET {BaseUrl}product?key=..&domain=2&asin=A,B,..&stats=90&history=0..`
(or `code=` for EAN lookups); batch up to `Keepa:BatchSize` (Keepa allows up to 100 ASINs/request). Read `tokensLeft`,
`refillIn`, `refillRate`, `tokensConsumed` from each response; stop/back off when `tokensLeft < MinTokensReserve` (delay until refill,
or return partial results and mark remainder as deferred). Handle 429/5xx with bounded retry + jitter. Parse Keepa's scaled prices
(integer pence-style values; -1 = no data -> null) into `decimal` GBP; read 90-day average Buy Box from `stats.avg90`, `monthlySold` for
sales, FBA seller count from `stats.offerCountFBA`/`totalOfferCount`, Amazon-on-listing from `stats.buyBoxIsAmazon`/offers (verify field
names against current Keepa docs when implementing – do not guess). **Redact the key** from every log/exception (it is in the query string):
use a logging-safe request description, and don't include `RequestUri` in exceptions. Cache = `AmazonSnapshot` rows with
`Keepa:RefreshAgeHours`. Selection via `Keepa:UseFake`; if `UseFake=false` and no key, fail fast at startup with a clear message.
Tests use a fake `HttpMessageHandler` with recorded JSON fixtures (no network, no key). Not verified against the live API until the owner supplies a key.

## Final polish (after phase 7)

README deployment section (below), `systemd` unit, Caddy/nginx reverse-proxy example, backup of the SQLite file, security pass
(cookie flags, antiforgery on all POSTs, HSTS), and `/health` endpoint.

## Deploying (EC2 / small VPS) – outline for the README

1. Ubuntu 22.04/24.04; `sudo apt-get update && sudo apt-get install -y dotnet-sdk-8.0` (or runtime only for publish output).
2. Clone via a **read-only deploy key** (`ssh-keygen -t ed25519 -f ~/.ssh/buylist_deploy -N ""`, add the `.pub` under repo Settings -> Deploy keys, `Host github.com` block in `~/.ssh/config` with `IdentityFile` + `IdentitiesOnly yes`).
3. `dotnet publish src/BuyList.Web -c Release -o /opt/buylist`.
4. Environment (systemd `EnvironmentFile`, mode 600, outside the repo): `ASPNETCORE_ENVIRONMENT=Production`, `Auth__PasswordHash=...`,
   `Keepa__ApiKey=...`, `Keepa__UseFake=false`, `ConnectionStrings__BuyList=Data Source=/var/lib/buylist/buylist.db`,
   `DataProtection__KeysPath=/var/lib/buylist/keys`, `ASPNETCORE_URLS=http://127.0.0.1:5000`.
5. Reverse proxy with TLS (Caddy: `buylist.example.com { reverse_proxy 127.0.0.1:5000 }`); security group exposes 80/443 only.
6. Back up `/var/lib/buylist` (cron + `sqlite3 .backup`).

## Decisions log

| # | Decision | Why |
|---|---|---|
| D1 | Decimals stored as integers (1/10000ths) | SQLite TEXT decimals can't be sorted/summed in SQL |
| D2 | Enums as strings, `[Flags]` as int | Readable DB; flags need bit ops |
| D3 | `Product.ManualFlags`, `SourceOffer.RawBrand`, `Opportunity.CalculatedAt` added | Needed for manual toggles, brand matching, staleness of calcs |
| D4 | Dev-only fallback login `admin/changeme`; non-Development refuses to start without a hash | Usability without weakening production |
| D5 | ROI denominator = cost of goods + inbound cost | Honest cash invested; MaxBuy formula derives from it |
| D6 | Unregistered sellers: full sell price is revenue, VAT on buy/fees/inbound is a cost | Matches how non-registered UK sellers actually net out |
| D7 | `MaxBuyPrice` stored inc-VAT; calculator returns both | Comparable with the price at the till |
| D8 | No sales data => EstMonthlyProfit 0, qty 1, DaysToSell null | "Do not guess"; keeps the row visible but ranked last |
| D9 | Confirmed 0 sales => qty 0 | Real data says don't buy |
| D10 | Hosted BackgroundService for refresh, not Hangfire | One user; avoids extra storage/dashboard auth surface |
| D11 | FBA fee table values are placeholders | Real rate card must be verified by the owner |
| D12 | Invalid EAN rejects the whole row (visible in the rejected list) | A wrong code could attach an offer to the wrong product; safer to fix the file |
| D13 | Canonical GTIN-13 (UPC padded); EAN-8/GTIN-14 unchanged | One key for the same item across suppliers/Keepa without conflating pack levels |
| D14 | Preview cached server-side by token, re-validated on confirm | Avoids huge hidden fields and client tampering |
| D15 | Re-importing an unchanged price still bumps `CheckedAt` | The import is the owner re-confirming the price; drives staleness |
| D16 | Brand+MPN never creates products; ambiguous/variant matches are refused | Prevents wrong-product matches; Keepa can't look up by MPN |
| D17 | Rows with extra values vs header are rejected | Catches unquoted-comma column shifts that would corrupt price/title silently |

## Open questions for the owner

- Confirm FBA fee tiers / referral % per category you actually sell in.
- Are supplier CSV prices usually ex-VAT (trade) or inc-VAT (retail)? (`vat_included` handles both, but defaults in the sample file follow your answer.)
- Preferred default branch name and whether PRs should be used per phase.
