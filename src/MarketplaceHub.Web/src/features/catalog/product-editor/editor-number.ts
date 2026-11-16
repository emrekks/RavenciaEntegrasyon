import { parseTrNumber } from '../../../shared/tr-number'

// The one point where the product editor turns a field's text into a number (WEB-SAYFALAR-10): card prices, channel and
// variant prices, cost, stock, desi measurements, VAT and safety stock.
// - Empty is 0 (an empty field sends what it did before: nothing typed is 0).
// - A plain number ("549.9", "12", "1e3") reads as JavaScript reads it. A number field (type="number") always gives its
//   value this way, and a single point is a decimal point there: "1.250" from such a field is 1.25, never 1250.
// - Anything else is Turkish text and goes to parseTrNumber: "549,90" is 549.9, "1.249,90" is 1249.9; text that is not a
//   number is NaN, as Number() gave it.
// - A whole number with a comma typed after it and nothing yet behind ("549,") is that number, as the card prices read it.
const plainNumber = /^[+-]?(\d+\.?\d*|\.\d+)(e[+-]?\d+)?$/i

export function parseEditorNumber(value: string | number): number {
  if (typeof value === 'number') return value
  const text = value.trim()
  if (text === '') return 0
  if (plainNumber.test(text)) return Number(text)
  if (/^[+-]?\d+,$/.test(text)) return Number(text.slice(0, -1))
  return parseTrNumber(text) ?? Number.NaN
}
