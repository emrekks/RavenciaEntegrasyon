import { useEffect, useMemo, useState } from 'react'
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
}
type JobDetail = { job: JobSummary; attempts: Array<{ attemptNumber: number; startedAt: string; completedAt: string | null; succeeded: boolean; errorCode: string | null; errorSummary: string | null }> }

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

function statusClass(status: JobStatus) {
  if (status === 'SUCCEEDED') return 'badge good'
  if (status === 'PENDING' || status === 'LEASED' || status === 'RETRY_SCHEDULED') return 'badge warn'
  return 'badge neutral'
}

type JobCategory = 'ALL' | 'ORDERS' | 'PRICE_INVENTORY' | 'CATALOG' | 'INVOICES' | 'RETURNS' | 'SYSTEM'

const categoryTabs: Array<{ key: JobCategory; label: string; match: (type: string) => boolean }> = [
  { key: 'ALL', label: 'Tüm İşlemler', match: () => true },
  { key: 'ORDERS', label: 'Sipariş & Kargo', match: t => /ORDER|SHIPMENT|PACKAGE|COURIER|LABEL/i.test(t) },
  { key: 'PRICE_INVENTORY', label: 'Fiyat & Stok', match: t => /PRICE|INVENTORY|STOCK|OFFER/i.test(t) },
  { key: 'CATALOG', label: 'Ürün & Yayın', match: t => /PRODUCT|CATALOG|IMPORT|ATTRIBUTE|CATEGORY|BRAND|PUBLICATION/i.test(t) },
  { key: 'INVOICES', label: 'Faturalar', match: t => /INVOICE|EFATURAM|BILLING/i.test(t) },
  { key: 'RETURNS', label: 'İadeler', match: t => /RETURN|CLAIM/i.test(t) },
  { key: 'SYSTEM', label: 'Sistem & Test', match: t => /TEST|PROBE|PING|MIGRATION|SCAN|SCHEDULER/i.test(t) }
]

