import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { Link, useParams, useSearchParams } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi, loadAllPages } from '../../shared/api'
import { newIdempotencyKey } from '../../shared/idempotency'
import { ValueBadge } from '../../shared/components/ValueBadge'
import { setManualInvoiceStatus } from './manual-invoice-status'
import { Busy, CargoProviderIcon, ErrorBox, InvoiceStatusBadge, Modal, Tabs, UiIcon, DialogSurface } from '../../shared/components'
import { invoiceStatusLabel, statusLabel } from '../../shared/status-labels'
import { appendNotification } from '../../shared/notifications'
import { NoticeToast } from '../../shared/components/NoticeToast'
import { resolveInvoiceTab } from '../../shared/dashboard-operational-links'
import { PlatformMark } from '../../shared/platform-mark'
import { platformLogoSource } from '../../shared/platform-logos'
import { PlatformMultiSelect } from '../../shared/platform-multi-select'
import { DateRangePicker } from '../../shared/DateRangePicker'
import { MobileFilterShell } from '../../shared/MobileFilterShell'
import { invoiceProviderForEnvironment, isInvoiceCreationActionVisible, isInvoiceCreationAvailable } from './invoice-creation-availability'
import { InvoiceSortSelect, invoiceSortOptions } from './InvoiceSortSelect'
import { InvoiceWorkspacePreviewModal, type InvoicePreviewTarget } from './InvoiceWorkspacePreviewModal'
import { invoiceReferenceMarketplaceHref, platformInvoiceLinkUnavailable, safeInvoiceReferenceUrl } from './invoice-reference-document-link'
import { marketplacePlatformOptions } from '../marketplace/marketplace-platform-support'
import './invoice-detail-ui.css'
import './manual-invoice-document.css'
import './invoices-responsive.css'
import './invoices-sade.css'
import { InvoiceGlyph, StatusRing, type InvoiceGlyphKind, badgeGlyphKind, invoiceGlyphKind, invoiceStatusProgress, orderStatusProgress } from './invoice-state-glyph'
import { viewTabLabel } from '../../shared/view-tabs'
import { formatDate, formatDateTime, formatIstanbul, fromDateTimeLocal, istanbulDayBoundary, toDateTimeLocal } from '../../shared/format-date'
import { formatMoney } from '../../shared/format-money'

type Invoice = { id: string; orderNumber: string; invoiceType: string; status: string; currency: string; payableTotal: number; invoiceNumber: string | null; dueAt: string | null; createdAt: string; version: number }
type InvoiceWorkspaceLine = { sku: string; barcode: string | null; description: string; quantity: number; unitPrice: number; vatRate: number; imageUrl: string | null }
type InvoiceWorkspace = { orderId: string; packageId: string; orderNumber: string; customerName: string; orderedAt: string; shipmentStatus: string; deliveredAt: string | null; invoiceDueAt: string | null; isDueSoon: boolean; currency: string; amount: number; productCount: number; primaryImageUrl: string | null; cargoProviderName: string | null; cargoTrackingNumber: string | null; invoiceId: string | null; invoiceStatus: string; invoiceNumber: string | null; canCreateInvoice: boolean; shipmentAddressJson: string | null; invoiceAddressJson: string | null; lines: InvoiceWorkspaceLine[] | null; invoiceErrorCode: string | null; invoiceDeliveryStatus: string | null; invoiceDeliveryReference: string | null; invoiceDocumentAvailable: boolean; marketplaceInvoiceUrl: string | null; platformCode: string; platformDisplayName: string; invoiceCreationEnabled: boolean; marketplaceInvoiceReadErrorCode?: string | null; marketplaceInvoiceReadErrorSummary?: string | null; environment: string; manualInvoiceDocumentId: string | null; manualInvoiceDocumentType: string | null; manualInvoiceDocumentUrl: string | null; manualInvoiceNumber: string | null; manualInvoiceIssuedAt: string | null; manualInvoiceDeliveryStatus: string | null; manualInvoiceDeliveryErrorCode: string | null; fiscalStatus: string; platformInvoiceStatus: string; deliveryStatus: string; currentPlatformInvoiceUrl: string | null; lastSuccessfulCheckAt: string | null; allowedActions: string[]; previousPlatformInvoiceReferences?: Array<{ invoiceNumber: string | null; invoiceUrl: string | null; changeReason: string; changedAt: string }>; marketplaceConnectionId?: string | null; externalOrderId?: string | null; externalPackageNumber?: string | null }
type InvoiceWorkspacePage = { items: InvoiceWorkspace[]; totalCount: number; pageNumber: number; pageSize: number; totalPages: number; uninvoicedCount: number; invoicedCount: number; dueSoonCount: number; hiddenCount: number; totalPackageCount: number; hasPendingMarketplaceInvoices: boolean; shipmentStatuses: string[]; cargoProviders: string[]; invoiceStatuses: string[] }
type InvoiceDetail = Invoice & { orderId: string; packageId: string | null; providerConnectionId: string; sequencePurpose: string; ettnUuid: string | null; taxExclusiveTotal: number; discountTotal: number; taxTotal: number; note: string; issuedAt: string | null; lastErrorCode: string | null; lines: Array<{ id: string; lineSequence: number; description: string; sku: string | null; unit: string; quantity: number; unitPrice: number; discountAmount: number; vatRate: number; vatAmount: number; lineTotal: number }>; documents: Array<{ id: string; documentType: string; sha256: string; createdAt: string }>; attempts: Array<{ attemptNumber: number; outcome: string; errorCode: string | null; startedAt: string; completedAt: string | null }>; deliveries: Array<{ id: string; deliveryType: string; status: string; externalReference: string | null; errorCode: string | null; createdAt: string }>; allowedActions: string[]; requiresSensitiveConfirmation: boolean }
type Connection = { id: string; platformCode: string; displayName: string; status: string; environment: string; hasCredential: boolean }
type InvoiceNoticeKind = 'success' | 'error' | 'info' | 'pending'
const invoiceCreationDisabledHelp = 'Fatura oluşturmak için Entegrasyonlar > bu bağlantı ayarlarından “Fatura oluşturma” seçeneğini açın.'

function displayInvoiceOrderNumber(value: string) { return `#${value.trim().replace(/^#+/, '') || '—'}` }

function InvoiceOrderNumber({ item }: { item: InvoiceWorkspace }) {
  const [copied, setCopied] = useState(false)
  async function copyOrderNumber() {
    try {
      await navigator.clipboard.writeText(item.orderNumber)
      setCopied(true)
      window.setTimeout(() => setCopied(false), 1400)
    } catch {
      setCopied(false)
    }
  }
  const tooltipId = `invoice-order-copy-tooltip-${item.packageId}`
  // eslint-disable-next-line jsx-a11y/click-events-have-key-events, jsx-a11y/no-noninteractive-element-interactions -- touch shortcut on phones (tap the number to copy); the row actions serve keyboards
  return <span className="order-number invoice-order-number"><strong onClick={event => { if (window.matchMedia('(max-width: 760px)').matches) { event.stopPropagation(); void copyOrderNumber() } }}>{displayInvoiceOrderNumber(item.orderNumber)}</strong><button type="button" className="order-number-copy" onClick={copyOrderNumber} aria-label={copied ? `Sipariş numarası ${item.orderNumber} kopyalandı` : `Sipariş numarası ${item.orderNumber} kopyala`} aria-describedby={tooltipId}><UiIcon name="copy" /><span id={tooltipId} className={`order-copy-tooltip${copied ? ' is-copied' : ''}`} role="status" aria-live="polite" aria-hidden={!copied}>{copied ? 'Kopyalandı' : 'Kopyala'}</span></button></span>
}
function invoiceCustomerDisplayName(item: Pick<InvoiceWorkspace, 'customerName' | 'cargoTrackingNumber'>) {
  const name = item.customerName.trim()
  const trackingNumber = item.cargoTrackingNumber?.trim()
  if (!trackingNumber || !name.toLocaleLowerCase('tr-TR').endsWith(trackingNumber.toLocaleLowerCase('tr-TR'))) return name || '—'
  return name.slice(0, -trackingNumber.length).replace(/[\s·,;:-]+$/u, '').trim() || '—'
}
function manualInvoiceDeliveryLabel(status: string | null) {
  switch (status) {
    case 'CONFIRMED': return 'Pazaryerinde doğrulandı'
    case 'QUEUED': return 'Platforma iletim sırada'
    case 'STARTED': return 'Platforma iletiliyor'
    case 'UNKNOWN': return 'Sonuç doğrulanıyor'
    case 'MANUAL_REVIEW': return 'İnceleme gerekli'
    case 'RETRYABLE_FAILURE': return 'Güvenli tekrar bekliyor'
    case 'FAILED': return 'İletim başarısız'
    default: return 'Henüz iletilmedi'
  }
}
function manualInvoiceDeliveryActionLabel(status: string | null) {
  if (status === 'UNKNOWN' || status === 'STARTED') return 'Sonucu denetle'
  if (status === 'MANUAL_REVIEW') return 'Pazaryeri durumunu denetle'
  if (status) return 'Kontrol et ve tekrar ilet'
  return 'Platforma ilet'
}

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
    {/* eslint-disable-next-line jsx-a11y/no-noninteractive-tabindex -- tooltip trigger: focusable so keyboards can open the product list */}
    <div ref={triggerRef} className="invoice-reference-product-summary" tabIndex={0} aria-describedby={tooltipId} onMouseEnter={() => setOpen(true)} onMouseLeave={() => setOpen(false)} onFocus={() => setOpen(true)} onBlur={() => setOpen(false)}>
      <small>{item.productCount} adet · {item.lines?.length ?? 1} çeşit</small>
    </div>
    {open && createPortal(<div ref={tooltipRef} className="invoice-reference-product-tooltip invoice-reference-product-tooltip-portal" id={tooltipId} role="tooltip" style={{ left: position.left, top: position.top }}>
      {item.lines?.length ? item.lines.map((line, index) => <article key={`${line.sku}-${index}`}>{line.imageUrl || (index === 0 ? item.primaryImageUrl : null) ? <img src={line.imageUrl || (index === 0 ? item.primaryImageUrl ?? undefined : undefined)} alt="" /> : <span className="invoice-reference-product-image-placeholder"><UiIcon name="image" size={18} /></span>}<span><strong>{line.description || line.sku || 'Ürün'}</strong><small>{line.quantity} adet · Birim {formatMoney(line.unitPrice, item.currency)}</small><small>SKU: {line.sku || '—'} · Barkod: {line.barcode || '—'}</small></span></article>) : <p>Sipariş ürün ayrıntısı bu kayıtta bulunmuyor.</p>}
    </div>, document.body)}
  </>
}

