// A returned order (or one on its way back) has no shipping deadline left to miss: it was shipped (owner, 17 Nov 2026).
const shipmentDeadlineExemptStatuses = new Set(['ON_HOLD', 'PAYMENT_PENDING', 'RETURNED', 'RETURN_IN_TRANSIT'])

export function onHoldOrderStatusText(platformCode: string, rawStatuses: readonly string[] = []) {
  if (platformCode.trim().toUpperCase() !== 'HEPSIBURADA') return 'Askıda'

  const statuses = rawStatuses.map(status => status.trim().toUpperCase().replace(/[\s_-]/g, ''))
  // An undelivered Hepsiburada order is UNDELIVERED since 5 Nov 2026; an older row still ON_HOLD until its next read
  // keeps saying what it is.
  if (statuses.includes('UNDELIVERED')) return 'Teslim edilemedi'
  if (statuses.includes('CLAIMCREATED')) return 'Talep açıldı'
  return 'Askıda'
}

export function overdueShipmentDays(status: string, dueAtValue: string | null, now = Date.now()) {
  if (shipmentDeadlineExemptStatuses.has(status.toUpperCase()) || !dueAtValue) return 0

  const dueAt = new Date(dueAtValue).getTime()
  if (!Number.isFinite(dueAt) || dueAt < Date.UTC(2000, 0, 1)) return 0

  return Math.max(0, Math.floor((now - dueAt) / 86_400_000))
}

// The hours an order is past its ship-by time, with their fraction (0 when not late, or exempt): an order late by less than a day is
// late too, not "Süre doldu" (scan D-11).
export function overdueShipmentHours(status: string, dueAtValue: string | null, now = Date.now()) {
  if (shipmentDeadlineExemptStatuses.has(status.toUpperCase()) || !dueAtValue) return 0

  const dueAt = new Date(dueAtValue).getTime()
  if (!Number.isFinite(dueAt) || dueAt < Date.UTC(2000, 0, 1) || now <= dueAt) return 0

  return (now - dueAt) / 3_600_000
}
