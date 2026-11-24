import { useEffect, useState, type ReactNode } from 'react'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { Link } from 'react-router'
import { hubPoll } from '../../shared/api'
import type { ApiAnswer } from '../../shared/api/api-answer'
import { queryKeys } from '../../shared/api/query-keys'
import { Pager } from '../../shared/components/Pager'
import { copyText } from '../../shared/copy-text'
import { formatDateTime } from '../../shared/format-date'
import { formatMoney } from '../../shared/format-money'
import { PanelSelect } from '../../shared/PanelSelect'
import { RrError, RrLoading, RrModal } from '../../shared/ui/rr-components'
import { jobTitle } from './job-presentation'
import './monitor-detail.css'

// Read-only detail lists behind the cells of API Sağlık: the Sorun cell of Entegrasyon Durumu (offers not sent, failed and
// review-waiting pushes, listings the marketplace lost) and the Hata Oranı / Hız Sınırı cells of API Sağlığı (the attempts
// behind the numbers). Nothing here sends or retries anything; every figure comes from the stored data the cell counts.
const PAGE_SIZE = 50

type InvalidOffers = ApiAnswer<'getPriceStockMonitorByConnectionIdInvalidOffers'>
type FailedPushes = ApiAnswer<'getPriceStockMonitorByConnectionIdFailedPushes'>
type LostLinks = ApiAnswer<'getPriceStockMonitorByConnectionIdLostLinks'>
type Attempts = ApiAnswer<'getApiHealthSignalsByConnectionIdAttempts'>

const REASONS: Record<string, string> = {
  CHANNEL_OFFER_INVALID: 'Fiyat girilmemiş ya da liste fiyatı satış fiyatından düşük',
  HEPSIBURADA_LISTING_LINK_REQUIRED: 'Hepsiburada ilan bağlantısı gerekli',
  PAZARAMA_BARCODE_INVALID: 'Pazarama barkodu geçersiz'
}
export const reasonText = (code: string) => REASONS[code] ?? code

const SIDES: Record<string, string> = { marketplace: 'Pazaryeri tarafı', ours: 'Bizim taraf', rateLimited: 'Kota sınırı', workflowOther: 'İş akışı beklemesi / diğer' }
export const sideText = (value: string) => SIDES[value] ?? value

function qs(values: Record<string, string | number>) {
  return Object.entries(values).filter(([, value]) => value !== '').map(([key, value]) => `${key}=${encodeURIComponent(String(value))}`).join('&')
}

function useDebounced(value: string, delay = 300) {
  const [debounced, setDebounced] = useState(value)
  useEffect(() => { const timer = window.setTimeout(() => setDebounced(value), delay); return () => window.clearTimeout(timer) }, [value, delay])
  return debounced
}

const money = (value: number | null | undefined, currency?: string | null) => value == null ? '—' : formatMoney(value, currency ?? 'TRY')

function CopyCode({ value, label }: { value: string | null | undefined; label: string }) {
  const [copied, setCopied] = useState(false)
  if (!value) return <span className="md-code md-none">—</span>
  return <button type="button" className="md-code" title={`${label} kopyala: ${value}`} onClick={() => { void copyText(value).then(done => { if (done) { setCopied(true); window.setTimeout(() => setCopied(false), 1200) } }) }}>{copied ? 'Kopyalandı' : value}</button>
}

function Identity({ barcode, sku }: { barcode?: string | null; sku?: string | null }) {
  return <span className="md-ident"><CopyCode value={barcode} label="Barkod" />{sku && sku !== barcode ? <CopyCode value={sku} label="Stok kodu" /> : null}</span>
}

function Product({ title, variant }: { title?: string | null; variant?: string | null }) {
  return <span className="md-prod"><b title={title ?? undefined}>{title || 'Ürün adı yok'}</b>{variant ? <small>{variant}</small> : null}</span>
}

