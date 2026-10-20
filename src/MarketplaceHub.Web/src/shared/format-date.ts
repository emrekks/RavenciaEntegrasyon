// Dates in the panel are Turkish business dates: every order, invoice and deadline is read and shown in Istanbul time,
// whatever the clock of the browser says. These helpers are the only place that turns an instant into text, a
// date-time input value, or a day boundary for a filter.
export const istanbulTimeZone = 'Europe/Istanbul'

type DateInput = string | number | Date | null | undefined

const formatters = new Map<string, Intl.DateTimeFormat>()
function formatter(options: Intl.DateTimeFormatOptions) {
  const key = JSON.stringify(options)
  let cached = formatters.get(key)
  if (!cached) {
    cached = new Intl.DateTimeFormat('tr-TR', { ...options, timeZone: istanbulTimeZone })
    formatters.set(key, cached)
  }
  return cached
}

/** A usable Date, or null for an empty or unreadable value. */
export function toDate(value: DateInput): Date | null {
  if (value === null || value === undefined || value === '') return null
  const date = value instanceof Date ? value : new Date(value)
  return Number.isNaN(date.getTime()) ? null : date
}

/** Any Intl date format, always in Istanbul time; `fallback` for an empty or unreadable value. */
export function formatIstanbul(value: DateInput, options: Intl.DateTimeFormatOptions = dateTimeOptions, fallback = '—') {
  const date = toDate(value)
  return date ? formatter(options).format(date) : fallback
}

// The same shapes `toLocaleString('tr-TR')`, `toLocaleDateString('tr-TR')` and `toLocaleTimeString('tr-TR')` give.
const dateOptions: Intl.DateTimeFormatOptions = { day: '2-digit', month: '2-digit', year: 'numeric' }
const timeOptions: Intl.DateTimeFormatOptions = { hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23' }
const dateTimeOptions: Intl.DateTimeFormatOptions = { ...dateOptions, ...timeOptions }

/** 19.10.2026 13:24:19 */
export const formatDateTime = (value: DateInput, fallback = '—') => formatIstanbul(value, dateTimeOptions, fallback)
/** 19.10.2026 */
export const formatDate = (value: DateInput, fallback = '—') => formatIstanbul(value, dateOptions, fallback)
/** 13:24:19 */
export const formatTime = (value: DateInput, fallback = '—') => formatIstanbul(value, timeOptions, fallback)

function istanbulParts(date: Date) {
  const parts = Object.fromEntries(formatter({ year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23' }).formatToParts(date).map(part => [part.type, part.value]))
  return { year: Number(parts.year), month: Number(parts.month), day: Number(parts.day), hour: Number(parts.hour) % 24, minute: Number(parts.minute), second: Number(parts.second) }
}

/** Minutes Istanbul is ahead of UTC at that instant (180 since 2016). */
export function istanbulOffsetMinutes(date: Date) {
  const wall = istanbulParts(date)
  const wallAsUtc = Date.UTC(wall.year, wall.month - 1, wall.day, wall.hour, wall.minute, wall.second)
  return Math.round((wallAsUtc - Math.floor(date.getTime() / 1000) * 1000) / 60_000)
}

const pad = (value: number, length = 2) => String(value).padStart(length, '0')

/** The Istanbul calendar day of an instant as YYYY-MM-DD (today by default). */
export function istanbulDateKey(value: DateInput = new Date()) {
  const date = toDate(value)
  if (!date) return ''
  const wall = istanbulParts(date)
  return `${wall.year}-${pad(wall.month)}-${pad(wall.day)}`
}

/** Value for an `<input type="datetime-local">`: the instant as Istanbul wall-clock time, YYYY-MM-DDTHH:mm. */
export function toDateTimeLocal(value: DateInput) {
  const date = toDate(value)
  if (!date) return ''
  const wall = istanbulParts(date)
  return `${wall.year}-${pad(wall.month)}-${pad(wall.day)}T${pad(wall.hour)}:${pad(wall.minute)}`
}

const localPattern = /^(\d{4})-(\d{2})-(\d{2})(?:T(\d{2}):(\d{2})(?::(\d{2})(?:\.(\d{1,3}))?)?)?$/

/** The instant an Istanbul wall-clock value (a datetime-local or date input) means, as an ISO string; '' when unreadable. */
export function fromDateTimeLocal(value: string) {
  const match = localPattern.exec(value.trim())
  if (!match) return ''
  const [, year, month, day, hour = '0', minute = '0', second = '0', millisecond = '0'] = match
  const wallAsUtc = Date.UTC(Number(year), Number(month) - 1, Number(day), Number(hour), Number(minute), Number(second), Number(millisecond.padEnd(3, '0')))
  // The offset is read at the guessed instant and once more at the corrected one, so a day that changes offset still lands right.
  let instant = wallAsUtc - istanbulOffsetMinutes(new Date(wallAsUtc)) * 60_000
  instant = wallAsUtc - istanbulOffsetMinutes(new Date(instant)) * 60_000
  return new Date(instant).toISOString()
}

function offsetText(minutes: number) {
  const sign = minutes < 0 ? '-' : '+'
  const absolute = Math.abs(minutes)
  return `${sign}${pad(Math.floor(absolute / 60))}:${pad(absolute % 60)}`
}

/** Start (or end) of an Istanbul calendar day for a date filter, e.g. 2026-10-19T00:00:00.000+03:00. */
export function istanbulDayBoundary(day: string, endOfDay = false) {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(day)) return ''
  const noon = new Date(`${day}T12:00:00Z`)
  return `${day}T${endOfDay ? '23:59:59.999' : '00:00:00.000'}${offsetText(istanbulOffsetMinutes(noon))}`
}
