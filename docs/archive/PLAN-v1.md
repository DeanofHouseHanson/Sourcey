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
| 4 | **Real** `KeepaClient` (token-safe) + `FakeKeepaClient`, enrichment, opportunity calculation, seed data, CLI | **Done** (live API not yet exercised) | `claude/phase-4-keepa-enrichment-opportunities` |
| 5 | Buy List page (home), dark theme, filters, sorting, links, risk badges, status actions, CSV export, "Refresh now" | **Done** | `claude/phase-5-buy-list` |
| 6 | Budget mode, blocklist page + eligibility, background refresh job | Next | |
| 7 | Live-API verification on EC2 (`check-keepa`), parsing fixes from real responses, hardening | After owner runs it | |

Phases are cumulative: each branch is based on the previous one. Test count after phase 5: 415, all passing.

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

## Done: Phase 4 – Keepa, enrichment, opportunities, seed (reference)

The owner chose to build the **real** Keepa client now (fake is for tests/offline demo only). `Keepa:UseFake` defaults to `true` in appsettings
(so a fresh clone runs with no key); set `Keepa__UseFake=false` + `Keepa__ApiKey` for live data. `UseFake=false` without a key **fails at startup** with a clear message.
Nothing calls Keepa at startup; tokens are only spent by `refresh`, `check-keepa <ASIN>` and (Phase 6) the refresh job.

**Contracts** (`Application/Keepa`): `IKeepaClient { GetTokenStatusAsync; GetByAsinsAsync(asins, maxTokens); GetByCodesAsync(codes, maxTokens) }` ->
`KeepaFetchResult { Products, NotFetched, TokensUsed, Tokens, Throttled }`. `KeepaProduct` = ASIN, title, brand, image URL, MPN, canonical EANs,
`Avg90BuyBox`, sales rank (current/90d avg), `SalesRankDrops30`, `FbaSellerCount` (+`FbaSellerCountIsApprox`), `AmazonOnListing`, `MonthlySold`,
`WeightGrams`, `FbaPickAndPackFee`. Missing data is `null` - never a guess.

**`KeepaClient` (Infrastructure/Keepa)** - request: `GET /product?key=..&domain=2&asin=A,B,..&stats=90&history=0&buybox=1` (or `&code=` for EANs).
No `offers` parameter (it is expensive). Cost model: 1 base token + 2 for `buybox=1` = **3 per product** (`Keepa:TokensPerProduct`).
*Never over-spend, in layers:* (1) balance read from the free `/token` endpoint before spending; (2) per call: `min(maxTokens, tokensLeft - MinTokensReserve)`
- Keepa lets balances go negative, so this is checked **before** each batch, re-planned after every response from the returned `tokensLeft`; (3) the planning
cost per item is `max(configured, highest cost Keepa has actually charged this session)`; (4) code lookups can return several products per code so they
are planned at 2x and sent 5 at a time; (5) a 429 stops the run (balance from the error body is remembered) and reports the rest in `NotFetched`;
(6) `Keepa:MaxTokensPerRun` (default 100) is a hard per-run ceiling shared by both enrichment stages; (7) 5xx/timeouts: 2 bounded retries with backoff, 4xx not retried.
*Key safety:* the key is only in the query string (Keepa has no header option), so `RemoveAllLoggers()` strips HttpClient logging, our logs/exceptions never include URLs,
ASIN/code inputs are regex-validated (no query injection), and tests assert the key never appears in logs or exception text.
*Parsing* (`KeepaResponseParser`, Keepa quirks confined here): prices are integer pence; -1/-2 = no data; stats arrays are indexed by csv type -
**0** Amazon price (>0 => Amazon on listing), **3** sales rank, **11** total new offers, **18** Buy Box incl. shipping (`avg90[18]` = 90-day average Buy Box, needs `buybox=1`),
**34** new FBA offer count (falls back to index 11 and sets `FbaSellerCountIsApprox`). `monthlySold` is Keepa's "bought in past month" (null for many ASINs =>
"no sales data", by design). Package weight (or item weight) and `fbaFees.pickAndPackFee` (pence, ex tax) are captured; the fee **overrides** the approximate FBA table.
Image is built from `imagesCSV` (filename allow-list). *The fixtures in tests are hand-written from Keepa's published model, not recorded - see Phase 7.*

