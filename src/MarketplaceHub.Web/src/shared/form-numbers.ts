import { parseTrCount, parseTrMeasure } from './tr-number'

// The number boxes of the order, shipment and return windows that reach a write (a cargo label, a Hepsiburada package,
// a return disposition, a bulk safety stock) are text boxes read in Turkish, never type="number": a tr-TR browser gave
// "1.250" typed in a number box as 1.25 and "1e3" as 1000 (measured in Chromium). Each reader gives the number, or the
// sentence saying why it was refused; the caller sends nothing on a refusal.
export type FormNumber = { ok: true; value: number } | { ok: false; message: string }
const text = (value: FormDataEntryValue | string | null | undefined) => typeof value === 'string' ? value : ''

// A whole number of pieces within [min, max] ("1.000" is 1000; "2,5", "1e3" and "0x10" are refused).
export function readFormCount(value: FormDataEntryValue | string | null | undefined, label: string, range: { min: number; max?: number }): FormNumber {
  const count = parseTrCount(text(value))
  if (!count.ok) return { ok: false, message: `${label} okunamadı; 1.250 gibi bir tam sayı girin.` }
  if (count.value < range.min) return { ok: false, message: `${label} en az ${range.min} olmalıdır.` }
  if (range.max != null && count.value > range.max) return { ok: false, message: `${label} en çok ${range.max} olabilir.` }
  return { ok: true, value: count.value }
}

// A measurement (desi, volume height) within its range: "1.250" is 1250, "0,5" a half; "1.25" (a point used as a decimal
// point) is refused, never read as 1.25 or 125. above: the value must be greater than it (a height of 0 is no height).
export function readFormMeasure(value: FormDataEntryValue | string | null | undefined, label: string, range: { min?: number; above?: number; max: number }): FormNumber {
  const raw = text(value)
  const measure = raw.trim() === '' ? null : parseTrMeasure(raw)
  if (measure == null) return { ok: false, message: `${label} okunamadı; ondalık için virgül kullanın (ör. 1,5).` }
  if (range.above != null && measure <= range.above) return { ok: false, message: `${label} ${range.above}'dan büyük olmalıdır.` }
  if (range.min != null && measure < range.min) return { ok: false, message: `${label} en az ${range.min} olmalıdır.` }
  if (measure > range.max) return { ok: false, message: `${label} en çok ${range.max.toLocaleString('tr-TR')} olabilir.` }
  return { ok: true, value: measure }
}
