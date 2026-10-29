// The performance panel's arithmetic, kept apart from the page so it can be tested. The server sends one row per
// Istanbul calendar day × store × currency for the chosen period and the previous one of equal elapsed time
// (GET /dashboard/performance); everything here only adds those rows up.
// The server's amount leaves returned orders out; the panel's revenue (ciro) is everything sold, returns included,
// with the returned part still shown apart.

export type PerformanceRow = { day: string; connectionId: string; currency: string; amount: number; orderCount: number; productQuantity: number; packageCount: number; returnedAmount: number; returnedOrderCount: number }
export type DashboardChannel = { connectionId: string; platformCode: string; displayName: string; status: string }
export type Performance = {
  from: string; to: string; previousFrom: string; previousTo: string
  currentStart: string; currentEnd: string; previousStart: string; previousEnd: string
  partialDay: boolean; channels: DashboardChannel[]; current: PerformanceRow[]; previous: PerformanceRow[]
  // A one-day period also comes hour by hour ("yyyy-MM-ddTHH:00", Istanbul); the previous day stops at the same time.
  currentHours?: HourRow[] | null; previousHours?: HourRow[] | null
}
export type HourRow = Omit<PerformanceRow, 'day'> & { hour: string }
// day is the calendar day, or the hour key ("yyyy-MM-ddTHH:00") of an hourly series; label is the axis text then.
export type DayPoint = { day: string; label?: string; amount: number; orderCount: number; productQuantity: number; packageCount: number; returnedAmount: number; returnedOrderCount: number }
export type Range = 'today' | 'yesterday' | '7' | '30' | '90' | 'custom'

const dayPattern = /^\d{4}-\d{2}-\d{2}$/

/** A calendar day moved by whole days; plain date arithmetic, no time zone involved. */
export function addDays(day: string, days: number) {
  const date = new Date(`${day}T00:00:00Z`)
  date.setUTCDate(date.getUTCDate() + days)
  return date.toISOString().slice(0, 10)
}

export function dayKeys(from: string, to: string) {
  if (!dayPattern.test(from) || !dayPattern.test(to) || from > to) return []
  const days: string[] = []
  for (let day = from; day <= to && days.length <= 400; day = addDays(day, 1)) days.push(day)
  return days
}

/** The calendar days a period button asks for; null while a custom range is only half picked. `today` is Istanbul's. */
export function periodDays(range: Range, custom: { from: string; to: string }, today: string): { from: string; to: string } | null {
  if (range === 'custom') {
    if (!dayPattern.test(custom.from) || !dayPattern.test(custom.to)) return null
    return custom.from <= custom.to ? { from: custom.from, to: custom.to } : { from: custom.to, to: custom.from }
  }
  // "Bugün" is today so far; 7/30/90 days are Istanbul calendar days ending with today, counted as a whole day and
  // compared with the whole days before them (no same-hour cut).
  if (range === 'today') return { from: today, to: today }
  // "Dün" is yesterday's whole Istanbul day (00:00-24:00); the server compares it with the whole day before it.
  if (range === 'yesterday') { const yesterday = addDays(today, -1); return { from: yesterday, to: yesterday } }
  return { from: addDays(today, 1 - Number(range)), to: today }
}

const revenue = (row: { amount: number; returnedAmount: number }) => row.amount + row.returnedAmount
const inChannel = (row: { connectionId: string }, channel: string) => channel === 'ALL' || row.connectionId === channel

/** The currency the panel totals in: Turkish lira whenever there is any, otherwise the largest one. Others are listed apart. */
export function primaryCurrency(rows: PerformanceRow[]) {
  if (!rows.length || rows.some(row => row.currency === 'TRY')) return 'TRY'
  const totals = new Map<string, number>()
  for (const row of rows) totals.set(row.currency, (totals.get(row.currency) ?? 0) + revenue(row))
  return [...totals.entries()].sort((left, right) => right[1] - left[1])[0][0]
}

/** One point per day of [from, to] in one currency, zero for days without orders. */
export function daySeries(rows: PerformanceRow[], from: string, to: string, channel: string, currency: string): DayPoint[] {
  const byDay = new Map<string, DayPoint>()
  for (const row of rows) {
    if (!inChannel(row, channel) || row.currency !== currency) continue
    const point = byDay.get(row.day) ?? { day: row.day, amount: 0, orderCount: 0, productQuantity: 0, packageCount: 0, returnedAmount: 0, returnedOrderCount: 0 }
    point.amount += revenue(row); point.orderCount += row.orderCount; point.productQuantity += row.productQuantity
    point.packageCount += row.packageCount; point.returnedAmount += row.returnedAmount; point.returnedOrderCount += row.returnedOrderCount
    byDay.set(row.day, point)
  }
  return dayKeys(from, to).map(day => byDay.get(day) ?? { day, amount: 0, orderCount: 0, productQuantity: 0, packageCount: 0, returnedAmount: 0, returnedOrderCount: 0 })
}

