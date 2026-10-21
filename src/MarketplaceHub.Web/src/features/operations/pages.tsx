import { useEffect, useMemo, useState } from 'react'
import { keepPreviousData, useInfiniteQuery, useMutation, useQuery, useQueryClient, type InfiniteData } from '@tanstack/react-query'
import { useSearchParams } from 'react-router'
import { hubApi, hubPoll, loadAllPages, type Me } from '../../shared/api'
import { PlatformSquareMark } from '../../shared/platform-square-mark'
import { marketplacePlatformLabel } from '../marketplace/marketplace-platform-support'
import { Button, Pagination, UiIcon, type UiIconName, DialogSurface } from '../../shared/components'
import { Busy } from '../../shared/components/AsyncState'
import { NoticeToast } from '../../shared/components/NoticeToast'
import { appendNotification } from '../../shared/notifications'
import { statusLabel } from '../../shared/status-labels'
import { marketplaceQuestionSyncChange } from './marketplace-question-sync'
import { catalogStructureImportChange } from './catalog-structure-import'
import { jobPresentation, jobSubtitle, jobTitle, jobTypeCode, type JobTypeIconName } from './job-presentation'
import { canOfferOneTimeInvoiceDelivery } from './one-time-invoice-delivery-visibility'
import { oneTimeInvoiceDeliveryIdempotencyKey } from './one-time-invoice-delivery-idempotency'
import { jobCountsQueryString, jobDetailPollInterval, jobListHeadPollInterval, jobListPollInterval, jobListPollMs, jobPageQueryString, jobPlatformFromSearch, jobPlatformOptions, shouldLoadTowardsPage, withFreshFirstPage, type JobListTimeRange } from './job-list-query'
import { jobCategoryMatcher, jobCategoryTabs, jobTypesForCategoryTab, type JobCategory } from './job-categories'
import { jobPageSizes, readJobsViewState, writeJobsViewState, type JobStatusChip, type JobsViewState } from './jobs-url-state'

// E-Faturam is the invoicing provider; every other code is a marketplace.
const jobPlatformLabel = (code: string) => code === 'TRENDYOL_EFATURAM' ? 'E-Faturam' : marketplacePlatformLabel(code)
import { JobQueueMetrics } from './JobQueueMetrics'
import './jobs-responsive.css'
import './jobs-sade.css'
import { ViewTabsWithMore, viewTabLabel } from '../../shared/view-tabs'
import { useRememberedTabCounts } from '../../shared/tab-counts'
import { formatDateTime, formatIstanbul } from '../../shared/format-date'
import { formatMoney } from '../../shared/format-money'
import { newIdempotencyKey } from '../../shared/idempotency'
import { jobAttemptDuration } from './job-duration'

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
}
type JobOrderContext = { orderId: string; orderNumber: string; externalOrderId: string; status: string; currency: string; netAmount: number; orderedAt: string; externalPackageId: string | null; cargoProvider: string | null; cargoTrackingNumber: string | null; customerName: string | null; lineCount: number }
type JobInvoiceContext = { invoiceId: string; orderNumber: string; status: string; invoiceType: string; currency: string; payableTotal: number; invoiceNumber: string | null; externalPackageId: string | null }
type JobChange = { label: string; value: string; detail: string | null }
type JobScan = { mode: string; label: string; detail: string; window: string | null; plannedIntervalSeconds: number | null; plannedIntervalLabel: string | null; previousScheduledAt: string | null; actualIntervalLabel: string | null }
type JobFailureReason = { key: string; title: string; description: string; technicalDetail: string; affectedRecords: number; sampleProductIds: string[] }
type JobDetail = { job: JobSummary; attempts: Array<{ attemptNumber: number; startedAt: string; completedAt: string | null; succeeded: boolean; errorCode: string | null; errorSummary: string | null }>; order: JobOrderContext | null; change: JobChange | null; relatedOrders: JobOrderContext[]; scan: JobScan | null; failureReasons: JobFailureReason[] | null; invoice: JobInvoiceContext | null }
type JobPage = { items: JobSummary[]; nextCursor: string | null; hasMore: boolean }

const statuses: Array<{ value: '' | JobStatus; label: string }> = [
  { value: '', label: 'Tüm durumlar' },
  { value: 'PENDING', label: 'Bekliyor' },
  { value: 'LEASED', label: 'Çalışıyor' },
  { value: 'RETRY_SCHEDULED', label: 'Tekrar denenecek' },
  { value: 'BLOCKED', label: 'Engellendi' },
  { value: 'MANUAL_REVIEW', label: 'Manuel inceleme' },
  { value: 'SUCCEEDED', label: 'Başarılı' },
  { value: 'DEAD', label: 'Deneme limiti doldu' },
  { value: 'CANCELLED', label: 'İptal edildi' }
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

type JobGlyphState = 'success' | 'running' | 'waiting' | 'error' | 'cancelled'

// The shared state glyphs: filled check, turning arc while running, clock arc while waiting, red cross, dashed circle.
function JobStateGlyph({ tone }: { tone: string }) {
  const state: JobGlyphState = tone === 'success' || tone === 'running' || tone === 'error' || tone === 'cancelled' ? tone : 'waiting'
  if (state === 'success') return <svg className="jb-glyph is-success" viewBox="0 0 16 16" aria-hidden="true"><circle cx="8" cy="8" r="7" fill="currentColor" /><path d="m5 8.2 2.1 2.1 3.9-4.2" fill="none" stroke="var(--rv-color-surface)" strokeWidth={1.7} strokeLinecap="round" strokeLinejoin="round" /></svg>
  if (state === 'running') return <svg className="jb-glyph is-running" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.8} strokeLinecap="round" aria-hidden="true"><circle cx="8" cy="8" r="6" opacity=".25" /><path d="M8 2a6 6 0 0 1 6 6" /></svg>
  if (state === 'error') return <svg className="jb-glyph is-error" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.6} strokeLinecap="round" aria-hidden="true"><circle cx="8" cy="8" r="6.2" /><path d="m5.8 5.8 4.4 4.4M10.2 5.8l-4.4 4.4" /></svg>
  if (state === 'cancelled') return <svg className="jb-glyph is-cancelled" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.6} strokeDasharray="2.6 2.2" aria-hidden="true"><circle cx="8" cy="8" r="6.2" /></svg>
  return <svg className="jb-glyph is-waiting" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.6} strokeLinecap="round" aria-hidden="true"><circle cx="8" cy="8" r="6.2" opacity=".35" /><path d="M8 1.8a6.2 6.2 0 0 1 5.9 4.3" /><path d="M8 5.6v2.6l1.8 1.1" /></svg>
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

