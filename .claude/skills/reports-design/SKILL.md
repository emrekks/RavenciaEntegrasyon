---
name: reports-design
description: Design rules, a local preview with a realistic report and a layout audit for the Raporlar (reports) page of the web panel (src/MarketplaceHub.Web/src/features/reports). Use before changing how any report tab looks or what figures it shows, and to check a report change in the browser.
---

# Raporlar design

The page is `src/MarketplaceHub.Web/src/features/reports/` (`ReportsPage.tsx`, `reports.css`, `report-visuals.tsx`,
`report-format.ts`); its data comes from `GET /reports/sales` (`ReportsContracts.cs`, `ReportBuilder.cs`,
`ReportsService.cs`). The owner reviews every change by eye and says plainly what is wrong; these rules are what they
asked for, in their words where it matters.

## Rules

1. **Turkish UI, plain words.** Labels name what the seller understands ("Masraflar Sonrası", not "payout").
2. **Calm finance board.** Flat cards on the surface colour, one accent (green for good, red for bad), no gradients,
   no decoration. "Ferah, temiz" — never crowded. Negative figures red, positive results green.
3. **Motion once.** A chart or figure animates only when it first scrolls into view (`useReveal`, `CountUp` with
   `active`), never in a loop; everything is static with reduced motion. A count never shows a value below zero.
4. **One spacing scale.** 20px between cards on every tab, 16px between rows inside a card, 12px between tiles. Cards
   side by side are the same height ("biri uzun biri kısa olmasın"): grids use `align-items: stretch`, a table wrapped
   for a grid column is itself `display: grid`, an empty panel centres its "Gösterilecek değer yok.".
5. **Line things up.** Steps or columns that sit side by side share rows (CSS subgrid), so titles, figures and notes
   line up across them. Filter controls share one label style and one height (40px).
6. **Details in a window.** Extra detail opens in `Modal`, never by expanding in place: nothing under it may move
   (layout shift 0).
7. **Figures agree.** Every total on a card is derived from the one before it on the same card (sales − deductions =
   what is left − product cost = profit). Never show a server figure computed on another basis next to them (23 Oct:
   a "net profit" above "after costs" because the server's left out cargo and fees). Say what an estimate leaves out.
8. **Show what the marketplaces show.** When asked to add reports, compare with the seller panels (Trendyol "Satış &
   Operasyon" reports) and add what our data can compute honestly; say what cannot be added and why.
9. **Never duplicate a card.** A breakdown shown in a step's window is not repeated as another card.

## Workflow

1. Read the parts of `ReportsPage.tsx` and `reports.css` you change. CSS selectors are scoped
   `.app-shell.stitch-shell main .reports-page …`; grep the class name first, an old rule of the same name may exist
   (a new `.reports-split` once picked up an older two-column rule).
2. Change, then run from `src/MarketplaceHub.Web`: `npm run typecheck`, `npx eslint src/features/reports --max-warnings=0`,
   `npx vitest run src/features/reports`. Update `ReportsPage.test.tsx` with the new figures.
3. Preview with a realistic 30-day report (no backend needed):

   ```bash
   cd "$CLAUDE_PROJECT_DIR/src/MarketplaceHub.Web" && npx vite build --outDir "<scratchpad>/reports-preview" --emptyOutDir
   node "$CLAUDE_PROJECT_DIR/.claude/skills/reports-design/serve.mjs" "<scratchpad>/reports-preview"   # background
   ```

   Open `http://127.0.0.1:4319/reports` in the built-in browser. Build from the web folder (from anywhere else
   `--emptyOutDir` empties the output and fails). Add fields the change needs to `report-data.mjs`. The panel keeps
   query results in `sessionStorage` (`rv-query-snapshot`): after changing the server's answers, open a new tab or
   clear it, or the page shows the old answer.
4. Audit the layout: run the contents of `audit.js` in the page (javascript tool), then read `window.__reportsAudit`
   about two minutes later. It loads every tab at 1000, 1280 and 1800px and lists cards of unequal height in a row and
   gaps other than 20px; an empty list means none.
5. Look at each changed tab yourself (screenshot at 1320px and phone width, no horizontal scroll). A hidden browser
   pane pauses transitions: wait or take a second screenshot before judging an animation.
6. Get the `reports-ui-reviewer` agent's read before saying the design is done.
