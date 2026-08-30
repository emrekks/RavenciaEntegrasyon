import { Fragment, useEffect, useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi, type Me } from './api'

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
}
type JobOrderContext = { orderId: string; orderNumber: string; externalOrderId: string; status: string; currency: string; netAmount: number; orderedAt: string; externalPackageId: string | null; cargoProvider: string | null; cargoTrackingNumber: string | null; customerName: string | null; lineCount: number }
type JobChange = { label: string; value: string; detail: string | null }
type JobDetail = { job: JobSummary; attempts: Array<{ attemptNumber: number; startedAt: string; completedAt: string | null; succeeded: boolean; errorCode: string | null; errorSummary: string | null }>; order: JobOrderContext | null; change: JobChange | null; relatedOrders: JobOrderContext[] }

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

function jobPresentation(jobType: string): { title: string; icon: JobTypeIconName } {
  const type = jobType.toUpperCase()
  if (type.includes('PRICE') || type.includes('INVENTORY') || type.includes('STOCK')) return { title: 'Fiyat Güncelleme', icon: 'price' }
  if (type.includes('ORDER') || type.includes('SHIPMENT') || type.includes('PACKAGE') || type.includes('COURIER') || type.includes('LABEL')) return { title: 'Sipariş Aktarımı', icon: 'order' }
  if (type.includes('INVOICE') || type.includes('EFATURAM') || type.includes('BILLING')) return { title: 'Fatura İletimi', icon: 'invoice' }
  if (type.includes('RETURN') || type.includes('CLAIM')) return { title: 'İade Senkronizasyonu', icon: 'return' }
  if (type.includes('PRODUCT') || type.includes('CATALOG') || type.includes('PUBLICATION') || type.includes('ATTRIBUTE') || type.includes('CATEGORY') || type.includes('BRAND')) return { title: 'Ürün Senkronizasyonu', icon: 'product' }
  if (type.includes('CONNECTION') || type.includes('PROBE') || type.includes('TEST')) return { title: 'Bağlantı Kontrolü', icon: 'connection' }
  return { title: jobType.replaceAll('_', ' ').toLocaleLowerCase('tr-TR').replace(/(^|\s)\S/g, value => value.toLocaleUpperCase('tr-TR')), icon: 'generic' }
}

