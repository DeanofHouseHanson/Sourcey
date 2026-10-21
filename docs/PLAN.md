# Plan v2 – Automated FBA deal finder

> Replaces the original plan (archived in `docs/archive/PLAN-v1.md`; its "Done: Phases 1-5 (reference)" sections still describe how the existing code works).
> Status: **DRAFT v2.1 for owner approval**, updated with the owner's answers. Only the "Reuse" items exist today.

## 1. Goal and constraints

A website that finds products which will make a profit on Amazon UK FBA, **automatically**, so the owner reviews a short ranked list of actionable deals instead of hand-feeding CSVs.

Owner decisions and facts:
- New plan, **reuse the working code**.
- **Amazon-first** discovery: find products that sell well and hold their price, compute the maximum buy price, then find a source below it.
- **Single user now, other sellers later** (design seam only; see section 5).
- **Website only for now.** Alerts will come later as a **Discord bot** (explicitly *not* to be implemented yet).
- **No category is off limits.** (The account plan and gating still decide what can actually be sold; the app flags them.)
- **Not wholesale: no capital for it yet.** Sourcing is retail/online arbitrage with a **small budget**, so capital efficiency matters as much as profit.
- Amazon seller account: **basic (Individual) plan**. See the blocker below.
- Keepa: **"Pro" plan** (to be confirmed as an *API* plan; see Phase 0).

### Blockers and risks the owner should decide on first

1. **Individual plan vs Buy Box.** Amazon's sources and seller guides say Individual sellers are **not eligible to win the Buy Box**, and also can't use bulk tools or ads. Our whole profit model sells at the *Buy Box* price. If the account can't win the Buy Box, real sales will be far lower than the figures shown. Amazon's own pages state that Individual costs 75p per item sold and Professional is £25/month (ex VAT) with no per-item fee, break-even about 35 units/month. **Recommendation: move to Professional before relying on any deal this app finds.** This is the owner's call.
2. **SP-API needs Professional.** Amazon's developer documentation says private seller applications require a Professional account; Individual accounts are not eligible. So the SP-API work (real fees, gating checks) is **blocked until the account is upgraded**; until then the app uses Keepa's reported FBA fee and the approximate table.
3. **Individual per-item fee isn't in the profit maths.** Until Professional, each sale also costs 75p. Add `Profit:PerItemSellingFee` (Phase 0).
4. **VAT status.** The calculator defaults to *VAT registered*. If the owner is not VAT registered (turnover below the threshold) the figures differ materially. Set `Profit:VatRegistered` correctly (Phase 0). Open question below.
5. **"Nothing off limits" vs account plan.** Several categories need Professional plus approval; gated/hazmat/IP-complaint-prone items are flagged (and hidden by default if asked) but never auto-assumed sellable.
6. **Brand-protected items need valid invoices** for ungating and IP disputes. Retail receipts may not satisfy a brand owner. Flagged brands should be avoided until the owner understands the invoice requirements.

## 2. The pipeline (Amazon-first)

```
 A DISCOVER        B TARGET            C SOURCE (retail/online)     D DECIDE (capital-aware)     E ACT / LEARN
 Keepa Product  -> max buy price   -> affiliate feeds, CSV,       -> rank by profit AND by       -> owner buys; record
 Finder saved      per candidate,      quick "check a product"       profit per £ tied up per       purchase; compare actual
 searches          stability score     (EAN/ASIN + price)            day; budget mode               vs predicted
                          \-> F QUALIFY via SP-API (real fees, gating) once the account is Professional
```

## 3. What we reuse (already merged, tested)

| Piece | Where | Use in v2 |
|---|---|---|
| Profit maths incl. VAT, fee seam, max buy price | `Application/Profit` | Core of stage B |
| Risk bands, scoring, ranking | `Application/Risk`, `Ranking` | Stage D, extended with capital efficiency |
| Token-safe Keepa client + `KeepaTokenState`, fake client | `Infrastructure/Keepa` | Extended with Product Finder (phase 1) |
| CSV import, GTIN normalisation, EAN/brand+MPN matching | `Application/Import`, `Infrastructure/Import` | One source type among several |
| Enrichment, opportunities, refresh pipeline, seed, CLI | `Infrastructure/*` | Building blocks of the scheduled job |
| Buy List page, Unresolved page, dark theme | `Pages/*`, `Listing` | Stage D UI; add Targets page |

Nothing is thrown away up front; items become obsolete only when a replacement is proven.

## 4. Phases

Each phase is small, ships on a branch `claude/v2-N-<slug>`, keeps `dotnet test` green, and has hand-checked worked examples in its tests. **Live checks run on the EC2 box** (the build sandbox cannot reach Keepa or Amazon): every integration ships a `check-*` CLI command that spends almost nothing.