/** Revenue in currencies other than the panel's, which is never added into its total. */
export function otherCurrencies(rows: PerformanceRow[], channel: string, primary: string) {
  const totals = new Map<string, { amount: number; orders: number }>()
  for (const row of rows) {
    if (!inChannel(row, channel) || row.currency === primary) continue
    const total = totals.get(row.currency) ?? { amount: 0, orders: 0 }
    total.amount += revenue(row); total.orders += row.orderCount
    totals.set(row.currency, total)
  }
  return [...totals.entries()].filter(([, total]) => total.amount > 0 || total.orders > 0).map(([currency, total]) => ({ currency, ...total }))
}

export type ChannelTotal = { connectionId: string; platformCode: string; name: string; amount: number; orders: number; previousAmount: number }

/** Revenue per store, keyed by connection: two stores of one marketplace stay two rows. */
export function channelTotals(performance: Performance, currency: string): ChannelTotal[] {
  const sum = (rows: PerformanceRow[], connectionId: string) => rows.filter(row => row.connectionId === connectionId && row.currency === currency)
  const known = new Map(performance.channels.map(channel => [channel.connectionId, channel]))
  const ids = new Set([...performance.current.map(row => row.connectionId), ...performance.channels.map(channel => channel.connectionId)])
  return [...ids].map(connectionId => {
    const current = sum(performance.current, connectionId)
    const channel = known.get(connectionId)
    return {
      connectionId,
      platformCode: channel?.platformCode ?? '',
      name: channel?.displayName ?? 'Kaldırılmış mağaza',
      amount: current.reduce((total, row) => total + revenue(row), 0),
      orders: current.reduce((total, row) => total + row.orderCount, 0),
      previousAmount: sum(performance.previous, connectionId).reduce((total, row) => total + revenue(row), 0),
    }
  }).filter(channel => channel.amount > 0 || channel.orders > 0).sort((left, right) => right.amount - left.amount || right.orders - left.orders)
}

const csvCell = (value: string | number) => `"${String(value).replace(/"/g, '""')}"`
const amountText = (value: number) => value.toLocaleString('tr-TR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
const slug = (value: string) => value.toLocaleLowerCase('tr-TR').replace(/[ıİ]/g, 'i').replace(/ğ/g, 'g').replace(/ü/g, 'u').replace(/ş/g, 's').replace(/ö/g, 'o').replace(/ç/g, 'c').replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '') || 'magaza'

/** The report file: one row per day and currency of the chosen period and store, the day exactly as the server sent it. */
export function revenueReport(performance: Performance, channel: string, channelName: string) {
  const currencies = [...new Set(performance.current.filter(row => inChannel(row, channel)).map(row => row.currency))].sort()
  if (!currencies.length) currencies.push('TRY')
  const header = ['Tarih', 'Para Birimi', 'Ciro', 'Sipariş', 'Ürün Adedi', 'Paket', 'İade Tutarı', 'İade Sipariş']
  const rows = currencies.flatMap(currency => daySeries(performance.current, performance.from, performance.to, channel, currency)
    .map(point => [point.day.slice(0, 10), currency, amountText(point.amount), point.orderCount, point.productQuantity, point.packageCount, amountText(point.returnedAmount), point.returnedOrderCount]))
  rows.sort((left, right) => String(left[0]).localeCompare(String(right[0])) || String(left[1]).localeCompare(String(right[1])))
  const content = String.fromCharCode(0xfeff) + `${[header, ...rows].map(row => row.map(csvCell).join(';')).join('\r\n')}`
  const name = `ravencia-ciro-${performance.from}_${performance.to}-${channel === 'ALL' ? 'tum-platformlar' : slug(channelName)}.csv`
  return { name, content, columns: header }
}

/** Hours 00..lastHour of one Istanbul day, the hourly rows of the chosen store and currency added up. */
export function hourSeries(rows: HourRow[], day: string, channel: string, currency: string, lastHour: number): DayPoint[] {
  const byHour = new Map<string, DayPoint>()
  for (const row of rows) {
    if (!inChannel(row, channel) || row.currency !== currency) continue
    const point = byHour.get(row.hour) ?? { day: row.hour, amount: 0, orderCount: 0, productQuantity: 0, packageCount: 0, returnedAmount: 0, returnedOrderCount: 0 }
    point.amount += revenue(row); point.orderCount += row.orderCount; point.productQuantity += row.productQuantity
    point.packageCount += row.packageCount; point.returnedAmount += row.returnedAmount; point.returnedOrderCount += row.returnedOrderCount
    byHour.set(row.hour, point)
  }
  return Array.from({ length: Math.max(0, Math.min(23, lastHour)) + 1 }, (_, hour) => {
    const label = `${String(hour).padStart(2, '0')}:00`
    const key = `${day}T${label}`
    return { ...(byHour.get(key) ?? { day: key, amount: 0, orderCount: 0, productQuantity: 0, packageCount: 0, returnedAmount: 0, returnedOrderCount: 0 }), label }
  })
}

/** The Istanbul hour (0-23) of an instant: the last hour a partial "today" report has reached. */
export function istanbulHour(instant: string) {
  const date = new Date(instant)
  if (Number.isNaN(date.getTime())) return 23
  const hour = Number(new Intl.DateTimeFormat('en-GB', { timeZone: 'Europe/Istanbul', hour: '2-digit', hourCycle: 'h23' }).format(date))
  return Number.isFinite(hour) ? hour : 23
}