**`EnrichmentService`** (`IEnrichmentService`): (1) *resolve* stub products (EAN, no ASIN) with code lookups - the response already contains the market data, so the snapshot
is saved immediately (no second request); one ASIN -> `Resolved`; none -> `NotFound` (retry after `Keepa:UnresolvedRetryDays`=7); several -> `Ambiguous` (**never auto-picked**; up to 10 options are stored in `AsinCandidate` with their market data, and the owner settles it on the **Unresolved** page - see below); an ASIN we already hold -> the stub is merged into it. (2) *refresh* products with an ASIN whose `KeepaCheckedAt` is older than `Keepa:RefreshAgeHours`
(never-fetched first, then oldest). Only products with at least one non-out-of-stock offer are worth tokens. `KeepaCheckedAt` is stamped for every request, even empty answers,
so a product Keepa has nothing on is not re-charged each run. Budget leftovers are `ProductsDeferred`. `B0FAKE*` ASINs are never sent to the live client.
Snapshots store `FbaSellerCount` (0 + `FbaSellerCountIsApprox` when unknown), `WeightGrams`, `KeepaFbaFee`.

**`OpportunityService`** (`IOpportunityService.RecalculateAsync`): for each matched in-stock offer with a latest snapshot that has an `Avg90BuyBox`: `ProfitCalculator`
(with weight + Keepa FBA fee) -> `OpportunityScorer` (buy cash = `BuyPriceIncVat`) -> upsert by `SourceOfferId`, **preserving `Status`**. `Opportunity.MaxBuyPrice` = inc-VAT value. Flags: `NoSalesData` +
`IEligibilityChecker` (manual toggles now; blocklist in Phase 6). Rows that stop qualifying are deleted **only if still `New`** (Checked/Bought/Ignored are kept). Amazon-on-listing, negative-profit and stale
rows still get opportunities - **the Buy List page filters them** (Phase 5).

**Unresolved page** (`Pages/Unresolved`, nav "Unresolved"): for each `Ambiguous` product shows the supplier title, EAN, its offers (retailer, price, source link) and a
**"Maps to multiple ASINs"** badge with a condensed options table (ASIN -> amazon.co.uk link, listing, rank, sold/month, 90d Buy Box, Amazon on listing; missing values read "no data";
first 5 shown, the rest behind a "+N more options" disclosure). **Use this one** (`IAsinResolutionService.ChooseAsync`) accepts only an ASIN that was actually offered, assigns it (merging into an
existing product with that ASIN if there is one), saves that option's stored data as the snapshot, and recalculates opportunities - **no Keepa tokens**. **None of these** marks the EAN `NotFound`. Below, a
"Not found on Amazon" table lists `NotFound` products with their offers. A later retry that settles on one ASIN (or none) clears stale options. Migration `AsinCandidates`.

**`IRefreshPipeline`** = match pending -> enrich -> recalculate, single-flight (a second concurrent run is refused so tokens are never double-spent). Phase 6's job + "Refresh now" button call this.

**`FakeKeepaClient`**: 38-entry deterministic catalogue keyed on `samples/offers.sample.csv` EANs (embedded `fake-catalogue.json`): good/bad margins, 4 Amazon-on-listing, 2 no-sales-data, 1 no-price,
1 approximate FBA count, sales from 4 to 600/month. Spends nothing.

**CLI** (`dotnet run --project src/BuyList.Web -- <cmd>`): `seed` (empty DB only; **always uses the fake catalogue**, so it never spends tokens even with `UseFake=false`; don't seed a DB you'll point at live data),
`refresh` (runs the pipeline with the configured client and prints a summary incl. tokens), `check-keepa [ASIN ...]` (live API, **max 3 ASINs**, prints balance (free) and parsed fields; no args = balance only).
In Development with `UseFake=true` and `Seed:OnStartup=true` (set in appsettings.Development.json) an empty DB is seeded automatically.

