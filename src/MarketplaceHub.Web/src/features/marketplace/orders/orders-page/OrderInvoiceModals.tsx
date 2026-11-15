import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../../../shared/api'
import { Busy, ErrorBox, UiIcon, DialogSurface } from '../../../../shared/components'
import { newIdempotencyKey } from '../../../../shared/idempotency'
import { invoiceSendDeniedReason, useCanSendInvoices } from '../../../../shared/hooks/useCanSendInvoices'
import { statusLabel } from '../../../../shared/status-labels'
import { RrBadge, RrError, RrIcon, RrImagePreview, RrLoading, RrModal, titleTr } from '../../../../shared/ui/rr-components'
import { visibleOrderLineVariantOptions } from '../../order-line-variant-options'
import { invoiceProviderForEnvironment } from '../../../invoicing/invoice-creation-availability'
import { InvoiceWorkspacePreviewModal, type InvoicePreviewTarget } from '../../../invoicing/InvoiceWorkspacePreviewModal'
import { fromDateTimeLocal } from '../../../../shared/format-date'
import { formatMoney } from '../../../../shared/format-money'
import { orderDetailQueryKey } from '../order-query-keys'
import { Connection, Order, displayOrderNumber, orderDisplayShipment } from '../../shared'
import { queryKeys } from '../../../../shared/api/query-keys'
import { type ProductImagePreview, type OrderDetail, type IntegrationFeedbackHandler, addressDataText } from './order-page-helpers'

type InvoiceViewer = { id: string; orderNumber: string; invoiceType: string; status: string; currency: string; payableTotal: number; taxTotal: number; invoiceNumber: string | null; lines: Array<{ id: string; description: string; sku: string | null; unit: string; quantity: number; vatRate: number; lineTotal: number }>; documents: Array<{ id: string; documentType: string; sha256: string }> }

export function ProductImagePreviewModal({ preview, onClose }: { preview: ProductImagePreview; onClose: () => void }) {
  const sources = [...new Set([preview.url, ...(preview.fallbackUrls ?? []), preview.fallbackUrl].filter((value): value is string => Boolean(value)))]
  const variant = visibleOrderLineVariantOptions(preview.variant ?? null).map(option => `${option.label}: ${option.value}`).join(' · ')
  return <RrImagePreview title={preview.title} crumb={[preview.modelCode ? `Model Kodu: ${preview.modelCode}` : '', variant].filter(Boolean).join(' · ')} sources={sources} onClose={onClose} />
}

function safeJson(value: string) { try { return JSON.parse(value || '{}') as Record<string, unknown> } catch { return {} } }

function addressText(value: string, platformCode = 'TRENDYOL') { const source = safeJson(value); return addressDataText([source.shipmentAddress, source.invoiceAddress, source.address].find(item => item && typeof item === 'object') as Record<string, unknown> | undefined ?? source, platformCode) }

