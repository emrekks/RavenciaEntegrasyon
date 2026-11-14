// The one rule for empty values on every page (owner, 14 Nov 2026).
//  - The big number of a summary card reads "—" only when there is no data source for it: no active connection that feeds
//    it, not loaded, the request failed, or it cannot be worked out (a margin or an average over a zero base).
//  - When the source exists and the count or amount is really zero, it reads "0" / "₺0" / "%0" in the neutral ink, with
//    its unit word ("0 Paket", "0 Bekleyen"). A dash takes no unit word.
//  - Sub-lines, notes, change pills and tab, chip, badge and sidebar counters read zero in their own format when empty
//    ("Sipariş başına ₺0", "%0", "0"). A reason note ("Maliyet Eksik") stays as it is.
//  - A warning, good or bad colour only marks a number above nought.
export const noValue = '—'

type CountState = { loading?: boolean; error?: boolean; hasSource?: boolean }

/** A card's count: "…" while it is on its way, "—" without a data source or when it failed, else the number ("0" included). */
export function cardCountText(value: number | null | undefined, state: CountState = {}): string {
  if (state.error) return noValue
  if (state.loading) return '…'
  if (state.hasSource === false) return noValue
  return typeof value === 'number' && Number.isFinite(value) ? value.toLocaleString('tr-TR') : noValue
}

/** True when a figure deserves its semantic colour: a number above nought. */
export const isAboveZero = (value: number | null | undefined) => typeof value === 'number' && value > 0

/** The tone a card may use for its figure: the tone itself above nought, else nothing (neutral ink). */
export function toneAbove<T extends string>(value: number | null | undefined, tone: T): T | undefined {
  return isAboveZero(value) ? tone : undefined
}

/** A main value with the unit word that belongs to it ("0 Paket"); a dash takes no unit: the word is left out. */
export function valueWithUnit(value: number | null | undefined, unit: string, format: (value: number) => string = number => number.toLocaleString('tr-TR')): string {
  return typeof value === 'number' && Number.isFinite(value) ? `${format(value)} ${unit}` : noValue
}
