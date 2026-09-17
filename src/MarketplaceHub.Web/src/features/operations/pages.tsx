import { useEffect, useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi, type Me } from '../../shared/api'
import { Pagination, Tabs, UiIcon, type UiIconName } from '../../shared/components'
import { statusLabel } from '../../shared/status-labels'

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
}
type JobOrderContext = { orderId: string; orderNumber: string; externalOrderId: string; status: string; currency: string; netAmount: number; orderedAt: string; externalPackageId: string | null; cargoProvider: string | null; cargoTrackingNumber: string | null; customerName: string | null; lineCount: number }
type JobChange = { label: string; value: string; detail: string | null }
type JobScan = { mode: string; label: string; detail: string; window: string | null; plannedIntervalSeconds: number | null; plannedIntervalLabel: string | null; previousScheduledAt: string | null; actualIntervalLabel: string | null }
type JobDetail = { job: JobSummary; attempts: Array<{ attemptNumber: number; startedAt: string; completedAt: string | null; succeeded: boolean; errorCode: string | null; errorSummary: string | null }>; order: JobOrderContext | null; change: JobChange | null; relatedOrders: JobOrderContext[]; scan: JobScan | null }

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

function idempotencyKey(action: string, jobId: string) {
  return `${action}-${jobId}-${crypto.randomUUID()}`
}

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

function jobStatusTone(status: JobStatus) {
  if (status === 'SUCCEEDED') return 'success'
  if (status === 'LEASED') return 'running'
  if (status === 'RETRY_SCHEDULED') return 'retry'
  if (status === 'BLOCKED' || status === 'MANUAL_REVIEW' || status === 'DEAD') return 'error'
  return 'neutral'
}

type JobTypeIconName = 'price' | 'order' | 'invoice' | 'return' | 'product' | 'connection' | 'generic'
type JobPresentation = { title: string; icon: JobTypeIconName; description: string }

function jobPresentation(jobType: string): JobPresentation {
  const type = jobType.toUpperCase()
  if (type.includes('PRICE') || type.includes('INVENTORY') || type.includes('STOCK')) return { title: 'Fiyat ve Stok Güncellemesi', icon: 'price', description: 'Ürün fiyatı veya stok bilgisi pazaryeriyle karşılaştırılır ve güncellenir.' }
  if (type === 'TRENDYOL_ORDER_STATUS_SYNC') return { title: 'Sipariş Durum Kontrolü', icon: 'order', description: 'Açık siparişlerin paket ve taşıma durumları kontrol edilerek yerel kayıtlar güncellenir.' }
  if (type === 'TRENDYOL_ORDER_RECONCILIATION') return { title: 'Sipariş Mutabakatı', icon: 'order', description: 'Yerel siparişlerle Trendyol kayıtları karşılaştırılır; eksik veya farklı durumlar düzeltilir.' }
  if (type === 'TRENDYOL_ORDER_INVOICE_RECONCILIATION') return { title: 'Paket Fatura Durum Kontrolü', icon: 'invoice', description: 'Trendyol’daki açık paketlerin fatura durumu okunur ve yerel pakete işlenir. Yeni fatura oluşturmaz.' }
  if (type === 'TRENDYOL_ORDER_RECOVERY_SYNC') return { title: 'Sipariş Geçmişi Taraması', icon: 'order', description: 'Erişilebilen sipariş geçmişi taranarak eksik yerel sipariş kayıtları tamamlanır.' }
  if (type === 'TRENDYOL_ORDER_SYNC') return { title: 'Yeni Sipariş Senkronizasyonu', icon: 'order', description: 'Trendyol’daki yeni ve değişen siparişler güvenli aralıklarla sisteme alınır.' }
  if (type.includes('SHIPMENT') || type.includes('PACKAGE') || type.includes('COURIER')) return { title: 'Kargo ve Paket İşlemi', icon: 'order', description: 'Paket, kargo firması veya teslimatla ilgili işlem pazaryerine gönderilir.' }
  if (type.includes('LABEL')) return { title: 'Kargo Etiketi', icon: 'order', description: 'Seçilen sipariş veya paket için kargo etiketi işlemi yürütülür.' }
  if (type.includes('ORDER')) return { title: 'Sipariş İşlemi', icon: 'order', description: 'Siparişle ilgili arka plan işlemi yürütülür.' }
  if (type.includes('INVOICE') || type.includes('EFATURAM') || type.includes('BILLING')) return { title: 'E-Fatura İşlemi', icon: 'invoice', description: 'E-Faturam üzerinden fatura oluşturma, durum veya belge işlemi yürütülür.' }
  if (type.includes('RETURN') || type.includes('CLAIM')) return { title: 'İade Senkronizasyonu', icon: 'return', description: 'Pazaryerindeki iade kayıtları ve durumları sistemle eşitlenir.' }
  if (type.includes('PRODUCT') || type.includes('CATALOG') || type.includes('PUBLICATION') || type.includes('ATTRIBUTE') || type.includes('CATEGORY') || type.includes('BRAND')) return { title: 'Ürün ve Katalog Senkronizasyonu', icon: 'product', description: 'Ürün, katalog, kategori veya özellik bilgileri senkronize edilir.' }
  if (type.includes('CONNECTION') || type.includes('PROBE') || type.includes('TEST')) return { title: 'Bağlantı Kontrolü', icon: 'connection', description: 'Pazaryeri bağlantısının ve gerekli yetkilerin çalıştığı doğrulanır.' }
  return { title: jobType.replaceAll('_', ' ').toLocaleLowerCase('tr-TR').replace(/(^|\s)\S/g, value => value.toLocaleUpperCase('tr-TR')), icon: 'generic', description: 'İşlem arka planda yürütülür; ayrıntılar için kaydı açabilirsiniz.' }
}

