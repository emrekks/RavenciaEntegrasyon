import { useEffect, useState, type FormEvent } from 'react'
import { Link, useParams, useSearchParams } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi, loadAllPages } from './api'
import './styles/invoices.css'

type Invoice = { id: string; orderNumber: string; invoiceType: string; status: string; currency: string; payableTotal: number; invoiceNumber: string | null; dueAt: string | null; createdAt: string; version: number }
type InvoiceWorkspaceLine = { sku: string; barcode: string | null; description: string; quantity: number; unitPrice: number; vatRate: number; imageUrl: string | null }
type InvoiceWorkspace = { orderId: string; packageId: string; orderNumber: string; customerName: string; orderedAt: string; shipmentStatus: string; deliveredAt: string | null; invoiceDueAt: string | null; isDueSoon: boolean; currency: string; amount: number; productCount: number; primaryImageUrl: string | null; cargoProviderName: string | null; cargoTrackingNumber: string | null; invoiceId: string | null; invoiceStatus: string; invoiceNumber: string | null; canCreateInvoice: boolean; shipmentAddressJson: string | null; invoiceAddressJson: string | null; lines: InvoiceWorkspaceLine[] | null }
type InvoiceDetail = Invoice & { orderId: string; packageId: string | null; providerConnectionId: string; sequencePurpose: string; ettnUuid: string | null; taxExclusiveTotal: number; discountTotal: number; taxTotal: number; note: string; issuedAt: string | null; lastErrorCode: string | null; lines: Array<{ id: string; lineSequence: number; description: string; sku: string | null; unit: string; quantity: number; unitPrice: number; discountAmount: number; vatRate: number; vatAmount: number; lineTotal: number }>; documents: Array<{ id: string; documentType: string; sha256: string; createdAt: string }>; attempts: Array<{ attemptNumber: number; outcome: string; errorCode: string | null; startedAt: string; completedAt: string | null }>; deliveries: Array<{ id: string; deliveryType: string; status: string; externalReference: string | null; errorCode: string | null; createdAt: string }>; allowedActions: string[]; requiresSensitiveConfirmation: boolean }
type Connection = { id: string; platformCode: string; displayName: string; status: string; hasCredential: boolean }
type Policy = { id: string; providerConnectionId: string; triggerState: string; packageScope: string; dueRule: string; roundingRule: string; adjustmentRule: string; autoSubmit: boolean; version: number }

