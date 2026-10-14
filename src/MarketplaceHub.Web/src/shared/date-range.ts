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
  if (!range.from && !range.to) return 'Tarih aralığı seçin'
  return `${format(range.from) || 'Başlangıç'} – ${format(range.to) || 'Bitiş'}`
}
