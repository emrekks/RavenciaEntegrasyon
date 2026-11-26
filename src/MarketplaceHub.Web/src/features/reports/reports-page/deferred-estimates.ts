import type { Estimate } from './Deferred'

// The heights a waiting block's box takes (Deferred), per window width: phone (to 760 px), tablet (to 1024), laptop and
// wide screen. Lists and tables are worked out from their rows, as the CSS draws them (measured 26 Nov 2026 on the
// preview report at 375, 800, 1280 and 1920 px); a card whose height does not follow a row count has its measured
// height. A box only holds a place below the screen until its block comes, so a few pixels off move nothing in view.

type Tuple = readonly [phone: number, tablet: number, desktop: number, wide?: number]
const each = (base: Tuple, add: number): Tuple => [base[0] + add, base[1] + add, base[2] + add, ...(base[3] === undefined ? [] : [base[3] + add])] as unknown as Tuple

// A panel's padding and title: 8 px less on a phone.
const panel: Tuple = [0, 8, 8]
// A panel holding only "Veri Yok".
export const emptyPanel: Estimate = each(panel, 249)

// Bars (report-parts Bars): 20 px rows 12 px apart under the title.
export function barsEstimate(items: number): Estimate {
  return items <= 0 ? emptyPanel : each(panel, 60 + 32 * items)
}

// Rising and falling products (SalesTables Movers): up to 8 rows of 70 px.
export function moversEstimate(items: number): Estimate {
  return items <= 0 ? emptyPanel : each(panel, 68 + 70 * Math.min(8, items))
}

// A table panel (report-parts DataTable): its title row and head, then its rows; "Veri Yok" without rows.
export function tableEstimate(rows: number, rowHeight: number | Tuple, empty = 267): Estimate {
  const row: Tuple = typeof rowHeight === 'number' ? [rowHeight, rowHeight, rowHeight] : rowHeight
  return rows <= 0 ? each(panel, empty) : [127 + rows * row[0], 135 + rows * row[1], 135 + rows * row[2]]
}

// Two tables one above the other in a grid (12 px apart on a phone, 20 px elsewhere).
export function stackedEstimate(first: Estimate, second: Estimate): Estimate {
  const at = (estimate: Estimate, index: number) => typeof estimate === 'number' ? estimate : estimate[index] ?? estimate[2]
  return [at(first, 0) + 12 + at(second, 0), at(first, 1) + 20 + at(second, 1), at(first, 2) + 20 + at(second, 2)]
}

// A figure card of Genel Bakış (OverviewTab HeroCard): its head, then 41 px rows.
export function heroEstimate(rows: number): Estimate {
  return [123 + 41 * rows, 131 + 41 * rows, 131 + 41 * rows]
}

// Cards measured as they are.
export const fixed = {
  costLines: [592, 390, 296],
  bridge: [663, 527, 527],
  period: [593, 424, 527],
  costAndProfit: [1109, 1141, 601],
  unlinked: [171, 179, 179],
  platforms: [692, 716, 716],
  payoutList: [1201, 1322, 1100, 1027],
  productSubTabs: [48, 48, 48],
  stockCounts: [608, 460, 300, 142],
  stockBestSellerKpis: [360, 300, 140, 142],
  timeOfDay: [567, 637, 528],
  cities: [612, 652, 473]
} satisfies Record<string, Tuple>
