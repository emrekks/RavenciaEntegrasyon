import { useEffect, useEffectEvent, useRef, useState, type ReactNode } from 'react'
import { lockPageScroll } from './hooks/useScrollLock'
import { createPortal } from 'react-dom'
import { UiIcon } from './components'
import { useDismissable } from './hooks/useDismissable'
import { dateRangePresets, formatDateRange, selectDateRangeDay, type DateRange } from './date-range'
import { istanbulDateKey } from './format-date'

function dateKey(date: Date) { return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}` }
function monthStart(value: string) { const date = value ? new Date(`${value}T12:00:00`) : new Date(); return new Date(date.getFullYear(), date.getMonth(), 1) }
// The two calendars end at this month at the latest, so the second one is never a month that is all in the future.
function firstShownMonth(value: string, future = false) { const start = monthStart(value); if (future) return start; const now = monthStart(istanbulDateKey()); const latest = new Date(now.getFullYear(), now.getMonth() - 1, 1); return start > latest ? latest : start }

// placeholder, icon and chevron let a page draw the trigger in its own style (the returns page uses the proposal's icons).
// future: the days to come can be picked too (a tariff's validity), the calendars start at the chosen month and the
// ready ranges (all past) are left out. disabled: the trigger does not open.
export function DateRangePicker({ from, to, onChange, label = 'Tarih Aralığı', className, placeholder, icon, chevron, future = false, disabled = false }: { from: string; to: string; onChange: (range: DateRange) => void; label?: string; className?: string; placeholder?: string; icon?: ReactNode; chevron?: ReactNode; future?: boolean; disabled?: boolean }) {
  const [open, setOpen] = useState(false)
  const [month, setMonth] = useState(() => firstShownMonth(from, future))
  const [popoverPosition, setPopoverPosition] = useState<{ left: number; top: number; maxHeight: number; width?: number } | null>(null)
  const root = useRef<HTMLDivElement>(null)
  const popover = useRef<HTMLElement>(null)
  // Today is Istanbul's: the filters read their days as Istanbul days, whatever time zone the device is set to.
  const today = istanbulDateKey()
  const presetRanges = dateRangePresets(today)
  function computePosition() {
    const bounds = root.current?.getBoundingClientRect()
    if (!bounds) return null
    {
      const viewportWidth = window.innerWidth || 1024
      const viewportHeight = window.innerHeight || 768
      // A phone opens it exactly as wide as its field, left edges aligned (owner's rule, 8 Nov 2026); never under 280px.
      const phone = viewportWidth <= 760
      const width = phone ? Math.min(Math.max(bounds.width, 280), viewportWidth - 24) : Math.min(500, viewportWidth - 32)
      const left = phone ? Math.max(12, Math.min(bounds.left, viewportWidth - width - 12)) : Math.max(16, Math.min(bounds.left, viewportWidth - width - 16))
      // A phone stacks the presets over the calendar (about 600px): it takes the room it has and scrolls the rest.
      const desiredHeight = Math.min(viewportWidth <= 760 ? 640 : 330, viewportHeight - 32)
      // In a phone filter sheet it stops above the sheet's sticky Temizle / Filtrele bar instead of covering it.
      const sheetBar = phone ? root.current?.closest('.rr-filters')?.querySelector<HTMLElement>(':scope > .rr-fbtns') : null
      const barTop = sheetBar?.getBoundingClientRect().top
      const bottomEdge = barTop && barTop > bounds.bottom ? Math.min(viewportHeight - 16, barTop - 8) : viewportHeight - 16
      const spaceBelow = bottomEdge - bounds.bottom
      const spaceAbove = bounds.top - 16
      const openAbove = spaceBelow < desiredHeight && spaceAbove > spaceBelow
      const top = openAbove
        ? Math.max(16, bounds.top - desiredHeight - 7)
        : bounds.bottom + 7
      const availableHeight = openAbove ? spaceAbove - 7 : bottomEdge - top
      return { left, top, maxHeight: Math.max(220, Math.min(desiredHeight, availableHeight)), ...(phone ? { width } : {}) }
    }
  }
  function toggleOpen() {
    if (open) { setOpen(false); return }
    const position = computePosition()
    if (position) setPopoverPosition(position)
    setActiveDay(null)
    setOpen(true)
  }
  const showSelectedMonth = useEffectEvent(() => setMonth(firstShownMonth(from, future)))
  // On a phone the open calendar is a layer over the page like a window: the page holds still and the bottom bar steps away, so the calendar can use the whole screen under its field.
  useEffect(() => (open && (window.innerWidth || 1024) <= 760 ? lockPageScroll() : undefined), [open])
  useEffect(() => {
    if (!open) return
    showSelectedMonth()
    // The popover is fixed to the viewport, so it follows its field whenever the page scrolls or resizes.
    let frame = 0
    // Only a real move re-renders the picker: every scroll frame used to set a new (equal) position and draw it again.
    const follow = () => { cancelAnimationFrame(frame); frame = requestAnimationFrame(() => { const position = computePosition(); if (position) setPopoverPosition(current => current && current.left === position.left && current.top === position.top && current.maxHeight === position.maxHeight && current.width === position.width ? current : position) }) }
    window.addEventListener('scroll', follow, true)
    window.addEventListener('resize', follow)
    return () => { cancelAnimationFrame(frame); window.removeEventListener('scroll', follow, true); window.removeEventListener('resize', follow) }
  }, [open])
  useDismissable(open, [root, popover], () => setOpen(false))
  // Keyboard: on opening, focus goes to the chosen day (else the first ready range, else the first day); the arrow keys
  // move a day or a week, Home/End to the start/end of the week, turning the month when needed; Tab stays inside.
  const [activeDay, setActiveDay] = useState<string | null>(null)
  useEffect(() => {
    if (!open) return
    const surface = popover.current
    const first = surface?.querySelector<HTMLElement>('[role="gridcell"][aria-selected="true"]:not(:disabled)')
      ?? surface?.querySelector<HTMLElement>('.date-range-picker-presets button, [role="gridcell"]:not(:disabled)')
    first?.focus({ preventScroll: true })
  }, [open])
  useEffect(() => {
    if (open && activeDay) popover.current?.querySelector<HTMLElement>(`[data-day="${activeDay}"]`)?.focus({ preventScroll: true })
  }, [open, activeDay, month])
  const secondMonth = new Date(month.getFullYear(), month.getMonth() + 1, 1)
  const visibleMonthKeys = [dateKey(month).slice(0, 7), dateKey(secondMonth).slice(0, 7)]
  const dayEnabled = (value: string) => future || value <= today
  const dayVisible = (value: string) => visibleMonthKeys.includes(value.slice(0, 7))
  // One day of the two calendars is in the Tab order (roving tabindex): the one moved to, else the start of the range,
  // else the first day that can be picked.
  const firstEnabledDay = [month, secondMonth].flatMap(visible => Array.from({ length: new Date(visible.getFullYear(), visible.getMonth() + 1, 0).getDate() }, (_, index) => dateKey(new Date(visible.getFullYear(), visible.getMonth(), index + 1)))).find(dayEnabled)
  const tabbableDay = [activeDay, from, to].find((value): value is string => !!value && dayVisible(value) && dayEnabled(value)) ?? firstEnabledDay
  function moveDay(event: KeyboardEvent) {
    const current = (event.target as HTMLElement).closest<HTMLElement>('[data-day]')?.dataset.day
    // W4: from the ready ranges, ArrowDown or ArrowRight goes into the calendar: to today, else the day in the Tab order.
    if (!current && (event.key === 'ArrowDown' || event.key === 'ArrowRight') && (event.target as HTMLElement).closest('.date-range-picker-presets')) {
      const target = dayVisible(today) && dayEnabled(today) ? today : tabbableDay
      if (!target) return
      event.preventDefault()
      setActiveDay(target)
      popover.current?.querySelector<HTMLElement>(`[data-day="${target}"]`)?.focus({ preventScroll: true })
      return
    }
    if (!current) return
    const date = new Date(`${current}T12:00:00`)
    const weekday = (date.getDay() + 6) % 7
    const shift = ({ ArrowLeft: -1, ArrowRight: 1, ArrowUp: -7, ArrowDown: 7, Home: -weekday, End: 6 - weekday } as Record<string, number>)[event.key]
    if (shift === undefined) return
    event.preventDefault()
    const target = dateKey(new Date(date.getFullYear(), date.getMonth(), date.getDate() + shift))
    if (!dayEnabled(target)) return
    if (target.slice(0, 7) < visibleMonthKeys[0]) setMonth(monthStart(target))
    else if (target.slice(0, 7) > visibleMonthKeys[1]) { const next = monthStart(target); setMonth(new Date(next.getFullYear(), next.getMonth() - 1, 1)) }
    setActiveDay(target)
  }
  function keepTabInside(event: KeyboardEvent) {
    if (event.key !== 'Tab') return
    const focusable = Array.from(popover.current?.querySelectorAll<HTMLElement>('button:not(:disabled)') ?? []).filter(element => element.tabIndex >= 0)
    if (!focusable.length) return
    const first = focusable[0]
    const last = focusable[focusable.length - 1]
    if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus() }
    else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus() }
  }
  const onPopoverKey = useEffectEvent((event: KeyboardEvent) => { keepTabInside(event); moveDay(event) })
  useEffect(() => {
    const surface = open ? popover.current : null
    if (!surface) return
    surface.addEventListener('keydown', onPopoverKey)
    return () => surface.removeEventListener('keydown', onPopoverKey)
  }, [open])
  const nextMonthBlocked = !future && new Date(month.getFullYear(), month.getMonth() + 2, 1) > monthStart(today)
  const renderMonth =(visibleMonth: Date) => {
    const monthStartWeekday = (visibleMonth.getDay() + 6) % 7
    const daysInMonth = new Date(visibleMonth.getFullYear(), visibleMonth.getMonth() + 1, 0).getDate()
    const cells = Array.from({ length: Math.ceil((monthStartWeekday + daysInMonth) / 7) * 7 }, (_, index) => index - monthStartWeekday + 1)
    const monthLabel = new Intl.DateTimeFormat('tr-TR', { month: 'long', year: 'numeric' }).format(visibleMonth)
    return <section className="date-range-picker-month" aria-label={monthLabel} key={`${visibleMonth.getFullYear()}-${visibleMonth.getMonth()}`}>
      <strong>{monthLabel}</strong>
      <div className="date-range-picker-grid" role="grid">{['Pt', 'Sa', 'Ça', 'Pe', 'Cu', 'Ct', 'Pa'].map(day => <span role="columnheader" key={day}>{day}</span>)}{cells.map((day, index) => day < 1 || day > daysInMonth ? <span key={`empty-${index}`} aria-hidden="true" /> : (() => { const date = new Date(visibleMonth.getFullYear(), visibleMonth.getMonth(), day); const value = dateKey(date); const selected = value === from || value === to; const inRange = Boolean(from && to && value > from && value < to); const later = !future && value > today; return <button type="button" role="gridcell" aria-label={new Intl.DateTimeFormat('tr-TR', { dateStyle: 'long' }).format(date)} aria-selected={selected} disabled={later} data-day={value} tabIndex={value === tabbableDay ? 0 : -1} className={`${selected ? 'is-selected' : ''}${inRange ? ' is-in-range' : ''}`} key={value} onClick={() => { const range = selectDateRangeDay({ from, to }, value); onChange(range); if (range.from && range.to) setOpen(false) }}>{day}</button> })())}</div>
    </section>
  }
  return <div className={`date-range-picker${className ? ` ${className}` : ''}`} ref={root}>
    <span className="date-range-picker-label">{label}</span>
    <button type="button" className="date-range-picker-trigger" aria-haspopup="dialog" aria-expanded={open} disabled={disabled} onClick={toggleOpen}>{icon ?? <UiIcon name="calendar" size={16} />}<span className={!from && !to && placeholder ? 'is-empty' : undefined}>{!from && !to && placeholder ? placeholder : formatDateRange({ from, to })}</span>{chevron ?? <UiIcon name="chevronDown" size={15} />}</button>
    {open && createPortal(<section ref={popover} className="date-range-picker-popover" role="dialog" aria-label={`${label} seç`} style={popoverPosition ?? undefined}>
      <div className="date-range-picker-layout">
        {!future && <nav className="date-range-picker-presets" aria-label="Hazır tarih aralıkları">{presetRanges.map(preset => <button type="button" key={preset.label} onClick={() => { onChange(preset.range); setOpen(false) }}>{preset.label}</button>)}</nav>}
        <div className="date-range-picker-calendars">
          <header><button type="button" className="secondary" aria-label="Önceki ay" onClick={() => setMonth(value => new Date(value.getFullYear(), value.getMonth() - 1, 1))}><UiIcon name="chevronLeft" /></button><button type="button" className="secondary" aria-label="Sonraki ay" disabled={nextMonthBlocked} onClick={() => setMonth(value => new Date(value.getFullYear(), value.getMonth() + 1, 1))}><UiIcon name="chevronRight" /></button></header>
          <div className="date-range-picker-months">{renderMonth(month)}{renderMonth(new Date(month.getFullYear(), month.getMonth() + 1, 1))}</div>
          <footer><small>{from && !to ? 'Bitiş gününü seçin' : 'Başlangıç ve bitiş gününü seçin'}</small><button type="button" className="secondary" onClick={() => onChange({ from: '', to: '' })}>Temizle</button></footer>
        </div>
      </div>
    </section>, document.body)}
  </div>
}