// The product opens in a new tab (this window and the list stay where they are); a job opens its detail over the page.
const openProduct = (productId?: string | null) => productId ? <Link className="rr-btn rr-link" to={`/products/${productId}`} target="_blank" rel="noopener noreferrer">Ürünü Aç</Link> : <span className="md-none">—</span>
function OpenJob({ jobId, onOpenJob }: { jobId: string; onOpenJob?: (jobId: string) => void }) {
  return onOpenJob ? <button type="button" className="rr-btn rr-link" onClick={() => onOpenJob(jobId)}>İşi Aç</button> : <Link className="rr-btn rr-link" to={`/jobs?ara=${encodeURIComponent(jobId)}`}>İşi Aç</Link>
}

// The list shrank under the person (a fix elsewhere, a refresh): a page past the last one is an empty page with no pager, so it goes back to the last real page.
function useClampPage(data: { items: unknown[]; pageSize: number } | undefined, total: number | undefined, page: number, setPage: (page: number) => void) {
  useEffect(() => { if (data && total != null && data.items.length === 0 && total > 0 && page > 1) setPage(Math.max(1, Math.ceil(total / data.pageSize))) }, [data, total, page, setPage])
}

function PagedFoot({ page, pageSize, total, onPage, shown }: { page: number; pageSize: number; total: number; onPage: (page: number) => void; shown: number }) {
  const pages = Math.max(1, Math.ceil(total / pageSize))
  return <div className="md-foot"><span className="rr-lead">{total.toLocaleString('tr-TR')} kayıttan {shown ? `${((page - 1) * pageSize + 1).toLocaleString('tr-TR')}–${((page - 1) * pageSize + shown).toLocaleString('tr-TR')}` : '0'} gösteriliyor</span>{pages > 1 ? <Pager page={page} totalPages={pages} onPageChange={onPage} /> : null}</div>
}

function Body({ query, empty, children }: { query: { isPending: boolean; isError: boolean; error: unknown; refetch: () => unknown }; empty: boolean; children: ReactNode }) {
  if (query.isPending) return <RrLoading />
  if (query.isError) return <RrError error={query.error} onRetry={() => void query.refetch()} />
  if (empty) return <p className="rr-lead md-empty">Bu listede kayıt yok.</p>
  return <>{children}</>
}

function InvalidOffersTab({ connectionId }: { connectionId: string }) {
  const [search, setSearch] = useState(''); const [reason, setReason] = useState(''); const [page, setPage] = useState(1)
  const term = useDebounced(search.trim())
  const query = useQuery({ queryKey: queryKeys.priceStockMonitor('invalid-offers', connectionId, term, reason, page), placeholderData: keepPreviousData,
    queryFn: ({ signal }) => hubPoll<InvalidOffers>(`/price-stock-monitor/${connectionId}/invalid-offers?${qs({ search: term, reason, page, pageSize: PAGE_SIZE })}`, { signal }) })
  const data = query.data
  useClampPage(data, data?.filteredCount, page, setPage)
  return <>
    <div className="md-filters">
      <label className="rr-fld rr-search"><small>Ara</small><input className="rr-in" aria-label="Gönderilemeyen tekliflerde ara" placeholder="Barkod, stok kodu veya ürün adı" value={search} onChange={event => { setSearch(event.target.value); setPage(1) }} /></label>
      <label className="rr-fld"><small>Sebep</small><PanelSelect ariaLabel="Sebep" value={reason} onChange={value => { setReason(value); setPage(1) }} width={320}
        options={[{ value: '', label: `Tüm Sebepler${data ? ` (${data.totalCount.toLocaleString('tr-TR')})` : ''}` }, ...(data?.reasons ?? []).map(item => ({ value: item.code, label: `${reasonText(item.code)} (${item.count.toLocaleString('tr-TR')})` }))]} /></label>
    </div>
    <Body query={query} empty={!data?.items.length}>{data && <>
      <div className="md-wrap"><table className="md-tbl"><thead><tr><th>Barkod / Stok Kodu</th><th>Ürün</th><th>Sebep</th><th>Panel Fiyatı (Liste / Satış)</th><th>Teklif Fiyatı (Liste / Satış)</th><th /></tr></thead><tbody>
        {data.items.map(item => <tr key={`${item.variantId}-${item.offerId ?? ''}`}>
          <td><Identity barcode={item.barcode} sku={item.sku} /></td>
          <td><Product title={item.productTitle} variant={item.variantLabel} /></td>
          <td title={item.reasonSummary}>{reasonText(item.reasonCode)}</td>
          <td className="md-num">{money(item.panelListPrice, item.currency)} / {money(item.panelSalePrice, item.currency)}</td>
          <td className="md-num">{money(item.offerListPrice, item.currency)} / {money(item.offerSalePrice, item.currency)}</td>
          <td className="md-act">{openProduct(item.productId)}</td></tr>)}
      </tbody></table></div>
      <PagedFoot page={data.page} pageSize={data.pageSize} total={data.filteredCount} shown={data.items.length} onPage={setPage} />
    </>}</Body>
  </>
}