function jobSource(jobType: string) {
  const type = jobType.toUpperCase()
  if (type.includes('EFATURAM') || type.includes('INVOICE')) return 'e-Faturam API'
  if (type.includes('TRENDYOL')) return 'Trendyol API'
  return 'Ravencia Worker'
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
  const common = { className: `jobs-reference-icon jobs-reference-icon-${name}`, viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor', strokeWidth: 1.8, strokeLinecap: 'round' as const, strokeLinejoin: 'round' as const, 'aria-hidden': true, focusable: false }
  if (name === 'calendar') return <svg {...common}><rect x="3.5" y="4.5" width="17" height="16" rx="2" /><path d="M7.5 3.5v3M16.5 3.5v3M3.5 9h17M8 13h.01M12 13h.01M16 13h.01M8 17h.01M12 17h.01M16 17h.01" /></svg>
  if (name === 'chevron-down') return <svg {...common}><path d="m7 9 5 5 5-5" /></svg>
  if (name === 'filter') return <svg {...common}><path d="M4 5h16M7 12h10M10 19h4" /></svg>
  if (name === 'search') return <svg {...common}><circle cx="10.5" cy="10.5" r="5.75" /><path d="m15 15 5 5" /></svg>
  if (name === 'price') return <svg {...common}><path d="M5 7.5 12 4l7 3.5v9L12 20l-7-3.5z" /><path d="M8.5 10.5h7M8.5 13.5h5" /></svg>
  if (name === 'order') return <svg {...common}><path d="m4 8 8-4 8 4-8 4zM4 8v8l8 4 8-4V8M12 12v8" /></svg>
  if (name === 'invoice') return <svg {...common}><path d="M6 3.5h9l3 3V20.5H6zM15 3.5v4h3M9 12h6M9 15.5h6" /></svg>
  if (name === 'return') return <svg {...common}><path d="M9 8 5 12l4 4M5 12h8a5 5 0 0 1 5 5v1" /></svg>
  if (name === 'product') return <svg {...common}><path d="m4 8 8-4 8 4-8 4zM4 8v8l8 4 8-4V8M8 10v8M16 10v8" /></svg>
  if (name === 'connection') return <svg {...common}><path d="M8 7V5a3 3 0 0 1 6 0v2M7 7h8v5a4 4 0 0 1-8 0zM12 16v3M9 20h6" /></svg>
  if (name === 'generic') return <svg {...common}><circle cx="12" cy="12" r="6" /><path d="M12 9v6M9 12h6" /></svg>
  return <svg {...common}><path d="M20 11a8 8 0 0 0-14.8-4L4 9" /><path d="M4 5v4h4M4 13a8 8 0 0 0 14.8 4L20 15" /><path d="M20 19v-4h-4" /></svg>
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
  const cancellable = selected && !['LEASED', 'SUCCEEDED', 'DEAD', 'CANCELLED'].includes(selected.status)
  const statusSummary = useMemo(() => ({
    success: rangeFiltered.filter(job => job.status === 'SUCCEEDED').length,
    running: rangeFiltered.filter(job => job.status === 'LEASED').length,
    waiting: rangeFiltered.filter(job => job.status === 'PENDING' || job.status === 'RETRY_SCHEDULED').length,
    error: rangeFiltered.filter(job => ['BLOCKED', 'MANUAL_REVIEW', 'DEAD'].includes(job.status)).length,
    cancelled: rangeFiltered.filter(job => job.status === 'CANCELLED').length
  }), [rangeFiltered])
  const categoryCounts = useMemo(() => new Map(categoryTabs.map(tab => [tab.key, rangeFiltered.filter(job => tab.match(job.jobType)).length])), [rangeFiltered])
  const pageNumbers = useMemo(() => {
    const pages = new Set([1, totalPages, currentPage, Math.max(1, currentPage - 1), Math.min(totalPages, currentPage + 1)])
    return [...pages].sort((a, b) => a - b)
  }, [currentPage, totalPages])
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
      <div className="jobs-reference-tabs" role="tablist" aria-label="İşlem kategorileri">
        {categoryTabs.filter(tab => tab.key !== 'SYSTEM').map(tab => (
          <button type="button" role="tab" aria-selected={category === tab.key} className={category === tab.key ? 'active' : ''} key={tab.key} onClick={() => setCategory(tab.key)}><span>{tab.label}</span><small className="jobs-reference-tab-count">{categoryCounts.get(tab.key) ?? 0}</small></button>
        ))}
      </div>
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
            return <tr className="jobs-reference-row" key={job.id} onClick={() => setSelectedId(job.id)} tabIndex={0} onKeyDown={event => { if (event.key === 'Enter') setSelectedId(job.id) }}>
              <td><div className="jobs-reference-type"><span className="jobs-reference-type-icon" aria-hidden="true"><JobsIcon name={presentation.icon} /></span><span><strong>{presentation.title}</strong><small>{job.marketplace} · {job.batchCount > 1 ? `Toplu işlem · ${job.batchCount} job` : job.externalId ?? jobSource(job.jobType)}</small></span></div></td>
              <td><span className={`jobs-reference-status ${jobStatusTone(job.status)}`}><i aria-hidden="true" />{jobStatusLabel(job.status)}</span></td>
              <td className="jobs-reference-attempt">{job.attemptCount} / {job.maxAttempts}</td>
              <td><div className="jobs-reference-time"><strong>{time.time}</strong><small>{time.day}</small><small>{job.startedAt ? `Süre ${jobDuration(job.startedAt, job.completedAt)}` : 'Çalışma başlamadı'}</small></div></td>
              <td><span className="jobs-reference-correlation">{job.correlationId}</span></td>
              <td><button type="button" className="jobs-reference-row-action" aria-label={`${presentation.title} ayrıntısını aç`} onClick={event => { event.stopPropagation(); setSelectedId(job.id) }}>›</button></td>
            </tr>
          })}
          {filtered.length === 0 && <tr><td className="jobs-reference-empty" colSpan={6}>Seçili kategori ve filtrelerle eşleşen kayıt bulunamadı.</td></tr>}
        </tbody></table></div>
        {filtered.length > 0 && <div className="jobs-reference-pagination"><strong>Toplam {filtered.length.toLocaleString('tr-TR')} kayıt</strong><div className="jobs-reference-page-buttons">
          <button type="button" aria-label="Önceki sayfa" disabled={currentPage <= 1} onClick={() => setPageNumber(value => Math.max(1, value - 1))}>‹</button>
          {pageNumbers.map((page, index) => <Fragment key={page}>{index > 0 && page - pageNumbers[index - 1] > 1 && <span aria-hidden="true">…</span>}<button type="button" className={page === currentPage ? 'active' : ''} aria-current={page === currentPage ? 'page' : undefined} onClick={() => setPageNumber(page)}>{page}</button></Fragment>)}
          <button type="button" aria-label="Sonraki sayfa" disabled={currentPage >= totalPages} onClick={() => setPageNumber(value => Math.min(totalPages, value + 1))}>›</button>
        </div></div>}
      </>}
    </div>
    {selectedId && <div className="job-detail-backdrop jobs-reference-drawer-backdrop" role="presentation" onMouseDown={() => setSelectedId(null)}><aside className="job-detail-drawer jobs-reference-drawer panel" role="dialog" aria-modal="true" aria-labelledby="job-detail-title" onMouseDown={event => event.stopPropagation()}>
      <div className="jobs-reference-drawer-header"><div><span className="jobs-reference-drawer-correlation">{selected?.correlationId ?? selectedId}</span>{selected && <span className={`jobs-reference-status ${jobStatusTone(selected.status)}`}><i aria-hidden="true" />{jobStatusLabel(selected.status)}</span>}<h2 id="job-detail-title">{selected ? jobPresentation(selected.jobType).title : 'İşlem ayrıntısı'}</h2><p>{selected ? `${jobSource(selected.jobType)} · ${selected.jobType}${selected.batchCount > 1 ? ` · Toplu işlem (${selected.batchCount} job)` : ''}` : 'İşlem ayrıntısı yükleniyor'}</p></div><button type="button" className="jobs-reference-drawer-close" aria-label="Detay panelini kapat" onClick={() => setSelectedId(null)}>×</button></div>
      {detail.isLoading ? <p className="jobs-reference-state">Yükleniyor…</p> : detail.isError || !detail.data ? <div role="alert" className="jobs-reference-state jobs-reference-state-error">İşlem ayrıntısı alınamadı.</div> : <div className="jobs-reference-drawer-body"><div className="jobs-reference-error-alert"><strong>{detail.data.job.lastErrorCode ?? 'İşlem durumu'}</strong><span>{detail.data.job.lastErrorSummary ?? 'Hata açıklaması bulunmuyor.'}</span></div>{detail.data.change && <section className="jobs-reference-change-summary" aria-labelledby="job-change-title"><div><span className="jobs-reference-section-kicker">İşlem özeti</span><h3 id="job-change-title">{detail.data.change.value}</h3></div><div><strong>{detail.data.change.label}</strong><p>{detail.data.change.detail ?? 'İşlem ayrıntısı mevcut.'}</p></div></section>}{detail.data.job.batchCount > 1 ? <section className="jobs-reference-batch-context" aria-labelledby="job-batch-title"><div className="jobs-reference-batch-heading"><div><span className="jobs-reference-section-kicker">Toplu işlem</span><h3 id="job-batch-title">{detail.data.job.batchCount} job · {detail.data.relatedOrders.length} sipariş</h3></div><span className="jobs-reference-batch-note">Sonuçlar sipariş bazında</span></div><div className="jobs-reference-batch-list">{detail.data.relatedOrders.map(order => <article key={order.orderId}><div><strong>Sipariş #{order.orderNumber}</strong><small>{order.customerName ?? 'Müşteri bilgisi yok'} · {order.lineCount} ürün satırı</small></div><span>{order.cargoProvider ?? 'Kargo bilgisi yok'}</span><b>{order.status}</b></article>)}{detail.data.relatedOrders.length === 0 && <p>Sipariş bağlantısı bulunamadı.</p>}</div></section> : detail.data.order && <section className="jobs-reference-order-context" aria-labelledby="job-order-context-title"><div><span className="jobs-reference-section-kicker">İlgili sipariş</span><h3 id="job-order-context-title">Sipariş #{detail.data.order.orderNumber}</h3><p>{detail.data.order.customerName ?? 'Müşteri bilgisi yok'} · {detail.data.order.lineCount} ürün satırı</p></div><div className="jobs-reference-order-facts"><p><small>Dış sipariş ID</small><strong>{detail.data.order.externalOrderId}</strong></p><p><small>Sipariş durumu</small><strong>{detail.data.order.status}</strong></p><p><small>Sipariş tarihi</small><strong>{formatOptionalJobTime(detail.data.order.orderedAt)}</strong></p><p><small>Sipariş tutarı</small><strong>{detail.data.order.netAmount.toLocaleString('tr-TR', { style: 'currency', currency: detail.data.order.currency })}</strong></p>{detail.data.order.externalPackageId && <p><small>Paket no</small><strong>{detail.data.order.externalPackageId}</strong></p>}{detail.data.order.cargoTrackingNumber && <p><small>Kargo takip no</small><strong>{detail.data.order.cargoTrackingNumber}</strong></p>}</div></section>}<div className="job-detail-facts"><p><small>Pazaryeri</small><strong>{detail.data.job.marketplace}</strong></p><p><small>İşlem</small><strong>{detail.data.job.jobType}</strong></p><p><small>Dış kimlik</small><strong>{detail.data.job.externalId ?? '—'}</strong></p><p><small>Retry sayısı</small><strong>{detail.data.job.attemptCount} / {detail.data.job.maxAttempts}</strong></p><p><small>Oluşturulma</small><strong>{formatOptionalJobTime(detail.data.job.createdAt)}</strong></p><p><small>Çalışma başlangıcı</small><strong>{formatOptionalJobTime(detail.data.job.startedAt)}</strong></p><p><small>Tamamlanma</small><strong>{formatOptionalJobTime(detail.data.job.completedAt)}</strong></p><p><small>Çalışma süresi</small><strong>{jobDuration(detail.data.job.startedAt, detail.data.job.completedAt)}</strong></p><p><small>İlk hata</small><strong>{formatOptionalJobTime(detail.data.job.firstFailedAt)}</strong></p><p><small>Son hata</small><strong>{formatOptionalJobTime(detail.data.job.lastFailedAt)}</strong></p><p><small>Sonraki deneme</small><strong>{formatOptionalJobTime(detail.data.job.nextRetryAt)}</strong></p><p><small>Correlation ID</small><strong>{detail.data.job.correlationId}</strong></p></div>{elevated && <div className="job-detail-actions">{retryable && <button type="button" disabled={action.isPending} onClick={() => action.mutate({ id: detail.data.job.id, verb: 'retry' })}>Manuel Yeniden Dene</button>}{cancellable && <button type="button" className="secondary" disabled={action.isPending} onClick={() => action.mutate({ id: detail.data.job.id, verb: 'cancel' })}>İptal et</button>}</div>}{action.isError && <div role="alert" className="error">İşlem güncellenemedi.</div>}<h3>Deneme geçmişi</h3><div className="table-wrap"><table><thead><tr><th>#</th><th>Başlangıç</th><th>Sonuç</th><th>Hata</th></tr></thead><tbody>{detail.data.attempts.map(attempt => <tr key={attempt.attemptNumber}><td>{attempt.attemptNumber}</td><td>{new Date(attempt.startedAt).toLocaleString('tr-TR')}</td><td>{attempt.completedAt ? (attempt.succeeded ? 'Başarılı' : 'Başarısız') : 'Çalışıyor'}</td><td>{attempt.errorCode ?? '—'}<small>{attempt.errorSummary ?? ''}</small></td></tr>)}{detail.data.attempts.length === 0 && <tr><td colSpan={4}>Henüz deneme yok.</td></tr>}</tbody></table></div></div>}
    </aside></div>}
  </section>
}