function fallbackJobChange(job: JobSummary): JobChange {
  const type = job.jobType.toUpperCase()
  if (type === 'MARKETPLACE_QUESTION_SYNC') return marketplaceQuestionSyncChange
  if (type === 'CATALOG_STRUCTURE_IMPORT') return catalogStructureImportChange
  if (type === 'STOCK_PROJECTION_DISPATCH') return { label: 'İşlem türü', value: 'Stok gönderim hazırlığı', detail: 'Bu adım dış API’ye yazmaz. Dış yazma açıksa ayrı bir fiyat-stok gönderim işi oluşturur.' }
  if (type === 'STOCK_RECONCILIATION') return { label: 'İşlem türü', value: 'Stok uzlaştırma taraması', detail: 'Bu kayıt tek başına pazaryerine stok gönderildiği anlamına gelmez.' }
  if (type === 'PRICE_INVENTORY_SYNC') return { label: 'İşlem türü', value: 'Fiyat ve stok gönderimi', detail: 'Dış yazma kapalıysa Trendyol’a yeni gönderim yapılmaz.' }
  if (type.includes('SHIPMENT_ACTION')) return { label: 'Yapılan değişiklik', value: 'Paket işlemi', detail: 'Paket işlemi Trendyol’a gönderildi.' }
  if (type === 'ORDER_STATUS_SYNC') return { label: 'Tarama türü', value: 'Sipariş durum taraması', detail: 'Açık siparişlerin paket ve taşıma durumları kontrol edilerek yerel durum güncellenir.' }
  if (type === 'TRENDYOL_ORDER_CARGO_INFO_RECONCILIATION') return { label: 'Tarama türü', value: 'Trendyol kargo bilgisi taraması', detail: 'Teslim edilmiş paketlerin kargo bilgileri ayrı, salt okunur bir taramada yenilenir.' }
  if (type === 'ORDER_RECONCILIATION') return { label: 'Tarama türü', value: 'Kapsamlı sipariş taraması', detail: 'Yerel siparişler ile pazaryeri kayıtları karşılaştırılır; durum ve paket farklılıkları düzeltilir.' }
  if (type === 'ORDER_INVOICE_RECONCILIATION') return { label: 'Tarama türü', value: 'Paket fatura taraması', detail: 'Teslim edilmiş ve açık paketlerin pazaryeri fatura durumu kontrol edilir.' }
  if (type === 'TRENDYOL_ORDER_RECOVERY_SYNC') return { label: 'Tarama türü', value: 'Tam sipariş taraması', detail: 'Erişilebilen sipariş pencereleri taranarak eksik yerel kayıtlar tamamlanır.' }
  if (type === 'TRENDYOL_ORDER_SYNC') return { label: 'Yapılan değişiklik', value: 'Sipariş senkronizasyonu', detail: 'Sipariş bilgileri pazaryerinden eşitlendi.' }
  if (type.includes('REFERENCE_SYNC')) return { label: 'Yapılan işlem', value: type.startsWith('HEPSIBURADA_') ? 'Hepsiburada kategori ve eşleştirme verisi' : type.startsWith('N11_') ? 'N11 kategori ve eşleştirme verisi' : type.startsWith('PTTAVM_') ? 'PTT AVM kategori verisi' : type.startsWith('PAZARAMA_') ? 'Pazarama kategori, özellik ve marka verisi' : 'Trendyol kategori ve eşleştirme verisi', detail: 'Kategori, özellik ve eşleştirme referansları salt okunur yenilendi; dış platforma veri gönderilmedi.' }
  if (type.includes('PRODUCT') || type.includes('CATALOG')) return { label: 'Yapılan değişiklik', value: 'Ürün senkronizasyonu', detail: 'Ürün bilgileri pazaryerine gönderildi.' }
  if (type.includes('INVOICE')) return { label: 'Yapılan değişiklik', value: 'Fatura işlemi', detail: 'Fatura isteği pazaryerine gönderildi.' }
  return { label: 'Yapılan değişiklik', value: jobPresentation(job.jobType).action, detail: 'İşlem isteği sisteme kaydedildi.' }
}