### Phase 0 – Re-baseline (small, do first)
- Adopt this plan (this PR). On EC2: run `check-keepa` (free) and record the **token balance and refill rate**; this confirms the Keepa plan is an *API* plan (a Keepa website/extension subscription is separate from API access) and sets `Keepa:MaxTokensPerRun` and the scheduler budget. Then `check-keepa` on 1-3 known ASINs and compare with Keepa's charts (still unverified from v1).
- Add `Profit:PerItemSellingFee` (default 0.75 while Individual, 0 on Professional); set `Profit:VatRegistered` to the owner's real status; show both on the Settings area so numbers are explainable.
- **Ownership seam** (D3): `OwnerId` on user-owned tables + `IUserContext` (single constant owner now).
- *Done when:* live Keepa values for 3 ASINs match Keepa's site, the token budget is written here, and profit settings reflect the real account.

### Phase 1 – Discovery: saved searches and Targets
- Keepa **Product Finder** in the existing client (same token governor, hard per-run cap; ASIN lists are then fetched in budgeted batches).
- `SavedSearch` (criteria, token budget, schedule) and `Candidate` (ASIN, latest snapshot, stability metrics, **max buy price**, status New/Watching/Dismissed).
- Criteria: sales rank / monthly sold range, 90-day average Buy Box price band, Amazon out of stock share, offer-count range, category include/exclude, price-stability limit. **Small-capital defaults:** low unit cost band, fast sell-through, low competition.
- **Stability metrics** from Keepa stats: current vs 30/90-day average, 90-day min/max spread, Amazon-in-stock share, offer-count trend. A price currently far above its 90-day average is a spike and is penalised, because the 90-day average is the sell price.
- **Targets page:** ranked "buy at or below £X", with demand, competition, flags; CSV export.
- *Done when:* a saved search on EC2 yields a Targets list within its token budget and the owner agrees with the top ten.

### Phase 2 – Capital-aware decisions and quick check
- **Budget mode** (carry-over from v1): given £X, pick the best set of buys by risk-adjusted monthly profit per £, with quantities and a total.
- **Capital efficiency metrics:** profit per £ tied up, expected days to sell, payback time, and ROI per day so a 20% ROI item that sells in 5 days can outrank a 60% ROI item that takes 60.
- **"Check a product"**: enter an EAN or ASIN and the price seen (in a shop or online) -> instant verdict (net profit, ROI, max buy, risk, gating flag, stability). The fastest useful retail-arbitrage tool and needs no sourcing integration.
- **Blocklist page** (brand/ASIN) (carry-over from v1).
- *Done when:* the owner can paste a product and price and get a trustworthy verdict, and budget mode never exceeds the budget.

### Phase 3 – Sourcing for retail/online arbitrage
- `ISourceFeed` adapter interface; the existing CSV import stays as the manual path (also for paid sourcing-tool exports, subject to their terms).
- **Affiliate product feeds** (e.g. Awin) for UK merchants the owner has joined: EAN, price (VAT-inclusive), stock; incremental by feed update time; per-source freshness shown. Check each programme's terms before use.
- Offers match to Candidates by EAN; an offer becomes an `Opportunity` only at or below the candidate's max buy price.
- **No scraping of retailer sites** unless the owner decides otherwise after legal advice (unsettled UK position, high maintenance, anti-bot measures).

### Phase 4 – Scheduler (website only)
- One hosted background job (single-flight, existing `IRefreshPipeline` pattern) runs: saved searches -> qualify -> match -> recalculate, each stage inside its own budget. "Refresh now" remains. The site shows what changed since the last visit ("new since yesterday").
- **No alerts yet** (see Phase 7).

### Phase 5 – Amazon truth via SP-API (starts only after the account is Professional)
Read-only; no listing or shipment writes.
- LWA authentication and a typed client for the EU endpoint / UK marketplace; per-operation rate limiter with backoff; credentials `SpApi__ClientId`, `SpApi__ClientSecret`, `SpApi__RefreshToken` (never in source or logs).
- **Catalogue by EAN** (up to 20 per call) to resolve EAN -> ASIN without spending Keepa tokens (Keepa lookups become the fallback).
- **Product Fees estimate** -> `IFeeEstimator`; **Listing restrictions** -> `IEligibilityChecker` (sets `Gated`, stores the approval link).
- CLI `check-spapi [ASIN...]`; fixture-based tests.
- *Done when:* a known ASIN's fee matches Seller Central's calculator and a known gated ASIN is flagged.

### Phase 6 – Purchases and learning
- Bought -> record quantity and actual cost; later pull actual sales and fees (SP-API orders/finances) to compare predicted vs actual profit and tune the model.

### Phase 7 – Discord bot alerts (LATER – do not implement yet)
Reserved. When the owner asks: a Discord bot posts new deals passing configurable rules (min profit/ROI, max risk, not gated, stable price), de-duplicated, with a daily digest. Design constraint now: keep "deal passed the rules" as a stored event so alerts can be added without changing the pipeline.

### Phase 8 – Inbound and listing automation (optional, high risk)
SP-API write operations only behind explicit per-action confirmation; separate decision.

### Phase 9 – Multi-user gate (decision point, not a build)
Resolve every item in section 5 "Gate G" before any second user.

