import { attributeNumberHelp, attributeNumberUnreadable } from './editor-number'

// A typed category attribute value (D-1, scan 25 Nov 2026). A numeric one is a Turkish text box ("12,5"), never
// type="number" (a tr-TR browser turned "1,125" into 1125 and refused a decimal comma); text the reader refuses stays on
// screen, marked. Every other one is a plain text box.
export function AttributeTextValueInput({ numeric, value, onChange, ariaLabel, placeholder }: { numeric: boolean; value: string; onChange: (value: string) => void; ariaLabel?: string; placeholder: string }) {
  if (!numeric) return <input aria-label={ariaLabel} value={value} onChange={event => onChange(event.target.value)} type="text" placeholder={placeholder} />
  const unreadable = attributeNumberUnreadable(value)
  return <input aria-label={ariaLabel} value={value} onChange={event => onChange(event.target.value)} type="text" inputMode="decimal" autoComplete="off" aria-invalid={unreadable || undefined} title={unreadable ? `Sayı okunamadı; ${attributeNumberHelp}` : undefined} placeholder="Örn. 12,5" />
}