async function uploadManualInvoiceDocument(item: InvoiceWorkspace, file: File | null, invoiceUrl: string, invoiceNumber: string, issuedAt: string) {
  if (file && file.size > 10 * 1024 * 1024) throw new Error('Fatura dosyası en fazla 10 MB olabilir.')
  if (file && !/\.pdf$/i.test(file.name)) throw new Error('Yalnız PDF dosyası yükleyebilirsiniz.')
  if (Boolean(file) === Boolean(invoiceUrl.trim())) throw new Error('PDF dosyası veya HTTPS fatura bağlantısından yalnızca birini girin.')
  if (!issuedAt) throw new Error('Fatura tarihi gereklidir.')
  const form = new FormData()
  form.append('orderId', item.orderId)
  form.append('invoiceUrl', invoiceUrl.trim())
  form.append('invoiceNumber', invoiceNumber.trim())
  form.append('issuedAt', fromDateTimeLocal(issuedAt))
  if (file) form.append('file', file)
  await hubApi('/invoice-workspace/manual-documents', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey(`manual-invoice-document:${item.orderId}`) }, body: form })
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
    HEPSIBURADA_INVOICE_DELIVERY_REPEATED_500: 'Fatura oluşturuldu; Hepsiburada belge iletiminde tekrarlanan sunucu hatası verdi.',
    INVOICE_DELIVERY_ENVIRONMENT_MISMATCH: 'Fatura sağlayıcısı ve pazaryeri farklı ortamlarda. Platform iletimi engellendi.',
    REMOTE_INVOICE_REJECTED: 'Pazaryeri faturayı reddetti.',
    INVOICE_SUBMISSION_OUTCOME_UNKNOWN: 'Sağlayıcıya gönderim sonucu alınamadı. Faturanın portalda oluşup oluşmadığını kontrol edin; oluşmadıysa yeniden gönderebilirsiniz.',
    INVOICE_SUBMISSION_CANCELLED: 'Gönderim sırasında bağlantı kesildi. Faturanın portalda oluşup oluşmadığını kontrol edin.',
    INVOICE_SUBMISSION_TRANSPORT_UNKNOWN: 'Sağlayıcıdan yanıt alınamadı. Faturanın portalda oluşup oluşmadığını kontrol edin.',
    REMOTE_INVOICE_NOT_PRESENT: 'Pazaryeri iletilen faturayı göstermedi; iletim yeniden denenecek.',
    PLATFORM_INVOICE_ALREADY_PRESENT: 'Pazaryerinde bu pakete ait bir fatura zaten görünüyor; üzerine yazılmadı. Pazaryeri panelinde kontrol edin.',
    MARKETPLACE_INVOICE_IDENTITY_MISMATCH: 'Pazaryeri bir fatura gösteriyor ama bu faturayla eşleştirilemedi. Pazaryeri panelinde kontrol edin.',
    DELIVERY_RETRY_PAYLOAD_CHANGED: 'Önceki iletim sonucu belirsiz kaldı ve fatura bilgisi değişti. Pazaryeri panelinde kontrol edin.',
    HEPSIBURADA_INVOICE_DELIVERY_READBACK_MISSING: 'Hepsiburada isteği aldı ama faturayı henüz göstermiyor. Birkaç dakika içinde yeniden kontrol edilir.',
    HEPSIBURADA_REMOTE_ERROR: 'Hepsiburada fatura iletimini kabul etmedi.',
    PLATFORM_INVOICE_STATUS_NOT_CLEAR: 'Pazaryeri fatura durumunu net bildirmedi. Pazaryeri panelinde kontrol edin.',
    OPERATOR_CONFIRMED_NO_REMOTE_INVOICE: 'Portalda fatura olmadığı onaylandı; yeniden gönderilebilir.',
    REMOTE_INVOICE_NUMBER_MISMATCH: 'Pazaryerinde bu faturayla eşleştirilemeyen bir fatura görünüyor. Pazaryeri panelinde kontrol edin.',
    HEPSIBURADA_INVOICE_ALREADY_PRESENT: 'Hepsiburada pakette zaten bir fatura gösteriyor; üzerine yazılmadı.',
    ONE_TIME_INVOICE_DELIVERY_ALREADY_ATTEMPTED: 'Önceki iletim belirsiz kaldı; ikinci kez otomatik gönderilmedi. Pazaryeri panelinde kontrol edin.'
  }
  return labels[code ?? ''] ?? (code ? `İletilemedi: ${code}` : 'Ayrıntılar için faturayı açın.')
}
function invoiceFailureGuidance(code: string | null) {
  const labels: Record<string, string> = {
    EFATURAM_FISCAL_PAYLOAD_INVALID: 'Fatura detaylarını açıp satır, tutar, vergi ve adres bilgilerini kontrol edin.',
    EFATURAM_REQUEST_REJECTED: 'Fatura sağlayıcısının yanıtını kontrol edin; bilgiler düzeltildikten sonra Tekrar Dene seçeneğini kullanın.',
    EFATURAM_APPLICATION_NOT_ACTIVE: 'Entegrasyonlar ekranından E-Faturam uygulamasını etkinleştirip bağlantıyı yeniden test edin.',
    EFATURAM_AUTHENTICATION_FAILED: 'Entegrasyonlar ekranından E-Faturam kimlik bilgilerini yenileyip bağlantıyı test edin.',
    EFATURAM_ACCESS_TOKEN_REJECTED: 'E-Faturam bağlantısını yeniden yetkilendirin, ardından faturayı tekrar deneyin.',
    EFATURAM_INVOICE_CREATE_PRIVILEGE_MISSING: 'E-Faturam hesabında fatura oluşturma yetkisini açtırın.',
    EFATURAM_CARRIER_CATALOG_MISS: 'Faturayı tekrar deneyin; kargo taşıyıcı bilgisi doğrulamaya eklendi.',
    HEPSIBURADA_INVOICE_DELIVERY_REPEATED_500: 'Platforma iletimi tekrar dene ile mevcut belgeyi kontrol edin. Yeni mali fatura oluşturulmaz.',
    INVOICE_DELIVERY_ENVIRONMENT_MISMATCH: 'İletim önizlemesindeki ortam bilgisini kontrol edin. Canlı sipariş için canlı ortamda düzenlenmiş belge gereklidir.',
    REMOTE_INVOICE_REJECTED: 'Fatura sağlayıcısının veya pazaryerinin ret nedenini kontrol edip gerekli bilgileri düzelttikten sonra tekrar deneyin.'
  }
  return labels[code ?? ''] ?? 'Fatura ayrıntılarını açıp son hata kodunu ve fatura sağlayıcısı denemelerini kontrol edin.'
}
function Badge({ value, tone, label }: { value: string; tone?: 'good' | 'warn' | 'neutral'; label?: string }) { const normalized = value.trim().toUpperCase(); const resolvedTone = tone ?? (['READY', 'ACCEPTED', 'COMPLETED', 'ACTIVE', 'SUPPORTED', 'CANCELLED', 'DELIVERED', 'SUCCESS', 'CONFIRMED'].includes(normalized) ? 'good' : ['UNKNOWN_RESULT', 'VALIDATION_FAILED', 'MANUAL_REVIEW', 'UNAPPROVED', 'UNKNOWN', 'CANCELLATION_PENDING', 'FAILED', 'RETRYABLE_FAILURE', 'MARKETPLACE_FAILED'].includes(normalized) ? 'warn' : 'neutral'); return <ValueBadge value={value} tone={resolvedTone} label={label} icon={<InvoiceGlyph kind={badgeGlyphKind(value, resolvedTone)} />} /> }
function isInvoiceFailureStatus(value: string | null | undefined) {
  return ['FATURA_REDDEDILDI', 'FATURA_PLATFORMA_AKTARILMADI', 'REJECTED', 'VALIDATION_FAILED', 'MANUAL_REVIEW', 'MARKETPLACE_FAILED'].includes(value?.trim().toUpperCase() ?? '')
}
// Detail line under the status rings of the table (proposal M): how long it has waited, which invoice it got. The carrier is not repeated here; the Kargo column shows it.
function orderRingDetail(item: InvoiceWorkspace, kind: InvoiceGlyphKind) {
  if (kind === 'packing') return 'Paketleniyor'
  if (kind === 'done') return item.deliveredAt ? `Teslim: ${formatDate(item.deliveredAt)}` : undefined
  if (kind === 'cancelled') return 'Sipariş kapandı'
  return undefined
}
function invoiceRingDetail(item: InvoiceWorkspace, kind: InvoiceGlyphKind, failed: boolean) {
  if (failed) return invoiceFailureReason(item.invoiceErrorCode ?? null)
  if (kind === 'waiting') return item.invoiceDueAt ? `Son Tarih: ${formatDate(item.invoiceDueAt)}` : undefined
  if (kind === 'sending') return 'Yanıt bekleniyor'
  if (kind === 'done') return item.invoiceNumber || item.manualInvoiceNumber || 'Pazaryerinde'
  if (kind === 'cancelled') return 'İptal edildi'
  return undefined
}
function invoiceWorkspaceStatus(item: Pick<InvoiceWorkspace, 'invoiceId' | 'invoiceStatus' | 'canCreateInvoice' | 'platformInvoiceStatus'>) {
  if (['INVOICED', 'RECEIVED'].includes(item.platformInvoiceStatus?.trim().toUpperCase() ?? '')) return { value: 'FATURA_KONTROLDE', tone: 'success' as const }
  if (!item.invoiceId || item.canCreateInvoice || item.invoiceStatus === 'FATURA_BEKLIYOR') return { value: 'FATURA_BEKLIYOR', tone: 'warning' as const }
  if (isInvoiceFailureStatus(item.invoiceStatus)) return { value: item.invoiceStatus, tone: 'danger' as const }
  if (item.invoiceStatus === 'FATURA_PLATFORMA_BEKLIYOR') return { value: item.invoiceStatus, tone: 'warning' as const }
  if (['FATURA_YUKLENDI', 'FATURA_KESILDI', 'COMPLETED'].includes(item.invoiceStatus)) return { value: item.invoiceStatus, tone: 'success' as const }
  return { value: item.invoiceStatus || 'FATURA_BILINMIYOR', tone: 'info' as const }
}
function invoiceDeliveryLabel(status: string | null | undefined) {
  if (!status) return 'Platforma aktarım başlatılmadı'
  return ({ STARTED: 'Aktarım başlatıldı', SUBMITTED: 'Platforma gönderildi', CONFIRMATION_RETRYABLE: 'Platform doğrulaması bekleniyor', CONFIRMED: 'Platformda doğrulandı', RETRYABLE_FAILURE: 'Yeniden denenecek', FAILED: 'Platform aktarımı başarısız', UNKNOWN: 'Aktarım sonucu bekleniyor', REVOKED: 'Platformdaki fatura kaldırıldı', REVIEW_REQUIRED: 'Platformdaki fatura kontrol edilmeli' } as Record<string, string>)[status] ?? statusLabel(status)
}
function invoiceDeliveryTone(status: string | null | undefined): 'good' | 'warn' | 'neutral' {
  if (status === 'CONFIRMED') return 'good'
  if (status) return 'warn'
  return 'neutral'
}
function actionLabel(action: string) { return ({ SUBMIT: 'E-Faturam’a gönder', STAGE_CAPABILITY_PROBE: 'Test Ortamında Mali Deneme Yap', RECONCILE: 'Durumu uzlaştır', DELIVER: 'Trendyol’a fatura linkini ilet', CANCEL: 'E-Arşiv iptal isteği', VALIDATE: 'Yerel doğrula' } as Record<string, string>)[action] ?? action }
function normalizeAddressText(value: string) { return value.toLocaleLowerCase('tr-TR').replace(/s+/g, ' ').trim() }
function addressLines(value: string | null | undefined) {
  if (!value) return []
  try {
    const parsed = JSON.parse(value) as unknown
    const labels: Record<string, string> = {
      fulladdress: 'Adres', address: 'Adres', addressline1: 'Adres satırı', addressline2: 'Adres satırı', address1: 'Adres satırı', address2: 'Adres satırı', address_line_1: 'Adres satırı', address_line_2: 'Adres satırı', street: 'Cadde / sokak',
      neighborhood: 'Mahalle', neighbourhood: 'Mahalle', neighborhoodname: 'Mahalle', district: 'İlçe', districtname: 'İlçe',
      city: 'İl', cityname: 'İl', province: 'İl', provincename: 'İl', state: 'İl', postalcode: 'Posta kodu', postal_code: 'Posta kodu', zipcode: 'Posta kodu', zip: 'Posta kodu', postcode: 'Posta kodu',
      country: 'Ülke', countryname: 'Ülke', phone: 'Telefon'
    }
    const values: Array<{ label: string; value: string }> = []
    const visit = (node: unknown) => {
      if (typeof node !== 'object' || node === null) return
      if (Array.isArray(node)) { node.forEach(visit); return }
      const record = node as Record<string, unknown>
      Object.entries(record).forEach(([key, item]) => {
        const label = labels[key.toLocaleLowerCase('en-US')]
        if (!label || typeof item !== 'string' || !item.trim()) return
        const value = item.trim()
        // The same street text often arrives twice, once as a line and once inside the full address; show it once.
        const normalized = normalizeAddressText(value)
        const streetLabel = ['Adres', 'Adres satırı', 'Cadde / sokak'].includes(label)
        if (values.some(line => line.value === value || (streetLabel && ['Adres', 'Adres satırı', 'Cadde / sokak'].includes(line.label) && (normalizeAddressText(line.value).includes(normalized) || normalized.includes(normalizeAddressText(line.value)))))) return
        values.push({ label, value })
      })
      Object.values(record).forEach(child => { if (typeof child === 'object') visit(child) })
    }
    visit(parsed)
    return values.slice(0, 8)
  } catch { return [] }
}

function InvoiceDetailImagePreview({ url, title, onClose }: { url: string; title: string; onClose: () => void }) {
  return createPortal(<div className="workspace-modal-backdrop image-lightbox-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) onClose() }}>
    <DialogSurface as="section" onClose={() => { onClose() }} className="workspace-modal product-image-modal" aria-labelledby="invoice-detail-image-modal-title" onMouseDown={event => event.stopPropagation()}>
      <header className="product-image-modal-header"><div><h2 id="invoice-detail-image-modal-title">{title}</h2></div><button type="button" className="modal-close" onClick={onClose} aria-label="Kapat"><UiIcon name="close" /></button></header>
      <div className="product-image-modal-body"><div className="product-image-modal-frame"><img src={url} alt={`${title} büyük ürün görseli`} loading="eager" decoding="async" /></div></div>
    </DialogSurface>
  </div>, document.body)
}

function InvoiceDetailLineImage({ url, title }: { url: string; title: string }) {
  const [previewOpen, setPreviewOpen] = useState(false)
  return <>
    <button type="button" className="invoice-detail-line-media invoice-detail-line-image-button" onClick={() => setPreviewOpen(true)} aria-label={`${title} görselini büyüt`} title="Büyütmek için tıklayın">
      <img src={url} alt="" loading="lazy" decoding="async" />
    </button>
    {previewOpen && <InvoiceDetailImagePreview url={url} title={title} onClose={() => setPreviewOpen(false)} />}
  </>
}

