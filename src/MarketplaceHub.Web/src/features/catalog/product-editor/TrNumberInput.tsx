import { useState, type InputHTMLAttributes } from 'react'
import { priceFieldText } from '../quick-price-edit'
import { trNumberInput } from '../../../shared/tr-number'
import { priceInputNumber } from './card-prices'
import { editorCount } from './editor-number'

// A number cell of the variant table (B1, 23 Nov 2026): a text box holding Turkish text, never type="number" (a tr-TR
// browser gave "1.250" typed there as 1.25). It keeps the text as typed while the row holds the number read from it by
// the editor's readers (the same ones updateVariantRow stores with); a number set from outside (a bulk value, Toplu
// Düzenle, a reload) is written back as Turkish text. Text the reader refuses is kept on screen, marked invalid, and the
// row holds NaN, which the editor's checks refuse on Kaydet.
export type TrNumberKind = 'price' | 'cost' | 'count'
const readers: Record<TrNumberKind, (text: string) => number | null> = {
  price: text => priceInputNumber(text),
  cost: text => text.trim() === '' ? null : priceInputNumber(text),
  count: text => editorCount(text),
}
const shown = (kind: TrNumberKind, value: number | null) => value == null || !Number.isFinite(value) ? '' : kind === 'count' ? trNumberInput(value) : value === 0 && kind === 'price' ? '0' : priceFieldText(value)

export function TrNumberInput({ kind, value, onText, ...props }: Omit<InputHTMLAttributes<HTMLInputElement>, 'value' | 'onChange' | 'type'> & { kind: TrNumberKind; value: number | null; onText: (text: string) => void }) {
  const [text, setText] = useState(() => shown(kind, value))
  const [mirrored, setMirrored] = useState<number | null>(value)
  if (!Object.is(value, mirrored)) { setMirrored(value); setText(shown(kind, value)) }
  // Unreadable only when the person typed it (mirrored holds NaN); a stored 99.999 or 2.5 shown as it is stays valid (O2).
  const invalid = mirrored != null && Number.isNaN(mirrored)
  return <input {...props} type="text" inputMode={kind === 'count' ? 'numeric' : 'decimal'} autoComplete="off" value={text} aria-invalid={invalid || undefined}
    title={invalid ? (kind === 'count' ? 'Stok okunamadı; 1.250 gibi bir tam sayı girin.' : 'Fiyat okunamadı; 1.250,50 biçiminde yazın (nokta yalnız binlik ayırıcıdır, ondalık için virgül ve en çok iki hane).') : props.title}
    onChange={event => { const next = event.target.value; setText(next); setMirrored(readers[kind](next)); onText(next) }} />
}
