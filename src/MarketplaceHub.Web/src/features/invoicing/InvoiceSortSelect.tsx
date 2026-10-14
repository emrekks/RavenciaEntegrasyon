import { useEffect, useId, useRef, useState } from 'react'
import { UiIcon } from '../../shared/components'
import './invoice-sort-select.css'

type SortOption = { value: string; label: string; direction: 'up' | 'down' }

// Order dates come first; the arrow shows the direction (down: newest or highest first, up: oldest or lowest first).
export const invoiceSortOptions: SortOption[] = [
  { value: 'ORDER_DESC', label: 'Sipariş Tarihi', direction: 'down' },
  { value: 'ORDER_ASC', label: 'Sipariş Tarihi', direction: 'up' },
  { value: 'DELIVERY_DESC', label: 'Kargo Tarihi', direction: 'down' },
  { value: 'DELIVERY_ASC', label: 'Kargo Tarihi', direction: 'up' },
  { value: 'DUE_ASC', label: 'Fatura Son Tarihi', direction: 'up' },
  { value: 'AMOUNT_DESC', label: 'Tutar', direction: 'down' },
  { value: 'AMOUNT_ASC', label: 'Tutar', direction: 'up' }
]

function DirectionIcon({ direction }: { direction: 'up' | 'down' }) {
  return <svg className="invoice-sort-arrow" viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" focusable="false">
    {direction === 'down' ? <path d="M12 5v14m-6-6 6 6 6-6" /> : <path d="M12 19V5m-6 6 6-6 6 6" />}
  </svg>
}

export function InvoiceSortSelect({ value, onChange }: { value: string; onChange: (value: string) => void }) {
  const [open, setOpen] = useState(false)
  const rootRef = useRef<HTMLDivElement>(null)
  const listId = useId()
  const current = invoiceSortOptions.find(option => option.value === value) ?? invoiceSortOptions[0]
  useEffect(() => {
    if (!open) return
    const close = (event: MouseEvent) => { if (!rootRef.current?.contains(event.target as Node)) setOpen(false) }
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') setOpen(false) }
    document.addEventListener('mousedown', close)
    document.addEventListener('keydown', escape)
    return () => { document.removeEventListener('mousedown', close); document.removeEventListener('keydown', escape) }
  }, [open])
  return <div className="invoice-reference-sort" ref={rootRef}>
    <span className="invoice-sort-caption" id={`${listId}-label`}>Sıralama</span>
    <button type="button" className="invoice-sort-trigger" aria-haspopup="listbox" aria-expanded={open} aria-labelledby={`${listId}-label ${listId}-value`} onClick={() => setOpen(item => !item)}>
      {/* every label is laid out invisibly so the control is as wide as the longest one */}
      <span className="invoice-sort-sizer" aria-hidden="true">{invoiceSortOptions.map(option => <span key={option.value}>{option.label}</span>)}</span>
      <span className="invoice-sort-current" id={`${listId}-value`}>{current.label}</span>
      <span className="invoice-sort-icons"><DirectionIcon direction={current.direction} /><UiIcon name="chevronDown" size={16} /></span>
    </button>
    {open && <ul className="invoice-sort-list" role="listbox" id={listId} aria-labelledby={`${listId}-label`}>
      {invoiceSortOptions.map(option => <li key={option.value} role="option" aria-selected={option.value === value}>
        <button type="button" className={option.value === value ? 'is-selected' : undefined} onClick={() => { onChange(option.value); setOpen(false) }}>
          <span>{option.label}</span><DirectionIcon direction={option.direction} />
        </button>
      </li>)}
    </ul>}
  </div>
}
