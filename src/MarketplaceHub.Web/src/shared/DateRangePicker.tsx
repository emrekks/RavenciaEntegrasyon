import { useEffect, useRef, useState } from 'react'
import { UiIcon } from './components'
import { formatDateRange, selectDateRangeDay, type DateRange } from './date-range'

function dateKey(date: Date) { return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}` }
function monthStart(value: string) { const date = value ? new Date(`${value}T12:00:00`) : new Date(); return new Date(date.getFullYear(), date.getMonth(), 1) }

export function DateRangePicker({ from, to, onChange, label = 'Tarih aralığı' }: { from: string; to: string; onChange: (range: DateRange) => void; label?: string }) {
  const [open, setOpen] = useState(false)
  const [month, setMonth] = useState(() => monthStart(from))
  const root = useRef<HTMLDivElement>(null)
  useEffect(() => {
    if (!open) return
    const closeOnOutside = (event: PointerEvent) => { if (!root.current?.contains(event.target as Node)) setOpen(false) }
    const closeOnEscape = (event: KeyboardEvent) => { if (event.key === 'Escape') setOpen(false) }
    document.addEventListener('pointerdown', closeOnOutside)
    document.addEventListener('keydown', closeOnEscape)
    return () => { document.removeEventListener('pointerdown', closeOnOutside); document.removeEventListener('keydown', closeOnEscape) }
  }, [open])
  const firstWeekday = (month.getDay() + 6) % 7
  const daysInMonth = new Date(month.getFullYear(), month.getMonth() + 1, 0).getDate()
  const cells = Array.from({ length: Math.ceil((firstWeekday + daysInMonth) / 7) * 7 }, (_, index) => index - firstWeekday + 1)
  const monthLabel = new Intl.DateTimeFormat('tr-TR', { month: 'long', year: 'numeric' }).format(month)
  return <div className="date-range-picker" ref={root}>
    <span className="date-range-picker-label">{label}</span>
    <button type="button" className="date-range-picker-trigger" aria-haspopup="dialog" aria-expanded={open} onClick={() => setOpen(value => !value)}><UiIcon name="calendar" size={16} /><span>{formatDateRange({ from, to })}</span><UiIcon name="chevronDown" size={15} /></button>
    {open && <section className="date-range-picker-popover" role="dialog" aria-label={`${label} seç`}>
      <header><button type="button" className="secondary" aria-label="Önceki ay" onClick={() => setMonth(value => new Date(value.getFullYear(), value.getMonth() - 1, 1))}><UiIcon name="chevronLeft" /></button><strong>{monthLabel}</strong><button type="button" className="secondary" aria-label="Sonraki ay" onClick={() => setMonth(value => new Date(value.getFullYear(), value.getMonth() + 1, 1))}><UiIcon name="chevronRight" /></button></header>
      <div className="date-range-picker-grid" role="grid">{['Pt', 'Sa', 'Ça', 'Pe', 'Cu', 'Ct', 'Pa'].map(day => <span role="columnheader" key={day}>{day}</span>)}{cells.map((day, index) => day < 1 || day > daysInMonth ? <span key={`empty-${index}`} aria-hidden="true" /> : (() => { const date = new Date(month.getFullYear(), month.getMonth(), day); const value = dateKey(date); const selected = value === from || value === to; const inRange = Boolean(from && to && value > from && value < to); return <button type="button" role="gridcell" aria-selected={selected} className={`${selected ? 'is-selected' : ''}${inRange ? ' is-in-range' : ''}`} key={value} onClick={() => { const range = selectDateRangeDay({ from, to }, value); onChange(range); if (range.from && range.to) setOpen(false) }}>{day}</button> })())}</div>
      <footer><small>{from && !to ? 'Bitiş gününü seçin' : 'Başlangıç ve bitiş gününü seçin'}</small><button type="button" className="secondary" onClick={() => onChange({ from: '', to: '' })}>Temizle</button></footer>
    </section>}
  </div>
}
