import { useEffect, useEffectEvent, useRef, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { UiIcon } from './components'
import { useDismissable } from './hooks/useDismissable'
import { formatDateRange, selectDateRangeDay, type DateRange } from './date-range'

function dateKey(date: Date) { return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}` }
function monthStart(value: string) { const date = value ? new Date(`${value}T12:00:00`) : new Date(); return new Date(date.getFullYear(), date.getMonth(), 1) }
// The two calendars end at this month at the latest, so the second one is never a month that is all in the future.
function firstShownMonth(value: string) { const start = monthStart(value); const now = new Date(); const latest = new Date(now.getFullYear(), now.getMonth() - 1, 1); return start > latest ? latest : start }

// placeholder, icon and chevron let a page draw the trigger in its own style (the returns page uses the proposal's icons).
export function DateRangePicker({ from, to, onChange, label = 'Tarih Aralığı', className, placeholder, icon, chevron }: { from: string; to: string; onChange: (range: DateRange) => void; label?: string; className?: string; placeholder?: string; icon?: ReactNode; chevron?: ReactNode }) {
  const [open, setOpen] = useState(false)
  const [month, setMonth] = useState(() => firstShownMonth(from))
  const [popoverPosition, setPopoverPosition] = useState<{ left: number; top: number; maxHeight: number } | null>(null)
  const root = useRef<HTMLDivElement>(null)
  const popover = useRef<HTMLElement>(null)
  const today = dateKey(new Date())
  const dateOffset = (days: number) => { const date = new Date(); date.setDate(date.getDate() + days); return dateKey(date) }
  const presetRanges: Array<{ label: string; range: DateRange }> = [
    { label: 'Bugün', range: { from: today, to: today } },
    { label: 'Dün', range: { from: dateOffset(-1), to: dateOffset(-1) } },
    { label: 'Geçen Hafta', range: (() => { const end = new Date(); const weekday = (end.getDay() + 6) % 7; end.setDate(end.getDate() - weekday - 1); const start = new Date(end); start.setDate(start.getDate() - 6); return { from: dateKey(start), to: dateKey(end) } })() },
    { label: 'Geçen Ay', range: (() => { const start = new Date(); start.setDate(1); start.setMonth(start.getMonth() - 1); const end = new Date(start.getFullYear(), start.getMonth() + 1, 0); return { from: dateKey(start), to: dateKey(end) } })() },
    { label: 'Bu Ay', range: (() => { const now = new Date(); return { from: dateKey(new Date(now.getFullYear(), now.getMonth(), 1)), to: today } })() },
    { label: 'Bu Sene', range: { from: `${new Date().getFullYear()}-01-01`, to: today } },
  ]
  function computePosition() {
    const bounds = root.current?.getBoundingClientRect()
    if (!bounds) return null
    {
      const viewportWidth = window.innerWidth || 1024
      const viewportHeight = window.innerHeight || 768
      const width = Math.min(500, viewportWidth - 32)
      const left = Math.max(16, Math.min(bounds.left, viewportWidth - width - 16))
      const desiredHeight = Math.min(330, viewportHeight - 32)
      const spaceBelow = viewportHeight - bounds.bottom - 16
      const spaceAbove = bounds.top - 16
      const openAbove = spaceBelow < desiredHeight && spaceAbove > spaceBelow
      const top = openAbove
        ? Math.max(16, bounds.top - desiredHeight - 7)
        : bounds.bottom + 7
      const availableHeight = openAbove ? spaceAbove - 7 : viewportHeight - top - 16
      return { left, top, maxHeight: Math.max(220, Math.min(desiredHeight, availableHeight)) }
    }
  }
  function toggleOpen() {
    if (open) { setOpen(false); return }
    const position = computePosition()
    if (position) setPopoverPosition(position)
    setOpen(true)
  }
  const showSelectedMonth = useEffectEvent(() => setMonth(firstShownMonth(from)))
  useEffect(() => {
    if (!open) return
    showSelectedMonth()
    // The popover is fixed to the viewport, so it follows its field whenever the page scrolls or resizes.
    let frame = 0
    const follow = () => { cancelAnimationFrame(frame); frame = requestAnimationFrame(() => { const position = computePosition(); if (position) setPopoverPosition(position) }) }
    window.addEventListener('scroll', follow, true)
    window.addEventListener('resize', follow)
    return () => { cancelAnimationFrame(frame); window.removeEventListener('scroll', follow, true); window.removeEventListener('resize', follow) }
  }, [open])
  useDismissable(open, [root, popover], () => setOpen(false))
  const nextMonthBlocked = new Date(month.getFullYear(), month.getMonth() + 2, 1) > monthStart(today)
  const renderMonth =(visibleMonth: Date) => {
    const monthStartWeekday = (visibleMonth.getDay() + 6) % 7
    const daysInMonth = new Date(visibleMonth.getFullYear(), visibleMonth.getMonth() + 1, 0).getDate()
    const cells = Array.from({ length: Math.ceil((monthStartWeekday + daysInMonth) / 7) * 7 }, (_, index) => index - monthStartWeekday + 1)
    const monthLabel = new Intl.DateTimeFormat('tr-TR', { month: 'long', year: 'numeric' }).format(visibleMonth)
    return <section className="date-range-picker-month" aria-label={monthLabel} key={`${visibleMonth.getFullYear()}-${visibleMonth.getMonth()}`}>
      <strong>{monthLabel}</strong>
      <div className="date-range-picker-grid" role="grid">{['Pt', 'Sa', 'Ça', 'Pe', 'Cu', 'Ct', 'Pa'].map(day => <span role="columnheader" key={day}>{day}</span>)}{cells.map((day, index) => day < 1 || day > daysInMonth ? <span key={`empty-${index}`} aria-hidden="true" /> : (() => { const date = new Date(visibleMonth.getFullYear(), visibleMonth.getMonth(), day); const value = dateKey(date); const selected = value === from || value === to; const inRange = Boolean(from && to && value > from && value < to); const future = value > today; return <button type="button" role="gridcell" aria-label={new Intl.DateTimeFormat('tr-TR', { dateStyle: 'long' }).format(date)} aria-selected={selected} disabled={future} className={`${selected ? 'is-selected' : ''}${inRange ? ' is-in-range' : ''}`} key={value} onClick={() => { const range = selectDateRangeDay({ from, to }, value); onChange(range); if (range.from && range.to) setOpen(false) }}>{day}</button> })())}</div>
    </section>
  }
  return <div className={`date-range-picker${className ? ` ${className}` : ''}`} ref={root}>
    <span className="date-range-picker-label">{label}</span>
    <button type="button" className="date-range-picker-trigger" aria-haspopup="dialog" aria-expanded={open} onClick={toggleOpen}>{icon ?? <UiIcon name="calendar" size={16} />}<span className={!from && !to && placeholder ? 'is-empty' : undefined}>{!from && !to && placeholder ? placeholder : formatDateRange({ from, to })}</span>{chevron ?? <UiIcon name="chevronDown" size={15} />}</button>
    {open && createPortal(<section ref={popover} className="date-range-picker-popover" role="dialog" aria-label={`${label} seç`} style={popoverPosition ?? undefined}>
      <div className="date-range-picker-layout">
        <nav className="date-range-picker-presets" aria-label="Hazır tarih aralıkları">{presetRanges.map(preset => <button type="button" key={preset.label} onClick={() => { onChange(preset.range); setOpen(false) }}>{preset.label}</button>)}</nav>
        <div className="date-range-picker-calendars">
          <header><button type="button" className="secondary" aria-label="Önceki ay" onClick={() => setMonth(value => new Date(value.getFullYear(), value.getMonth() - 1, 1))}><UiIcon name="chevronLeft" /></button><button type="button" className="secondary" aria-label="Sonraki ay" disabled={nextMonthBlocked} onClick={() => setMonth(value => new Date(value.getFullYear(), value.getMonth() + 1, 1))}><UiIcon name="chevronRight" /></button></header>
          <div className="date-range-picker-months">{renderMonth(month)}{renderMonth(new Date(month.getFullYear(), month.getMonth() + 1, 1))}</div>
          <footer><small>{from && !to ? 'Bitiş gününü seçin' : 'Başlangıç ve bitiş gününü seçin'}</small><button type="button" className="secondary" onClick={() => onChange({ from: '', to: '' })}>Temizle</button></footer>
        </div>
      </div>
    </section>, document.body)}
  </div>
}