function InvoiceReferenceActionsMenu({ item, canCreateInvoice, canSyncOrder, deliveryPending, hidden, onCreateInvoice, onSyncOrder, onToggleHidden, onConfirmNoRemoteInvoice, onReplaceCancelledInvoice, onConfirmDelivered, onAddManualInvoice, onSendManualInvoice }: {
  item: InvoiceWorkspace
  canCreateInvoice: boolean
  canSyncOrder: boolean
  deliveryPending: boolean
  hidden: boolean
  onCreateInvoice: () => void
  onSyncOrder: () => void
  onToggleHidden: () => void
  onConfirmNoRemoteInvoice: () => void
  onReplaceCancelledInvoice: () => void
  onConfirmDelivered: () => void
  onAddManualInvoice: () => void
  onSendManualInvoice: () => void
}) {
  const [open, setOpen] = useState(false)
  const triggerRef = useRef<HTMLButtonElement>(null)
  const menuRef = useRef<HTMLDivElement>(null)
  const [position, setPosition] = useState({ left: 8, top: 8 })
  const hasManualDocument = Boolean(item.manualInvoiceDocumentId)
  const canSendManual = hasManualDocument && item.manualInvoiceDeliveryStatus !== 'CONFIRMED'
  const canRetryInvoice = Boolean(item.platformCode !== 'SHOPIFY' && item.invoiceId && isInvoiceFailureStatus(item.invoiceStatus) && isInvoiceCreationActionVisible(item))
  const invoiceActionAvailable = canCreateInvoice || canRetryInvoice
  const invoiceActionLabel = canRetryInvoice
    ? item.invoiceStatus === 'FATURA_PLATFORMA_AKTARILMADI' ? 'Platforma Tekrar Gönder' : 'Tekrar Dene'
    : 'Fatura Oluştur'
  const manualDeliveryDisabled = deliveryPending || !canSendManual || !item.manualInvoiceIssuedAt
  const manualDeliveryTitle = deliveryPending
    ? 'Fatura iletimi zaten sırada.'
    : !hasManualDocument
      ? 'Önce manuel fatura belgesi ekleyin.'
      : item.manualInvoiceDeliveryStatus === 'CONFIRMED'
        ? 'Manuel fatura pazaryerine zaten iletildi.'
        : !item.manualInvoiceIssuedAt
          ? 'Pazaryerine iletmek için fatura tarihi gerekli.'
          : item.manualInvoiceDeliveryErrorCode ?? undefined
  useLayoutEffect(() => {
    if (!open) return
    const updatePosition = () => {
      const trigger = triggerRef.current?.getBoundingClientRect()
      const menu = menuRef.current?.getBoundingClientRect()
      if (!trigger) return
      const width = menu?.width ?? 220
      const height = menu?.height ?? 150
      const below = trigger.bottom + 5
      const top = window.innerHeight - below >= height + 8 || trigger.top <= height + 12
        ? below
        : Math.max(8, trigger.top - height - 5)
      const left = Math.max(8, Math.min(trigger.right - width, window.innerWidth - width - 8))
      setPosition({ left, top })
    }
    const frame = window.requestAnimationFrame(updatePosition)
    window.addEventListener('resize', updatePosition)
    window.addEventListener('scroll', updatePosition, true)
    const dismiss = (event: MouseEvent | KeyboardEvent) => {
      if (event instanceof KeyboardEvent && event.key === 'Escape') setOpen(false)
      if (event instanceof MouseEvent && !menuRef.current?.contains(event.target as Node) && !triggerRef.current?.contains(event.target as Node)) setOpen(false)
    }
    document.addEventListener('mousedown', dismiss)
    document.addEventListener('keydown', dismiss)
    return () => {
      window.cancelAnimationFrame(frame)
      window.removeEventListener('resize', updatePosition)
      window.removeEventListener('scroll', updatePosition, true)
      document.removeEventListener('mousedown', dismiss)
      document.removeEventListener('keydown', dismiss)
    }
  }, [open])
  return <div className="invoice-reference-actions-menu">
    <button ref={triggerRef} type="button" className="invoice-reference-actions-trigger" aria-haspopup="menu" aria-expanded={open} onClick={() => setOpen(value => !value)}>
      <span>İşlemler</span><UiIcon name="chevronDown" />
    </button>
    {open && createPortal(<div ref={menuRef} className="invoice-reference-actions-popover" role="menu" aria-label={`${displayInvoiceOrderNumber(item.orderNumber)} sipariş işlemleri`} style={{ left: position.left, top: position.top }}>
      <button type="button" role="menuitem" disabled={!invoiceActionAvailable} title={!invoiceActionAvailable ? 'Bu sipariş için fatura oluşturma şu anda uygun değil.' : undefined} onClick={() => { setOpen(false); onCreateInvoice() }}><UiIcon name="invoice" size={16} />{invoiceActionLabel}</button>
      {item.platformCode === 'SHOPIFY'
        ? <button type="button" role="menuitem" onClick={() => { setOpen(false); onToggleHidden() }}><UiIcon name={hidden ? 'eye' : 'eyeOff'} size={16} />{hidden ? 'Gizlemeden Çıkar' : 'Siparişi Gizle'}</button>
        : item.platformCode !== 'TRENDYOL' && <button type="button" role="menuitem" disabled={!canSyncOrder} title={!canSyncOrder ? 'Pazaryeri sipariş bilgileri bulunamadı veya bağlantı etkin değil.' : undefined} onClick={() => { setOpen(false); onSyncOrder() }}><UiIcon name="refresh" size={16} />Sipariş Durumunu Güncelle</button>}
      {item.platformCode !== 'SHOPIFY' && ['FATURA_ISLENIYOR', 'FATURA_REDDEDILDI'].includes(item.invoiceStatus ?? '') && <button type="button" role="menuitem" title="Gönderim sonucu kaybolmuş ve sağlayıcı portalında fatura görünmüyorsa yeniden gönderimi açar." onClick={() => { setOpen(false); onConfirmNoRemoteInvoice() }}><UiIcon name="sync" size={16} />Sağlayıcıda Fatura Yok, Yeniden Gönder</button>}
      {item.platformCode !== 'SHOPIFY' && item.invoiceId && item.invoiceNumber && ['FATURA_PLATFORMA_AKTARILMADI', 'FATURA_REDDEDILDI', 'FATURA_PLATFORMA_BEKLIYOR'].includes(item.invoiceStatus ?? '') && <button type="button" role="menuitem" title="Faturayı pazaryerinin satıcı panelinde gördüyseniz tamamlandı olarak işaretler." onClick={() => { setOpen(false); onConfirmDelivered() }}><UiIcon name="circleCheck" size={16} />Pazaryerinde Gördüm, Tamamla</button>}
      {item.platformCode !== 'SHOPIFY' && item.invoiceId && ['FATURA_PLATFORMA_AKTARILMADI', 'FATURA_KONTROLDE'].includes(item.invoiceStatus ?? '') && <button type="button" role="menuitem" title="Faturayı E-Faturam portalında iptal ettiyseniz bu kaydı kapatır ve yeni fatura kesmenize izin verir." onClick={() => { setOpen(false); onReplaceCancelledInvoice() }}><UiIcon name="undo" size={16} />Portalda İptal Ettim, Yeni Fatura Kes</button>}
      <hr className="invoice-reference-actions-separator" /><button type="button" role="menuitem" onClick={() => { setOpen(false); onAddManualInvoice() }}><UiIcon name="upload" size={16} />{hasManualDocument ? 'Manuel Faturayı Değiştir' : 'Manuel Fatura Ekle'}</button>
      <button type="button" role="menuitem" disabled={manualDeliveryDisabled} title={manualDeliveryDisabled ? manualDeliveryTitle : undefined} onClick={() => { setOpen(false); onSendManualInvoice() }}><UiIcon name="link" size={16} />{deliveryPending ? 'Kuyruğa Alınıyor…' : 'Manuel Faturayı Pazaryerine Gönder'}</button>
    </div>, document.body)}
  </div>
}

