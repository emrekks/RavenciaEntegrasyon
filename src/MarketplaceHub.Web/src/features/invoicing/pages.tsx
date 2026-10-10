import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { Link, useParams, useSearchParams } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi, loadAllPages } from '../../shared/api'
import { Busy, CargoProviderIcon, ErrorBox, InvoiceStatusBadge, Pagination, Tabs, UiIcon } from '../../shared/components'
import { invoiceStatusLabel, statusLabel } from '../../shared/status-labels'
import { invoiceCompletionOutcome } from './invoice-completion'
import { appendNotification } from '../../shared/notifications'
import { resolveInvoiceTab } from '../../shared/dashboard-operational-links'
import { PlatformMark } from '../../shared/platform-mark'
import { PlatformMultiSelect } from '../../shared/platform-multi-select'
import { invoiceProviderForEnvironment, invoiceSubmissionAction, isInvoiceCreationAvailable, isValidatedInvoiceReadyToSubmit } from './invoice-creation-availability'
import { marketplacePlatformOptions } from '../marketplace/marketplace-platform-support'

type Invoice = { id: string; orderNumber: string; invoiceType: string; status: string; currency: string; payableTotal: number; invoiceNumber: string | null; dueAt: string | null; createdAt: string; version: number }
type InvoiceWorkspaceLine = { sku: string; barcode: string | null; description: string; quantity: number; unitPrice: number; vatRate: number; imageUrl: string | null }
type InvoiceWorkspace = { orderId: string; packageId: string; orderNumber: string; customerName: string; orderedAt: string; shipmentStatus: string; deliveredAt: string | null; invoiceDueAt: string | null; isDueSoon: boolean; currency: string; amount: number; productCount: number; primaryImageUrl: string | null; cargoProviderName: string | null; cargoTrackingNumber: string | null; invoiceId: string | null; invoiceStatus: string; invoiceNumber: string | null; canCreateInvoice: boolean; shipmentAddressJson: string | null; invoiceAddressJson: string | null; lines: InvoiceWorkspaceLine[] | null; invoiceErrorCode: string | null; invoiceDeliveryStatus: string | null; invoiceDeliveryReference: string | null; invoiceDocumentAvailable: boolean; platformCode: string; platformDisplayName: string; invoiceCreationEnabled: boolean; marketplaceEnvironment: string; marketplaceInvoiceReadErrorCode?: string | null; marketplaceInvoiceReadErrorSummary?: string | null }
type InvoiceWorkspacePage = { items: InvoiceWorkspace[]; totalCount: number; pageNumber: number; pageSize: number; totalPages: number; uninvoicedCount: number; invoicedCount: number; dueSoonCount: number; hiddenCount: number; totalPackageCount: number; hasPendingMarketplaceInvoices: boolean; shipmentStatuses: string[]; cargoProviders: string[]; invoiceStatuses: string[] }
type InvoiceDetail = Invoice & { orderId: string; packageId: string | null; providerConnectionId: string; marketplacePlatformCode: string; marketplacePlatformDisplayName: string; sequencePurpose: string; ettnUuid: string | null; taxExclusiveTotal: number; discountTotal: number; taxTotal: number; note: string; issuedAt: string | null; lastErrorCode: string | null; lines: Array<{ id: string; lineSequence: number; description: string; sku: string | null; unit: string; quantity: number; unitPrice: number; discountAmount: number; vatRate: number; vatAmount: number; lineTotal: number }>; documents: Array<{ id: string; documentType: string; sha256: string; createdAt: string }>; attempts: Array<{ attemptNumber: number; outcome: string; errorCode: string | null; startedAt: string; completedAt: string | null }>; deliveries: Array<{ id: string; deliveryType: string; status: string; externalReference: string | null; errorCode: string | null; createdAt: string }>; allowedActions: string[]; requiresSensitiveConfirmation: boolean }
type Connection = { id: string; platformCode: string; environment: string; displayName: string; status: string; hasCredential: boolean }
type InvoiceNoticeKind = 'success' | 'error' | 'info'
const invoiceCreationDisabledHelp = 'Fatura oluşturmak için Entegrasyonlar > bu bağlantı ayarlarından “Fatura oluşturma” seçeneğini açın.'

function idempotency() { return crypto.randomUUID() }
function displayInvoiceOrderNumber(value: string) { return `#${value.trim().replace(/^#+/, '') || '—'}` }
function InvoiceProductSummary({ item }: { item: InvoiceWorkspace }) {
  const triggerRef = useRef<HTMLDivElement>(null)
  const tooltipRef = useRef<HTMLDivElement>(null)
  const [open, setOpen] = useState(false)
  const [position, setPosition] = useState({ left: 16, top: 16 })
  const tooltipId = `invoice-products-${item.packageId}`

  useLayoutEffect(() => {
    if (!open) return
    const updatePosition = () => {
      const trigger = triggerRef.current?.getBoundingClientRect()
      const tooltip = tooltipRef.current?.getBoundingClientRect()
      if (!trigger) return
      const width = tooltip?.width ?? Math.min(340, window.innerWidth - 32)
      const height = tooltip?.height ?? 120
      const below = trigger.bottom + 8
      const top = window.innerHeight - below >= height + 12 || trigger.top <= height + 20
        ? below
        : Math.max(12, trigger.top - height - 8)
      const left = Math.min(Math.max(16, trigger.left), Math.max(16, window.innerWidth - width - 16))
      setPosition({ left, top })
    }
    const frame = window.requestAnimationFrame(updatePosition)
    window.addEventListener('resize', updatePosition)
    window.addEventListener('scroll', updatePosition, true)
    return () => {
      window.cancelAnimationFrame(frame)
      window.removeEventListener('resize', updatePosition)
      window.removeEventListener('scroll', updatePosition, true)
    }
  }, [open, item.lines])

  return <>
    <div ref={triggerRef} className="invoice-reference-product-summary" tabIndex={0} aria-describedby={tooltipId} onMouseEnter={() => setOpen(true)} onMouseLeave={() => setOpen(false)} onFocus={() => setOpen(true)} onBlur={() => setOpen(false)}>
      <small>{item.productCount} adet · {item.lines?.length ?? 1} çeşit</small>
    </div>
    {open && createPortal(<div ref={tooltipRef} className="invoice-reference-product-tooltip invoice-reference-product-tooltip-portal" id={tooltipId} role="tooltip" style={{ left: position.left, top: position.top }}>
      {item.lines?.length ? item.lines.map((line, index) => <article key={`${line.sku}-${index}`}>{line.imageUrl || (index === 0 ? item.primaryImageUrl : null) ? <img src={line.imageUrl || (index === 0 ? item.primaryImageUrl ?? undefined : undefined)} alt="" /> : <span className="invoice-reference-product-image-placeholder"><UiIcon name="image" size={18} /></span>}<span><strong>{line.description || line.sku || 'Ürün'}</strong><small>{line.quantity} adet · Birim {line.unitPrice.toLocaleString('tr-TR', { style: 'currency', currency: item.currency })}</small><small>SKU: {line.sku || '—'} · Barkod: {line.barcode || '—'}</small></span></article>) : <p>Sipariş ürün ayrıntısı bu kayıtta bulunmuyor.</p>}
    </div>, document.body)}
  </>
}

async function waitForInvoiceCompletion(invoiceId: string) {
  for (let attempt = 0; attempt < 120; attempt += 1) {
    const invoice = await hubApi<InvoiceDetail>(`/invoices/${invoiceId}`)
    const outcome = invoiceCompletionOutcome(invoice.status)
    if (outcome === 'complete') return invoice
    if (outcome === 'failed') throw new Error(invoiceFailureToast(invoice))
    if (attempt < 119) await new Promise(resolve => window.setTimeout(resolve, 2000))
  }
  throw new Error('Fatura sağlayıcıda oluşturulmuş olabilir ancak pazaryerine aktarım henüz doğrulanmadı. İşlem arka planda sürüyor; İşlem takibi ekranından platform sonucunu kontrol edin.')
}