export function InvoiceInfoModal({ item, onClose }: { item: Order; onClose: () => void }) {
  const detail = useQuery({ queryKey: orderDetailQueryKey(item.id), queryFn: ({ signal }) => hubApi<OrderDetail>(`/orders/${item.id}`, { signal }) })
  const order = detail.data
  const missing = (value: string | null | undefined) => value ? value : 'Bilgi gelmedi'
  return <RrModal crumb={displayOrderNumber(item.orderNumber)} title="Fatura ve Adres Bilgileri" hint="Faturayı başka bir yerde kestiyseniz Fatura İşlemleri › Fatura Yükle ile siparişe ekleyin. PDF, JPEG, JPG veya PNG kabul edilir." size="md" onClose={onClose} foot={<><span className="rr-note" /><button type="button" className="rr-btn" onClick={onClose}>Kapat</button><button type="button" className="rr-btn rr-primary" disabled={!order} onClick={() => window.print()}><RrIcon name="print" />Yazdır</button></>}>
    {detail.isLoading ? <RrLoading text="Fatura ve adres bilgileri yükleniyor…" /> : detail.isError || !order ? <RrError error={detail.error} onRetry={() => void detail.refetch()} /> : <>
      <div className="rr-two-cards"><section className="rr-dsec"><header><div className="rr-h3">Teslimat Adresi</div></header><dl className="rr-kv"><dt>Ad Soyad</dt><dd>{order.customerName}</dd><dt>Adres</dt><dd>{addressText(order.shipmentAddressJson, order.platformCode)}</dd></dl></section><section className="rr-dsec"><header><div className="rr-h3">Fatura Adresi</div></header><dl className="rr-kv"><dt>Ad Soyad</dt><dd>{order.customerName}</dd><dt>Adres</dt><dd>{addressText(order.invoiceAddressJson, order.platformCode)}</dd><dt>E-Fatura Mükellefi</dt><dd>{order.isEInvoiceAvailable === null ? 'Bilgi gelmedi' : order.isEInvoiceAvailable ? 'Evet' : 'Hayır'}</dd></dl></section></div>
      <dl className="rr-kv"><dt>E-posta Adresi</dt><dd className={order.customerEmail ? undefined : 'rr-empty'}>{missing(order.customerEmail)}</dd><dt>Telefon Numarası</dt><dd className={order.customerPhone ? 'rr-num' : 'rr-empty'}>{missing(order.customerPhone)}</dd></dl>
    </>}
  </RrModal>
}
export function InvoiceDraftModal({ item, providers, onClose, onFeedback }: { item: Order; providers: Connection[]; onClose: () => void; onFeedback: IntegrationFeedbackHandler }) {
  const client = useQueryClient()
  const detail = useQuery({ queryKey: orderDetailQueryKey(item.id), queryFn: ({ signal }) => hubApi<OrderDetail>(`/orders/${item.id}`, { signal }) })
  const order = detail.data
  const marketplace = providers.find(connection => connection.id === order?.connectionId)
  const provider = invoiceProviderForEnvironment(providers, marketplace?.environment)
  const packageId = order ? orderDisplayShipment(order)?.id : undefined
  if (detail.isLoading) return <div className="workspace-modal-backdrop" role="presentation" onMouseDown={onClose}><DialogSurface as="section" onClose={onClose} className="workspace-modal invoice-draft-modal" aria-label="Fatura önizlemesi" onMouseDown={event => event.stopPropagation()}><Busy text="Sipariş, paket ve fatura verileri okunuyor…" /></DialogSurface></div>
  if (detail.isError || !order) return <div className="workspace-modal-backdrop" role="presentation" onMouseDown={onClose}><DialogSurface as="section" onClose={onClose} className="workspace-modal invoice-draft-modal" aria-label="Fatura önizlemesi" onMouseDown={event => event.stopPropagation()}><ErrorBox error={detail.error} onRetry={() => void detail.refetch()} /><button type="button" className="secondary" onClick={onClose}>Kapat</button></DialogSurface></div>
  if (!packageId || !provider) return <div className="workspace-modal-backdrop" role="presentation" onMouseDown={onClose}><DialogSurface as="section" onClose={onClose} className="workspace-modal invoice-draft-modal" aria-labelledby="invoice-draft-blocked-title" onMouseDown={event => event.stopPropagation()}><header><div><h2 id="invoice-draft-blocked-title">Fatura Önizlemesi Açılamadı</h2><p>{displayOrderNumber(order.orderNumber)}</p></div><button type="button" className="modal-close" onClick={onClose} aria-label="Pencereyi kapat"><UiIcon name="close" /></button></header><p className="notice" role="status">{!packageId ? 'Fatura için pazaryeri paket bilgisi henüz yok. Paket eşitlemesinden sonra yeniden deneyin.' : `Sipariş ortamında (${marketplace?.environment ?? 'bilinmiyor'}) etkin ve credential’ı olan E-Faturam bağlantısı bulunamadı.`}</p><footer><button type="button" className="secondary" onClick={onClose}>Kapat</button></footer></DialogSurface></div>
  const target: InvoicePreviewTarget = { orderId: order.id, packageId, providerConnectionId: provider.id }
  return <InvoiceWorkspacePreviewModal targets={[target]} onClose={onClose} onNotice={(text, kind, context) => onFeedback(text, kind, context)} onComplete={() => { void Promise.all([client.invalidateQueries({ queryKey: queryKeys.orders() }), client.invalidateQueries({ queryKey: queryKeys.invoiceWorkspacePage() }), client.invalidateQueries({ queryKey: queryKeys.invoiceWorkspaceSummary() })]) }} />
}
export function InvoiceViewerModal({ item, onClose }: { item: Order; onClose: () => void }) {
  const invoiceId = item.invoiceId
  const detail = useQuery({ queryKey: queryKeys.invoiceViewer(invoiceId), queryFn: ({ signal }) => hubApi<InvoiceViewer>(`/invoices/${invoiceId}`, { signal }), enabled: !!invoiceId })
  const invoice = detail.data
  const money = (value: number) => formatMoney(value, invoice?.currency ?? item.currency)
  return <RrModal crumb="Fatura" title={displayOrderNumber(item.orderNumber)} desc={`${invoice?.invoiceNumber ?? 'Numara bekleniyor'}${invoice ? ` · ${titleTr(statusLabel(invoice.invoiceType))}` : ''}`} size="md" onClose={onClose} foot={<><span className="rr-note" /><button type="button" className="rr-btn" onClick={onClose}>Kapat</button><button type="button" className="rr-btn rr-primary" disabled={!invoice} onClick={() => window.print()}><RrIcon name="print" />Yazdır</button></>}>
    {detail.isLoading ? <RrLoading text="Fatura yükleniyor…" /> : detail.isError || !invoice ? <RrError error={detail.error} onRetry={() => void detail.refetch()} /> : <>
      <div className="rr-facts"><div><small>Durum</small><RrBadge label={titleTr(statusLabel(invoice.status))} /></div><div><small>Fatura Tipi</small><b>{titleTr(statusLabel(invoice.invoiceType))}</b></div><div><small>Ödenecek</small><b className="rr-num">{money(invoice.payableTotal)}</b></div><div><small>Vergi</small><b className="rr-num">{money(invoice.taxTotal)}</b></div></div>
      <table className="rr-ltbl"><thead><tr><th>Ürün</th><th>Miktar</th><th>KDV</th><th>Toplam</th></tr></thead><tbody>{invoice.lines.map(line => <tr key={line.id}><td><b>{line.description}</b><small>{line.sku ?? 'Stok kodu yok'}</small></td><td className="rr-num">{line.quantity} {line.unit}</td><td className="rr-num">%{line.vatRate}</td><td className="rr-num"><b>{money(line.lineTotal)}</b></td></tr>)}</tbody></table>
      <div className="rr-h3">Belgeler</div>
      {invoice.documents.length ? <div style={{ display: 'grid', gap: 6, justifyItems: 'start' }}>{invoice.documents.map(document => <a key={document.id} className="rr-btn rr-link" href={`/api/v1/invoices/${invoice.id}/documents/${document.id}/content`} target="_blank" rel="noreferrer"><RrIcon name="doc" />{document.documentType === 'PDF' ? 'PDF Faturayı Aç' : 'Fatura Belgesini Aç'}<RrIcon name="open" /></a>)}</div> : <p className="rr-lead">Sağlayıcı belgesi henüz arşivlenmedi.</p>}
    </>}
  </RrModal>
}
export function InvoiceUploadModal({ item, onClose, onFeedback }: { item: Order; onClose: () => void; onFeedback: IntegrationFeedbackHandler }) {
  const client = useQueryClient()
  const [file, setFile] = useState<File | null>(null)
  const [invoiceUrl, setInvoiceUrl] = useState('')
  const [invoiceNumber, setInvoiceNumber] = useState('')
  const [issuedAt, setIssuedAt] = useState('')
  const [message, setMessage] = useState('')
  const [savedDocumentId, setSavedDocumentId] = useState<string | null>(null)
  const [documentRequestKey, setDocumentRequestKey] = useState(() => `manual-invoice-document:${item.id}:${newIdempotencyKey()}`)
  const [dragging, setDragging] = useState(false)
  const detail = useQuery({ queryKey: orderDetailQueryKey(item.id), queryFn: ({ signal }) => hubApi<OrderDetail>(`/orders/${item.id}`, { signal }) })
  const saveDocument = useMutation({
    mutationFn: async () => {
      if (Boolean(file) === Boolean(invoiceUrl.trim())) throw new Error('PDF dosyası veya HTTPS fatura bağlantısından yalnızca birini girin.')
      if (file && file.size > 10 * 1024 * 1024) throw new Error('Fatura dosyası en fazla 10 MB olabilir.')
      if (file && !/\.pdf$/i.test(file.name)) throw new Error('Yalnız PDF dosyası yükleyebilirsiniz.')
      const order = detail.data
      if (!order) throw new Error('Sipariş bilgileri henüz yüklenmedi.')
      if (!order.packages?.some(packageItem => packageItem.status !== 'CANCELLED')) throw new Error('Manuel fatura belgesi için siparişte etkin bir paket bulunamadı.')
      const form = new FormData()
      form.append('orderId', order.id)
      form.append('invoiceUrl', invoiceUrl.trim())
      form.append('invoiceNumber', invoiceNumber.trim())
      form.append('issuedAt', issuedAt ? fromDateTimeLocal(issuedAt) : '')
      if (file) form.append('file', file)
      return hubApi<{ id: string }>('/invoice-workspace/manual-documents', { method: 'POST', headers: { 'Idempotency-Key': documentRequestKey }, body: form })
    },
    onSuccess: async result => {
      setSavedDocumentId(result.id)
      const notice = 'Manuel belge siparişe kaydedildi. Pazaryerine henüz gönderilmedi; isterseniz aşağıdaki ayrı düğmeden iletebilirsiniz.'
      setMessage(notice)
      onFeedback('Manuel fatura belgesi kaydedildi; pazaryerine gönderim başlatılmadı.', 'success')
      await Promise.all([client.invalidateQueries({ queryKey: queryKeys.orders() }), client.invalidateQueries({ queryKey: queryKeys.invoiceViewer() }), client.invalidateQueries({ queryKey: queryKeys.invoiceWorkspaceSummary() }), client.invalidateQueries({ queryKey: queryKeys.invoiceWorkspacePage() })])
    },
    onError: error => { const errorMessage = error instanceof Error ? error.message : 'Manuel fatura belgesi kaydedilemedi.'; setMessage(errorMessage); onFeedback(errorMessage, 'error') }
  })
  const deliverDocument = useMutation({
    mutationFn: async () => {
      if (!savedDocumentId) throw new Error('Önce manuel fatura belgesini kaydedin.')
      if (!issuedAt) throw new Error('Platforma iletim için fatura tarihi gereklidir.')
      if (item.platformCode === 'SHOPIFY' && !invoiceNumber.trim()) throw new Error('Shopify siparişine iletim için fatura numarası gereklidir.')
      return hubApi<{ jobIds: string[]; queuedPackageCount: number; inspectionOnlyPackageCount: number }>(`/invoice-workspace/manual-documents/${savedDocumentId}/delivery-jobs`, { method: 'POST', headers: { 'Idempotency-Key': `manual-order-invoice-delivery:${savedDocumentId}:${newIdempotencyKey()}` } })
    },
    onSuccess: async result => {
      const notice = result.inspectionOnlyPackageCount > 0
        ? 'Önceki iletim sonucu belirsiz. Yalnızca pazaryeri durumu okunacak; belge tekrar gönderilmeyecek.'
        : `${result.queuedPackageCount} paket için manuel fatura iletimi kuyruğa alındı.`
      setMessage(notice)
      onFeedback(notice, 'info')
      await Promise.all([client.invalidateQueries({ queryKey: queryKeys.orders() }), client.invalidateQueries({ queryKey: queryKeys.invoiceWorkspacePage() }), client.invalidateQueries({ queryKey: queryKeys.jobs() })])
    },
    onError: error => { const errorMessage = error instanceof Error ? error.message : 'Manuel fatura iletimi kuyruğa alınamadı.'; setMessage(errorMessage); onFeedback(errorMessage, 'error') }
  })
  function changed() { setSavedDocumentId(null); setMessage(''); setDocumentRequestKey(`manual-invoice-document:${item.id}:${newIdempotencyKey()}`) }
  function choose(selected: File | undefined) { if (selected) { setFile(selected); setInvoiceUrl(''); changed() } }
  const hasOneSource = Boolean(file) !== Boolean(invoiceUrl.trim())
  const busy = saveDocument.isPending || deliverDocument.isPending
  const canSend = useCanSendInvoices()
  const failed = deliverDocument.isError || saveDocument.isError
  return <RrModal title="Siparişe Manuel Fatura Belgesi Ekle" desc={displayOrderNumber(item.orderNumber)} hint="Kaydetmek mali fatura oluşturmaz ve platforma gönderim başlatmaz." size="md" locked={busy} onClose={onClose} foot={<><span className="rr-note">{!canSend ? invoiceSendDeniedReason : null}</span><button type="button" className="rr-btn" onClick={onClose} disabled={busy}>Kapat</button><button type="button" className="rr-btn" disabled={!hasOneSource || saveDocument.isPending || detail.isLoading} onClick={() => saveDocument.mutate()}>{saveDocument.isPending ? 'Kaydediliyor…' : 'Belgeyi Kaydet'}</button><button type="button" className="rr-btn rr-primary" disabled={!canSend || !savedDocumentId || !issuedAt || (item.platformCode === 'SHOPIFY' && !invoiceNumber.trim()) || deliverDocument.isPending} onClick={() => deliverDocument.mutate()} title={!canSend ? invoiceSendDeniedReason : !savedDocumentId ? 'Önce belgeyi kaydedin.' : !issuedAt ? 'Fatura tarihi gereklidir.' : undefined}>{deliverDocument.isPending ? 'Kuyruğa Alınıyor…' : 'Platforma İlet'}</button></>}>
    {detail.isError ? <RrError error={detail.error} onRetry={() => void detail.refetch()} /> : detail.isLoading ? <RrLoading text="Sipariş ve paket bilgileri yükleniyor…" /> : <>
      <div className="rr-dgrid"><label className="rr-fld"><small>Fatura Numarası</small><input className="rr-in" value={invoiceNumber} onChange={event => { setInvoiceNumber(event.target.value); changed() }} disabled={busy} maxLength={64} placeholder="RVC2026000000412" /></label><label className="rr-fld"><small>Fatura Tarihi</small><input className="rr-in" type="datetime-local" value={issuedAt} onChange={event => { setIssuedAt(event.target.value); changed() }} disabled={busy} /></label><label className="rr-fld rr-full"><small>Mevcut HTTPS Fatura Bağlantısı</small><input className="rr-in" type="url" value={invoiceUrl} onChange={event => { setInvoiceUrl(event.target.value); if (event.target.value) setFile(null); changed() }} disabled={busy} placeholder="https://…" /></label></div>
      <div className="rr-divider"><span>veya PDF dosyası</span></div>
      <label className={`rr-drop${dragging ? ' rr-on' : ''}`} onDragEnter={event => { event.preventDefault(); setDragging(true) }} onDragOver={event => event.preventDefault()} onDragLeave={() => setDragging(false)} onDrop={event => { event.preventDefault(); setDragging(false); choose(event.dataTransfer.files[0]) }}><input type="file" hidden accept=".pdf,application/pdf" disabled={busy} onChange={event => choose(event.target.files?.[0])} /><RrIcon name="upload" /><b>{file ? file.name : 'PDF Dosyasını Seçin'}</b><small>{file ? `${(file.size / 1024 / 1024).toFixed(2)} MB` : 'PDF · en fazla 10 MB'}</small><span className="rr-btn" style={{ marginTop: 6 }}>Dosya Seç</span></label>
    </>}
    {message && <div className={`rr-callout${failed ? ' rr-danger' : ''}`} role={failed ? 'alert' : 'status'}><RrIcon name={failed ? 'alert' : 'info'} /><span>{message}</span></div>}
  </RrModal>
}