export function InvoicesPage() {
  const tabs = [['ALL', 'Tümü'], ['UNINVOICED', 'Faturalandırılmamışlar'], ['INVOICED', 'Faturalandırılmışlar'], ['DUE_SOON', 'Süresi Yaklaşanlar'], ['HIDDEN', 'Gizlenenler']] as const
  const [searchParams, setSearchParams] = useSearchParams()
  const requestedTab = searchParams.get('tab')
  const initialTab = resolveInvoiceTab(requestedTab)
  const client = useQueryClient(); const [search, setSearch] = useState(''); const [sortOrder, setSortOrder] = useState('ORDER_DESC'); const [tab, setTab] = useState(initialTab); const [selectedPlatforms, setSelectedPlatforms] = useState<string[] | null>(null); const [shipmentStatusFilter, setShipmentStatusFilter] = useState('ALL'); const [cargoFilter, setCargoFilter] = useState('ALL'); const [invoiceStatusFilter, setInvoiceStatusFilter] = useState('ALL'); const [invoiceActionFilter, setInvoiceActionFilter] = useState<'ALL' | 'CREATABLE' | 'NOT_CREATABLE'>('ALL'); const [dateFrom, setDateFrom] = useState(''); const [dateTo, setDateTo] = useState(''); const [columnFilterOpen, setColumnFilterOpen] = useState<'cargo' | 'shipment' | 'invoice' | 'action' | null>(null); const [message, setMessageState] = useState(''); const [messageKind, setMessageKind] = useState<InvoiceNoticeKind>('info'); const [pageSize, setPageSize] = useState(20); const [pageNumber, setPageNumber] = useState(1); const [selectedItem, setSelectedItem] = useState<InvoiceWorkspace | null>(null); const [shopifyStatusItem, setShopifyStatusItem] = useState<InvoiceWorkspace | null>(null); const [manualStatusBatch, setManualStatusBatch] = useState<InvoiceWorkspace[] | null>(null); const [manualUploadItem, setManualUploadItem] = useState<InvoiceWorkspace | null>(null); const [selectedPackages, setSelectedPackages] = useState<Set<string>>(() => new Set())
  // A column filter closes on a click outside its column heading or on Escape.
  useEffect(() => {
    if (!columnFilterOpen) return
    const close = (event: MouseEvent | KeyboardEvent) => {
      if (event instanceof KeyboardEvent) { if (event.key === 'Escape') setColumnFilterOpen(null); return }
      if (!(event.target as Element | null)?.closest?.('.order-column-filter, .platform-multi-select-field')) setColumnFilterOpen(null)
    }
    document.addEventListener('mousedown', close)
    document.addEventListener('keydown', close)
    return () => { document.removeEventListener('mousedown', close); document.removeEventListener('keydown', close) }
  }, [columnFilterOpen])
  useEffect(() => { setTab(initialTab) }, [initialTab])
  function setMessage(value: string, kind: InvoiceNoticeKind = 'info') { setMessageState(value); setMessageKind(kind); if (value && kind !== 'pending') appendNotification(value, kind) }
  useEffect(() => {
    if (!message || messageKind === 'pending') return
    const timeout = window.setTimeout(() => setMessageState(''), messageKind === 'info' ? 7000 : 5500)
    return () => window.clearTimeout(timeout)
  }, [message, messageKind])
  const connections = useQuery({ queryKey: ['connections', 'billing-workspace'], queryFn: () => loadAllPages<Connection>('/connections') })
  const providerConnections = connections.data?.items ?? []
  const provider = providerConnections.find(x => x.platformCode === 'TRENDYOL_EFATURAM' && x.hasCredential && (x.status === 'ACTIVE' || x.status === 'VERIFIED'))
  const providerForItem = (item: InvoiceWorkspace) => invoiceProviderForEnvironment(providerConnections, item.environment)
  const pageQuery = new URLSearchParams({ pageNumber: String(pageNumber), pageSize: String(pageSize), tab, providerHasCredential: String(Boolean(provider?.hasCredential)) })
  if (search.trim()) pageQuery.set('search', search.trim())
  if (selectedPlatforms?.length) pageQuery.set('platformCodes', selectedPlatforms.join(','))
  if (shipmentStatusFilter !== 'ALL') pageQuery.set('shipmentStatus', shipmentStatusFilter)
  if (cargoFilter !== 'ALL') pageQuery.set('cargoProviderName', cargoFilter)
  if (invoiceStatusFilter !== 'ALL') pageQuery.set('invoiceStatus', invoiceStatusFilter)
  if (sortOrder !== 'DELIVERY_DESC') pageQuery.set('sort', sortOrder)
  if (invoiceActionFilter !== 'ALL') pageQuery.set('invoiceAction', invoiceActionFilter)
  if (dateFrom) pageQuery.set('from', istanbulDayBoundary(dateFrom))
  if (dateTo) pageQuery.set('to', istanbulDayBoundary(dateTo, true))
  const query = useQuery({
    queryKey: ['invoice-workspace-page', pageNumber, pageSize, tab, search, sortOrder, selectedPlatforms, shipmentStatusFilter, cargoFilter, invoiceStatusFilter, invoiceActionFilter, dateFrom, dateTo, Boolean(provider?.hasCredential)],
    queryFn: () => hubApi<InvoiceWorkspacePage>(`/invoice-workspace/page?${pageQuery.toString()}`),
    refetchInterval: 30_000
  })
  const confirmDelivered = useMutation({
    mutationFn: async (item: InvoiceWorkspace) => hubApi<{ confirmed: boolean }>(`/invoice-workspace/orders/${item.orderId}/confirm-marketplace-delivered`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() } }),
    onSuccess: async (_result, item) => {
      setMessage(`#${item.orderNumber} nolu siparişin faturası pazaryerinde doğrulandı ve tamamlandı olarak işaretlendi.`, 'success')
      await Promise.all([client.invalidateQueries({ queryKey: ['invoice-workspace'] }), client.invalidateQueries({ queryKey: ['invoice-workspace-page'] }), client.invalidateQueries({ queryKey: ['invoice-workspace-summary'] })])
    },
    onError: error => setMessage(error instanceof Error ? error.message : 'Fatura tamamlandı olarak işaretlenemedi.', 'error')
  })
  const replaceInvoice = useMutation({
    mutationFn: async (item: InvoiceWorkspace) => hubApi<{ replaced: boolean }>(`/invoice-workspace/orders/${item.orderId}/replace-cancelled-invoice`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() } }),
    onSuccess: async (_result, item) => {
      setMessage(`#${item.orderNumber} nolu siparişin eski faturası iptal olarak kapatıldı. Yeni fatura kesebilirsiniz.`, 'success')
      await Promise.all([client.invalidateQueries({ queryKey: ['invoice-workspace'] }), client.invalidateQueries({ queryKey: ['invoice-workspace-page'] }), client.invalidateQueries({ queryKey: ['invoice-workspace-summary'] })])
    },
    onError: error => setMessage(error instanceof Error ? error.message : 'Eski fatura kapatılamadı.', 'error')
  })
  const noRemoteInvoice = useMutation({
    mutationFn: async (item: InvoiceWorkspace) => hubApi<{ confirmed: boolean }>(`/invoice-workspace/orders/${item.orderId}/confirm-no-remote-invoice`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() } }),
    onSuccess: async (_result, item) => {
      setMessage(`#${item.orderNumber} nolu siparişin faturası için yeniden gönderim açıldı. Faturayı tekrar oluşturabilirsiniz.`, 'success')
      await Promise.all([client.invalidateQueries({ queryKey: ['invoice-workspace'] }), client.invalidateQueries({ queryKey: ['invoice-workspace-page'] })])
    },
    onError: error => setMessage(error instanceof Error ? error.message : 'Yeniden gönderim açılamadı.', 'error')
  })
  const visibility = useMutation({
    mutationFn: async ({ packageIds, hidden }: { packageIds: string[]; hidden: boolean }) => hubApi<{ updatedCount: number }>('/invoice-workspace/hidden', { method: 'PUT', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ packageIds, hidden }) }),
    onSuccess: async (_result, variables) => {
      setSelectedPackages(new Set())
      setSelectedItem(null)
      setMessage(variables.hidden ? 'Seçilen siparişler gizlendi.' : 'Seçilen siparişler yeniden görünür yapıldı.', 'success')
      await client.invalidateQueries({ queryKey: ['invoice-workspace'] })
      await client.invalidateQueries({ queryKey: ['invoice-workspace-page'] })
    },
    onError: error => setMessage(error instanceof Error ? error.message : 'Sipariş görünürlüğü güncellenemedi.', 'error')
  })
  const manualInvoiceDelivery = useMutation({
    mutationFn: async (item: InvoiceWorkspace) => {
      if (!item.manualInvoiceDocumentId) throw new Error('Siparişte güncel manuel fatura belgesi bulunamadı.')
      return hubApi<{ jobIds: string[]; queuedPackageCount: number; inspectionOnlyPackageCount: number }>(`/invoice-workspace/manual-documents/${item.manualInvoiceDocumentId}/delivery-jobs`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() } })
    },
    onSuccess: async result => {
      setMessage(result.inspectionOnlyPackageCount > 0
        ? `${result.inspectionOnlyPackageCount} paketin önceki iletim sonucu belirsiz; yalnızca pazaryeri durumu okunacak, yeni belge gönderilmeyecek.`
        : `${result.queuedPackageCount} paket için taze durum kontrolü ve manuel fatura iletimi kuyruğa alındı.`, 'success')
      await Promise.all([client.invalidateQueries({ queryKey: ['invoice-workspace-page'] }), client.invalidateQueries({ queryKey: ['orders'] }), client.invalidateQueries({ queryKey: ['jobs'] })])
    },
    onError: error => setMessage(error instanceof Error ? error.message : 'Manuel fatura iletimi kuyruğa alınamadı.', 'error')
  })
  const orderStatusSync = useMutation({
    mutationFn: async (item: InvoiceWorkspace) => {
      const connection = connections.data?.items.find(candidate => candidate.id === item.marketplaceConnectionId
        && candidate.platformCode === item.platformCode
        && (!item.environment || item.environment === 'UNKNOWN' || candidate.environment === item.environment))
      if (!connection || !['ACTIVE', 'VERIFIED'].includes(connection.status)) throw new Error('Sipariş için etkin pazaryeri bağlantısı bulunamadı.')
      if (!item.externalOrderId?.trim()) throw new Error('Pazaryeri sipariş numarası bulunamadı.')
      const supportsPackageLookup = ['TRENDYOL', 'HEPSIBURADA'].includes(item.platformCode)
      return hubApi<{ jobId: string }>(`/connections/${connection.id}/order-sync-jobs`, {
        method: 'POST',
        headers: { 'Idempotency-Key': `invoice-order-refresh:${item.packageId}:${newIdempotencyKey()}` },
        body: JSON.stringify({ externalOrderId: item.externalOrderId, full: false, packageNumber: supportsPackageLookup ? item.externalPackageNumber : null })
      })
    },
    onSuccess: async () => {
      setMessage('Pazaryerindeki sipariş durumu yenileme kuyruğuna alındı.', 'success')
      await Promise.all([client.invalidateQueries({ queryKey: ['invoice-workspace-page'] }), client.invalidateQueries({ queryKey: ['orders'] }), client.invalidateQueries({ queryKey: ['jobs'] })])
    },
    onError: error => setMessage(error instanceof Error ? error.message : 'Sipariş durumu pazaryerinden yenilenemedi.', 'error')
  })
  const invoiceReconcile = useMutation({
    mutationFn: async (item: InvoiceWorkspace) => hubApi<{ jobId: string }>(`/invoice-workspace/orders/${item.orderId}/reconcile-jobs`, { method: 'POST', headers: { 'Idempotency-Key': `invoice-workspace-check:${item.orderId}:${newIdempotencyKey()}` } }),
    onSuccess: async () => {
      setMessage('Pazaryerindeki güncel fatura bilgisi salt okunur kontrol kuyruğuna alındı.', 'success')
      await Promise.all([client.invalidateQueries({ queryKey: ['invoice-workspace-page'] }), client.invalidateQueries({ queryKey: ['jobs'] })])
    },
    onError: error => setMessage(error instanceof Error ? error.message : 'Pazaryeri fatura kontrolü kuyruğa alınamadı.', 'error')
  })
  const serverPageNumber = query.data?.pageNumber
  useEffect(() => { if (serverPageNumber !== undefined && serverPageNumber !== pageNumber) setPageNumber(serverPageNumber) }, [serverPageNumber, pageNumber])
  const [previewTargets, setPreviewTargets] = useState<InvoicePreviewTarget[] | null>(null)
  const [manualConfirmation, setManualConfirmation] = useState<{ kind: 'delivered' | 'replace' | 'noRemote'; item: InvoiceWorkspace } | null>(null)
  function runManualConfirmation() {
    const confirmation = manualConfirmation
    if (!confirmation) return
    setManualConfirmation(null)
    if (confirmation.kind === 'delivered') confirmDelivered.mutate(confirmation.item)
    else if (confirmation.kind === 'replace') replaceInvoice.mutate(confirmation.item)
    else noRemoteInvoice.mutate(confirmation.item)
  }
  async function openInvoicePreview(items: InvoiceWorkspace[]) {
    const targets: InvoicePreviewTarget[] = []
    const missing: string[] = []
    const seenOrders = new Set<string>()
    for (const item of items) {
      if (seenOrders.has(item.orderId)) continue
      seenOrders.add(item.orderId)
      if (item.manualInvoiceDocumentId) {
        missing.push(`#${item.orderNumber} için manuel fatura belgesi zaten kayıtlı`)
        continue
      }
      if (item.invoiceId) {
        try {
          const existing = await hubApi<InvoiceDetail>(`/invoices/${item.invoiceId}`)
          targets.push({ orderId: item.orderId, packageId: item.packageId, providerConnectionId: existing.providerConnectionId })
        } catch (error) {
          setMessage(error instanceof Error ? error.message : 'Mevcut fatura bilgileri okunamadı.', 'error')
        }
        continue
      }
      const exactEnvironmentProvider = providerForItem(item)
      if (!exactEnvironmentProvider) { missing.push(`#${item.orderNumber} (${item.environment})`); continue }
      targets.push({ orderId: item.orderId, packageId: item.packageId, providerConnectionId: exactEnvironmentProvider.id })
    }
    if (missing.length) setMessage(`Aynı ortamda etkin ve API bilgileri kayıtlı E-Faturam bağlantısı bulunamadı: ${missing.slice(0, 4).join(', ')}${missing.length > 4 ? ` ve ${missing.length - 4} kayıt daha` : ''}.`, 'error')
    if (targets.length) setPreviewTargets(targets)
  }
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
  const selectedCreatableItems = selectedItems.filter(item => !item.manualInvoiceDocumentId && requiresInvoiceAction(item) && isInvoiceCreationAvailable(item, Boolean(providerForItem(item)?.hasCredential)))
  const allVisibleSelected = pageItems.length > 0 && pageItems.every(item => selectedPackages.has(item.packageId))
  const [selectionMode, setSelectionMode] = useState(false)
  function exitSelectionMode() { setSelectionMode(false); setSelectedPackages(new Set()) }
  useEffect(() => { setPageNumber(1) }, [search, sortOrder, tab, selectedPlatforms, shipmentStatusFilter, cargoFilter, invoiceStatusFilter, invoiceActionFilter, dateFrom, dateTo, pageSize])
  useEffect(() => { setSelectedPackages(new Set()) }, [search, tab, selectedPlatforms, shipmentStatusFilter, cargoFilter, invoiceStatusFilter, invoiceActionFilter, dateFrom, dateTo, pageNumber, pageSize])
  const counts = { all: (pageData?.uninvoicedCount ?? 0) + (pageData?.invoicedCount ?? 0), unInvoiced: pageData?.uninvoicedCount ?? 0, invoiced: pageData?.invoicedCount ?? 0, dueSoon: pageData?.dueSoonCount ?? 0, hidden: pageData?.hiddenCount ?? 0 }
  const activeTabLabel = tabs.find(([value]) => value === tab)?.[1] ?? 'Faturalar'
  function selectTab(value: string) {
    const nextTab = tabs.find(([tabValue]) => tabValue === value)?.[0] ?? 'UNINVOICED'
    setTab(nextTab)
    setSearchParams(current => { const params = new URLSearchParams(current); params.set('tab', nextTab); return params }, { replace: true })
  }
  return <section className="content f3 invoices-page reference-invoices-page">
    <div className="page-heading invoices-reference-heading">
      <div><p className="eyebrow">Mali Belgeler</p><h1>Faturalar</h1><p className="invoices-reference-lead">Paket bazlı fatura kesimi, pazaryerine iletim ve takip.</p></div>
    </div>
    {message && <NoticeToast key={`${messageKind}:${message}`} kind={messageKind} message={message} onClose={() => setMessageState('')} durationMs={messageKind === 'pending' ? undefined : messageKind === 'info' ? 7000 : 5500} />}
    <div className="invoice-reference-metrics">
      <article className="invoice-metric-pending"><small>Fatura Bekleyen</small><strong>{pageData ? counts.unInvoiced.toLocaleString('tr-TR') : '—'}</strong><span>paket bazlı işlem</span></article>
      <article className="invoice-metric-due"><small>Süresi Yaklaşan</small><strong>{pageData ? counts.dueSoon.toLocaleString('tr-TR') : '—'}</strong><span>teslimden 5 gün geçen</span></article>
      <article className="invoice-metric-complete"><small>Faturalandırılan</small><strong>{pageData ? counts.invoiced.toLocaleString('tr-TR') : '—'}</strong><span>ikinci fatura kapalı</span></article>
      <article className="invoice-metric-total"><small>Toplam Paket</small><strong>{pageData ? pageData.totalPackageCount.toLocaleString('tr-TR') : '—'}</strong><span>fatura çalışma alanı</span></article>
    </div>
    <div className="invoice-reference-filter-shell">
      <Tabs className="invoice-reference-tabs rv-view-tabs" ariaLabel="Fatura görünümleri" value={tab} onChange={selectTab} items={tabs.map(([value, label]) => ({ value, label: viewTabLabel(({ ALL: 'all', UNINVOICED: 'invoiceMissing', INVOICED: 'invoiceDone', DUE_SOON: 'due', HIDDEN: 'hidden' } as const)[value], label), count: !pageData ? undefined : (value === 'ALL' ? counts.all : value === 'UNINVOICED' ? counts.unInvoiced : value === 'INVOICED' ? counts.invoiced : value === 'DUE_SOON' ? counts.dueSoon : counts.hidden).toLocaleString('tr-TR') }))} />
      <MobileFilterShell as="section" className="invoice-reference-filters" aria-label="Fatura filtreleri" sort={{ title: 'Sıralama', value: sortOrder, options: invoiceSortOptions.map(option => ({ value: option.value, label: `${option.label} · ${option.direction === 'down' ? 'Azalan' : 'Artan'}` })), onChange: setSortOrder }} activeCount={(search.trim() ? 1 : 0) + (selectedPlatforms?.length && selectedPlatforms.length < platformOptions.length ? 1 : 0) + (shipmentStatusFilter !== 'ALL' ? 1 : 0) + (cargoFilter !== 'ALL' ? 1 : 0) + (invoiceStatusFilter !== 'ALL' ? 1 : 0) + (invoiceActionFilter !== 'ALL' ? 1 : 0) + (dateFrom || dateTo ? 1 : 0) + (sortOrder !== 'ORDER_DESC' ? 1 : 0)} onClear={() => { setSearch(''); setSelectedPlatforms(null); setShipmentStatusFilter('ALL'); setCargoFilter('ALL'); setInvoiceStatusFilter('ALL'); setInvoiceActionFilter('ALL'); setDateFrom(''); setDateTo(''); setColumnFilterOpen(null); setSortOrder('ORDER_DESC') }}>
        <label className="invoice-reference-search" data-mfs-search><span>Fatura Ara</span><span className="invoice-reference-search-control"><UiIcon name="search" size={18} /><input aria-label="Fatura Ara" placeholder="Sipariş, müşteri, fatura veya takip no ara…" value={search} onChange={event => setSearch(event.target.value)} /></span></label>
        <PlatformMultiSelect label="Platform" options={platformOptions} selectedCodes={selectedPlatforms} onChange={setSelectedPlatforms} />
        <DateRangePicker className="invoice-reference-date-filter" from={dateFrom} to={dateTo} onChange={range => { setDateFrom(range.from); setDateTo(range.to) }} label="Sipariş Tarihi" />
        <InvoiceSortSelect value={sortOrder} onChange={setSortOrder} />
        <label className="mfs-field mfs-sheet-only"><span>Kargo firması</span><select aria-label="Kargo firmasına göre filtrele (mobil)" value={cargoFilter} onChange={event => setCargoFilter(event.target.value)}><option value="ALL">Tüm kargolar</option>{cargoOptions.map(value => <option key={value} value={value}>{value}</option>)}</select></label><label className="mfs-field mfs-sheet-only"><span>Sipariş durumu</span><select aria-label="Sipariş durumuna göre filtrele (mobil)" value={shipmentStatusFilter} onChange={event => setShipmentStatusFilter(event.target.value)}><option value="ALL">Tüm sipariş durumları</option>{shipmentStatusOptions.map(value => <option key={value} value={value}>{statusLabel(value)}</option>)}</select></label><label className="mfs-field mfs-sheet-only"><span>Fatura durumu</span><select aria-label="Fatura durumuna göre filtrele (mobil)" value={invoiceStatusFilter} onChange={event => setInvoiceStatusFilter(event.target.value)}><option value="ALL">Tüm fatura durumları</option>{invoiceStatusOptions.map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label><label className="mfs-field mfs-sheet-only"><span>Oluşturma durumu</span><select aria-label="Fatura oluşturma durumuna göre filtrele (mobil)" value={invoiceActionFilter} onChange={event => setInvoiceActionFilter(event.target.value as typeof invoiceActionFilter)}><option value="ALL">Tüm işlem durumları</option><option value="CREATABLE">Fatura oluşturulabilir</option><option value="NOT_CREATABLE">Fatura oluşturulamaz</option></select></label>
        <label className="mfs-field mfs-sheet-only"><span>Sayfa başına fatura</span><select aria-label="Sayfa başına fatura (mobil)" value={pageSize} onChange={event => setPageSize(Number(event.target.value))}>{[20, 50, 100, 200].map(value => <option key={value} value={value}>{value} paket</option>)}</select></label>
        <button type="button" className="secondary invoice-reference-filter-reset" disabled={!hasInvoiceFilters} onClick={() => { setSearch(''); setSelectedPlatforms(null); setShipmentStatusFilter('ALL'); setCargoFilter('ALL'); setInvoiceStatusFilter('ALL'); setInvoiceActionFilter('ALL'); setDateFrom(''); setDateTo(''); setColumnFilterOpen(null) }}>Filtreleri Temizle</button>
      </MobileFilterShell>
    </div>
    <section className="invoice-reference-workspace">
      <header><div><h2>{activeTabLabel}</h2><p>Filtreleme sonuçları: {pageData?.totalCount ?? 0} paket</p></div><label className="invoice-reference-page-size">Sayfa Başına<select aria-label="Sayfa başına fatura" value={pageSize} onChange={event => setPageSize(Number(event.target.value))}>{[20, 50, 100, 200].map(value => <option key={value} value={value}>{value}</option>)}</select></label></header>
      {selectionMode && <div className="orders-selection-bar invoice-selection-bar" role="region" aria-label="Seçim modu"><button type="button" className="orders-selection-cancel" onClick={exitSelectionMode}>İptal</button><strong>{selectedItems.length} sipariş seçildi</strong><button type="button" className="orders-selection-all" onClick={() => setSelectedPackages(allVisibleSelected ? new Set() : new Set(pageItems.map(item => item.packageId)))} disabled={!pageItems.length}>{allVisibleSelected ? 'Seçimi Kaldır' : 'Tümünü Seç'}</button></div>}
      {selectedItems.length > 0 && <div className="invoice-reference-bulk-toolbar" role="status"><span>{selectedItems.length} sipariş seçildi</span>{selectedCreatableItems.length > 0 && <button type="button" onClick={() => openInvoicePreview(selectedCreatableItems)}><UiIcon name="download" size={16} />Toplu fatura oluştur ({selectedCreatableItems.length})</button>}{selectedShopifyItems.length > 0 && <button type="button" className={selectedCreatableItems.length ? 'secondary' : undefined} onClick={() => { setManualStatusBatch(selectedShopifyItems); setSelectedPackages(current => new Set([...current].filter(id => !selectedShopifyItems.some(item => item.packageId === id)))) }}><UiIcon name="edit" size={16} /> Shopify faturalarını güncelle ({selectedShopifyItems.length})</button>}<button type="button" className="secondary" onClick={() => visibility.mutate({ packageIds: selectedItems.map(item => item.packageId), hidden: tab !== 'HIDDEN' })} disabled={visibility.isPending}><UiIcon name={tab === 'HIDDEN' ? 'eye' : 'eyeOff'} size={16} />{tab === 'HIDDEN' ? 'Görünür yap' : 'Siparişleri gizle'}</button><button type="button" className="secondary" onClick={() => setSelectedPackages(new Set())}>Seçimi temizle</button></div>}
      {query.isLoading ? <div className="invoice-reference-state"><Busy /></div> : query.isError ? <div className="invoice-reference-state"><ErrorBox error={query.error} onRetry={() => void query.refetch()} /></div> : <><div className="invoice-reference-table" role="table">
        <div className="invoice-reference-head" role="row">
          <div className="invoice-reference-platform-heading"><label className="invoice-reference-select-all"><input type="checkbox" aria-label="Bu sayfadaki tüm siparişleri seç" checked={allVisibleSelected} disabled={!pageItems.length} onChange={() => setSelectedPackages(allVisibleSelected ? new Set() : new Set(pageItems.map(item => item.packageId)))} /><span className="sr-only">Tüm siparişleri seç</span></label><strong>Sipariş Bilgileri</strong><PlatformMultiSelect compact label="Platform filtresi" options={platformOptions} selectedCodes={selectedPlatforms} onChange={setSelectedPlatforms} /></div>
          <strong>Alıcı</strong>
          <div className={`order-column-filter${cargoFilter !== 'ALL' ? ' has-filter' : ''}`}><span className="order-column-filter-label">Kargo</span><button type="button" className="order-column-filter-trigger" aria-label="Kargo filtresini aç" aria-expanded={columnFilterOpen === 'cargo'} aria-controls={columnFilterOpen === 'cargo' ? 'invoices-cargo-filter' : undefined} onClick={() => setColumnFilterOpen(current => current === 'cargo' ? null : 'cargo')}><svg className="order-filter-funnel" viewBox="0 0 24 24" aria-hidden="true"><path d="M4 5h16l-6 7.2V18l-4 1v-6.8L4 5z" /></svg></button>{columnFilterOpen === 'cargo' && <div id="invoices-cargo-filter" className="order-column-filter-popover" role="dialog" aria-label="Kargo filtreleri"><ColumnFilterChoices label="Kargo Firması" value={cargoFilter} options={[['ALL', 'Tüm Kargolar'], ...cargoOptions.map(value => [value, value] as const)]} onChange={value => { setCargoFilter(value); setColumnFilterOpen(null) }} />{cargoFilter !== 'ALL' && <button type="button" className="order-column-filter-reset" onClick={() => { setCargoFilter('ALL'); setColumnFilterOpen(null) }}>Filtreyi Temizle</button>}</div>}</div>
          <div className={`order-column-filter${shipmentStatusFilter !== 'ALL' ? ' has-filter' : ''}`}><span className="order-column-filter-label">Sipariş durumu</span><button type="button" className="order-column-filter-trigger" aria-label="Sipariş durumu filtresini aç" aria-expanded={columnFilterOpen === 'shipment'} aria-controls={columnFilterOpen === 'shipment' ? 'invoices-shipment-filter' : undefined} onClick={() => setColumnFilterOpen(current => current === 'shipment' ? null : 'shipment')}><svg className="order-filter-funnel" viewBox="0 0 24 24" aria-hidden="true"><path d="M4 5h16l-6 7.2V18l-4 1v-6.8L4 5z" /></svg></button>{columnFilterOpen === 'shipment' && <div id="invoices-shipment-filter" className="order-column-filter-popover" role="dialog" aria-label="Sipariş durumu filtreleri"><ColumnFilterChoices label="Sipariş Durumu" value={shipmentStatusFilter} options={[['ALL', 'Tüm Sipariş Durumları'], ...shipmentStatusOptions.map(value => [value, statusLabel(value)] as const)]} onChange={value => { setShipmentStatusFilter(value); setColumnFilterOpen(null) }} />{shipmentStatusFilter !== 'ALL' && <button type="button" className="order-column-filter-reset" onClick={() => { setShipmentStatusFilter('ALL'); setColumnFilterOpen(null) }}>Filtreyi Temizle</button>}</div>}</div>
          <div className={`order-column-filter${invoiceStatusFilter !== 'ALL' ? ' has-filter' : ''}`}><span className="order-column-filter-label">Fatura durumu</span><button type="button" className="order-column-filter-trigger" aria-label="Fatura durumu filtresini aç" aria-expanded={columnFilterOpen === 'invoice'} aria-controls={columnFilterOpen === 'invoice' ? 'invoices-invoice-filter' : undefined} onClick={() => setColumnFilterOpen(current => current === 'invoice' ? null : 'invoice')}><svg className="order-filter-funnel" viewBox="0 0 24 24" aria-hidden="true"><path d="M4 5h16l-6 7.2V18l-4 1v-6.8L4 5z" /></svg></button>{columnFilterOpen === 'invoice' && <div id="invoices-invoice-filter" className="order-column-filter-popover invoice-filter-popover" role="dialog" aria-label="Fatura durumu filtreleri"><ColumnFilterChoices label="Fatura Durumu" value={invoiceStatusFilter} options={[['ALL', 'Tüm Fatura Durumları'], ...invoiceStatusOptions]} onChange={value => { setInvoiceStatusFilter(value); setColumnFilterOpen(null) }} />{invoiceStatusFilter !== 'ALL' && <button type="button" className="order-column-filter-reset" onClick={() => { setInvoiceStatusFilter('ALL'); setColumnFilterOpen(null) }}>Filtreyi Temizle</button>}</div>}</div>
          <strong>Tutar</strong>
          <div className={`order-column-filter${invoiceActionFilter !== 'ALL' ? ' has-filter' : ''}`}><span className="order-column-filter-label">İşlemler</span><button type="button" className="order-column-filter-trigger" aria-label="Fatura oluşturma filtresini aç" aria-expanded={columnFilterOpen === 'action'} aria-controls={columnFilterOpen === 'action' ? 'invoices-action-filter' : undefined} onClick={() => setColumnFilterOpen(current => current === 'action' ? null : 'action')}><svg className="order-filter-funnel" viewBox="0 0 24 24" aria-hidden="true"><path d="M4 5h16l-6 7.2V18l-4 1v-6.8L4 5z" /></svg></button>{columnFilterOpen === 'action' && <div id="invoices-action-filter" className="order-column-filter-popover invoice-filter-popover" role="dialog" aria-label="Fatura oluşturma uygunluğu filtreleri"><ColumnFilterChoices label="Oluşturma Durumu" value={invoiceActionFilter} options={[['ALL', 'Tüm İşlem Durumları'], ['CREATABLE', 'Fatura Oluşturulabilir'], ['NOT_CREATABLE', 'Fatura Oluşturulamaz']]} onChange={value => { setInvoiceActionFilter(value as typeof invoiceActionFilter); setColumnFilterOpen(null) }} />{invoiceActionFilter !== 'ALL' && <button type="button" className="order-column-filter-reset" onClick={() => { setInvoiceActionFilter('ALL'); setColumnFilterOpen(null) }}>Filtreyi Temizle</button>}</div>}</div>
        </div>
        {/* eslint-disable-next-line jsx-a11y/click-events-have-key-events, jsx-a11y/no-noninteractive-element-to-interactive-role -- phone tap target that selects the row; its checkbox and buttons are the keyboard controls */}
        {!pageItems.length && <div className="invoice-reference-state" role="row"><div className="empty"><span className="empty-icon" aria-hidden="true"><UiIcon name="box" /></span><strong>Kayıt yok</strong><p>Seçili fatura sekmesi ve filtrelerle eşleşen paket bulunamadı.</p></div></div>}{pageItems.map(item => { const isPicked = selectedPackages.has(item.packageId); const isPhone = () => window.matchMedia('(max-width: 760px)').matches; return <article className={`invoice-reference-row ${item.isDueSoon ? 'due-soon' : ''}${isPicked ? ' is-selected' : ''}`} key={item.packageId} role="row" onClick={event => { if (!isPhone()) return; if ((event.target as Element).closest('button, a, input, select, textarea, label, .invoice-order-number')) return; if (selectionMode) return; setSelectedItem(item) }}>
          <div className="invoice-reference-order"><input className="invoice-reference-select" type="checkbox" aria-label={`${item.platformDisplayName} ${displayInvoiceOrderNumber(item.orderNumber)} siparişini seç`} checked={selectedPackages.has(item.packageId)} onChange={() => setSelectedPackages(current => { const next = new Set(current); if (next.has(item.packageId)) next.delete(item.packageId); else next.add(item.packageId); return next })} /><div className="order-reference-platform"><PlatformMark code={item.platformCode} name={item.platformDisplayName} /></div><div><InvoiceOrderNumber item={item} /><small>{formatIstanbul(item.orderedAt, { day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit' })}{item.invoiceNumber?.trim() ? ` · ${item.invoiceNumber}` : ''}</small></div></div>
          <div className="invoice-reference-buyer"><strong>{invoiceCustomerDisplayName(item)}</strong><InvoiceProductSummary item={item} /></div>
          <div className="invoice-reference-products"><div className="cargo-provider-display invoice-cargo-provider"><CargoProviderIcon value={item.cargoProviderName ?? 'Kargo bilgisi yok'} fallbackText /></div><small>{item.cargoTrackingNumber ?? 'Takip numarası yok'}</small></div>
          <div className="invoice-reference-shipment">{(() => { const kind = badgeGlyphKind(item.shipmentStatus, 'neutral'); return <StatusRing kind={kind} progress={orderStatusProgress(kind)} label={statusLabel(item.shipmentStatus)} detail={orderRingDetail(item, kind)} /> })()}</div>
          <div className="invoice-reference-status">{(() => { const invoiceState = invoiceWorkspaceStatus(item); const failed = ['FATURA_REDDEDILDI', 'FATURA_PLATFORMA_AKTARILMADI'].includes(invoiceState.value); const due = item.invoiceDueAt ? new Date(item.invoiceDueAt) : null; return <StatusRing kind={invoiceGlyphKind(invoiceState.value)} progress={invoiceStatusProgress(invoiceState.value)} label={invoiceStatusLabel(invoiceState.value)} title={failed ? invoiceFailureReason(item.invoiceErrorCode ?? null) : undefined} detail={invoiceRingDetail(item, invoiceGlyphKind(invoiceState.value), failed)} detailClassName={failed ? 'is-danger' : due && due.getTime() - Date.now() <= 86_400_000 ? 'deadline critical' : undefined} /> })()}{item.marketplaceInvoiceReadErrorCode && <small className="invoice-marketplace-read-error" title={item.marketplaceInvoiceReadErrorSummary ?? undefined}>{item.marketplaceInvoiceReadErrorCode === 'REMOTE_PACKAGE_NOT_FOUND' ? 'Paket bilgisi pazaryerinden alınamadı; kayıtlı fatura durumu gösteriliyor.' : 'Pazaryeri fatura durumu yenilenemedi.'}</small>}</div>
          <div className="invoice-reference-amount"><strong>{formatMoney(item.amount, item.currency)}</strong><small>{item.isDueSoon ? 'Öncelikli Takip' : 'Sipariş Toplamı'}</small></div>
          <div className="invoice-reference-actions">
            <InvoiceReferenceActionsMenu
              item={item}
              canCreateInvoice={!item.manualInvoiceDocumentId && (item.platformCode === 'SHOPIFY'
                ? isInvoiceCreationAvailable(item, Boolean(providerForItem(item)?.hasCredential)) && (!item.invoiceId || item.canCreateInvoice)
                : !item.invoiceId && isInvoiceCreationActionVisible(item))}
              canSyncOrder={Boolean(item.marketplaceConnectionId && item.externalOrderId?.trim() && connections.data?.items.some(connection => connection.id === item.marketplaceConnectionId && ['ACTIVE', 'VERIFIED'].includes(connection.status)))}
              deliveryPending={manualInvoiceDelivery.isPending}
              hidden={tab === 'HIDDEN'}
              onCreateInvoice={() => openInvoicePreview([item])}
              onSyncOrder={() => orderStatusSync.mutate(item)}
              onToggleHidden={() => visibility.mutate({ packageIds: [item.packageId], hidden: tab !== 'HIDDEN' })}
              onConfirmDelivered={() => setManualConfirmation({ kind: 'delivered', item })}
              onReplaceCancelledInvoice={() => setManualConfirmation({ kind: 'replace', item })}
              onConfirmNoRemoteInvoice={() => setManualConfirmation({ kind: 'noRemote', item })}
              onAddManualInvoice={() => setManualUploadItem(item)}
              onSendManualInvoice={() => manualInvoiceDelivery.mutate(item)}
            />
            {platformInvoiceLinkUnavailable(item) && <small className="invoice-marketplace-note">Platformda fatura var</small>}
            <div className="invoice-reference-action-footer"><button type="button" className="invoice-reference-details-trigger" onClick={() => setSelectedItem(item)}>Detayları Aç <UiIcon name="externalLink" /></button></div>
          </div>
        </article> })}
      </div>{pageItems.length > 0 && <nav className="order-pagination" aria-label="Fatura sayfaları"><span>Toplam {(pageData?.totalCount ?? 0).toLocaleString('tr-TR')} adet</span><InvoicePager page={currentPage} totalPages={totalPages} onChange={setPageNumber} /></nav>}</>}
    </section>
    {selectedItem && <div className="invoice-detail-backdrop" role="presentation" onMouseDown={() => setSelectedItem(null)}><DialogSurface as="section" onClose={() => setSelectedItem(null)} className="workspace-modal invoice-detail-modal" aria-labelledby="invoice-detail-title" onMouseDown={event => event.stopPropagation()}>
      <header className="invoice-detail-header"><div className="invoice-detail-identity">{platformLogoSource(selectedItem.platformCode) && <img className="invoice-detail-title-logo" src={platformLogoSource(selectedItem.platformCode)!} alt="" aria-hidden="true" />}<div><h2 id="invoice-detail-title"><InvoiceOrderNumber item={selectedItem} /></h2><p className="invoice-detail-meta"><span><UiIcon name="users" size={14} />{invoiceCustomerDisplayName(selectedItem)}</span><span><UiIcon name="calendar" size={14} />{formatIstanbul(selectedItem.orderedAt, { day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit' })}</span></p></div></div><div className="invoice-detail-header-actions"><button type="button" className="modal-close" onClick={() => setSelectedItem(null)} aria-label="Detay panelini kapat"><UiIcon name="close" /></button></div></header>
      <div className="invoice-detail-body"><section className="invoice-detail-summary"><div><small>Sipariş Durumu</small><Badge value={selectedItem.shipmentStatus} /></div><div className="invoice-detail-invoice-status"><small>Fatura Durumu</small><InvoiceStatusBadge icon={<InvoiceGlyph kind={invoiceGlyphKind(selectedItem.invoiceStatus)} />} status={selectedItem.invoiceStatus} />{selectedItem.marketplaceInvoiceReadErrorSummary && <small className="invoice-detail-error-reason">Pazaryeri fatura durumu güncellenemedi: {selectedItem.marketplaceInvoiceReadErrorSummary}</small>}{selectedItem.marketplaceInvoiceReadErrorCode && <code className="invoice-detail-error-code">Pazaryeri kontrol hatası: {selectedItem.marketplaceInvoiceReadErrorCode}</code>}{selectedItem.invoiceErrorCode && <code className="invoice-detail-error-code">Hata kodu: {selectedItem.invoiceErrorCode}</code>}</div><div><small>Toplam</small><strong>{formatMoney(selectedItem.amount, selectedItem.currency)}</strong></div><div><small>Kargo / Takip</small><div className="cargo-provider-display invoice-detail-cargo"><CargoProviderIcon value={selectedItem.cargoProviderName ?? 'Kargo bilgisi yok'} fallbackText /></div><strong>{selectedItem.cargoTrackingNumber ?? 'Takip no yok'}</strong></div></section>
        <section className="invoice-detail-section invoice-summary-section"><div className="invoice-detail-section-heading"><div><h3>Fatura Bilgileri</h3><p className="invoice-detail-muted">Mali fatura, pazaryerindeki fatura ve teslim kanıtı ayrı izlenir.</p></div></div><div className="invoice-detail-summary-facts">{selectedItem.invoiceNumber?.trim() && <div><small>Mali Fatura Numarası</small><strong>{selectedItem.invoiceNumber}</strong></div>}<div><small>Mali Fatura</small><Badge value={selectedItem.fiscalStatus ?? 'NONE'} label={statusLabel(selectedItem.fiscalStatus ?? 'NONE')} /></div><div><small>Pazaryerindeki Fatura</small><Badge value={selectedItem.platformInvoiceStatus ?? 'UNKNOWN'} label={statusLabel(selectedItem.platformInvoiceStatus ?? 'UNKNOWN')} tone={selectedItem.platformInvoiceStatus === 'INVOICED' ? 'good' : selectedItem.platformInvoiceStatus === 'UNKNOWN' ? 'warn' : 'neutral'} /></div><div><small>İletim Kanıtı</small><Badge value={selectedItem.deliveryStatus ?? selectedItem.invoiceDeliveryStatus ?? 'NOT_SENT'} tone={invoiceDeliveryTone(selectedItem.deliveryStatus ?? selectedItem.invoiceDeliveryStatus)} label={invoiceDeliveryLabel(selectedItem.deliveryStatus ?? selectedItem.invoiceDeliveryStatus)} />{selectedItem.invoiceDeliveryReference && <small>Referans: {selectedItem.invoiceDeliveryReference}</small>}</div><div><small>Son Başarılı Kontrol</small><strong>{selectedItem.lastSuccessfulCheckAt ? formatDateTime(selectedItem.lastSuccessfulCheckAt) : 'Henüz başarılı kontrol yok'}</strong></div></div>{platformInvoiceLinkUnavailable(selectedItem) && <p className="invoice-detail-muted">Pazaryerinde fatura var; pazaryeri fatura bağlantısını paylaşmıyor.</p>}{isInvoiceFailureStatus(selectedItem.invoiceStatus) && <div className="invoice-detail-callout is-danger" role="alert"><UiIcon name="alert" /><span><strong>{invoiceFailureReason(selectedItem.invoiceErrorCode)}</strong> {invoiceFailureGuidance(selectedItem.invoiceErrorCode)}</span></div>}{(invoiceReferenceMarketplaceHref(selectedItem) || selectedItem.manualInvoiceDocumentUrl || (selectedItem.invoiceId && selectedItem.invoiceDocumentAvailable)) && <div className="invoice-detail-links">{invoiceReferenceMarketplaceHref(selectedItem) && <a className="invoice-document-link" href={invoiceReferenceMarketplaceHref(selectedItem)!} target="_blank" rel="noreferrer"><UiIcon name="externalLink" />Pazaryerindeki güncel fatura bağlantısını aç</a>}{selectedItem.manualInvoiceDocumentUrl && <a className="invoice-document-link" href={selectedItem.manualInvoiceDocumentUrl} target="_blank" rel="noreferrer"><UiIcon name="externalLink" />Manuel fatura belgesini aç</a>}{selectedItem.invoiceId && selectedItem.invoiceDocumentAvailable && <a className="invoice-document-link" href={`/api/v1/invoices/${selectedItem.invoiceId}/documents/latest/content`} target="_blank" rel="noreferrer"><UiIcon name="externalLink" />Mali sağlayıcı yerel PDF’sini aç</a>}</div>}{selectedItem.manualInvoiceDocumentId && <div className="invoice-detail-summary-facts"><div><small>Manuel platform belgesi</small><Badge value={manualInvoiceDeliveryLabel(selectedItem.manualInvoiceDeliveryStatus)} tone={selectedItem.manualInvoiceDeliveryStatus === 'CONFIRMED' ? 'good' : 'neutral'} />{selectedItem.manualInvoiceDeliveryErrorCode && <small>{selectedItem.manualInvoiceDeliveryErrorCode}</small>}</div>{selectedItem.manualInvoiceNumber && <div><small>Belge numarası</small><strong>{selectedItem.manualInvoiceNumber}</strong></div>}</div>}<div className="invoice-detail-document-actions">{selectedItem.manualInvoiceDocumentId && selectedItem.manualInvoiceDeliveryStatus !== 'CONFIRMED' && <button type="button" onClick={() => manualInvoiceDelivery.mutate(selectedItem)} disabled={manualInvoiceDelivery.isPending || !selectedItem.manualInvoiceIssuedAt}>{manualInvoiceDelivery.isPending ? 'Kuyruğa alınıyor…' : manualInvoiceDeliveryActionLabel(selectedItem.manualInvoiceDeliveryStatus)}</button>}{selectedItem.platformCode === 'SHOPIFY' && <button type="button" className="secondary" disabled={!selectedItem.invoiceCreationEnabled && !selectedItem.invoiceId} title={!selectedItem.invoiceCreationEnabled && !selectedItem.invoiceId ? invoiceCreationDisabledHelp : undefined} onClick={() => { setShopifyStatusItem(selectedItem); setSelectedItem(null) }}>Fatura Durumunu Değiştir</button>}</div></section>
        {Boolean(selectedItem.previousPlatformInvoiceReferences?.length) && <section className="invoice-detail-section"><details className="invoice-platform-reference-history"><summary>Önceki pazaryeri fatura referansları ({selectedItem.previousPlatformInvoiceReferences!.length})</summary>{selectedItem.previousPlatformInvoiceReferences!.map((reference, index) => <div className="invoice-detail-document-actions" key={`${reference.changedAt}-${index}`}><span>{reference.invoiceNumber ? `Fatura ${reference.invoiceNumber} · ` : ''}{formatDateTime(reference.changedAt)} · {reference.changeReason}</span>{safeInvoiceReferenceUrl(reference.invoiceUrl) && <a className="invoice-document-link" href={safeInvoiceReferenceUrl(reference.invoiceUrl)!} target="_blank" rel="noreferrer"><UiIcon name="externalLink" />Geçmiş bağlantıyı aç</a>}</div>)}</details></section>}
        <section className="invoice-detail-section"><div className="invoice-detail-section-heading"><h3>Ürünler</h3><span>{selectedItem.lines?.length ?? selectedItem.productCount} kalem</span></div><div className="invoice-detail-lines">{(selectedItem.lines ?? []).map((line, index) => <article className="invoice-detail-line" key={`${line.sku}-${index}`}>{line.imageUrl ? <InvoiceDetailLineImage url={line.imageUrl} title={line.description} /> : <span className="invoice-detail-line-media"><UiIcon name="image" /></span>}<div><strong>{line.description}</strong><small>SKU: {line.sku || '—'} · Barkod: {line.barcode || '—'}</small><small>{line.quantity} adet · Birim {formatMoney(line.unitPrice, selectedItem.currency)} · KDV %{line.vatRate}</small></div></article>)}{!selectedItem.lines?.length && <p className="invoice-detail-muted">Ürün satırı detayına ulaşılamadı; sipariş kaydı korunuyor.</p>}</div></section>
        {addressLines(selectedItem.shipmentAddressJson).length || addressLines(selectedItem.invoiceAddressJson).length ? <section className="invoice-detail-addresses">{addressLines(selectedItem.shipmentAddressJson).length > 0 && <article><h3>Teslimat Adresi</h3>{addressLines(selectedItem.shipmentAddressJson).map(line => <span key={`${line.label}:${line.value}`}><strong>{line.label}:</strong> {line.value}</span>)}</article>}{addressLines(selectedItem.invoiceAddressJson).length > 0 && <article><h3>Fatura Adresi</h3>{addressLines(selectedItem.invoiceAddressJson).map(line => <span key={`${line.label}:${line.value}`}><strong>{line.label}:</strong> {line.value}</span>)}</article>}</section> : <p className="invoice-detail-address-note">Pazaryeri sipariş kaydında adres bilgisi bulunmuyor.</p>}
      </div><footer className="invoice-detail-footer">{selectedItem.allowedActions?.includes('CHECK_PLATFORM') && <button type="button" className="secondary invoice-detail-platform-refresh" onClick={() => invoiceReconcile.mutate(selectedItem)} disabled={invoiceReconcile.isPending} title="Pazaryeri fatura bilgisini platformdan yeniden okur">{invoiceReconcile.isPending ? 'Güncelleniyor…' : 'Platformdan Bilgileri Güncelle'}</button>}<span className="invoice-detail-footer-gap" /><button type="button" className="secondary" onClick={() => setSelectedItem(null)}>Kapat</button><button type="button" className="secondary" onClick={() => visibility.mutate({ packageIds: [selectedItem.packageId], hidden: tab !== 'HIDDEN' })} disabled={visibility.isPending}>{tab === 'HIDDEN' ? 'Görünür Yap' : 'Siparişi Gizle'}</button>{selectedItem.invoiceId && <Link className="button-link invoice-detail-open-record" to={`/invoices/${selectedItem.invoiceId}`}>Fatura Kaydını Aç <UiIcon name="externalLink" /></Link>}</footer>
    </DialogSurface></div>}
    {!provider?.hasCredential && Boolean(pageData?.hasPendingMarketplaceInvoices) && <div className="unknown invoice-provider-notice"><strong>E-Faturam bağlantısı hazır değil</strong><p>Fatura oluşturmak için sipariş bağlantısıyla aynı ortamda (test veya canlı) etkin ve API bilgileri kayıtlı bir E-Faturam bağlantısı gerekir. Shopify için mali fatura oluşturma, sağlayıcı sözleşmesi doğrulanana kadar kapalıdır.</p><Link className="button-link" to="/integrations">Bağlantıyı yönet</Link></div>}
    {shopifyStatusItem && <ShopifyInvoiceStatusModal item={shopifyStatusItem} onClose={() => setShopifyStatusItem(null)} onFeedback={setMessage} />}
    {manualUploadItem && <InvoiceWorkspaceUploadModal item={manualUploadItem} onClose={() => setManualUploadItem(null)} onFeedback={setMessage} />}
    {manualStatusBatch && <InvoiceWorkspaceBulkStatusModal items={manualStatusBatch} onClose={() => setManualStatusBatch(null)} onFeedback={setMessage} />}
    {manualConfirmation && <InvoiceManualConfirmationModal kind={manualConfirmation.kind} item={manualConfirmation.item} onCancel={() => setManualConfirmation(null)} onConfirm={runManualConfirmation} />}
    {previewTargets && <InvoiceWorkspacePreviewModal targets={previewTargets} onClose={() => setPreviewTargets(null)} onNotice={(text, kind) => setMessage(text, kind)} onComplete={() => { void Promise.all([client.invalidateQueries({ queryKey: ['invoice-workspace'] }), client.invalidateQueries({ queryKey: ['invoice-workspace-summary'] }), client.invalidateQueries({ queryKey: ['invoice-workspace-page'] }), client.invalidateQueries({ queryKey: ['orders'] }), client.invalidateQueries({ queryKey: ['invoice'] })]) }} />}
  </section>
}


// Page numbers around the current page with previous / next arrows, plus a go-to-page field.
function invoicePagerPages(page: number, totalPages: number): Array<number | null> {
  if (totalPages <= 7) return Array.from({ length: totalPages }, (_, index) => index + 1)
  const pages = new Set([1, totalPages, page - 1, page, page + 1].filter(value => value >= 1 && value <= totalPages))
  const sorted = [...pages].sort((left, right) => left - right)
  return sorted.flatMap((value, index) => index > 0 && value - sorted[index - 1] > 1 ? [null, value] : [value])
}

function InvoicePager({ page, totalPages, onChange }: { page: number; totalPages: number; onChange: (page: number) => void }) {
  const [draft, setDraft] = useState('')
  const safeTotal = Math.max(1, totalPages)
  function go(next: number) { onChange(Math.min(safeTotal, Math.max(1, next))) }
  return <div className="invoice-pager">
    <div className="invoice-pager-pages">
      <button type="button" aria-label="Önceki sayfa" disabled={page <= 1} onClick={() => go(page - 1)}><UiIcon name="chevronLeft" /></button>
      {invoicePagerPages(page, safeTotal).map((value, index) => value === null ? <span key={`gap-${index}`} className="invoice-pager-gap">…</span> : <button type="button" key={value} className={value === page ? 'is-on' : undefined} aria-current={value === page ? 'page' : undefined} onClick={() => go(value)}>{value}</button>)}
      <button type="button" aria-label="Sonraki sayfa" disabled={page >= safeTotal} onClick={() => go(page + 1)}><UiIcon name="chevronRight" /></button>
    </div>
    <form className="invoice-pager-jump" onSubmit={event => { event.preventDefault(); const value = Number(draft); if (Number.isFinite(value) && value >= 1) go(value); setDraft('') }}>
      <label>Sayfaya Git<input type="number" inputMode="numeric" min={1} max={safeTotal} value={draft} placeholder={`1–${safeTotal}`} aria-label="Sayfa numarası" onChange={event => setDraft(event.target.value)} /></label>
      <button type="submit" className="secondary">Git</button>
    </form>
  </div>
}

function ColumnFilterChoices({ label, value, options, onChange }: { label: string; value: string; options: ReadonlyArray<readonly [string, string]>; onChange: (value: string) => void }) {
  return <div className="invoice-filter-choices" role="radiogroup" aria-label={label}>
    <span className="invoice-filter-choices-label">{label}</span>
    {options.map(([optionValue, optionLabel]) => <button type="button" role="radio" key={optionValue} aria-checked={value === optionValue} className={value === optionValue ? 'is-on' : undefined} onClick={() => onChange(optionValue)}><span className="invoice-filter-radio" aria-hidden="true" />{optionLabel}</button>)}
  </div>
}

const manualConfirmationCopy = {
  delivered: { title: 'Pazaryerinde Gördüm, Tamamla', confirm: 'Faturayı Tamamla', tone: 'primary' as const, note: 'Onaylarsanız fatura tamamlandı olarak işaretlenir ve tekrar gönderilmez.' },
  replace: { title: 'Portalda İptal Ettim, Yeni Fatura Kes', confirm: 'İptal Edildi, Yeni Fatura Kes', tone: 'warn' as const, note: 'Onaylarsanız bu fatura panelde iptal olarak kapanır ve aynı siparişe yeni fatura kesebilirsiniz. Portalda iptal edilmemiş bir fatura için bunu yapmayın.' },
  noRemote: { title: 'Sağlayıcıda Fatura Yok, Yeniden Gönder', confirm: 'Yeniden Gönderilebilir Yap', tone: 'danger' as const, note: 'Onaylarsanız bu fatura yeniden gönderilebilir hâle gelir. Portalda fatura varsa mükerrer fatura oluşur.' },
}

// The manual invoice confirmations, formerly browser dialogs: the order, the invoice and what the choice does.
function InvoiceManualConfirmationModal({ kind, item, onCancel, onConfirm }: { kind: 'delivered' | 'replace' | 'noRemote'; item: InvoiceWorkspace; onCancel: () => void; onConfirm: () => void }) {
  const copy = manualConfirmationCopy[kind]
  const orderNumber = displayInvoiceOrderNumber(item.orderNumber)
  const description = kind === 'delivered'
    ? `${orderNumber} siparişinde ${item.invoiceNumber ?? 'mevcut'} numaralı faturayı pazaryerinin satıcı panelinde gördünüz mü?`
    : kind === 'replace'
      ? `${orderNumber} için ${item.invoiceNumber ?? 'mevcut'} numaralı faturayı E-Faturam portalında iptal ettiniz mi?`
      : `${orderNumber} için E-Faturam portalında fatura olmadığını doğruladınız mı?`
  return <Modal open title={copy.title} description={description} className="invoice-confirm-modal" onClose={onCancel} footer={<><button type="button" className="secondary" onClick={onCancel}>Vazgeç</button><button type="button" className={copy.tone === 'danger' ? 'destructive' : 'primary'} onClick={onConfirm}>{copy.confirm}</button></>}>
    <div className="invoice-confirm-order"><PlatformMark code={item.platformCode} name={item.platformDisplayName} /><span><strong>{orderNumber}</strong><small>{invoiceCustomerDisplayName(item)}{item.invoiceNumber ? ` · ${item.invoiceNumber}` : ''}</small></span><InvoiceStatusBadge icon={<InvoiceGlyph kind={invoiceGlyphKind(item.invoiceStatus)} />} status={item.invoiceStatus} /></div>
    <p className={`invoice-confirm-note is-${copy.tone}`}><UiIcon name="alert" />{copy.note}</p>
  </Modal>
}

function ShopifyInvoiceStatusModal({ item, onClose, onFeedback }: { item: InvoiceWorkspace; onClose: () => void; onFeedback: (message: string, kind: InvoiceNoticeKind) => void }) {
  const client = useQueryClient()
  const [status, setStatus] = useState<'PENDING' | 'UPLOADED'>(item.invoiceStatus === 'FATURA_YUKLENDI' ? 'UPLOADED' : 'PENDING')
  const invoice = useQuery({ queryKey: ['invoice', item.invoiceId], queryFn: () => hubApi<InvoiceDetail>(`/invoices/${item.invoiceId}`), enabled: Boolean(item.invoiceId) })
  const hasUploadedDocument = item.invoiceDocumentAvailable || Boolean(invoice.data?.documents.length)
  const update = useMutation({
    mutationFn: () => setManualInvoiceStatus([item.packageId], status),
    onSuccess: async () => {
      const message = status === 'UPLOADED' ? 'Shopify fatura durumu “Faturası yüklendi” olarak kaydedildi.' : 'Shopify fatura durumu “Fatura bekliyor” olarak kaydedildi.'
      await Promise.all([client.invalidateQueries({ queryKey: ['invoice-workspace'] }), client.invalidateQueries({ queryKey: ['invoice-workspace-summary'] }), client.invalidateQueries({ queryKey: ['invoice-workspace-page'] }), client.invalidateQueries({ queryKey: ['orders'] }), client.invalidateQueries({ queryKey: ['invoice'] })])
      onFeedback(message, 'success')
      onClose()
    },
    onError: error => onFeedback(error instanceof Error ? error.message : 'Fatura durumu güncellenemedi.', 'error')
  })
  return <div className="workspace-modal-backdrop" role="presentation" onMouseDown={() => { if (!update.isPending) onClose() }}><DialogSurface as="section" onClose={() => { if (!update.isPending) onClose() }} className="workspace-modal shopify-invoice-status-modal" aria-labelledby="invoice-workspace-shopify-status-title" onMouseDown={event => event.stopPropagation()}><header><div><p className="eyebrow">SHOPIFY</p><h2 id="invoice-workspace-shopify-status-title">Fatura durumunu değiştir</h2><p>{displayInvoiceOrderNumber(item.orderNumber)} · Bu değişiklik yalnızca panel kaydını günceller.</p></div><button type="button" className="modal-close" onClick={onClose} disabled={update.isPending} aria-label="Pencereyi kapat"><UiIcon name="close" /></button></header><div className="shopify-invoice-status-form"><label><span>Yeni fatura durumu</span><select value={status} onChange={event => setStatus(event.target.value as 'PENDING' | 'UPLOADED')} disabled={update.isPending}><option value="PENDING">Fatura bekliyor</option><option value="UPLOADED">Faturası yüklendi</option></select></label><p className="notice" role={status === 'UPLOADED' && !hasUploadedDocument ? 'status' : undefined}>{status === 'UPLOADED' && !hasUploadedDocument ? 'Belge henüz yüklenmedi; bu işlem yalnızca panel durumunu günceller, Shopify’a fatura göndermez. Belge eklemek için sipariş detaylarında “Manuel fatura yükle”yi kullanın.' : 'Shopify’a fatura gönderilmez; yalnızca paneldeki durum güncellenir.'}</p><footer><button type="button" className="secondary" onClick={onClose} disabled={update.isPending}>Vazgeç</button><button type="button" onClick={() => update.mutate()} disabled={update.isPending}>{update.isPending ? 'Kaydediliyor…' : 'Durumu kaydet'}</button></footer></div></DialogSurface></div>
}

function InvoiceWorkspaceBulkStatusModal({ items, onClose, onFeedback }: { items: InvoiceWorkspace[]; onClose: () => void; onFeedback: (message: string, kind: InvoiceNoticeKind) => void }) {
  const client = useQueryClient()
  const [status, setStatus] = useState<'PENDING' | 'UPLOADED'>('PENDING')
  const update = useMutation({
    mutationFn: () => setManualInvoiceStatus(items.map(item => item.packageId), status),
    onSuccess: async result => {
      await Promise.all([client.invalidateQueries({ queryKey: ['invoice-workspace'] }), client.invalidateQueries({ queryKey: ['invoice-workspace-summary'] }), client.invalidateQueries({ queryKey: ['invoice-workspace-page'] }), client.invalidateQueries({ queryKey: ['orders'] }), client.invalidateQueries({ queryKey: ['invoice'] })])
      const label = status === 'UPLOADED' ? 'Faturası yüklendi' : 'Fatura bekliyor'
      onFeedback(`${result.updatedCount} siparişin fatura durumu “${label}” olarak güncellendi. Dosya yüklenmedi ve pazaryerine fatura gönderilmedi.`, 'success')
      onClose()
    },
    onError: error => onFeedback(error instanceof Error ? error.message : 'Fatura durumları güncellenemedi.', 'error')
  })
  return <div className="workspace-modal-backdrop" role="presentation" onMouseDown={() => { if (!update.isPending) onClose() }}><DialogSurface as="section" onClose={() => { if (!update.isPending) onClose() }} className="workspace-modal shopify-invoice-status-modal" aria-labelledby="invoice-workspace-bulk-status-title" onMouseDown={event => event.stopPropagation()}><header><div><p className="eyebrow">TOPLU FATURA DURUMU · SHOPIFY</p><h2 id="invoice-workspace-bulk-status-title">Seçilen Shopify faturalarını güncelle</h2><p>{items.length} Shopify siparişi · Fatura dosyası yüklenmez.</p></div><button type="button" className="modal-close" onClick={onClose} disabled={update.isPending} aria-label="Pencereyi kapat"><UiIcon name="close" /></button></header><div className="shopify-invoice-status-form"><label><span>Yeni fatura durumu</span><select aria-label="Yeni fatura durumu" value={status} onChange={event => setStatus(event.target.value as 'PENDING' | 'UPLOADED')} disabled={update.isPending}><option value="PENDING">Fatura bekliyor</option><option value="UPLOADED">Faturası yüklendi</option></select></label><p className="notice">Durum yalnızca Shopify siparişlerinin panel kaydında güncellenir. Dosya yüklenmez ve Shopify’a fatura gönderilmez.</p><ul className="invoice-bulk-status-orders">{items.map(item => <li key={item.packageId}><span className="invoice-bulk-status-platform">Shopify</span><span className="invoice-bulk-status-order">{displayInvoiceOrderNumber(item.orderNumber)}<small>{item.customerName} · {formatDate(item.orderedAt)}</small></span><span className="invoice-bulk-status-value">{invoiceStatusLabel(invoiceWorkspaceStatus(item).value)}</span></li>)}</ul><footer><button type="button" className="secondary" onClick={onClose} disabled={update.isPending}>Vazgeç</button><button type="button" onClick={() => update.mutate()} disabled={update.isPending}>{update.isPending ? 'Güncelleniyor…' : `${items.length} Shopify siparişini güncelle`}</button></footer></div></DialogSurface></div>
}

function InvoiceWorkspaceUploadModal({ item, onClose, onFeedback }: { item: InvoiceWorkspace; onClose: () => void; onFeedback: (message: string, kind: InvoiceNoticeKind) => void }) {
  const client = useQueryClient()
  const [file, setFile] = useState<File | null>(null)
  const [invoiceUrl, setInvoiceUrl] = useState('')
  const [invoiceNumber, setInvoiceNumber] = useState(item.manualInvoiceNumber ?? '')
  const [issuedAt, setIssuedAt] = useState(toDateTimeLocal(item.manualInvoiceIssuedAt))
  const [message, setMessage] = useState('')
  const [dragging, setDragging] = useState(false)
  const upload = useMutation({
    mutationFn: async () => {
      await uploadManualInvoiceDocument(item, file, invoiceUrl, invoiceNumber, issuedAt)
    },
    onSuccess: async () => {
      onFeedback('Manuel fatura belgesi siparişe kaydedildi. Platforma iletim başlamadı; iletimi ayrı düğmeden başlatabilirsiniz.', 'success')
      await Promise.all([client.invalidateQueries({ queryKey: ['invoice-workspace'] }), client.invalidateQueries({ queryKey: ['invoice-workspace-summary'] }), client.invalidateQueries({ queryKey: ['invoice-workspace-page'] }), client.invalidateQueries({ queryKey: ['orders'] }), client.invalidateQueries({ queryKey: ['invoice'] })])
      onClose()
    },
    onError: error => { const errorMessage = error instanceof Error ? error.message : 'Manuel fatura belgesi kaydedilemedi.'; setMessage(errorMessage); onFeedback(errorMessage, 'error') }
  })
  function choose(selected: File | undefined) { if (selected) { setFile(selected); setInvoiceUrl(''); setMessage('') } }
  const hasOneSource = Boolean(file) !== Boolean(invoiceUrl.trim())
  return <div className="workspace-modal-backdrop" role="presentation" onMouseDown={() => { if (!upload.isPending) onClose() }}><DialogSurface as="section" onClose={() => { if (!upload.isPending) onClose() }} className="workspace-modal invoice-upload-modal invoice-workspace-upload-modal" aria-labelledby="invoice-workspace-upload-title" onMouseDown={event => event.stopPropagation()}>
    <header><div><p className="eyebrow">{item.platformDisplayName.toLocaleUpperCase('tr-TR')} · {displayInvoiceOrderNumber(item.orderNumber)}</p><h2 id="invoice-workspace-upload-title">Siparişe manuel fatura belgesi ekle</h2><p>Belge e-Faturam bağlantısı veya mali taslak gerektirmeden kaydedilir. Kaydetmek platforma gönderim başlatmaz.</p></div><button type="button" className="modal-close" onClick={onClose} disabled={upload.isPending} aria-label="Pencereyi kapat"><UiIcon name="close" /></button></header>
    <div className="invoice-upload-context"><div><small>Mali fatura kaydı</small><InvoiceStatusBadge icon={<InvoiceGlyph kind={invoiceGlyphKind(item.invoiceStatus)} />} status={item.invoiceStatus} /></div><div><small>Platform belgesi</small><Badge value={manualInvoiceDeliveryLabel(item.manualInvoiceDeliveryStatus)} tone={item.manualInvoiceDeliveryStatus === 'CONFIRMED' ? 'good' : 'neutral'} /></div></div>
    {item.manualInvoiceDocumentUrl && <a className="invoice-document-link" href={item.manualInvoiceDocumentUrl} target="_blank" rel="noreferrer">Mevcut manuel belgeyi aç <UiIcon name="externalLink" /></a>}
    {item.invoiceId && item.invoiceDocumentAvailable && <a className="invoice-document-link" href={`/api/v1/invoices/${item.invoiceId}/documents/latest/content`} target="_blank" rel="noreferrer">Mali sağlayıcı belgesini aç <UiIcon name="externalLink" /></a>}
    <label className="invoice-upload-field"><span>Fatura numarası</span><input value={invoiceNumber} onChange={event => setInvoiceNumber(event.target.value)} maxLength={64} placeholder="Fatura numarası" /></label>
    <label className="invoice-upload-field"><span>Fatura tarihi</span><input type="datetime-local" value={issuedAt} onChange={event => setIssuedAt(event.target.value)} /></label>
    <label className="invoice-upload-field"><span>Mevcut HTTPS fatura bağlantısı</span><input type="url" value={invoiceUrl} onChange={event => { setInvoiceUrl(event.target.value); if (event.target.value) setFile(null) }} placeholder="https://…" /></label>
    <div className="invoice-upload-or">veya PDF dosyası</div>
    <label className={`invoice-dropzone${dragging ? ' dragging' : ''}`} onDragEnter={event => { event.preventDefault(); setDragging(true) }} onDragOver={event => event.preventDefault()} onDragLeave={() => setDragging(false)} onDrop={event => { event.preventDefault(); setDragging(false); choose(event.dataTransfer.files[0]) }}><input type="file" accept=".pdf,application/pdf" onChange={event => choose(event.target.files?.[0])} /><span className="invoice-upload-icon" aria-hidden="true"><UiIcon name="upload" /></span><strong>{file ? file.name : 'PDF dosyasını seçin'}</strong><small>{file ? `${(file.size / 1024 / 1024).toFixed(2)} MB` : 'PDF · En fazla 10 MB'}</small><b>Dosya Seç</b></label>
    {item.manualInvoiceDocumentId && <p className="notice">Kaydedilen yeni belge siparişin güncel manuel sürümü olur. Eski sürüm ve gönderim geçmişi korunur.</p>}{message && <p className="error" role="alert">{message}</p>}
    <footer><button type="button" className="secondary" onClick={onClose} disabled={upload.isPending}>Vazgeç</button><button type="button" disabled={!hasOneSource || !issuedAt || upload.isPending} onClick={() => upload.mutate()}>{upload.isPending ? 'Kaydediliyor…' : 'Belgeyi kaydet'}</button></footer>
  </DialogSurface></div>
}

export function InvoiceDetailPage() {
  const { id = '' } = useParams(); const [searchParams] = useSearchParams(); const client = useQueryClient(); const [notice, setNoticeState] = useState(''); const [noticeKind, setNoticeKind] = useState<InvoiceNoticeKind>('info'); const [password, setPassword] = useState(''); const [confirmed, setConfirmed] = useState(false); const [uploadFile, setUploadFile] = useState<File | null>(null)
  function setNotice(value: string, kind: InvoiceNoticeKind = 'info') { setNoticeState(value); setNoticeKind(kind); if (value && kind !== 'pending') appendNotification(value, kind) }
  useEffect(() => {
    if (!notice) return
    const timeout = window.setTimeout(() => setNoticeState(''), noticeKind === 'info' ? 7000 : 5500)
    return () => window.clearTimeout(timeout)
  }, [notice, noticeKind])
  const query = useQuery({ queryKey: ['invoice', id], queryFn: () => hubApi<InvoiceDetail>(`/invoices/${id}`) })
  const operation = useMutation({
    mutationFn: async ({ action, invoice }: { action: string; invoice: InvoiceDetail }) => {
      if (action === 'VALIDATE') return hubApi<InvoiceDetail>(`/invoices/${id}/validate`, { method: 'POST', headers: { 'If-Match': `"v${invoice.version}"` } })
      if (action === 'RECONCILE') return hubApi(`/invoices/${id}/reconcile-jobs`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() } })
      const endpoint = action === 'SUBMIT' ? 'submit-jobs' : action === 'STAGE_CAPABILITY_PROBE' ? 'stage-capability-probe-jobs' : action === 'DELIVER' ? 'marketplace-delivery-jobs' : 'cancellation-jobs'
      return hubApi(`/invoices/${id}/${endpoint}`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey(), ...(action !== 'DELIVER' ? { 'If-Match': `"v${invoice.version}"` } : {}) }, ...(action === 'STAGE_CAPABILITY_PROBE' ? {} : { body: JSON.stringify({ password, confirmed }) }) })
    },
    onSuccess: (_value, variables) => { setNotice(variables.action === 'VALIDATE' ? 'Yerel doğrulama tamamlandı.' : 'İşlem sıraya alındı.', variables.action === 'VALIDATE' ? 'success' : 'info'); setPassword(''); setConfirmed(false); void client.invalidateQueries({ queryKey: ['invoice', id] }) },
    onError: error => setNotice(error instanceof Error ? error.message : 'İşlem başarısız.', 'error')
  })
  const upload = useMutation({
    mutationFn: (file: File) => { const form = new FormData(); form.append('file', file); return hubApi<{ duplicate: boolean }>(`/invoices/${id}/documents/manual`, { method: 'POST', headers: { 'Idempotency-Key': `invoice-document:${id}:${file.name}:${file.size}:${file.lastModified}` }, body: form }) },
    onSuccess: async result => { setNotice(result.duplicate ? 'Bu fatura belgesi zaten güvenli arşivde bulunuyor.' : 'Fatura belgesi güvenli özel arşive yüklendi. Belge henüz Trendyol’a veya E‑Faturam’a iletilmedi.', 'success'); setUploadFile(null); await client.invalidateQueries({ queryKey: ['invoice', id] }) },
    onError: error => setNotice(error instanceof Error ? error.message : 'Fatura belgesi yüklenemedi.', 'error')
  })
  if (query.isLoading) return <section className="content"><Busy /></section>; if (query.isError || !query.data) return <section className="content"><ErrorBox error={query.error} onRetry={() => void query.refetch()} /></section>; const invoice = query.data
  const protectedActions = invoice.requiresSensitiveConfirmation ? invoice.allowedActions.filter(action => action !== 'VALIDATE' && action !== 'RECONCILE') : []
  return <section className="content f3"><Link className="back" to="/invoices"><UiIcon name="arrowLeft" /> Faturalar</Link><div className="page-heading"><div><p className="eyebrow">Fatura detayı</p><h1>{invoice.orderNumber}</h1><p className="lede">{invoice.invoiceNumber?.trim() ? `${invoice.invoiceNumber} · ` : ''}{statusLabel(invoice.invoiceType)}</p></div><InvoiceStatusBadge icon={<InvoiceGlyph kind={invoiceGlyphKind(invoice.status)} />} status={invoice.status} /></div>
    {notice && <NoticeToast key={`${noticeKind}:${notice}`} kind={noticeKind} message={notice} onClose={() => setNoticeState('')} durationMs={noticeKind === 'info' ? 7000 : 5500} />}
    <div className="grid"><article><small>Ödenecek</small><strong>{formatMoney(invoice.payableTotal, invoice.currency)}</strong><p>Vergi: {invoice.taxTotal.toLocaleString('tr-TR')}</p></article><article><small>ETTN / UUID</small><strong>{invoice.ettnUuid ?? 'Henüz atanmadı'}</strong><p>{invoice.sequencePurpose} · {invoice.issuedAt ? formatDateTime(invoice.issuedAt) : 'Henüz düzenlenmedi'}</p></article><article><small>Son hata</small><strong>{invoice.lastErrorCode ?? 'Yok'}</strong><p>Bilinmeyen sonuç otomatik başarı sayılmaz.</p></article></div>
     <div className="panel"><h2>Satırlar</h2><div className="data-table compact" role="table">{invoice.lines.map(line => <div role="row" key={line.id}><span><strong>{line.description}</strong><small><code className="technical-text sku-value">{line.sku ?? 'SKU yok'}</code> · indirim {line.discountAmount.toLocaleString('tr-TR')}</small></span><span>{line.quantity} {line.unit}</span><span>%{line.vatRate}</span><span>{formatMoney(line.lineTotal, invoice.currency)}</span></div>)}</div></div>
    <div className="split"><div className="panel"><h2>Fatura Sağlayıcısı Denemeleri</h2>{invoice.attempts.length ? <div className="card-list">{invoice.attempts.map(item => <div className="record-card" key={item.attemptNumber}><span><strong>Deneme #{item.attemptNumber}</strong><small>{item.errorCode ?? 'Hata yok'}</small></span><Badge value={item.outcome} /></div>)}</div> : <p>Henüz dış gönderim denemesi yok.</p>}</div><div className="panel"><h2>Trendyol teslimleri</h2><p className="invoice-detail-muted">Bu alan, faturanın Trendyol paketine gönderilip gönderilmediğini gösterir.</p>{invoice.deliveries.length ? <div className="card-list">{invoice.deliveries.map(item => <div className="record-card" key={item.id}><span><strong>{statusLabel(item.deliveryType)}</strong><small>{item.externalReference ?? item.errorCode ?? 'Referans bekleniyor'}</small></span><Badge value={item.status} /></div>)}</div> : <p>Platforma aktarım henüz başlatılmadı. Belge hazır olduğunda aşağıdaki işlemlerden fatura linkini iletebilirsiniz.</p>}</div></div>
    <div className={`panel invoice-documents-panel ${searchParams.get('upload') === '1' ? 'invoice-upload-highlight' : ''}`}><div className="invoice-panel-heading"><div><h2>Belgeler</h2><p className="invoice-detail-muted">Fatura sağlayıcısından gelen belge ve platforma iletilecek güvenli bağlantı.</p></div>{invoice.documents.length > 0 && <a className="invoice-document-link" href={`/api/v1/invoices/${invoice.id}/documents/latest/content`} target="_blank" rel="noreferrer">Fatura linkini aç <UiIcon name="externalLink" /></a>}</div>{invoice.documents.length ? <div className="card-list">{invoice.documents.map(document => <a className="record-card" key={document.id} href={`/api/v1/invoices/${invoice.id}/documents/${document.id}/content`} target="_blank" rel="noreferrer"><span><strong>{statusLabel(document.documentType)}</strong><small>SHA-256 {document.sha256.slice(0, 16)}…</small></span><span>Güvenli aç <UiIcon name="externalLink" /></span></a>)}</div> : <p>Henüz belge eklenmedi. Fatura sağlayıcısının belgesi alındığında ayrıca güvenli biçimde saklanır.</p>}<form className="invoice-document-upload" onSubmit={event => { event.preventDefault(); if (uploadFile) upload.mutate(uploadFile) }}><div><strong>Fatura belgesi yükle</strong><small>PDF, JPEG veya PNG · en fazla 10 MiB · sadece özel arşive kaydedilir</small></div><input type="file" accept="application/pdf,image/jpeg,image/png" onChange={event => setUploadFile(event.target.files?.[0] ?? null)} aria-label="Fatura belgesi seç" /><button type="submit" disabled={!uploadFile || upload.isPending}>{upload.isPending ? 'Yükleniyor…' : 'Belgeyi yükle'}</button></form></div>
    {protectedActions.length > 0 && <form className="panel form-panel" onSubmit={event => event.preventDefault()}><h2>Dış mali işlem onayı</h2><p>Parola yalnız canlı ortamdaki mali işlemler için yeniden sorulur. Sunucu, bağlantının ve yazma ayarlarının bu işleme izin verdiğini ayrıca denetler.</p><label>Hesap parolası<input type="password" autoComplete="current-password" value={password} onChange={event => setPassword(event.target.value)} /></label><label className="check"><input type="checkbox" checked={confirmed} onChange={event => setConfirmed(event.target.checked)} /> Bu dış mali işlemi açıkça onaylıyorum.</label></form>}
    <div className="panel"><h2>Kullanılabilir işlemler</h2>{invoice.allowedActions.length ? <><p className="invoice-detail-muted">Fatura belgesi hazırsa “Trendyol’a fatura linkini ilet” işlemi, güvenli bağlantıyı ilgili pakete gönderir.</p><div className="button-row">{invoice.allowedActions.map(action => <button key={action} type="button" onClick={() => operation.mutate({ action, invoice })} disabled={operation.isPending || (protectedActions.includes(action) && (!password || !confirmed))}>{actionLabel(action)}</button>)}</div></> : <div className="unknown"><strong>Dış işlemler kapalı</strong><p>Bağlantı, teknik doğrulama ve güvenli yazma koşulları işlem öncesi denetlenir.</p></div>}</div>
  </section>
}