function jobSource(jobType: string) {
  const type = jobType.toUpperCase()
  if (type.includes('EFATURAM')) return 'e-Faturam API'
  if (type.startsWith('TRENDYOL_')) return 'Trendyol API'
  if (type.includes('INVOICE')) return 'e-Faturam API'
  return 'Ravencia Worker'
}

function fallbackJobChange(job: JobSummary): JobChange {
  const type = job.jobType.toUpperCase()
  if (type.includes('SHIPMENT_ACTION')) return { label: 'Yapılan değişiklik', value: 'Paket işlemi', detail: 'Paket işlemi Trendyol’a gönderildi.' }
  if (type === 'TRENDYOL_ORDER_STATUS_SYNC') return { label: 'Tarama türü', value: 'Sipariş durum taraması', detail: 'Açık siparişlerin paket ve taşıma durumları kontrol edilerek yerel durum güncellenir.' }
  if (type === 'TRENDYOL_ORDER_RECONCILIATION') return { label: 'Tarama türü', value: 'Kapsamlı sipariş taraması', detail: 'Yerel siparişler ile pazaryeri kayıtları karşılaştırılır; durum ve paket farklılıkları düzeltilir.' }
  if (type === 'TRENDYOL_ORDER_INVOICE_RECONCILIATION') return { label: 'Tarama türü', value: 'Paket fatura taraması', detail: 'Teslim edilmiş ve açık paketlerin pazaryeri fatura durumu kontrol edilir.' }
  if (type === 'TRENDYOL_ORDER_RECOVERY_SYNC') return { label: 'Tarama türü', value: 'Tam sipariş taraması', detail: 'Erişilebilen sipariş pencereleri taranarak eksik yerel kayıtlar tamamlanır.' }
  if (type === 'TRENDYOL_ORDER_SYNC') return { label: 'Yapılan değişiklik', value: 'Sipariş senkronizasyonu', detail: 'Sipariş bilgileri pazaryerinden eşitlendi.' }
  if (type.includes('REFERENCE_SYNC')) return { label: 'Yapılan değişiklik', value: 'Referans verisi senkronizasyonu', detail: 'Kategori, marka veya özellik verileri güncellendi.' }
  if (type.includes('PRODUCT') || type.includes('CATALOG')) return { label: 'Yapılan değişiklik', value: 'Ürün senkronizasyonu', detail: 'Ürün bilgileri pazaryerine gönderildi.' }
  if (type.includes('INVOICE')) return { label: 'Yapılan değişiklik', value: 'Fatura işlemi', detail: 'Fatura isteği pazaryerine gönderildi.' }
  return { label: 'Yapılan değişiklik', value: jobPresentation(job.jobType).title, detail: 'İşlem isteği sisteme kaydedildi.' }
}

