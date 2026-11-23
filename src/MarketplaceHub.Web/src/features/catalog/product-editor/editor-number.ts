import { parseTrCount, parseTrMeasure } from '../../../shared/tr-number'

// The product editor's readers of a field's text (B1, 23 Nov 2026). Every number box of the editor is a text box holding
// Turkish text: a type="number" box gave "1.250" typed in a tr-TR browser as 1.25 and "1.250,50" as 1.2505 (measured in
// Chromium, tr-TR and en-US alike), so none is left for a price, a stock, a cost or a desi. Loaded values are written as
// Turkish text (priceFieldText / trNumberInput), so an untouched field reads back unchanged.
// - parseEditorNumber: a measurement or a VAT rate (desi, kg, cm, a category attribute): "1.250" is 1250, "0,125" is
//   0.125; a point used as a decimal point ("1.25") or anything else not a number is NaN. Empty is 0.
// - Prices and costs use priceInputNumber (card-prices.ts: at most two decimals, "1,250" refused); counts editorCount.
export function parseEditorNumber(value: string | number): number {
  if (typeof value === 'number') return value
  const text = value.trim()
  if (text === '') return 0
  return parseTrMeasure(text) ?? Number.NaN
}

// A stock or safety stock as typed: a Turkish whole number ("1.000" is 1000); a fraction, "1e3", "0x10" or a negative is
// NaN, which the editor's checks refuse. Empty is 0.
export function editorCount(value: string | number): number {
  if (typeof value === 'number') return value
  if (value.trim() === '') return 0
  const count = parseTrCount(value)
  return count.ok ? count.value : Number.NaN
}

export type Measures = { weight: string; width: string; length: string; height: string }
export const MEASURE_FIELDS: ReadonlyArray<{ key: keyof Measures; label: string }> = [
  { key: 'weight', label: 'Ağırlık (kg)' }, { key: 'width', label: 'En (cm)' }, { key: 'length', label: 'Boy (cm)' }, { key: 'height', label: 'Yükseklik (cm)' }
]
// A measure the Turkish rule cannot read ("12.5": a point used as a decimal point) is never taken as 0 or guessed.
export const unreadableMeasureMessage = 'Ölçü okunamadı; 12,5 biçiminde yazın (ondalık için virgül).'

// The calculator's measures (review 23 Nov, ORTA-3): typed here, handed to the form only with Uygula, so İptal takes back
// what was typed. An unreadable measure is marked and keeps Uygula off; the desi is never saved as a silent 1.
export function calculatorDesi(measures: Measures): { desi: number; unreadable: Array<keyof Measures> } {
  const unreadable = MEASURE_FIELDS.map(field => field.key).filter(key => measures[key].trim() !== '' && Number.isNaN(parseEditorNumber(measures[key])))
  const [width, length, height] = [measures.width, measures.length, measures.height].map(parseEditorNumber)
  return { desi: !unreadable.length && width > 0 && length > 0 && height > 0 ? width * length * height / 3000 : 0, unreadable }
}