Schema changes (migration `KeepaEnrichment`): `Product.AsinLookupStatus`, `Product.KeepaCheckedAt`; `AmazonSnapshot.FbaSellerCountIsApprox`, `.WeightGrams`, `.KeepaFbaFee`.
Test-suite note: web tests must set config with `builder.UseSetting(...)`, **not** `ConfigureAppConfiguration` - `Program.cs` reads configuration before the host is built, so the latter silently doesn't apply.

## Done: Phase 5 – Buy List page and dark theme (reference)

**Dark theme:** the whole app is dark (`<html data-bs-theme="dark">`, Bootstrap upgraded 5.1 -> 5.3.3 and vendored locally; jQuery and the unused validation scripts removed). Palette tokens and the table/risk-badge styles are in `wwwroot/css/site.css`.
Risk badges run red -> green (VeryHigh `#da3633`, High orange, Medium amber, Low light green, VeryLow green) and always carry a text label.

**Query layer** (`Application/Listing` = contracts, `Infrastructure/Listing/BuyListService`; the namespace is `Listing`, *not* `BuyList`, which would shadow the `BuyList.` root):
`BuyListQuery` -> `IBuyListService.QueryAsync / QueryAllAsync / SetStatusAsync / GetPipelineStatusAsync`. One SQL query joins `Opportunity` + the product's **latest** `AmazonSnapshot` (anti-join).
- **Defaults** (the "sensible" view): `MinNetProfit = 0` (clear it to see losses), `HideAmazon = true`, stale hidden (`Ranking:StaleAfterHours`, 24h), status = New + Checked (Ignored hidden; Bought has its own tab), blocklisted always hidden, ranking = risk-adjusted.
- **Filters:** min ROI, min net profit, min monthly sales (rows with no sales data fail any minimum), max risk (`RiskClassifier.IsWithin`), hide Amazon-on-listing, hide flagged (= Gated/Hazmat/IP-prone only; no-sales-data is **not** "flagged" - it reads "no data" and the min-sales filter handles it), max unit cost (inc VAT), retailer, include stale (greyed, marked "stale"), status.
- **Sorting:** `BuyListSort` enum whitelist (never user text in SQL); all 16 columns both ways; Risk sorts by severity (CASE), not alphabetically; ties = ROI desc, profit desc, id asc (same as `OpportunityRanker`). Default sort follows the ranking toggle (Risk-adjusted `RiskAdjusted` / Raw `EstMonthlyProfit`). Paging 25/50/100/200, out-of-range page clamps.
- **`HiddenSummary`:** "Hidden by filters: 3 below the minimum profit · 3 with Amazon on the listing ..." - counts scoped to the *current view* (retailer etc.), only for filters that are on.
- **`PipelineStatus`** explains an empty list instead of showing a blank page: no offers -> "Import prices"; offers but no opportunities -> where the pipeline stands (matched, awaiting Amazon lookup, ambiguous, not found, no Buy Box price) + demo/live note; opportunities exist but filtered out -> "Reset filters".

**Page** (`Pages/Index`, query-string driven so every view is bookmarkable; all header/pager links keep the current filters): the 17 columns of the brief - image, title/brand/ASIN, buy price (inc, ex VAT underneath), retailer **source link** (`target=_blank rel="noopener noreferrer"`) with "checked 3h ago", Amazon 90d avg, net profit (green/red) + margin, ROI, sales/mo, est. and risk-adjusted £/mo, risk badge, days to sell, competition (FBA count, `~` when approximate; "Amazon" tag), flags, max buy, qty, status dropdown, quick links (Amazon UK, Keepa, SellerAmp, Source). Missing values read **no data**. Tabs **Buy List / Bought**.
Row action = POST (antiforgery) `?handler=Status`; the `returnUrl` is only honoured if local; an unparseable status is a 400 (it must not silently become "New").
**Export CSV** (`?handler=Export`, same filters/order, all pages, UTF-8 BOM, formula-injection-safe text cells, negative numbers stay numeric, includes source URL and Amazon/Keepa links).
**Refresh now** (`?handler=Refresh`) runs `IRefreshPipeline` (demo: no tokens; live: confirm dialog + the `MaxTokensPerRun` cap) and reports results/token use; single-flight. (The scheduled background job is still Phase 6.)