function formatJobTime(value: string) {
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return { time: '—', day: '—' }
  const now = new Date()
  const sameDay = date.getFullYear() === now.getFullYear() && date.getMonth() === now.getMonth() && date.getDate() === now.getDate()
  return {
    time: date.toLocaleTimeString('tr-TR', { hour: '2-digit', minute: '2-digit', second: '2-digit' }),
    day: sameDay ? 'Bugün' : date.toLocaleDateString('tr-TR', { day: '2-digit', month: '2-digit', year: 'numeric' })
  }
}

function formatOptionalJobTime(value: string | null) {
  if (!value) return '—'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '—' : date.toLocaleString('tr-TR')
}
function jobDuration(startedAt: string | null, completedAt: string | null) {
  if (!startedAt) return 'Başlamadı'
  const start = new Date(startedAt).getTime(); const end = completedAt ? new Date(completedAt).getTime() : Date.now()
  if (!Number.isFinite(start) || !Number.isFinite(end) || end < start) return '—'
  const seconds = Math.max(0, Math.round((end - start) / 1000))
  if (seconds < 60) return `${seconds} sn`
  return `${Math.floor(seconds / 60)} dk ${seconds % 60} sn`
}

type JobTimeRange = '24h' | '7d' | 'all'

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

type JobCategory = 'ALL' | 'ORDERS' | 'PRICE_INVENTORY' | 'CATALOG' | 'INVOICES' | 'RETURNS' | 'SYSTEM'

const categoryTabs: Array<{ key: JobCategory; label: string; match: (type: string) => boolean }> = [
  { key: 'ALL', label: 'Tüm işlemler', match: () => true },
  { key: 'ORDERS', label: 'Sipariş/Kargo', match: t => /ORDER|SHIPMENT|PACKAGE|COURIER|LABEL/i.test(t) },
  { key: 'PRICE_INVENTORY', label: 'Fiyat/Stok', match: t => /PRICE|INVENTORY|STOCK|OFFER/i.test(t) },
  { key: 'CATALOG', label: 'Ürün/Yayın', match: t => /PRODUCT|CATALOG|IMPORT|ATTRIBUTE|CATEGORY|BRAND|PUBLICATION/i.test(t) },
  { key: 'INVOICES', label: 'Faturalar', match: t => /INVOICE|EFATURAM|BILLING/i.test(t) },
  { key: 'RETURNS', label: 'İadeler', match: t => /RETURN|CLAIM/i.test(t) },
  { key: 'SYSTEM', label: 'Sistem & Test', match: t => /TEST|PROBE|PING|MIGRATION|SCAN|SCHEDULER/i.test(t) }
]

