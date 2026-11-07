import { lazy, Suspense, useEffect, useMemo, useRef, useState } from 'react'
import { platformDisplayNames } from '../../shared/platform-names'
import { keepPreviousData, useInfiniteQuery, useMutation, useQuery, useQueryClient, type InfiniteData } from '@tanstack/react-query'
import { Link, useSearchParams } from 'react-router'
import { hubApi, hubPoll, loadAllPages, type Me } from '../../shared/api'
import { RrLogo, RrPageSize } from '../marketplace/returns-ui'
import { ChoiceField, FilterMenu, OpsFilterCard, SearchField, type FilterMenuGroup } from './filter-fields'
import { marketplacePlatformLabel } from '../marketplace/marketplace-platform-support'
import { Button, EmptyState, Pagination, Tabs, TitleHint, UiIcon, type UiIconName, DialogSurface } from '../../shared/components'
import { Busy } from '../../shared/components/AsyncState'
import { NoticeToast } from '../../shared/components/NoticeToast'
import { appendNotification, notificationError, notificationLinks, platformCodeFromName, type NotificationContext } from '../../shared/notifications'
import { statusLabel } from '../../shared/status-labels'
import { fallbackJobChange, jobRelatedLinks, type JobChange } from './job-change'
import { isProductLevelJob, type JobWriteSummary } from './job-write-summary'
import { JobWriteDetail } from './JobWriteDetail'
import { JobSentDetail } from './JobSentDetail'
import { JobReadRecords } from './JobReadRecords'
import { jobProgressPercent, keepsReadRecords, readProgressCounts, type JobReadSummary } from './job-read-summary'
import type { JobVariantResults } from '../catalog/product-editor/PublicationJobVariants'
import { jobDirections, jobPresentation, jobSubtitle, jobTitle, jobTypeCode, jobTypesForDirection, type JobDirection, type JobTypeIconName } from './job-presentation'
import { canOfferOneTimeInvoiceDelivery } from './one-time-invoice-delivery-visibility'
import { oneTimeInvoiceDeliveryIdempotencyKey } from './one-time-invoice-delivery-idempotency'
import { isJobIdSearch, isJobInTimeRange, jobLookupQueryString, jobLookupTerm, jobTimeRangeOptions, jobCountsQueryString, jobDetailPollInterval, jobListHeadPollInterval, jobListPollInterval, jobListPollMs, jobPageQueryString,jobPlatformsFromSearch, jobPlatformOptions, shouldLoadTowardsPage, withFreshFirstPage, type JobListTimeRange } from './job-list-query'
import { jobCategoryMatcher, jobCategoryTabs, jobTypesForCategoryTab, type JobCategory } from './job-categories'
import { jobPageSizes, jobsMainTab, readJobsViewState, withJobsMainTab, writeJobsViewState, type JobsMainTab, type JobStatusChip, type JobsViewState } from './jobs-url-state'
import type { IntegrationFeedback } from '../marketplace/shared'

// The logs (Loglar: Ürün Hareketleri and the send log) and the sending health (API Sağlık) load with their own chunks the
// first time their tab opens.
const OperationLogs = lazy(() => import('./OperationLogs'))
const ApiHealthView = lazy(() => import('./ApiHealthView').then(module => ({ default: module.ApiHealthView })))

// E-Faturam is the invoicing provider; every other code is a marketplace.
const jobPlatformLabel = (code: string) => code === 'TRENDYOL_EFATURAM' ? platformDisplayNames.TRENDYOL_EFATURAM : marketplacePlatformLabel(code)
import './jobs-responsive.css'
import './jobs-sade.css'
import { viewTabLabel } from '../../shared/view-tabs'
import { useRememberedTabCounts } from '../../shared/tab-counts'
import { formatDateTime, formatIstanbul, istanbulDateKey } from '../../shared/format-date'
import { formatMoney } from '../../shared/format-money'
import { formatPackageLabel } from '../../shared/package-label'
import { newIdempotencyKey } from '../../shared/idempotency'
import { jobAttemptDuration } from './job-duration'
import { isWaitingJob, jobAttemptOutcome, jobAttemptResultLabel, jobAttemptsText, waitingJobDescription, waitingJobLabel } from './job-waiting'
import { JobStateGlyph } from './job-state-glyph'
import { useTabSlide } from '../../shared/tab-slide'

const jobsMainTabOrder = ['jobs', 'logs', 'health']
type JobStatus = 'PENDING' | 'LEASED' | 'RETRY_SCHEDULED' | 'BLOCKED' | 'MANUAL_REVIEW' | 'SUCCEEDED' | 'DEAD' | 'CANCELLED'
type JobSummary = {
  id: string
  connectionId: string | null
  jobType: string
  status: JobStatus
  attemptCount: number
  maxAttempts: number
  availableAt: string
  lastErrorCode: string | null
  lastErrorSummary: string | null
  correlationId: string
  createdAt: string
  startedAt: string | null
  completedAt: string | null
  marketplace: string
  externalId: string | null
  firstFailedAt: string | null
  lastFailedAt: string | null
  nextRetryAt: string | null
  batchCount: number
  progressCurrent: number
  progressTotal: number | null
  progressPercent: number | null
  progressLabel: string | null
  progressReceived: number
  progressProcessed: number
  progressSkipped: number
  progressFailed: number
  lastAttemptStartedAt: string | null
  heartbeatAt: string | null
  waitingChecks?: number
  attemptBudget?: number
  waiting?: boolean
}
type JobOrderContext = { orderId: string; orderNumber: string; externalOrderId: string; status: string; currency: string; netAmount: number; orderedAt: string; externalPackageId: string | null; cargoProvider: string | null; cargoTrackingNumber: string | null; customerName: string | null; lineCount: number }
type JobInvoiceContext = { invoiceId: string; orderNumber: string; status: string; invoiceType: string; currency: string; payableTotal: number; invoiceNumber: string | null; externalPackageId: string | null }
type JobScan = { mode: string; label: string; detail: string; window: string | null; plannedIntervalSeconds: number | null; plannedIntervalLabel: string | null; previousScheduledAt: string | null; actualIntervalLabel: string | null }
type JobFailureReason = { key: string; title: string; description: string; technicalDetail: string; affectedRecords: number; sampleProductIds: string[] }
type JobDetail = { job: JobSummary; attempts: Array<{ attemptNumber: number; startedAt: string; completedAt: string | null; succeeded: boolean; errorCode: string | null; errorSummary: string | null; outcome?: string }>; order: JobOrderContext | null; change: JobChange | null; relatedOrders: JobOrderContext[]; scan: JobScan | null; failureReasons: JobFailureReason[] | null; invoice: JobInvoiceContext | null; productId?: string | null; variantResults?: JobVariantResults | null; write?: JobWriteSummary | null; read?: JobReadSummary | null }
// The job a notice is about: its title ("Pazarama Ürün İşlemi"), number, marketplace and store.
function jobNotificationContext(job: Pick<JobSummary, 'id' | 'connectionId' | 'jobType' | 'marketplace'>): NotificationContext {
  const title = jobTitle(job.jobType, job.marketplace)
  return { operation: title, platformCode: platformCodeFromName(job.marketplace), connectionId: job.connectionId ?? undefined, jobId: job.id, subject: { kind: 'job', id: job.id, label: title, href: notificationLinks.job(job.id) } }
}
type JobPage = { items: JobSummary[]; nextCursor: string | null; hasMore: boolean }

const statuses: Array<{ value: '' | JobStatus; label: string }> = [
  { value: '', label: 'Tüm Durumlar' },
  { value: 'PENDING', label: 'Bekliyor' },
  { value: 'LEASED', label: 'Çalışıyor' },
  { value: 'RETRY_SCHEDULED', label: 'Tekrar Denenecek' },
  { value: 'BLOCKED', label: 'Engellendi' },
  { value: 'MANUAL_REVIEW', label: 'Manuel İnceleme' },
  { value: 'SUCCEEDED', label: 'Başarılı' },
  { value: 'DEAD', label: 'Deneme Limiti Doldu' },
  { value: 'CANCELLED', label: 'İptal Edildi' }
]