function FailedPushesTab({ connectionId, kind, onOpenJob }: { connectionId: string; kind: 'failed' | 'manual-review'; onOpenJob?: (jobId: string) => void }) {
  const [page, setPage] = useState(1)
  const query = useQuery({ queryKey: queryKeys.priceStockMonitor('failed-pushes', connectionId, kind, page), placeholderData: keepPreviousData,
    queryFn: ({ signal }) => hubPoll<FailedPushes>(`/price-stock-monitor/${connectionId}/failed-pushes?${qs({ kind, page, pageSize: PAGE_SIZE })}`, { signal }) })
  const data = query.data
  useClampPage(data, data?.totalCount, page, setPage)
  return <Body query={query} empty={!data?.items.length}>{data && <>
    <div className="md-wrap"><table className="md-tbl"><thead><tr><th>Zaman</th><th>Durum</th><th>Hata</th><th /></tr></thead><tbody>
      {data.items.map(item => <tr key={item.jobId}>
        <td className="md-nowrap">{item.at ? formatDateTime(item.at) : '—'}</td>
        <td>{item.status}</td>
        <td title={item.errorSummary ?? undefined}>{item.errorCode ? <b className="md-errcode">{item.errorCode}</b> : null}{item.errorSummary ?? (item.errorCode ? '' : '—')}</td>
        <td className="md-act"><OpenJob jobId={item.jobId} onOpenJob={onOpenJob} /></td></tr>)}
    </tbody></table></div>
    <PagedFoot page={data.page} pageSize={data.pageSize} total={data.totalCount} shown={data.items.length} onPage={setPage} />
  </>}</Body>
}

function LostLinksTab({ connectionId, notFound, rejected }: { connectionId: string; notFound: number; rejected: number }) {
  const [health, setHealth] = useState(notFound > 0 || rejected === 0 ? 'NOT_FOUND' : 'REJECTED'); const [page, setPage] = useState(1)
  const query = useQuery({ queryKey: queryKeys.priceStockMonitor('lost-links', connectionId, health, page), placeholderData: keepPreviousData,
    queryFn: ({ signal }) => hubPoll<LostLinks>(`/price-stock-monitor/${connectionId}/lost-links?${qs({ health, page, pageSize: PAGE_SIZE })}`, { signal }) })
  const data = query.data
  useClampPage(data, data?.totalCount, page, setPage)
  return <>
    <div className="md-filters"><label className="rr-fld"><small>Durum</small><PanelSelect ariaLabel="Durum" value={health} onChange={value => { setHealth(value); setPage(1) }} width={240}
      options={[{ value: 'NOT_FOUND', label: `Bulunamadı (${notFound.toLocaleString('tr-TR')})` }, { value: 'REJECTED', label: `Reddedildi (${rejected.toLocaleString('tr-TR')})` }]} /></label></div>
    <Body query={query} empty={!data?.items.length}>{data && <>
      <div className="md-wrap"><table className="md-tbl"><thead><tr><th>Barkod / Stok Kodu</th><th>Ürün</th><th>Durum</th><th>Tarihten Beri</th><th /></tr></thead><tbody>
        {data.items.map(item => <tr key={item.variantId}>
          <td><Identity barcode={item.barcode} sku={item.sku} /></td>
          <td><Product title={item.productTitle} variant={item.variantLabel} /></td>
          <td>{item.healthStatus === 'REJECTED' ? 'Reddedildi' : 'Bulunamadı'}</td>
          <td className="md-nowrap">{item.since ? formatDateTime(item.since) : '—'}</td>
          <td className="md-act">{openProduct(item.productId)}</td></tr>)}
      </tbody></table></div>
      <PagedFoot page={data.page} pageSize={data.pageSize} total={data.totalCount} shown={data.items.length} onPage={setPage} />
    </>}</Body>
  </>
}

