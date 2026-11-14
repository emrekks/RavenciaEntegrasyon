// The one rule for empty values on every page (owner, 14 Nov 2026):
//  - The big number of a summary card with no data - no records in the period or list, nothing to base it on, not loaded,
//    failed - reads "—" in the neutral ink, for money, a percentage and a count alike (never a word: "Veri yok", "Tutar yok").
//  - Everything around it reads zero in its own format when empty: sub-lines and notes ("Sipariş başına ₺0", "Reklam öncesi %0",
//    "Net 0 · 0 iade", "Paket | 0 Adet Ürün", "— Bekleyen | 0 Yaklaşan"), change pills ("%0"), tab, chip, badge and sidebar
//    counters ("0", "₺0", "%0"). A reason note ("Maliyet Eksik") stays as it is.
//  - A real non-zero value reads as it is; a warning, good or bad colour only marks such a value.
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

/** A main value with the unit word that belongs to it ("3 Paket"); a dash takes no unit ("—"): the word is left out. */
export function valueWithUnit(value: number | null | undefined, unit: string, format: (value: number) => string = number => number.toLocaleString('tr-TR')): string {
  return isAboveZero(value) ? `${format(value as number)} ${unit}` : noValue
}