async function submitInvoice(item: InvoiceWorkspace, provider: Connection | undefined) {
  if (!item.invoiceCreationEnabled) throw new Error(invoiceCreationDisabledHelp)
  if (!provider) throw new Error(`Bu siparişin ${item.marketplaceEnvironment || 'bilinmeyen'} ortamında etkin ve kimlik bilgisi kayıtlı E-Faturam bağlantısı gereklidir.`)
  let invoice: InvoiceDetail
  if (item.invoiceId) {
    invoice = await hubApi<InvoiceDetail>(`/invoices/${item.invoiceId}`)
    const action = invoiceSubmissionAction(invoice.status, invoice.allowedActions)
    if (action === 'VALIDATE') {
      invoice = await hubApi<InvoiceDetail>(`/invoices/${invoice.id}/validate`, { method: 'POST', headers: { 'If-Match': `"v${invoice.version}"` } })
      if (!isValidatedInvoiceReadyToSubmit(invoice.status, invoice.allowedActions)) throw new Error(invoiceFailureToast(invoice))
    } else if (action !== 'SUBMIT') {
      throw new Error(invoiceFailureToast(invoice))
    }
  } else {
    const draft = await hubApi<InvoiceDetail>('/invoices', { method: 'POST', headers: { 'Idempotency-Key': `invoice:${item.orderId}:${item.packageId}` }, body: JSON.stringify({ orderId: item.orderId, packageId: item.packageId, providerConnectionId: provider.id, originalInvoiceId: null }) })
    invoice = await hubApi<InvoiceDetail>(`/invoices/${draft.id}/validate`, { method: 'POST', headers: { 'If-Match': `"v${draft.version}"` } })
    if (!isValidatedInvoiceReadyToSubmit(invoice.status, invoice.allowedActions)) throw new Error(invoiceFailureToast(invoice))
  }
  await hubApi(`/invoices/${invoice.id}/submit-jobs`, { method: 'POST', headers: { 'Idempotency-Key': item.invoiceId ? `invoice-submit-retry:${invoice.id}:${idempotency()}` : `invoice-submit:${invoice.id}`, 'If-Match': `"v${invoice.version}"` }, body: JSON.stringify({ password: '', confirmed: false }) })
  return invoice.id
}
async function ensureShopifyInvoiceRecord(item: InvoiceWorkspace) {
  if (item.platformCode !== 'SHOPIFY') throw new Error('Manuel fatura kaydı yalnızca Shopify siparişlerinde kullanılabilir.')
  if (item.invoiceId) return item.invoiceId
  if (!item.invoiceCreationEnabled) throw new Error(invoiceCreationDisabledHelp)
  const order = await hubApi<{ id: string; connectionId: string | null; platformCode: string }>(`/orders/${item.orderId}`)
  if (order.platformCode !== 'SHOPIFY' || !order.connectionId) throw new Error('Siparişin Shopify bağlantısı doğrulanamadı.')
  const invoice = await hubApi<{ id: string }>('/invoices', { method: 'POST', headers: { 'Idempotency-Key': `invoice:${item.orderId}:${item.packageId}` }, body: JSON.stringify({ orderId: item.orderId, packageId: item.packageId, providerConnectionId: order.connectionId, originalInvoiceId: null }) })
  return invoice.id
}
async function uploadManualInvoiceDocument(item: InvoiceWorkspace, file: File, provider?: Connection) {
  if (file.size > 10 * 1024 * 1024) throw new Error('Fatura dosyası en fazla 10 MB olabilir.')
  if (!/\.(pdf|jpe?g|png)$/i.test(file.name)) throw new Error('Yalnız PDF, JPEG, JPG veya PNG dosyası yükleyebilirsiniz.')
  let invoiceId = item.invoiceId
  if (!invoiceId) {
    if (!item.invoiceCreationEnabled) throw new Error(invoiceCreationDisabledHelp)
    if (item.platformCode === 'SHOPIFY') invoiceId = await ensureShopifyInvoiceRecord(item)
    else {
      if (!provider) throw new Error('Aktif fatura bağlantısı gereklidir.')
      const invoice = await hubApi<{ id: string }>('/invoices', { method: 'POST', headers: { 'Idempotency-Key': `invoice:${item.orderId}:${item.packageId}` }, body: JSON.stringify({ orderId: item.orderId, packageId: item.packageId, providerConnectionId: provider.id, originalInvoiceId: null }) })
      invoiceId = invoice.id
    }
  }
  const form = new FormData()
  form.append('file', file)
  await hubApi(`/invoices/${invoiceId}/documents/manual`, { method: 'POST', headers: { 'Idempotency-Key': `invoice-document:${invoiceId}:${file.name}:${file.size}:${file.lastModified}` }, body: form })
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
    EFATURAM_CARRIER_CATALOG_MISS: 'Kargo firmasının fatura bilgisi sağlayıcıya tanımlı değil.',
    INVOICE_DELIVERY_ENVIRONMENT_MISMATCH: 'Fatura ve pazaryeri bağlantıları farklı ortamlarda (Stage / Production).',
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
    EFATURAM_CARRIER_CATALOG_MISS: 'Faturayı tekrar deneyin; kargo taşıyıcı bilgisi doğrulamaya eklendi.',
    INVOICE_DELIVERY_ENVIRONMENT_MISMATCH: 'Canlı pazaryeri siparişi için Production E-Faturam bağlantısıyla yeni fatura oluşturun. Stage faturası canlı siparişe gönderilmez.',
    REMOTE_INVOICE_REJECTED: 'Provider veya pazaryeri ret nedenini kontrol edip gerekli bilgileri düzelttikten sonra tekrar deneyin.'
  }
  return labels[code ?? ''] ?? 'Fatura detaylarını açıp son hata kodunu ve provider denemelerini kontrol edin.'
}
function Badge({ value, tone, label }: { value: string; tone?: 'good' | 'warn' | 'neutral'; label?: string }) { const normalized = value.trim().toUpperCase(); const resolvedTone = tone ?? (['READY', 'ACCEPTED', 'COMPLETED', 'ACTIVE', 'SUPPORTED', 'CANCELLED', 'DELIVERED', 'SUCCESS', 'CONFIRMED'].includes(normalized) ? 'good' : ['UNKNOWN_RESULT', 'VALIDATION_FAILED', 'MANUAL_REVIEW', 'UNAPPROVED', 'UNKNOWN', 'CANCELLATION_PENDING', 'FAILED', 'RETRYABLE_FAILURE', 'MARKETPLACE_FAILED'].includes(normalized) ? 'warn' : 'neutral'); return <span className={`badge ${resolvedTone}`}><i aria-hidden="true" />{label ?? statusLabel(value)}</span> }
function isInvoiceFailureStatus(value: string | null | undefined) {
  return ['FATURA_REDDEDILDI', 'REJECTED', 'VALIDATION_FAILED', 'MANUAL_REVIEW', 'MARKETPLACE_FAILED'].includes(value?.trim().toUpperCase() ?? '')
}
function invoiceWorkspaceStatus(item: Pick<InvoiceWorkspace, 'invoiceId' | 'invoiceStatus' | 'canCreateInvoice'>) {
  if (!item.invoiceId || item.canCreateInvoice || item.invoiceStatus === 'FATURA_BEKLIYOR') return { value: 'FATURA_BEKLIYOR', tone: 'warning' as const }
  if (isInvoiceFailureStatus(item.invoiceStatus)) return { value: item.invoiceStatus, tone: 'danger' as const }
  if (['FATURA_YUKLENDI', 'FATURA_KESILDI', 'COMPLETED'].includes(item.invoiceStatus)) return { value: item.invoiceStatus, tone: 'success' as const }
  return { value: item.invoiceStatus || 'FATURA_BILINMIYOR', tone: 'info' as const }
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
function actionLabel(action: string) { return ({ SUBMIT: 'E-Faturam’a gönder', STAGE_CAPABILITY_PROBE: 'Stage mali canary çalıştır', RECONCILE: 'Durumu uzlaştır', DELIVER: 'Pazaryerine fatura bağlantısını ilet', CANCEL: 'E-Arşiv iptal isteği', VALIDATE: 'Yerel doğrula' } as Record<string, string>)[action] ?? action }
function invoiceMarketplaceName(invoice: Pick<InvoiceDetail, 'marketplacePlatformCode' | 'marketplacePlatformDisplayName'>) {
  const code = invoice.marketplacePlatformCode.trim().toUpperCase()
  return ({ TRENDYOL: 'Trendyol', HEPSIBURADA: 'Hepsiburada', SHOPIFY: 'Shopify' } as Record<string, string>)[code] ?? invoice.marketplacePlatformDisplayName ?? 'Pazaryeri'
}
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
  const tabs = [['UNINVOICED', 'Faturalandırılmamışlar'], ['INVOICED', 'Faturalandırılmışlar'], ['DUE_SOON', 'Süresi Yaklaşanlar'], ['HIDDEN', 'Gizlenenler']] as const
  const [searchParams, setSearchParams] = useSearchParams()
  const requestedTab = searchParams.get('tab')
  const initialTab = resolveInvoiceTab(requestedTab)
  const client = useQueryClient(); const [search, setSearch] = useState(''); const [tab, setTab] = useState(initialTab); const [selectedPlatforms, setSelectedPlatforms] = useState<string[] | null>(null); const [shipmentStatusFilter, setShipmentStatusFilter] = useState('ALL'); const [cargoFilter, setCargoFilter] = useState('ALL'); const [invoiceStatusFilter, setInvoiceStatusFilter] = useState('ALL'); const [invoiceActionFilter, setInvoiceActionFilter] = useState<'ALL' | 'CREATABLE' | 'NOT_CREATABLE'>('ALL'); const [dateFrom, setDateFrom] = useState(''); const [dateTo, setDateTo] = useState(''); const [columnFilterOpen, setColumnFilterOpen] = useState<'cargo' | 'shipment' | 'invoice' | 'action' | null>(null); const [message, setMessageState] = useState(''); const [messageKind, setMessageKind] = useState<InvoiceNoticeKind>('info'); const [pageSize, setPageSize] = useState(20); const [pageNumber, setPageNumber] = useState(1); const [selectedItem, setSelectedItem] = useState<InvoiceWorkspace | null>(null); const [shopifyStatusItem, setShopifyStatusItem] = useState<InvoiceWorkspace | null>(null); const [manualStatusBatch, setManualStatusBatch] = useState<InvoiceWorkspace[] | null>(null); const [manualUploadItem, setManualUploadItem] = useState<InvoiceWorkspace | null>(null); const [selectedPackages, setSelectedPackages] = useState<Set<string>>(() => new Set())
  useEffect(() => { setTab(initialTab) }, [initialTab])
  function setMessage(value: string, kind: InvoiceNoticeKind = 'info') { setMessageState(value); setMessageKind(kind); if (value) appendNotification(value, kind) }
  useEffect(() => {
    if (!message) return
    const timeout = window.setTimeout(() => setMessageState(''), messageKind === 'info' ? 7000 : 5500)
    return () => window.clearTimeout(timeout)
  }, [message, messageKind])
  const connections = useQuery({ queryKey: ['connections', 'billing-workspace'], queryFn: () => loadAllPages<Connection>('/connections') })
  const invoiceConnections = connections.data?.items ?? []
  const providerEnvironmentsWithCredential = (['STAGE', 'PRODUCTION'] as const).filter(environment => Boolean(invoiceProviderForEnvironment(invoiceConnections, environment)))
  const providerFor = (item: InvoiceWorkspace) => invoiceProviderForEnvironment(invoiceConnections, item.marketplaceEnvironment)
  const providerHasCredential = providerEnvironmentsWithCredential.length > 0
  const providerEnvironmentKey = providerEnvironmentsWithCredential.join(',')
  const pageQuery = new URLSearchParams({ pageNumber: String(pageNumber), pageSize: String(pageSize), tab, providerHasCredential: String(providerHasCredential), providerEnvironmentsWithCredential: providerEnvironmentKey })
  if (search.trim()) pageQuery.set('search', search.trim())
  if (selectedPlatforms?.length) pageQuery.set('platformCodes', selectedPlatforms.join(','))
  if (shipmentStatusFilter !== 'ALL') pageQuery.set('shipmentStatus', shipmentStatusFilter)
  if (cargoFilter !== 'ALL') pageQuery.set('cargoProviderName', cargoFilter)
  if (invoiceStatusFilter !== 'ALL') pageQuery.set('invoiceStatus', invoiceStatusFilter)
  if (invoiceActionFilter !== 'ALL') pageQuery.set('invoiceAction', invoiceActionFilter)
  if (dateFrom) pageQuery.set('from', new Date(`${dateFrom}T00:00:00`).toISOString())
  if (dateTo) pageQuery.set('to', new Date(`${dateTo}T23:59:59.999`).toISOString())
  const query = useQuery({
    queryKey: ['invoice-workspace-page', pageNumber, pageSize, tab, search, selectedPlatforms, shipmentStatusFilter, cargoFilter, invoiceStatusFilter, invoiceActionFilter, dateFrom, dateTo, providerEnvironmentKey],
    queryFn: () => hubApi<InvoiceWorkspacePage>(`/invoice-workspace/page?${pageQuery.toString()}`),
    refetchInterval: 30_000
  })
  const visibility = useMutation({
    mutationFn: async ({ packageIds, hidden }: { packageIds: string[]; hidden: boolean }) => hubApi<{ updatedCount: number }>('/invoice-workspace/hidden', { method: 'PUT', headers: { 'Idempotency-Key': idempotency() }, body: JSON.stringify({ packageIds, hidden }) }),
    onSuccess: async (_result, variables) => {
      setSelectedPackages(new Set())
      setSelectedItem(null)
      setMessage(variables.hidden ? 'Seçilen siparişler gizlendi.' : 'Seçilen siparişler yeniden görünür yapıldı.', 'success')
      await client.invalidateQueries({ queryKey: ['invoice-workspace'] })
      await client.invalidateQueries({ queryKey: ['invoice-workspace-page'] })
    },
    onError: error => setMessage(error instanceof Error ? error.message : 'Sipariş görünürlüğü güncellenemedi.', 'error')
  })
  useEffect(() => { if (query.data && query.data.pageNumber !== pageNumber) setPageNumber(query.data.pageNumber) }, [query.data?.pageNumber, pageNumber])
  const create = useMutation({ mutationFn: async (item: InvoiceWorkspace) => {
    if (item.platformCode === 'SHOPIFY') return { platformCode: 'SHOPIFY', invoiceId: await ensureShopifyInvoiceRecord(item) }
    const invoiceId = await submitInvoice(item, providerFor(item))
    setMessage(`#${item.orderNumber} için fatura sağlayıcıda işleniyor…`)
    await waitForInvoiceCompletion(invoiceId)
    return { platformCode: item.platformCode, invoiceId }
  }, onMutate: item => setMessage(`#${item.orderNumber} için ${item.platformCode === 'SHOPIFY' ? 'fatura takip kaydı hazırlanıyor…' : 'fatura oluşturuluyor…'}`), onSuccess: async (_result, item) => {
    if (item.platformCode === 'SHOPIFY') setMessage(`Shopify #${item.orderNumber} için fatura takip kaydı hazır. Gerçek faturayı “Manuel fatura yükle” seçeneğinden panele ekleyin.`, 'success')
    else setMessage('Fatura başarıyla oluşturuldu.', 'success')
    await Promise.all([client.invalidateQueries({ queryKey: ['invoice-workspace'] }), client.invalidateQueries({ queryKey: ['invoice-workspace-summary'] }), client.invalidateQueries({ queryKey: ['invoice-workspace-page'] }), client.invalidateQueries({ queryKey: ['orders'] })])
  }, onError: async error => {
    setMessage(error instanceof Error ? error.message : 'Fatura oluşturulamadı.', 'error')
    await Promise.all([client.invalidateQueries({ queryKey: ['invoice-workspace'] }), client.invalidateQueries({ queryKey: ['invoice-workspace-summary'] }), client.invalidateQueries({ queryKey: ['invoice-workspace-page'] }), client.invalidateQueries({ queryKey: ['orders'] })])
  } })
  const bulkCreate = useMutation({
    mutationFn: async (targets: InvoiceWorkspace[]) => {
      const completed: string[] = []
      const failed: Array<{ item: InvoiceWorkspace; reason: string }> = []
      for (const item of targets) {
        try { await create.mutateAsync(item); completed.push(item.packageId) }
        catch (error) { failed.push({ item, reason: error instanceof Error ? error.message : 'İşlem başarısız.' }) }
      }
      return { completed, failed }
    },
    onSuccess: (result, targets) => {
      setSelectedPackages(current => new Set([...current].filter(id => !result.completed.includes(id))))
      const details = result.failed.slice(0, 3).map(item => `#${item.item.orderNumber}: ${item.reason}`).join(' · ')
      const doneText = `${result.completed.length}/${targets.length} sipariş için fatura işlemi tamamlandı.`
      setMessage(result.failed.length ? `${doneText} ${result.failed.length} sipariş işlenemedi.${details ? ` ${details}` : ''}` : doneText, result.failed.length ? 'error' : 'success')
    },
  })
  const pageData = query.data
  const pageItems = pageData?.items ?? []
  const platformOptions = marketplacePlatformOptions(
    connections.data?.items ?? [],
    pageItems.map(item => ({ platformCode: item.platformCode, displayName: item.platformDisplayName }))
  )
  const cargoOptions = pageData?.cargoProviders ?? []
  const shipmentStatusOptions = pageData?.shipmentStatuses ?? []
  const invoiceStatusOptions = (pageData?.invoiceStatuses ?? []).map(value => [value, invoiceStatusLabel(value)] as const)
  const hasInvoiceFilters = Boolean(search.trim() || (selectedPlatforms?.length && selectedPlatforms.length < platformOptions.length) || shipmentStatusFilter !== 'ALL' || cargoFilter !== 'ALL' || invoiceStatusFilter !== 'ALL' || invoiceActionFilter !== 'ALL' || dateFrom || dateTo)
  const totalPages = pageData?.totalPages ?? 1
  const currentPage = pageData?.pageNumber ?? pageNumber
  const selectedItems = pageItems.filter(item => selectedPackages.has(item.packageId))
  const selectedShopifyItems = selectedItems.filter(item => item.platformCode === 'SHOPIFY')
  const selectedCreatableItems = selectedItems.filter(item => !item.invoiceId && requiresInvoiceAction(item) && isInvoiceCreationAvailable(item, Boolean(providerFor(item))))
  const allVisibleSelected = pageItems.length > 0 && pageItems.every(item => selectedPackages.has(item.packageId))
  useEffect(() => { setPageNumber(1) }, [search, tab, selectedPlatforms, shipmentStatusFilter, cargoFilter, invoiceStatusFilter, invoiceActionFilter, dateFrom, dateTo, pageSize])
  useEffect(() => { setSelectedPackages(new Set()) }, [search, tab, selectedPlatforms, shipmentStatusFilter, cargoFilter, invoiceStatusFilter, invoiceActionFilter, dateFrom, dateTo, pageNumber, pageSize])
  const counts = { unInvoiced: pageData?.uninvoicedCount ?? 0, invoiced: pageData?.invoicedCount ?? 0, dueSoon: pageData?.dueSoonCount ?? 0, hidden: pageData?.hiddenCount ?? 0 }
  const activeTabLabel = tabs.find(([value]) => value === tab)?.[1] ?? 'Faturalar'
  function selectTab(value: string) {
    const nextTab = tabs.find(([tabValue]) => tabValue === value)?.[0] ?? 'UNINVOICED'
    setTab(nextTab)
    setSearchParams(current => { const params = new URLSearchParams(current); params.set('tab', nextTab); return params }, { replace: true })
  }
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
      <article className="invoice-metric-total"><small>Toplam paket</small><strong>{pageData?.totalPackageCount ?? 0}</strong><span>fatura çalışma alanı</span></article>
    </div>
    <div className="invoice-reference-filter-shell">
      <Tabs className="invoice-reference-tabs" ariaLabel="Fatura görünümleri" value={tab} onChange={selectTab} items={tabs.map(([value, label]) => ({ value, label, count: value === 'UNINVOICED' ? counts.unInvoiced : value === 'INVOICED' ? counts.invoiced : value === 'DUE_SOON' ? counts.dueSoon : counts.hidden }))} />
      <section className="invoice-reference-filters" aria-label="Fatura filtreleri">
        <label className="invoice-reference-search"><span>Fatura ara</span><span className="invoice-reference-search-control"><UiIcon name="search" size={18} /><input aria-label="Fatura ara" placeholder="Sipariş, müşteri, fatura veya takip no ara…" value={search} onChange={event => setSearch(event.target.value)} /></span></label>
        <PlatformMultiSelect label="Platform" options={platformOptions} selectedCodes={selectedPlatforms} onChange={setSelectedPlatforms} />
        <label className="invoice-reference-date-filter"><span>Sipariş tarihi</span><span className="invoice-reference-date-range"><input type="date" aria-label="Başlangıç tarihi" value={dateFrom} max={dateTo || undefined} onChange={event => setDateFrom(event.target.value)} /><span aria-hidden="true">–</span><input type="date" aria-label="Bitiş tarihi" value={dateTo} min={dateFrom || undefined} onChange={event => setDateTo(event.target.value)} /></span></label>
        {hasInvoiceFilters && <button type="button" className="secondary invoice-reference-filter-reset" onClick={() => { setSearch(''); setSelectedPlatforms(null); setShipmentStatusFilter('ALL'); setCargoFilter('ALL'); setInvoiceStatusFilter('ALL'); setInvoiceActionFilter('ALL'); setDateFrom(''); setDateTo(''); setColumnFilterOpen(null) }}>Filtreleri temizle</button>}
      </section>
    </div>
    <section className="invoice-reference-workspace">
      <header><div><h2>{activeTabLabel}</h2><p>Filtreleme sonuçları: {pageData?.totalCount ?? 0} paket · Fatura işlemleri kayıt bazında yürütülür.</p></div><label className="invoice-reference-page-size">Sayfa başına<select aria-label="Sayfa başına fatura" value={pageSize} onChange={event => setPageSize(Number(event.target.value))}>{[20, 50, 100, 200].map(value => <option key={value} value={value}>{value}</option>)}</select></label></header>
      {selectedItems.length > 0 && <div className="invoice-reference-bulk-toolbar" role="status"><span>{selectedItems.length} sipariş seçildi</span>{selectedCreatableItems.length > 0 && <button type="button" onClick={() => bulkCreate.mutate(selectedCreatableItems)} disabled={bulkCreate.isPending || create.isPending}><UiIcon name="download" size={16} />{bulkCreate.isPending ? 'Faturalar hazırlanıyor…' : `Toplu fatura oluştur (${selectedCreatableItems.length})`}</button>}{selectedShopifyItems.length > 0 && <button type="button" className={selectedCreatableItems.length ? 'secondary' : undefined} onClick={() => { setManualStatusBatch(selectedShopifyItems); setSelectedPackages(current => new Set([...current].filter(id => !selectedShopifyItems.some(item => item.packageId === id)))) }}><UiIcon name="edit" size={16} /> Shopify faturalarını güncelle ({selectedShopifyItems.length})</button>}<button type="button" className="secondary" onClick={() => visibility.mutate({ packageIds: selectedItems.map(item => item.packageId), hidden: tab !== 'HIDDEN' })} disabled={visibility.isPending}><UiIcon name={tab === 'HIDDEN' ? 'eye' : 'eyeOff'} size={16} />{tab === 'HIDDEN' ? 'Görünür yap' : 'Siparişleri gizle'}</button><button type="button" className="secondary" onClick={() => setSelectedPackages(new Set())}>Seçimi temizle</button></div>}
      {query.isLoading ? <div className="invoice-reference-state"><Busy /></div> : query.isError ? <div className="invoice-reference-state"><ErrorBox error={query.error} /></div> : !(pageData?.totalCount ?? 0) ? <div className="invoice-reference-state"><div className="empty"><span className="empty-icon" aria-hidden="true"><UiIcon name="box" /></span><strong>Kayıt yok</strong><p>Seçili fatura sekmesi ve filtrelerle eşleşen paket bulunamadı.</p></div></div> : <><div className="invoice-reference-table" role="table">
        <div className="invoice-reference-head" role="row">
          <div className="invoice-reference-platform-heading"><label className="invoice-reference-select-all"><input type="checkbox" aria-label="Bu sayfadaki tüm siparişleri seç" checked={allVisibleSelected} disabled={!pageItems.length} onChange={() => setSelectedPackages(allVisibleSelected ? new Set() : new Set(pageItems.map(item => item.packageId)))} /><span className="sr-only">Tüm siparişleri seç</span></label><strong>Sipariş bilgileri</strong><PlatformMultiSelect compact label="Platform filtresi" options={platformOptions} selectedCodes={selectedPlatforms} onChange={setSelectedPlatforms} /></div>
          <strong>Alıcı</strong>
          <div className={`order-column-filter${cargoFilter !== 'ALL' ? ' has-filter' : ''}`}><span className="order-column-filter-label">Kargo</span><button type="button" className="order-column-filter-trigger" aria-label="Kargo filtresini aç" aria-expanded={columnFilterOpen === 'cargo'} aria-controls={columnFilterOpen === 'cargo' ? 'invoices-cargo-filter' : undefined} onClick={() => setColumnFilterOpen(current => current === 'cargo' ? null : 'cargo')}><svg className="order-filter-funnel" viewBox="0 0 24 24" aria-hidden="true"><path d="M4 5h16l-6 7.2V18l-4 1v-6.8L4 5z" /></svg></button>{columnFilterOpen === 'cargo' && <div id="invoices-cargo-filter" className="order-column-filter-popover" role="dialog" aria-label="Kargo filtreleri"><label>Kargo firması<select aria-label="Kargo firmasına göre filtrele" value={cargoFilter} onChange={event => setCargoFilter(event.target.value)}><option value="ALL">Tüm kargolar</option>{cargoOptions.map(value => <option key={value} value={value}>{value}</option>)}</select></label>{cargoFilter !== 'ALL' && <button type="button" className="order-column-filter-reset" onClick={() => { setCargoFilter('ALL'); setColumnFilterOpen(null) }}>Filtreyi temizle</button>}</div>}</div>
          <div className={`order-column-filter${shipmentStatusFilter !== 'ALL' ? ' has-filter' : ''}`}><span className="order-column-filter-label">Sipariş durumu</span><button type="button" className="order-column-filter-trigger" aria-label="Sipariş durumu filtresini aç" aria-expanded={columnFilterOpen === 'shipment'} aria-controls={columnFilterOpen === 'shipment' ? 'invoices-shipment-filter' : undefined} onClick={() => setColumnFilterOpen(current => current === 'shipment' ? null : 'shipment')}><svg className="order-filter-funnel" viewBox="0 0 24 24" aria-hidden="true"><path d="M4 5h16l-6 7.2V18l-4 1v-6.8L4 5z" /></svg></button>{columnFilterOpen === 'shipment' && <div id="invoices-shipment-filter" className="order-column-filter-popover" role="dialog" aria-label="Sipariş durumu filtreleri"><label>Sipariş durumu<select aria-label="Sipariş durumuna göre filtrele" value={shipmentStatusFilter} onChange={event => setShipmentStatusFilter(event.target.value)}><option value="ALL">Tüm sipariş durumları</option>{shipmentStatusOptions.map(value => <option key={value} value={value}>{statusLabel(value)}</option>)}</select></label>{shipmentStatusFilter !== 'ALL' && <button type="button" className="order-column-filter-reset" onClick={() => { setShipmentStatusFilter('ALL'); setColumnFilterOpen(null) }}>Filtreyi temizle</button>}</div>}</div>
          <div className={`order-column-filter${invoiceStatusFilter !== 'ALL' ? ' has-filter' : ''}`}><span className="order-column-filter-label">Fatura durumu</span><button type="button" className="order-column-filter-trigger" aria-label="Fatura durumu filtresini aç" aria-expanded={columnFilterOpen === 'invoice'} aria-controls={columnFilterOpen === 'invoice' ? 'invoices-invoice-filter' : undefined} onClick={() => setColumnFilterOpen(current => current === 'invoice' ? null : 'invoice')}><svg className="order-filter-funnel" viewBox="0 0 24 24" aria-hidden="true"><path d="M4 5h16l-6 7.2V18l-4 1v-6.8L4 5z" /></svg></button>{columnFilterOpen === 'invoice' && <div id="invoices-invoice-filter" className="order-column-filter-popover invoice-filter-popover" role="dialog" aria-label="Fatura durumu filtreleri"><label>Fatura durumu<select aria-label="Fatura durumuna göre filtrele" value={invoiceStatusFilter} onChange={event => setInvoiceStatusFilter(event.target.value)}><option value="ALL">Tüm fatura durumları</option>{invoiceStatusOptions.map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label>{invoiceStatusFilter !== 'ALL' && <button type="button" className="order-column-filter-reset" onClick={() => { setInvoiceStatusFilter('ALL'); setColumnFilterOpen(null) }}>Filtreyi temizle</button>}</div>}</div>
          <strong>Tutar</strong>
          <div className={`order-column-filter${invoiceActionFilter !== 'ALL' ? ' has-filter' : ''}`}><span className="order-column-filter-label">İşlemler</span><button type="button" className="order-column-filter-trigger" aria-label="Fatura oluşturma filtresini aç" aria-expanded={columnFilterOpen === 'action'} aria-controls={columnFilterOpen === 'action' ? 'invoices-action-filter' : undefined} onClick={() => setColumnFilterOpen(current => current === 'action' ? null : 'action')}><svg className="order-filter-funnel" viewBox="0 0 24 24" aria-hidden="true"><path d="M4 5h16l-6 7.2V18l-4 1v-6.8L4 5z" /></svg></button>{columnFilterOpen === 'action' && <div id="invoices-action-filter" className="order-column-filter-popover invoice-filter-popover" role="dialog" aria-label="Fatura oluşturma uygunluğu filtreleri"><label>Oluşturma durumu<select aria-label="Fatura oluşturma durumuna göre filtrele" value={invoiceActionFilter} onChange={event => setInvoiceActionFilter(event.target.value as typeof invoiceActionFilter)}><option value="ALL">Tüm işlem durumları</option><option value="CREATABLE">Fatura oluşturulabilir</option><option value="NOT_CREATABLE">Fatura oluşturulamaz</option></select></label>{invoiceActionFilter !== 'ALL' && <button type="button" className="order-column-filter-reset" onClick={() => { setInvoiceActionFilter('ALL'); setColumnFilterOpen(null) }}>Filtreyi temizle</button>}</div>}</div>
        </div>
        {pageItems.map(item => <article className={`invoice-reference-row ${item.isDueSoon ? 'due-soon' : ''}`} key={item.packageId} role="row">
          <div className="invoice-reference-order"><input className="invoice-reference-select" type="checkbox" aria-label={`${item.platformDisplayName} ${displayInvoiceOrderNumber(item.orderNumber)} siparişini seç`} checked={selectedPackages.has(item.packageId)} onChange={() => setSelectedPackages(current => { const next = new Set(current); if (next.has(item.packageId)) next.delete(item.packageId); else next.add(item.packageId); return next })} /><div className="order-reference-platform"><PlatformMark code={item.platformCode} name={item.platformDisplayName} /></div><div><strong>{displayInvoiceOrderNumber(item.orderNumber)}</strong><small>{new Date(item.orderedAt).toLocaleString('tr-TR')}</small>{item.invoiceNumber?.trim() && <small>{item.invoiceNumber}</small>}</div></div>
          <div className="invoice-reference-buyer"><strong>{item.customerName}</strong><InvoiceProductSummary item={item} /></div>
          <div className="invoice-reference-products"><div className="cargo-provider-display invoice-cargo-provider"><CargoProviderIcon value={item.cargoProviderName ?? 'Kargo bilgisi yok'} fallbackText /></div><small>{item.cargoTrackingNumber ?? 'Takip numarası yok'}</small></div>
          <div className="invoice-reference-shipment"><Badge value={item.shipmentStatus} /><small>{item.deliveredAt ? `Teslim: ${new Date(item.deliveredAt).toLocaleDateString('tr-TR')}` : 'Henüz teslim edilmedi'}</small></div>
          <div className="invoice-reference-status">{(() => { const invoiceState = invoiceWorkspaceStatus(item); return <InvoiceStatusBadge status={invoiceState.value} tone={invoiceState.tone} /> })()}{item.marketplaceInvoiceReadErrorCode && <small className="invoice-marketplace-read-error" title={item.marketplaceInvoiceReadErrorSummary ?? undefined}>{item.marketplaceInvoiceReadErrorCode === 'REMOTE_PACKAGE_NOT_FOUND' ? 'Paket bilgisi pazaryerinden alınamadı; kayıtlı fatura durumu gösteriliyor.' : 'Pazaryeri fatura durumu yenilenemedi.'}</small>}{item.invoiceDueAt && <small className={new Date(item.invoiceDueAt).getTime() - Date.now() <= 86_400_000 ? 'deadline critical' : ''}>Son tarih: {new Date(item.invoiceDueAt).toLocaleDateString('tr-TR')}</small>}</div>
          <div className="invoice-reference-amount"><strong>{item.amount.toLocaleString('tr-TR', { style: 'currency', currency: item.currency })}</strong><small>{item.isDueSoon ? 'Öncelikli takip' : 'Sipariş toplamı'}</small></div>
          <div className="invoice-reference-actions">
            {item.platformCode === 'SHOPIFY' ? <>
              <div className="invoice-shopify-split">
                {item.invoiceId && !item.canCreateInvoice ? <InvoiceStatusBadge status={invoiceWorkspaceStatus(item).value} tone={invoiceWorkspaceStatus(item).tone} /> : <span className="shopify-invoice-create-wrap" title={!item.invoiceCreationEnabled ? invoiceCreationDisabledHelp : undefined}><button type="button" className="shopify-invoice-create-trigger" aria-busy={create.isPending && create.variables?.packageId === item.packageId} disabled={create.isPending || !isInvoiceCreationAvailable(item, Boolean(providerFor(item)))} onClick={() => create.mutate(item)}>{create.isPending && create.variables?.packageId === item.packageId && <span className="invoice-action-spinner" aria-hidden="true" />}<span className="invoice-action-label">{create.isPending && create.variables?.packageId === item.packageId ? 'Hazırlanıyor…' : 'Fatura Oluştur'}</span></button></span>}
              </div>
            </> : item.invoiceId ? isInvoiceFailureStatus(item.invoiceStatus) ? <button type="button" aria-busy={create.isPending && create.variables?.packageId === item.packageId} disabled={create.isPending || !isInvoiceCreationAvailable(item, Boolean(providerFor(item)))} title={!item.invoiceCreationEnabled ? invoiceCreationDisabledHelp : undefined} onClick={() => create.mutate(item)}>{create.isPending && create.variables?.packageId === item.packageId && <span className="invoice-action-spinner" aria-hidden="true" />}<span className="invoice-action-label">{create.isPending && create.variables?.packageId === item.packageId ? 'Deneniyor…' : 'Tekrar dene'}</span></button> : <InvoiceStatusBadge status={item.invoiceStatus} /> : <button type="button" aria-busy={create.isPending && create.variables?.packageId === item.packageId} disabled={create.isPending || !isInvoiceCreationAvailable(item, Boolean(providerFor(item)))} title={!item.invoiceCreationEnabled ? invoiceCreationDisabledHelp : undefined} onClick={() => create.mutate(item)}>{create.isPending && create.variables?.packageId === item.packageId && <span className="invoice-action-spinner" aria-hidden="true" />}<span className="invoice-action-label">{create.isPending && create.variables?.packageId === item.packageId ? 'İşleniyor…' : 'Fatura oluştur'}</span></button>}
            {item.invoiceId && item.invoiceDocumentAvailable && <a className="invoice-reference-document-link" href={`/api/v1/invoices/${item.invoiceId}/documents/latest/content`} target="_blank" rel="noreferrer">Fatura linki <UiIcon name="externalLink" /></a>}
            <button type="button" className="invoice-reference-details-trigger" onClick={() => setSelectedItem(item)}>Detayları aç <UiIcon name="externalLink" /></button>
          </div>
        </article>)}
      </div><nav className="order-pagination" aria-label="Fatura sayfaları"><span>Toplam {(pageData?.totalCount ?? 0).toLocaleString('tr-TR')} adet</span><Pagination className="pagination-controls" page={currentPage} totalPages={totalPages} onPageChange={setPageNumber} onPrevious={() => setPageNumber(value => Math.max(1, value - 1))} onNext={() => setPageNumber(value => Math.min(totalPages, value + 1))} /></nav></>}
    </section>
    {selectedItem && <div className="invoice-detail-backdrop" role="presentation" onMouseDown={() => setSelectedItem(null)}><section className="workspace-modal invoice-detail-modal" role="dialog" aria-modal="true" aria-labelledby="invoice-detail-title" onMouseDown={event => event.stopPropagation()}>
      <header className="invoice-detail-header"><div><p className="eyebrow">Sipariş ve fatura özeti</p><h2 id="invoice-detail-title">#{selectedItem.orderNumber}</h2><p>{selectedItem.customerName} · {new Date(selectedItem.orderedAt).toLocaleString('tr-TR')}</p></div><button type="button" className="modal-close" onClick={() => setSelectedItem(null)} aria-label="Detay panelini kapat"><UiIcon name="close" /></button></header>
      <div className="invoice-detail-body"><section className="invoice-detail-summary"><div><small>Sipariş durumu</small><Badge value={selectedItem.shipmentStatus} /></div><div className="invoice-detail-invoice-status"><small>Fatura durumu</small><InvoiceStatusBadge status={selectedItem.invoiceStatus} />{selectedItem.marketplaceInvoiceReadErrorSummary && <small className="invoice-detail-error-reason">Pazaryeri fatura durumu güncellenemedi: {selectedItem.marketplaceInvoiceReadErrorSummary}</small>}{selectedItem.marketplaceInvoiceReadErrorCode && <code className="invoice-detail-error-code">Pazaryeri kontrol hatası: {selectedItem.marketplaceInvoiceReadErrorCode}</code>}{isInvoiceFailureStatus(selectedItem.invoiceStatus) && <small className="invoice-detail-error-reason">{invoiceFailureReason(selectedItem.invoiceErrorCode)}</small>}{selectedItem.invoiceErrorCode && <code className="invoice-detail-error-code">Hata kodu: {selectedItem.invoiceErrorCode}</code>}{isInvoiceFailureStatus(selectedItem.invoiceStatus) && <small className="invoice-detail-error-guidance">{invoiceFailureGuidance(selectedItem.invoiceErrorCode)}</small>}</div><div><small>Toplam</small><strong>{selectedItem.amount.toLocaleString('tr-TR', { style: 'currency', currency: selectedItem.currency })}</strong></div><div><small>Kargo / takip</small><strong>{selectedItem.cargoProviderName ?? '—'}<br />{selectedItem.cargoTrackingNumber ?? 'Takip no yok'}</strong></div></section>
        <section className="invoice-detail-section invoice-summary-section"><div className="invoice-detail-section-heading"><div><h3>Fatura bilgileri</h3><p className="invoice-detail-muted">Fatura numarasını, yüklenen belgeyi ve varsa pazaryeri aktarımını buradan takip edebilirsiniz.</p></div></div><div className="invoice-detail-summary-facts">{selectedItem.invoiceNumber?.trim() && <div><small>Fatura numarası</small><strong>{selectedItem.invoiceNumber}</strong></div>}<div><small>Platform aktarımı</small><Badge value={selectedItem.invoiceDeliveryStatus ?? 'NOT_SENT'} tone={invoiceDeliveryTone(selectedItem.invoiceDeliveryStatus)} label={invoiceDeliveryLabel(selectedItem.invoiceDeliveryStatus)} />{selectedItem.invoiceDeliveryReference && <small>Referans: {selectedItem.invoiceDeliveryReference}</small>}</div></div>{selectedItem.invoiceId && selectedItem.invoiceDocumentAvailable ? <a className="invoice-document-link" href={`/api/v1/invoices/${selectedItem.invoiceId}/documents/latest/content`} target="_blank" rel="noreferrer">Fatura dosyasını aç <UiIcon name="externalLink" /></a> : <p className="invoice-detail-muted">Fatura belgesi henüz yüklenmedi. Manuel olarak panele ekleyebilirsiniz.</p>}<div className="invoice-detail-document-actions"><button type="button" className="secondary" disabled={!selectedItem.invoiceId && (!selectedItem.invoiceCreationEnabled || (selectedItem.platformCode !== 'SHOPIFY' && !providerFor(selectedItem)))} title={!selectedItem.invoiceId && !selectedItem.invoiceCreationEnabled ? invoiceCreationDisabledHelp : !selectedItem.invoiceId && selectedItem.platformCode !== 'SHOPIFY' && !providerFor(selectedItem) ? `Manuel fatura yüklemek için ${selectedItem.marketplaceEnvironment || 'siparişin'} ortamında etkin ve kimlik bilgisi kayıtlı fatura bağlantısı gereklidir.` : undefined} onClick={() => { setManualUploadItem(selectedItem); setSelectedItem(null) }}>Manuel fatura yükle</button>{selectedItem.platformCode === 'SHOPIFY' && <button type="button" className="secondary" disabled={!selectedItem.invoiceCreationEnabled && !selectedItem.invoiceId} title={!selectedItem.invoiceCreationEnabled && !selectedItem.invoiceId ? invoiceCreationDisabledHelp : undefined} onClick={() => { setShopifyStatusItem(selectedItem); setSelectedItem(null) }}>Fatura durumunu değiştir</button>}</div></section>
        <section className="invoice-detail-section"><div className="invoice-detail-section-heading"><h3>Ürünler</h3><span>{selectedItem.lines?.length ?? selectedItem.productCount} kalem</span></div><div className="invoice-detail-lines">{(selectedItem.lines ?? []).map((line, index) => <article className="invoice-detail-line" key={`${line.sku}-${index}`}><span className="invoice-detail-line-media">{line.imageUrl ? <img src={line.imageUrl} alt="" /> : <UiIcon name="image" />}</span><div><strong>{line.description}</strong><small>SKU: {line.sku || '—'} · Barkod: {line.barcode || '—'}</small><small>{line.quantity} adet · Birim {line.unitPrice.toLocaleString('tr-TR', { style: 'currency', currency: selectedItem.currency })} · KDV %{line.vatRate}</small></div></article>)}{!selectedItem.lines?.length && <p className="invoice-detail-muted">Ürün satırı detayına ulaşılamadı; sipariş kaydı korunuyor.</p>}</div></section>
        {addressLines(selectedItem.shipmentAddressJson).length || addressLines(selectedItem.invoiceAddressJson).length ? <section className="invoice-detail-addresses">{addressLines(selectedItem.shipmentAddressJson).length > 0 && <article><h3>Teslimat adresi</h3>{addressLines(selectedItem.shipmentAddressJson).map(line => <span key={line}>{line}</span>)}</article>}{addressLines(selectedItem.invoiceAddressJson).length > 0 && <article><h3>Fatura adresi</h3>{addressLines(selectedItem.invoiceAddressJson).map(line => <span key={line}>{line}</span>)}</article>}</section> : <p className="invoice-detail-address-note">Pazaryeri sipariş kaydında adres bilgisi bulunmuyor.</p>}
      </div><footer className="invoice-detail-footer"><button type="button" className="secondary" onClick={() => setSelectedItem(null)}>Kapat</button><button type="button" className="secondary" onClick={() => visibility.mutate({ packageIds: [selectedItem.packageId], hidden: tab !== 'HIDDEN' })} disabled={visibility.isPending}>{tab === 'HIDDEN' ? 'Görünür yap' : 'Siparişi gizle'}</button>{selectedItem.invoiceId && <Link className="button-link" to={`/invoices/${selectedItem.invoiceId}`}>Fatura kaydını aç</Link>}</footer>
    </section></div>}
    {Boolean(pageData?.hasPendingMarketplaceInvoices) && pageItems.some(item => item.platformCode !== 'SHOPIFY' && requiresInvoiceAction(item) && !providerFor(item)) && <div className="unknown invoice-provider-notice"><strong>Sipariş ortamında E-Faturam bağlantısı hazır değil</strong><p>Her pazaryeri siparişi yalnız kendi Stage veya Production ortamındaki etkin E-Faturam bağlantısıyla faturalandırılabilir. Entegrasyonlar ekranında aynı ortam için bağlantı ve şifreli kimlik bilgisi tanımlayın.</p><Link className="button-link" to="/integrations">Bağlantıyı yönet</Link></div>}
    {shopifyStatusItem && <ShopifyInvoiceStatusModal item={shopifyStatusItem} onClose={() => setShopifyStatusItem(null)} onFeedback={setMessage} />}
    {manualUploadItem && <InvoiceWorkspaceUploadModal item={manualUploadItem} provider={providerFor(manualUploadItem)} onClose={() => setManualUploadItem(null)} onFeedback={setMessage} />}
    {manualStatusBatch && <InvoiceWorkspaceBulkStatusModal items={manualStatusBatch} onClose={() => setManualStatusBatch(null)} onFeedback={setMessage} />}
  </section>
}


function ShopifyInvoiceStatusModal({ item, onClose, onFeedback }: { item: InvoiceWorkspace; onClose: () => void; onFeedback: (message: string, kind: InvoiceNoticeKind) => void }) {
  const client = useQueryClient()
  const [status, setStatus] = useState<'PENDING' | 'UPLOADED'>(item.invoiceStatus === 'FATURA_YUKLENDI' ? 'UPLOADED' : 'PENDING')
  const invoice = useQuery({ queryKey: ['invoice', item.invoiceId], queryFn: () => hubApi<InvoiceDetail>(`/invoices/${item.invoiceId}`), enabled: Boolean(item.invoiceId) })
  const hasUploadedDocument = item.invoiceDocumentAvailable || Boolean(invoice.data?.documents.length)
  const update = useMutation({
    mutationFn: () => hubApi('/invoice-workspace/manual-status', { method: 'PUT', headers: { 'Idempotency-Key': `invoice-workspace-status:${status}:${idempotency()}` }, body: JSON.stringify({ packageIds: [item.packageId], status }) }),
    onSuccess: async () => {
      const message = status === 'UPLOADED' ? 'Shopify fatura durumu “Faturası yüklendi” olarak kaydedildi.' : 'Shopify fatura durumu “Fatura bekliyor” olarak kaydedildi.'
      await Promise.all([client.invalidateQueries({ queryKey: ['invoice-workspace'] }), client.invalidateQueries({ queryKey: ['invoice-workspace-summary'] }), client.invalidateQueries({ queryKey: ['invoice-workspace-page'] }), client.invalidateQueries({ queryKey: ['orders'] }), client.invalidateQueries({ queryKey: ['invoice'] })])
      onFeedback(message, 'success')
      onClose()
    },
    onError: error => onFeedback(error instanceof Error ? error.message : 'Fatura durumu güncellenemedi.', 'error')
  })
  return <div className="workspace-modal-backdrop" role="presentation" onMouseDown={() => { if (!update.isPending) onClose() }}><section className="workspace-modal shopify-invoice-status-modal" role="dialog" aria-modal="true" aria-labelledby="invoice-workspace-shopify-status-title" onMouseDown={event => event.stopPropagation()}><header><div><p className="eyebrow">SHOPIFY</p><h2 id="invoice-workspace-shopify-status-title">Fatura durumunu değiştir</h2><p>{displayInvoiceOrderNumber(item.orderNumber)} · Bu değişiklik yalnızca panel kaydını günceller.</p></div><button type="button" className="modal-close" onClick={onClose} disabled={update.isPending} aria-label="Pencereyi kapat"><UiIcon name="close" /></button></header><div className="shopify-invoice-status-form"><label><span>Yeni fatura durumu</span><select value={status} onChange={event => setStatus(event.target.value as 'PENDING' | 'UPLOADED')} disabled={update.isPending}><option value="PENDING">Fatura bekliyor</option><option value="UPLOADED">Faturası yüklendi</option></select></label><p className="notice" role={status === 'UPLOADED' && !hasUploadedDocument ? 'status' : undefined}>{status === 'UPLOADED' && !hasUploadedDocument ? 'Belge henüz yüklenmedi; bu işlem yalnızca panel durumunu günceller, Shopify’a fatura göndermez. Belge eklemek için sipariş detaylarında “Manuel fatura yükle”yi kullanın.' : 'Shopify’a fatura gönderilmez; yalnızca paneldeki durum güncellenir.'}</p><footer><button type="button" className="secondary" onClick={onClose} disabled={update.isPending}>Vazgeç</button><button type="button" onClick={() => update.mutate()} disabled={update.isPending}>{update.isPending ? 'Kaydediliyor…' : 'Durumu kaydet'}</button></footer></div></section></div>
}

function InvoiceWorkspaceBulkStatusModal({ items, onClose, onFeedback }: { items: InvoiceWorkspace[]; onClose: () => void; onFeedback: (message: string, kind: InvoiceNoticeKind) => void }) {
  const client = useQueryClient()
  const [status, setStatus] = useState<'PENDING' | 'UPLOADED'>('PENDING')
  const update = useMutation({
    mutationFn: () => hubApi<{ updatedCount: number }>('/invoice-workspace/manual-status', { method: 'PUT', headers: { 'Idempotency-Key': `invoice-workspace-status:${status}:${idempotency()}` }, body: JSON.stringify({ packageIds: items.map(item => item.packageId), status }) }),
    onSuccess: async result => {
      await Promise.all([client.invalidateQueries({ queryKey: ['invoice-workspace'] }), client.invalidateQueries({ queryKey: ['invoice-workspace-summary'] }), client.invalidateQueries({ queryKey: ['invoice-workspace-page'] }), client.invalidateQueries({ queryKey: ['orders'] }), client.invalidateQueries({ queryKey: ['invoice'] })])
      const label = status === 'UPLOADED' ? 'Faturası yüklendi' : 'Fatura bekliyor'
      onFeedback(`${result.updatedCount} siparişin fatura durumu “${label}” olarak güncellendi. Dosya yüklenmedi ve pazaryerine fatura gönderilmedi.`, 'success')
      onClose()
    },
    onError: error => onFeedback(error instanceof Error ? error.message : 'Fatura durumları güncellenemedi.', 'error')
  })
  return <div className="workspace-modal-backdrop" role="presentation" onMouseDown={() => { if (!update.isPending) onClose() }}><section className="workspace-modal shopify-invoice-status-modal" role="dialog" aria-modal="true" aria-labelledby="invoice-workspace-bulk-status-title" onMouseDown={event => event.stopPropagation()}><header><div><p className="eyebrow">TOPLU FATURA DURUMU · SHOPIFY</p><h2 id="invoice-workspace-bulk-status-title">Seçilen Shopify faturalarını güncelle</h2><p>{items.length} Shopify siparişi · Fatura dosyası yüklenmez.</p></div><button type="button" className="modal-close" onClick={onClose} disabled={update.isPending} aria-label="Pencereyi kapat"><UiIcon name="close" /></button></header><div className="shopify-invoice-status-form"><label><span>Yeni fatura durumu</span><select aria-label="Yeni fatura durumu" value={status} onChange={event => setStatus(event.target.value as 'PENDING' | 'UPLOADED')} disabled={update.isPending}><option value="PENDING">Fatura bekliyor</option><option value="UPLOADED">Faturası yüklendi</option></select></label><p className="notice">Durum yalnızca Shopify siparişlerinin panel kaydında güncellenir. Dosya yüklenmez ve Shopify’a fatura gönderilmez.</p><ul className="invoice-bulk-status-orders">{items.map(item => <li key={item.packageId}><span className="invoice-bulk-status-platform">Shopify</span><span className="invoice-bulk-status-order">{displayInvoiceOrderNumber(item.orderNumber)}<small>{item.customerName} · {new Date(item.orderedAt).toLocaleDateString('tr-TR')}</small></span><span className="invoice-bulk-status-value">{invoiceStatusLabel(invoiceWorkspaceStatus(item).value)}</span></li>)}</ul><footer><button type="button" className="secondary" onClick={onClose} disabled={update.isPending}>Vazgeç</button><button type="button" onClick={() => update.mutate()} disabled={update.isPending}>{update.isPending ? 'Güncelleniyor…' : `${items.length} Shopify siparişini güncelle`}</button></footer></div></section></div>
}

function InvoiceWorkspaceUploadModal({ item, provider, onClose, onFeedback }: { item: InvoiceWorkspace; provider: Connection | undefined; onClose: () => void; onFeedback: (message: string, kind: InvoiceNoticeKind) => void }) {
  const client = useQueryClient()
  const [file, setFile] = useState<File | null>(null)
  const [message, setMessage] = useState('')
  const [dragging, setDragging] = useState(false)
  const canCreateTrackingRecord = item.invoiceCreationEnabled && (item.platformCode === 'SHOPIFY' || Boolean(provider))
  const upload = useMutation({
    mutationFn: async () => {
      if (!file) throw new Error('Yüklenecek fatura dosyasını seçin.')
      await uploadManualInvoiceDocument(item, file, provider)
    },
    onSuccess: async () => {
      onFeedback('Fatura dosyası güvenli panele yüklendi; bu işlem pazaryerine veya mali sağlayıcıya gönderim yapmadı.', 'success')
      await Promise.all([client.invalidateQueries({ queryKey: ['invoice-workspace'] }), client.invalidateQueries({ queryKey: ['invoice-workspace-summary'] }), client.invalidateQueries({ queryKey: ['invoice-workspace-page'] }), client.invalidateQueries({ queryKey: ['orders'] }), client.invalidateQueries({ queryKey: ['invoice'] })])
      onClose()
    },
    onError: error => { const errorMessage = error instanceof Error ? error.message : 'Fatura dosyası yüklenemedi.'; setMessage(errorMessage); onFeedback(errorMessage, 'error') }
  })
  function choose(selected: File | undefined) { if (selected) { setFile(selected); setMessage('') } }
  const canUpload = Boolean(item.invoiceId) || canCreateTrackingRecord
  return <div className="workspace-modal-backdrop" role="presentation" onMouseDown={() => { if (!upload.isPending) onClose() }}><section className="workspace-modal invoice-upload-modal invoice-workspace-upload-modal" role="dialog" aria-modal="true" aria-labelledby="invoice-workspace-upload-title" onMouseDown={event => event.stopPropagation()}><header><div><p className="eyebrow">{item.platformDisplayName.toLocaleUpperCase('tr-TR')} · {displayInvoiceOrderNumber(item.orderNumber)}</p><h2 id="invoice-workspace-upload-title">Manuel fatura yükle</h2><p>Dosya güvenli panele eklenir; bu işlem pazaryerine veya mali sağlayıcıya gönderim yapmaz.</p></div><button type="button" className="modal-close" onClick={onClose} disabled={upload.isPending} aria-label="Pencereyi kapat"><UiIcon name="close" /></button></header><div className="invoice-upload-context"><div><small>Fatura durumu</small><InvoiceStatusBadge status={item.invoiceStatus} /></div>{item.invoiceNumber?.trim() && <div><small>Fatura numarası</small><strong>{item.invoiceNumber}</strong></div>}<div><small>Pazaryeri aktarımı</small><Badge value={item.invoiceDeliveryStatus ?? 'NOT_SENT'} tone={invoiceDeliveryTone(item.invoiceDeliveryStatus)} label={invoiceDeliveryLabel(item.invoiceDeliveryStatus)} /></div></div>{item.invoiceId && item.invoiceDocumentAvailable && <a className="invoice-document-link" href={`/api/v1/invoices/${item.invoiceId}/documents/latest/content`} target="_blank" rel="noreferrer">Mevcut fatura dosyasını aç <UiIcon name="externalLink" /></a>}<label className={`invoice-dropzone${dragging ? ' dragging' : ''}`} onDragEnter={event => { event.preventDefault(); setDragging(true) }} onDragOver={event => event.preventDefault()} onDragLeave={() => setDragging(false)} onDrop={event => { event.preventDefault(); setDragging(false); choose(event.dataTransfer.files[0]) }}><input type="file" accept=".pdf,.jpeg,.jpg,.png,application/pdf,image/jpeg,image/png" onChange={event => choose(event.target.files?.[0])} /><span className="invoice-upload-icon" aria-hidden="true"><UiIcon name="upload" /></span><strong>{file ? file.name : 'Fatura dosyasını seçin'}</strong><small>{file ? `${(file.size / 1024 / 1024).toFixed(2)} MB` : 'PDF, JPEG veya PNG · En fazla 10 MB'}</small><b>Dosya Seç</b></label>{!item.invoiceId && !canCreateTrackingRecord && <p className="notice" role="status">{!item.invoiceCreationEnabled ? invoiceCreationDisabledHelp : 'Aktif fatura bağlantısı gereklidir.'}</p>}{message && <p className="error" role="alert">{message}</p>}<footer><button type="button" className="secondary" onClick={onClose} disabled={upload.isPending}>Vazgeç</button><button type="button" disabled={!file || upload.isPending || !canUpload} title={!canUpload ? !item.invoiceCreationEnabled ? invoiceCreationDisabledHelp : 'Aktif fatura bağlantısı gereklidir.' : undefined} onClick={() => upload.mutate()}>{upload.isPending ? 'Yükleniyor…' : 'Faturayı Yükle'}</button></footer></section></div>
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
    onSuccess: (_value, variables) => { setNotice(variables.action === 'VALIDATE' ? 'Yerel doğrulama tamamlandı.' : 'İş güvenli kuyruğa alındı.', variables.action === 'VALIDATE' ? 'success' : 'info'); setPassword(''); setConfirmed(false); void client.invalidateQueries({ queryKey: ['invoice', id] }) },
    onError: error => setNotice(error instanceof Error ? error.message : 'İşlem başarısız.', 'error')
  })
  const upload = useMutation({
    mutationFn: (file: File) => { const form = new FormData(); form.append('file', file); return hubApi<{ duplicate: boolean }>(`/invoices/${id}/documents/manual`, { method: 'POST', headers: { 'Idempotency-Key': `invoice-document:${id}:${file.name}:${file.size}:${file.lastModified}` }, body: form }) },
    onSuccess: async result => { setNotice(result.duplicate ? 'Bu fatura belgesi zaten güvenli arşivde bulunuyor.' : 'Fatura belgesi güvenli özel arşive yüklendi. Belge henüz pazaryerine veya E‑Faturam’a iletilmedi.', 'success'); setUploadFile(null); await client.invalidateQueries({ queryKey: ['invoice', id] }) },
    onError: error => setNotice(error instanceof Error ? error.message : 'Fatura belgesi yüklenemedi.', 'error')
  })
  if (query.isLoading) return <section className="content"><Busy /></section>; if (query.isError || !query.data) return <section className="content"><ErrorBox error={query.error} /></section>; const invoice = query.data
  const protectedActions = invoice.requiresSensitiveConfirmation ? invoice.allowedActions.filter(action => action !== 'VALIDATE' && action !== 'RECONCILE') : []
  return <section className="content f3"><Link className="back" to="/invoices"><UiIcon name="arrowLeft" /> Faturalar</Link><div className="page-heading"><div><p className="eyebrow">Fatura detayı</p><h1>{invoice.orderNumber}</h1><p className="lede">{invoice.invoiceNumber?.trim() ? `${invoice.invoiceNumber} · ` : ''}{statusLabel(invoice.invoiceType)}</p></div><InvoiceStatusBadge status={invoice.status} /></div>
    {notice && <div role={noticeKind === 'error' ? 'alert' : 'status'} className={`notice invoice-provider-toast ${noticeKind}`}>{notice}</div>}
    <div className="grid"><article><small>Ödenecek</small><strong>{invoice.payableTotal.toLocaleString('tr-TR', { style: 'currency', currency: invoice.currency })}</strong><p>Vergi: {invoice.taxTotal.toLocaleString('tr-TR')}</p></article><article><small>ETTN / UUID</small><strong>{invoice.ettnUuid ?? 'Henüz atanmadı'}</strong><p>{invoice.sequencePurpose} · {invoice.issuedAt ? new Date(invoice.issuedAt).toLocaleString('tr-TR') : 'Henüz düzenlenmedi'}</p></article><article><small>Son hata</small><strong>{invoice.lastErrorCode ?? 'Yok'}</strong><p>Bilinmeyen sonuç otomatik başarı sayılmaz.</p></article></div>
     <div className="panel"><h2>Satırlar</h2><div className="data-table compact" role="table">{invoice.lines.map(line => <div role="row" key={line.id}><span><strong>{line.description}</strong><small><code className="technical-text sku-value">{line.sku ?? 'SKU yok'}</code> · indirim {line.discountAmount.toLocaleString('tr-TR')}</small></span><span>{line.quantity} {line.unit}</span><span>%{line.vatRate}</span><span>{line.lineTotal.toLocaleString('tr-TR', { style: 'currency', currency: invoice.currency })}</span></div>)}</div></div>
    <div className="split"><div className="panel"><h2>Provider denemeleri</h2>{invoice.attempts.length ? <div className="card-list">{invoice.attempts.map(item => <div className="record-card" key={item.attemptNumber}><span><strong>Deneme #{item.attemptNumber}</strong><small>{item.errorCode ?? 'Hata yok'}</small></span><Badge value={item.outcome} /></div>)}</div> : <p>Henüz dış gönderim denemesi yok.</p>}</div><div className="panel"><h2>{invoiceMarketplaceName(invoice)} teslimleri</h2><p className="invoice-detail-muted">Bu alan, faturanın {invoiceMarketplaceName(invoice)} siparişine gönderilip gönderilmediğini gösterir.</p>{invoice.deliveries.length ? <div className="card-list">{invoice.deliveries.map(item => <div className="record-card" key={item.id}><span><strong>{statusLabel(item.deliveryType)}</strong><small>{item.externalReference ?? item.errorCode ?? 'Referans bekleniyor'}</small></span><Badge value={item.status} /></div>)}</div> : <p>Platforma aktarım henüz başlatılmadı. Belge hazır olduğunda aşağıdaki işlemlerden fatura bağlantısını iletebilirsiniz.</p>}</div></div>
    <div className={`panel invoice-documents-panel ${searchParams.get('upload') === '1' ? 'invoice-upload-highlight' : ''}`}><div className="invoice-panel-heading"><div><h2>Belgeler</h2><p className="invoice-detail-muted">Provider’dan gelen fatura belgesi ve platforma iletilecek güvenli bağlantı.</p></div>{invoice.documents.length > 0 && <a className="invoice-document-link" href={`/api/v1/invoices/${invoice.id}/documents/latest/content`} target="_blank" rel="noreferrer">Fatura linkini aç <UiIcon name="externalLink" /></a>}</div>{invoice.documents.length ? <div className="card-list">{invoice.documents.map(document => <a className="record-card" key={document.id} href={`/api/v1/invoices/${invoice.id}/documents/${document.id}/content`} target="_blank" rel="noreferrer"><span><strong>{statusLabel(document.documentType)}</strong><small>SHA-256 {document.sha256.slice(0, 16)}…</small></span><span>Güvenli aç <UiIcon name="externalLink" /></span></a>)}</div> : <p>Henüz belge eklenmedi. Provider belgesi alındığında ayrıca güvenli biçimde saklanır.</p>}<form className="invoice-document-upload" onSubmit={event => { event.preventDefault(); if (uploadFile) upload.mutate(uploadFile) }}><div><strong>Fatura belgesi yükle</strong><small>PDF, JPEG veya PNG · en fazla 10 MiB · sadece özel arşive kaydedilir</small></div><input type="file" accept="application/pdf,image/jpeg,image/png" onChange={event => setUploadFile(event.target.files?.[0] ?? null)} aria-label="Fatura belgesi seç" /><button type="submit" disabled={!uploadFile || upload.isPending}>{upload.isPending ? 'Yükleniyor…' : 'Belgeyi yükle'}</button></form></div>
    {protectedActions.length > 0 && <form className="panel form-panel" onSubmit={event => event.preventDefault()}><h2>Dış mali işlem onayı</h2><p>Parola yalnız production mali işlemi için yeniden doğrulanır. Sunucu capability, bağlantı ve yazma kapılarını ayrıca denetler.</p><label>Hesap parolası<input type="password" autoComplete="current-password" value={password} onChange={event => setPassword(event.target.value)} /></label><label className="check"><input type="checkbox" checked={confirmed} onChange={event => setConfirmed(event.target.checked)} /> Bu dış mali işlemi açıkça onaylıyorum.</label></form>}
    <div className="panel"><h2>Kullanılabilir işlemler</h2>{invoice.allowedActions.length ? <><p className="invoice-detail-muted">Fatura belgesi hazırsa aktarım işlemi, ilgili {invoiceMarketplaceName(invoice)} bağlantısına ve pakete yönlendirilir. Fatura ancak pazaryeri durumu doğruladığında tamamlandı sayılır.</p><div className="button-row">{invoice.allowedActions.map(action => <button key={action} type="button" onClick={() => operation.mutate({ action, invoice })} disabled={operation.isPending || (protectedActions.includes(action) && (!password || !confirmed))}>{actionLabel(action)}</button>)}</div></> : <div className="unknown"><strong>Dış işlemler kapalı</strong><p>Bağlantı, teknik doğrulama ve güvenli yazma koşulları işlem öncesi denetlenir.</p></div>}</div>
  </section>
}