## 5. Designing for "me now, others later"

- **Shared market data, private decisions.** `Product`, `AmazonSnapshot`, `AsinCandidate` (Keepa/Amazon facts) are shared (one fetch can serve many users, which is what makes multi-user economics work). User-owned: offers, opportunities, saved searches, candidate status, blocklist, settings (VAT, plan fees, target ROI, budget), later alert rules. These get `OwnerId` (D3).
- **Credentials behind an interface** (`ICredentialProvider`): environment variables today, encrypted per-user secrets later; no other code reads credentials.
- **Settings per owner**, not only `appsettings.json` (today's options become defaults).
- No implicit global state keyed to "the" user (the single-flight refresh lock becomes per-owner later).

**Gate G (answer before a second user; they may change the whole approach):**
1. **Keepa terms:** may Keepa data be shown to or shared between other sellers? Does each user need their own API plan? (Not researched: Keepa's docs were unreachable.)
2. **Amazon terms:** serving other sellers makes this a third-party developer app (public SP-API app, Amazon review). Developer fees were announced in 2025 and reported cancelled or delayed in 2026; status unconfirmed.
3. **Security and data:** per-user secret storage, tenant isolation tests, backups, UK GDPR.
4. **Money:** pricing that covers each user's Keepa token use.
5. **Liability:** a tool that influences purchasing needs clear disclaimers.
6. **Each user's account type** (Individual vs Professional) changes what the numbers mean (Buy Box eligibility), so the app must model it per user.

## 6. Hard rules (carried over)

No secrets in the repo or logs; no scraping of Amazon (Keepa and SP-API only); never guess missing data (shown as "no data"); match on EAN/GTIN then brand+MPN, never title alone; Keepa tokens are a hard budget (never raise caps without asking; live checks via `check-*` commands); `seed` stays offline; money is `decimal`, times UTC, UI in GBP and dark-only; every behaviour has hand-checked tests. See `CLAUDE.md`.

## 7. Assumptions to verify (secondary sources)

- Keepa Product Finder returns ASIN lists and pages results; its token cost per page (one source says 10) and exact filter field names are **unverified**. Deals browsing reportedly costs 5 tokens per 150 deals.
- Keepa API plans are separate from website subscriptions; a guide lists 20 tokens/min for €49 and a 60-minute token expiry. Phase 0 measures the real plan.
- SP-API rate limits are inconsistent across sources (Fees: 1 req/s burst 2; Catalog: 2 or 5 req/s). Use conservative limits and read the rate-limit response headers.
- SP-API developer fees: Amazon said private apps on a seller's own account are exempt; confirm on Amazon's developer announcements.
- UK fee changes (Dec 2025 reductions, Low-Price FBA threshold, peak-season fee of about £0.12/unit from 15 Oct 2026 to 14 Jan 2027) each rest on a single source; SP-API fee estimates (Phase 5) remove the dependency.
- Individual plan: 75p per item, no Buy Box, no ads (Amazon pricing page confirmed 75p / £25; Buy Box and ads limits come from a comparison site and seller guides, so confirm in Seller Central).

## 8. Decisions log (v2)

| # | Decision | Why |
|---|---|---|
| D1 | Reuse the existing code, new plan | Owner choice |
| D2 | Amazon-first discovery | Owner choice; gives a short target-price list |
| D3 | `OwnerId` + `IUserContext` on user-owned tables now | "Me now, others later"; cheap before real data exists |
| D4 | Shared market data, private decisions | One Keepa fetch can serve many users |
| D5 | SP-API read-only, only after Professional upgrade; Keepa fee + approximate table meanwhile | SP-API is unavailable to Individual accounts |
| D6 | No automatic purchasing | Retailer anti-bot, terms, stock errors, capital risk |
| D7 | No retailer scraping by default | Unsettled UK legal position; high maintenance |
| D8 | Not wholesale: retail/online sourcing, affiliate feeds, quick-check tool, small-budget defaults | Owner has no capital for wholesale yet |
| D9 | Capital efficiency (profit per £ per day) is a first-class ranking measure | Small capital must recycle fast |
| D10 | Alerts deferred; Discord bot later, website only now | Owner choice |
| D11 | Include the Individual-plan 75p per-item fee and the real VAT status in profit maths | Otherwise profit is overstated |
| D12 | Phases 6-9 optional, need owner approval | Dependent on unresolved questions or high risk |

## 9. Open questions for the owner

- **Will you move to the Professional plan?** (Buy Box eligibility, SP-API, restricted categories; £25 + VAT a month.) This decides whether Phase 5 can happen at all.
- **Are you VAT registered?** (Changes every profit figure.)
- What is your starting budget for stock (default for budget mode), and the minimum profit and ROI that make a deal worth your time?
- Which UK retailers do you buy from, or want to? (Decides which affiliate programmes matter in Phase 3.)
- Confirm after Phase 0's `check-keepa`: your Keepa plan, refill rate and token balance.