function jobStatusLabel(status: JobStatus) {
  if (status === 'PENDING') return 'Bekliyor'
  if (status === 'LEASED') return 'Çalışıyor'
  if (status === 'RETRY_SCHEDULED') return 'Yeniden denenecek'
  if (status === 'BLOCKED') return 'Engellendi'
  if (status === 'MANUAL_REVIEW') return 'İnceleme bekliyor'
  if (status === 'SUCCEEDED') return 'Başarılı'
  if (status === 'DEAD') return 'Deneme limiti doldu'
  return 'İptal edildi'
}


// A job that only waits for the marketplace to finish (a batch task, an approval) reads as waiting, not as a retry.
function jobStateLabel(job: JobSummary) {
  return isWaitingJob(job) ? waitingJobLabel(job) : jobStatusLabel(job.status)
}

function jobStateTone(job: JobSummary) {
  return isWaitingJob(job) ? 'neutral' : jobStatusTone(job.status)
}

function jobStatusTone(status: JobStatus) {
  if (status === 'SUCCEEDED') return 'success'
  if (status === 'LEASED') return 'running'
  if (status === 'RETRY_SCHEDULED') return 'retry'
  if (status === 'BLOCKED' || status === 'MANUAL_REVIEW' || status === 'DEAD') return 'error'
  return 'neutral'
}

// The status counts above the list double as filters; each job falls in exactly one of them, the same way they are counted.
function jobStatusChip(status: JobStatus): JobStatusChip {
  if (status === 'SUCCEEDED') return 'success'
  if (status === 'LEASED') return 'running'
  if (status === 'PENDING' || status === 'RETRY_SCHEDULED') return 'waiting'
  if (status === 'CANCELLED') return 'cancelled'
  return 'error'
}

// The statuses each state filter stands for; the list asks the server for them so older jobs past the newest 2.000 show too.
const jobChipStatuses: Record<JobStatusChip, JobStatus[]> = { success: ['SUCCEEDED'], running: ['LEASED'], waiting: ['PENDING', 'RETRY_SCHEDULED'], error: ['BLOCKED', 'MANUAL_REVIEW', 'DEAD'], cancelled: ['CANCELLED'] }

// "Bugün" is the Istanbul day, as the time beside it, whatever the browser's clock says.
export function formatJobTime(value: string, now: Date = new Date()) {
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return { time: '—', day: '—' }
  const sameDay = istanbulDateKey(date) === istanbulDateKey(now)
  return {
    time: formatIstanbul(date, { hour: '2-digit', minute: '2-digit', second: '2-digit' }),
    day: sameDay ? 'Bugün' : formatIstanbul(date, { day: '2-digit', month: '2-digit', year: 'numeric' })
  }
}

function formatOptionalJobTime(value: string | null) {
  if (!value) return '—'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '—' : formatDateTime(date)
}

type JobTimeRange = JobListTimeRange

type JobsIconName = 'refresh' | JobTypeIconName

function JobsIcon({ name }: { name: JobsIconName }) {
  const iconNames: Record<JobsIconName, UiIconName> = {
    refresh: 'refresh',
    price: 'tag',
    order: 'orders',
    invoice: 'invoice',
    return: 'returns',
    product: 'products',
    connection: 'connect',
    generic: 'spark',
  }
  return <UiIcon name={iconNames[name]} className={`jobs-reference-icon jobs-reference-icon-${name}`} size={18} />
}

function JobScanSummary({ scan }: { scan: JobScan }) {
  return <section className={`jobs-reference-scan-summary jobs-reference-scan-${scan.mode.toLocaleLowerCase('tr-TR')}`} aria-labelledby="job-scan-title">
    <div className="jobs-reference-scan-content">
      <div>
        <span className="jobs-reference-section-kicker">Tarama Türü</span>
        <h3 id="job-scan-title">{scan.label}{scan.detail ? <TitleHint label={scan.label}>{scan.detail}</TitleHint> : null}</h3>
      </div>
      <div className="jobs-reference-scan-facts">
        {scan.window && <p><small>Kapsam:</small><strong>{scan.window}</strong></p>}
        <p><small>Planlanan Aralık:</small><strong>{scan.plannedIntervalLabel ?? 'Manuel / isteğe bağlı'}</strong></p>
        {scan.actualIntervalLabel && <p><small>Önceki Taramadan Geçen:</small><strong>{scan.actualIntervalLabel}</strong></p>}
        {scan.previousScheduledAt && <p><small>Önceki Planlama:</small><strong>{formatOptionalJobTime(scan.previousScheduledAt)}</strong></p>}
      </div>
    </div>
  </section>
}

// A store-wide product or catalog read; a job about one product card (publish, update, archive, approval check) is not.
function isProductSyncJob(job: JobSummary) {
  return /PRODUCT|CATALOG/i.test(job.jobType) && !isProductLevelJob(job.jobType)
}

function formatJobCount(value: number) {
  return value.toLocaleString('tr-TR')
}

function jobStatusDescription(job: JobSummary) {
  if (job.status === 'SUCCEEDED') return 'İşlem başarıyla tamamlandı; aşağıdaki özet, yapılan çalışmanın kapsamını gösterir.'
  if (job.status === 'LEASED') return job.heartbeatAt
    ? 'Worker işlemi hâlâ yürütüyor. Son deneme süresi ve worker sinyali aşağıdaki bilgilerde güncel görünür.'
    : 'İşlem şu anda çalışıyor. Son denemenin başlangıç zamanı aşağıda gösterilir.'
  if (isWaitingJob(job)) return waitingJobDescription(job)
  if (job.status === 'PENDING' || job.status === 'RETRY_SCHEDULED') return 'İşlem kuyruğa alındı ve çalıştırılmak için bekliyor.'
  if (job.status === 'CANCELLED') return 'İşlem iptal edildi; dış pazaryerine yeni bir istek gönderilmiyor.'
  if (job.status === 'MANUAL_REVIEW') return 'İşlem manuel inceleme bekliyor. Ayrıntıları kontrol edip yeniden deneyebilirsiniz.'
  if (job.status === 'DEAD') return 'İşlem deneme limitine ulaştı ve tamamlanamadı.'
  if (job.status === 'BLOCKED') return 'İşlem engellendi ve otomatik olarak devam etmiyor.'
  return 'İşlem durumu ve yürütme ayrıntıları aşağıda gösterilir.'
}

function JobStatusSummary({ job }: { job: JobSummary }) {
  const tone = jobStateTone(job)
  const lastEventAt = job.status === 'LEASED'
    ? job.heartbeatAt ?? job.lastAttemptStartedAt ?? job.startedAt ?? job.createdAt
    : job.completedAt ?? job.startedAt ?? job.createdAt
  return <section className={`jobs-reference-status-hero jobs-reference-status-hero-${tone}`} aria-labelledby="job-status-title">
    <div>
      <span className="jobs-reference-section-kicker">İşlem Durumu</span>
      <h3 id="job-status-title"><JobStateGlyph tone={tone} />{jobStateLabel(job)}</h3>
      <p>{jobStatusDescription(job)}</p>
    </div>
    <div className="jobs-reference-status-hero-time">
      <small>{job.status === 'LEASED' ? 'Son worker sinyali' : 'Son kayıt'}</small>
      <strong>{formatOptionalJobTime(lastEventAt)}</strong>
    </div>
  </section>
}

