import type { ApiAnswer } from '../../../../shared/api/api-answer'
import { copyText } from '../../../../shared/copy-text'
import { observeUnitPriceAlignment } from '../../unit-price-alignment'
import { Fragment, useLayoutEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { Link, useNavigate } from 'react-router'
import { hubApi } from '../../../../shared/api'
import { cargoCarrier, cargoLabel, DialogSurface } from '../../../../shared/components'
import { safeExternalUrl } from '../../../../shared/safe-url'
import { useMediaQuery } from '../../../../shared/hooks/useMediaQuery'
import { useProductLines } from '../../product-lines'
import { handSetInvoiceStatus } from '../../../invoicing/manual-invoice-status'
import { ShopifyInvoiceStatusDialog } from '../../../invoicing/ShopifyInvoiceStatusDialog'
import { invoiceStatusDisplayLabel } from '../../../../shared/status-labels'
import { RrAnchor, RrBadge, RrIcon, RrRing, RrThumb, RrWordmark, titleTr, type RingTone } from '../../../../shared/ui/rr-components'
import { useScrollLock } from '../../../../shared/hooks/useScrollLock'
import { PlatformSquareMark } from '../../../../shared/platform-square-mark'
import { appendNotification, normalizeNotificationKind } from '../../../../shared/notifications'
import { canTakeOrderIntoWork, orderStatusLabel } from '../../order-status-tabs'
import { printedShippingLabelKey, type ShippingLabelFormat, type ShippingLabelSettings } from '../../../shipping'
import { productImageFallbackUrls as createProductImageFallbackUrls } from '../../product-image-lookups'
import { formatMoney } from '../../../../shared/format-money'
import { formatPackageLabel } from '../../../../shared/package-label'
import { deliveryDueLine, deliveryText, dueLineTitle, orderRingDetail, undeliveredReturn } from '../order-delivery'
import { BuyerTypeTag, OrderRowMarkIcons } from '../order-row-marks'
import { ExchangeResendTag, exchangeNoPackageNote, exchangeResendStatus } from '../exchange-resend-tag'
import { ExchangeResendShipDialog } from '../ExchangeResendShipDialog'
import { ManualResendShipDialog, ManualResendTag } from '../ManualResendShipDialog'
import { manualResendStatus } from '../manual-resends'
import { useLacksElevatedRole } from '../../../elevated-role'
import { isPartlyCancelled, lineCancel, partialQuantityText, remainingText, rowLines } from '../order-line-cancel'
import { CARGO_BARCODE_TITLE, cargoNumber } from '../cargo-number'
import { Order, OrderLine, ProductTitleLink, ReturnFactLines, ReturnInvoiceRing, displayOrderNumber, orderDisplayShipment, returnDateText, returnLineFacts, returnPlatformName } from '../../shared'
import { useDismissable } from '../../../../shared/hooks/useDismissable'
import { invoiceCreationDisabledHelp, type ProductImagePreview, isInvoicePending, isMicroExportLabelBlocked, orderNotificationContext, customerDisplayName, canChangeOrderCargo } from './order-page-helpers'

function hepsiburadaPackageStatusHistoryCutoff(now = new Date()): Date {
  const cutoff = new Date(now)
  const day = cutoff.getUTCDate()
  cutoff.setUTCDate(1)
  cutoff.setUTCMonth(cutoff.getUTCMonth() - 1)
  const lastDay = new Date(Date.UTC(cutoff.getUTCFullYear(), cutoff.getUTCMonth() + 1, 0)).getUTCDate()
  cutoff.setUTCDate(Math.min(day, lastDay))
  return cutoff
}

const cancelledOrderInvoiceWriteNote = 'İptal edilen siparişe fatura kesilmez, yüklenmez ve fatura durumu elle değiştirilmez. Kesilmiş fatura Fatura İptal Et ile iptal edilir.'

// The Shopify package whose invoice status is set by hand: the order detail names it (the list row may not carry it).
async function shopifyInvoicePackageId(orderId: string) {
  const detail = await hubApi<ApiAnswer<'getOrdersById'>>(`/orders/${orderId}`)
  if (detail.platformCode !== 'SHOPIFY') throw new Error('Manuel fatura durumu yalnız Shopify siparişlerinde kullanılabilir.')
  const packageId = orderDisplayShipment(detail)?.id
  if (!packageId) throw new Error('Fatura durumu için Shopify sipariş paketi bulunamadı.')
  return packageId
}

export function OrderReferenceRow({ item, selected, onSelect, onMenuChange, onInvoiceCreate, onInvoiceDetails, onInvoiceUpload, onCourierChange, onOrderStatusChange, onProcessOrder, onCreatePackage, processing, onPrintLabel, onPreviewImage, labelSettings, printedLabels, noBuyer = false, tab = 'ALL', showCancelled = false }: { tab?: string; showCancelled?: boolean; item: Order; selected: boolean; onSelect: (checked: boolean) => void; openMenu: 'invoice' | 'actions' | null; onMenuChange: (value: 'invoice' | 'actions' | null) => void; onInvoiceCreate: () => void; onInvoiceDetails: () => void; onInvoiceUpload: () => void; onCourierChange: () => void; onOrderStatusChange: () => void; onProcessOrder: () => void; onCreatePackage: () => void; processing?: boolean; onPrintLabel: (format: ShippingLabelFormat) => void; onPreviewImage: (preview: ProductImagePreview) => void; labelSettings: ShippingLabelSettings; printedLabels: Set<string>; noBuyer?: boolean }) {

  // In the İptal tab a partly cancelled order shows only its cancelled products, with where the rest of it is.
  const lines = rowLines(item, tab, showCancelled)
  const lineView = useProductLines(lines.length)
  const cancelledPart = tab === 'CANCELLED' && isPartlyCancelled(item)
  const shipment = orderDisplayShipment(item)
  const invoiceNeedsAction = isInvoicePending(item)
  const invoiceCreationEnabled = item.invoiceCreationEnabled ?? false
  const invoiceCreationTooltip = invoiceCreationDisabledHelp
  const labelBlockedByInvoice = isMicroExportLabelBlocked(item)
  const [copied, setCopied] = useState(false)
  const [invoiceStatusModalOpen, setInvoiceStatusModalOpen] = useState(false)
  const money = (value: number) => formatMoney(value, item.currency)
  const delivery = deliveryText(item)
  const normalizedOrderStatus = item.derivedStatus.toUpperCase()
  const isShopifyOrder = item.platformCode === 'SHOPIFY'
  const isHepsiburadaOrder = item.platformCode === 'HEPSIBURADA'
  const canTakeIntoWork = canTakeOrderIntoWork(item.platformCode)
  const navigate = useNavigate()
  const isHepsiburadaUnverifiedWithoutPackage = item.derivedStatus === 'UNVERIFIED' && item.platformCode.trim().toUpperCase() === 'HEPSIBURADA' && (item.packages?.length ?? 0) === 0
  const hepsiburadaPackageHistoryExpired = isHepsiburadaUnverifiedWithoutPackage && Date.parse(item.orderedAt) < hepsiburadaPackageStatusHistoryCutoff().getTime()
  const isCancelledOrder = normalizedOrderStatus === 'CANCELLED'
  // In the İptal tab a partly cancelled order says so beside its platform (an order cancelled whole needs no mark
  // there); its product lines are only struck through.
  const tabBadge = cancelledPart ? <RrBadge label="Kısmi İptal" tone="warn" glyph="cancelled" /> : null
  const saleTotal = item.grossAmount || item.netAmount
  const hasDiscount = item.discountAmount > 0
  const invoiceableTotal = Math.max(0, saleTotal - item.discountAmount)
  const isCargoTrackingStage = ['SHIPPED', 'UNDELIVERED'].includes(normalizedOrderStatus)
  const isCargoLabelStage = ['NEW', 'PROCESSING', 'READY_TO_SHIP'].includes(normalizedOrderStatus)
  const canChangeCargo = canChangeOrderCargo(item)
  const enabledLabelFormats: ShippingLabelFormat[] = [
    ...(labelSettings.showA4Button ? ['a4' as const] : []),
    ...(labelSettings.showStickerButton ? ['sticker' as const] : [])
  ]
  async function copyOrderNumber() { try { if (!await copyText(item.orderNumber)) throw new Error('copy'); setCopied(true); window.setTimeout(() => setCopied(false), 1400) } catch { setCopied(false) } }
  const rowRef = useRef<HTMLTableRowElement>(null)
  // Each unit price sits level with its product line (desktop table only).
  useLayoutEffect(() => {
    const row = rowRef.current
    return row ? observeUnitPriceAlignment(row) : undefined
  }, [lines.length, noBuyer])
  const [detailOpen, setDetailOpen] = useState(false)
  const [detailActionsOpen, setDetailActionsOpen] = useState(false)
  // The detail sheet's action menu had no outside close: it stayed open until an action ran or the sheet closed.
  const detailMenuRef = useRef<HTMLDivElement>(null)
  const detailActionsTriggerRef = useRef<HTMLButtonElement>(null)
  useDismissable(detailActionsOpen, [detailActionsTriggerRef, detailMenuRef], () => setDetailActionsOpen(false), { triggerRef: detailActionsTriggerRef })
  // The order detail sheet covers the page on phones only; there the page behind it holds still.
  const phone = useMediaQuery('(max-width: 760px)')
  useScrollLock(detailOpen && phone)
  // Owner, 18 Nov 2026: an exchange resend shows the exchange package Trendyol named on the claim (package, carrier,
  // tracking), the first package beside it as "İlk Paket"; without that package only a note says so (nothing invented).
  const exchange = item.exchangeResend ?? null
  const exchangePackage = exchange?.packageId ? exchange : null
  // Owner, 23 Nov 2026: the owner marks the exchange package handed to the carrier (or removes his mark); owners and admins only.
  const lacksElevated = useLacksElevatedRole()
  const [shipMark, setShipMark] = useState<boolean | null>(null)
  const shipEntry: ['check' | 'close', string, () => void] | null = !exchange?.claimId || lacksElevated ? null : !exchange.shipped ? ['check', 'Kargoya Verildi Olarak İşaretle', () => setShipMark(true)] : exchange.shippedSource === 'MANUAL' ? ['close', 'İşareti Kaldır', () => setShipMark(false)] : null
  // Owner, 23 Nov 2026: a re-shipment added by hand ("Elle Eklendi") closes with the same mark (an exchange's own mark first).
  const manual = item.manualResend ?? null
  const [manualMark, setManualMark] = useState<boolean | null>(null)
  const manualShipEntry: ['check' | 'close', string, () => void] | null = !manual || lacksElevated || shipEntry ? null : !manual.shipped ? ['check', 'Kargoya Verildi Olarak İşaretle', () => setManualMark(true)] : ['close', 'İşareti Kaldır', () => setManualMark(false)]
  const firstPackageText = formatPackageLabel(shipment?.externalPackageId)
  const firstCargoText = [cargoLabel(shipment?.cargoProviderName ?? item.cargoProviderName), shipment?.cargoTrackingNumber ?? item.cargoTrackingNumber].filter(Boolean).join(' · ')
  const packageNoText = exchangePackage ? formatPackageLabel(exchangePackage.packageId) ?? exchangePackage.packageId!
    : formatPackageLabel(shipment?.externalPackageId) ?? (isHepsiburadaUnverifiedWithoutPackage ? 'Paket bilgisi doğrulanmadı' : isHepsiburadaOrder ? 'Henüz paketlenmedi' : 'Bekleniyor')
  // The package's cargo barcode while no tracking number has come (Hepsiburada gives it for an Open package).
  const cargoBarcode = cargoNumber(shipment, item.cargoTrackingNumber)?.isBarcode ? shipment?.cargoBarcode?.trim() ?? null : null
  const deliveryNoText = shipment?.cargoTrackingNumber ?? item.cargoTrackingNumber ?? cargoBarcode ?? (isHepsiburadaUnverifiedWithoutPackage ? 'Gönderi bilgisi doğrulanmadı' : isHepsiburadaOrder ? shipment ? 'Kargoya verildiğinde gelir' : 'Paket oluşturulunca gelir' : 'Takip no bekleniyor')
  // Phones: the order card opens its detail sheet only from this button (a tap anywhere on the card opened it by
  // mistake while scrolling); wider screens hide it (orders-sade.css) and keep the row as it is.
  const detailOpenButton = <button type="button" className="rr-btn rr-detail-open" aria-haspopup="dialog" onClick={() => setDetailOpen(true)}>Detayları Aç</button>
  const closeDetailThen = (run: () => void) => () => { setDetailOpen(false); setDetailActionsOpen(false); run() }
  const detailPrimaryAction = isCancelledOrder ? null : isCargoTrackingStage ? (shipment?.cargoTrackingNumber ? <Link className="rr-btn rr-primary" to={`/shipments/${shipment.id}`}>Kargo Takip</Link> : null) : isCargoLabelStage && shipment ? <>{enabledLabelFormats.map(format => <button type="button" key={format} className="rr-btn rr-primary" disabled={labelBlockedByInvoice} onClick={closeDetailThen(() => onPrintLabel(format))}><RrIcon name="print" />{format === 'a4' ? 'A4' : 'Sticker'} Etiketi Yazdır</button>)}</> : null
  const invoiceDocumentHref = item.invoiceId ? `/api/v1/invoices/${item.invoiceId}/documents/latest/content` : item.invoiceDocumentUrl
  // A cancelled order's sheet offers what its desktop invoice menu does: reading only, and the way to cancel the issued
  // invoice on its detail page; nothing that issues, uploads or sets an invoice status.
  const detailActions: Array<{ key: string; label: string; disabled?: boolean; run: () => void }> = isCancelledOrder ? (exchangePackage ? [] : [
    ...(!invoiceNeedsAction && invoiceDocumentHref ? [{ key: 'invoice-view', label: 'Faturayı Gör', run: () => { window.open(invoiceDocumentHref, '_blank', 'noopener') } }] : []),
    { key: 'invoice-info', label: 'Fatura Bilgileri', run: onInvoiceDetails },
    ...(!invoiceNeedsAction ? [{ key: 'invoice-cancel', label: 'Fatura İptal Et', disabled: !item.invoiceId, run: () => navigate(`/invoices/${item.invoiceId}`) }] : [])
  ]) : [
    ...(isShopifyOrder ? [{ key: 'status', label: 'Sipariş Durumunu Değiştir', run: onOrderStatusChange }] : normalizedOrderStatus === 'NEW' && canTakeIntoWork ? [{ key: 'process', label: processing ? 'İşleniyor…' : 'İşleme Al', disabled: processing, run: isHepsiburadaOrder ? onCreatePackage : onProcessOrder }] : []),
    ...(isHepsiburadaOrder && !['SHIPPED', 'UNDELIVERED', 'DELIVERED', 'RETURNED'].includes(normalizedOrderStatus) ? [{ key: 'package', label: 'Paket Oluştur', run: onCreatePackage }] : []),
    ...(canChangeCargo ? [{ key: 'courier', label: 'Başka Kargo Firması İle Gönder', run: onCourierChange }] : []),
    ...(invoiceNeedsAction ? [{ key: 'invoice-create', label: 'Fatura Oluştur', disabled: !invoiceCreationEnabled, run: onInvoiceCreate }] : []),
    { key: 'invoice-info', label: 'Fatura Bilgileri', run: onInvoiceDetails },
    ...(invoiceNeedsAction ? [{ key: 'invoice-upload', label: 'Fatura Yükle', disabled: !invoiceCreationEnabled && !item.invoiceId, run: onInvoiceUpload }] : []),
    ...(shipEntry ? [{ key: 'exchange-ship', label: shipEntry[1], run: shipEntry[2] }] : []),
    ...(manualShipEntry ? [{ key: 'manual-ship', label: manualShipEntry[1], run: manualShipEntry[2] }] : [])
  ]
  const detailQuickAction = detailActions.find(action => action.key === 'process')
  const detailMenuActions = detailActions.filter(action => action !== detailQuickAction)
  const [ringTone, ringProgress, ringLabel] = undeliveredReturn(item) ? (['bad', 1, 'Teslim Edilemedi'] as [RingTone, number, string]) : orderRingState(item.derivedStatus)
  const ringDetail = orderRingDetail(delivery)
  // Time left (or days late) before shipping moves from the status ring to the bottom of the order info, in red under a day.
  const dueLine = deliveryDueLine(delivery)
  const facts = (line: OrderLine) => returnLineFacts(item.platformCode, line)
  const lineImage = (line: OrderLine) => line.imageUrl ?? (lines.length === 1 ? item.primaryImageUrl : null)
  // A line with no known photo asks the marketplace for it by barcode (the preview does the same); a miss shows the placeholder.
  const thumbFallback = (line: OrderLine) => line.fallbackImageUrl ?? (lineImage(line) ? null : createProductImageFallbackUrls([line.barcode, line.sku], item.connectionId)[0] ?? null)
  const openPreview = (line: OrderLine) => { const imageUrl = lineImage(line); const fallbackUrls = [...(line.fallbackImageUrl ? [line.fallbackImageUrl] : []), ...createProductImageFallbackUrls([line.barcode, line.sku], item.connectionId)]; onPreviewImage({ url: imageUrl ?? fallbackUrls[0]!, fallbackUrls, title: line.title, modelCode: line.modelCode, variant: line.optionSignature }) }
  const trackingNumber = exchangePackage ? exchangePackage.cargoTrackingNumber ?? null : shipment?.cargoTrackingNumber ?? item.cargoTrackingNumber
  const cargoName = exchangePackage ? exchangePackage.cargoProviderName ?? null : shipment?.cargoProviderName ?? item.cargoProviderName
  const cargoIcon = cargoCarrier(cargoName)?.iconUrl
  const labelFormat: ShippingLabelFormat | undefined = enabledLabelFormats.includes(labelSettings.defaultFormat) ? labelSettings.defaultFormat : enabledLabelFormats[0]
  // The server's first-print time counts on every computer; the local mark covers a failed call (audit 2026-10-20 S8).
  const labelPrinted = Boolean(item.labelPrintedAt) || (labelFormat ? printedLabels.has(printedShippingLabelKey(item.id, labelFormat)) : false)
  // The label printed here is the first package's: never offered on an exchange package row.
  const canPrintLabels = isCargoLabelStage && Boolean(shipment) && enabledLabelFormats.length > 0 && !exchangePackage
  // A cancelled order shows its invoice menu too (see, read, cancel the issued invoice), but nothing that issues an
  // invoice, uploads one or sets its status by hand: the manual upload and status endpoints do not refuse a cancelled order.
  const writeDisabled = (blocked: boolean): [boolean, string] => isCancelledOrder ? [true, cancelledOrderInvoiceWriteNote] : [blocked, invoiceCreationTooltip]
  const invoiceItems: Array<[ 'doc' | 'info' | 'edit' | 'upload' | 'open' | 'close', string, () => void, string?, boolean?, string?] | 'hr'> = invoiceNeedsAction
    ? [['doc', 'Fatura Oluştur', onInvoiceCreate, '', ...writeDisabled(!invoiceCreationEnabled)], ['info', 'Fatura Bilgileri', onInvoiceDetails], ...(isShopifyOrder ? [['edit', 'Fatura Durumunu Değiştir', () => setInvoiceStatusModalOpen(true), '', ...writeDisabled(!invoiceCreationEnabled && !item.invoiceId)] as ['edit', string, () => void, string, boolean, string]] : []), ['upload', 'Fatura Yükle', onInvoiceUpload, '', ...writeDisabled(!invoiceCreationEnabled && !item.invoiceId)]]
    : [...(invoiceDocumentHref ? [['open', 'Faturayı Gör', () => window.open(invoiceDocumentHref, '_blank', 'noopener')] as ['open', string, () => void]] : []), ['info', 'Fatura Bilgileri', onInvoiceDetails], ...(isShopifyOrder ? [['edit', 'Fatura Durumunu Değiştir', () => setInvoiceStatusModalOpen(true), '', ...writeDisabled(!invoiceCreationEnabled && !item.invoiceId)] as ['edit', string, () => void, string, boolean, string]] : []), 'hr', ['close', 'Fatura İptal Et', () => navigate(`/invoices/${item.invoiceId}`), 'rr-danger', !item.invoiceId, 'Bu fatura panelde kesilmedi; e-Arşiv iptali yalnız panelde kesilen faturada, Faturalar sayfasındaki fatura detayından istenir.']]
  const actionItems: Array<['edit' | 'play' | 'box' | 'truck' | 'print' | 'ban' | 'check' | 'close', string, () => void, string?, boolean?, string?] | 'hr'> = [
    ...(isShopifyOrder ? [['edit', 'Sipariş Durumunu Değiştir', onOrderStatusChange] as ['edit', string, () => void]] : []),
    // Shopify has no picking step and PttAVM refuses it (canTakeOrderIntoWork); on Hepsiburada an order is taken into
    // work by creating its package.
    ...(!canTakeIntoWork ? [] : [normalizedOrderStatus === 'NEW' ? ['play', processing ? 'İşleniyor…' : 'İşleme Al', isHepsiburadaOrder ? onCreatePackage : onProcessOrder, '', processing] as ['play', string, () => void, string, boolean] : ['play', normalizedOrderStatus === 'DELIVERED' ? 'Teslim Edildi' : 'İşleme Al', () => undefined, '', true, 'Sipariş zaten işleme alınmış.'] as ['play', string, () => void, string, boolean, string]]),
    ...(isHepsiburadaOrder && !['SHIPPED', 'UNDELIVERED', 'DELIVERED', 'RETURNED'].includes(normalizedOrderStatus) ? [['box', 'Paket Oluştur', onCreatePackage] as ['box', string, () => void]] : []),
    ...(canChangeCargo ? [['truck', 'Başka Kargo Firması İle Gönder', onCourierChange] as ['truck', string, () => void]] : []),
    ...(shipEntry ? [shipEntry] : []),
    ...(manualShipEntry ? [manualShipEntry] : []),
    ...(canPrintLabels ? ['hr' as const, ...enabledLabelFormats.map(format => ['print', `${format === 'a4' ? 'A4' : 'Sticker'} Etiketi Yazdır`, () => onPrintLabel(format), '', labelBlockedByInvoice, 'Mikro ihracat faturası kesilmeden kargo etiketi yazdırılamaz.'] as ['print', string, () => void, string, boolean, string])] : []),
    'hr', ['ban', 'İptal Et', () => undefined, 'rr-danger', true, 'Sipariş iptali pazar yerinin panelinden yapılır.'],
  ]
  const menu = (items: typeof actionItems | typeof invoiceItems, label: string) => <div className="rr-menu" role="menu" aria-label={label}>{items.map((entry, index) => entry === 'hr' ? <hr key={`hr-${index}`} /> : <button key={entry[1]} type="button" role="menuitem" className={entry[3] || undefined} disabled={entry[4]} title={entry[4] ? entry[5] : undefined} onClick={() => { onMenuChange(null); entry[2]() }}><RrIcon name={entry[0]} />{entry[1]}</button>)}</div>
  const detailSheet = detailOpen ? createPortal(<div className="rr-layer"><DialogSurface as="div" onClose={() => setDetailOpen(false)} className="rr-sheet rr-sheet-full" aria-label={`Sipariş ${item.orderNumber} detayı`}>
    <header className="rr-sheet-h"><button type="button" className="rr-icon-btn" aria-label="Detayı kapat" onClick={() => setDetailOpen(false)}><RrIcon name="left" /></button><b>Sipariş Detayı</b><span /></header>
    <div className="rr-sheet-b">
      <article className="rr-scard"><header><div className="rr-h3">Sipariş Bilgileri</div><RrRing tone={ringTone} progress={ringProgress} label={ringLabel} detail={ringDetail.text} detailClass={ringDetail.cls} /></header><dl className="rr-kv"><dt>Platform</dt><dd><PlatformSquareMark code={item.platformCode} name={item.platformDisplayName || returnPlatformName(item.platformCode)} size="sm" /> {item.platformDisplayName || returnPlatformName(item.platformCode)}</dd><dt>Sipariş Numarası</dt><dd className="rr-num">{displayOrderNumber(item.orderNumber)}<button type="button" className="rr-icon-btn" onClick={copyOrderNumber} aria-label={`Sipariş numarası ${item.orderNumber} kopyala`}><RrIcon name={copied ? 'check' : 'copy'} size={14} /></button></dd><dt>Sipariş Tarihi</dt><dd className="rr-num">{returnDateText(item.orderedAt)}</dd><dt>Paket No</dt><dd className="rr-num">{packageNoText}</dd><dt>Alıcı</dt><dd>{customerDisplayName(item)}</dd>{item.buyerType && <><dt>Alıcı Tipi</dt><dd><BuyerTypeTag buyerType={item.buyerType} /><small className="rr-buyer-reason">{item.buyerType.reason}</small></dd></>}{(item.platformInfo ?? []).map(info => <Fragment key={info.label}><dt>{info.label}</dt><dd>{safeExternalUrl(info.url) ? <a className="rr-btn rr-link" href={safeExternalUrl(info.url)!} target="_blank" rel="noreferrer noopener">{info.value}</a> : info.value}</dd></Fragment>)}</dl></article>
      <article className="rr-scard"><header><div className="rr-h3">Ürünler</div></header>{rowLines(item, 'ALL', showCancelled).length ? <div className="rr-plines">{rowLines(item, 'ALL', showCancelled).map(line => { const cancel = lineCancel(line); return <div className={`rr-pline${cancel.whole ? ' rr-pline-off' : ''}`} key={line.id}><RrThumb src={lineImage(line)} fallbackSrc={thumbFallback(line)} quantity={cancel.whole ? cancel.ordered : cancel.active} alt={line.title} onOpen={() => openPreview(line)} /><div><b>{line.title}</b>{cancel.whole && <span className="sr-only">İptal edildi</span>}<ReturnFactLines facts={facts(line)} /><small><span>Birim Fiyatı:</span> <b className="rr-num">{cancel.whole && line.unitPrice <= 0 ? '—' : money(line.unitPrice)}</b></small>{cancel.partial && <small className="rr-pline-cut">{partialQuantityText(line)}</small>}</div></div> })}</div> : <p className="rr-lead">Ürün bilgisi eşitleme bekliyor</p>}</article>
      <article className="rr-scard"><header><div className="rr-h3">Kargo Bilgileri</div></header><dl className="rr-kv"><dt>Kargo Firması</dt><dd>{cargoLabel(cargoName)}</dd><dt>Kargo Kodu</dt><dd className="rr-num" title={!trackingNumber && cargoBarcode ? CARGO_BARCODE_TITLE : undefined}>{deliveryNoText}</dd>{item.shippedAt && <><dt>Kargoya Veriliş</dt><dd className="rr-num">{returnDateText(item.shippedAt)}</dd></>}</dl></article>
      <article className="rr-scard"><header><div className="rr-h3">Fatura Bilgileri</div>{isCancelledOrder && invoiceStatusDisplayLabel(item.invoiceStatus, false) === 'Fatura Bekliyor' ? <span className="rr-muted-note">İptal edildi</span> : <ReturnInvoiceRing status={item.invoiceStatus} isMicroExport={item.isMicroExport} setByHand={Boolean(handSetInvoiceStatus(shipment?.manualInvoiceStatus))} />}</header><dl className="rr-kv">{hasDiscount && <><dt>Satış Tutarı</dt><dd className="rr-num">{money(saleTotal)}</dd><dt>Satıcı İndirimi</dt><dd className="rr-num">{money(item.discountAmount)}</dd></>}<dt>Faturalanacak Tutar</dt><dd className="rr-num"><b>{money(invoiceableTotal)}</b></dd>{!invoiceNeedsAction && invoiceDocumentHref && <><dt>Fatura Belgesi</dt><dd><a className="rr-btn rr-link" href={invoiceDocumentHref} download>Faturayı Gör</a></dd></>}</dl></article>
    </div>
    <footer className="rr-sheet-f">
      {detailActionsOpen && detailMenuActions.length > 0 && <div ref={detailMenuRef} className="rr-menu rr-sheet-menu" role="menu">{detailMenuActions.map(action => <button type="button" role="menuitem" key={action.key} disabled={action.disabled} onClick={closeDetailThen(action.run)}>{action.label}</button>)}</div>}
      {detailMenuActions.length > 0 && <button ref={detailActionsTriggerRef} type="button" className={`rr-trigger rr-sheet-trigger${detailActionsOpen ? ' rr-on' : ''}`} aria-haspopup="menu" aria-expanded={detailActionsOpen} onClick={() => setDetailActionsOpen(open => !open)}><span>İşlemler</span><RrIcon name="chev" /></button>}
      {detailQuickAction && <button type="button" className="rr-btn" disabled={detailQuickAction.disabled} onClick={closeDetailThen(detailQuickAction.run)}><RrIcon name="play" />{detailQuickAction.label}</button>}
      {detailPrimaryAction}
    </footer>
  </DialogSurface></div>, document.body) : null
  const rowClasses = [selected ? 'rr-sel' : '', delivery.overdue ? 'rr-due' : '', isCancelledOrder ? 'is-cancelled' : ''].filter(Boolean).join(' ')
  return <><tr ref={rowRef} className={rowClasses || undefined}>
    <td className="rr-wide"><div className={`rr-ordercell${isCancelledOrder ? ' is-cancelled' : ''}`}>{isCancelledOrder ? <span style={{ width: 16, flex: 'none' }} title="İptal siparişler toplu seçilemez" /> : <input type="checkbox" className="rr-cb" checked={selected} onChange={event => onSelect(event.target.checked)} aria-label={`Sipariş ${item.orderNumber} seç`} />}<div>{tabBadge ? <span className="rr-wordmark-row"><RrWordmark code={item.platformCode} name={item.platformDisplayName || returnPlatformName(item.platformCode)} />{tabBadge}</span> : <RrWordmark code={item.platformCode} name={item.platformDisplayName || returnPlatformName(item.platformCode)} />}<span className={`rr-on-no${delivery.overdue ? ' rr-crit' : ''}${displayOrderNumber(item.orderNumber).length > 16 ? ' rr-on-long' : ''}`}>{isCancelledOrder ? <s className="rr-on-cut" aria-label={`${displayOrderNumber(item.orderNumber)} (iptal)`}>{displayOrderNumber(item.orderNumber)}</s> : displayOrderNumber(item.orderNumber)}<button type="button" className="rr-icon-btn" title={copied ? 'Kopyalandı' : 'Kopyala'} aria-label={copied ? `Sipariş numarası ${item.orderNumber} kopyalandı` : `Sipariş numarası ${item.orderNumber} kopyala`} onClick={() => void copyOrderNumber()}><RrIcon name={copied ? 'check' : 'copy'} size={14} /></button><OrderRowMarkIcons marks={item.marks} unreadableLines={item.unreadableLineCount} amountMayBeIncomplete={item.amountMayBeIncomplete} /><ExchangeResendTag resend={item.exchangeResend} platformCode={item.platformCode} /><ManualResendTag resend={manual} /></span>{noBuyer && <small className="rr-buyer-inline"><b>Alıcı:</b> {customerDisplayName(item)} <BuyerTypeTag buyerType={item.buyerType} /></small>}<small className="rr-num"><b>Sipariş Tarihi:</b> {returnDateText(item.orderedAt)}</small><small className="rr-num"><b>Paket No:</b> {packageNoText}</small>{exchangePackage && firstPackageText && <small className="rr-num rr-first-package"><b>İlk Paket:</b> {firstPackageText}</small>}{exchange && !exchangePackage && <small className="rr-exchange-note">{exchangeNoPackageNote(item.platformCode)}</small>}{exchange && <small className="rr-exchange-note"><b>Değişim:</b> {exchangeResendStatus(exchange)}</small>}{manual && <small className="rr-exchange-note"><b>Yeniden Gönderim:</b> {manualResendStatus(manual)}</small>}{dueLine && <small className={`rr-due${delivery.overdue ? ' is-late' : ''}${delivery.kind === 'remaining' && delivery.overdue ? ' is-soon' : ''}`} title={dueLineTitle(delivery)}><b>Termin:</b> {dueLine.text}</small>}{undeliveredReturn(item) && <small className="rr-due is-late" title="Kargo paketi teslim edemedi ve paket size geri döndü."><b>Durum:</b> Teslim Edilemedi, İade Döndü</small>}{hepsiburadaPackageHistoryExpired && <small className="rr-note-line">Hepsiburada kargo durum listeleri son bir ayı kapsıyor; daha eski paket kaydı API’den doğrulanamıyor.</small>}{cancelledPart && <small className="rr-order-part"><Link to={`/orders?${new URLSearchParams({ search: item.orderNumber })}`} onClick={event => { if (window.matchMedia('(max-width: 760px)').matches) { event.preventDefault(); setDetailOpen(true) } }}>{remainingText(item)}</Link></small>}</div></div></td>
    {!noBuyer && <td className="rr-mid" data-l="Alıcı"><div className="rr-cust"><b>{customerDisplayName(item).replace(/-/g, '‑')}</b><BuyerTypeTag buyerType={item.buyerType} /></div></td>}
    <td className="rr-wide" data-l="Bilgiler"><div className={`rr-plines${lineView.scrolls ? ' rr-plines-scroll' : ''}`} ref={lineView.linesRef} onScroll={lineView.scrolls ? lineView.linesScroll : undefined} data-more={lineView.scrolls ? lineView.moreHint : undefined}>{lines.length ? lines.slice(0, lineView.limit).map(line => { const cancel = lineCancel(line); const off = cancel.whole || cancelledPart; return <div className={`rr-pline${off ? ' rr-pline-off' : ''}`} key={line.id}><RrThumb src={lineImage(line)} fallbackSrc={thumbFallback(line)} quantity={cancelledPart ? cancel.cancelled : cancel.whole ? cancel.ordered : cancel.active} alt={line.title} onOpen={() => openPreview(line)} /><div><b title={line.title}><ProductTitleLink platformCode={item.platformCode} line={line} /></b>{off && <span className="sr-only">İptal edildi</span>}<small className="rr-card-only"><span>Birim Fiyatı:</span> <b className="rr-num">{off && line.unitPrice <= 0 ? '—' : money(line.unitPrice)}</b></small>{facts(line).map(([label, value]) => <small key={label} title={`${label}: ${value}`}><span>{label}:</span> <b className="rr-num">{value}</b></small>)}</div></div> }) : <div className="rr-pline"><RrThumb src={item.primaryImageUrl} alt="Ürün" /><div><b>Ürün bilgisi eşitleme bekliyor</b></div></div>}</div>{lineView.phone && lineView.hiddenOnPhone > 0 && <button type="button" className="rr-btn rr-link rr-more-lines" aria-expanded={lineView.expanded} onClick={lineView.toggleExpanded}>{lineView.expanded ? 'Daha Az Göster' : `${lineView.hiddenOnPhone} Ürün Daha`}</button>}</td>
    <td className="rr-mid rr-price-col" data-l="Birim Fiyat"><div className={`rr-prices${lineView.scrolls ? ' rr-prices-scroll' : ''}`} ref={lineView.pricesRef} onScroll={lineView.scrolls ? lineView.pricesScroll : undefined}>{lines.length ? lines.slice(0, lineView.limit).map(line => { const cancel = lineCancel(line); const off = cancel.whole || cancelledPart; const quantity = cancelledPart ? cancel.cancelled : cancel.whole ? cancel.ordered : cancel.active; return <div className={`rr-pr${off ? ' rr-pr-off' : ''}`} key={line.id}><b className="rr-num">{off && line.unitPrice <= 0 ? '—' : money(line.unitPrice)}</b>{cancel.partial && !cancelledPart ? <small>{partialQuantityText(line)}</small> : off ? <small>{quantity} adet iptal</small> : quantity > 1 && <small>{quantity} adet</small>}</div> }) : <div className="rr-pr"><b className="rr-num">{money(item.netAmount)}</b></div>}</div></td>
    <td className="rr-mid" data-l="Kargo"><div className="rr-cargocell">{cargoIcon ? <img className="rr-cargo-img" src={cargoIcon} alt={cargoLabel(cargoName)} title={cargoLabel(cargoName)} referrerPolicy="no-referrer" loading="lazy" /> : <span className="rr-muted">{cargoName?.trim() ? cargoLabel(cargoName) : 'Kargo bekleniyor'}</span>}<small className="rr-num" title={!trackingNumber && cargoBarcode ? CARGO_BARCODE_TITLE : undefined}>{trackingNumber ?? (exchangePackage ? 'Takip no bekleniyor' : cargoBarcode ?? deliveryNoText)}</small>{item.shippedAt && <small className="rr-shipdate" title="Kargoya veriliş tarihi"><span>Kargoya Veriliş</span><b className="rr-num">{returnDateText(item.shippedAt)}</b></small>}{exchangePackage ? <>{exchangePackage.cargoTrackingLink && <a className="rr-btn rr-link" style={{ fontSize: 12 }} href={exchangePackage.cargoTrackingLink} target="_blank" rel="noopener noreferrer">Kargo Takip <RrIcon name="open" /></a>}{firstCargoText && <small className="rr-first-package" title="İlk paketin kargosu"><b>İlk Paket:</b> {firstCargoText}</small>}</> : isCargoTrackingStage && shipment?.cargoTrackingNumber && <Link className="rr-btn rr-link" style={{ fontSize: 12 }} to={`/shipments/${shipment.id}`}>Kargo Takip <RrIcon name="open" /></Link>}</div></td>
    <td data-l="Fatura"><div className="rr-invcell">{hasDiscount && <><span className="rr-m"><span>Satış Tutarı</span><b className="rr-num">{money(saleTotal)}</b></span><span className="rr-m"><span>Satıcı İndirimi</span><b className="rr-num">{money(item.discountAmount)}</b></span></>}<span className="rr-m rr-pay"><span>Faturalanacak Tutar</span><b className="rr-num">{money(invoiceableTotal)}</b></span>{isCancelledOrder && invoiceStatusDisplayLabel(item.invoiceStatus, false) === 'Fatura Bekliyor' ? <span className="rr-muted-note">İptal edildi</span> : <ReturnInvoiceRing status={item.invoiceStatus} isMicroExport={item.isMicroExport} setByHand={Boolean(handSetInvoiceStatus(shipment?.manualInvoiceStatus))} />}{exchangePackage && <small className="rr-first-package">İlk paketin faturası</small>}{!exchangePackage && <RrAnchor trigger={(open, toggle) => <button type="button" className="rr-btn rr-link rr-inv-trigger" aria-haspopup="menu" aria-expanded={open} onClick={toggle}>Fatura İşlemleri <RrIcon name="chev" /></button>}>{() => menu(invoiceItems, `${displayOrderNumber(item.orderNumber)} fatura işlemleri`)}</RrAnchor>}</div></td>
    <td className="rr-mid" data-l="İşlemler">{isCancelledOrder ? <div className="rr-actcell"><span className="rr-muted-note">İşlem yapılamaz</span>{detailOpenButton}</div> : <div className="rr-actcell">{canPrintLabels && labelFormat && <button type="button" className={`rr-btn rr-lbl${labelPrinted ? ' rr-printed' : ''}`} disabled={labelBlockedByInvoice} title={labelBlockedByInvoice ? 'Mikro ihracat faturası kesilmeden kargo etiketi yazdırılamaz.' : labelPrinted ? `${labelFormat === 'a4' ? 'A4' : 'Sticker'} etiketi daha önce yazdırıldı; tekrar yazdırılabilir.` : `${labelFormat === 'a4' ? 'A4' : 'Sticker'} Etiketi Yazdır`} onClick={() => onPrintLabel(labelFormat)}><RrIcon name="print" />Etiket</button>}<RrAnchor className="rr-dropdown" trigger={(open, toggle) => <button type="button" className={`rr-trigger${open ? ' rr-on' : ''}`} aria-haspopup="menu" aria-expanded={open} onClick={toggle}><span>İşlemler</span><RrIcon name="chev" /></button>}>{() => menu(actionItems, `${displayOrderNumber(item.orderNumber)} sipariş işlemleri`)}</RrAnchor>{detailOpenButton}</div>}</td>
  </tr>
    {detailSheet}
    {shipMark !== null && exchange?.claimId && <ExchangeResendShipDialog claim={{ id: exchange.claimId, version: exchange.claimVersion ?? 0, orderNumber: displayOrderNumber(item.orderNumber) }} platformCode={item.platformCode} marked={shipMark} onClose={() => setShipMark(null)} onDone={(message, kind) => appendNotification({ ...orderNotificationContext(item), operation: 'Değişim yeniden gönderimi', message, kind })} />}
    {manualMark !== null && manual && <ManualResendShipDialog record={{ id: manual.id, version: manual.version, orderNumber: displayOrderNumber(item.orderNumber) }} marked={manualMark} onClose={() => setManualMark(null)} onDone={(message, kind) => appendNotification({ ...orderNotificationContext(item), operation: 'Elle eklenen yeniden gönderim', message, kind })} />}
    {invoiceStatusModalOpen && <ShopifyInvoiceStatusDialog orderNumber={displayOrderNumber(item.orderNumber)} invoiceStatus={item.invoiceStatus} manualInvoiceStatus={shipment?.manualInvoiceStatus} resolvePackageId={() => shopifyInvoicePackageId(item.id)} onClose={() => setInvoiceStatusModalOpen(false)} onFeedback={(message, kind) => { appendNotification({ ...orderNotificationContext(item), operation: 'Shopify fatura durumu', message, kind: normalizeNotificationKind(message, kind) }) }} />}
  </>
}

// Order status as a ring: how far the order has come, coloured by its state.
function orderRingState(status: string): [RingTone, number, string] {
  switch (status.trim().toUpperCase()) {
    case 'NEW': case 'CREATED': case 'AWAITING': case 'PENDING': return ['prog', 0.2, 'Yeni']
    case 'PROCESSING': case 'PICKING': case 'PREPARING': case 'READY_TO_SHIP': case 'INVOICED': return ['wait', 0.45, 'İşleme Alındı']
    case 'SHIPPED': case 'IN_TRANSIT': return ['move', 0.7, 'Kargoda']
    case 'DELIVERED': return ['ok', 1, 'Teslim Edildi']
    case 'CANCELLED': return ['bad', 1, 'İptal Edildi']
    case 'UNDELIVERED': return ['bad', 0.7, 'Teslim Edilemedi']
    case 'RETURN_IN_TRANSIT': return ['move', 0.5, 'İade Taşımada']
    case 'RETURNED': return ['off', 1, 'İade Edildi']
    case 'PAYMENT_PENDING': return ['wait', 0.1, orderStatusLabel(status)]
    case 'ON_HOLD': return ['off', 0, orderStatusLabel(status)]
    case 'UNVERIFIED': return ['off', 0, orderStatusLabel(status)]
    default: return ['off', 0, titleTr(orderStatusLabel(status))]
  }
}