function idempotency() { return crypto.randomUUID() }
function ErrorBox({ error }: { error: unknown }) { return <div role="alert" className="error">{error instanceof Error ? error.message : 'İşlem tamamlanamadı.'}</div> }
function Busy() { return <div className="status inline" role="status"><div className="spinner" /><strong>Veriler yükleniyor…</strong></div> }
function statusLabel(value: string) {
  const normalized = value.trim().toUpperCase()
  return ({
    READY: 'Hazır', ACCEPTED: 'Kabul edildi', COMPLETED: 'Tamamlandı', ACTIVE: 'Aktif', SUPPORTED: 'Destekleniyor', CANCELLED: 'İptal edildi',
    UNKNOWN_RESULT: 'Bilinmeyen sonuç', VALIDATION_FAILED: 'Doğrulama başarısız', MANUAL_REVIEW: 'Manuel inceleme', UNAPPROVED: 'Onaylanmadı', UNKNOWN: 'Bilinmiyor', CANCELLATION_PENDING: 'İptal bekliyor',
    NEW: 'Yeni', PROCESSING: 'İşleme alındı', READY_TO_SHIP: 'Kargoya hazır', SHIPPED: 'Kargoda', UNDELIVERED: 'Teslim edilemedi', DELIVERED: 'Teslim edildi', RETURNED: 'İade edildi', RETURN_IN_TRANSIT: 'İade kargoda',
    SUCCESS: 'Başarılı', FAILED: 'Başarısız', IN_PROGRESS: 'Devam ediyor', RUNNING: 'Çalışıyor',
    'DUPLICATE SAFE': 'Çoklu işleme güvenli', DUPLICATE_SAFE: 'Çoklu işleme güvenli', AUTO: 'Otomatik', TEMELFATURA: 'Temel fatura', EARSIVFATURA: 'E-Arşiv fatura', MANUAL_UPLOAD: 'Elle yüklenen fatura belgesi', MARKETPLACE_DELIVERY: 'Pazaryeri teslimi'
  } as Record<string, string>)[normalized] ?? value
}
function Badge({ value }: { value: string }) { const normalized = value.trim().toUpperCase(); const tone = ['READY', 'ACCEPTED', 'COMPLETED', 'ACTIVE', 'SUPPORTED', 'CANCELLED', 'DELIVERED', 'SUCCESS'].includes(normalized) ? 'good' : ['UNKNOWN_RESULT', 'VALIDATION_FAILED', 'MANUAL_REVIEW', 'UNAPPROVED', 'UNKNOWN', 'CANCELLATION_PENDING', 'FAILED'].includes(normalized) ? 'warn' : 'neutral'; return <span className={`badge ${tone}`}>{statusLabel(value)}</span> }
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
  const client = useQueryClient(); const [search, setSearch] = useState(''); const [tab, setTab] = useState('UNINVOICED'); const [status, setStatus] = useState('ALL'); const [message, setMessage] = useState(''); const [pageSize, setPageSize] = useState(20); const [pageNumber, setPageNumber] = useState(1); const [selectedItem, setSelectedItem] = useState<InvoiceWorkspace | null>(null)
  const query = useQuery({ queryKey: ['invoice-workspace'], queryFn: () => hubApi<InvoiceWorkspace[]>('/invoice-workspace') })
  const connections = useQuery({ queryKey: ['connections', 'billing-workspace'], queryFn: () => loadAllPages<Connection>('/connections') })
  const provider = connections.data?.items.find(x => x.platformCode === 'TRENDYOL_EFATURAM' && (x.status === 'ACTIVE' || x.status === 'VERIFIED'))
  const create = useMutation({ mutationFn: async (item: InvoiceWorkspace) => { if (!provider) throw new Error('Aktif Trendyol E-Faturam bağlantısı gereklidir.'); const invoice = await hubApi<InvoiceDetail>('/invoices', { method: 'POST', headers: { 'Idempotency-Key': `invoice:${item.orderId}:${item.packageId}` }, body: JSON.stringify({ orderId: item.orderId, packageId: item.packageId, providerConnectionId: provider.id, originalInvoiceId: null }) }); const ready = await hubApi<InvoiceDetail>(`/invoices/${invoice.id}/validate`, { method: 'POST', headers: { 'If-Match': `"v${invoice.version}"` } }); await hubApi(`/invoices/${invoice.id}/submit-jobs`, { method: 'POST', headers: { 'Idempotency-Key': `invoice-submit:${invoice.id}`, 'If-Match': `"v${ready.version}"` }, body: JSON.stringify({ password: '', confirmed: false }) }); return invoice }, onSuccess: async () => { setMessage('Fatura E-Faturam’a gönderildi. Sağlayıcı yanıtı işleniyor.'); await client.invalidateQueries({ queryKey: ['invoice-workspace'] }) }, onError: error => setMessage(error instanceof Error ? error.message : 'Fatura oluşturulamadı.') })
  const items = query.data ?? []; const normalized = search.trim().toLocaleLowerCase('tr-TR')
  const visible = items.filter(item => {
    const tabMatch = tab === 'UNINVOICED' ? item.canCreateInvoice : tab === 'INVOICED' ? !item.canCreateInvoice : item.isDueSoon
    const statusMatch = status === 'ALL' || item.shipmentStatus === status
    return tabMatch && statusMatch && (!normalized || [item.orderNumber, item.customerName, item.invoiceNumber ?? '', item.cargoTrackingNumber ?? ''].some(value => value.toLocaleLowerCase('tr-TR').includes(normalized)))
  })
  const totalPages = Math.max(1, Math.ceil(visible.length / pageSize)); const currentPage = Math.min(pageNumber, totalPages); const pageItems = visible.slice((currentPage - 1) * pageSize, currentPage * pageSize)
  useEffect(() => { setPageNumber(1) }, [search, tab, status, pageSize])
  const tabs = [['UNINVOICED', 'Faturalandırılmamışlar'], ['INVOICED', 'Faturalandırılmışlar'], ['DUE_SOON', 'Süresi Yaklaşanlar']] as const
  const counts = { unInvoiced: items.filter(x => x.canCreateInvoice).length, invoiced: items.filter(x => !x.canCreateInvoice).length, dueSoon: items.filter(x => x.isDueSoon).length }
  const activeTabLabel = tabs.find(([value]) => value === tab)?.[1] ?? 'Faturalar'
  const clearFilters = () => { setSearch(''); setStatus('ALL'); setPageNumber(1) }
  return <section className="content f3 invoices-page reference-invoices-page">
    <div className="page-heading invoices-reference-heading">
      <div><p className="eyebrow">Mali belgeler</p><h1>Faturalar</h1><p className="lede">Faturaları paket, teslimat ve ödeme bilgileriyle tek çalışma alanında takip edin.</p></div>
      <div className="invoices-reference-heading-actions"><span className="invoice-safety-status"><i aria-hidden="true" /> Manuel işlem güvenli</span><Badge value="DUPLICATE SAFE" /></div>
    </div>
    {message && <div role="status" className="notice">{message}</div>}
    <div className="invoice-reference-metrics">
      <article className="invoice-metric-pending"><small>Fatura bekleyen</small><strong>{counts.unInvoiced}</strong><span>paket bazlı işlem</span></article>
      <article className="invoice-metric-due"><small>Süresi yaklaşan</small><strong>{counts.dueSoon}</strong><span>teslimden 5 gün geçen</span></article>
      <article className="invoice-metric-complete"><small>Faturalandırılan</small><strong>{counts.invoiced}</strong><span>ikinci fatura kapalı</span></article>
      <article className="invoice-metric-total"><small>Toplam paket</small><strong>{items.length}</strong><span>fatura çalışma alanı</span></article>
    </div>
    <div className="invoice-reference-filter-shell">
      <div className="invoice-reference-tabs" role="tablist" aria-label="Fatura görünümleri">{tabs.map(([value, label]) => <button key={value} type="button" role="tab" aria-selected={tab === value} className={tab === value ? 'active' : ''} onClick={() => setTab(value)}><span>{label}</span><b>{value === 'UNINVOICED' ? counts.unInvoiced : value === 'INVOICED' ? counts.invoiced : counts.dueSoon}</b></button>)}</div>
      <section className="invoice-reference-filters" aria-label="Fatura filtreleri">
        <label className="invoice-reference-search"><span aria-hidden="true">⌕</span><input aria-label="Fatura ara" placeholder="Sipariş, müşteri, fatura veya takip no ara…" value={search} onChange={event => setSearch(event.target.value)} /></label>
        <label>Sipariş durumu<select value={status} onChange={event => setStatus(event.target.value)}><option value="ALL">Tümü</option><option value="NEW">Yeni</option><option value="PROCESSING">İşleme alınmış</option><option value="SHIPPED">Kargoya verilmiş</option><option value="DELIVERED">Teslim edilmiş</option><option value="CANCELLED">İptal edilmiş</option></select></label>
        <div className="invoice-reference-filter-actions"><button type="button" className="secondary" onClick={clearFilters}>Temizle</button></div>
      </section>
    </div>
    <section className="invoice-reference-workspace">
      <header><div><h2>{activeTabLabel}</h2><p>Filtreleme sonuçları: {visible.length} paket · Fatura işlemleri kayıt bazında yürütülür.</p></div><label className="invoice-reference-page-size">Sayfa başına<select aria-label="Sayfa başına fatura" value={pageSize} onChange={event => setPageSize(Number(event.target.value))}>{[20, 50, 100, 200].map(value => <option key={value} value={value}>{value}</option>)}</select></label></header>
      {query.isLoading ? <div className="invoice-reference-state"><Busy /></div> : query.isError ? <div className="invoice-reference-state"><ErrorBox error={query.error} /></div> : !visible.length ? <div className="invoice-reference-state"><div className="empty"><strong>Kayıt yok</strong><p>Seçili fatura sekmesi ve filtrelerle eşleşen paket bulunamadı.</p></div></div> : <><div className="invoice-reference-table" role="table">
        <div className="invoice-reference-head" role="row"><strong>Sipariş bilgileri</strong><strong>Alıcı</strong><strong>Ürün &amp; kargo</strong><strong>Sipariş durumu</strong><strong>Fatura durumu</strong><strong>Tutar</strong><strong>İşlemler</strong></div>
        {pageItems.map(item => <article className={`invoice-reference-row ${item.isDueSoon ? 'due-soon' : ''}`} key={item.packageId} role="row">
          <div className="invoice-reference-order"><span className="invoice-reference-media">{item.primaryImageUrl ? <img src={item.primaryImageUrl} alt="" /> : <span>▧</span>}</span><div><strong>#{item.orderNumber}</strong><small>{new Date(item.orderedAt).toLocaleString('tr-TR')}</small><small>{item.invoiceNumber ?? 'Fatura numarası bekleniyor'}</small></div></div>
          <div className="invoice-reference-buyer"><strong>{item.customerName}</strong><small>{item.productCount} ürünlük sipariş</small></div>
          <div className="invoice-reference-products"><strong>{item.cargoProviderName ?? 'Kargo bilgisi yok'}</strong><small>{item.cargoTrackingNumber ?? 'Takip numarası yok'}</small><small>Paket: {item.packageId}</small></div>
          <div className="invoice-reference-shipment"><Badge value={item.shipmentStatus} /><small>{item.deliveredAt ? `Teslim: ${new Date(item.deliveredAt).toLocaleDateString('tr-TR')}` : 'Henüz teslim edilmedi'}</small></div>
          <div className="invoice-reference-status"><span className={item.canCreateInvoice ? 'invoice-status pending' : 'invoice-status complete'}>{item.canCreateInvoice ? 'Fatura bekliyor' : 'Fatura oluşturuldu'}</span><small>{statusLabel(item.invoiceStatus)}</small><small className={item.isDueSoon ? 'deadline critical' : ''}>{item.invoiceDueAt ? `Son tarih: ${new Date(item.invoiceDueAt).toLocaleDateString('tr-TR')}` : 'Son tarih teslimata göre hesaplanır'}</small></div>
          <div className="invoice-reference-amount"><strong>{item.amount.toLocaleString('tr-TR', { style: 'currency', currency: item.currency })}</strong><small>{item.isDueSoon ? 'Öncelikli takip' : 'Sipariş toplamı'}</small></div>
          <div className="invoice-reference-actions">{item.invoiceId ? <span className="badge neutral">Fatura oluşturuldu</span> : <button disabled={!provider?.hasCredential || create.isPending || !item.canCreateInvoice} onClick={() => create.mutate(item)}>{create.isPending ? 'Oluşturuluyor…' : 'Fatura oluştur'}</button>}<button type="button" className="invoice-reference-details-trigger" onClick={() => setSelectedItem(item)}>Detayları aç <span aria-hidden="true">↗</span></button></div>
        </article>)}
      </div><div className="order-pagination"><span>{(currentPage - 1) * pageSize + 1}–{Math.min(currentPage * pageSize, visible.length)} / {visible.length} paket</span><div><button type="button" disabled={currentPage <= 1} onClick={() => setPageNumber(value => Math.max(1, value - 1))}>Önceki</button><b>Sayfa {currentPage} / {totalPages}</b><button type="button" disabled={currentPage >= totalPages} onClick={() => setPageNumber(value => Math.min(totalPages, value + 1))}>Sonraki</button></div></div></>}
    </section>
    {selectedItem && <div className="invoice-detail-backdrop" role="presentation" onMouseDown={() => setSelectedItem(null)}><aside className="invoice-detail-drawer" role="dialog" aria-modal="true" aria-labelledby="invoice-detail-title" onMouseDown={event => event.stopPropagation()}>
      <header className="invoice-detail-header"><div><p className="eyebrow">Sipariş ve fatura özeti</p><h2 id="invoice-detail-title">#{selectedItem.orderNumber}</h2><p>{selectedItem.customerName} · {new Date(selectedItem.orderedAt).toLocaleString('tr-TR')}</p></div><button type="button" className="modal-close" onClick={() => setSelectedItem(null)} aria-label="Detay panelini kapat">×</button></header>
      <div className="invoice-detail-body"><section className="invoice-detail-summary"><div><small>Sipariş durumu</small><Badge value={selectedItem.shipmentStatus} /></div><div><small>Fatura durumu</small><strong>{statusLabel(selectedItem.invoiceStatus)}</strong></div><div><small>Toplam</small><strong>{selectedItem.amount.toLocaleString('tr-TR', { style: 'currency', currency: selectedItem.currency })}</strong></div><div><small>Kargo / takip</small><strong>{selectedItem.cargoProviderName ?? '—'}<br />{selectedItem.cargoTrackingNumber ?? 'Takip no yok'}</strong></div></section>
        <section className="invoice-detail-section"><div className="invoice-detail-section-heading"><h3>Ürünler</h3><span>{selectedItem.lines?.length ?? selectedItem.productCount} kalem</span></div><div className="invoice-detail-lines">{(selectedItem.lines ?? []).map((line, index) => <article className="invoice-detail-line" key={`${line.sku}-${index}`}><span className="invoice-detail-line-media">{line.imageUrl ? <img src={line.imageUrl} alt="" /> : <span aria-hidden="true">▧</span>}</span><div><strong>{line.description}</strong><small>SKU: {line.sku || '—'} · Barkod: {line.barcode || '—'}</small><small>{line.quantity} adet · Birim {line.unitPrice.toLocaleString('tr-TR', { style: 'currency', currency: selectedItem.currency })} · KDV %{line.vatRate}</small></div></article>)}{!selectedItem.lines?.length && <p className="invoice-detail-muted">Ürün satırı detayına ulaşılamadı; sipariş kaydı korunuyor.</p>}</div></section>
        <section className="invoice-detail-addresses"><article><h3>Teslimat adresi</h3>{addressLines(selectedItem.shipmentAddressJson).map(line => <span key={line}>{line}</span>)}{!addressLines(selectedItem.shipmentAddressJson).length && <span className="invoice-detail-muted">Adres bilgisi yok</span>}</article><article><h3>Fatura adresi</h3>{addressLines(selectedItem.invoiceAddressJson).map(line => <span key={line}>{line}</span>)}{!addressLines(selectedItem.invoiceAddressJson).length && <span className="invoice-detail-muted">Adres bilgisi yok</span>}</article></section>
      </div><footer className="invoice-detail-footer"><button type="button" className="secondary" onClick={() => setSelectedItem(null)}>Kapat</button>{selectedItem.invoiceId && <Link className="button-link" to={`/invoices/${selectedItem.invoiceId}`}>Fatura kaydını aç</Link>}</footer>
    </aside></div>}
    {!provider?.hasCredential && <div className="unknown invoice-provider-notice"><strong>E-Faturam provider hazır değil</strong><p>Fatura kes butonu için aktif bağlantı ve şifreli credential gerekir.</p><Link className="button-link" to="/integrations">Bağlantıyı yönet</Link></div>}
  </section>
}