function JobProgressSummary({ job, read }: { job: JobSummary; read: JobReadSummary | null }) {
  const productSync = isProductSyncJob(job)
  const counts = readProgressCounts(job, read)
  const received = counts.received
  // A read that came back empty writes its label ("Pazaryerinden yeni kayıt gelmedi"), so it shows too.
  const hasCounters = productSync || Boolean(read) || received > 0 || job.progressProcessed > 0 || job.progressSkipped > 0 || job.progressFailed > 0 || job.progressTotal !== null || Boolean(job.progressLabel)
  if (!hasCounters) return null
  const percent = jobProgressPercent(job)
  const totalLabel = job.progressTotal === null
    ? job.status === 'SUCCEEDED' && received > 0 ? `${formatJobCount(received)} kayıt değerlendirildi` : 'Toplam kapsam henüz kesinleşmedi'
    : `${formatJobCount(job.progressTotal)} kayıtlık kapsam`
  return <section className="jobs-reference-progress-summary" aria-labelledby="job-progress-title">
    <div className="jobs-reference-progress-heading">
      <div>
        <span className="jobs-reference-section-kicker">{productSync ? 'Ürün Aktarım Özeti' : 'İşlem İlerlemesi'}</span>
        <h3 id="job-progress-title">Pazaryerinden Alınan Kayıtların Sonucu</h3>
      </div>
      <strong>{percent === null ? '—' : `%${percent}`}</strong>
    </div>
    {job.progressLabel && <p className="jobs-reference-progress-label">{job.progressLabel}</p>}
    <div className="jobs-reference-progress-track" role="progressbar" aria-label="İşlem ilerlemesi" aria-valuemin={0} aria-valuemax={100} aria-valuenow={percent ?? 0}>
      <span style={{ width: `${percent ?? 0}%` }} />
    </div>
    <small className="jobs-reference-progress-total">{totalLabel}</small>
    <div className="jobs-reference-progress-grid">
      <p><small>Okunan / Çekilen</small><strong>{formatJobCount(received)}</strong><span>Pazaryerinden alınan</span></p>
      {counts.inserted === null
        ? <p><small>İşlenen</small><strong>{formatJobCount(counts.processed)}</strong><span>Yeni eklenen veya güncellenen</span></p>
        : <><p><small>Yeni</small><strong>{formatJobCount(counts.inserted)}</strong><span>Panele ilk kez eklenen</span></p><p><small>Güncellenen</small><strong>{formatJobCount(counts.updated ?? 0)}</strong><span>Durumu veya paketi değişen</span></p></>}
      <p><small>Değişmeyen</small><strong>{formatJobCount(counts.unchanged)}</strong><span>Panelde zaten güncel olan</span></p>
      <p><small>Atlanan</small><strong>{formatJobCount(counts.skipped)}</strong><span>Uygulanmayan kayıt</span></p>
      <p><small>Hatalı</small><strong>{formatJobCount(counts.failed)}</strong><span>İşlenemeyen kayıt</span></p>
    </div>
  </section>
}

function JobFailureReasons({ job, reasons }: { job: JobSummary; reasons: JobFailureReason[] | null }) {
  if (!isProductSyncJob(job) || (job.progressFailed === 0 && !reasons?.length)) return null
  const explained = reasons?.reduce((total, reason) => total + reason.affectedRecords, 0) ?? 0
  const unclassified = Math.max(0, job.progressFailed - explained)
  return <section className="jobs-reference-failure-reasons" aria-labelledby="job-failure-reasons-title">
    <div className="jobs-reference-section-heading"><div><span className="jobs-reference-section-kicker">Hata İncelemesi</span><h3 id="job-failure-reasons-title">Ürünler Neden Aktarılamadı?</h3></div><span>{formatJobCount(job.progressFailed)} başarısız ürün</span></div>
    {reasons?.length ? <>
      <div className="jobs-reference-failure-reason-list">{reasons.map(reason => <article key={reason.key}>
        <div className="jobs-reference-failure-reason-heading"><strong>{reason.title}</strong><b>{formatJobCount(reason.affectedRecords)} ürün</b></div>
        <p>{reason.description}</p>
        {reason.sampleProductIds.length > 0 && <small>Örnek pazaryeri ürün no: {reason.sampleProductIds.join(', ')}</small>}
        <details><summary>Teknik Ayrıntı</summary><code>{reason.technicalDetail}</code></details>
      </article>)}</div>
      {unclassified > 0 && <p className="jobs-reference-failure-unclassified">{formatJobCount(unclassified)} ürün için ayrıntılı hata kaydı henüz bulunamadı. Hata sayısı, içe aktarılamayan ürün grupları ilerledikçe artar.</p>}
    </> : <p className="jobs-reference-failure-unclassified">Hata sayısı, ürünlerin model grupları veritabanına kaydedilirken yükseliyor. Bu işlem için ayrıntılı neden kaydı bulunamadı; sonraki denemelerde nedenler burada gösterilecek.</p>}
  </section>
}

type JobDetailQueryState = { isLoading: boolean; isError: boolean; data: JobDetail | undefined }
type JobActionState = { isPending: boolean; isError: boolean; variables?: { id: string; verb: 'retry' | 'cancel' }; mutate: (variables: { id: string; verb: 'retry' | 'cancel' }) => void }
type OneTimeInvoiceDeliveryActionState = { isPending: boolean; isError: boolean; mutate: (variables: { id: string; confirmed: boolean; orderNumber: string }) => void }

