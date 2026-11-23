// A number as a person types it in Turkish, read the one way it can mean, or null when it could mean two things or is
// not a number at all; never a guess.
// - "1.250" -> 1250: points between groups of three digits are thousands points ("1.250.000" -> 1250000).
// - "1.250,50" / "1250,5" -> 1250.5: the comma is the decimal mark.
// - "1250.50" / "12.5" -> 1250.5 / 12.5: a single point with one or two digits after it can only be a decimal point.
// - Whitespace (no-break ones too) is allowed only around the number; here (the commission tariff filters) with a
//   leading "₺" or "%" or a trailing "₺", "TL" or "%"; a leading minus ("-" or "−") makes it negative. Anything else
//   inside the number (a space: "1, 250", "1 250"; a sign or letter: "1₺250", "12a") is refused, never dropped (Y1,
//   review 23 Nov: "1, 250" read as 1.25).
// - Refused: "0.125" (a thousands point after a leading 0), "1,250.50" (English grouping), "1.25.0", "1,2,3", "12a",
//   and more than 15 digits (a double would round them silently).
export function parseTrNumber(raw: string): number | null {
  const token = trToken(raw, 'any')
  return token === null ? null : readBody(token)
}

type Token = { negative: boolean; body: string }

function readBody({ negative, body }: Token): number | null {
  if (body.replace(/\D/g, '').length > 15) return null
  const grouped = '[1-9]\\d{0,2}(?:\\.\\d{3})+'
  let plain: string | null = null
  if (/^\d+$/.test(body)) plain = body
  else if (new RegExp(`^${grouped}$`).test(body)) plain = body.replace(/\./g, '')
  else if (new RegExp(`^(?:${grouped}|\\d+),\\d+$`).test(body)) plain = body.replace(/\./g, '').replace(',', '.')
  else if (/^\d+\.\d{1,2}$/.test(body)) plain = body
  if (plain === null) return null
  const value = Number(plain)
  if (!Number.isFinite(value)) return null
  return negative ? -value : value
}

// The units a box allows around the number (review 23 Nov): a price one leading or trailing "₺" or a trailing "TL"
// (never "%": "%10" is no price); a rate one "%"; a stock or a measure none ("5 TL" is no stock); the commission tariff
// filters any of them.
type TrUnits = 'money' | 'percent' | 'none' | 'any'
const UNIT_PATTERNS: Record<TrUnits, readonly [RegExp, RegExp] | null> = {
  money: [/^₺\s*/, /\s*(₺|TL)$/i],
  percent: [/^%\s*/, /\s*%$/],
  none: null,
  any: [/^[₺%]\s*/, /\s*(₺|TL|%)$/i]
}

// The number's own characters, or null when anything but digits, points and commas is left inside it once the
// surrounding whitespace, the unit the box allows and a leading minus are taken off.
function trToken(raw: string, units: TrUnits): Token | null {
  const patterns = UNIT_PATTERNS[units]
  const trimmed = raw.trim()
  const text = patterns ? trimmed.replace(patterns[0], '').replace(patterns[1], '') : trimmed
  const match = /^([-−–]?)([\d.,]+)$/.exec(text)
  return match ? { negative: match[1] !== '', body: match[2] } : null
}

// A measurement typed in Turkish (desi, kg, cm): a point only groups thousands and a comma is the only decimal mark, any
// number of decimals ("0,125" kg). A point used as a decimal point ("1.25", "12.5") could mean two things: null. No unit.
export function parseTrMeasure(raw: string): number | null {
  return readMeasure(trToken(raw, 'none'))
}
function readMeasure(token: Token | null): number | null {
  if (token === null || /^\d+\.\d{1,2}$/.test(token.body)) return null
  return readBody(token)
}

// The one rule of every price and stock box a person types into (the product screens' quick edit, Toplu Düzenle, the
// product editor, the commission tariff's own price): a point only groups thousands and a comma is the only decimal
// mark, at most two decimals, so the number read is the number the box shows formatted (1.250 -> 1.250,00) and sent.
// - "1.250" -> 1250, "1.250,50" -> 1250.5, "1250,5" -> 1250.5, "12,5" -> 12.5, "1250" -> 1250.
// - Refused (null), never guessed: a point used as a decimal point ("1.25", "12.5", "1250.50"), a comma with three or
//   more digits after it ("1,250", "1,000": a thousands comma or three decimals?), English grouping ("1,250.50") and
//   everything parseTrNumber refuses. parseTrNumber's single-point reading stays for the commission tariff filters.
// - Whitespace (no-break and thin spaces too) is allowed only around the number, with one "₺" or "TL" (never "%");
//   a space inside it ("1 250", "1, 250", "1,25 0") is refused, so no check is ever made on text read differently
//   later (Y1: "1, 250" passed the three-digit check and read as 1.25).
// - A price or a stock is never below zero: a minus sign is refused too ("-5" is not read as -5).
// - Ceilings (review 23 Nov): a price at most 9.999.999,99, a stock at most 999.999; more is refused, never rounded.
export const MAX_TR_PRICE = 9_999_999.99
export const MAX_TR_COUNT = 999_999
export function parseTrEntry(raw: string): number | null {
  const value = readEntry(trToken(raw, 'money'))
  return value !== null && value <= MAX_TR_PRICE ? value : null
}
function readEntry(token: Token | null): number | null {
  if (token === null || token.negative || /,\d{3,}$/.test(token.body)) return null
  return readMeasure(token)
}

// A rate typed in Turkish (commission, target margin, VAT): the price rule with one "%" around it instead of a currency.
export function parseTrPercent(raw: string): number | null {
  return readEntry(trToken(raw, 'percent'))
}

// The message for a typed price parseTrEntry refuses. A phone keyboard set to Turkish may offer only a point: the
// message says the comma plainly (kuruş için virgül), and the ceiling.
export const trEntryHelp = 'Fiyat okunamadı; 1.250,50 biçiminde yazın (nokta yalnız binlik ayırıcıdır, kuruş için virgül ve en çok iki hane; en çok 9.999.999,99).'

// A whole number typed in Turkish ("1.250" -> 1250), read by the price rule without a unit, or why it is not one:
// empty, unreadable ("5 TL" too), a fraction, below zero or above MAX_TR_COUNT.
export function parseTrCount(raw: string): { ok: true; value: number } | { ok: false; reason: 'empty' | 'invalid' | 'fraction' | 'negative' | 'tooLarge' } {
  if (raw.trim() === '') return { ok: false, reason: 'empty' }
  const token = trToken(raw, 'none')
  const value = token === null ? null : readEntry({ negative: false, body: token.body })
  if (value === null) return { ok: false, reason: 'invalid' }
  if (!Number.isInteger(value)) return { ok: false, reason: 'fraction' }
  if (token!.negative && value !== 0) return { ok: false, reason: 'negative' }
  if (value > MAX_TR_COUNT) return { ok: false, reason: 'tooLarge' }
  return { ok: true, value }
}

// The message for a stock above MAX_TR_COUNT.
export const trCountTooLarge = 'Stok en çok 999.999 olabilir.'

// A stored number as a Turkish field starts it: a decimal comma and no thousands points ("1250,5"), so reading it back
// with parseTrNumber gives the same number; empty for none.
export function trNumberInput(value: number | null | undefined): string {
  return value == null || !Number.isFinite(value) ? '' : String(value).replace('.', ',')
}