export function JobsPage({ me }: { me: Me }) {
  const client = useQueryClient()
  const [category, setCategory] = useState<JobCategory>('ALL')
  const [status, setStatus] = useState<'' | JobStatus>('')
  const [search, setSearch] = useState('')
  const [filterOpen, setFilterOpen] = useState(false)
  const [timeRange, setTimeRange] = useState<JobTimeRange>('24h')
  const [timeRangeOpen, setTimeRangeOpen] = useState(false)
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
  const [pageSize, setPageSize] = useState(20)
  const [pageNumber, setPageNumber] = useState(1)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const elevated = ['OWNER', 'ADMINISTRATOR'].includes((me.role ?? '').toUpperCase())
  const list = useQuery({
    queryKey: ['jobs', status],
    queryFn: () => hubApi<JobSummary[]>(`/jobs${status ? `?status=${encodeURIComponent(status)}` : ''}`),
    refetchInterval: 5000
  })
  const detail = useQuery({
    queryKey: ['job', selectedId],
    queryFn: () => hubApi<JobDetail>(`/jobs/${selectedId}`),
    enabled: Boolean(selectedId),
    refetchInterval: selectedId ? 5000 : false
  })
  const action = useMutation({
    mutationFn: ({ id, verb }: { id: string; verb: 'retry' | 'cancel' }) => hubApi<JobDetail>(`/jobs/${id}/${verb}`, { method: 'POST', headers: { 'Idempotency-Key': idempotencyKey(verb, id) } }),
    onSuccess: async data => {
      setSelectedId(data.job.id)
      await Promise.all([client.invalidateQueries({ queryKey: ['jobs'] }), client.invalidateQueries({ queryKey: ['job', data.job.id] })])
    }
  })
  const rawJobs = list.data ?? []
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
    const categoryMatcher = categoryTabs.find(tab => tab.key === category)?.match ?? (() => true)
    return rangeFiltered.filter(job => {
      const matchCategory = categoryMatcher(job.jobType)
      if (!matchCategory) return false
      if (!term) return true
      return [job.jobType, job.status, job.lastErrorCode, job.lastErrorSummary, job.correlationId].some(value => value?.toLocaleLowerCase('tr-TR').includes(term))
    })
  }, [rangeFiltered, category, search])
  useEffect(() => { setPageNumber(1) }, [category, status, search, pageSize, timeRange])
  const totalPages = Math.max(1, Math.ceil(filtered.length / pageSize))
  const currentPage = Math.min(pageNumber, totalPages)
  const pageJobs = filtered.slice((currentPage - 1) * pageSize, currentPage * pageSize)
  const selected = detail.data?.job
  const retryable = selected && ['BLOCKED', 'MANUAL_REVIEW', 'DEAD'].includes(selected.status)
  const cancellable = selected && !['SUCCEEDED', 'DEAD', 'CANCELLED'].includes(selected.status)
  const selectedIsRunning = selected?.status === 'LEASED'
  const statusSummary = useMemo(() => ({
    success: rangeFiltered.filter(job => job.status === 'SUCCEEDED').length,
    running: rangeFiltered.filter(job => job.status === 'LEASED').length,
    waiting: rangeFiltered.filter(job => job.status === 'PENDING' || job.status === 'RETRY_SCHEDULED').length,
    error: rangeFiltered.filter(job => ['BLOCKED', 'MANUAL_REVIEW', 'DEAD'].includes(job.status)).length,
    cancelled: rangeFiltered.filter(job => job.status === 'CANCELLED').length
  }), [rangeFiltered])
  const categoryCounts = useMemo(() => new Map(categoryTabs.map(tab => [tab.key, rangeFiltered.filter(job => tab.match(job.jobType)).length])), [rangeFiltered])
  const refreshJobs = () => {
    void Promise.all([
      client.invalidateQueries({ queryKey: ['jobs'] }),
      selectedId ? client.invalidateQueries({ queryKey: ['job', selectedId] }) : Promise.resolve()
    ])
  }

  return <section className="content jobs-page jobs-reference-page">
    <div className="jobs-reference-heading">
      <div>
        <h1>Arka Plan İşlemleri</h1>
        <p>Pazaryerleri ile sistem arasındaki senkronizasyon kuyruğunu ve hataları izleyin.</p>
      </div>
      <div className="jobs-reference-heading-actions">
        <div className="jobs-reference-range-wrap">
          <button type="button" className="jobs-reference-range" aria-expanded={timeRangeOpen} onClick={() => setTimeRangeOpen(value => !value)}><JobsIcon name="calendar" />{timeRangeLabel(timeRange)}<JobsIcon name="chevron-down" /></button>
          {timeRangeOpen && <div className="jobs-reference-range-menu" role="menu" aria-label="Zaman aralığı">
            {([['24h', 'Son 24 Saat'], ['7d', 'Son 7 Gün'], ['all', 'Tüm Zamanlar']] as const).map(([value, label]) => <button type="button" role="menuitem" className={timeRange === value ? 'active' : ''} key={value} onClick={() => { setTimeRange(value); setTimeRangeOpen(false) }}>{label}</button>)}
          </div>}
        </div>
        <div className="jobs-reference-filter-wrap">
          <button type="button" className="jobs-reference-filter-toggle" aria-expanded={filterOpen} onClick={() => setFilterOpen(value => !value)}><JobsIcon name="filter" />Filtrele</button>
          {filterOpen && <div className="jobs-reference-filter-panel" role="dialog" aria-label="İşlem filtreleri">
            <label>Durum<select value={status} onChange={event => setStatus(event.target.value as '' | JobStatus)}>{statuses.map(item => <option key={item.value || 'all'} value={item.value}>{item.label}</option>)}</select></label>
            <label>Sayfa başına<select aria-label="Sayfa başına işlem" value={pageSize} onChange={event => setPageSize(Number(event.target.value))}>{[20, 50, 100, 200].map(value => <option key={value} value={value}>{value}</option>)}</select></label>
          </div>}
        </div>
      </div>
    </div>
    <div className="jobs-reference-canvas">
      <Tabs className="jobs-reference-tabs" ariaLabel="İşlem kategorileri" value={category} onChange={value => setCategory(value as JobCategory)} items={categoryTabs.filter(tab => tab.key !== 'SYSTEM').map(tab => ({ value: tab.key, label: tab.label, count: categoryCounts.get(tab.key) ?? 0 }))} />
      <div className="jobs-reference-toolbar">
        <div className="jobs-reference-status-summary" aria-label="İşlem durum özeti">
          <span className="success"><i aria-hidden="true" />Başarılı <strong>{statusSummary.success}</strong></span>
          <span className="running"><i aria-hidden="true" />Çalışıyor <strong>{statusSummary.running}</strong></span>
          <span className="waiting"><i aria-hidden="true" />Bekliyor <strong>{statusSummary.waiting}</strong></span>
          <span className="error"><i aria-hidden="true" />Hata <strong>{statusSummary.error}</strong></span>
          {statusSummary.cancelled > 0 && <span className="cancelled"><i aria-hidden="true" />İptal <strong>{statusSummary.cancelled}</strong></span>}
        </div>
        <div className="jobs-reference-toolbar-actions">
          <label className="jobs-reference-search"><JobsIcon name="search" /><input value={search} onChange={event => setSearch(event.target.value)} placeholder="Correlation ID..." aria-label="Correlation ID ile işlem ara" /></label>
          <button type="button" className="jobs-reference-refresh" title="Yenile" aria-label="İşlemleri yenile" onClick={refreshJobs}><JobsIcon name="refresh" /></button>
        </div>
      </div>
      {list.isLoading ? <p className="jobs-reference-state">İşlemler yükleniyor…</p> : list.isError ? <div role="alert" className="jobs-reference-state jobs-reference-state-error">İşlem listesi alınamadı.</div> : <>
        <div className="jobs-reference-table-scroll"><table className="jobs-reference-table"><thead><tr><th>İşlem Türü</th><th>Durum</th><th>Deneme</th><th>Zaman</th><th>Correlation ID</th><th>Aksiyon</th></tr></thead><tbody>
          {pageJobs.map(job => {
            const presentation = jobPresentation(job.jobType)
            const time = formatJobTime(job.createdAt)
            const typeDescriptionId = `job-type-description-${job.id}`
            return <tr className="jobs-reference-row" key={job.id} aria-describedby={typeDescriptionId} onClick={() => setSelectedId(job.id)} tabIndex={0} onKeyDown={event => { if (event.key === 'Enter') setSelectedId(job.id) }}>
              <td><div className="jobs-reference-type"><span className="jobs-reference-type-icon" aria-hidden="true"><JobsIcon name={presentation.icon} /></span><span className="jobs-reference-type-content"><strong>{presentation.title}</strong><small>{job.marketplace} · {job.batchCount > 1 ? `Toplu işlem · ${job.batchCount} job` : job.externalId ?? jobSource(job.jobType)}</small><span id={typeDescriptionId} className="jobs-reference-type-tooltip" role="tooltip">{presentation.description}</span></span></div></td>
              <td><span className={`jobs-reference-status ${jobStatusTone(job.status)}`}><i aria-hidden="true" />{jobStatusLabel(job.status)}</span></td>
              <td className="jobs-reference-attempt">{job.attemptCount} / {job.maxAttempts}</td>
              <td><div className="jobs-reference-time"><strong>{time.time}</strong><small>{time.day}</small><small>{job.startedAt ? `Süre ${jobDuration(job.startedAt, job.completedAt)}` : 'Çalışma başlamadı'}</small></div></td>
              <td><span className="jobs-reference-correlation">{job.correlationId}</span></td>
              <td><button type="button" className="jobs-reference-row-action" aria-label={`${presentation.title} ayrıntısını aç`} onClick={event => { event.stopPropagation(); setSelectedId(job.id) }}><UiIcon name="chevronRight" /></button></td>
            </tr>
          })}
          {filtered.length === 0 && <tr><td className="jobs-reference-empty" colSpan={6}>Seçili kategori ve filtrelerle eşleşen kayıt bulunamadı.</td></tr>}
        </tbody></table></div>
        {filtered.length > 0 && <div className="jobs-reference-pagination"><strong>Toplam {filtered.length.toLocaleString('tr-TR')} kayıt</strong><Pagination className="jobs-reference-page-controls" page={currentPage} totalPages={totalPages} onPageChange={setPageNumber} onPrevious={() => setPageNumber(value => Math.max(1, value - 1))} onNext={() => setPageNumber(value => Math.min(totalPages, value + 1))} /></div>}
      </>}
    </div>
    {selectedId && <div className="job-detail-backdrop jobs-reference-drawer-backdrop" role="presentation" onMouseDown={() => setSelectedId(null)}><aside className="job-detail-drawer jobs-reference-drawer panel" role="dialog" aria-modal="true" aria-labelledby="job-detail-title" onMouseDown={event => event.stopPropagation()}>
       <div className="jobs-reference-drawer-header"><div><span className="jobs-reference-drawer-correlation">{selected?.correlationId ?? selectedId}</span>{selected && <span className={`jobs-reference-status ${jobStatusTone(selected.status)}`}><i aria-hidden="true" />{jobStatusLabel(selected.status)}</span>}<h2 id="job-detail-title">{selected ? jobPresentation(selected.jobType).title : 'İşlem ayrıntısı'}</h2><p>{selected ? `${jobSource(selected.jobType)} · ${selected.jobType}${selected.batchCount > 1 ? ` · Toplu işlem (${selected.batchCount} job)` : ''}` : 'İşlem ayrıntısı yükleniyor'}</p></div><button type="button" className="jobs-reference-drawer-close" aria-label="Detay panelini kapat" onClick={() => setSelectedId(null)}><UiIcon name="close" /></button></div>
      {detail.isLoading ? <p className="jobs-reference-state">Yükleniyor…</p> : detail.isError || !detail.data ? <div role="alert" className="jobs-reference-state jobs-reference-state-error">İşlem ayrıntısı alınamadı.</div> : <div className="jobs-reference-drawer-body"><div className="jobs-reference-error-alert"><strong>{detail.data.job.lastErrorCode ?? 'İşlem durumu'}</strong><span>{detail.data.job.lastErrorSummary ?? 'Hata açıklaması bulunmuyor.'}</span></div>{selectedIsRunning && <p className="jobs-reference-cancel-note">Çalışan işlem durduruluyor. Dış API çağrısı tamamlanana kadar durum birkaç saniye daha “Çalışıyor” görünebilir.</p>}{(() => { const change = detail.data.change ?? fallbackJobChange(detail.data.job); return <section className="jobs-reference-change-summary" aria-labelledby="job-change-title"><div><span className="jobs-reference-section-kicker">İşlem özeti</span><h3 id="job-change-title">{change.value}</h3></div><div><strong>{change.label}</strong><p>{change.detail ?? 'İşlem ayrıntısı mevcut.'}</p></div></section> })()}{detail.data.scan && <JobScanSummary scan={detail.data.scan} />}{detail.data.job.batchCount > 1 ? <section className="jobs-reference-batch-context" aria-labelledby="job-batch-title"><div className="jobs-reference-batch-heading"><div><span className="jobs-reference-section-kicker">Toplu işlem</span><h3 id="job-batch-title">{detail.data.job.batchCount} job · {detail.data.relatedOrders.length} sipariş</h3></div><span className="jobs-reference-batch-note">Sonuçlar sipariş bazında</span></div><div className="jobs-reference-batch-list">{detail.data.relatedOrders.map(order => <article key={order.orderId}><div><strong>Sipariş #{order.orderNumber}</strong><small>{order.customerName ?? 'Müşteri bilgisi yok'} · {order.lineCount} ürün satırı</small></div><span>{order.cargoProvider ?? 'Kargo bilgisi yok'}</span><b>{statusLabel(order.status)}</b></article>)}{detail.data.relatedOrders.length === 0 && <p>Sipariş bağlantısı bulunamadı.</p>}</div></section> : detail.data.order && <section className="jobs-reference-order-context" aria-labelledby="job-order-context-title"><div><span className="jobs-reference-section-kicker">İlgili sipariş</span><h3 id="job-order-context-title">Sipariş #{detail.data.order.orderNumber}</h3><p>{detail.data.order.customerName ?? 'Müşteri bilgisi yok'} · {detail.data.order.lineCount} ürün satırı</p></div><div className="jobs-reference-order-facts"><p><small>Dış sipariş ID</small><strong>{detail.data.order.externalOrderId}</strong></p><p><small>Sipariş durumu</small><strong>{statusLabel(detail.data.order.status)}</strong></p><p><small>Sipariş tarihi</small><strong>{formatOptionalJobTime(detail.data.order.orderedAt)}</strong></p><p><small>Sipariş tutarı</small><strong>{detail.data.order.netAmount.toLocaleString('tr-TR', { style: 'currency', currency: detail.data.order.currency })}</strong></p>{detail.data.order.externalPackageId && <p><small>Paket no</small><strong>{detail.data.order.externalPackageId}</strong></p>}{detail.data.order.cargoTrackingNumber && <p><small>Kargo takip no</small><strong>{detail.data.order.cargoTrackingNumber}</strong></p>}</div></section>}<div className="job-detail-facts"><p><small>Pazaryeri</small><strong>{detail.data.job.marketplace}</strong></p><p><small>İşlem</small><strong>{detail.data.job.jobType}</strong></p><p><small>Dış kimlik</small><strong>{detail.data.job.externalId ?? '—'}</strong></p><p><small>Retry sayısı</small><strong>{detail.data.job.attemptCount} / {detail.data.job.maxAttempts}</strong></p><p><small>Oluşturulma</small><strong>{formatOptionalJobTime(detail.data.job.createdAt)}</strong></p><p><small>Çalışma başlangıcı</small><strong>{formatOptionalJobTime(detail.data.job.startedAt)}</strong></p><p><small>Tamamlanma</small><strong>{formatOptionalJobTime(detail.data.job.completedAt)}</strong></p><p><small>Çalışma süresi</small><strong>{jobDuration(detail.data.job.startedAt, detail.data.job.completedAt)}</strong></p><p><small>İlk hata</small><strong>{formatOptionalJobTime(detail.data.job.firstFailedAt)}</strong></p><p><small>Son hata</small><strong>{formatOptionalJobTime(detail.data.job.lastFailedAt)}</strong></p><p><small>Sonraki deneme</small><strong>{formatOptionalJobTime(detail.data.job.nextRetryAt)}</strong></p><p><small>Correlation ID</small><strong>{detail.data.job.correlationId}</strong></p></div>{elevated && <div className="job-detail-actions">{retryable && <button type="button" disabled={action.isPending} onClick={() => action.mutate({ id: detail.data.job.id, verb: 'retry' })}>Manuel Yeniden Dene</button>}{cancellable && <button type="button" className="secondary" disabled={action.isPending} onClick={() => action.mutate({ id: detail.data.job.id, verb: 'cancel' })}>{action.isPending ? selectedIsRunning ? 'Durduruluyor…' : 'İptal ediliyor…' : selectedIsRunning ? 'Durdur' : 'İptal et'}</button>}</div>}{action.isError && <div role="alert" className="error">İşlem güncellenemedi.</div>}<h3>Deneme geçmişi</h3><div className="table-wrap"><table><thead><tr><th>#</th><th>Başlangıç</th><th>Sonuç</th><th>Hata</th></tr></thead><tbody>{detail.data.attempts.map(attempt => <tr key={attempt.attemptNumber}><td>{attempt.attemptNumber}</td><td>{new Date(attempt.startedAt).toLocaleString('tr-TR')}</td><td>{attempt.completedAt ? (attempt.succeeded ? 'Başarılı' : 'Başarısız') : 'Çalışıyor'}</td><td>{attempt.errorCode ?? '—'}<small>{attempt.errorSummary ?? ''}</small></td></tr>)}{detail.data.attempts.length === 0 && <tr><td colSpan={4}>Henüz deneme yok.</td></tr>}</tbody></table></div></div>}
    </aside></div>}
  </section>
}
