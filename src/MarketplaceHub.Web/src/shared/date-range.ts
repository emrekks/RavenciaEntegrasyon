export type DateRange = { from: string; to: string }

export function selectDateRangeDay(range: DateRange, selected: string): DateRange {
  if (!range.from || range.to || selected < range.from) return { from: selected, to: '' }
  return { from: range.from, to: selected }
}

export function formatDateRange(range: DateRange): string {
  const format = (value: string) => {
    if (!value) return ''
    const [year, month, day] = value.split('-')
    return `${day}.${month}.${year}`
  }
  if (!range.from && !range.to) return 'Tarih Aralığı Seçin'
  return `${format(range.from) || 'Başlangıç'} – ${format(range.to) || 'Bitiş'}`
}

/** A calendar day moved by whole days; plain date arithmetic, no time zone involved. */
function shiftDay(day: string, days: number) {
  const date = new Date(`${day}T00:00:00Z`)
  date.setUTCDate(date.getUTCDate() + days)
  return date.toISOString().slice(0, 10)
}

/**
 * The picker's ready ranges for `today`, an Istanbul calendar day (istanbulDateKey()). The filters read their days as
 * Istanbul days, so a device set to another time zone must not move "Bugün" or "Bu Ay" to its own calendar.
 */
export function dateRangePresets(today: string): Array<{ label: string; range: DateRange }> {
  const [year, month] = today.split('-')
  const weekday = (new Date(`${today}T00:00:00Z`).getUTCDay() + 6) % 7
  const lastSunday = shiftDay(today, -weekday - 1)
  const thisMonthStart = `${year}-${month}-01`
  const lastMonthEnd = shiftDay(thisMonthStart, -1)
  return [
    { label: 'Bugün', range: { from: today, to: today } },
    { label: 'Dün', range: { from: shiftDay(today, -1), to: shiftDay(today, -1) } },
    { label: 'Geçen Hafta', range: { from: shiftDay(lastSunday, -6), to: lastSunday } },
    { label: 'Geçen Ay', range: { from: `${lastMonthEnd.slice(0, 7)}-01`, to: lastMonthEnd } },
    { label: 'Bu Ay', range: { from: thisMonthStart, to: today } },
    { label: 'Bu Sene', range: { from: `${year}-01-01`, to: today } },
  ]
}
