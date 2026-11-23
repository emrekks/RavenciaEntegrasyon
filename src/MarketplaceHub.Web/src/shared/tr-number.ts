// A number as a person types it in Turkish, read the one way it can mean, or null when it could mean two things or is
// not a number at all; never a guess.
// - "1.250" -> 1250: points between groups of three digits are thousands points ("1.250.000" -> 1250000).
// - "1.250,50" / "1250,5" -> 1250.5: the comma is the decimal mark.
// - "1250.50" / "12.5" -> 1250.5 / 12.5: a single point with one or two digits after it can only be a decimal point.
// - Whitespace (no-break ones too) is allowed only around the number, with a leading "₺" or "%" or a trailing "₺", "TL"
//   or "%"; a leading minus ("-" or "−") makes it negative. Anything else inside the number (a space: "1, 250", "1 250";
//   a sign or letter: "1₺250", "12a") is refused, never dropped (Y1, review 23 Nov: "1, 250" read as 1.25).
// - Refused: "0.125" (a thousands point after a leading 0), "1,250.50" (English grouping), "1.25.0", "1,2,3", "12a".
export function parseTrNumber(raw: string): number | null {
  const token = trToken(raw)
  if (token === null) return null
  const { negative, body } = token
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

// The number's own characters, or null when anything but digits, points and commas is left inside it once the
// surrounding whitespace, one currency or percent sign and a leading minus are taken off.
function trToken(raw: string): { negative: boolean; body: string } | null {
  const text = raw.trim().replace(/^[₺%]\s*/, '').replace(/\s*(₺|TL|%)$/i, '')
  const match = /^([-−–]?)([\d.,]+)$/.exec(text)
  return match ? { negative: match[1] !== '', body: match[2] } : null
}

// A measurement typed in Turkish (desi, kg, cm): a point only groups thousands and a comma is the only decimal mark, any
// number of decimals ("0,125" kg). A point used as a decimal point ("1.25", "12.5") could mean two things: null.
export function parseTrMeasure(raw: string): number | null {
  const token = trToken(raw)
  if (token === null || /^\d+\.\d{1,2}$/.test(token.body)) return null
  return parseTrNumber(raw)
}

// The one rule of every price and stock box a person types into (the product screens' quick edit, Toplu Düzenle, the
// product editor, the commission tariff's own price): a point only groups thousands and a comma is the only decimal
// mark, at most two decimals, so the number read is the number the box shows formatted (1.250 -> 1.250,00) and sent.
// - "1.250" -> 1250, "1.250,50" -> 1250.5, "1250,5" -> 1250.5, "12,5" -> 12.5, "1250" -> 1250.
// - Refused (null), never guessed: a point used as a decimal point ("1.25", "12.5", "1250.50"), a comma with three or
//   more digits after it ("1,250", "1,000": a thousands comma or three decimals?), English grouping ("1,250.50") and
//   everything parseTrNumber refuses. parseTrNumber's single-point reading stays for the commission tariff filters.
// - Whitespace (no-break and thin spaces too) is allowed only around the number, with a leading or trailing ₺, TL or %;
//   a space inside it ("1 250", "1, 250", "1,25 0") is refused, so no check is ever made on text parseTrNumber later
//   reads differently (Y1: "1, 250" passed the three-digit check and read as 1.25).
// - A price or a stock is never below zero: a minus sign is refused too ("-5" is not read as -5).
export function parseTrEntry(raw: string): number | null {
  const token = trToken(raw)
  if (token === null || token.negative || /,\d{3,}$/.test(token.body)) return null
  return parseTrMeasure(token.body)
}

// The message for a typed price parseTrEntry refuses.
export const trEntryHelp = 'Fiyat okunamadı; 1.250,50 biçiminde yazın (nokta yalnız binlik ayırıcıdır, ondalık için virgül ve en çok iki hane).'

// A whole number typed in Turkish ("1.250" -> 1250), read by parseTrEntry, or why it is not one: empty, unreadable,
// a fraction or below zero.
export function parseTrCount(raw: string): { ok: true; value: number } | { ok: false; reason: 'empty' | 'invalid' | 'fraction' | 'negative' } {
  if (raw.trim() === '') return { ok: false, reason: 'empty' }
  const token = trToken(raw)
  const value = token === null ? null : parseTrEntry(token.body)
  if (value === null) return { ok: false, reason: 'invalid' }
  if (!Number.isInteger(value)) return { ok: false, reason: 'fraction' }
  if (token!.negative && value !== 0) return { ok: false, reason: 'negative' }
  return { ok: true, value }
}

// A stored number as a Turkish field starts it: a decimal comma and no thousands points ("1250,5"), so reading it back
// with parseTrNumber gives the same number; empty for none.
export function trNumberInput(value: number | null | undefined): string {
  return value == null || !Number.isFinite(value) ? '' : String(value).replace('.', ',')
}
