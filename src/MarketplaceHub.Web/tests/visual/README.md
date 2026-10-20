# Visual and cascade checks

Two tools for changing the panel's CSS without being able to sign in to the live panel and look:

- **Visual capture** (`npm run test:visual`) renders the built panel in headless Chromium with a mocked API and a
  signed-in owner, and records the full computed style and box of every element (and of `::before`/`::after`) plus a
  full-page screenshot, for every scenario in `scenarios.mjs` at 375, 768, 1280 and 1920 px. Two captures are
  compared element by element; one CSS change reads as one line ("12x padding-top: 8px -> 10px").
- **Cascade check** (`npm run check:cascade`) is a static model of the whole cascade (selectors, specificity, layers,
  `!important`, media, load order of the main bundle, the tablet/phone sheets and each lazily loaded chunk). It
  compares a git revision with the working tree and reports every declaration that appeared or disappeared and every
  pair of competing declarations whose winner changed. It covers what the capture cannot reach: hover and focus
  states, closed windows, screens without fixtures.

A refactor (moving rules between files or layers, merging duplicate selectors, dropping `!important`) ships only
when both are empty. A fix ships when both show only the intended elements.

## Visual capture

```bash
npm run test:visual -- --update      # on the commit you start from: build + write the baseline (tests/visual/.out/baseline)
# ...change CSS...
npm run test:visual                  # build + capture + compare with the baseline; exit code 1 when anything differs
npm run test:visual -- --no-build --only orders,returns --widths 375,1280
```

Lower level: `node tests/visual/capture.mjs --dist <built folder> --out <folder>` and
`node tests/visual/diff.mjs <baseline folder> <current folder>`.

- `fixtures.mjs` and `fixtures/*.mjs` hold the mocked API data (shell, sales, catalog, back office). Every request a
  scenario makes must be answered by a fixture; the capture lists the ones that fell through to the empty default.
- The clock is frozen at 2026-10-20 09:30 Istanbul, animations and transitions are off, lazy images are loaded,
  requests to other hosts are refused and the pointer leaves the page before the snapshot, so two captures of the
  same build are identical (checked: 54 of 54 captures, styles and pixels).
- The browser is Playwright's Chromium (`npx playwright install chromium`), else the newest one in the Playwright
  cache, else `PLAYWRIGHT_CHROMIUM_EXECUTABLE`. Compare only captures made with the same browser build.
- A full run (15 scenarios, 54 captures) takes about 45 s with 4 workers, plus the build.

## Cascade check

```bash
npm run check:cascade                       # HEAD vs working tree
npm run check:cascade -- --base origin/main # any revision
```

Two declarations compete when their properties overlap (shorthands and longhands, logical and physical sides) and
their selectors may match one element (their rightmost compounds share a class, or one has no class and their element
types do not exclude each other); the model is deliberately conservative. Order between two different lazily loaded
chunks is unknown (it depends on which screen opens first); a pair whose order was already unknown and stays the
same unknown is not reported. Inline `style` attributes are outside the model: dropping an `!important` on a
property the code also sets inline needs a manual check.

## Not in CI

Neither check is a CI gate: both compare against a baseline taken from the base revision (two builds), the capture
needs a Chromium download on the runner, and a difference is a question for a person ("is this the intended
change?"), not a pass/fail fact. Run both locally before a stylesheet change.