export function InvoiceDetailPage() {
  const { id = '' } = useParams(); const [searchParams] = useSearchParams(); const client = useQueryClient(); const [notice, setNotice] = useState(''); const [password, setPassword] = useState(''); const [confirmed, setConfirmed] = useState(false); const [uploadFile, setUploadFile] = useState<File | null>(null)
  const query = useQuery({ queryKey: ['invoice', id], queryFn: () => hubApi<InvoiceDetail>(`/invoices/${id}`) })
  const operation = useMutation({
    mutationFn: async ({ action, invoice }: { action: string; invoice: InvoiceDetail }) => {
      if (action === 'VALIDATE') return hubApi<InvoiceDetail>(`/invoices/${id}/validate`, { method: 'POST', headers: { 'If-Match': `"v${invoice.version}"` } })
      if (action === 'RECONCILE') return hubApi(`/invoices/${id}/reconcile-jobs`, { method: 'POST', headers: { 'Idempotency-Key': idempotency() } })
      const endpoint = action === 'SUBMIT' ? 'submit-jobs' : action === 'STAGE_CAPABILITY_PROBE' ? 'stage-capability-probe-jobs' : action === 'DELIVER' ? 'marketplace-delivery-jobs' : 'cancellation-jobs'
      return hubApi(`/invoices/${id}/${endpoint}`, { method: 'POST', headers: { 'Idempotency-Key': idempotency(), ...(action !== 'DELIVER' ? { 'If-Match': `"v${invoice.version}"` } : {}) }, ...(action === 'STAGE_CAPABILITY_PROBE' ? {} : { body: JSON.stringify({ password, confirmed }) }) })
    },
    onSuccess: (_value, variables) => { setNotice(variables.action === 'VALIDATE' ? 'Yerel doğrulama tamamlandı.' : 'İş güvenli kuyruğa alındı.'); setPassword(''); setConfirmed(false); void client.invalidateQueries({ queryKey: ['invoice', id] }) },
    onError: error => setNotice(error instanceof Error ? error.message : 'İşlem başarısız.')
  })
  const upload = useMutation({
    mutationFn: (file: File) => { const form = new FormData(); form.append('file', file); return hubApi<{ duplicate: boolean }>(`/invoices/${id}/documents/manual`, { method: 'POST', headers: { 'Idempotency-Key': `invoice-document:${id}:${file.name}:${file.size}:${file.lastModified}` }, body: form }) },
    onSuccess: async result => { setNotice(result.duplicate ? 'Bu fatura belgesi zaten güvenli arşivde bulunuyor.' : 'Fatura belgesi güvenli özel arşive yüklendi. Belge henüz Trendyol’a veya E‑Faturam’a iletilmedi.'); setUploadFile(null); await client.invalidateQueries({ queryKey: ['invoice', id] }) },
    onError: error => setNotice(error instanceof Error ? error.message : 'Fatura belgesi yüklenemedi.')
  })
  if (query.isLoading) return <section className="content"><Busy /></section>; if (query.isError || !query.data) return <section className="content"><ErrorBox error={query.error} /></section>; const invoice = query.data
  const protectedActions = invoice.requiresSensitiveConfirmation ? invoice.allowedActions.filter(action => action !== 'VALIDATE' && action !== 'RECONCILE') : []
  return <section className="content f3"><Link className="back" to="/invoices">← Faturalar</Link><div className="page-heading"><div><p className="eyebrow">Fatura detayı</p><h1>{invoice.orderNumber}</h1><p className="lede">{invoice.invoiceNumber ?? 'Henüz numara atanmadı'} · {statusLabel(invoice.invoiceType)}</p></div><Badge value={invoice.status} /></div>
    {notice && <div role="status" className="notice">{notice}</div>}
    <div className="grid"><article><small>Ödenecek</small><strong>{invoice.payableTotal.toLocaleString('tr-TR', { style: 'currency', currency: invoice.currency })}</strong><p>Vergi: {invoice.taxTotal.toLocaleString('tr-TR')}</p></article><article><small>ETTN / UUID</small><strong>{invoice.ettnUuid ?? 'Henüz atanmadı'}</strong><p>{invoice.sequencePurpose} · {invoice.issuedAt ? new Date(invoice.issuedAt).toLocaleString('tr-TR') : 'Henüz düzenlenmedi'}</p></article><article><small>Son hata</small><strong>{invoice.lastErrorCode ?? 'Yok'}</strong><p>Bilinmeyen sonuç otomatik başarı sayılmaz.</p></article></div>
     <div className="panel"><h2>Satırlar</h2><div className="data-table compact" role="table">{invoice.lines.map(line => <div role="row" key={line.id}><span><strong>{line.description}</strong><small><code className="technical-text sku-value">{line.sku ?? 'SKU yok'}</code> · indirim {line.discountAmount.toLocaleString('tr-TR')}</small></span><span>{line.quantity} {line.unit}</span><span>%{line.vatRate}</span><span>{line.lineTotal.toLocaleString('tr-TR', { style: 'currency', currency: invoice.currency })}</span></div>)}</div></div>
    <div className="split"><div className="panel"><h2>Provider denemeleri</h2>{invoice.attempts.length ? <div className="card-list">{invoice.attempts.map(item => <div className="record-card" key={item.attemptNumber}><span><strong>Deneme #{item.attemptNumber}</strong><small>{item.errorCode ?? 'Hata yok'}</small></span><Badge value={item.outcome} /></div>)}</div> : <p>Henüz dış gönderim denemesi yok.</p>}</div><div className="panel"><h2>Trendyol teslimleri</h2>{invoice.deliveries.length ? <div className="card-list">{invoice.deliveries.map(item => <div className="record-card" key={item.id}><span><strong>{statusLabel(item.deliveryType)}</strong><small>{item.externalReference ?? item.errorCode ?? 'Referans bekleniyor'}</small></span><Badge value={item.status} /></div>)}</div> : <p>Henüz pazaryeri fatura linki teslimi yok.</p>}</div></div>
    <div className={`panel invoice-documents-panel ${searchParams.get('upload') === '1' ? 'invoice-upload-highlight' : ''}`}><h2>Belgeler</h2>{invoice.documents.length ? <div className="card-list">{invoice.documents.map(document => <a className="record-card" key={document.id} href={`/api/v1/invoices/${invoice.id}/documents/${document.id}/content`} target="_blank" rel="noreferrer"><span><strong>{statusLabel(document.documentType)}</strong><small>SHA-256 {document.sha256.slice(0, 16)}…</small></span><span>Güvenli aç ↗</span></a>)}</div> : <p>Henüz belge eklenmedi. Provider belgesi alındığında ayrıca güvenli biçimde saklanır.</p>}<form className="invoice-document-upload" onSubmit={event => { event.preventDefault(); if (uploadFile) upload.mutate(uploadFile) }}><div><strong>Fatura belgesi yükle</strong><small>PDF, JPEG veya PNG · en fazla 10 MiB · sadece özel arşive kaydedilir</small></div><input type="file" accept="application/pdf,image/jpeg,image/png" onChange={event => setUploadFile(event.target.files?.[0] ?? null)} aria-label="Fatura belgesi seç" /><button type="submit" disabled={!uploadFile || upload.isPending}>{upload.isPending ? 'Yükleniyor…' : 'Belgeyi yükle'}</button></form></div>
    {protectedActions.length > 0 && <form className="panel form-panel" onSubmit={event => event.preventDefault()}><h2>Dış mali işlem onayı</h2><p>Parola yalnız production mali işlemi için yeniden doğrulanır. Sunucu capability, bağlantı ve yazma kapılarını ayrıca denetler.</p><label>Hesap parolası<input type="password" autoComplete="current-password" value={password} onChange={event => setPassword(event.target.value)} /></label><label className="check"><input type="checkbox" checked={confirmed} onChange={event => setConfirmed(event.target.checked)} /> Bu dış mali işlemi açıkça onaylıyorum.</label></form>}
    <div className="panel"><h2>Kullanılabilir işlemler</h2>{invoice.allowedActions.length ? <div className="button-row">{invoice.allowedActions.map(action => <button key={action} type="button" onClick={() => operation.mutate({ action, invoice })} disabled={operation.isPending || (protectedActions.includes(action) && (!password || !confirmed))}>{actionLabel(action)}</button>)}</div> : <div className="unknown"><strong>Dış işlemler kapalı</strong><p>Bağlantı, teknik doğrulama ve güvenli yazma koşulları işlem öncesi denetlenir.</p></div>}</div>
  </section>
}

export function BillingSettingsPage() {
  const client = useQueryClient(); const [connectionId, setConnectionId] = useState(''); const [message, setMessage] = useState('')
  const connections = useQuery({ queryKey: ['connections'], queryFn: () => loadAllPages<Connection>('/connections') })
  const policy = useQuery({ queryKey: ['invoice-policy', connectionId], queryFn: () => hubApi<Policy>(`/billing/invoice-policies/${connectionId}`), enabled: !!connectionId, retry: false })
  async function savePolicy(event: FormEvent<HTMLFormElement>) { event.preventDefault(); if (!connectionId) return; try { await hubApi(`/billing/invoice-policies/${connectionId}`, { method: 'PUT', headers: policy.data ? { 'If-Match': `"v${policy.data.version}"` } : {}, body: JSON.stringify({ triggerState: 'MANUAL_CONFIRMED', packageScope: 'SHIPMENT_PACKAGE', dueRule: 'IMMEDIATE', roundingRule: 'LINE_HALF_AWAY_FROM_ZERO', adjustmentRule: 'REJECT_OVER_ONE_KURUS', autoSubmit: false }) }); setMessage('Manuel paket faturası politikası kaydedildi; otomatik gönderim kapalı kaldı.'); await client.invalidateQueries({ queryKey: ['invoice-policy', connectionId] }) } catch (error) { setMessage(error instanceof Error ? error.message : 'Kayıt başarısız.') } }
  const providers = connections.data?.items.filter(x => x.platformCode === 'TRENDYOL_EFATURAM') ?? []
  return <section className="content f3"><div className="page-heading"><div><p className="eyebrow">Faturalama ayarları</p><h1>Otomatik fatura yönlendirmesi</h1><p className="lede">Gönderen mali bilgiler E-Faturam hesabında yönetilir. Panel yalnız Trendyol siparişindeki müşteri ve fatura adresi snapshotını kullanır.</p></div><Badge value="AUTO SUBMIT OFF" /></div>{message && <div role="status" className="notice">{message}</div>}
    <div className="split"><article className="panel"><h2>Belge türü otomatik seçilir</h2><div className="card-list"><div className="record-card"><span><strong>Kurumsal + E-Fatura uygun</strong><small>Trendyol siparişinde commercial=true ve eInvoiceAvailable=true</small></span><Badge value="TEMELFATURA" /></div><div className="record-card"><span><strong>Bireysel veya E-Fatura uygun değil</strong><small>Müşteri ve adres bilgileriyle internet satışı E-Arşiv</small></span><Badge value="EARSIVFATURA" /></div></div><p>Ödeme ve taşıyıcı alanları ekranda ayar değildir. E-Arşiv gönderiminde Trendyol siparişi ve resmî kargo sağlayıcı kataloğundan otomatik oluşturulur.</p></article>
      <form className="panel form-panel" onSubmit={savePolicy}><h2>Manuel paket faturası politikası</h2><label>Provider<select value={connectionId} onChange={event => setConnectionId(event.target.value)} required><option value="">Seçin</option>{providers.map(item => <option key={item.id} value={item.id}>{item.displayName}</option>)}</select></label><label>Tetikleme durumu<input value="MANUAL_CONFIRMED" readOnly /></label><label>Paket kapsamı<input value="SHIPMENT_PACKAGE" readOnly /></label><label>Yuvarlama kuralı<input value="LINE_HALF_AWAY_FROM_ZERO" readOnly /></label><label>Düzeltme kuralı<input value="REJECT_OVER_ONE_KURUS" readOnly /></label><button disabled={!connectionId || policy.isLoading || policy.isFetching}>Manuel paket politikasını kaydet</button><p>Gerçek gönderim ayrıca parola ve açık onay ister.</p></form>
    </div>
  </section>
}
