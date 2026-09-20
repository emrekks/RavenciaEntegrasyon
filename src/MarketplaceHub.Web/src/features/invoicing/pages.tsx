import { useEffect, useState } from 'react'
import { Link, useParams, useSearchParams } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi, loadAllPages } from '../../shared/api'
import { Busy, ErrorBox, Pagination, Tabs, UiIcon } from '../../shared/components'
import { statusLabel } from '../../shared/status-labels'
import { appendNotification } from '../../shared/notifications'

type Invoice = { id: string; orderNumber: string; invoiceType: string; status: string; currency: string; payableTotal: number; invoiceNumber: string | null; dueAt: string | null; createdAt: string; version: number }
type InvoiceWorkspaceLine = { sku: string; barcode: string | null; description: string; quantity: number; unitPrice: number; vatRate: number; imageUrl: string | null }
type InvoiceWorkspace = { orderId: string; packageId: string; orderNumber: string; customerName: string; orderedAt: string; shipmentStatus: string; deliveredAt: string | null; invoiceDueAt: string | null; isDueSoon: boolean; currency: string; amount: number; productCount: number; primaryImageUrl: string | null; cargoProviderName: string | null; cargoTrackingNumber: string | null; invoiceId: string | null; invoiceStatus: string; invoiceNumber: string | null; canCreateInvoice: boolean; shipmentAddressJson: string | null; invoiceAddressJson: string | null; lines: InvoiceWorkspaceLine[] | null; invoiceErrorCode: string | null; invoiceDeliveryStatus: string | null; invoiceDeliveryReference: string | null; invoiceDocumentAvailable: boolean }
type InvoiceDetail = Invoice & { orderId: string; packageId: string | null; providerConnectionId: string; sequencePurpose: string; ettnUuid: string | null; taxExclusiveTotal: number; discountTotal: number; taxTotal: number; note: string; issuedAt: string | null; lastErrorCode: string | null; lines: Array<{ id: string; lineSequence: number; description: string; sku: string | null; unit: string; quantity: number; unitPrice: number; discountAmount: number; vatRate: number; vatAmount: number; lineTotal: number }>; documents: Array<{ id: string; documentType: string; sha256: string; createdAt: string }>; attempts: Array<{ attemptNumber: number; outcome: string; errorCode: string | null; startedAt: string; completedAt: string | null }>; deliveries: Array<{ id: string; deliveryType: string; status: string; externalReference: string | null; errorCode: string | null; createdAt: string }>; allowedActions: string[]; requiresSensitiveConfirmation: boolean }
type Connection = { id: string; platformCode: string; displayName: string; status: string; hasCredential: boolean }
type InvoiceNoticeKind = 'success' | 'error' | 'info'

