import { useQuery } from '@tanstack/react-query'
import { createPortal } from 'react-dom'
import { Link } from 'react-router'
import { hubPoll } from '../../../shared/api'
import { DialogSurface, UiIcon } from '../../../shared/components'
import { notificationLinks } from '../../../shared/notifications'
import { platformLogoSource } from '../../../shared/platform-logos'
import { jobPresentation } from '../../operations/job-presentation'
import { isWaitingJob, jobAttemptOutcome, jobAttemptResultLabel, jobAttemptsText, waitingJobLabel } from '../../operations/job-waiting'
import { publicationJobPollInterval } from '../publication-job-poll'

type JobAttempt = { attemptNumber: number; startedAt: string; completedAt: string | null; succeeded: boolean; errorCode: string | null; errorSummary: string | null; outcome?: string }
type JobChange = { label: string; value: string; detail: string | null }
export type PublicationJobDialogDetail = {
  job: {
    id: string; jobType: string; status: string; attemptCount: number; maxAttempts: number; correlationId: string
    createdAt: string; startedAt: string | null; completedAt: string | null; nextRetryAt: string | null; heartbeatAt: string | null
    progressCurrent: number; progressTotal: number | null; progressPercent: number | null; progressLabel: string | null
    lastErrorCode: string | null; lastErrorSummary: string | null
    waiting?: boolean; waitingChecks?: number; attemptBudget?: number
  }
  attempts?: JobAttempt[]
  change?: JobChange | null
}

const statusLabels: Record<string, string> = {
  PENDING: 'Sırada', LEASED: 'Çalışıyor', RETRY_SCHEDULED: 'Yeniden Denenecek', BLOCKED: 'Engellendi', MANUAL_REVIEW: 'İnceleme Bekliyor',
  SUCCEEDED: 'Tamamlandı', DEAD: 'Deneme Limiti Doldu', CANCELLED: 'İptal Edildi'
}
const statusTones: Record<string, string> = { SUCCEEDED: 'success', DEAD: 'danger', BLOCKED: 'danger', CANCELLED: 'neutral', MANUAL_REVIEW: 'warning', RETRY_SCHEDULED: 'warning' }

export function publicationJobStatusLabel(status: string) {
  return statusLabels[status.trim().toUpperCase()] ?? status
}

// "%40 · 2/5 · Renkler gönderiliyor" or nothing while no progress is known.
export function publicationJobProgress(job: PublicationJobDialogDetail['job']) {
  const parts = [
    job.progressPercent != null ? `%${job.progressPercent}` : null,
    job.progressTotal != null ? `${job.progressCurrent}/${job.progressTotal}` : null,
    job.progressLabel
  ].filter(Boolean)
  return parts.length ? parts.join(' · ') : null
}

const attemptClass = { SUCCEEDED: 'is-ok', FAILED: 'is-failed', WAITING: 'is-running', RUNNING: 'is-running' } as const

// A failed try shows the marketplace's answer; a waiting check says it was checked and what it waits for.
function attemptText(attempt: JobAttempt) {
  const outcome = jobAttemptOutcome(attempt)
  if (outcome === 'SUCCEEDED') return 'Başarılı'
  if (outcome === 'RUNNING') return 'Sürüyor'
  if (outcome === 'WAITING') return `${jobAttemptResultLabel(attempt)}${attempt.errorSummary ? ` · ${attempt.errorSummary}` : ''}`
  return `${attempt.errorSummary ?? 'Başarısız'}${attempt.errorCode ? ` (${attempt.errorCode})` : ''}`
}