function formatJobTime(value: string) {
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return { time: '—', day: '—' }
  const now = new Date()
  const sameDay = date.getFullYear() === now.getFullYear() && date.getMonth() === now.getMonth() && date.getDate() === now.getDate()
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

function timeRangeLabel(value: JobTimeRange) {
  if (value === '7d') return 'Son 7 Gün'
  if (value === 'all') return 'Tüm Zamanlar'
  return 'Son 24 Saat'
}

type JobsIconName = 'calendar' | 'chevron-down' | 'filter' | 'refresh' | 'search' | JobTypeIconName

function JobsIcon({ name }: { name: JobsIconName }) {
  const iconNames: Record<JobsIconName, UiIconName> = {
    calendar: 'calendar',
    'chevron-down': 'chevronDown',
    filter: 'listSortAscending',
    refresh: 'refresh',
    search: 'search',
    price: 'box',
    order: 'orders',
    invoice: 'invoice',
    return: 'returns',
    product: 'bag',
    connection: 'connect',
    generic: 'spark',
  }
  return <UiIcon name={iconNames[name]} className={`jobs-reference-icon jobs-reference-icon-${name}`} size={18} />
}

function JobScanSummary({ scan }: { scan: JobScan }) {
  return <section className={`jobs-reference-scan-summary jobs-reference-scan-${scan.mode.toLocaleLowerCase('tr-TR')}`} aria-labelledby="job-scan-title">
    <div className="jobs-reference-scan-content">
      <div>
        <span className="jobs-reference-section-kicker">Tarama türü</span>
        <h3 id="job-scan-title">{scan.label}</h3>
        <p>{scan.detail}</p>
      </div>
      <div className="jobs-reference-scan-facts">
        {scan.window && <p><small>Kapsam:</small><strong>{scan.window}</strong></p>}
        <p><small>Planlanan aralık:</small><strong>{scan.plannedIntervalLabel ?? 'Manuel / isteğe bağlı'}</strong></p>
        {scan.actualIntervalLabel && <p><small>Önceki taramadan geçen:</small><strong>{scan.actualIntervalLabel}</strong></p>}
        {scan.previousScheduledAt && <p><small>Önceki planlama:</small><strong>{formatOptionalJobTime(scan.previousScheduledAt)}</strong></p>}
      </div>
    </div>
  </section>
}

function isProductSyncJob(job: JobSummary) {
  return /PRODUCT|CATALOG/i.test(job.jobType)
}

function jobProgressPercent(job: JobSummary) {
  if (job.progressPercent !== null) return Math.max(0, Math.min(100, job.progressPercent))
  if (job.progressTotal && job.progressTotal > 0) return Math.max(0, Math.min(100, Math.round((job.progressCurrent / job.progressTotal) * 100)))
  return null
}

function formatJobCount(value: number) {
  return value.toLocaleString('tr-TR')
}

function jobStatusDescription(job: JobSummary) {
  if (job.status === 'SUCCEEDED') return 'İşlem başarıyla tamamlandı; aşağıdaki özet, yapılan çalışmanın kapsamını gösterir.'
  if (job.status === 'LEASED') return job.heartbeatAt
    ? 'Worker işlemi hâlâ yürütüyor. Son deneme süresi ve worker sinyali aşağıdaki bilgilerde güncel görünür.'
    : 'İşlem şu anda çalışıyor. Son denemenin başlangıç zamanı aşağıda gösterilir.'
  if (job.status === 'PENDING' || job.status === 'RETRY_SCHEDULED') return 'İşlem kuyruğa alındı ve çalıştırılmak için bekliyor.'
  if (job.status === 'CANCELLED') return 'İşlem iptal edildi; dış pazaryerine yeni bir istek gönderilmiyor.'
  if (job.status === 'MANUAL_REVIEW') return 'İşlem manuel inceleme bekliyor. Ayrıntıları kontrol edip yeniden deneyebilirsiniz.'
  if (job.status === 'DEAD') return 'İşlem deneme limitine ulaştı ve tamamlanamadı.'
  if (job.status === 'BLOCKED') return 'İşlem engellendi ve otomatik olarak devam etmiyor.'
  return 'İşlem durumu ve yürütme ayrıntıları aşağıda gösterilir.'
}

function JobStatusSummary({ job }: { job: JobSummary }) {
  const tone = jobStatusTone(job.status)
  const lastEventAt = job.status === 'LEASED'
    ? job.heartbeatAt ?? job.lastAttemptStartedAt ?? job.startedAt ?? job.createdAt
    : job.completedAt ?? job.startedAt ?? job.createdAt
  return <section className={`jobs-reference-status-hero jobs-reference-status-hero-${tone}`} aria-labelledby="job-status-title">
    <div>
      <span className="jobs-reference-section-kicker">İşlem durumu</span>
      <h3 id="job-status-title"><JobStateGlyph tone={tone} />{jobStatusLabel(job.status)}</h3>
      <p>{jobStatusDescription(job)}</p>
    </div>
    <div className="jobs-reference-status-hero-time">
      <small>{job.status === 'LEASED' ? 'Son worker sinyali' : 'Son kayıt'}</small>
      <strong>{formatOptionalJobTime(lastEventAt)}</strong>
    </div>
  </section>
}

function JobProgressSummary({ job }: { job: JobSummary }) {
  const productSync = isProductSyncJob(job)
  const received = Math.max(job.progressReceived, job.progressCurrent)
  const hasCounters = productSync || received > 0 || job.progressProcessed > 0 || job.progressSkipped > 0 || job.progressFailed > 0 || job.progressTotal !== null
  if (!hasCounters) return null
  const percent = jobProgressPercent(job)
  const totalLabel = job.progressTotal === null
    ? job.status === 'SUCCEEDED' && received > 0 ? `${formatJobCount(received)} kayıt değerlendirildi` : 'Toplam kapsam henüz kesinleşmedi'
    : `${formatJobCount(job.progressTotal)} kayıtlık kapsam`
  return <section className="jobs-reference-progress-summary" aria-labelledby="job-progress-title">
    <div className="jobs-reference-progress-heading">
      <div>
        <span className="jobs-reference-section-kicker">{productSync ? 'Ürün aktarım özeti' : 'İşlem ilerlemesi'}</span>
        <h3 id="job-progress-title">Pazaryerinden alınan kayıtların sonucu</h3>
      </div>
      <strong>{percent === null ? '—' : `%${percent}`}</strong>
    </div>
    {job.progressLabel && <p className="jobs-reference-progress-label">{job.progressLabel}</p>}
    <div className="jobs-reference-progress-track" role="progressbar" aria-label="İşlem ilerlemesi" aria-valuemin={0} aria-valuemax={100} aria-valuenow={percent ?? 0}>
      <span style={{ width: `${percent ?? 0}%` }} />
    </div>
    <small className="jobs-reference-progress-total">{totalLabel}</small>
    <div className="jobs-reference-progress-grid">
      <p><small>Okunan / çekilen</small><strong>{formatJobCount(received)}</strong><span>Pazaryerinden alınan</span></p>
      <p><small>İşlenen</small><strong>{formatJobCount(job.progressProcessed)}</strong><span>Yerel kaydı güncellenen</span></p>
      <p><small>Atlanan</small><strong>{formatJobCount(job.progressSkipped)}</strong><span>Değişiklik gerektirmeyen</span></p>
      <p><small>Hatalı</small><strong>{formatJobCount(job.progressFailed)}</strong><span>İşlenemeyen kayıt</span></p>
    </div>
  </section>
}

function JobFailureReasons({ job, reasons }: { job: JobSummary; reasons: JobFailureReason[] | null }) {
  if (!isProductSyncJob(job) || (job.progressFailed === 0 && !reasons?.length)) return null
  const explained = reasons?.reduce((total, reason) => total + reason.affectedRecords, 0) ?? 0
  const unclassified = Math.max(0, job.progressFailed - explained)
  return <section className="jobs-reference-failure-reasons" aria-labelledby="job-failure-reasons-title">
    <div className="jobs-reference-section-heading"><div><span className="jobs-reference-section-kicker">Hata incelemesi</span><h3 id="job-failure-reasons-title">Ürünler neden aktarılamadı?</h3></div><span>{formatJobCount(job.progressFailed)} başarısız ürün</span></div>
    {reasons?.length ? <>
      <div className="jobs-reference-failure-reason-list">{reasons.map(reason => <article key={reason.key}>
        <div className="jobs-reference-failure-reason-heading"><strong>{reason.title}</strong><b>{formatJobCount(reason.affectedRecords)} ürün</b></div>
        <p>{reason.description}</p>
        {reason.sampleProductIds.length > 0 && <small>Örnek pazaryeri ürün no: {reason.sampleProductIds.join(', ')}</small>}
        <details><summary>Teknik ayrıntı</summary><code>{reason.technicalDetail}</code></details>
      </article>)}</div>
      {unclassified > 0 && <p className="jobs-reference-failure-unclassified">{formatJobCount(unclassified)} ürün için ayrıntılı hata kaydı henüz bulunamadı. Hata sayısı, içe aktarılamayan ürün grupları ilerledikçe artar.</p>}
    </> : <p className="jobs-reference-failure-unclassified">Hata sayısı, ürünlerin model grupları veritabanına kaydedilirken yükseliyor. Bu işlem için ayrıntılı neden kaydı bulunamadı; sonraki denemelerde nedenler burada gösterilecek.</p>}
  </section>
}

type JobDetailQueryState = { isLoading: boolean; isError: boolean; data: JobDetail | undefined }
type JobActionState = { isPending: boolean; isError: boolean; mutate: (variables: { id: string; verb: 'retry' | 'cancel' }) => void }
type OneTimeInvoiceDeliveryActionState = { isPending: boolean; isError: boolean; mutate: (variables: { id: string; confirmed: boolean; orderNumber: string }) => void }

function JobDetailDrawer({ selected, detail, selectedIsRunning, elevated, retryable, cancellable, action, oneTimeInvoiceDelivery, onClose }: { selected: JobSummary | undefined; detail: JobDetailQueryState; selectedIsRunning: boolean; elevated: boolean; retryable: boolean | undefined; cancellable: boolean | undefined; action: JobActionState; oneTimeInvoiceDelivery: OneTimeInvoiceDeliveryActionState; onClose: () => void }) {
  const [oneTimeDeliveryOpen, setOneTimeDeliveryOpen] = useState(false)
  const [oneTimeDeliveryConfirmed, setOneTimeDeliveryConfirmed] = useState(false)
  return <div className="job-detail-backdrop jobs-reference-drawer-backdrop" role="presentation" onMouseDown={onClose}><DialogSurface as="aside" onClose={onClose} className="job-detail-drawer jobs-reference-drawer panel" aria-labelledby="job-detail-title" onMouseDown={event => event.stopPropagation()}>
    <div className="jobs-reference-drawer-header">
      <div>
        <h2 id="job-detail-title">{selected ? jobTitle(selected.jobType, selected.marketplace) : 'İşlem ayrıntısı'}</h2>
        <p>{selected ? `${jobSubtitle(selected.jobType)}${selected.batchCount > 1 ? ` · Toplu işlem (${selected.batchCount} job)` : ''}` : 'İşlem ayrıntısı yükleniyor'}</p>
      </div>
      <button type="button" className="jobs-reference-drawer-close" aria-label="Detay panelini kapat" onClick={onClose}><UiIcon name="close" /></button>
    </div>
    {detail.isLoading ? <div className="jobs-reference-drawer-body"><Busy text="İşlem ayrıntısı yükleniyor…" /></div> : detail.isError || !detail.data ? <div role="alert" className="jobs-reference-state jobs-reference-state-error">İşlem ayrıntısı alınamadı.</div> : (() => {
      const job = detail.data.job
      const change = detail.data.change ?? fallbackJobChange(job)
      const hasError = job.status !== 'SUCCEEDED' && Boolean(job.lastErrorCode || job.lastErrorSummary)
      const invoice = detail.data.invoice
      const canSendOneTimeInvoice = canOfferOneTimeInvoiceDelivery({ elevated, jobType: job.jobType, jobStatus: job.status, lastErrorCode: job.lastErrorCode, attempts: detail.data.attempts, invoice })
      return <div className="jobs-reference-drawer-body">
        <JobStatusSummary job={job} />
        {hasError && <div className="jobs-reference-error-alert"><strong>{job.lastErrorCode ?? 'İşlem hatası'}</strong><span>{job.lastErrorSummary ?? 'İşlem başarısız oldu ancak ayrıntılı hata açıklaması kaydedilmedi.'}</span></div>}
        {selectedIsRunning && action.isPending && <p className="jobs-reference-cancel-note">Çalışan işlem durduruluyor. Dış API çağrısı tamamlanana kadar durum birkaç saniye daha “Çalışıyor” görünebilir.</p>}
        <section className="jobs-reference-change-summary" aria-labelledby="job-change-title"><div><span className="jobs-reference-section-kicker">İşlem özeti</span><h3 id="job-change-title">{change.value}</h3></div><div><strong>{change.label}</strong><p>{change.detail ?? 'İşlem ayrıntısı mevcut.'}</p></div></section>
        {detail.data.invoice && <section className="jobs-reference-order-context" aria-labelledby="job-invoice-context-title"><div><span className="jobs-reference-section-kicker">Fatura denemesi</span><h3 id="job-invoice-context-title">Sipariş #{detail.data.invoice.orderNumber}</h3><p>Bu e-Fatura işlemi seçili sipariş için başlatıldı.</p></div><div className="jobs-reference-order-facts"><p><small>Fatura Kaydı</small><strong>{detail.data.invoice.invoiceId}</strong></p><p><small>Fatura Durumu</small><strong>{statusLabel(detail.data.invoice.status)}</strong></p><p><small>Fatura Türü</small><strong>{detail.data.invoice.invoiceType}</strong></p><p><small>Fatura Tutarı</small><strong>{formatMoney(detail.data.invoice.payableTotal, detail.data.invoice.currency)}</strong></p>{detail.data.invoice.invoiceNumber && <p><small>Fatura Numarası</small><strong>{detail.data.invoice.invoiceNumber}</strong></p>}{detail.data.invoice.externalPackageId && <p><small>Paket No</small><strong>{detail.data.invoice.externalPackageId}</strong></p>}</div></section>}
        <JobProgressSummary job={job} />
        <JobFailureReasons job={job} reasons={detail.data.failureReasons ?? null} />
        {canSendOneTimeInvoice && invoice && <section className="jobs-reference-order-context jobs-reference-one-time-invoice"><div><span className="jobs-reference-section-kicker">Tek seferlik dış işlem</span><h3>Faturayı Hepsiburada’ya İlet</h3><p>Sipariş #{invoice.orderNumber} · Fatura {invoice.invoiceNumber} · Paket {invoice.externalPackageId}. Bu işlem yalnız bu faturayı gönderir; fiyat, stok, sipariş veya iade yazma kapılarını açmaz.</p>{!oneTimeDeliveryOpen ? <button type="button" disabled={oneTimeInvoiceDelivery.isPending} onClick={() => setOneTimeDeliveryOpen(true)}>Tek Seferlik Gönderimi Hazırla</button> : <form onSubmit={event => { event.preventDefault(); if (!oneTimeDeliveryConfirmed) return; oneTimeInvoiceDelivery.mutate({ id: job.id, confirmed: true, orderNumber: invoice.orderNumber }) }}><p>Hepsiburada canlı ortamındaki bu siparişe mevcut faturanın PDF bağlantısı iletilecek. Belirsiz bir yanıt olursa sistem otomatik tekrar göndermeyecek.</p><label className="check"><input type="checkbox" checked={oneTimeDeliveryConfirmed} onChange={event => setOneTimeDeliveryConfirmed(event.target.checked)} /> Yalnızca #{invoice.orderNumber} siparişine bu faturanın iletilmesini onaylıyorum.</label><div className="job-detail-actions"><button type="submit" disabled={oneTimeInvoiceDelivery.isPending || !oneTimeDeliveryConfirmed}>{oneTimeInvoiceDelivery.isPending ? 'Kuyruğa alınıyor…' : 'Faturayı Bir Kez İlet'}</button><button type="button" className="secondary" disabled={oneTimeInvoiceDelivery.isPending} onClick={() => { setOneTimeDeliveryOpen(false); setOneTimeDeliveryConfirmed(false) }}>Vazgeç</button></div></form>}{oneTimeInvoiceDelivery.isError && <div role="alert" className="error">Tek seferlik fatura işlemi başlatılamadı. Hata ayrıntısı için işlemi yeniden yükleyin.</div>}</div></section>}
        {detail.data.scan && <JobScanSummary scan={detail.data.scan} />}
        {job.batchCount > 1 ? <section className="jobs-reference-batch-context" aria-labelledby="job-batch-title"><div className="jobs-reference-batch-heading"><div><span className="jobs-reference-section-kicker">Toplu işlem</span><h3 id="job-batch-title">{job.batchCount} job · {detail.data.relatedOrders.length} sipariş</h3></div><span className="jobs-reference-batch-note">Sonuçlar sipariş bazında</span></div><div className="jobs-reference-batch-list">{detail.data.relatedOrders.map(order => <article key={order.orderId}><div><strong>Sipariş #{order.orderNumber}</strong><small>{order.customerName ?? 'Müşteri bilgisi yok'} · {order.lineCount} ürün satırı</small></div><span>{order.cargoProvider ?? 'Kargo bilgisi yok'}</span><b>{statusLabel(order.status)}</b></article>)}{detail.data.relatedOrders.length === 0 && <p>Sipariş bağlantısı bulunamadı.</p>}</div></section> : detail.data.order && <section className="jobs-reference-order-context" aria-labelledby="job-order-context-title"><div><span className="jobs-reference-section-kicker">İlgili sipariş</span><h3 id="job-order-context-title">Sipariş #{detail.data.order.orderNumber}</h3><p>{detail.data.order.customerName ?? 'Müşteri bilgisi yok'} · {detail.data.order.lineCount} ürün satırı</p></div><div className="jobs-reference-order-facts"><p><small>Dış Sipariş ID</small><strong>{detail.data.order.externalOrderId}</strong></p><p><small>Sipariş Durumu</small><strong>{statusLabel(detail.data.order.status)}</strong></p><p><small>Sipariş Tarihi</small><strong>{formatOptionalJobTime(detail.data.order.orderedAt)}</strong></p><p><small>Sipariş Tutarı</small><strong>{formatMoney(detail.data.order.netAmount, detail.data.order.currency)}</strong></p>{detail.data.order.externalPackageId && <p><small>Paket No</small><strong>{detail.data.order.externalPackageId}</strong></p>}{detail.data.order.cargoTrackingNumber && <p><small>Kargo Takip No</small><strong>{detail.data.order.cargoTrackingNumber}</strong></p>}</div></section>}
        <section className="jobs-reference-facts-section" aria-labelledby="job-facts-title"><div className="jobs-reference-section-heading"><div><span className="jobs-reference-section-kicker">Kayıt ayrıntıları</span><h3 id="job-facts-title">Teknik Bilgiler</h3></div><span>İşlemin kimliği ve yürütme zamanları</span></div><div className="job-detail-facts"><p><small>Pazaryeri</small><strong>{job.marketplace}</strong></p><p><small>İşlem</small><strong>{jobTypeCode(job.jobType, job.marketplace)}</strong></p><p><small>Dış Kimlik</small><strong>{job.externalId ?? '—'}</strong></p><p><small>Deneme Sayısı</small><strong>{job.attemptCount} / {job.maxAttempts}</strong></p><p><small>Oluşturulma</small><strong>{formatOptionalJobTime(job.createdAt)}</strong></p><p><small>İlk Çalışma Başlangıcı</small><strong>{formatOptionalJobTime(job.startedAt)}</strong></p><p><small>Son Deneme Başlangıcı</small><strong>{formatOptionalJobTime(job.lastAttemptStartedAt ?? job.startedAt)}</strong></p>{job.status === 'LEASED' && <p><small>Son Worker Sinyali</small><strong>{formatOptionalJobTime(job.heartbeatAt)}</strong></p>}<p><small>Tamamlanma</small><strong>{formatOptionalJobTime(job.completedAt)}</strong></p><p><small>Son Deneme Süresi</small><strong>{jobAttemptDuration(job)}</strong></p><p><small>İlk Hata</small><strong>{formatOptionalJobTime(job.firstFailedAt)}</strong></p><p><small>Son Hata</small><strong>{formatOptionalJobTime(job.lastFailedAt)}</strong></p><p><small>Sonraki Deneme</small><strong>{formatOptionalJobTime(job.nextRetryAt)}</strong></p><p><small>Correlation ID</small><strong>{job.correlationId}</strong></p></div></section>
        {elevated && <div className="job-detail-actions">{retryable && <button type="button" disabled={action.isPending} onClick={() => action.mutate({ id: job.id, verb: 'retry' })}>Manuel Yeniden Dene</button>}{cancellable && <button type="button" className="secondary" disabled={action.isPending} onClick={() => action.mutate({ id: job.id, verb: 'cancel' })}>{action.isPending ? selectedIsRunning ? 'Durduruluyor…' : 'İptal ediliyor…' : selectedIsRunning ? 'Durdur' : 'İptal Et'}</button>}</div>}
        {action.isError && <div role="alert" className="error">İşlem güncellenemedi.</div>}
        <section className="jobs-reference-attempts-section" aria-labelledby="job-attempts-title"><div className="jobs-reference-section-heading"><div><span className="jobs-reference-section-kicker">Yürütme geçmişi</span><h3 id="job-attempts-title">Deneme Geçmişi</h3></div><span>{detail.data.attempts.length} kayıt</span></div><div className="table-wrap"><table><thead><tr><th>#</th><th>Başlangıç</th><th>Sonuç</th><th>Hata</th></tr></thead><tbody>{detail.data.attempts.map(attempt => <tr key={attempt.attemptNumber}><td>{attempt.attemptNumber}</td><td>{formatDateTime(attempt.startedAt)}</td><td>{attempt.completedAt ? (attempt.succeeded ? 'Başarılı' : 'Başarısız') : 'Çalışıyor'}</td><td>{attempt.errorCode ?? '—'}<small>{attempt.errorSummary ?? ''}</small></td></tr>)}{detail.data.attempts.length === 0 && <tr><td colSpan={4}>Henüz deneme yok.</td></tr>}</tbody></table></div></section>
      </div>
    })()}
  </DialogSurface></div>
}

export function JobsPage({ me }: { me: Me }) {
  const client = useQueryClient()
  const [filterOpen, setFilterOpen] = useState(false)
  const [timeRangeOpen, setTimeRangeOpen] = useState(false)
  // Filters and the page live in the address (jobs-url-state.ts): Back, a refresh or a link opens the same view.
  // ?durum=hata opens the list with the error filter on (the overview's İncele link uses it); ?platform=trendyol keeps
  // the platform, which the server applies to the list and the counts.
  const [searchParams, setSearchParams] = useSearchParams()
  const view = readJobsViewState(searchParams)
  const { category, status, search, timeRange, pageSize, page: pageNumber } = view
  const statusChips = view.chips
  const platform = jobPlatformFromSearch(searchParams.get('platform'))
  const updateView = (patch: Partial<JobsViewState>) => setSearchParams(current => writeJobsViewState(current, patch), { replace: true })
  const setCategory = (next: JobCategory) => updateView({ category: next })
  const setStatus = (next: '' | JobStatus) => updateView({ status: next })
  const setSearch = (next: string) => updateView({ search: next })
  const setTimeRange = (next: JobTimeRange) => updateView({ timeRange: next })
  const setPageSize = (next: number) => updateView({ pageSize: next })
  const setPageNumber = (next: number | ((current: number) => number)) => updateView({ page: typeof next === 'function' ? next(pageNumber) : next })
  const setStatusChips = (next: (current: JobStatusChip[]) => JobStatusChip[]) => updateView({ chips: next(statusChips) })
  const setPlatform = (code: string | null) => setSearchParams(current => {
    const next = new URLSearchParams(current)
    if (code) next.set('platform', code.toLowerCase())
    else next.delete('platform')
    next.delete('sayfa')
    return next
  }, { replace: true })
  const connections = useQuery({ queryKey: ['connections', 'job-platforms'], queryFn: () => loadAllPages<{ platformCode: string }>('/connections'), staleTime: 60_000 })
  const platformOptions = useMemo(() => jobPlatformOptions((connections.data?.items ?? []).map(connection => connection.platformCode), platform), [connections.data, platform])
  useEffect(() => {
    if (!filterOpen && !timeRangeOpen) return
    const closeOnOutsidePointer = (event: PointerEvent) => {
      if (!(event.target instanceof Element)) return
      if (!event.target.closest('.jobs-reference-range-wrap') && !event.target.closest('.jobs-reference-filter-wrap')) {
        setFilterOpen(false)
        setTimeRangeOpen(false)
      }
    }
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setFilterOpen(false)
        setTimeRangeOpen(false)
      }
    }
    document.addEventListener('pointerdown', closeOnOutsidePointer)
    document.addEventListener('keydown', closeOnEscape)
    return () => {
      document.removeEventListener('pointerdown', closeOnOutsidePointer)
      document.removeEventListener('keydown', closeOnEscape)
    }
  }, [filterOpen, timeRangeOpen])
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [feedback, setFeedback] = useState<{ message: string; tone: 'success' | 'danger' | 'info' } | null>(null)
  useEffect(() => {
    if (!feedback) return
    appendNotification(feedback.message, feedback.tone === 'danger' ? 'error' : feedback.tone)
    const timeout = window.setTimeout(() => setFeedback(null), 5500)
    return () => window.clearTimeout(timeout)
  }, [feedback])
  const elevated = ['OWNER', 'ADMINISTRATOR'].includes((me.role ?? '').toUpperCase())
  const serverStatus = status || [...statusChips].sort().flatMap(chip => jobChipStatuses[chip]).join(',')
  // The list holds only the jobs loaded so far, so the counts come from the server's real totals; until they arrive
  // the loaded list is counted instead.
  const serverCounts = useQuery({
    queryKey: ['jobs', 'counts', timeRange, platform],
    queryFn: () => hubPoll<Array<{ status: JobStatus; jobType: string; count: number }>>(`/jobs/counts${jobCountsQueryString(timeRange, platform)}`),
    refetchInterval: jobListPollMs
  })
  // The tab's job types come from its fixed definition, never from the live counts, so a count refresh that brings a
  // new type cannot change the list key and send the list back to page 1 (G22).
  const categoryJobTypes = jobTypesForCategoryTab(category)
  const list = useInfiniteQuery({
    queryKey: ['jobs', 'page', serverStatus, timeRange, category, platform],
    queryFn: context => hubPoll<JobPage>(`/jobs/page${jobPageQueryString(serverStatus, timeRange, { jobTypes: categoryJobTypes, after: context.pageParam, platform })}`),
    initialPageParam: null as string | null,
    getNextPageParam: lastPage => lastPage.hasMore && lastPage.nextCursor ? lastPage.nextCursor : undefined,
    placeholderData: keepPreviousData,
    refetchInterval: query => jobListPollInterval(query.state.data?.pages.length ?? 0)
  })
  // With older pages loaded the list itself stops polling; its first page is polled on its own and put in front.
  const loadedServerPages = list.data?.pages.length ?? 0
  const listHead = useQuery({
    queryKey: ['jobs', 'page-head', serverStatus, timeRange, category, platform],
    queryFn: () => hubPoll<JobPage>(`/jobs/page${jobPageQueryString(serverStatus, timeRange, { jobTypes: categoryJobTypes, platform })}`),
    enabled: loadedServerPages > 1,
    refetchInterval: jobListHeadPollInterval(loadedServerPages)
  })
  useEffect(() => {
    if (listHead.data) client.setQueryData<InfiniteData<JobPage, string | null>>(['jobs', 'page', serverStatus, timeRange, category, platform], current => withFreshFirstPage(current, listHead.data))
  }, [listHead.data, client, serverStatus, timeRange, category, platform])
  const detail = useQuery({
    queryKey: ['job', selectedId],
    queryFn: () => hubPoll<JobDetail>(`/jobs/${selectedId}`),
    enabled: Boolean(selectedId),
    refetchInterval: query => selectedId ? jobDetailPollInterval(query.state.data?.job.status) : false
  })
  const action = useMutation({
    mutationFn: ({ id, verb }: { id: string; verb: 'retry' | 'cancel' }) => hubApi<JobDetail>(`/jobs/${id}/${verb}`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey(`${verb}-${id}`) } }),
    onSuccess: async (data, variables) => {
      setSelectedId(data.job.id)
      setFeedback({ message: variables.verb === 'retry' ? 'İşlem yeniden kuyruğa alındı.' : 'İşlem iptal edildi.', tone: variables.verb === 'retry' ? 'info' : 'success' })
      await Promise.all([client.invalidateQueries({ queryKey: ['jobs'] }), client.invalidateQueries({ queryKey: ['job', data.job.id] })])
    },
    onError: error => setFeedback({ message: error instanceof Error ? error.message : 'İşlem güncellenemedi.', tone: 'danger' })
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
  const rawJobs = useMemo(() => {
    // A refetch can shift rows between pages while new jobs arrive, so a job is shown once.
    const seen = new Set<string>()
    return (list.data?.pages ?? []).flatMap(page => page.items).filter(job => !seen.has(job.id) && Boolean(seen.add(job.id)))
  }, [list.data])
  const rangeFiltered = useMemo(() => {
    if (timeRange === 'all') return rawJobs
    const rangeMs = timeRange === '7d' ? 7 * 24 * 60 * 60 * 1000 : 24 * 60 * 60 * 1000
    const cutoff = Date.now() - rangeMs
    return rawJobs.filter(job => {
      const timestamp = new Date(job.createdAt).getTime()
      return Number.isNaN(timestamp) || timestamp >= cutoff
    })
  }, [rawJobs, timeRange])
  const filtered = useMemo(() => {
    const term = search.trim().toLocaleLowerCase('tr-TR')
    const categoryMatcher = jobCategoryMatcher(category)
    return rangeFiltered.filter(job => {
      const matchCategory = categoryMatcher(job.jobType)
      if (!matchCategory) return false
      if (statusChips.length && !statusChips.includes(jobStatusChip(job.status))) return false
      if (!term) return true
      return [job.jobType, job.status, job.lastErrorCode, job.lastErrorSummary, job.correlationId].some(value => value?.toLocaleLowerCase('tr-TR').includes(term))
    })
  }, [rangeFiltered, category, search, statusChips])
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
    if (shouldLoadTowardsPage(pageNumber, totalPages, moreOnServer, loadedServerPages, list.isFetching)) void list.fetchNextPage()
  }, [pageNumber, totalPages, moreOnServer, loadedServerPages, list])
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
  const countRows = useMemo(() => serverCounts.data ?? rangeFiltered.map(job => ({ status: job.status, jobType: job.jobType, count: 1 })), [serverCounts.data, rangeFiltered])
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

  return <section className="content jobs-page jobs-reference-page">
    <div className="jobs-reference-heading rv-page-head">
      <div>
        <p className="eyebrow">Sistem İzleme</p>
        <h1>İşlem Takibi</h1>
        <p className="rv-page-lead">Arka plan işlemlerinin durumunu, hatalarını ve kuyruk performansını izleyin.</p>
      </div>
      <div className="jobs-reference-heading-actions">
        <div className="jobs-reference-range-wrap">
          <button type="button" className="jobs-reference-range" aria-expanded={timeRangeOpen} onClick={() => setTimeRangeOpen(value => !value)}><JobsIcon name="calendar" />{timeRangeLabel(timeRange)}<JobsIcon name="chevron-down" /></button>
          {timeRangeOpen && <div className="jobs-reference-range-menu" role="menu" aria-label="Zaman aralığı">
            {([['24h', 'Son 24 Saat'], ['7d', 'Son 7 Gün'], ['all', 'Tüm Zamanlar']] as const).map(([value, label]) => <button type="button" role="menuitem" className={timeRange === value ? 'active' : ''} key={value} onClick={() => { setTimeRange(value); setTimeRangeOpen(false) }}>{label}</button>)}
          </div>}
        </div>
        <div className="jobs-reference-filter-wrap">
          <button type="button" className="jobs-reference-filter-toggle" aria-expanded={filterOpen} onClick={() => setFilterOpen(value => !value)}><JobsIcon name="filter" />Filtrele{platform && <span className="jobs-reference-filter-platform"><PlatformSquareMark code={platform} name={jobPlatformLabel(platform)} />{jobPlatformLabel(platform)}</span>}</button>
          {filterOpen && <div className="jobs-reference-filter-panel" role="dialog" aria-label="İşlem filtreleri">
            <label className="jobs-reference-filter-platform-field">Platform<span className="jobs-reference-platform-select">{platform && <PlatformSquareMark code={platform} name={jobPlatformLabel(platform)} />}<select aria-label="Platform" value={platform ?? ''} onChange={event => setPlatform(event.target.value || null)}><option value="">Tümü</option>{platformOptions.map(code => <option key={code} value={code}>{jobPlatformLabel(code)}</option>)}</select></span></label>
            <label>Durum<select value={status} onChange={event => setStatus(event.target.value as '' | JobStatus)}>{statuses.map(item => <option key={item.value || 'all'} value={item.value}>{item.label}</option>)}</select></label>
            <label>Sayfa Başına<select aria-label="Sayfa başına işlem" value={pageSize} onChange={event => setPageSize(Number(event.target.value))}>{jobPageSizes.map(value => <option key={value} value={value}>{value}</option>)}</select></label>
          </div>}
        </div>
      </div>
    </div>
    <JobQueueMetrics hours={timeRange === '24h' ? 24 : 168} titleFor={jobType => `${jobTitle(jobType)} · ${jobPresentation(jobType).action}`} platform={platform} />
    <div className="jobs-reference-canvas">
      <ViewTabsWithMore more={[]} className="jobs-reference-tabs" ariaLabel="İşlem kategorileri" value={category} onChange={value => setCategory(value as JobCategory)} items={jobCategoryTabs.filter(tab => tab.key !== 'SYSTEM').map(tab => ({ value: tab.key, label: viewTabLabel(({ ALL: 'all', ORDERS: 'order', PRICE_INVENTORY: 'price', CATALOG: 'product', INVOICES: 'invoice', RETURNS: 'return', SYSTEM: 'review' } as const)[tab.key], tab.label), count: list.isLoading ? undefined : categoryCounts.get(tab.key) ?? 0 }))} />
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
          <label className="jobs-reference-search"><JobsIcon name="search" /><input value={search} onChange={event => setSearch(event.target.value)} placeholder="Correlation ID..." aria-label="Correlation ID ile işlem ara" /></label>
          <button type="button" className="jobs-reference-refresh" title="Yenile" aria-label="İşlemleri yenile" onClick={refreshJobs}><JobsIcon name="refresh" /></button>
        </div>
      </div>
      {list.isLoading ? <Busy text="İşlemler yükleniyor…" /> : list.isError && !list.data ? <div role="alert" className="jobs-reference-state jobs-reference-state-error">İşlem listesi alınamadı.</div> : <>
        <div className="jobs-reference-table-scroll"><table className="jobs-reference-table"><thead><tr><th>İşlem Türü</th><th>Durum</th><th>Deneme</th><th>Zaman</th><th>Correlation ID</th></tr></thead><tbody>
          {pageJobs.map(job => {
            const presentation = jobPresentation(job.jobType)
            const time = formatJobTime(job.createdAt)
            const typeDescriptionId = `job-type-description-${job.id}`
            return <tr className="jobs-reference-row" key={job.id} aria-describedby={typeDescriptionId} onClick={() => setSelectedId(job.id)} tabIndex={0} onKeyDown={event => { if (event.key === 'Enter') setSelectedId(job.id) }}>
              <td><div className="jobs-reference-type"><span className="jobs-reference-type-icon" aria-hidden="true"><JobsIcon name={presentation.icon} /></span><span className="jobs-reference-type-content"><strong>{jobTitle(job.jobType, job.marketplace)}</strong><small>{jobSubtitle(job.jobType)}{job.batchCount > 1 ? ` · ${job.batchCount} kayıt birlikte` : job.externalId ? ` · ${job.externalId}` : ''}</small><span id={typeDescriptionId} className="jobs-reference-type-tooltip" role="tooltip">{presentation.description}</span></span></div></td>
              <td><span className={`jobs-reference-status ${jobStatusTone(job.status)}`}><JobStateGlyph tone={jobStatusTone(job.status)} />{jobStatusLabel(job.status)}</span></td>
              <td className="jobs-reference-attempt">{job.attemptCount} / {job.maxAttempts}</td>
              <td><div className="jobs-reference-time"><strong>{time.time}</strong><small>{time.day} · {job.attemptCount > 0 ? `son deneme ${jobAttemptDuration(job)}` : 'çalışma başlamadı'}</small></div></td>
              <td><span className="jobs-reference-correlation">{job.correlationId}</span></td>
            </tr>
          })}
          {filtered.length === 0 && <tr><td className="jobs-reference-empty" colSpan={6}>{moreOnServer ? 'Yüklenen kayıtlarda eşleşen işlem yok. Daha eski işlemler için Daha Fazla Yükle’ye basın.' : 'Seçili kategori ve filtrelerle eşleşen kayıt bulunamadı.'}</td></tr>}
        </tbody></table></div>
        {(filtered.length > 0 || moreOnServer) && <div className="jobs-reference-pagination"><strong>{moreOnServer ? `${filtered.length.toLocaleString('tr-TR')} kayıt yüklendi, daha eskileri var` : `Toplam ${filtered.length.toLocaleString('tr-TR')} kayıt`}</strong><div className="jobs-reference-pagination-actions">{moreOnServer && <Button variant="secondary" className="jobs-reference-load-more" loading={list.isFetchingNextPage} onClick={() => void loadMoreJobs()}>Daha Fazla Yükle</Button>}<Pagination className="jobs-reference-page-controls" page={currentPage} totalPages={totalPages} hasNext={currentPage < totalPages || moreOnServer} disabled={list.isFetchingNextPage} onPageChange={setPageNumber} onPrevious={() => setPageNumber(value => Math.max(1, value - 1))} onNext={goToNextPage} /></div></div>}
      </>}
    </div>
     {selectedId && <JobDetailDrawer selected={selected} detail={detail} selectedIsRunning={Boolean(selectedIsRunning)} elevated={elevated} retryable={retryable} cancellable={cancellable} action={action} oneTimeInvoiceDelivery={oneTimeInvoiceDelivery} onClose={() => setSelectedId(null)} />}
     {feedback && <NoticeToast key={`${feedback.tone}:${feedback.message}`} kind={feedback.tone === 'danger' ? 'error' : feedback.tone} message={feedback.message} onClose={() => setFeedback(null)} durationMs={5500} />}
  </section>
}