export type ProblemCounts = { invalidOffers: number; failedPushes: number; manualReview: number; notFoundLinks: number; rejectedLinks: number }
export const problemTotal = (counts: ProblemCounts) => counts.invalidOffers + counts.failedPushes + counts.manualReview + counts.notFoundLinks + counts.rejectedLinks

// The Sorun cell's window: the title count is the cell's number; a tab per kind of problem, each with its own count.
export function PriceStockProblemDialog({ connectionId, displayName, counts, onClose, onOpenJob }: { connectionId: string; displayName: string; counts: ProblemCounts; onClose: () => void; onOpenJob?: (jobId: string) => void }) {
  // A job opened from here opens over this window, which stays as it is underneath (page, search and scroll kept).
  const openJob = onOpenJob
  const tabs = [
    { id: 'invalid', label: 'Gönderilemeyen Teklif', count: counts.invalidOffers },
    { id: 'failed', label: 'Başarısız Gönderim', count: counts.failedPushes },
    { id: 'review', label: 'İnceleme Bekleyen', count: counts.manualReview },
    { id: 'lost', label: 'Kayıp Bağlantı', count: counts.notFoundLinks + counts.rejectedLinks }
  ].filter(tab => tab.count > 0)
  const [active, setActive] = useState(tabs[0]?.id ?? 'invalid')
  const current = tabs.find(tab => tab.id === active) ?? tabs[0]
  const total = problemTotal(counts)
  const title = `${tabs.length === 1 && current ? current.label.replace('Teklif', 'Teklifler') : 'Gönderim Sorunları'} · ${displayName} (${total.toLocaleString('tr-TR')})`
  return <RrModal crumb="API Sağlık" title={title} size="xl" modalClass="md-modal" onClose={onClose} foot={<button type="button" className="rr-btn" onClick={onClose}>Kapat</button>}>
    {tabs.length > 1 && <div className="md-tabs" role="tablist" aria-label="Sorun türü">{tabs.map(tab => <button key={tab.id} type="button" role="tab" aria-selected={tab.id === current?.id} className={tab.id === current?.id ? 'md-on' : undefined} onClick={() => setActive(tab.id)}>{tab.label}<span className="rr-num">{tab.count.toLocaleString('tr-TR')}</span></button>)}</div>}
    <p className="rr-lead md-lead">Yalnız okuma: buradan bir şey gönderilmez veya yeniden denenmez. Düzeltmek için ürüne gidin.</p>
    {current?.id === 'invalid' && <InvalidOffersTab key="invalid" connectionId={connectionId} />}
    {current?.id === 'failed' && <FailedPushesTab key="failed" connectionId={connectionId} kind="failed" onOpenJob={openJob} />}
    {current?.id === 'review' && <FailedPushesTab key="review" connectionId={connectionId} kind="manual-review" onOpenJob={openJob} />}
    {current?.id === 'lost' && <LostLinksTab key="lost" connectionId={connectionId} notFound={counts.notFoundLinks} rejected={counts.rejectedLinks} />}
  </RrModal>
}

