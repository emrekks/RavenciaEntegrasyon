// The one rule for what a summary card (and its sub-lines) shows when there is no data for a value (owner, 14 Nov 2026),
// the same on every page:
//  - No data for a value - no records in the period or list, nothing to base it on, not loaded, failed - reads "—" in the
//    neutral ink, for money, a percentage and a count alike (never "0", "₺0", "%0", and never a word: "Veri yok",
//    "Tutar yok", "Bilgi yok"). A change pill with nothing to compare reads "—" too.
//  - A real non-zero value reads as it is; a warning, good or bad colour only marks such a value.
//  - The small counters on tabs, chips and the sidebar are not cards: they stay numbers, "0" included.
export const noValue = '—'

type CountState = { loading?: boolean; error?: boolean }

/** A card's count: "…" while it is on its way, "—" when it failed or there is nothing to count, else the number. */
export function cardCountText(value: number | null | undefined, state: CountState = {}): string {
  if (state.error) return noValue
  if (state.loading) return '…'
  return typeof value === 'number' && Number.isFinite(value) && value > 0 ? value.toLocaleString('tr-TR') : noValue
}

/** True when a figure deserves its semantic colour: a number above nought. */
export const isAboveZero = (value: number | null | undefined) => typeof value === 'number' && value > 0

/** The tone a card may use for its figure: the tone itself above nought, else nothing (neutral ink). */
export function toneAbove<T extends string>(value: number | null | undefined, tone: T): T | undefined {
  return isAboveZero(value) ? tone : undefined
}
