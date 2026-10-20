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

/** The last attempt of a job: from the start of that attempt to its end (now while it runs). */
export function jobAttemptDuration(job: { status: string; startedAt: string | null; lastAttemptStartedAt: string | null; completedAt: string | null; lastFailedAt: string | null }) {
  return jobDuration(job.lastAttemptStartedAt ?? job.startedAt, job.completedAt ?? job.lastFailedAt, job.lastAttemptStartedAt, job.status)
}