const when = (value: string | null | undefined) => value
  ? new Date(value).toLocaleString('tr-TR', { day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit', second: '2-digit', timeZone: 'Europe/Istanbul' })
  : null

// The API job behind a publication card's status: what it does, where it stands, its tries and the marketplace's answer.
export function PublicationJobDialog({ jobId, platformCode, platformName, storeName, onClose }: { jobId: string; platformCode: string; platformName: string; storeName: string; onClose: () => void }) {
  const detail = useQuery({
    queryKey: ['publication-job-detail', jobId],
    queryFn: () => hubPoll<PublicationJobDialogDetail>(`/jobs/${jobId}`),
    refetchInterval: query => publicationJobPollInterval(query.state.data?.job.status, query.state.status === 'error')
  })
  const job = detail.data?.job
  const status = job?.status.trim().toUpperCase() ?? ''
  const presentation = job ? jobPresentation(job.jobType) : null
  const logo = platformLogoSource(platformCode)
  const progress = job ? publicationJobProgress(job) : null
  // A job that only waits for the marketplace to finish (a batch task, an approval) has no error to show.
  const waiting = Boolean(job && isWaitingJob({ status, waiting: job.waiting }))
  const statusText = job && waiting ? waitingJobLabel(job) : publicationJobStatusLabel(status)
  const hasError = Boolean(job && status !== 'SUCCEEDED' && !waiting && (job.lastErrorCode || job.lastErrorSummary))
  const attempts = detail.data?.attempts ?? []
  const facts: Array<[string, string | null]> = job ? [
    ['Durum', statusText],
    ['İşlem', presentation ? `${presentation.area} · ${presentation.action}` : job.jobType],
    ['İlerleme', progress],
    ['Deneme', jobAttemptsText(job)],
    ['Durum Kontrolü', job.waitingChecks ? String(job.waitingChecks) : null],
    ['Oluşturuldu', when(job.createdAt)],
    ['Başladı', when(job.startedAt)],
    ['Bitti', when(job.completedAt)],
    [waiting ? 'Sonraki Kontrol' : 'Sonraki Deneme', status === 'RETRY_SCHEDULED' ? when(job.nextRetryAt) : null],
    ['Son Sinyal', status === 'LEASED' ? when(job.heartbeatAt) : null],
    ['İş No', job.id],
    ['Korelasyon No', job.correlationId]
  ] : []

  // Portaled into main: rendered inside the publish card, the sticky Yayınla footer later in the page covered it.
  return createPortal(<div className="workspace-modal-backdrop publication-job-backdrop" role="presentation" onMouseDown={onClose}>
    <DialogSurface as="section" onClose={onClose} className="workspace-modal publication-job-modal" aria-labelledby="publication-job-title" onMouseDown={event => event.stopPropagation()}>
      <header>
        <div className="publication-job-head">
          {logo && <img className="publication-job-logo" src={logo} alt="" aria-hidden="true" />}
          <div><p className="eyebrow">{storeName && storeName !== platformName ? `${platformName} · ${storeName}` : platformName}</p><h2 id="publication-job-title">{presentation ? `${platformName} ${presentation.action}` : 'Yayın İşi'}</h2>{job && <span className={`publication-job-status is-${waiting ? 'info' : statusTones[status] ?? 'info'}`}>{statusText}</span>}</div>
        </div>
        <button type="button" className="modal-close" onClick={onClose} aria-label="İş ayrıntısını kapat"><UiIcon name="close" /></button>
      </header>
      <div className="publication-job-body">
        {detail.isLoading ? <p className="publication-job-note" role="status">İş ayrıntısı yükleniyor…</p>
          : detail.isError || !job ? <p className="publication-job-note" role="alert">İş ayrıntısı okunamadı.</p>
            : <>
              {hasError && <div className="publication-job-error" role="alert"><UiIcon name="alert" /><div><strong>{job.lastErrorSummary ?? 'Pazaryeri işi reddetti.'}</strong>{job.lastErrorCode && <small>Kod: {job.lastErrorCode}</small>}</div></div>}
              {detail.data?.change && <p className="publication-job-change"><b>{detail.data.change.label}:</b> {detail.data.change.value}{detail.data.change.detail ? ` — ${detail.data.change.detail}` : ''}</p>}
              <dl className="publication-job-facts">{facts.filter(([, value]) => value).map(([label, value]) => <div key={label}><dt>{label}</dt><dd>{value}</dd></div>)}</dl>
              {attempts.length > 0 && <section className="publication-job-attempts" aria-label="Denemeler">
                <h3>Denemeler</h3>
                <ol>{attempts.map(attempt => <li key={attempt.attemptNumber} className={attemptClass[jobAttemptOutcome(attempt)]}>
                  <span>{attempt.attemptNumber}. Deneme · {when(attempt.startedAt)}</span>
                  <small>{attemptText(attempt)}</small>
                </li>)}</ol>
              </section>}
            </>}
      </div>
      <footer><Link className="button-link secondary" to={notificationLinks.job(jobId)} onClick={onClose}>İşlem Takibinde Aç</Link><button type="button" onClick={onClose}>Kapat</button></footer>
    </DialogSurface>
  </div>, document.querySelector('main') ?? document.body)
}
