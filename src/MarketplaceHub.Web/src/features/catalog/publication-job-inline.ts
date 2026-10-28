import { statusLabel } from '../../shared/status-labels'
import { isSettledPublicationJob } from './publication-job-poll'
import { isApprovalWaitJob } from './publication-status'

export type PublicationJobSnapshot = {
  jobType?: string | null
  status: string
  progressCurrent: number
  progressTotal: number | null
  progressPercent: number | null
  progressLabel: string | null
  lastErrorCode: string | null
  lastErrorSummary: string | null
}

// What a publish card shows of its last job without growing: a running job turns the status badge into a spinner
// (its message on hover and for screen readers) and offers the stop button beside refresh; a job that stopped on
// its own (blocked, manual review, dead, cancelled) leaves one cut-off line under the status.
export type PublicationJobInline =
  | { state: 'hidden' }
  | { state: 'running'; message: string }
  | { state: 'settled'; message: string; tone: 'danger' | 'muted' }

export function publicationJobInline(job: PublicationJobSnapshot | null | undefined): PublicationJobInline {
  if (!job) return { state: 'hidden' }
  const status = job.status.trim().toUpperCase()
  // A finished job says nothing the card's publication status does not already say.
  if (status === 'SUCCEEDED') return { state: 'hidden' }
  // An approval check only waiting for the marketplace: the card's status already says "Onay bekliyor".
  if (isApprovalWaitJob(job)) return { state: 'hidden' }
  const summary = job.lastErrorSummary?.trim()
  if (isSettledPublicationJob(status)) {
    return { state: 'settled', message: summary ? `${statusLabel(status)} · ${summary}` : statusLabel(status), tone: status === 'CANCELLED' ? 'muted' : 'danger' }
  }
  const percent = job.progressPercent == null ? null : Math.max(0, Math.min(100, job.progressPercent))
  const counts = job.progressTotal == null ? '' : `${job.progressCurrent.toLocaleString('tr-TR')} / ${job.progressTotal.toLocaleString('tr-TR')}`
  const progress = percent !== null ? [`%${percent}`, counts].filter(Boolean).join(' · ') : counts
  const label = job.progressLabel?.trim() || 'Yayın işlemi'
  return { state: 'running', message: `${label}: ${summary || progress || statusLabel(status)}` }
}
