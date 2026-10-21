# Plan v2 – Automated FBA deal finder

> Replaces the original plan (kept for reference in `docs/archive/PLAN-v1.md`; its "Phase 1–5 reference" sections still describe how the existing code works).
> Status: **DRAFT for owner approval.** Nothing below is built yet except what is listed under "Reuse".

## 1. Goal

A website that finds products which will make a profit on Amazon UK FBA, **automatically**, so the owner reviews a short ranked list of actionable deals instead of hand-feeding CSVs.

Decisions already made by the owner:
- Start a new plan; **reuse the working code** where it fits.
- **Amazon-first**: start from Amazon/Keepa data to find products that sell well and hold their price, then find where to buy them below a computed maximum buy price.
- **Single user now, other sellers later**: build for one seller account, but do not paint the design into a corner (see section 5).
- Owner has a **Keepa API subscription** and a **confirmed Amazon seller account** (SP-API is possible).

Success looks like: every morning the site (or an alert) shows the best few deals, each with real fees, a stable price history, a max buy price, where to buy, and whether the owner is allowed to sell it.

## 2. The pipeline

```
 A DISCOVER        B QUALIFY         C TARGET          D SOURCE            E DECIDE & ALERT     F ACT / LEARN
 Keepa Product  -> SP-API: gating, -> Max buy price -> match offers/feeds -> rank (profit x risk) -> owner buys
 Finder saved      real fees,         per candidate     to candidates       alert when a deal     track purchase,
 searches          catalogue (EAN)    (target ROI)      by EAN               passes rules          compare actual v. predicted
```

Why Amazon-first: Amazon/Keepa tell us *what is worth selling* (demand, price stability, Amazon not competing). The profit calculator then turns each candidate into a **target price** ("buy this at £X or less"), so sourcing becomes a lookup against a short list, not a scan of the whole internet.

## 3. What we reuse (already merged, tested)

| Piece | Where | Use in v2 |
|---|---|---|
| Profit maths incl. VAT, fees seam, max buy price | `Application/Profit` | Core of stage C; fee estimator swapped for SP-API (phase 1) |
| Risk bands, scoring, ranking | `Application/Risk`, `Ranking` | Stage E ranking, unchanged |
| Token-safe Keepa client + `KeepaTokenState`, fake client | `Infrastructure/Keepa` | Extended with Product Finder (phase 2) |
| CSV import, GTIN normalisation, EAN/brand+MPN matching | `Application/Import`, `Infrastructure/Import` | Stage D "supplier list" source |
| Enrichment, opportunities, refresh pipeline, seed, CLI | `Infrastructure/Enrichment`, `Opportunities`, `Refresh` | Becomes the scheduled job's building blocks |
| Buy List page, Unresolved page, dark theme | `Pages/*`, `Listing` | Stage E UI; add a "Targets" page (phase 2) |

Nothing is thrown away up front. Items become obsolete only when a replacement is proven (e.g. `ApproximateFeeEstimator` stays as the fallback when SP-API is unavailable).

## 4. Phases

Each phase is small, ships on its own branch `claude/v2-N-<slug>`, keeps `dotnet test` green, and ends with a hand-checked worked example in its tests. **Live checks run on the EC2 box** (the build sandbox cannot reach Keepa or Amazon): every external integration ships with a `check-*` CLI command that spends almost nothing.

### Phase 0 – Re-baseline (small)
- Archive v1 plan (done in this PR) and adopt this one.
- On EC2: `check-keepa` (free) to record the **real token balance and refill rate**; set `Keepa:MaxTokensPerRun` etc. from it. Run `check-keepa` on 1–3 known ASINs and compare against Keepa's charts (this was Phase 7 of v1; still unverified).
- **Ownership seam** (decision D3, section 5): add an `OwnerId` column to user-owned tables with a constant single owner and an `IUserContext`. Cheap now, expensive later.
- Fix anything the live check shows (which Keepa fields are actually populated; real per-product token cost).
- *Done when:* live Keepa values for 3 ASINs match Keepa's own site, and the token budget is recorded in this file.

