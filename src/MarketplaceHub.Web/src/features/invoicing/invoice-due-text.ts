import { formatDate } from '../../shared/format-date'

const hourMs = 3_600_000
const dayMs = 24 * hourMs

// The line under "Fatura Bekliyor": how long is left until the invoice deadline, then the day itself. Under a day left
// it counts hours and is critical (shown red); a passed deadline says so.
export function invoiceDueText(dueAt: string, now = Date.now()) {
  const left = new Date(dueAt).getTime() - now
  const day = formatDate(dueAt)
  if (!Number.isFinite(left)) return { text: `Son Tarih: ${day}`, critical: false }
  if (left <= 0) return { text: `Süre doldu · ${day}`, critical: true }
  if (left < dayMs) return { text: `${Math.max(1, Math.floor(left / hourMs))} saat kaldı · ${day}`, critical: true }
  return { text: `${Math.floor(left / dayMs)} gün kaldı · ${day}`, critical: false }
}