function idempotency() { return crypto.randomUUID() }
async function waitForInvoiceCompletion(invoiceId: string) {
  const successfulStatuses = new Set(['ACCEPTED', 'COMPLETED'])
  const failedStatuses = new Set(['REJECTED', 'VALIDATION_FAILED', 'MANUAL_REVIEW', 'MARKETPLACE_FAILED', 'CANCELLED', 'CANCELLED_LOCAL'])
  for (let attempt = 0; attempt < 120; attempt += 1) {
    const invoice = await hubApi<InvoiceDetail>(`/invoices/${invoiceId}`)
    const normalizedStatus = invoice.status.trim().toUpperCase()
    if (successfulStatuses.has(normalizedStatus)) return invoice
    if (failedStatuses.has(normalizedStatus)) throw new Error(invoiceFailureToast(invoice))
    if (attempt < 119) await new Promise(resolve => window.setTimeout(resolve, 2000))
  }
  throw new Error('Fatura sağlayıcıda hâlâ işleniyor. İşlem arka planda devam ediyor; lütfen İşlem takibi ekranından sonucu kontrol edin.')
}
async function submitInvoice(item: InvoiceWorkspace, provider: Connection | undefined) {
  if (!provider) throw new Error('Aktif Trendyol E-Faturam bağlantısı gereklidir.')
  let invoice: InvoiceDetail
  if (item.invoiceId) {
    invoice = await hubApi<InvoiceDetail>(`/invoices/${item.invoiceId}`)
    if (!invoice.allowedActions.includes('SUBMIT')) throw new Error('Bu fatura tekrar gönderilebilir durumda değil.')
  } else {
    const draft = await hubApi<InvoiceDetail>('/invoices', { method: 'POST', headers: { 'Idempotency-Key': `invoice:${item.orderId}:${item.packageId}` }, body: JSON.stringify({ orderId: item.orderId, packageId: item.packageId, providerConnectionId: provider.id, originalInvoiceId: null }) })
    invoice = await hubApi<InvoiceDetail>(`/invoices/${draft.id}/validate`, { method: 'POST', headers: { 'If-Match': `"v${draft.version}"` } })
  }
  await hubApi(`/invoices/${invoice.id}/submit-jobs`, { method: 'POST', headers: { 'Idempotency-Key': item.invoiceId ? `invoice-submit-retry:${invoice.id}:${idempotency()}` : `invoice-submit:${invoice.id}`, 'If-Match': `"v${invoice.version}"` }, body: JSON.stringify({ password: '', confirmed: false }) })
  return invoice.id
}
function requiresInvoiceAction(item: InvoiceWorkspace) { return item.canCreateInvoice || item.invoiceStatus === 'FATURA_REDDEDILDI' }
function invoiceFailureReason(code: string | null) {
  const labels: Record<string, string> = {
    EFATURAM_FISCAL_PAYLOAD_INVALID: 'Fatura bilgileri sağlayıcının istediği biçimde değil.',
    EFATURAM_REQUEST_REJECTED: 'E-Faturam isteği reddetti.',
    EFATURAM_APPLICATION_NOT_ACTIVE: 'E-Faturam fatura uygulaması bu hesap için aktif değil.',
    EFATURAM_AUTHENTICATION_FAILED: 'E-Faturam kimlik doğrulamasını kabul etmedi.',
    EFATURAM_ACCESS_TOKEN_REJECTED: 'E-Faturam erişim anahtarını reddetti.',
    EFATURAM_INVOICE_CREATE_PRIVILEGE_MISSING: 'Bu hesapta fatura oluşturma yetkisi bulunmuyor.',
    REMOTE_INVOICE_REJECTED: 'Pazaryeri faturayı reddetti.'
  }
  return labels[code ?? ''] ?? (code ? `Fatura oluşturulamadı: ${code}` : 'Fatura oluşturulamadı. Detayları açın.')
}
function invoiceFailureToast(invoice: Pick<InvoiceDetail, 'lastErrorCode'>) {
  const reason = invoiceFailureReason(invoice.lastErrorCode)
  const summary = reason.startsWith('Fatura oluşturulamadı:') ? 'Fatura sağlayıcısı işlemi kabul etmedi.' : reason
  return `Fatura oluşturulamadı. ${summary} Ayrıntılar için “Detayları aç” seçeneğini kullanabilirsiniz.`
}
function invoiceFailureGuidance(code: string | null) {
  const labels: Record<string, string> = {
    EFATURAM_FISCAL_PAYLOAD_INVALID: 'Fatura detaylarını açıp satır, tutar, vergi ve adres bilgilerini kontrol edin.',
    EFATURAM_REQUEST_REJECTED: 'Provider yanıtını kontrol edin; bilgiler düzeltildikten sonra Tekrar dene seçeneğini kullanın.',
    EFATURAM_APPLICATION_NOT_ACTIVE: 'Entegrasyonlar ekranından E-Faturam uygulamasını etkinleştirip bağlantıyı yeniden test edin.',
    EFATURAM_AUTHENTICATION_FAILED: 'Entegrasyonlar ekranından E-Faturam kimlik bilgilerini yenileyip bağlantıyı test edin.',
    EFATURAM_ACCESS_TOKEN_REJECTED: 'E-Faturam bağlantısını yeniden yetkilendirin, ardından faturayı tekrar deneyin.',
    EFATURAM_INVOICE_CREATE_PRIVILEGE_MISSING: 'E-Faturam hesabında fatura oluşturma yetkisini açtırın.',
    REMOTE_INVOICE_REJECTED: 'Provider veya pazaryeri ret nedenini kontrol edip gerekli bilgileri düzelttikten sonra tekrar deneyin.'
  }
  return labels[code ?? ''] ?? 'Fatura detaylarını açıp son hata kodunu ve provider denemelerini kontrol edin.'
}
function Badge({ value, tone, label }: { value: string; tone?: 'good' | 'warn' | 'neutral'; label?: string }) { const normalized = value.trim().toUpperCase(); const resolvedTone = tone ?? (['READY', 'ACCEPTED', 'COMPLETED', 'ACTIVE', 'SUPPORTED', 'CANCELLED', 'DELIVERED', 'SUCCESS', 'CONFIRMED'].includes(normalized) ? 'good' : ['UNKNOWN_RESULT', 'VALIDATION_FAILED', 'MANUAL_REVIEW', 'UNAPPROVED', 'UNKNOWN', 'CANCELLATION_PENDING', 'FAILED', 'RETRYABLE_FAILURE', 'MARKETPLACE_FAILED'].includes(normalized) ? 'warn' : 'neutral'); return <span className={`badge ${resolvedTone}`}><i aria-hidden="true" />{label ?? statusLabel(value)}</span> }
function isInvoiceFailureStatus(value: string | null | undefined) {
  return ['FATURA_REDDEDILDI', 'REJECTED', 'VALIDATION_FAILED', 'MANUAL_REVIEW', 'MARKETPLACE_FAILED'].includes(value?.trim().toUpperCase() ?? '')
}
function invoiceWorkspaceStatus(item: Pick<InvoiceWorkspace, 'invoiceId' | 'invoiceStatus' | 'canCreateInvoice'>) {
  if (!item.invoiceId || item.canCreateInvoice || item.invoiceStatus === 'FATURA_BEKLIYOR') return { value: 'FATURA_BEKLIYOR', tone: 'warn' as const, label: 'Fatura bekliyor' }
  if (isInvoiceFailureStatus(item.invoiceStatus)) return { value: item.invoiceStatus, tone: 'warn' as const, label: 'Hatalı' }
  if (item.invoiceStatus === 'FATURA_KESILDI' || item.invoiceStatus === 'COMPLETED') return { value: item.invoiceStatus, tone: 'good' as const, label: 'Fatura oluşturuldu' }
  return { value: item.invoiceStatus || 'FATURA_BILINMIYOR', tone: 'warn' as const, label: 'Fatura işleniyor' }
}
function invoiceDeliveryLabel(status: string | null | undefined) {
  if (!status) return 'Platforma aktarım başlatılmadı'
  return ({ STARTED: 'Aktarım başlatıldı', SUBMITTED: 'Platforma gönderildi', CONFIRMATION_RETRYABLE: 'Platform doğrulaması bekleniyor', CONFIRMED: 'Platformda doğrulandı', RETRYABLE_FAILURE: 'Yeniden denenecek', FAILED: 'Platform aktarımı başarısız', UNKNOWN: 'Aktarım sonucu bekleniyor' } as Record<string, string>)[status] ?? statusLabel(status)
}
function invoiceDeliveryTone(status: string | null | undefined): 'good' | 'warn' | 'neutral' {
  if (status === 'CONFIRMED') return 'good'
  if (status) return 'warn'
  return 'neutral'
}
function isCancelledShipment(item: Pick<InvoiceWorkspace, 'shipmentStatus'>) {
  return ['CANCELLED', 'CANCELED'].includes(item.shipmentStatus.trim().toUpperCase())
}
function actionLabel(action: string) { return ({ SUBMIT: 'E-Faturam’a gönder', STAGE_CAPABILITY_PROBE: 'Stage mali canary çalıştır', RECONCILE: 'Durumu uzlaştır', DELIVER: 'Trendyol’a fatura linkini ilet', CANCEL: 'E-Arşiv iptal isteği', VALIDATE: 'Yerel doğrula' } as Record<string, string>)[action] ?? action }
function addressLines(value: string | null | undefined) {
  if (!value) return []
  try {
    const parsed = JSON.parse(value) as unknown
    const preferred = ['fullAddress', 'address', 'addressLine1', 'addressLine2', 'street', 'neighborhood', 'district', 'city', 'province', 'postalCode', 'zipCode', 'phone']
    const values: string[] = []
    const visit = (node: unknown) => {
      if (typeof node !== 'object' || node === null) return
      if (Array.isArray(node)) { node.forEach(visit); return }
      const record = node as Record<string, unknown>
      preferred.forEach(key => { const item = record[key]; if (typeof item === 'string' && item.trim() && !values.includes(item.trim())) values.push(item.trim()) })
      Object.values(record).forEach(child => { if (typeof child === 'object') visit(child) })
    }
    visit(parsed)
    return values.slice(0, 8)
  } catch { return [] }
}