### Phase 1 – Amazon truth via SP-API (read-only)
Replaces guesses with Amazon's own data. All calls are read-only; **no listing or shipment writes**.
- LWA authentication (refresh-token flow) and a typed `SpApiClient` for the EU endpoint, UK marketplace; per-operation rate limiter with backoff; credentials from `SpApi__ClientId`, `SpApi__ClientSecret`, `SpApi__RefreshToken` (never in source, never logged).
- **Catalogue by EAN** (up to 20 identifiers per call): resolves EAN -> ASIN and attributes (brand, dimensions) **without spending Keepa tokens**. Keepa code lookups become the fallback. Several ASINs for one EAN still goes to the existing Unresolved screen.
- **Product Fees estimate**: real referral + FBA fee for an ASIN at a given price; cached by ASIN and price band; implements `IFeeEstimator` (existing seam). Falls back to the approximate table on failure.
- **Listing restrictions**: per ASIN for this seller account -> sets the `Gated` flag automatically and stores the approval link when approval is available; implements `IEligibilityChecker`.
- CLI `check-spapi [ASIN...]`. Fixture-based tests for parsing, rate limiting, error handling, key redaction.
- *Done when:* on EC2 the fee for a known ASIN matches Seller Central's revenue calculator, and a known gated ASIN is flagged.

### Phase 2 – Discovery: saved searches and Targets
- Keepa **Product Finder** support in the Keepa client (same token governor; a hard per-run cap; results are ASIN lists that are then fetched in budgeted batches).
- `SavedSearch` (criteria + token budget + schedule) and `Candidate` (ASIN, latest snapshot, stability metrics, computed **max buy price**, status New/Watching/Dismissed).
- Criteria the owner can set: sales rank / monthly sold range, 90-day average Buy Box price band, Amazon out of stock for at least X% of 90 days, offer-count range, category include/exclude, price-stability limit.
- **Stability metrics** from Keepa stats: current vs 30/90-day average, 90-day min/max spread, Amazon-in-stock share, offer-count trend. A candidate whose price is spiking (current far above 90-day average) is penalised: the 90-day average is the sell price, so a spike is a trap.
- **Targets page**: ranked "buy at or below £X" list per candidate with demand, competition and gating status; CSV export to send to suppliers for quotes.
- *Done when:* a saved search on EC2 produces a Targets list within its token budget, and the owner agrees with the top ten.

### Phase 3 – Sourcing: match supplier data to Targets
- Existing importer feeds stage D: an offer whose EAN matches a candidate becomes an `Opportunity` only if its price is at or below the candidate's max buy price (otherwise it is stored but not surfaced).
- **Scheduled ingestion** of supplier lists: a watched folder first (`Sources:Inbox`), with a per-source freshness indicator; later email attachments or a URL pull.
- Carry-overs from v1: **blocklist page** (brand/ASIN) and **budget mode** (best set of buys for a given £ budget, ranked by risk-adjusted monthly profit per pound).
- *Done when:* dropping a supplier CSV into the inbox produces opportunities without opening the site.

### Phase 4 – Scheduler and alerts
- One hosted background job (single-flight, existing `IRefreshPipeline` pattern) runs: saved searches -> qualify -> match -> recalculate, each stage inside its own budget.
- **Alert rules** (min monthly profit, min ROI, max risk, not gated, price stable) -> email and/or Telegram/Discord webhook; de-duplicated; optional daily digest.
- "Refresh now" button remains.
- *Done when:* a new qualifying deal reaches the owner's phone without opening the site, and the same deal does not alert twice.

### Phase 5 – Feeds (optional, owner decides)
- `ISourceFeed` adapter interface and an **affiliate product feed** adapter (e.g. Awin) for UK merchants the owner has joined: EAN, price (VAT-inclusive), stock; incremental by feed update time.
- Check each programme's terms for price/stock use before enabling. **No scraping of retailer sites** unless the owner decides otherwise after legal advice.

### Phase 6 – Purchases and learning
- Bought -> record quantity and actual cost; later pull actual sale prices and fees (SP-API orders/finances) to compare predicted vs actual profit and tune the model.
- Inventory/ROI view.

### Phase 7 – Inbound and listing automation (optional, high risk)
- SP-API write operations (listings, inbound shipments) only behind explicit per-action confirmation. Not started without a separate decision.

### Phase 8 – Multi-user gate (decision point, not a build)
Before any second user: resolve every item in section 5 "Gate G". Only then plan the build.

## 5. Designing for "me now, others later"