export function JobsPage({ me }: { me: Me }) {
  const client = useQueryClient()
  const [category, setCategory] = useState<JobCategory>('ALL')
  const [status, setStatus] = useState<'' | JobStatus>('')
  const [search, setSearch] = useState('')
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
  const filtered = useMemo(() => {
    const term = search.trim().toLocaleLowerCase('tr-TR')
    const categoryMatcher = categoryTabs.find(tab => tab.key === category)?.match ?? (() => true)
    return rawJobs.filter(job => {
      const matchCategory = categoryMatcher(job.jobType)
      if (!matchCategory) return false
      if (!term) return true
      return [job.jobType, job.status, job.lastErrorCode, job.lastErrorSummary, job.correlationId].some(value => value?.toLocaleLowerCase('tr-TR').includes(term))
    })
  }, [rawJobs, category, search])
  useEffect(() => { setPageNumber(1) }, [category, status, search, pageSize])
  const totalPages = Math.max(1, Math.ceil(filtered.length / pageSize))
  const currentPage = Math.min(pageNumber, totalPages)
  const pageJobs = filtered.slice((currentPage - 1) * pageSize, currentPage * pageSize)
  const selected = detail.data?.job
  const retryable = selected && ['BLOCKED', 'MANUAL_REVIEW', 'DEAD'].includes(selected.status)
  const cancellable = selected && !['LEASED', 'SUCCEEDED', 'DEAD', 'CANCELLED'].includes(selected.status)

  return <section className="content jobs-page"><div className="page-heading"><div><p className="eyebrow">Operasyon</p><h1>İşlem Takibi</h1><p className="lede">Arka plan işlemlerini, senkronizasyon raporlarını, denemeleri ve çalışma durumlarını izleyin.</p></div></div>
    <div className="order-tabs job-tabs" role="tablist" aria-label="İşlem kategorileri">
      {categoryTabs.map(tab => {
        const count = rawJobs.filter(j => tab.match(j.jobType)).length
        return (
          <button
            type="button"
            role="tab"
            aria-selected={category === tab.key}
            className={category === tab.key ? 'active' : ''}
            key={tab.key}
            onClick={() => setCategory(tab.key)}
          >
            <span>{tab.label}</span>
            <b>{count}</b>
          </button>
        )
      })}
    </div>
    <div className="order-toolbar"><input className="order-search" value={search} onChange={event => setSearch(event.target.value)} placeholder="Job türü, hata veya correlation ID ara…" aria-label="İşlem ara" /><select value={status} onChange={event => setStatus(event.target.value as '' | JobStatus)} aria-label="Duruma göre filtrele">{statuses.map(item => <option key={item.value || 'all'} value={item.value}>{item.label}</option>)}</select><label>Sayfa başına<select aria-label="Sayfa başına işlem" value={pageSize} onChange={event => setPageSize(Number(event.target.value))}>{[20, 50, 100, 200].map(value => <option key={value} value={value}>{value}</option>)}</select></label></div>
    {list.isLoading ? <p>İşlemler yükleniyor…</p> : list.isError ? <div role="alert" className="error">İşlem listesi alınamadı.</div> : <><div className="table-wrap"><table><thead><tr><th>İşlem</th><th>Durum</th><th>Deneme</th><th>Oluşturulma</th></tr></thead><tbody>{pageJobs.map(job => <tr key={job.id} onClick={() => setSelectedId(job.id)} tabIndex={0} onKeyDown={event => { if (event.key === 'Enter') setSelectedId(job.id) }} style={{ cursor: 'pointer' }}><td><strong>{job.jobType}</strong><small>{job.lastErrorCode ?? job.correlationId}</small></td><td><span className={statusClass(job.status)}>{job.status}</span></td><td>{job.attemptCount}/{job.maxAttempts}</td><td>{new Date(job.createdAt).toLocaleString('tr-TR')}</td></tr>)}{filtered.length === 0 && <tr><td colSpan={4}>Seçili kategori ve filtrelerle eşleşen kayıt bulunamadı.</td></tr>}</tbody></table></div>{filtered.length > 0 && <div className="order-pagination"><span>{(currentPage - 1) * pageSize + 1}–{Math.min(currentPage * pageSize, filtered.length)} / {filtered.length} işlem</span><div><button type="button" disabled={currentPage <= 1} onClick={() => setPageNumber(value => Math.max(1, value - 1))}>Önceki</button><b>Sayfa {currentPage} / {totalPages}</b><button type="button" disabled={currentPage >= totalPages} onClick={() => setPageNumber(value => Math.min(totalPages, value + 1))}>Sonraki</button></div></div>}</>}
    {selectedId && <article className="panel" style={{ marginTop: 18 }}><div className="panel-title"><div><h2>İşlem ayrıntısı</h2><p>{selected?.id ?? selectedId}</p></div><button type="button" className="secondary" onClick={() => setSelectedId(null)}>Kapat</button></div>{detail.isLoading ? <p>Yükleniyor…</p> : detail.isError || !detail.data ? <div role="alert" className="error">İşlem ayrıntısı alınamadı.</div> : <><p><strong>Correlation ID:</strong> {detail.data.job.correlationId}</p><p><strong>Son hata:</strong> {detail.data.job.lastErrorCode ?? 'Yok'}{detail.data.job.lastErrorSummary ? ` — ${detail.data.job.lastErrorSummary}` : ''}</p>{elevated && <div style={{ display: 'flex', gap: 10, flexWrap: 'wrap' }}>{retryable && <button type="button" disabled={action.isPending} onClick={() => action.mutate({ id: detail.data.job.id, verb: 'retry' })}>Yeniden dene</button>}{cancellable && <button type="button" className="secondary" disabled={action.isPending} onClick={() => action.mutate({ id: detail.data.job.id, verb: 'cancel' })}>İptal et</button>}</div>}{action.isError && <div role="alert" className="error">İşlem güncellenemedi.</div>}<h3>Deneme geçmişi</h3><div className="table-wrap"><table><thead><tr><th>#</th><th>Başlangıç</th><th>Sonuç</th><th>Hata</th></tr></thead><tbody>{detail.data.attempts.map(attempt => <tr key={attempt.attemptNumber}><td>{attempt.attemptNumber}</td><td>{new Date(attempt.startedAt).toLocaleString('tr-TR')}</td><td>{attempt.completedAt ? (attempt.succeeded ? 'Başarılı' : 'Başarısız') : 'Çalışıyor'}</td><td>{attempt.errorCode ?? '—'}<small>{attempt.errorSummary ?? ''}</small></td></tr>)}{detail.data.attempts.length === 0 && <tr><td colSpan={4}>Henüz deneme yok.</td></tr>}</tbody></table></div></>}</article>}
  </section>
}