Tests: `Listing/BuyListServiceTests` (every filter and sort against real SQLite), `Listing/ListingHelpersTests` (TimeAgo, links, CSV), `Web/BuyListPageTests` + `BuyListEmptyStateTests` (end to end through the host: rendering, dark theme, link safety, "no data", filters, sort, paging, status actions, open-redirect and antiforgery checks, export, refresh, empty states). Also verified in a real browser (Chromium): sort, filter, status change, Bought tab, CSV download.

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

## Phase 7 – Live verification and hardening (needs the owner's key + network)

The client is built (Phase 4) but has only been tested against hand-written fixtures; this sandbox can't reach `api.keepa.com` and has no key. On the EC2 box:
1. `export Keepa__ApiKey=...` (or the systemd `EnvironmentFile`), then `dotnet run --project src/BuyList.Web -- check-keepa` (free: shows balance).
2. `check-keepa B0XXXXXXXX` on 1-3 real ASINs you know. Check: 90d avg Buy Box matches Keepa's chart; sales rank; FBA sellers; Amazon-on-listing; monthly sold; weight/fee.
3. Paste any surprise (key redacted!) back. Things most likely to need adjusting: whether `stats` is populated with `history=0` (if not, drop `history=0` in `KeepaClient`), whether `avg90[18]` is
   present for all products, the real `tokensConsumed` per product (then tune `Keepa:TokensPerProduct`), and how often `monthlySold` is null.
4. Then `refresh` with `Keepa__UseFake=false` against a copy of the DB (or a fresh one with a small CSV) and compare opportunities to Seller Central / Keepa by hand.
Open follow-ups: Amazon-in-stock-percent over 90 days.

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
| D18 | Real Keepa client built in Phase 4, fake kept for tests/seed/offline | Owner supplies a key; earlier real data beats delayed verification |
| D19 | Token safety is layered: free balance check, reserve, per-call and per-run caps, learned real cost, 2x for code lookups | "Do not go over the limit" - Keepa permits negative balances, so the check must precede spending |
| D20 | Ambiguous EAN (several ASINs) is never auto-picked; the options (max 10, best sales rank first) are stored and shown on **Unresolved** as "Maps to multiple ASINs" with a condensed list (5 up front, rest behind "+N more"); owner clicks "Use this one" (costs no tokens) or "None of these" | A wrong ASIN means buying the wrong product; owner decision |
| D21 | Sales come only from Keepa `monthlySold`; null => `NoSalesData` flag + VeryHigh risk, no proxy. **Wherever any value is missing the UI says plainly "no data"** (`Gbp.NoData` / `Gbp.Format(null)`), never "-", "N/A", "unknown" or a guess | Owner decision; spec: never guess |
| D23 | Hide-flagged = Gated/Hazmat/IP only; missing sales data is shown as "no data" rather than as a separate flag badge | Owner wants plain "no data"; min-sales filter covers the rest |
| D24 | "Refresh now" button added in Phase 5 (pipeline already existed) | Otherwise an empty Buy List could only be filled from the CLI |
| D22 | `seed` always uses the fake catalogue | Seeding must never spend tokens |
| D17 | Rows with extra values vs header are rejected | Catches unquoted-comma column shifts that would corrupt price/title silently |

## Open questions for the owner

- Confirm FBA fee tiers / referral % per category you actually sell in.
- Are supplier CSV prices usually ex-VAT (trade) or inc-VAT (retail)? (`vat_included` handles both, but defaults in the sample file follow your answer.)
- Preferred default branch name and whether PRs should be used per phase.