// The Hata Oranı and Hız Sınırı cells' window: the four numbers behind the rate, the formula, then the failed attempts.
export function ApiHealthAttemptsDialog({ connectionId, displayName, kind, onClose, onOpenJob }: { connectionId: string; displayName: string; kind: 'errorRate' | 'rateLimit'; onClose: () => void; onOpenJob?: (jobId: string) => void }) {
  const openJob = onOpenJob
  const [side, setSide] = useState(kind === 'rateLimit' ? 'rateLimited' : ''); const [page, setPage] = useState(1)
  const query = useQuery({ queryKey: queryKeys.apiHealthSignals('attempts', connectionId, side, page), placeholderData: keepPreviousData,
    queryFn: ({ signal }) => hubPoll<Attempts>(`/api-health-signals/${connectionId}/attempts?${qs({ class: side, page, pageSize: PAGE_SIZE })}`, { signal }) })
  const data = query.data
  useClampPage(data, data?.totalCount, page, setPage)
  const totals = data?.totals
  const count = (value: number | undefined) => value == null ? '—' : value.toLocaleString('tr-TR')
  const title = `${kind === 'rateLimit' ? 'Hız Sınırı' : 'Hata Oranı'} · ${displayName} (son 24 saat)`
  return <RrModal crumb="API Sağlığı" title={title} size="xl" modalClass="md-modal" onClose={onClose} foot={<button type="button" className="rr-btn" onClick={onClose}>Kapat</button>}>
    <dl className="md-totals">
      <div><dt>Toplam deneme</dt><dd className="rr-num">{count(totals?.attempts)}</dd></div>
      <div><dt>Pazaryeri tarafı</dt><dd className="rr-num">{count(totals?.remoteSide)}</dd></div>
      <div><dt>Bizim taraf</dt><dd className="rr-num">{count(totals?.ourSide)}</dd></div>
      <div><dt>Kota sınırı</dt><dd className="rr-num">{count(totals?.rateLimited)}</dd></div>
    </dl>
    <p className="rr-lead md-lead">Oran = (pazaryeri tarafı + bizim taraf + kota sınırı) ÷ toplam deneme; iş akışı beklemesi orana katılmaz{totals?.workflowWait ? ` (${count(totals.workflowWait)} deneme)` : ''}. Pazaryeri tarafı: sunucu hatası, zaman aşımı, ağ. Bizim taraf: pazaryerinin reddettiği istekler.</p>
    <div className="md-filters"><label className="rr-fld"><small>Taraf</small><PanelSelect ariaLabel="Taraf" value={side} onChange={value => { setSide(value); setPage(1) }} width={260}
      options={[{ value: '', label: 'Tüm Başarısız Denemeler' }, ...(data?.classes ?? ['marketplace', 'ours', 'rateLimited', 'workflowOther']).map(value => ({ value, label: sideText(value) }))]} /></label></div>
    <Body query={query} empty={!data?.items.length}>{data && <>
      <div className="md-wrap"><table className="md-tbl"><thead><tr><th>Zaman</th><th>İş</th><th>Hata</th><th>Taraf</th><th /></tr></thead><tbody>
        {data.items.map(item => <tr key={`${item.jobId}-${item.attemptedAt}`}>
          <td className="md-nowrap">{formatDateTime(item.attemptedAt)}</td>
          <td>{jobTitle(item.jobType)}</td>
          <td title={item.errorSummary ?? undefined}>{item.errorCode ? <b className="md-errcode">{item.errorCode}</b> : null}{item.errorSummary ?? (item.errorCode ? '' : '—')}</td>
          <td>{sideText(item.class)}</td>
          <td className="md-act"><OpenJob jobId={item.jobId} onOpenJob={openJob} /></td></tr>)}
      </tbody></table></div>
      <PagedFoot page={data.page} pageSize={data.pageSize} total={data.totalCount} shown={data.items.length} onPage={setPage} />
    </>}</Body>
  </RrModal>
}