Principles that cost little now:
- **Shared market data, private decisions.** `Product`, `AmazonSnapshot`, `AsinCandidate` (Keepa/Amazon facts) are global and shared (one fetch serves everyone, which is the token economics that make multi-user viable). User-owned: offers, opportunities, saved searches, candidates' status, blocklist, settings (VAT, target ROI, budget, inbound cost), alert rules. These get `OwnerId` (D3).
- **Credentials behind an interface.** `ICredentialProvider` returns Keepa and SP-API credentials for the current owner. Today it reads environment variables; later it reads encrypted per-user secrets. No code outside it reads credentials.
- **Settings per owner**, not only `appsettings.json` (today's `Profit`, `Ranking` options become defaults).
- No global mutable state keyed implicitly to "the" user (the single-flight refresh lock becomes per-owner later).

**Gate G (must be answered before a second user, because they may change the whole approach):**
1. **Keepa terms:** may Keepa data be shown to or shared between other sellers? Does each user need their own API plan? (Not researched: Keepa's docs were unreachable from the research tool.)
2. **Amazon terms:** serving other sellers makes this a third-party developer app. SP-API developer fees were announced for such apps in 2025 and reported cancelled/delayed in 2026; status unconfirmed. A public app also needs Amazon's app review.
3. **Security and data:** per-user secret storage, tenant isolation tests, backups, GDPR/UK-GDPR.
4. **Money:** billing/pricing that covers each user's Keepa token use.
5. **Support and liability:** a tool that influences purchasing decisions needs clear disclaimers.

## 6. Hard rules (carried over, unchanged)

No secrets in the repo or logs; no scraping of Amazon (Keepa and SP-API only); never guess missing data (shown as "no data"); match on EAN/GTIN then brand+MPN, never title alone; Keepa tokens are a hard budget (never raise caps without asking, live checks via `check-*` commands); `seed` stays offline; money is `decimal`, times UTC, UI in GBP and dark-only; every behaviour has hand-checked tests. See `CLAUDE.md`.

## 7. Assumptions to verify (from research, secondary sources)

These came from search summaries and vendor pages, not from Amazon/Keepa documentation:
- Keepa Product Finder returns ASIN lists and pages results; its token cost per page (a source says 10) and the exact filter field names are **unverified**. Deals browsing is reported at 5 tokens per 150 deals.
- Keepa plan sizes (a guide lists 20 tokens/min for €49) and token expiry (60 minutes) are **unverified**; Phase 0 measures the real plan.
- SP-API rate limits are inconsistent across sources (Fees: 1 request/s burst 2; Catalog: 2 or 5 requests/s). Use conservative limits and read `x-amzn-RateLimit-Limit` headers.
- SP-API usage fees: for the owner's own private app Amazon said no fees; confirm on Amazon's developer announcements before launch.
- UK fee changes (December 2025 reductions, Low-Price FBA threshold, peak-season fee of about £0.12/unit from 15 Oct 2026 to 14 Jan 2027) each rest on a single source. Phase 1's real fee estimates remove the dependency on these.
- Whether SP-API registration requires the Professional selling plan: believed yes, not verified.

## 8. Decisions log (v2)

| # | Decision | Why |
|---|---|---|
| D1 | Reuse the existing code, new plan | Owner choice; most of stages B, C, E already work |
| D2 | Amazon-first discovery | Owner choice; produces a short target-price list to source against |
| D3 | Add `OwnerId` + `IUserContext` to user-owned tables now | "Me now, others later"; far cheaper before there is real data |
| D4 | Shared market data, private decisions | One Keepa fetch can serve many users |
| D5 | SP-API read-only first; EAN->ASIN via Amazon catalogue, Keepa as fallback | Saves Keepa tokens; real fees and gating replace guesses |
| D6 | No automatic purchasing; alerts + links instead | Retailer anti-bot, terms, stock errors, capital risk |
| D7 | No retailer scraping by default | Unsettled UK legal position; high maintenance |
| D8 | Phases 5-8 are optional and need owner approval | Highest legal/operational risk or dependent on unresolved questions |

## 9. Open questions for the owner

- Which categories do you want to sell, and which are off limits?
- Wholesale suppliers, retail, or both? (Decides whether Phase 3 ingestion or Phase 5 feeds matters more.)
- Preferred alert channel: email, Telegram or Discord?
- Which Keepa plan are you on (Phase 0 will tell us; a quick `check-keepa` answers it)?
- Is the seller account on the Professional plan, and is it already registered as a developer?
