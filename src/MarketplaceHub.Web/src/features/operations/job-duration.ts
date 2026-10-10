export function jobDuration(
  startedAt: string | null,
  completedAt: string | null,
  currentAttemptStartedAt: string | null,
  status: string
) {
  const effectiveStart = status === 'LEASED' ? currentAttemptStartedAt ?? startedAt : startedAt
  if (!effectiveStart) return 'Başlamadı'
  const start = new Date(effectiveStart).getTime()
  const end = completedAt && status !== 'LEASED' ? new Date(completedAt).getTime() : Date.now()
  if (!Number.isFinite(start) || !Number.isFinite(end) || end < start) return '—'
  const seconds = Math.max(0, Math.round((end - start) / 1000))
  if (seconds < 60) return `${seconds} sn`
  return `${Math.floor(seconds / 60)} dk ${seconds % 60} sn`
}
