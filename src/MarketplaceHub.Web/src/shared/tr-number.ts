// A number as a person types it in Turkish, read the one way it can mean, or null when it could mean two things or is
// not a number at all; never a guess.
// - "1.250" -> 1250: points between groups of three digits are thousands points ("1.250.000" -> 1250000).
// - "1.250,50" / "1250,5" -> 1250.5: the comma is the decimal mark.
// - "1250.50" / "12.5" -> 1250.5 / 12.5: a single point with one or two digits after it can only be a decimal point.
// - Spaces (no-break ones too: \s covers them), "₺" and "%" are ignored; a leading minus ("-" or "−") makes it negative.
// - Refused: "0.125" (a thousands point after a leading 0), "1,250.50" (English grouping), "1.25.0", "1,2,3", "12a".
export function parseTrNumber(raw: string): number | null {
  const text = raw.replace(/[\s₺%]/g, '').replace(/^[−–]/, '-')
  const negative = text.startsWith('-')
  const body = negative ? text.slice(1) : text
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

// A whole number typed in Turkish ("1.250" -> 1250), or why it is not one: empty, unreadable, a fraction or below zero.
export function parseTrCount(raw: string): { ok: true; value: number } | { ok: false; reason: 'empty' | 'invalid' | 'fraction' | 'negative' } {
  if (raw.trim() === '') return { ok: false, reason: 'empty' }
  const value = parseTrNumber(raw)
  if (value === null) return { ok: false, reason: 'invalid' }
  if (!Number.isInteger(value)) return { ok: false, reason: 'fraction' }
  if (value < 0) return { ok: false, reason: 'negative' }
  return { ok: true, value }
}

// A stored number as a Turkish field starts it: a decimal comma and no thousands points ("1250,5"), so reading it back
// with parseTrNumber gives the same number; empty for none.
export function trNumberInput(value: number | null | undefined): string {
  return value == null || !Number.isFinite(value) ? '' : String(value).replace('.', ',')
}