function JobDetailDrawer({ selected, detail, selectedIsRunning, elevated, retryable, cancellable, action, oneTimeInvoiceDelivery, onClose }: { selected: JobSummary | undefined; detail: JobDetailQueryState; selectedIsRunning: boolean; elevated: boolean; retryable: boolean | undefined; cancellable: boolean | undefined; action: JobActionState; oneTimeInvoiceDelivery: OneTimeInvoiceDeliveryActionState; onClose: () => void }) {
  const [oneTimeDeliveryOpen, setOneTimeDeliveryOpen] = useState(false)
  const [oneTimeDeliveryConfirmed, setOneTimeDeliveryConfirmed] = useState(false)
  return <div className="job-detail-backdrop jobs-reference-drawer-backdrop" role="presentation" onMouseDown={onClose}><DialogSurface as="aside" onClose={onClose} className="job-detail-drawer jobs-reference-drawer panel" aria-labelledby="job-detail-title" onMouseDown={event => event.stopPropagation()}>
    <div className="jobs-reference-drawer-header">
      <div>
        <h2 id="job-detail-title">{selected ? jobTitle(selected.jobType, selected.marketplace) : 'İşlem Ayrıntısı'}</h2>
        <p>{selected ? `${jobSubtitle(selected.jobType)}${selected.batchCount > 1 ? ` · Toplu işlem (${selected.batchCount} job)` : ''}` : 'İşlem ayrıntısı yükleniyor'}</p>
      </div>
      <button type="button" className="jobs-reference-drawer-close" aria-label="Detay panelini kapat" onClick={onClose}><UiIcon name="close" /></button>
    </div>
    {detail.isLoading ? <div className="jobs-reference-drawer-body"><Busy text="İşlem ayrıntısı yükleniyor…" /></div> : detail.isError || !detail.data ? <div role="alert" className="jobs-reference-state jobs-reference-state-error">İşlem ayrıntısı alınamadı.</div> : (() => {
      const job = detail.data.job
      const change = detail.data.change ?? fallbackJobChange(job.jobType)
      const relatedLinks = jobRelatedLinks(detail.data)
      const waiting = isWaitingJob(job)
      const hasError = job.status !== 'SUCCEEDED' && !waiting && Boolean(job.lastErrorCode || job.lastErrorSummary)
      const invoice = detail.data.invoice
      const canSendOneTimeInvoice = canOfferOneTimeInvoiceDelivery({ elevated, jobType: job.jobType, jobStatus: job.status, lastErrorCode: job.lastErrorCode, attempts: detail.data.attempts, invoice })
      // A retry or cancel still on its way locks (and a failed one is reported on) the job it was for only.
      const actionOnThisJob = action.variables?.id === job.id
      const actionPending = action.isPending && actionOnThisJob
      // A job about one product card shows the product, what each request carried and the answer per variant instead.
      const productDetail = isProductLevelJob(job.jobType) && Boolean(detail.data.write?.productTitle || detail.data.variantResults)
      return <div className="jobs-reference-drawer-body">
        <JobStatusSummary job={job} />
        {hasError && <div className="jobs-reference-error-alert"><strong>{job.lastErrorSummary ?? 'İşlem tamamlanamadı; nedeni kaydedilmedi.'}</strong>{job.lastErrorCode && <span>Kod: {job.lastErrorCode}</span>}</div>}
        {selectedIsRunning && actionPending && <p className="jobs-reference-cancel-note">Çalışan işlem durduruluyor. Dış API çağrısı tamamlanana kadar durum birkaç saniye daha “Çalışıyor” görünebilir.</p>}
        {productDetail ? <JobWriteDetail write={detail.data.write ?? null} variantResults={detail.data.variantResults ?? null} approvalCheck={job.jobType.toUpperCase() === 'PRODUCT_APPROVAL_RECONCILE'} running={!['SUCCEEDED', 'DEAD', 'CANCELLED'].includes(job.status)} links={relatedLinks} note={detail.data.write?.parts.length ? null : change.detail} /> : <section className="jobs-reference-change-summary" aria-labelledby="job-change-title"><div><span className="jobs-reference-section-kicker">İşlem Özeti</span><h3 id="job-change-title">{change.value}</h3></div><div><strong>{change.label}</strong><p>{change.detail ?? 'İşlem ayrıntısı mevcut.'}</p></div>{relatedLinks.length > 0 && <nav className="jobs-reference-related-links" aria-label="İlgili Kayıtlar">{relatedLinks.map(link => <Link key={link.href} to={link.href}>{link.label}</Link>)}</nav>}</section>}
        {!productDetail && detail.data.write && <JobSentDetail write={detail.data.write} />}
        {detail.data.invoice && <section className="jobs-reference-order-context" aria-labelledby="job-invoice-context-title"><div><span className="jobs-reference-section-kicker">Fatura Denemesi</span><h3 id="job-invoice-context-title">Sipariş #{detail.data.invoice.orderNumber}</h3></div><div className="jobs-reference-order-facts"><p><small>Fatura Kaydı</small><strong>{detail.data.invoice.invoiceId}</strong></p><p><small>Fatura Durumu</small><strong>{statusLabel(detail.data.invoice.status)}</strong></p><p><small>Fatura Türü</small><strong>{detail.data.invoice.invoiceType}</strong></p><p><small>Fatura Tutarı</small><strong>{formatMoney(detail.data.invoice.payableTotal, detail.data.invoice.currency)}</strong></p>{detail.data.invoice.invoiceNumber && <p><small>Fatura Numarası</small><strong>{detail.data.invoice.invoiceNumber}</strong></p>}{detail.data.invoice.externalPackageId && <p><small>Paket No</small><strong>{formatPackageLabel(detail.data.invoice.externalPackageId)}</strong></p>}</div></section>}
        {!productDetail && <JobProgressSummary job={job} read={detail.data.read ?? null} />}
        {!productDetail && <JobReadRecords read={detail.data.read ?? null} recordsKept={keepsReadRecords(job.jobType)} />}
        <JobFailureReasons job={job} reasons={detail.data.failureReasons ?? null} />
        {canSendOneTimeInvoice && invoice && <section className="jobs-reference-order-context jobs-reference-one-time-invoice"><div><span className="jobs-reference-section-kicker">Tek Seferlik Dış İşlem</span><h3>Faturayı Hepsiburada’ya İlet<TitleHint label="Faturayı Hepsiburada’ya İlet">Bu işlem yalnız bu faturayı gönderir; fiyat, stok, sipariş veya iade yazma kapılarını açmaz.</TitleHint></h3><p>Sipariş #{invoice.orderNumber} · Fatura {invoice.invoiceNumber} · Paket {formatPackageLabel(invoice.externalPackageId)}</p>{!oneTimeDeliveryOpen ? <button type="button" disabled={oneTimeInvoiceDelivery.isPending} onClick={() => setOneTimeDeliveryOpen(true)}>Tek Seferlik Gönderimi Hazırla</button> : <form onSubmit={event => { event.preventDefault(); if (!oneTimeDeliveryConfirmed) return; oneTimeInvoiceDelivery.mutate({ id: job.id, confirmed: true, orderNumber: invoice.orderNumber }) }}><p>Hepsiburada canlı ortamındaki bu siparişe mevcut faturanın PDF bağlantısı iletilecek. Belirsiz bir yanıt olursa sistem otomatik tekrar göndermeyecek.</p><label className="check"><input type="checkbox" checked={oneTimeDeliveryConfirmed} onChange={event => setOneTimeDeliveryConfirmed(event.target.checked)} /> Yalnızca #{invoice.orderNumber} siparişine bu faturanın iletilmesini onaylıyorum.</label><div className="job-detail-actions"><button type="submit" disabled={oneTimeInvoiceDelivery.isPending || !oneTimeDeliveryConfirmed}>{oneTimeInvoiceDelivery.isPending ? 'Kuyruğa Alınıyor…' : 'Faturayı Bir Kez İlet'}</button><button type="button" className="secondary" disabled={oneTimeInvoiceDelivery.isPending} onClick={() => { setOneTimeDeliveryOpen(false); setOneTimeDeliveryConfirmed(false) }}>Vazgeç</button></div></form>}{oneTimeInvoiceDelivery.isError && <div role="alert" className="error">Tek seferlik fatura işlemi başlatılamadı. Hata ayrıntısı için işlemi yeniden yükleyin.</div>}</div></section>}
        {detail.data.scan && <JobScanSummary scan={detail.data.scan} />}
        {job.batchCount > 1 ? <section className="jobs-reference-batch-context" aria-labelledby="job-batch-title"><div className="jobs-reference-batch-heading"><div><span className="jobs-reference-section-kicker">Toplu İşlem</span><h3 id="job-batch-title">{job.batchCount} İş · {detail.data.relatedOrders.length} Sipariş</h3></div><span className="jobs-reference-batch-note">Sonuçlar sipariş bazında</span></div><div className="jobs-reference-batch-list">{detail.data.relatedOrders.map(order => <article key={order.orderId}><div><strong>Sipariş #{order.orderNumber}</strong><small>{order.customerName ?? 'Müşteri bilgisi yok'} · {order.lineCount} ürün satırı</small></div><span>{order.cargoProvider ?? 'Kargo bilgisi yok'}</span><b>{statusLabel(order.status)}</b></article>)}{detail.data.relatedOrders.length === 0 && <p>Sipariş bağlantısı bulunamadı.</p>}</div></section> : detail.data.order && <section className="jobs-reference-order-context" aria-labelledby="job-order-context-title"><div><span className="jobs-reference-section-kicker">İlgili Sipariş</span><h3 id="job-order-context-title">Sipariş #{detail.data.order.orderNumber}</h3><p>{detail.data.order.customerName ?? 'Müşteri bilgisi yok'} · {detail.data.order.lineCount} ürün satırı</p></div><div className="jobs-reference-order-facts"><p><small>Dış Sipariş ID</small><strong>{detail.data.order.externalOrderId}</strong></p><p><small>Sipariş Durumu</small><strong>{statusLabel(detail.data.order.status)}</strong></p><p><small>Sipariş Tarihi</small><strong>{formatOptionalJobTime(detail.data.order.orderedAt)}</strong></p><p><small>Sipariş Tutarı</small><strong>{formatMoney(detail.data.order.netAmount, detail.data.order.currency)}</strong></p>{detail.data.order.externalPackageId && <p><small>Paket No</small><strong>{formatPackageLabel(detail.data.order.externalPackageId)}</strong></p>}{detail.data.order.cargoTrackingNumber && <p><small>Kargo Takip No</small><strong>{detail.data.order.cargoTrackingNumber}</strong></p>}</div></section>}
        <section className="jobs-reference-facts-section" aria-labelledby="job-facts-title"><div className="jobs-reference-section-heading"><div><span className="jobs-reference-section-kicker">Kayıt Ayrıntıları</span><h3 id="job-facts-title">Teknik Bilgiler</h3></div><span>İşlemin kimliği ve yürütme zamanları</span></div><div className="job-detail-facts"><p><small>Pazaryeri</small><strong>{job.marketplace}</strong></p><p><small>İşlem</small><strong>{jobTypeCode(job.jobType, job.marketplace)}</strong></p><p><small>Dış Kimlik</small><strong>{job.externalId ?? '—'}</strong></p><p><small>Deneme Sayısı</small><strong>{jobAttemptsText(job)}</strong></p>{(job.waitingChecks ?? 0) > 0 && <p><small>Durum Kontrolü</small><strong>{job.waitingChecks}</strong></p>}<p><small>Oluşturulma</small><strong>{formatOptionalJobTime(job.createdAt)}</strong></p><p><small>İlk Çalışma Başlangıcı</small><strong>{formatOptionalJobTime(job.startedAt)}</strong></p><p><small>Son Deneme Başlangıcı</small><strong>{formatOptionalJobTime(job.lastAttemptStartedAt ?? job.startedAt)}</strong></p>{job.status === 'LEASED' && <p><small>Son Worker Sinyali</small><strong>{formatOptionalJobTime(job.heartbeatAt)}</strong></p>}<p><small>Tamamlanma</small><strong>{formatOptionalJobTime(job.completedAt)}</strong></p><p><small>Son Deneme Süresi</small><strong>{jobAttemptDuration(job)}</strong></p><p><small>İlk Hata</small><strong>{formatOptionalJobTime(job.firstFailedAt)}</strong></p><p><small>Son Hata</small><strong>{formatOptionalJobTime(job.lastFailedAt)}</strong></p><p><small>{waiting ? 'Sonraki Kontrol' : 'Sonraki Deneme'}</small><strong>{formatOptionalJobTime(job.nextRetryAt)}</strong></p><p><small>Korelasyon No</small><strong>{job.correlationId}</strong></p></div></section>
        {elevated && <div className="job-detail-actions">{retryable && <button type="button" disabled={actionPending} onClick={() => action.mutate({ id: job.id, verb: 'retry' })}>Manuel Yeniden Dene</button>}{cancellable && <button type="button" className="secondary" disabled={actionPending} onClick={() => action.mutate({ id: job.id, verb: 'cancel' })}>{actionPending ? selectedIsRunning ? 'Durduruluyor…' : 'İptal Ediliyor…' : selectedIsRunning ? 'Durdur' : 'İptal Et'}</button>}</div>}
        {action.isError && actionOnThisJob && <div role="alert" className="error">İşlem güncellenemedi.</div>}
        <section className="jobs-reference-attempts-section" aria-labelledby="job-attempts-title"><div className="jobs-reference-section-heading"><div><span className="jobs-reference-section-kicker">Yürütme Geçmişi</span><h3 id="job-attempts-title">Deneme Geçmişi</h3></div><span>{detail.data.attempts.length} kayıt</span></div><div className="table-wrap"><table><thead><tr><th>#</th><th>Başlangıç</th><th>Sonuç</th><th>Ayrıntı</th></tr></thead><tbody>{detail.data.attempts.map(attempt => <tr key={attempt.attemptNumber} data-outcome={jobAttemptOutcome(attempt).toLowerCase()}><td>{attempt.attemptNumber}</td><td>{formatDateTime(attempt.startedAt)}</td><td>{jobAttemptResultLabel(attempt)}</td><td>{attempt.errorCode ?? '—'}<small>{attempt.errorSummary ?? ''}</small></td></tr>)}{detail.data.attempts.length === 0 && <tr><td colSpan={4}>Henüz deneme yok.</td></tr>}</tbody></table></div></section>
      </div>
    })()}
  </DialogSurface></div>
}

// The categories are the second level under İş Takibi: one segmented strip of equal text buttons (the dashboard's period
// picker), every category in view with a quiet count; on a narrow screen the strip scrolls sideways.
function JobCategorySegment({ items: givenItems, value, onChange }: { items: Array<{ value: string; label: string; count?: number }>; value: string; onChange: (value: string) => void }) {
  const items = useRememberedTabCounts('İşlem kategorileri', givenItems)
  const strip = useRef<HTMLDivElement>(null)
  // A category opened from a link may sit past the edge of a phone's strip: scroll the strip (only it) to show it.
  useEffect(() => {
    const element = strip.current
    const active = element?.querySelector<HTMLElement>('[aria-selected="true"]')
    if (!element || !active || element.scrollWidth <= element.clientWidth) return
    const box = element.getBoundingClientRect()
    const tab = active.getBoundingClientRect()
    if (tab.left < box.left || tab.right > box.right) element.scrollLeft += tab.left - box.left - (box.width - tab.width) / 2
  }, [value])
  return <div ref={strip} className="jobs-category-segment" role="tablist" aria-label="İşlem kategorileri">{items.map((item, index) => <button key={item.value} type="button" role="tab" tabIndex={item.value === value ? 0 : -1} aria-selected={item.value === value} className={item.value === value ? 'is-active' : undefined} onClick={() => onChange(item.value)} onKeyDown={event => {
    const next = event.key === 'Home' ? 0 : event.key === 'End' ? items.length - 1 : event.key === 'ArrowRight' ? (index + 1) % items.length : event.key === 'ArrowLeft' ? (index - 1 + items.length) % items.length : -1
    if (next < 0) return
    event.preventDefault()
    onChange(items[next].value)
    event.currentTarget.parentElement?.querySelectorAll<HTMLButtonElement>('[role="tab"]')[next]?.focus()
  }}><span className="jobs-category-segment-label" data-text={item.label}>{item.label}</span>{item.count !== undefined && <span className="jobs-category-segment-count">{item.count}</span>}</button>)}</div>
}

export function JobsPage({ me }: { me: Me }) {
  const client = useQueryClient()
  // Filters and the page live in the address (jobs-url-state.ts): Back, a refresh or a link opens the same view.
  // ?durum=hata opens the list with the error filter on (the overview's İncele link uses it); ?platform=trendyol keeps
  // the platforms (?platform=trendyol,N11), which the server applies to the list and the counts.
  const [searchParams, setSearchParams] = useSearchParams()
  const view = readJobsViewState(searchParams)
  const { category, direction, status, search, timeRange, pageSize, page: pageNumber } = view
  const statusChips = view.chips
  const platforms = jobPlatformsFromSearch(searchParams.get('platform'))
  // The server's comma list, or null for every platform; it also keys the list and the counts.
  const platform = platforms.length ? platforms.join(',') : null
  const updateView = (patch: Partial<JobsViewState>) => setSearchParams(current => writeJobsViewState(current, patch), { replace: true })
  // Three main tabs sit above the page body: İş Takibi (the job list with its category tabs), Loglar (the price/stock
  // send log) and API Sağlık (the price/stock sending health per connection, with the emergency stop). Going back to İş
  // Takibi reopens the category the address still holds; the job list is not read while another tab is open.
  const mainTab = jobsMainTab(searchParams)
  const listHidden = mainTab !== 'jobs'
  const setMainTab = (next: string) => setSearchParams(current => withJobsMainTab(current, next as JobsMainTab), { replace: true })
  const showPanelFeedback = (next: IntegrationFeedback) => setFeedback({ message: next.message, tone: next.kind === 'error' ? 'danger' : next.kind, context: { ...next.context, title: next.title ?? next.context?.title } })
  const setCategory = (next: string) => updateView({ category: next as JobCategory })
  const setStatus = (next: '' | JobStatus) => updateView({ status: next })
  const setDirection = (next: '' | JobDirection) => updateView({ direction: next })
  const setSearch = (next: string) => updateView({ search: next })
  const setTimeRange = (next: JobTimeRange) => updateView({ timeRange: next })
  const setPageSize = (next: number) => updateView({ pageSize: next })
  const setPageNumber = (next: number | ((current: number) => number)) => updateView({ page: typeof next === 'function' ? next(pageNumber) : next })
  const setStatusChips = (next: (current: JobStatusChip[]) => JobStatusChip[]) => updateView({ chips: next(statusChips) })
  const withPlatforms = (current: URLSearchParams, codes: readonly string[]) => {
    const next = new URLSearchParams(current)
    if (codes.length) next.set('platform', codes.map(code => code.toLowerCase()).join(','))
    else next.delete('platform')
    next.delete('sayfa')
    return next
  }
  const setPlatforms = (codes: string[]) => setSearchParams(current => withPlatforms(current, codes), { replace: true })
  // Temizle: the card's fields back to their defaults (the status counts above the list stay as they are).
  const clearFilters = () => setSearchParams(current => withPlatforms(writeJobsViewState(current, { direction: '', status: '', search: '', timeRange: '24h' }), []), { replace: true })
  const connections = useQuery({ queryKey: ['connections', 'job-platforms'], queryFn: () => loadAllPages<{ platformCode: string }>('/connections'), staleTime: 60_000 })
  const connectionPlatforms = connections.data?.items
  const platformOptions = useMemo(() => jobPlatformOptions((connectionPlatforms ?? []).map(connection => connection.platformCode), platform ? platform.split(',') : []), [connectionPlatforms, platform])
  const platformGroups = useMemo<FilterMenuGroup[]>(() => [{ options: platformOptions.map(code => ({ value: code, label: jobPlatformLabel(code), mark: <RrLogo code={code} /> })) }], [platformOptions])
  const cardFilterCount = [platforms.length > 0, Boolean(direction), Boolean(status), timeRange !== '24h'].filter(Boolean).length
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [feedback, setFeedback] = useState<{ message: string; tone: 'success' | 'danger' | 'info'; context?: NotificationContext } | null>(null)
  useEffect(() => {
    if (!feedback) return
    appendNotification({ ...feedback.context, message: feedback.message, kind: feedback.tone === 'danger' ? 'error' : feedback.tone })
    const timeout = window.setTimeout(() => setFeedback(null), 5500)
    return () => window.clearTimeout(timeout)
  }, [feedback])
  const elevated = ['OWNER', 'ADMINISTRATOR'].includes((me.role ?? '').toUpperCase())
  const serverStatus = status || [...statusChips].sort().flatMap(chip => jobChipStatuses[chip]).join(',')
  // The list holds only the jobs loaded so far, so the counts come from the server's real totals; until they arrive
  // the loaded list is counted instead.
  const serverCounts = useQuery({
    queryKey: ['jobs', 'counts', timeRange, platform],
    // Not read while the Loglar or API Sağlık tab is shown (the list itself is off then too).
    enabled: !listHidden,
    queryFn: () => hubPoll<Array<{ status: JobStatus; jobType: string; count: number }>>(`/jobs/counts${jobCountsQueryString(timeRange, platform)}`),
    refetchInterval: jobListPollMs
  })
  // The tab's job types come from its fixed definition, never from the live counts, so a count refresh that brings a
  // new type cannot change the list key and send the list back to page 1 (G22).
  // Okuma / Yazma / Bildirim / Panel İçi narrows the tab's job types further; when no type is left, a type no job has
  // keeps the server list empty instead of unfiltered.
  const categoryJobTypes = useMemo(() => {
    const byCategory = jobTypesForCategoryTab(category)
    if (!direction) return byCategory
    const byDirection = jobTypesForDirection(direction)
    const both = byCategory ? byDirection.filter(type => byCategory.includes(type)) : byDirection
    return both.length ? both : ['NO_MATCHING_JOB_TYPE']
  }, [category, direction])
  const list = useInfiniteQuery({
    queryKey: ['jobs', 'page', serverStatus, timeRange, category, direction, platform],
    queryFn: context => hubPoll<JobPage>(`/jobs/page${jobPageQueryString(serverStatus, timeRange, { jobTypes: categoryJobTypes, after: context.pageParam, platform })}`),
    initialPageParam: null as string | null,
    getNextPageParam: lastPage => lastPage.hasMore && lastPage.nextCursor ? lastPage.nextCursor : undefined,
    placeholderData: keepPreviousData,
    // The job list is not read while the Loglar tab shows the send log.
    enabled: !listHidden,
    refetchInterval: query => jobListPollInterval(query.state.data?.pages.length ?? 0)
  })
  // With older pages loaded the list itself stops polling; its first page is polled on its own and put in front.
  const loadedServerPages = list.data?.pages.length ?? 0
  const listHead = useQuery({
    queryKey: ['jobs', 'page-head', serverStatus, timeRange, category, direction, platform],
    queryFn: () => hubPoll<JobPage>(`/jobs/page${jobPageQueryString(serverStatus, timeRange, { jobTypes: categoryJobTypes, platform })}`),
    enabled: loadedServerPages > 1 && !listHidden,
    refetchInterval: jobListHeadPollInterval(loadedServerPages)
  })
  useEffect(() => {
    if (listHead.data) client.setQueryData<InfiniteData<JobPage, string | null>>(['jobs', 'page', serverStatus, timeRange, category, direction, platform], current => withFreshFirstPage(current, listHead.data))
  }, [listHead.data, client, serverStatus, timeRange, category, direction, platform])
  const detail = useQuery({
    queryKey: ['job', selectedId],
    queryFn: () => hubPoll<JobDetail>(`/jobs/${selectedId}`),
    enabled: Boolean(selectedId),
    refetchInterval: query => selectedId ? jobDetailPollInterval(query.state.data?.job.status) : false
  })
  const action = useMutation({
    mutationFn: ({ id, verb }: { id: string; verb: 'retry' | 'cancel' }) => hubApi<JobDetail>(`/jobs/${id}/${verb}`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey(`${verb}-${id}`) } }),
    onSuccess: async (data, variables) => {
      // The answer moves the drawer only while it still shows the job the action was for: another job opened in the
      // meantime stays open.
      setSelectedId(current => current === variables.id ? data.job.id : current)
      const title = jobTitle(data.job.jobType, data.job.marketplace)
      setFeedback({ message: variables.verb === 'retry' ? `${title} yeniden kuyruğa alındı.` : `${title} iptal edildi.`, tone: variables.verb === 'retry' ? 'info' : 'success', context: { ...jobNotificationContext(data.job), title: variables.verb === 'retry' ? 'İşlem Yeniden Denenecek' : 'İşlem İptal Edildi' } })
      await Promise.all([client.invalidateQueries({ queryKey: ['jobs'] }), client.invalidateQueries({ queryKey: ['job', data.job.id] })])
    },
    onError: (error, variables) => {
      const failure = notificationError(error, 'İşlem güncellenemedi.')
      const job = rawJobs.find(candidate => candidate.id === variables.id)
      setFeedback({ message: failure.message, tone: 'danger', context: { ...(job ? jobNotificationContext(job) : { jobId: variables.id }), title: variables.verb === 'retry' ? 'İşlem Yeniden Denenemedi' : 'İşlem İptal Edilemedi', code: failure.code } })
    }
  })
  const oneTimeInvoiceDelivery = useMutation({
    mutationFn: ({ id, confirmed, orderNumber }: { id: string; confirmed: boolean; orderNumber: string }) => hubApi<JobDetail>(`/jobs/${id}/invoice-delivery-once`, {
      method: 'POST',
      headers: { 'Idempotency-Key': oneTimeInvoiceDeliveryIdempotencyKey(id) },
      body: JSON.stringify({ confirmed, orderNumber })
    }),
    onSuccess: async data => {
      setSelectedId(data.job.id)
      setFeedback({ message: 'Seçilen sipariş için tek seferlik fatura iletimi kuyruğa alındı. Genel dış yazma kapıları kapalı kaldı.', tone: 'info' })
      await Promise.all([client.invalidateQueries({ queryKey: ['jobs'] }), client.invalidateQueries({ queryKey: ['job', data.job.id] })])
    },
    onError: error => setFeedback({ message: error instanceof Error ? error.message : 'Tek seferlik fatura işlemi başlatılamadı.', tone: 'danger' })
  })
  // A term that can be one identifier is also looked up by the server, so a job is found by its id, correlation id or
  // marketplace id even when it is older than the loaded pages or the selected range (notification links: ?ara=<job id>).
  const lookupTerm = listHidden ? null : jobLookupTerm(search)
  const lookup = useQuery({
    queryKey: ['jobs', 'lookup', lookupTerm],
    queryFn: () => hubPoll<JobPage>(`/jobs/page${jobLookupQueryString(lookupTerm ?? '')}`),
    enabled: Boolean(lookupTerm)
  })
  const lookupItems = useMemo(() => lookupTerm ? lookup.data?.items ?? [] : [], [lookupTerm, lookup.data])
  const lookupIds = useMemo(() => new Set(lookupItems.map(job => job.id)), [lookupItems])
  // A whole job id opens its detail once the lookup found it (once per term, so closing the drawer keeps it closed).
  const openedLookup = useRef<string | null>(null)
  useEffect(() => {
    if (!lookupTerm || !isJobIdSearch(lookupTerm) || openedLookup.current === lookupTerm) return
    const job = lookupItems.find(item => item.id.toLowerCase() === lookupTerm.toLowerCase())
    if (!job) return
    openedLookup.current = lookupTerm
    setSelectedId(job.id)
  }, [lookupTerm, lookupItems])
  const rawJobs = useMemo(() => {
    // A refetch can shift rows between pages while new jobs arrive, so a job is shown once.
    const seen = new Set<string>()
    const loaded = (list.data?.pages ?? []).flatMap(page => page.items)
    const jobs = [...loaded, ...lookupItems].filter(job => !seen.has(job.id) && Boolean(seen.add(job.id)))
    return lookupItems.length ? jobs.sort((left, right) => Date.parse(right.createdAt) - Date.parse(left.createdAt)) : jobs
  }, [list.data, lookupItems])
  const rangeFiltered = useMemo(() => {
    if (timeRange === 'all') return rawJobs
    const now = Date.now()
    return rawJobs.filter(job => lookupIds.has(job.id) || isJobInTimeRange(job, timeRange, now))
  }, [rawJobs, timeRange, lookupIds])
  const filtered = useMemo(() => {
    const term = search.trim().toLocaleLowerCase('tr-TR')
    const categoryMatcher = jobCategoryMatcher(category)
    return rangeFiltered.filter(job => {
      const matchCategory = categoryMatcher(job.jobType)
      if (!matchCategory) return false
      if (direction && jobPresentation(job.jobType).direction !== direction) return false
      if (statusChips.length && !statusChips.includes(jobStatusChip(job.status))) return false
      if (!term || lookupIds.has(job.id)) return true
      return [job.id, job.jobType, job.status, job.lastErrorCode, job.lastErrorSummary, job.correlationId, job.externalId].some(value => value?.toLocaleLowerCase('tr-TR').includes(term))
    })
  }, [rangeFiltered, category, direction, search, statusChips, lookupIds])
  // A count toggles its filter; several can be on at once and tapping one again takes it off.
  const statusChipProps = (chip: JobStatusChip) => {
    const on = statusChips.includes(chip)
    const toggle = () => setStatusChips(current => current.includes(chip) ? current.filter(item => item !== chip) : [...current, chip])
    return { role: 'button', tabIndex: 0, 'aria-pressed': on, className: `${chip}${on ? ' is-on' : ''}`, onClick: toggle, onKeyDown: (event: React.KeyboardEvent) => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); toggle() } } } as const
  }
  const totalPages = Math.max(1, Math.ceil(filtered.length / pageSize))
  const currentPage = Math.min(pageNumber, totalPages)
  const pageJobs = filtered.slice((currentPage - 1) * pageSize, currentPage * pageSize)
  const moreOnServer = Boolean(list.hasNextPage)
  // A ?sayfa=N link: read server pages until page N has rows (or the server has no more).
  useEffect(() => {
    if (!listHidden && shouldLoadTowardsPage(pageNumber, totalPages, moreOnServer, loadedServerPages, list.isFetching)) void list.fetchNextPage()
  }, [listHidden, pageNumber, totalPages, moreOnServer, loadedServerPages, list])
  // Reads the next server page; from the last screen page the view moves on to the newly loaded rows.
  const loadMoreJobs = async () => {
    if (!moreOnServer || list.isFetchingNextPage) return
    const wasLastPage = currentPage === totalPages
    const result = await list.fetchNextPage()
    if (result.isError) { setFeedback({ message: result.error instanceof Error ? result.error.message : 'İşlem listesinin devamı alınamadı.', tone: 'danger' }); return }
    if (wasLastPage && pageJobs.length === pageSize) setPageNumber(currentPage + 1)
  }
  const goToNextPage = () => {
    if (currentPage < totalPages) setPageNumber(currentPage + 1)
    else void loadMoreJobs()
  }
  const selected = detail.data?.job
  const retryable = selected && ['BLOCKED', 'MANUAL_REVIEW', 'DEAD'].includes(selected.status)
  const cancellable = selected && !['SUCCEEDED', 'DEAD', 'CANCELLED'].includes(selected.status)
  const selectedIsRunning = selected?.status === 'LEASED'
  const countRows = useMemo(() => {
    const rows = serverCounts.data ?? rangeFiltered.map(job => ({ status: job.status, jobType: job.jobType, count: 1 }))
    return direction ? rows.filter(row => jobPresentation(row.jobType).direction === direction) : rows
  }, [serverCounts.data, rangeFiltered, direction])
  const statusSummary = useMemo(() => {
    const summary = { success: 0, running: 0, waiting: 0, error: 0, cancelled: 0 }
    for (const row of countRows) summary[jobStatusChip(row.status)] += row.count
    return summary
  }, [countRows])
  const [successCount, runningCount, waitingCount, errorCount] = useRememberedTabCounts('İşlem durum özeti', (['success', 'running', 'waiting', 'error'] as const).map(key => ({ value: key, count: list.isLoading ? undefined : statusSummary[key] })))
  const categoryCounts = useMemo(() => new Map(jobCategoryTabs.map(tab => [tab.key, countRows.reduce((total, row) => total + (tab.match(row.jobType) ? row.count : 0), 0)])), [countRows])
  const refreshJobs = () => {
    void Promise.all([
      client.invalidateQueries({ queryKey: ['jobs'] }),
      selectedId ? client.invalidateQueries({ queryKey: ['job', selectedId] }) : Promise.resolve()
    ])
  }
  // A main tab slides its view in from its side and starts the page from its top (shared/tab-slide.tsx).
  const mainSlide = useTabSlide(mainTab, jobsMainTabOrder)

  return <section className="content jobs-page jobs-reference-page">
    <div className="jobs-reference-heading rv-page-head">
      <div>
        <p className="eyebrow">Sistem İzleme</p>
        <h1>İşlem Takibi</h1>
      </div>
    </div>
    <Tabs className="rv-view-tabs jobs-reference-main-tabs" ariaLabel="İşlem Takibi görünümü" value={mainTab} onChange={setMainTab} items={[{ value: 'jobs', label: viewTabLabel('processing', 'İş Takibi') }, { value: 'logs', label: viewTabLabel('log', 'Loglar') }, { value: 'health', label: viewTabLabel('connected', 'API Sağlık') }]} />
    <div key={mainSlide.key} className={`jobs-reference-canvas${mainSlide.className ? ` ${mainSlide.className}` : ''}`}>
      {!listHidden && <JobCategorySegment value={category} onChange={setCategory} items={jobCategoryTabs.filter(tab => tab.key !== 'SYSTEM').map(tab => ({ value: tab.key, label: tab.label, count: list.isLoading ? undefined : categoryCounts.get(tab.key) ?? 0 }))} />}
      {mainTab === 'logs' ? <Suspense fallback={<Busy text="Loglar yükleniyor…" />}><OperationLogs platformOptions={platformOptions} onOpenJob={setSelectedId} /></Suspense>
        : mainTab === 'health' ? <Suspense fallback={<Busy text="API sağlığı yükleniyor…" />}><ApiHealthView onFeedback={showPanelFeedback} /></Suspense> : <>
      <OpsFilterCard label="İşlem filtreleri" active={cardFilterCount} clearCount={cardFilterCount + (search.trim() ? 1 : 0)} onClear={clearFilters} onApply={refreshJobs}>
        <SearchField label="İşlem Ara" ariaLabel="Correlation ID ile işlem ara" placeholder="Correlation ID, iş numarası veya hata kodu" value={search} onChange={setSearch} />
        <ChoiceField name="Zaman Aralığı" value={timeRange} options={jobTimeRangeOptions(timeRange)} onChange={value => setTimeRange(value as JobTimeRange)} />
        <FilterMenu name="Platform" allLabel="Tüm Platformlar" unit="Platform" groups={platformGroups} selected={platforms} onChange={setPlatforms} emptyText="Bağlı pazaryeri yok." />
        <ChoiceField name="Yön" allValue="" value={direction} options={[{ value: '', label: 'Tüm Yönler' }, ...jobDirections.map(item => ({ value: item, label: item }))]} onChange={value => setDirection(value as '' | JobDirection)} />
        <ChoiceField name="Durum" allValue="" value={status} options={statuses} onChange={value => setStatus(value as '' | JobStatus)} />
      </OpsFilterCard>
      <div className="jobs-reference-toolbar">
        <div className="jobs-reference-status-summary" aria-label="İşlem durum özeti">
          <span {...statusChipProps('success')}><JobStateGlyph tone="success" />Başarılı <strong>{successCount.count ?? 0}</strong></span>
          <span {...statusChipProps('running')}><JobStateGlyph tone="running" />Çalışıyor <strong>{runningCount.count ?? 0}</strong></span>
          <span {...statusChipProps('waiting')}><JobStateGlyph tone="waiting" />Bekliyor <strong>{waitingCount.count ?? 0}</strong></span>
          <span {...statusChipProps('error')}><JobStateGlyph tone="error" />Hata <strong>{errorCount.count ?? 0}</strong></span>
          {/* Always shown like the others: appearing once the counts arrive pushed the table down on phones. */}
          <span {...statusChipProps('cancelled')}><JobStateGlyph tone="cancelled" />İptal <strong>{statusSummary.cancelled}</strong></span>
        </div>
        <div className="jobs-reference-toolbar-actions">
          <span className="rr ops-psize"><RrPageSize value={pageSize} sizes={[...jobPageSizes]} onChange={setPageSize} /></span>
          <button type="button" className="jobs-reference-refresh" title="Yenile" aria-label="İşlemleri yenile" onClick={refreshJobs}><JobsIcon name="refresh" /></button>
        </div>
      </div>
      {list.isLoading ? <Busy text="İşlemler yükleniyor…" /> : list.isError && !list.data ? <div role="alert" className="jobs-reference-state jobs-reference-state-error">İşlem listesi alınamadı.</div> : <>
        <div className="jobs-reference-table-scroll"><table className="jobs-reference-table"><thead><tr><th>İşlem Türü</th><th>Durum</th><th>Deneme</th><th>Zaman</th><th>Correlation ID</th></tr></thead><tbody>
          {pageJobs.map(job => {
            const presentation = jobPresentation(job.jobType)
            const time = formatJobTime(job.createdAt)
            const typeDescriptionId = `job-type-description-${job.id}`
            return <tr className="jobs-reference-row" key={job.id} aria-describedby={typeDescriptionId} onClick={() => setSelectedId(job.id)} tabIndex={0} onKeyDown={event => { if (event.target === event.currentTarget && (event.key === 'Enter' || event.key === ' ')) { event.preventDefault(); setSelectedId(job.id) } }}>
              <td><div className="jobs-reference-type"><span className="jobs-reference-type-icon" aria-hidden="true"><JobsIcon name={presentation.icon} /></span><span className="jobs-reference-type-content"><strong>{jobTitle(job.jobType, job.marketplace)}</strong><small>{jobSubtitle(job.jobType)}{job.externalId ? ` · ${job.externalId}` : ''}{job.batchCount > 1 ? ` · ${job.batchCount} kayıt birlikte` : ''}</small><span id={typeDescriptionId} className="jobs-reference-type-tooltip" role="tooltip">{presentation.description}</span></span></div></td>
              <td><span className={`jobs-reference-status ${jobStateTone(job)}`}><JobStateGlyph tone={jobStateTone(job)} />{jobStateLabel(job)}</span></td>
              <td className="jobs-reference-attempt">{jobAttemptsText(job)}</td>
              <td><div className="jobs-reference-time"><strong>{time.time}</strong><small>{time.day} · {job.attemptCount > 0 ? `son deneme ${jobAttemptDuration(job)}` : 'çalışma başlamadı'}</small></div></td>
              <td><span className="jobs-reference-correlation">{job.correlationId}</span></td>
            </tr>
          })}
          {filtered.length === 0 && <tr><td className="jobs-reference-empty" colSpan={5}><EmptyState title="Kayıt Bulunamadı">{moreOnServer ? 'Yüklenen kayıtlarda eşleşen işlem yok. Daha eski işlemler için Daha Fazla Yükle’ye basın.' : 'Seçili kategori ve filtrelerle eşleşen kayıt bulunamadı.'}</EmptyState></td></tr>}
        </tbody></table></div>
        {(filtered.length > 0 || moreOnServer) && <div className="jobs-reference-pagination"><strong>{moreOnServer ? `${filtered.length.toLocaleString('tr-TR')} kayıt yüklendi, daha eskileri var` : `Toplam ${filtered.length.toLocaleString('tr-TR')} kayıt`}</strong><div className="jobs-reference-pagination-actions">{moreOnServer && <Button variant="secondary" className="jobs-reference-load-more" loading={list.isFetchingNextPage} onClick={() => void loadMoreJobs()}>Daha Fazla Yükle</Button>}<Pagination className="jobs-reference-page-controls" page={currentPage} totalPages={totalPages} hasNext={currentPage < totalPages || moreOnServer} disabled={list.isFetchingNextPage} onPageChange={setPageNumber} onPrevious={() => setPageNumber(value => Math.max(1, value - 1))} onNext={goToNextPage} /></div></div>}
      </>}
      </>}
    </div>
     {selectedId && <JobDetailDrawer key={selectedId} selected={selected} detail={detail} selectedIsRunning={Boolean(selectedIsRunning)} elevated={elevated} retryable={retryable} cancellable={cancellable} action={action} oneTimeInvoiceDelivery={oneTimeInvoiceDelivery} onClose={() => setSelectedId(null)} />}
     {feedback && <NoticeToast key={`${feedback.tone}:${feedback.message}`} kind={feedback.tone === 'danger' ? 'error' : feedback.tone} title={feedback.context?.title} message={feedback.message} onClose={() => setFeedback(null)} durationMs={5500} />}
  </section>
}
