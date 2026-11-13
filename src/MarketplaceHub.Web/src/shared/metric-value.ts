// The one rule for what a summary card (or any figure that stands for a count or an amount) shows when there is nothing
// to show (owner, 14 Nov 2026), the same on every page:
//  - When the data was read and there is nothing (no order or record in the period, nothing to base it on), the figure is
//    zero in its own format: a count "0", money "₺0", a percentage "%0", a change pill "%0". Neutral ink, never a colour.
//  - "—" only for a real unknown: the read is still loading's "…", the request failed, or the figure cannot be worked out
//    while the data exists (a profit with the product costs missing keeps its "Maliyet Eksik" reason; it is not a 0).
//  - Never a word ("Veri yok", "Tutar yok", "Bilgi yok"); the reason, when it helps, is a tooltip or note.
//  - A warning, good or bad colour only marks a figure above nought.
export const noValue = '—'

type CountState = { loading?: boolean; error?: boolean }

/** A count: "…" while it is on its way, "—" when it could not be read, else the number ("0" included). */
export function countText(value: number | null | undefined, state: CountState = {}): string {
  if (state.error) return noValue
  if (state.loading) return '…'
  return typeof value === 'number' && Number.isFinite(value) ? value.toLocaleString('tr-TR') : noValue
}

/** A count of a list that has been read: a count the answer leaves out is a known zero, not an unknown. */
export function readCountText(value: number | null | undefined, state: CountState = {}): string {
  return countText(state.error || state.loading ? value : value ?? 0, state)
}

/** True when a figure deserves its semantic colour: a known number above nought. */
export const isAboveZero = (value: number | null | undefined) => typeof value === 'number' && value > 0

/** The tone a card may use for its figure: the tone itself above nought, else nothing (neutral ink). */
export function toneAbove<T extends string>(value: number | null | undefined, tone: T): T | undefined {
  return isAboveZero(value) ? tone : undefined
}