export function InvoicesPage() {
  const client = useQueryClient(); const [search, setSearch] = useState(''); const [tab, setTab] = useState('UNINVOICED'); const [shipmentStatusFilter, setShipmentStatusFilter] = useState('ALL'); const [cargoFilter, setCargoFilter] = useState('ALL'); const [invoiceStatusFilter, setInvoiceStatusFilter] = useState('ALL'); const [dateFrom, setDateFrom] = useState(''); const [dateTo, setDateTo] = useState(''); const [columnFilterOpen, setColumnFilterOpen] = useState<'cargo' | 'shipment' | 'invoice' | null>(null); const [message, setMessageState] = useState(''); const [messageKind, setMessageKind] = useState<InvoiceNoticeKind>('info'); const [pageSize, setPageSize] = useState(20); const [pageNumber, setPageNumber] = useState(1); const [selectedItem, setSelectedItem] = useState<InvoiceWorkspace | null>(null)
  function setMessage(value: string, kind: InvoiceNoticeKind = 'info') { setMessageState(value); setMessageKind(kind); if (value) appendNotification(value, kind) }
  useEffect(() => {
    if (!message) return
    const timeout = window.setTimeout(() => setMessageState(''), messageKind === 'info' ? 7000 : 5500)
    return () => window.clearTimeout(timeout)
  }, [message, messageKind])
  const query = useQuery({ queryKey: ['invoice-workspace'], queryFn: () => hubApi<InvoiceWorkspace[]>('/invoice-workspace') })
  const connections = useQuery({ queryKey: ['connections', 'billing-workspace'], queryFn: () => loadAllPages<Connection>('/connections') })
  const provider = connections.data?.items.find(x => x.platformCode === 'TRENDYOL_EFATURAM' && (x.status === 'ACTIVE' || x.status === 'VERIFIED'))
  const create = useMutation({ mutationFn: async (item: InvoiceWorkspace) => { const invoiceId = await submitInvoice(item, provider); setMessage(`#${item.orderNumber} için fatura sağlayıcıda işleniyor…`); return waitForInvoiceCompletion(invoiceId) }, onMutate: item => setMessage(`#${item.orderNumber} için fatura oluşturuluyor…`), onSuccess: async () => { setMessage('Fatura başarıyla oluşturuldu.', 'success'); await client.invalidateQueries({ queryKey: ['invoice-workspace'] }) }, onError: error => setMessage(error instanceof Error ? error.message : 'Fatura oluşturulamadı.', 'error') })
  const items = (query.data ?? []).filter(item => !isCancelledShipment(item)); const normalized = search.trim().toLocaleLowerCase('tr-TR')
  const cargoOptions = Array.from(new Set(items.map(item => item.cargoProviderName?.trim()).filter((value): value is string => Boolean(value)))).sort((left, right) => left.localeCompare(right, 'tr-TR'))
  const shipmentStatusOptions = Array.from(new Set(items.map(item => item.shipmentStatus.trim()).filter(Boolean))).sort((left, right) => statusLabel(left).localeCompare(statusLabel(right), 'tr-TR'))
  const invoiceStatusOptions = Array.from(new Map(items.map(item => { const value = invoiceWorkspaceStatus(item); return [value.value, value.label] })).entries())
  const dateFromTime = dateFrom ? new Date(`${dateFrom}T00:00:00`).getTime() : Number.NEGATIVE_INFINITY
  const dateToTime = dateTo ? new Date(`${dateTo}T23:59:59.999`).getTime() : Number.POSITIVE_INFINITY
  const hasInvoiceFilters = Boolean(search.trim() || shipmentStatusFilter !== 'ALL' || cargoFilter !== 'ALL' || invoiceStatusFilter !== 'ALL' || dateFrom || dateTo)
  const visible = items.filter(item => {
    const tabMatch = tab === 'UNINVOICED' ? requiresInvoiceAction(item) : tab === 'INVOICED' ? !requiresInvoiceAction(item) : item.isDueSoon
    const shipmentMatch = shipmentStatusFilter === 'ALL' || item.shipmentStatus === shipmentStatusFilter
    const cargoMatch = cargoFilter === 'ALL' || item.cargoProviderName?.trim() === cargoFilter
    const invoiceMatch = invoiceStatusFilter === 'ALL' || invoiceWorkspaceStatus(item).value === invoiceStatusFilter
    const orderedAt = new Date(item.orderedAt).getTime()
    const dateMatch = orderedAt >= dateFromTime && orderedAt <= dateToTime
    return tabMatch && shipmentMatch && cargoMatch && invoiceMatch && dateMatch && (!normalized || [item.orderNumber, item.customerName, item.invoiceNumber ?? '', item.cargoTrackingNumber ?? ''].some(value => value.toLocaleLowerCase('tr-TR').includes(normalized)))
  })
  const totalPages = Math.max(1, Math.ceil(visible.length / pageSize)); const currentPage = Math.min(pageNumber, totalPages); const pageItems = visible.slice((currentPage - 1) * pageSize, currentPage * pageSize)
  useEffect(() => { setPageNumber(1) }, [search, tab, shipmentStatusFilter, cargoFilter, invoiceStatusFilter, dateFrom, dateTo, pageSize])
  const tabs = [['UNINVOICED', 'Faturalandırılmamışlar'], ['INVOICED', 'Faturalandırılmışlar'], ['DUE_SOON', 'Süresi Yaklaşanlar']] as const
  const counts = { unInvoiced: items.filter(requiresInvoiceAction).length, invoiced: items.filter(x => !requiresInvoiceAction(x)).length, dueSoon: items.filter(x => x.isDueSoon).length }
  const activeTabLabel = tabs.find(([value]) => value === tab)?.[1] ?? 'Faturalar'
  return <section className="content f3 invoices-page reference-invoices-page">
    <div className="page-heading invoices-reference-heading">
      <div><p className="eyebrow">Mali belgeler</p><h1>Faturalar</h1><p className="lede">Faturaları paket, teslimat ve ödeme bilgileriyle tek çalışma alanında takip edin.</p></div>
      <div className="invoices-reference-heading-actions"><span className="invoice-safety-status"><i aria-hidden="true" /> Manuel işlem güvenli</span><Badge value="DUPLICATE SAFE" /></div>
    </div>
    {message && <div role={messageKind === 'error' ? 'alert' : 'status'} className={`notice invoice-provider-toast ${messageKind}`}>{message}</div>}
    <div className="invoice-reference-metrics">
      <article className="invoice-metric-pending"><small>Fatura bekleyen</small><strong>{counts.unInvoiced}</strong><span>paket bazlı işlem</span></article>
      <article className="invoice-metric-due"><small>Süresi yaklaşan</small><strong>{counts.dueSoon}</strong><span>teslimden 5 gün geçen</span></article>
      <article className="invoice-metric-complete"><small>Faturalandırılan</small><strong>{counts.invoiced}</strong><span>ikinci fatura kapalı</span></article>
      <article className="invoice-metric-total"><small>Toplam paket</small><strong>{items.length}</strong><span>fatura çalışma alanı</span></article>
    </div>
    <div className="invoice-reference-filter-shell">
      <Tabs className="invoice-reference-tabs" ariaLabel="Fatura görünümleri" value={tab} onChange={value => setTab(value as typeof tab)} items={tabs.map(([value, label]) => ({ value, label, count: value === 'UNINVOICED' ? counts.unInvoiced : value === 'INVOICED' ? counts.invoiced : counts.dueSoon }))} />
      <section className="invoice-reference-filters" aria-label="Fatura filtreleri">
        <label className="invoice-reference-search"><span>Fatura ara</span><span className="invoice-reference-search-control"><UiIcon name="search" size={18} /><input aria-label="Fatura ara" placeholder="Sipariş, müşteri, fatura veya takip no ara…" value={search} onChange={event => setSearch(event.target.value)} /></span></label>
        <label className="invoice-reference-date-filter"><span>Sipariş tarihi</span><span className="invoice-reference-date-range"><input type="date" aria-label="Başlangıç tarihi" value={dateFrom} max={dateTo || undefined} onChange={event => setDateFrom(event.target.value)} /><span aria-hidden="true">–</span><input type="date" aria-label="Bitiş tarihi" value={dateTo} min={dateFrom || undefined} onChange={event => setDateTo(event.target.value)} /></span></label>
        {hasInvoiceFilters && <button type="button" className="secondary invoice-reference-filter-reset" onClick={() => { setSearch(''); setShipmentStatusFilter('ALL'); setCargoFilter('ALL'); setInvoiceStatusFilter('ALL'); setDateFrom(''); setDateTo(''); setColumnFilterOpen(null) }}>Filtreleri temizle</button>}
      </section>
    </div>
    <section className="invoice-reference-workspace">
      <header><div><h2>{activeTabLabel}</h2><p>Filtreleme sonuçları: {visible.length} paket · Fatura işlemleri kayıt bazında yürütülür.</p></div><label className="invoice-reference-page-size">Sayfa başına<select aria-label="Sayfa başına fatura" value={pageSize} onChange={event => setPageSize(Number(event.target.value))}>{[20, 50, 100, 200].map(value => <option key={value} value={value}>{value}</option>)}</select></label></header>
      {query.isLoading ? <div className="invoice-reference-state"><Busy /></div> : query.isError ? <div className="invoice-reference-state"><ErrorBox error={query.error} /></div> : !visible.length ? <div className="invoice-reference-state"><div className="empty"><strong>Kayıt yok</strong><p>Seçili fatura sekmesi ve filtrelerle eşleşen paket bulunamadı.</p></div></div> : <><div className="invoice-reference-table" role="table">
        <div className="invoice-reference-head" role="row"><strong>Sipariş bilgileri</strong><strong>Alıcı</strong><div className={`order-column-filter${cargoFilter !== 'ALL' ? ' has-filter' : ''}`}><span className="order-column-filter-label">Kargo</span><button type="button" className="order-column-filter-trigger" aria-label="Kargo filtresini aç" aria-expanded={columnFilterOpen === 'cargo'} aria-controls={columnFilterOpen === 'cargo' ? 'invoices-cargo-filter' : undefined} onClick={() => setColumnFilterOpen(current => current === 'cargo' ? null : 'cargo')}><svg className="order-filter-funnel" viewBox="0 0 24 24" aria-hidden="true"><path d="M4 5h16l-6 7.2V18l-4 1v-6.8L4 5z" /></svg></button>{columnFilterOpen === 'cargo' && <div id="invoices-cargo-filter" className="order-column-filter-popover" role="dialog" aria-label="Kargo filtreleri"><label>Kargo firması<select aria-label="Kargo firmasına göre filtrele" value={cargoFilter} onChange={event => setCargoFilter(event.target.value)}><option value="ALL">Tüm kargolar</option>{cargoOptions.map(value => <option key={value} value={value}>{value}</option>)}</select></label>{cargoFilter !== 'ALL' && <button type="button" className="order-column-filter-reset" onClick={() => { setCargoFilter('ALL'); setColumnFilterOpen(null) }}>Filtreyi temizle</button>}</div>}</div><div className={`order-column-filter${shipmentStatusFilter !== 'ALL' ? ' has-filter' : ''}`}><span className="order-column-filter-label">Sipariş durumu</span><button type="button" className="order-column-filter-trigger" aria-label="Sipariş durumu filtresini aç" aria-expanded={columnFilterOpen === 'shipment'} aria-controls={columnFilterOpen === 'shipment' ? 'invoices-shipment-filter' : undefined} onClick={() => setColumnFilterOpen(current => current === 'shipment' ? null : 'shipment')}><svg className="order-filter-funnel" viewBox="0 0 24 24" aria-hidden="true"><path d="M4 5h16l-6 7.2V18l-4 1v-6.8L4 5z" /></svg></button>{columnFilterOpen === 'shipment' && <div id="invoices-shipment-filter" className="order-column-filter-popover" role="dialog" aria-label="Sipariş durumu filtreleri"><label>Sipariş durumu<select aria-label="Sipariş durumuna göre filtrele" value={shipmentStatusFilter} onChange={event => setShipmentStatusFilter(event.target.value)}><option value="ALL">Tüm sipariş durumları</option>{shipmentStatusOptions.map(value => <option key={value} value={value}>{statusLabel(value)}</option>)}</select></label>{shipmentStatusFilter !== 'ALL' && <button type="button" className="order-column-filter-reset" onClick={() => { setShipmentStatusFilter('ALL'); setColumnFilterOpen(null) }}>Filtreyi temizle</button>}</div>}</div><div className={`order-column-filter${invoiceStatusFilter !== 'ALL' ? ' has-filter' : ''}`}><span className="order-column-filter-label">Fatura durumu</span><button type="button" className="order-column-filter-trigger" aria-label="Fatura durumu filtresini aç" aria-expanded={columnFilterOpen === 'invoice'} aria-controls={columnFilterOpen === 'invoice' ? 'invoices-invoice-filter' : undefined} onClick={() => setColumnFilterOpen(current => current === 'invoice' ? null : 'invoice')}><svg className="order-filter-funnel" viewBox="0 0 24 24" aria-hidden="true"><path d="M4 5h16l-6 7.2V18l-4 1v-6.8L4 5z" /></svg></button>{columnFilterOpen === 'invoice' && <div id="invoices-invoice-filter" className="order-column-filter-popover invoice-filter-popover" role="dialog" aria-label="Fatura durumu filtreleri"><label>Fatura durumu<select aria-label="Fatura durumuna göre filtrele" value={invoiceStatusFilter} onChange={event => setInvoiceStatusFilter(event.target.value)}><option value="ALL">Tüm fatura durumları</option>{invoiceStatusOptions.map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label>{invoiceStatusFilter !== 'ALL' && <button type="button" className="order-column-filter-reset" onClick={() => { setInvoiceStatusFilter('ALL'); setColumnFilterOpen(null) }}>Filtreyi temizle</button>}</div>}</div><strong>Tutar</strong><strong>İşlemler</strong></div>
        {pageItems.map(item => <article className={`invoice-reference-row ${item.isDueSoon ? 'due-soon' : ''}`} key={item.packageId} role="row">
          <div className="invoice-reference-order"><div><strong>#{item.orderNumber}</strong><small>{new Date(item.orderedAt).toLocaleString('tr-TR')}</small><small>{item.invoiceNumber ?? 'Fatura numarası bekleniyor'}</small></div></div>
          <div className="invoice-reference-buyer"><strong>{item.customerName}</strong><small>{item.productCount} adet · {item.lines?.length ?? 1} çeşit</small></div>
          <div className="invoice-reference-products"><strong>{item.cargoProviderName ?? 'Kargo bilgisi yok'}</strong><small>{item.cargoTrackingNumber ?? 'Takip numarası yok'}</small><small>Paket: {item.packageId}</small></div>
          <div className="invoice-reference-shipment"><Badge value={item.shipmentStatus} /><small>{item.deliveredAt ? `Teslim: ${new Date(item.deliveredAt).toLocaleDateString('tr-TR')}` : 'Henüz teslim edilmedi'}</small></div>
          <div className="invoice-reference-status"><Badge {...invoiceWorkspaceStatus(item)} />{item.invoiceDueAt && <small className={item.isDueSoon ? 'deadline critical' : ''}>Son tarih: {new Date(item.invoiceDueAt).toLocaleDateString('tr-TR')}</small>}</div>
          <div className="invoice-reference-amount"><strong>{item.amount.toLocaleString('tr-TR', { style: 'currency', currency: item.currency })}</strong><small>{item.isDueSoon ? 'Öncelikli takip' : 'Sipariş toplamı'}</small></div>
          <div className="invoice-reference-actions">{item.invoiceId ? isInvoiceFailureStatus(item.invoiceStatus) ? <button type="button" aria-busy={create.isPending && create.variables?.packageId === item.packageId} disabled={!provider?.hasCredential || create.isPending} onClick={() => create.mutate(item)}>{create.isPending && create.variables?.packageId === item.packageId && <span className="invoice-action-spinner" aria-hidden="true" />}<span className="invoice-action-label">{create.isPending && create.variables?.packageId === item.packageId ? 'Deneniyor…' : 'Tekrar dene'}</span></button> : <Badge value={item.invoiceStatus} tone={item.invoiceStatus === 'FATURA_KESILDI' ? 'good' : 'warn'} label={item.invoiceStatus === 'FATURA_KESILDI' ? 'Faturası kesilmiş' : 'Fatura işleniyor'} /> : <button type="button" aria-busy={create.isPending && create.variables?.packageId === item.packageId} disabled={!provider?.hasCredential || create.isPending || !item.canCreateInvoice} onClick={() => create.mutate(item)}>{create.isPending && create.variables?.packageId === item.packageId && <span className="invoice-action-spinner" aria-hidden="true" />}<span className="invoice-action-label">{create.isPending && create.variables?.packageId === item.packageId ? 'İşleniyor…' : 'Fatura oluştur'}</span></button>}{item.invoiceId && item.invoiceDocumentAvailable && <a className="invoice-reference-document-link" href={`/api/v1/invoices/${item.invoiceId}/documents/latest/content`} target="_blank" rel="noreferrer">Fatura linki <UiIcon name="externalLink" /></a>}<button type="button" className="invoice-reference-details-trigger" onClick={() => setSelectedItem(item)}>Detayları aç <UiIcon name="externalLink" /></button></div>
        </article>)}
      </div><nav className="order-pagination" aria-label="Fatura sayfaları"><span>Toplam {visible.length.toLocaleString('tr-TR')} adet</span><Pagination className="pagination-controls" page={currentPage} totalPages={totalPages} onPageChange={setPageNumber} onPrevious={() => setPageNumber(value => Math.max(1, value - 1))} onNext={() => setPageNumber(value => Math.min(totalPages, value + 1))} /></nav></>}
    </section>
    {selectedItem && <div className="invoice-detail-backdrop" role="presentation" onMouseDown={() => setSelectedItem(null)}><aside className="invoice-detail-drawer" role="dialog" aria-modal="true" aria-labelledby="invoice-detail-title" onMouseDown={event => event.stopPropagation()}>
      <header className="invoice-detail-header"><div><p className="eyebrow">Sipariş ve fatura özeti</p><h2 id="invoice-detail-title">#{selectedItem.orderNumber}</h2><p>{selectedItem.customerName} · {new Date(selectedItem.orderedAt).toLocaleString('tr-TR')}</p></div><button type="button" className="modal-close" onClick={() => setSelectedItem(null)} aria-label="Detay panelini kapat"><UiIcon name="close" /></button></header>
      <div className="invoice-detail-body"><section className="invoice-detail-summary"><div><small>Sipariş durumu</small><Badge value={selectedItem.shipmentStatus} /></div><div className="invoice-detail-invoice-status"><small>Fatura durumu</small>{isInvoiceFailureStatus(selectedItem.invoiceStatus) ? <Badge value="FATURA_REDDEDILDI" tone="warn" label="Hatalı" /> : <strong>{statusLabel(selectedItem.invoiceStatus)}</strong>}{isInvoiceFailureStatus(selectedItem.invoiceStatus) && <small className="invoice-detail-error-reason">{invoiceFailureReason(selectedItem.invoiceErrorCode)}</small>}{selectedItem.invoiceErrorCode && <code className="invoice-detail-error-code">Hata kodu: {selectedItem.invoiceErrorCode}</code>}{isInvoiceFailureStatus(selectedItem.invoiceStatus) && <small className="invoice-detail-error-guidance">{invoiceFailureGuidance(selectedItem.invoiceErrorCode)}</small>}</div><div><small>Toplam</small><strong>{selectedItem.amount.toLocaleString('tr-TR', { style: 'currency', currency: selectedItem.currency })}</strong></div><div><small>Kargo / takip</small><strong>{selectedItem.cargoProviderName ?? '—'}<br />{selectedItem.cargoTrackingNumber ?? 'Takip no yok'}</strong></div></section>
        <section className="invoice-detail-section invoice-summary-section"><div className="invoice-detail-section-heading"><div><h3>Fatura bilgileri</h3><p className="invoice-detail-muted">Belgenin hazır olup olmadığını ve Trendyol aktarımını buradan takip edebilirsiniz.</p></div></div><div className="invoice-detail-summary-facts"><div><small>Fatura numarası</small><strong>{selectedItem.invoiceNumber ?? 'Henüz atanmadı'}</strong></div><div><small>Platform aktarımı</small><Badge value={selectedItem.invoiceDeliveryStatus ?? 'NOT_SENT'} tone={invoiceDeliveryTone(selectedItem.invoiceDeliveryStatus)} label={invoiceDeliveryLabel(selectedItem.invoiceDeliveryStatus)} />{selectedItem.invoiceDeliveryReference && <small>Referans: {selectedItem.invoiceDeliveryReference}</small>}</div></div>{selectedItem.invoiceId && selectedItem.invoiceDocumentAvailable ? <a className="invoice-document-link" href={`/api/v1/invoices/${selectedItem.invoiceId}/documents/latest/content`} target="_blank" rel="noreferrer">Fatura dosyasını aç <UiIcon name="externalLink" /></a> : <p className="invoice-detail-muted">Fatura belgesi henüz hazır değil. Hazır olduğunda dosya bağlantısı burada görünecek.</p>}</section>
        <section className="invoice-detail-section"><div className="invoice-detail-section-heading"><h3>Ürünler</h3><span>{selectedItem.lines?.length ?? selectedItem.productCount} kalem</span></div><div className="invoice-detail-lines">{(selectedItem.lines ?? []).map((line, index) => <article className="invoice-detail-line" key={`${line.sku}-${index}`}><span className="invoice-detail-line-media">{line.imageUrl ? <img src={line.imageUrl} alt="" /> : <UiIcon name="image" />}</span><div><strong>{line.description}</strong><small>SKU: {line.sku || '—'} · Barkod: {line.barcode || '—'}</small><small>{line.quantity} adet · Birim {line.unitPrice.toLocaleString('tr-TR', { style: 'currency', currency: selectedItem.currency })} · KDV %{line.vatRate}</small></div></article>)}{!selectedItem.lines?.length && <p className="invoice-detail-muted">Ürün satırı detayına ulaşılamadı; sipariş kaydı korunuyor.</p>}</div></section>
        <section className="invoice-detail-addresses"><article><h3>Teslimat adresi</h3>{addressLines(selectedItem.shipmentAddressJson).map(line => <span key={line}>{line}</span>)}{!addressLines(selectedItem.shipmentAddressJson).length && <span className="invoice-detail-muted">Adres bilgisi yok</span>}</article><article><h3>Fatura adresi</h3>{addressLines(selectedItem.invoiceAddressJson).map(line => <span key={line}>{line}</span>)}{!addressLines(selectedItem.invoiceAddressJson).length && <span className="invoice-detail-muted">Adres bilgisi yok</span>}</article></section>
      </div><footer className="invoice-detail-footer"><button type="button" className="secondary" onClick={() => setSelectedItem(null)}>Kapat</button>{selectedItem.invoiceId && <Link className="button-link" to={`/invoices/${selectedItem.invoiceId}`}>Fatura kaydını aç</Link>}</footer>
    </aside></div>}
    {!provider?.hasCredential && <div className="unknown invoice-provider-notice"><strong>E-Faturam provider hazır değil</strong><p>Fatura kes butonu için aktif bağlantı ve şifreli credential gerekir.</p><Link className="button-link" to="/integrations">Bağlantıyı yönet</Link></div>}
  </section>
}


export function InvoiceDetailPage() {
  const { id = '' } = useParams(); const [searchParams] = useSearchParams(); const client = useQueryClient(); const [notice, setNoticeState] = useState(''); const [noticeKind, setNoticeKind] = useState<InvoiceNoticeKind>('info'); const [password, setPassword] = useState(''); const [confirmed, setConfirmed] = useState(false); const [uploadFile, setUploadFile] = useState<File | null>(null)
  function setNotice(value: string, kind: InvoiceNoticeKind = 'info') { setNoticeState(value); setNoticeKind(kind); if (value) appendNotification(value, kind) }
  useEffect(() => {
    if (!notice) return
    const timeout = window.setTimeout(() => setNoticeState(''), noticeKind === 'info' ? 7000 : 5500)
    return () => window.clearTimeout(timeout)
  }, [notice, noticeKind])
  const query = useQuery({ queryKey: ['invoice', id], queryFn: () => hubApi<InvoiceDetail>(`/invoices/${id}`) })
  const operation = useMutation({
    mutationFn: async ({ action, invoice }: { action: string; invoice: InvoiceDetail }) => {
      if (action === 'VALIDATE') return hubApi<InvoiceDetail>(`/invoices/${id}/validate`, { method: 'POST', headers: { 'If-Match': `"v${invoice.version}"` } })
      if (action === 'RECONCILE') return hubApi(`/invoices/${id}/reconcile-jobs`, { method: 'POST', headers: { 'Idempotency-Key': idempotency() } })
      const endpoint = action === 'SUBMIT' ? 'submit-jobs' : action === 'STAGE_CAPABILITY_PROBE' ? 'stage-capability-probe-jobs' : action === 'DELIVER' ? 'marketplace-delivery-jobs' : 'cancellation-jobs'
      return hubApi(`/invoices/${id}/${endpoint}`, { method: 'POST', headers: { 'Idempotency-Key': idempotency(), ...(action !== 'DELIVER' ? { 'If-Match': `"v${invoice.version}"` } : {}) }, ...(action === 'STAGE_CAPABILITY_PROBE' ? {} : { body: JSON.stringify({ password, confirmed }) }) })
    },
    onSuccess: (_value, variables) => { setNotice(variables.action === 'VALIDATE' ? 'Yerel doğrulama tamamlandı.' : 'İş güvenli kuyruğa alındı.', 'success'); setPassword(''); setConfirmed(false); void client.invalidateQueries({ queryKey: ['invoice', id] }) },
    onError: error => setNotice(error instanceof Error ? error.message : 'İşlem başarısız.', 'error')
  })
  const upload = useMutation({
    mutationFn: (file: File) => { const form = new FormData(); form.append('file', file); return hubApi<{ duplicate: boolean }>(`/invoices/${id}/documents/manual`, { method: 'POST', headers: { 'Idempotency-Key': `invoice-document:${id}:${file.name}:${file.size}:${file.lastModified}` }, body: form }) },
    onSuccess: async result => { setNotice(result.duplicate ? 'Bu fatura belgesi zaten güvenli arşivde bulunuyor.' : 'Fatura belgesi güvenli özel arşive yüklendi. Belge henüz Trendyol’a veya E‑Faturam’a iletilmedi.', 'success'); setUploadFile(null); await client.invalidateQueries({ queryKey: ['invoice', id] }) },
    onError: error => setNotice(error instanceof Error ? error.message : 'Fatura belgesi yüklenemedi.', 'error')
  })
  if (query.isLoading) return <section className="content"><Busy /></section>; if (query.isError || !query.data) return <section className="content"><ErrorBox error={query.error} /></section>; const invoice = query.data
  const protectedActions = invoice.requiresSensitiveConfirmation ? invoice.allowedActions.filter(action => action !== 'VALIDATE' && action !== 'RECONCILE') : []
  return <section className="content f3"><Link className="back" to="/invoices"><UiIcon name="arrowLeft" /> Faturalar</Link><div className="page-heading"><div><p className="eyebrow">Fatura detayı</p><h1>{invoice.orderNumber}</h1><p className="lede">{invoice.invoiceNumber ?? 'Henüz numara atanmadı'} · {statusLabel(invoice.invoiceType)}</p></div><Badge value={invoice.status} /></div>
    {notice && <div role={noticeKind === 'error' ? 'alert' : 'status'} className={`notice invoice-provider-toast ${noticeKind}`}>{notice}</div>}
    <div className="grid"><article><small>Ödenecek</small><strong>{invoice.payableTotal.toLocaleString('tr-TR', { style: 'currency', currency: invoice.currency })}</strong><p>Vergi: {invoice.taxTotal.toLocaleString('tr-TR')}</p></article><article><small>ETTN / UUID</small><strong>{invoice.ettnUuid ?? 'Henüz atanmadı'}</strong><p>{invoice.sequencePurpose} · {invoice.issuedAt ? new Date(invoice.issuedAt).toLocaleString('tr-TR') : 'Henüz düzenlenmedi'}</p></article><article><small>Son hata</small><strong>{invoice.lastErrorCode ?? 'Yok'}</strong><p>Bilinmeyen sonuç otomatik başarı sayılmaz.</p></article></div>
     <div className="panel"><h2>Satırlar</h2><div className="data-table compact" role="table">{invoice.lines.map(line => <div role="row" key={line.id}><span><strong>{line.description}</strong><small><code className="technical-text sku-value">{line.sku ?? 'SKU yok'}</code> · indirim {line.discountAmount.toLocaleString('tr-TR')}</small></span><span>{line.quantity} {line.unit}</span><span>%{line.vatRate}</span><span>{line.lineTotal.toLocaleString('tr-TR', { style: 'currency', currency: invoice.currency })}</span></div>)}</div></div>
    <div className="split"><div className="panel"><h2>Provider denemeleri</h2>{invoice.attempts.length ? <div className="card-list">{invoice.attempts.map(item => <div className="record-card" key={item.attemptNumber}><span><strong>Deneme #{item.attemptNumber}</strong><small>{item.errorCode ?? 'Hata yok'}</small></span><Badge value={item.outcome} /></div>)}</div> : <p>Henüz dış gönderim denemesi yok.</p>}</div><div className="panel"><h2>Trendyol teslimleri</h2><p className="invoice-detail-muted">Bu alan, faturanın Trendyol paketine gönderilip gönderilmediğini gösterir.</p>{invoice.deliveries.length ? <div className="card-list">{invoice.deliveries.map(item => <div className="record-card" key={item.id}><span><strong>{statusLabel(item.deliveryType)}</strong><small>{item.externalReference ?? item.errorCode ?? 'Referans bekleniyor'}</small></span><Badge value={item.status} /></div>)}</div> : <p>Platforma aktarım henüz başlatılmadı. Belge hazır olduğunda aşağıdaki işlemlerden fatura linkini iletebilirsiniz.</p>}</div></div>
    <div className={`panel invoice-documents-panel ${searchParams.get('upload') === '1' ? 'invoice-upload-highlight' : ''}`}><div className="invoice-panel-heading"><div><h2>Belgeler</h2><p className="invoice-detail-muted">Provider’dan gelen fatura belgesi ve platforma iletilecek güvenli bağlantı.</p></div>{invoice.documents.length > 0 && <a className="invoice-document-link" href={`/api/v1/invoices/${invoice.id}/documents/latest/content`} target="_blank" rel="noreferrer">Fatura linkini aç <UiIcon name="externalLink" /></a>}</div>{invoice.documents.length ? <div className="card-list">{invoice.documents.map(document => <a className="record-card" key={document.id} href={`/api/v1/invoices/${invoice.id}/documents/${document.id}/content`} target="_blank" rel="noreferrer"><span><strong>{statusLabel(document.documentType)}</strong><small>SHA-256 {document.sha256.slice(0, 16)}…</small></span><span>Güvenli aç <UiIcon name="externalLink" /></span></a>)}</div> : <p>Henüz belge eklenmedi. Provider belgesi alındığında ayrıca güvenli biçimde saklanır.</p>}<form className="invoice-document-upload" onSubmit={event => { event.preventDefault(); if (uploadFile) upload.mutate(uploadFile) }}><div><strong>Fatura belgesi yükle</strong><small>PDF, JPEG veya PNG · en fazla 10 MiB · sadece özel arşive kaydedilir</small></div><input type="file" accept="application/pdf,image/jpeg,image/png" onChange={event => setUploadFile(event.target.files?.[0] ?? null)} aria-label="Fatura belgesi seç" /><button type="submit" disabled={!uploadFile || upload.isPending}>{upload.isPending ? 'Yükleniyor…' : 'Belgeyi yükle'}</button></form></div>
    {protectedActions.length > 0 && <form className="panel form-panel" onSubmit={event => event.preventDefault()}><h2>Dış mali işlem onayı</h2><p>Parola yalnız production mali işlemi için yeniden doğrulanır. Sunucu capability, bağlantı ve yazma kapılarını ayrıca denetler.</p><label>Hesap parolası<input type="password" autoComplete="current-password" value={password} onChange={event => setPassword(event.target.value)} /></label><label className="check"><input type="checkbox" checked={confirmed} onChange={event => setConfirmed(event.target.checked)} /> Bu dış mali işlemi açıkça onaylıyorum.</label></form>}
    <div className="panel"><h2>Kullanılabilir işlemler</h2>{invoice.allowedActions.length ? <><p className="invoice-detail-muted">Fatura belgesi hazırsa “Trendyol’a fatura linkini ilet” işlemi, güvenli bağlantıyı ilgili pakete gönderir.</p><div className="button-row">{invoice.allowedActions.map(action => <button key={action} type="button" onClick={() => operation.mutate({ action, invoice })} disabled={operation.isPending || (protectedActions.includes(action) && (!password || !confirmed))}>{actionLabel(action)}</button>)}</div></> : <div className="unknown"><strong>Dış işlemler kapalı</strong><p>Bağlantı, teknik doğrulama ve güvenli yazma koşulları işlem öncesi denetlenir.</p></div>}</div>
  </section>
}
