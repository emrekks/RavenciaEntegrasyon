const shipmentDeadlineExemptStatuses = new Set(['ON_HOLD'])

export function onHoldOrderStatusText(platformCode: string, rawStatuses: readonly string[] = []) {
  if (platformCode.trim().toUpperCase() !== 'HEPSIBURADA') return 'Askıda'

  const statuses = rawStatuses.map(status => status.trim().toUpperCase().replace(/[\s_-]/g, ''))
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
