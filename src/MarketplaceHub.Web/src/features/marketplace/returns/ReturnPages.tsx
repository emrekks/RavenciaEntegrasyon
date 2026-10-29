// İadeler (/returns, /returns/:id).
import { copyText } from '../../../shared/copy-text'
import { Fragment, useEffect, useEffectEvent, useLayoutEffect, useRef, useState, type FormEvent } from 'react'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router'
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi, loadAllPages } from '../../../shared/api'
import { cargoCarrier, cargoLabel, CountUp } from '../../../shared/components'
import { newIdempotencyKey } from '../../../shared/idempotency'
import { sendStockDisposition, type PendingDispositionKeys } from './return-disposition-key'
import { useMediaQuery } from '../../../shared/hooks/useMediaQuery'
import { NoticeToast } from '../../../shared/components/NoticeToast'
import { statusLabel } from '../../../shared/status-labels'
import { DateRangePicker } from '../../../shared/DateRangePicker'
import { readUrlChoice, readUrlList, readUrlNumber, readUrlText, useListUrlSync } from '../../../shared/list-url-state'
import { isSellerFaultReturn, returnReasonPresentation, returnReasonSummary } from '../return-reference-presentation'
import { RrAnchor, RrBadge, RrError, RrFilterCard, RrIcon, RrLoading, RrModal, RrPageSize, RrRing, RrThumb, RrWordmark, titleTr, type RingTone } from '../returns-ui'
import { appendNotification, integrationNotificationTarget, isExternalWriteMessage, notificationError, notificationLinks, type NotificationContext, type NotificationDetail, type NotificationTarget } from '../../../shared/notifications'
import { resolveReturnStatus } from '../../../shared/dashboard-operational-links'
import { code128Bars } from '../../shipping'
import { marketplacePlatformOptions } from '../marketplace-platform-support'
import { activeReturnSyncConnections, enqueueReturnSyncs } from '../return-sync'
import { ReturnApproveDialog, type ReturnApproveRow } from '../ReturnApproveDialog'
import { ReturnBulkDecisionDialog, type BulkDecisionItem } from '../ReturnBulkDecisionDialog'
import { isActionableReturnLine } from '../return-claim-visibility'
import { canPrintReturnLabel } from '../return-label-visibility'
import { ReturnMarketplaceHistory, acceptDetailText } from './ReturnMarketplaceHistory'
import { formatMarketplaceBarcode, shouldShowReturnCountdown, type ApprovedReturnSort } from '../return-reference-presentation'
import { ViewTabsWithMore, viewTabLabel } from '../../../shared/view-tabs'
import { formatMoney } from '../../../shared/format-money'
import { returnListParams, returnSummaryParams, type ReturnFilters, type ReturnRefinements, type ReturnSummary } from './return-list-query'
import { Connection, Order, OrderLine, Page, ProductTitleLink, RETURN_NO_PLATFORM, ReturnColumnPop, ReturnFactLines, ReturnHeadCell, ReturnInvoiceRing, ReturnLine, ReturnNoticeKind, ReturnPlatformPop, type ReturnAmounts, displayOrderNumber, orderDisplayShipment, productLineQuantity, remainingText, returnDateText, returnLineFacts, returnLineFieldValue, returnOptionalValue, returnPlatformName, usableShipmentDueAt } from '../shared'

type ReturnClaim = { id: string; externalClaimId: string; orderNumber: string; status: string; rawStatus: string; reasonText: string | null; reasonCode?: string | null; actionDueAt: string | null; approvedAt?: string | null; systemNote?: string | null; sellerDescription?: string | null; platformApprovalReason?: string | null; platformDescription?: string | null; version: number; customerName: string; orderedAt: string | null; orderAmount: number; currency: string; cargoProviderName: string | null; cargoTrackingNumber: string | null; cargoTrackingLink?: string | null; orderCargoProviderName?: string | null; orderCargoTrackingNumber?: string | null; primaryImageUrl: string | null; productCount: number; primaryBarcode: string | null; lines: OrderLine[] | null; packageNumber: string | null; invoiceStatus: string; grossAmount: number; discountAmount: number; isMicroExport: boolean; connectionId: string | null; platformCode: string; platformDisplayName: string; lineStatuses?: Record<string, string> | null; actionDueAtEstimated?: boolean; returnAmounts?: ReturnAmounts | null; rowKey?: string }
type ReturnDetail = Omit<ReturnClaim, 'lines'> & { reasonCode: string | null; allowedActions: string[]; lines: ReturnLine[] | null; stockDispositionAvailable: boolean; externalWritesEnabled: boolean; decisionPending?: boolean }; type ReturnIssueReason = { id: string; name: string; evidenceRequired: boolean }
function isUrgentReturnDeadline(value: string | null) { const usable = usableShipmentDueAt(value); return usable !== null && new Date(usable).getTime() - Date.now() <= 24 * 60 * 60 * 1000 }
function printReturnLabel(item: ReturnClaim, lines: OrderLine[]) {
  const barcodeValue = item.cargoTrackingNumber?.trim() || item.packageNumber?.trim() || item.externalClaimId
  const bars = code128Bars(barcodeValue)
  const barcode = bars.map((isBar, index) => isBar ? `<rect x="${index}" y="0" width="1" height="48"/>` : '').join('')
  const escape = (value: string) => value.replace(/[&<>"']/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[char]!)
  const products = lines.map(line => `<li>${escape(line.title)} · ${productLineQuantity(line)} adet${line.sku ? ` · ${escape(line.sku)}` : ''}</li>`).join('')
  const popup = window.open('', '_blank', 'popup,width=760,height=650')
  if (!popup) return
  popup.document.write(`<!doctype html><html lang="tr"><head><meta charset="utf-8"><title>İade etiketi #${escape(item.orderNumber)}</title><style>body{font:14px Arial,sans-serif;color:#111;margin:0;padding:24px}.label{width:100%;max-width:680px;margin:auto;border:2px solid #111;padding:20px;box-sizing:border-box}.head{display:flex;justify-content:space-between;gap:16px;border-bottom:1px solid #999;padding-bottom:12px}.eyebrow{font-size:11px;letter-spacing:1px;text-transform:uppercase;color:#555}h1{font-size:22px;margin:5px 0}.meta{display:grid;grid-template-columns:1fr 1fr;gap:14px;margin:18px 0}.meta div{display:grid;gap:5px}.meta small{color:#555}.barcode{width:100%;height:70px;margin:16px 0}.barcode rect{fill:#111}.number{text-align:center;font-size:18px;font-weight:700;letter-spacing:2px}.products{border-top:1px solid #999;padding-top:12px}.products ul{padding-left:18px;line-height:1.6}@media print{body{padding:0}.label{max-width:none;min-height:250mm;page-break-inside:avoid}}</style></head><body><main class="label"><div class="head"><div><span class="eyebrow">${escape(item.platformDisplayName)} · İade paketi</span><h1>Sipariş #${escape(item.orderNumber)}</h1></div><strong>${escape(statusLabel(item.status))}</strong></div><div class="meta"><div><small>İade kayıt no</small><strong>${escape(item.externalClaimId)}</strong></div><div><small>Kargo firması</small><strong>${escape(item.cargoProviderName || 'Belirtilmemiş')}</strong></div><div><small>Kargo takip no</small><strong>${escape(item.cargoTrackingNumber || 'Henüz iletilmedi')}</strong></div><div><small>Paket no</small><strong>${escape(item.packageNumber || '—')}</strong></div></div><svg class="barcode" viewBox="0 0 ${bars.length} 48" role="img" aria-label="${escape(barcodeValue)}">${barcode}</svg><div class="number">${escape(barcodeValue)}</div><section class="products"><strong>Ürünler</strong><ul>${products || '<li>Ürün ayrıntısı yok</li>'}</ul></section></main></body></html>`)
  popup.document.close()
  popup.focus()
  popup.print()
}
function returnGroup(status: string) { const value = status.toUpperCase(); if (['APPROVED', 'COMPLETED'].includes(value)) return 'APPROVED'; if (['REJECTED', 'CANCELLED'].includes(value)) return 'REJECTED'; if (['REQUESTED', 'CREATED'].includes(value)) return 'REQUESTED'; if (['ACTION_REQUIRED', 'WAITING_FOR_SELLER_ACTION'].includes(value)) return 'ACTION_REQUIRED'; if (['AWAITING_SHIPMENT', 'WAITING_FOR_SHIPMENT', 'IN_TRANSIT', 'RETURN_IN_TRANSIT', 'SHIPPED'].includes(value)) return 'SHIPPING'; if (value === 'DISPUTED') return 'DISPUTED'; if (['SUSPENDED', 'ON_HOLD'].includes(value)) return 'SUSPENDED'; return 'REVIEW' }
function returnRemaining(value: string | null) { const text = remainingText(value); return /doldu|gelmedi/u.test(text) ? text : `${text.replace(/\s*kaldı$/u, '')} kaldı` }
function returnRingTone(status: string): [RingTone, number] {
  switch (returnGroup(status)) {
    case 'REQUESTED': return ['prog', 0.2]
    case 'SHIPPING': return ['move', 0.45]
    case 'ACTION_REQUIRED': return ['wait', 0.7]
    case 'APPROVED': return ['ok', 1]
    case 'REJECTED': return ['bad', 1]
    case 'DISPUTED': return ['bad', 0.66]
    case 'REVIEW': return ['move', 0.66]
    default: return ['off', 0]
  }
}
function ReturnImagePreview({ title, imageUrl, facts, onClose }: { title: string; imageUrl: string | null | undefined; facts: Array<[string, string]>; onClose: () => void }) {
  const crumb = facts.filter(([label]) => label === 'Model Kodu' || label === 'Renk' || label === 'Beden').map(([label, value]) => `${label}: ${value}`).join(' · ')
  return <RrModal crumb={crumb || undefined} title={title} size="img" onClose={onClose}><div className="rr-img-preview">{imageUrl ? <img src={imageUrl} alt={title} referrerPolicy="no-referrer" /> : <RrIcon name="image" />}</div></RrModal>
}
function ReturnLabelPreview({ item, lines, onClose }: { item: ReturnClaim; lines: OrderLine[]; onClose: () => void }) {
  const track = item.cargoTrackingNumber?.trim() || item.packageNumber?.trim() || item.externalClaimId
  return <RrModal crumb="Yazdırma Önizlemesi" title="İade Etiketi Yazdır" desc={`${displayOrderNumber(item.orderNumber)} · Etiket yeni pencerede açılır ve yazdırma başlar.`} size="md" onClose={onClose} foot={<><span className="rr-note" /><button type="button" className="rr-btn" onClick={onClose}>Vazgeç</button><button type="button" className="rr-btn rr-primary" onClick={() => { printReturnLabel(item, lines); onClose() }}><RrIcon name="print" />Yazdır</button></>}>
    <div style={{ display: 'grid', placeItems: 'center' }}><div className="rr-rlabel"><div className="rr-hd"><div><small>{returnPlatformName(item.platformCode)} · İade Paketi</small><h4>Sipariş {displayOrderNumber(item.orderNumber)}</h4></div><b>{titleTr(statusLabel(item.status))}</b></div><div className="rr-mt"><div><small>İade Kayıt No</small><b>{item.externalClaimId}</b></div><div><small>Kargo Firması</small><b>{cargoLabel(item.cargoProviderName)}</b></div><div><small>Kargo Takip No</small><b>{track}</b></div><div><small>Müşteri</small><b>{item.customerName}</b></div></div><div className="rr-bars" /><div className="rr-no">{item.packageNumber ?? track}</div><ul>{lines.map(line => <li key={line.id}>{line.title} · {productLineQuantity(line)} adet{line.sku ? ` · ${line.sku}` : ''}</li>)}</ul></div></div>
  </RrModal>
}
// The return's amounts as the marketplace wrote them for the returned lines (Trendyol: Satış, İndirim, Faturalanmış;
// Hepsiburada: its own refund amount), never calculated from the order. An amount the marketplace did not write reads "—".
type ReturnAmountRow = { label: string; value: number | null; pay?: boolean }
const RETURN_AMOUNT_UNKNOWN = 'Pazaryeri iade tutarını bildirmedi.'
function returnAmountRows(amounts: ReturnAmounts | null | undefined): ReturnAmountRow[] {
  const sale = amounts?.saleAmount ?? null
  const discount = amounts?.discountAmount ?? null
  const invoiced = amounts?.invoicedAmount ?? null
  const refund = amounts?.refundAmount ?? null
  if (sale === null && discount === null && invoiced === null) return [{ label: 'İade Tutarı', value: refund, pay: true }]
  const rows: ReturnAmountRow[] = [{ label: 'Satış Tutarı', value: sale }, { label: 'İndirim Tutarı', value: discount }, { label: 'Faturalanmış Tutar', value: invoiced, pay: true }]
  return refund === null ? rows : [...rows, { label: 'İade Tutarı', value: refund, pay: true }]
}
// The unit price the marketplace sold the returned item at (as Trendyol shows it), else the line's stored unit price.
function returnUnitPrice(unitPrice: number, amounts: ReturnAmounts | null | undefined, quantity: number) {
  return amounts?.saleAmount != null && quantity > 0 ? amounts.saleAmount / quantity : unitPrice
}
function ReturnAmountValue({ value, money }: { value: number | null; money: (value: number) => string }) {
  return value === null ? <b className="rr-num rr-muted" title={RETURN_AMOUNT_UNKNOWN}>—</b> : <b className="rr-num">{money(value)}</b>
}
function ReturnAmountCells({ amounts, money }: { amounts: ReturnAmounts | null | undefined; money: (value: number) => string }) {
  return <>{returnAmountRows(amounts).map(row => <span key={row.label} className={`rr-m${row.pay ? ' rr-pay' : ''}`}><span>{row.label}</span><ReturnAmountValue value={row.value} money={money} /></span>)}</>
}
// Facts strip of the decision and detail popups.
function ReturnDetailFacts({ item, plain, money }: { item: ReturnClaim | ReturnDetail; plain?: boolean; money: (value: number) => string }) {
  const state = titleTr(statusLabel(item.status))
  const cargoIcon = cargoCarrier(item.cargoProviderName)?.iconUrl
  return <div className="rr-facts"><div><small>Durum</small>{plain ? <RrBadge label={state} /> : <b>{state}</b>}</div>{item.reasonText && <div><small>İade Sebebi</small><b>{returnReasonPresentation(item).label}</b></div>}{(item.cargoTrackingNumber || item.cargoProviderName) && <div><small>Kargo</small>{cargoIcon ? <img className="rr-cargo-img rr-fact-cargo" src={cargoIcon} alt={cargoLabel(item.cargoProviderName)} title={cargoLabel(item.cargoProviderName)} referrerPolicy="no-referrer" loading="lazy" /> : item.cargoProviderName?.trim() && <b>{cargoLabel(item.cargoProviderName)}</b>}{item.cargoTrackingNumber && <b className="rr-num rr-fact-track">{item.cargoTrackingNumber}</b>}</div>}{returnAmountRows(item.returnAmounts).map(row => <div key={row.label}><small>{row.label}</small><ReturnAmountValue value={row.value} money={money} /></div>)}{item.orderedAt && <div><small>Talep Tarihi</small><b className="rr-num">{returnDateText(item.orderedAt)}</b></div>}{plain && item.approvedAt && <div><small>Onaylanma Tarihi</small><b className="rr-num">{returnDateText(item.approvedAt)}</b></div>}</div>
}
function ReturnReferenceRow({ item, order, onNotice, selection, selectColumn, noBuyer }: { item: ReturnClaim; order: Order | null; onNotice?: ReturnNoticeHandler; selection?: { checked: boolean; onChange: () => void }; selectColumn?: boolean; noBuyer?: boolean }) {
  const [previewLine, setPreviewLine] = useState<OrderLine | null>(null)
  const [labelOpen, setLabelOpen] = useState(false)
  const [copiedOrderNumber, setCopiedOrderNumber] = useState(false)
  const [dialogMode, setDialogMode] = useState<'DETAIL' | 'APPROVE' | 'REJECT' | null>(null)
  const [selectedReturnLineIds, setSelectedReturnLineIds] = useState<string[]>([])
  const rowRef = useRef<HTMLTableRowElement>(null)
  const detailQuery = useQuery({ queryKey: ['return', item.id], queryFn: () => hubApi<ReturnDetail>(`/returns/${item.id}`), enabled: dialogMode !== null, staleTime: 30_000 })
  const navigate = useNavigate()
  const lines = order?.lines?.length ? order.lines : item.lines ?? []
  const currency = order?.currency ?? item.currency
  const money = (value: number) => formatMoney(value, currency)
  const invoiceStatus = order?.invoiceStatus ?? item.invoiceStatus
  const orderShipment = order ? orderDisplayShipment(order) : null
  const rejected = returnGroup(item.status) === 'REJECTED'
  const hasOutboundShipment = rejected && Boolean(item.cargoTrackingNumber?.trim() || item.cargoTrackingLink?.trim())
  const trackingNumber = rejected ? item.cargoTrackingNumber : item.cargoTrackingNumber ?? orderShipment?.cargoTrackingNumber ?? item.orderCargoTrackingNumber
  const cargoProvider = rejected ? item.cargoProviderName : item.cargoProviderName ?? orderShipment?.cargoProviderName ?? item.orderCargoProviderName
  const canTrackReturn = Boolean(item.cargoTrackingLink && /^https:\/\//i.test(item.cargoTrackingLink))
  const canTrackOrder = !rejected && Boolean(orderShipment?.id && trackingNumber?.trim())
  const detailItem = detailQuery.data ?? item
  const detailLines = detailQuery.data?.lines ?? lines
  const detailCurrency = detailItem.currency ?? currency
  const detailMoney = (value: number) => formatMoney(value, detailCurrency)
  const reasonPresentation = returnReasonPresentation(item)
  const sellerFault = isSellerFaultReturn(item, reasonPresentation.label)
  const decisionAvailable = detailQuery.data?.allowedActions.some(value => value === 'APPROVE' || value === 'REJECT') === true
  const marketplaceName = returnPlatformName(detailItem.platformCode)
  // Approvals use the product-selection dialog; Hepsiburada adds its refund/change choice, and its pre-approval review keeps the older flow.
  const approveIsHepsiburada = detailItem.platformCode?.toUpperCase() === 'HEPSIBURADA'
  const useApproveDialog = !approveIsHepsiburada || detailItem.rawStatus.replace(/[_-]/gu, '').toUpperCase() !== 'AWAITINGPREAPPROVAL'
  const approveRows: ReturnApproveRow[] = (detailQuery.data?.lines ?? []).filter(isActionableReturnLine).map(line => ({ id: line.id, orderLineId: line.orderLineId, title: line.title, imageUrl: line.imageUrl, quantity: line.remainingQuantity > 0 ? line.remainingQuantity : line.quantity, sku: line.sku, barcode: formatMarketplaceBarcode(detailItem.platformCode, line.barcode) ?? line.barcode, color: returnLineFieldValue(line, ['renk', 'color'], 'color') ?? null, size: returnLineFieldValue(line, ['beden', 'size'], 'size') ?? null, modelCode: line.modelCode ?? null, hasInventoryMapping: line.hasInventoryMapping }))
  const actionRequired = returnGroup(item.status) === 'ACTION_REQUIRED'
  const urgentDeadline = isUrgentReturnDeadline(item.actionDueAt)
  const activeCountdown = shouldShowReturnCountdown(actionRequired || hasOutboundShipment, item.actionDueAt)
  const canPrint = canPrintReturnLabel(item.status, hasOutboundShipment)
  const [ringTone, ringProgress] = returnRingTone(item.status)
  const customer = order?.customerName ?? item.customerName
  const selectActionableLines = useEffectEvent(() => {
    if (!dialogMode || !detailQuery.data) return
    setSelectedReturnLineIds(detailQuery.data.lines?.filter(isActionableReturnLine).map(line => line.id) ?? [])
  })
  useEffect(() => selectActionableLines(), [dialogMode, detailQuery.data?.id])
  // Each unit price sits level with its product line (desktop table only).
  useLayoutEffect(() => {
    const row = rowRef.current
    if (!row) return
    const align = () => {
      const products = row.querySelectorAll<HTMLElement>('.rr-pline'), prices = row.querySelectorAll<HTMLElement>('.rr-pr')
      const table = window.innerWidth > 1024
      prices.forEach((price, index) => { price.style.height = table && products[index] ? `${products[index].getBoundingClientRect().height}px` : '' })
    }
    align()
    if (typeof ResizeObserver === 'undefined') return
    const observer = new ResizeObserver(align)
    row.querySelectorAll('.rr-pline').forEach(element => observer.observe(element))
    return () => observer.disconnect()
  }, [lines.length, noBuyer])
  function toggleReturnLine(lineId: string) { setSelectedReturnLineIds(current => current.includes(lineId) ? current.filter(id => id !== lineId) : [...current, lineId]) }
  async function copyReturnOrderNumber() { try { if (!await copyText(item.orderNumber)) throw new Error('copy'); setCopiedOrderNumber(true); window.setTimeout(() => setCopiedOrderNumber(false), 1400) } catch { setCopiedOrderNumber(false) } }
  const menuItems: Array<[ 'check' | 'close' | 'open' | 'print', string, () => void, string?, boolean?] | 'hr'> = actionRequired
    ? [['check', 'İadeyi Onayla', () => setDialogMode('APPROVE')], ['close', 'İade Ret Talebi', () => setDialogMode('REJECT'), 'rr-danger'], 'hr', ['open', 'Detaylı Bilgi', () => setDialogMode('DETAIL')], ['print', 'İade Etiketi Yazdır', () => setLabelOpen(true), '', !canPrint]]
    : canPrint ? [['print', 'İade Etiketi Yazdır', () => setLabelOpen(true)], 'hr', ['open', 'Detaylı Bilgi', () => setDialogMode('DETAIL')]]
    : [['open', 'Detaylı Bilgi', () => setDialogMode('DETAIL')], 'hr', ['print', 'İade Etiketi Yazdır', () => setLabelOpen(true), '', true]]
  const cargoIcon = cargoCarrier(cargoProvider)?.iconUrl
  const reasonNote = returnReasonSummary(item)?.note ?? null
  const closeDialog = () => setDialogMode(null)
  return <><tr ref={rowRef} className={[selection?.checked ? 'rr-sel' : '', urgentDeadline && actionRequired ? 'rr-due' : ''].filter(Boolean).join(' ') || undefined}>
    <td className="rr-wide"><div className="rr-ordercell">{selectColumn && (selection ? <input type="checkbox" className="rr-cb" checked={selection.checked} onChange={selection.onChange} aria-label={`${displayOrderNumber(item.orderNumber)} iadesini toplu onaya dahil et`} /> : <span style={{ width: 16, flex: 'none' }} />)}<div><RrWordmark code={item.platformCode} name={item.platformDisplayName || returnPlatformName(item.platformCode)} /><span className={`rr-on-no${urgentDeadline && actionRequired ? ' rr-crit' : ''}`}>{displayOrderNumber(item.orderNumber)}<button type="button" className="rr-icon-btn" title={copiedOrderNumber ? 'Kopyalandı' : 'Kopyala'} aria-label={copiedOrderNumber ? `Sipariş numarası ${item.orderNumber} kopyalandı` : `Sipariş numarası ${item.orderNumber} kopyala`} style={{ width: 22, height: 22 }} onClick={() => void copyReturnOrderNumber()}><RrIcon name={copiedOrderNumber ? 'check' : 'copy'} size={14} /></button></span>{noBuyer && <small className="rr-buyer-inline"><b>Alıcı:</b> {customer}</small>}<small className="rr-num"><b>Sipariş Tarihi:</b> {returnDateText(order?.orderedAt ?? item.orderedAt)}</small><small className="rr-num"><b>Paket No:</b> {orderShipment?.externalPackageId ?? item.packageNumber ?? '—'}</small><small className="rr-num"><b>Kargo Teslimat No:</b> {trackingNumber?.trim() || 'Henüz platformdan iletilmedi'}</small></div></div></td>
    {!noBuyer && <td className="rr-mid" data-l="Alıcı"><div className="rr-cust"><b>{customer}</b></div></td>}
    <td className="rr-wide" data-l="Bilgiler"><div className="rr-plines">{lines.length ? lines.map(line => { const imageUrl = line.imageUrl ?? item.primaryImageUrl; const quantity = productLineQuantity(line); const facts = returnLineFacts(item.platformCode, line); return <div className="rr-pline" key={line.id}><RrThumb src={imageUrl} quantity={quantity} alt={line.title} onOpen={() => setPreviewLine(line)} /><div><b title={line.title}><ProductTitleLink platformCode={item.platformCode} line={line} /></b><small className="rr-card-only"><span>Birim Fiyatı:</span> <b className="rr-num">{money(line.unitPrice)}</b></small>{facts.map(([label, value]) => <small key={label} title={`${label}: ${value}`}><span>{label}:</span> <b className="rr-num">{value}</b></small>)}</div></div> }) : <div className="rr-pline"><RrThumb src={item.primaryImageUrl} alt={`${item.productCount} ürün`} /><div><b>{item.productCount} ürün</b><small><span>Barkod:</span> <b className="rr-num">{formatMarketplaceBarcode(item.platformCode, item.primaryBarcode) ?? '—'}</b></small></div></div>}</div></td>
    <td className="rr-mid rr-price-col" data-l="Birim Fiyat"><div className="rr-prices">{lines.length ? lines.map(line => { const quantity = productLineQuantity(line); return <div className="rr-pr" key={line.id}><b className="rr-num">{money(returnUnitPrice(line.unitPrice, line.returnAmounts, quantity))}</b>{quantity > 1 && <small>{quantity} adet</small>}</div> }) : <div className="rr-pr"><b className="rr-num">{money(item.orderAmount)}</b></div>}</div></td>
    <td className="rr-mid" data-l="Kargo"><div className="rr-cargocell">{cargoIcon ? <img className="rr-cargo-img" src={cargoIcon} alt={cargoLabel(cargoProvider)} title={cargoLabel(cargoProvider)} referrerPolicy="no-referrer" loading="lazy" /> : <span className="rr-muted">{cargoProvider?.trim() ? cargoLabel(cargoProvider) : 'Kargo bilgisi bekleniyor'}</span>}<small className="rr-num">{trackingNumber?.trim() || '—'}</small>{canTrackReturn ? <a className="rr-btn rr-link" style={{ fontSize: 12 }} href={item.cargoTrackingLink!} target="_blank" rel="noopener noreferrer">Kargo Takip <RrIcon name="open" /></a> : canTrackOrder && orderShipment && <button type="button" className="rr-btn rr-link" style={{ fontSize: 12 }} onClick={() => navigate(`/shipments/${orderShipment.id}`)}>Paket Detayı <RrIcon name="open" /></button>}</div></td>
    <td data-l="Fatura"><div className="rr-invcell"><ReturnAmountCells amounts={item.returnAmounts} money={money} /><ReturnInvoiceRing status={invoiceStatus} isMicroExport={item.isMicroExport} /></div></td>
    <td data-l="İade Sebebi"><div className="rr-reasoncell"><b className={sellerFault ? 'rr-fault' : undefined} title={sellerFault ? 'Satıcı kaynaklı iade: iade masrafları satıcıya yansıtılır.' : undefined}>{reasonPresentation.label}</b>{reasonNote && <small title={reasonNote[1]}><strong>{reasonNote[0]}:</strong> {reasonNote[1]}</small>}</div></td>
    <td className="rr-mid" data-l="Durum"><RrRing tone={ringTone} progress={ringProgress} label={titleTr(statusLabel(item.status))} detail={activeCountdown ? `${returnRemaining(item.actionDueAt)}${item.actionDueAtEstimated ? ' (tahmini)' : ''}` : undefined} detailClass={activeCountdown && urgentDeadline ? 'crit' : undefined} /></td>
    <td className="rr-mid" data-l="İşlemler"><div className="rr-actcell"><RrAnchor trigger={(open, toggle) => <button type="button" className={`rr-trigger${open ? ' rr-on' : ''}`} aria-haspopup="menu" aria-expanded={open} onClick={toggle}><span>İşlemler</span><RrIcon name="chev" /></button>}>{close => <div className="rr-menu" role="menu" aria-label={`${displayOrderNumber(item.orderNumber)} iade işlemleri`}>{menuItems.map((entry, index) => entry === 'hr' ? <hr key={`hr-${index}`} /> : <button key={entry[1]} type="button" role="menuitem" className={entry[3] || undefined} disabled={entry[4]} title={entry[4] ? 'Bu iade için etiket henüz oluşmadı.' : undefined} onClick={() => { close(); entry[2]() }}><RrIcon name={entry[0]} />{entry[1]}</button>)}</div>}</RrAnchor><button type="button" className="rr-btn rr-link" onClick={() => setDialogMode('DETAIL')}>Detayları Aç <RrIcon name="open" /></button></div></td>
  </tr>
    {previewLine && <ReturnImagePreview title={previewLine.title} imageUrl={previewLine.imageUrl ?? item.primaryImageUrl} facts={returnLineFacts(item.platformCode, previewLine)} onClose={() => setPreviewLine(null)} />}
    {labelOpen && <ReturnLabelPreview item={item} lines={lines} onClose={() => setLabelOpen(false)} />}
    {dialogMode === 'APPROVE' && useApproveDialog && <ReturnApproveDialog open claimId={item.id} version={detailItem.version} marketplaceName={marketplaceName} order={{ connectionId: detailItem.connectionId ?? item.connectionId ?? null, platformCode: detailItem.platformCode, platformName: marketplaceName, orderNumber: displayOrderNumber(detailItem.orderNumber), customer: detailItem.customerName, orderedAt: detailItem.orderedAt, packageNumber: item.packageNumber, claimCode: item.externalClaimId, reason: reasonPresentation.label }} rows={approveRows} wholeClaim={approveIsHepsiburada} loading={detailQuery.isLoading} error={detailQuery.isError ? detailQuery.error : null} onClose={closeDialog} onDone={(message, kind, details, target) => notifyReturn(onNotice, message, kind, details, target?.href.startsWith('/integrations') ? target : undefined, { ...returnNotificationContext(detailItem), title: kind === 'error' ? 'İade Kabul Kararı İletilemedi' : 'İade Kabul Kararı İletildi', operation: 'İade kabul kararı' })} />}
    {dialogMode && !(dialogMode === 'APPROVE' && useApproveDialog) && <RrModal crumb={`${marketplaceName} İade`} title={dialogMode === 'APPROVE' ? 'İade Onayı' : dialogMode === 'REJECT' ? 'İade Ret Talebi' : 'Detaylı Bilgi'} desc={`Sipariş ${displayOrderNumber(detailItem.orderNumber)} · ${detailItem.customerName}`} size="lg" onClose={closeDialog} foot={<><span className="rr-note" /><button type="button" className="rr-btn" onClick={closeDialog}>Kapat</button></>}>
      {detailQuery.isLoading ? <RrLoading text="İade detayları yükleniyor…" /> : detailQuery.isError || !detailQuery.data ? <RrError error={detailQuery.error} onRetry={() => void detailQuery.refetch()} /> : <>
        <ReturnDetailFacts item={detailItem} plain={dialogMode === 'DETAIL'} money={detailMoney} />
        <div className="rr-h3">Ürünler</div>
        {dialogMode === 'DETAIL' ? detailLines.map(line => {
          const approvedAt = returnLineFieldValue(line, ['onaylanma', 'approved'], 'approvedAt') ?? detailItem.approvedAt
          const systemNote = returnLineFieldValue(line, ['sistem notu', 'system'], 'systemNote') ?? returnOptionalValue(detailItem.systemNote)
          const reasonText = returnLineFieldValue(line, ['sebep', 'reason'], 'reasonText') ?? detailItem.reasonText
          const sellerDescription = returnLineFieldValue(line, ['satıcı açıklaması', 'seller'], 'sellerDescription') ?? returnOptionalValue(detailItem.sellerDescription)
          // Trendyol's settlement of the line (acceptDetail, note, resolved, acceptedBySeller) fills these rows when nothing else does.
          const settlement = line as Partial<ReturnLine>
          const platformReason = returnLineFieldValue(line, ['trendyol onay nedeni', 'approval reason'], 'platformApprovalReason') ?? returnOptionalValue(detailItem.platformApprovalReason) ?? acceptDetailText(settlement.acceptDetail)
          const platformDescription = returnLineFieldValue(line, ['trendyol açıklaması', 'platform description'], 'platformDescription') ?? returnOptionalValue(detailItem.platformDescription) ?? (settlement.marketplaceNote?.trim() || null)
          const disputeState = settlement.resolved == null ? null : settlement.resolved ? 'Çözüldü' : 'Çözülmedi'
          const sellerAccepted = settlement.acceptedBySeller == null ? null : settlement.acceptedBySeller ? 'Evet' : 'Hayır'
          const fields: Array<[string, string, string?]> = [['Onaylanma Tarihi', approvedAt ? returnDateText(approvedAt) : '-', approvedAt ? undefined : 'rr-empty'], ['Sistem Notu', systemNote ?? '-', systemNote ? 'rr-sys' : 'rr-empty'], ['Sebep', reasonText ?? '-', reasonText ? undefined : 'rr-empty'], ['Satıcı Açıklaması', sellerDescription ?? '-', sellerDescription ? undefined : 'rr-empty'], [`${marketplaceName} Onay Nedeni`, platformReason ?? '-', platformReason ? undefined : 'rr-empty'], [`${marketplaceName} Açıklaması`, platformDescription ?? '-', platformDescription ? undefined : 'rr-empty'], ...(detailItem.platformCode === 'TRENDYOL' ? [['İhtilaf Durumu', disputeState ?? '-', disputeState ? undefined : 'rr-empty'], ['Satıcı Onayı', sellerAccepted ?? '-', sellerAccepted ? undefined : 'rr-empty']] as Array<[string, string, string?]> : [])]
          return <article className="rr-refline" key={line.id}><div className="rr-rh"><RrThumb src={line.imageUrl} quantity={productLineQuantity(line as OrderLine)} large alt={line.title} /><div><b>{line.title}<button type="button" className="rr-icon-btn" title="Ürün adını kopyala" aria-label="Ürün adını kopyala" style={{ width: 22, height: 22 }} onClick={() => { void navigator.clipboard?.writeText(line.title).catch(() => undefined) }}><RrIcon name="copy" /></button></b><ReturnFactLines facts={returnLineFacts(detailItem.platformCode, line)} /></div></div><dl className="rr-kv">{fields.map(([label, value, className]) => <Fragment key={label}><dt>{label}</dt><dd className={className}>{value}</dd></Fragment>)}</dl></article>
        }) : <div className="rr-pcards">{detailLines.map(line => {
          const selectable = decisionAvailable
          const selected = selectedReturnLineIds.includes(line.id)
          const facts = returnLineFacts(detailItem.platformCode, line)
          const ordered = ['Barkod', 'Stok Kodu', 'Renk', 'Beden', 'Model Kodu'].map(label => facts.find(fact => fact[0] === label)).filter((fact): fact is [string, string] => Boolean(fact))
          const reason = returnLineFieldValue(line, ['sebep', 'reason'], 'reasonText') ?? detailItem.reasonText
          // eslint-disable-next-line jsx-a11y/no-noninteractive-element-interactions -- the whole card is a mouse target; the checkbox inside is the keyboard control
          return <article className={`rr-pcard${selectable && selected ? ' rr-on' : ''}`} key={line.id} onClick={selectable ? event => { if (!(event.target as Element).closest('.rr-cklabel')) toggleReturnLine(line.id) } : undefined} onKeyDown={selectable ? event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); toggleReturnLine(line.id) } } : undefined} role={selectable ? 'button' : undefined} tabIndex={selectable ? 0 : undefined} aria-pressed={selectable ? selected : undefined}><div className="rr-pc-h"><RrThumb src={line.imageUrl} quantity={line.quantity} large alt={line.title} /><span className="rr-ptext"><b>{line.title}</b><small>{line.quantity} adet · {detailMoney(returnUnitPrice(line.unitPrice, line.returnAmounts, line.quantity ?? 0))}{line.returnAmounts?.invoicedAmount != null && <> · Faturalanmış Tutar {detailMoney(line.returnAmounts.invoicedAmount)}</>}{line.returnAmounts?.refundAmount != null && <> · İade Tutarı {detailMoney(line.returnAmounts.refundAmount)}</>}</small></span>{selectable && <label className="rr-cklabel"><input type="checkbox" className="rr-cb" checked={selected} onChange={() => toggleReturnLine(line.id)} />İşleme Dahil</label>}</div><dl className="rr-pairs">{ordered.map(([label, value]) => <div key={label}><dt>{label}</dt><dd>{value}</dd></div>)}{reason && <div><dt>Sebep</dt><dd>{reason}</dd></div>}</dl></article>
        })}</div>}
        {dialogMode === 'DETAIL' && detailItem.platformCode === 'TRENDYOL' && <ReturnMarketplaceHistory returnId={detailItem.id} lines={detailLines} showSettlement={false} />}
        {dialogMode === 'DETAIL' ? <ReturnInlineActions item={detailQuery.data} onNotice={onNotice} /> : <ReturnDecisionActions item={detailQuery.data} selectedLineIds={selectedReturnLineIds} onSelectedLineIdsChange={setSelectedReturnLineIds} onNotice={onNotice} onComplete={closeDialog} initialAction={dialogMode} showActionPicker={false} />}
      </>}
    </RrModal>}
  </>
}
function returnPlatformStatusText(rawStatus: string | null | undefined, marketplaceName: string) {
  if (!rawStatus) return ''
  const normalized = rawStatus.trim().toUpperCase().replace(/[\s-]+/g, '_').replace(/[^A-Z0-9_]/g, '')
  const known: Record<string, string> = {
    WAITINGFRAUDCHECK: `${marketplaceName} incelemesi bekleniyor`,
    WAITING_FRAUD_CHECK: `${marketplaceName} incelemesi bekleniyor`,
    WAITING_APPROVAL: `${marketplaceName} onayı bekleniyor`,
    APPROVED: `${marketplaceName} tarafından onaylandı`,
    REJECTED: `${marketplaceName} tarafından reddedildi`,
  }
  return known[normalized] ?? statusLabel(rawStatus)
}
type ReturnNoticeHandler = (message: string, kind?: ReturnNoticeKind) => void
function returnPlatformStatusKind(rawStatus: string | null | undefined): ReturnNoticeKind {
  if (!rawStatus) return 'success'
  const normalized = rawStatus.trim().toUpperCase().replace(/[\s-]+/g, '_').replace(/[^A-Z0-9_]/g, '')
  if (/(WAITING|PENDING|PROCESS|FRAUD|REVIEW|APPROVAL)/u.test(normalized)) return 'info'
  if (/(REJECT|DECLIN|FAIL|ERROR)/u.test(normalized)) return 'error'
  if (/(APPROV|ACCEPT|COMPLETE|SUCCESS)/u.test(normalized)) return 'success'
  return 'info'
}
function notifyReturn(onNotice: ReturnNoticeHandler | undefined, message: string, kind: ReturnNoticeKind = 'info', details?: NotificationDetail[], target?: NotificationTarget | null, context?: NotificationContext) {
  appendNotification({ ...context, message, kind, target, details })
  onNotice?.(message, kind)
}
// The return a notice is about: its claim and order number (opening the return), its marketplace and store.
function returnNotificationContext(item: Pick<ReturnClaim, 'id' | 'orderNumber' | 'externalClaimId' | 'platformCode' | 'connectionId' | 'platformDisplayName'>): NotificationContext {
  const claim = item.externalClaimId?.trim()
  return { platformCode: item.platformCode, connectionId: item.connectionId ?? undefined, storeName: item.platformDisplayName || undefined, subject: { kind: 'return', id: item.id, label: `${displayOrderNumber(item.orderNumber)}${claim ? ` · Talep ${claim}` : ''}`, href: notificationLinks.return(item.id) } }
}
function ReturnReceiptButton({ item, onNotice }: { item: ReturnDetail; onNotice?: ReturnNoticeHandler }) {
  const client = useQueryClient()
  const receive = useMutation({
    mutationFn: () => hubApi(`/returns/${item.id}/received`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey(), 'If-Match': `"v${item.version}"` } }),
    onSuccess: async () => {
      notifyReturn(onNotice, 'İade paketi teslim alındı. Onay veya ret işlemleri açıldı.', 'success', undefined, undefined, { ...returnNotificationContext(item), title: 'İade Paketi Teslim Alındı', operation: 'İade teslim alma' })
      await client.invalidateQueries({ queryKey: ['return', item.id] })
      await client.invalidateQueries({ queryKey: ['returns'] })
    },
    onError: error => { const failure = notificationError(error, 'İade teslim alındı olarak işaretlenemedi.'); notifyReturn(onNotice, failure.message, 'error', undefined, undefined, { ...returnNotificationContext(item), title: 'İade Teslim Alınamadı', operation: 'İade teslim alma', code: failure.code }) }
  })
  if (!item.allowedActions.includes('RECEIVE')) return null
  return <section className="rr-dsec"><header><div className="rr-h3">Kargo Teslimi</div><p>İade paketi fiziksel olarak elinize ulaştıysa teslim aldığınızı işaretleyin. Ardından aynı pencereden onay veya ret kararı verebilirsiniz.</p></header><div><button type="button" className="rr-btn" disabled={receive.isPending} onClick={() => receive.mutate()}><RrIcon name="box" />{receive.isPending ? 'Kaydediliyor…' : 'Teslim Aldım'}</button></div></section>
}
type ReturnDecisionDraft = { action: 'APPROVE' | 'REJECT' | 'PREAPPROVAL_CONFIRM'; reasonCode: string | null; explanation: string | null; evidenceAssetIds: string[]; returnLineIds: string[]; reasonLabel: string | null; finalizedWith: 'Refund' | 'Change' | null }
type ReturnDecisionActionsProps = { item: ReturnDetail; onNotice?: ReturnNoticeHandler; onComplete?: () => void; selectedLineIds?: string[]; onSelectedLineIdsChange?: (ids: string[]) => void; initialAction?: ReturnDecisionDraft['action']; showActionPicker?: boolean }
function ReturnDecisionActions({ item, onNotice, onComplete, selectedLineIds: controlledSelectedLineIds, onSelectedLineIdsChange, initialAction, showActionPicker = true }: ReturnDecisionActionsProps) {
  const client = useQueryClient(); const lines = (item.lines ?? []).filter(isActionableReturnLine); const isHepsiburada = item.platformCode?.toUpperCase() === 'HEPSIBURADA'; const isAwaitingPreApproval = isHepsiburada && item.rawStatus.replace(/[_-]/gu, '').toUpperCase() === 'AWAITINGPREAPPROVAL'; const [action, setAction] = useState<ReturnDecisionDraft['action']>(() => initialAction ?? (isAwaitingPreApproval ? 'PREAPPROVAL_CONFIRM' : 'APPROVE')); const [reasonId, setReasonId] = useState(''); const [finalizedWith, setFinalizedWith] = useState<'Refund' | 'Change' | ''>(''); const [evidenceAssetIds, setEvidenceAssetIds] = useState<string[]>([]); const [localSelectedLineIds, setLocalSelectedLineIds] = useState<string[]>(() => lines.map(line => line.id)); const [confirmation, setConfirmation] = useState<ReturnDecisionDraft | null>(null); const isControlled = controlledSelectedLineIds !== undefined; const selectedLineIds = controlledSelectedLineIds ?? localSelectedLineIds; const updateSelectedLineIds = onSelectedLineIdsChange ?? setLocalSelectedLineIds; const externalWritesDisabled = item.externalWritesEnabled !== true
  const marketplaceName = returnPlatformName(item.platformCode)
  const fileInput = useRef<HTMLInputElement>(null)
  const resetDecision = useEffectEvent(() => { if (!isControlled) setLocalSelectedLineIds(lines.map(line => line.id)); setConfirmation(null); setAction(initialAction ?? (isAwaitingPreApproval ? 'PREAPPROVAL_CONFIRM' : 'APPROVE')) })
  useEffect(() => resetDecision(), [item.id, item.version, item.lines, isControlled, isAwaitingPreApproval, initialAction])
  const reasons = useQuery({ queryKey: ['return-reasons', item.id], queryFn: () => hubApi<ReturnIssueReason[]>(`/returns/${item.id}/rejection-reasons`), enabled: item.allowedActions.includes('REJECT'), retry: false })
  const decision = useMutation({
    mutationFn: (payload: ReturnDecisionDraft) => hubApi<ReturnDetail>(`/returns/${item.id}/actions`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey(), 'If-Match': `"v${item.version}"` }, body: JSON.stringify({ action: payload.action, reasonCode: payload.reasonCode, explanation: payload.explanation, evidenceAssetIds: payload.evidenceAssetIds, returnLineIds: payload.returnLineIds, finalizedWith: payload.finalizedWith }) }),
    onSuccess: async (result, payload) => {
      setConfirmation(null)
      const response = result.rawStatus ? ` ${marketplaceName} durumu: ${returnPlatformStatusText(result.rawStatus, marketplaceName)}.` : ''
      const message = payload.action === 'PREAPPROVAL_CONFIRM'
        ? `${displayOrderNumber(item.orderNumber)} siparişi için inceleme geri gönderim talebi ${marketplaceName}’a iletildi.${response}`
        : result.status === 'APPROVED'
        ? `${displayOrderNumber(item.orderNumber)} siparişinin iade kabul kararı ${marketplaceName}’a iletildi.${response}`
        : result.status === 'REJECTED'
          ? `${displayOrderNumber(item.orderNumber)} siparişinin iade ret kararı ${marketplaceName}’a iletildi.${response}`
          : result.rawStatus
            ? `${displayOrderNumber(item.orderNumber)} siparişinin iade kararı ${marketplaceName}’a iletildi; sonuç henüz kesinleşmedi.${response}`
            : `${displayOrderNumber(item.orderNumber)} siparişinin iade kararı iletildi ancak ${marketplaceName}’dan yanıt alınamadı.`
      const kind = payload.action === 'PREAPPROVAL_CONFIRM' ? 'info' : result.status === 'REJECTED' ? 'error' : result.status === 'APPROVED' ? returnPlatformStatusKind(result.rawStatus) : 'info'
      const title = payload.action === 'PREAPPROVAL_CONFIRM' ? 'İnceleme Talebi İletildi' : result.status === 'APPROVED' ? 'İade Kabul Kararı İletildi' : result.status === 'REJECTED' ? 'İade Ret Kararı İletildi' : 'İade Kararı İletildi'
      notifyReturn(onNotice, message, kind, [{ label: 'Müşteri', value: item.customerName }, ...(result.rawStatus ? [{ label: `${marketplaceName} Durumu`, value: returnPlatformStatusText(result.rawStatus, marketplaceName) }] : [])], undefined, { ...returnNotificationContext(item), title, operation: 'İade kararı' })
      await client.invalidateQueries({ queryKey: ['return', item.id] })
      await client.invalidateQueries({ queryKey: ['returns'] })
      onComplete?.()
    },
    onError: error => {
      const failure = notificationError(error, 'İade kararı başlatılamadı.')
      const reason = failure.message
      notifyReturn(onNotice, `${displayOrderNumber(item.orderNumber)} siparişinin iade kararı ${marketplaceName}’a iletilemedi: ${reason}`, 'error', [{ label: 'Müşteri', value: item.customerName }, { label: 'Neden', value: reason, tone: 'error' }], isExternalWriteMessage(reason) ? integrationNotificationTarget(item.connectionId, marketplaceName) : undefined, { ...returnNotificationContext(item), title: 'İade Kararı İletilemedi', operation: 'İade kararı', code: failure.code })
    }
  })
  async function uploadFile(file: File) {
    if (!file.size) return
    const body = new FormData(); body.set('file', file)
    try { const asset = await hubApi<{ id: string }>('/files/return-evidence', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body }); setEvidenceAssetIds(ids => ids.includes(asset.id) ? ids : [...ids, asset.id]); notifyReturn(onNotice, 'İade kanıtı güvenli depoya yüklendi.', 'success') } catch (error) { notifyReturn(onNotice, error instanceof Error ? error.message : 'Kanıt yüklenemedi.', 'error') }
  }
  const decisionActions = item.allowedActions.filter(value => value === 'APPROVE' || value === 'REJECT' || value === 'PREAPPROVAL_CONFIRM')
  const decisionLabel = (value: string) => value === 'PREAPPROVAL_CONFIRM' ? 'İnceleme İçin Geri Gönder' : value === 'APPROVE' ? 'Onayla' : value === 'REJECT' ? 'Reddet' : titleTr(statusLabel(value))
  if (!decisionActions.length) return null
  if (initialAction && !decisionActions.includes(initialAction)) return <section className="rr-dsec"><header><div className="rr-h3">Bu İşlem Kullanılamıyor</div><p>İade durumu, {marketplaceName} tarafında bu kararın gönderilmesine izin vermiyor.</p></header></section>
  const selectedReason = reasons.data?.find(reason => reason.id === reasonId)
  function requestDecision(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (externalWritesDisabled) { notifyReturn(onNotice, 'Dış yazma kapalı olduğu için iade kararı gönderilemez.', 'error'); return }
    if (isHepsiburada ? !lines.length : !selectedLineIds.length) { notifyReturn(onNotice, 'İşlem yapılacak en az bir ürün seçin.', 'error'); return }
    const data = new FormData(event.currentTarget)
    if (isHepsiburada && action === 'APPROVE' && !finalizedWith) { notifyReturn(onNotice, 'Hepsiburada talebi için ücret iadesi veya ürün değişimi seçin.', 'error'); return }
    setConfirmation({ action, reasonCode: action === 'REJECT' ? reasonId : null, explanation: action === 'REJECT' ? String(data.get('explanation') ?? '') : null, evidenceAssetIds: [...evidenceAssetIds], returnLineIds: isHepsiburada ? lines.map(line => line.id) : [...selectedLineIds], reasonLabel: action === 'REJECT' ? selectedReason?.name ?? null : null, finalizedWith: isHepsiburada && action === 'APPROVE' && finalizedWith ? finalizedWith : null })
  }
  const selectedLines = isHepsiburada ? lines : lines.filter(line => selectedLineIds.includes(line.id))
  const allLinesSelected = lines.length > 0 && selectedLineIds.length === lines.length
  const reject = action === 'REJECT'
  const submitLabel = reject ? 'Ret Kararını Gönder' : action === 'PREAPPROVAL_CONFIRM' ? 'İnceleme Talebi Oluştur' : 'İadeyi Onayla'
  const confirmKind = confirmation?.action === 'REJECT' ? 'reject' : confirmation?.action === 'PREAPPROVAL_CONFIRM' ? 'pre' : 'approve'
  const confirmExtra: Array<[string, string]> = !confirmation ? [] : confirmation.action === 'REJECT' ? [['Ret Nedeni', confirmation.reasonLabel ?? '—'], ['Açıklama', confirmation.explanation || '—']] : confirmation.action === 'PREAPPROVAL_CONFIRM' ? [['İnceleme Nedeni', 'Ürün incelemesi']] : confirmation.finalizedWith ? [['Onay Türü', confirmation.finalizedWith === 'Refund' ? 'Ücret İadesi' : 'Ürün Değişimi']] : []
  return <section className="rr-dsec">
    <header><div className="rr-h3">İade İşlemi</div><p>{isAwaitingPreApproval ? 'Ön onay talebinde iade, değişim, ret veya inceleme için geri gönderim kararını seçin.' : 'İade paketi kontrol edildikten sonra kabul veya ret kararını buradan gönderin.'}</p><p>Birden fazla ürün varsa ürün kartlarına tıklayarak hangi ürünlerin işleme alınacağını seçin. Karar gönderilmeden önce seçiminizi özet penceresinde onaylayabilirsiniz.</p></header>
    {externalWritesDisabled && <div className="rr-callout rr-warn" role="status"><RrIcon name="alert" /><span><b>Dış Yazma Kapalı.</b> Bu iade için onay veya ret gönderilemez.</span></div>}
    {isControlled && <div className="rr-selnote"><strong>{selectedLineIds.length} / {lines.length} ürün seçildi</strong><span>Ürün seçimini yukarıdaki kartlardan değiştirebilirsiniz.</span></div>}
    {!isControlled && lines.length > 1 && !isHepsiburada && <div className="rr-selnote"><label className="rr-cklabel"><input type="checkbox" className="rr-cb" checked={allLinesSelected} onChange={event => updateSelectedLineIds(event.target.checked ? lines.map(line => line.id) : [])} /><strong>{selectedLineIds.length} / {lines.length} ürün seçildi</strong></label>{lines.map(line => <label key={line.id} className="rr-cklabel"><input type="checkbox" className="rr-cb" checked={selectedLineIds.includes(line.id)} onChange={event => updateSelectedLineIds(event.target.checked ? [...selectedLineIds, line.id] : selectedLineIds.filter(id => id !== line.id))} />{line.title} · {line.quantity.toLocaleString('tr-TR')} adet</label>)}</div>}
    <form style={{ display: 'contents' }} onSubmit={requestDecision}>
      <div className="rr-dgrid">
        {showActionPicker ? <label className="rr-fld"><small>Karar</small><select className="rr-in" value={action} onChange={event => { setAction(event.target.value as ReturnDecisionDraft['action']); setReasonId(''); setFinalizedWith(''); setEvidenceAssetIds([]) }}>{decisionActions.map(value => <option key={value} value={value}>{decisionLabel(value)}</option>)}</select></label> : <div className="rr-fld"><small>İşlem</small><b>{reject ? 'İade Ret Talebi' : action === 'PREAPPROVAL_CONFIRM' ? 'İnceleme İçin Geri Gönder' : 'İadeyi Onayla'}</b></div>}
        {isHepsiburada && action === 'APPROVE' && <label className="rr-fld"><small>Onay Türü</small><select className="rr-in" value={finalizedWith} onChange={event => setFinalizedWith(event.target.value as 'Refund' | 'Change' | '')} required><option value="">Seçin</option><option value="Refund">Ücret İadesi</option><option value="Change">Ürün Değişimi</option></select></label>}
        {reject && <><label className="rr-fld"><small>{marketplaceName} Ret Nedeni</small><select className="rr-in" value={reasonId} onChange={event => setReasonId(event.target.value)} required disabled={reasons.isLoading || reasons.isError}><option value="">{reasons.isLoading ? 'Ret Nedenleri Yükleniyor…' : reasons.isError ? 'Ret Nedenleri Alınamadı' : 'Seçin'}</option>{reasons.data?.map(reason => <option key={reason.id} value={reason.id}>{reason.name}</option>)}</select>{reasons.isError ? <em className="rr-err">{marketplaceName} ret nedenleri alınamadı. İade detayını yenileyip tekrar deneyin.</em> : selectedReason && <em>{selectedReason.evidenceRequired ? 'Bu ret nedeni için kanıt dosyası zorunludur.' : 'Bu ret nedeni için açıklama zorunludur.'}</em>}</label>
          <label className="rr-fld rr-full"><small>{marketplaceName} Açıklaması</small><textarea className="rr-in" name="explanation" maxLength={500} placeholder="Ret kararının nedenini yazın…" required /></label>
          <div className="rr-fld rr-full"><small>Kanıt (PDF/JPEG/PNG, En Fazla 10 MiB)</small><div className="rr-drop" role="button" tabIndex={0} onClick={() => fileInput.current?.click()} onKeyDown={event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); fileInput.current?.click() } }} onDragOver={event => event.preventDefault()} onDrop={event => { event.preventDefault(); const file = event.dataTransfer.files?.[0]; if (file) void uploadFile(file) }}><RrIcon name="upload" /><b>Dosya Seç veya Sürükle</b><small>PDF, JPEG veya PNG · en fazla 10 MiB</small><input ref={fileInput} type="file" hidden accept="application/pdf,image/jpeg,image/png" onChange={event => { const file = event.currentTarget.files?.[0]; if (file) void uploadFile(file); event.currentTarget.value = '' }} /></div><em>{evidenceAssetIds.length} kanıt hazır{selectedReason?.evidenceRequired ? ' · bu neden için zorunlu' : ''}</em></div></>}
      </div>
      {isHepsiburada && lines.length > 1 && <em className="rr-lead">Hepsiburada kararı bu talepteki tüm ürün satırlarına uygulanır.</em>}
      {isAwaitingPreApproval && action === 'PREAPPROVAL_CONFIRM' && <em className="rr-lead">Hepsiburada’ya ürün incelemesi için geri gönderim ön onayı iletilecek.</em>}
      <div style={{ display: 'flex', justifyContent: 'flex-end' }}><button type="submit" className={`rr-btn ${reject ? 'rr-solid' : 'rr-primary'}`} disabled={externalWritesDisabled || decision.isPending || !lines.length || (isHepsiburada && action === 'APPROVE' && !finalizedWith) || (reject && (reasons.isLoading || reasons.isError || !reasonId || (selectedReason?.evidenceRequired && !evidenceAssetIds.length)))} title={externalWritesDisabled ? 'Dış yazma kapalı' : undefined}>{submitLabel}</button></div>
    </form>
    {confirmation && <RrModal title={confirmKind === 'reject' ? 'Ret Kararını Onayla' : confirmKind === 'pre' ? 'İnceleme İçin Geri Gönderimi Onayla' : 'İade Kararını Onayla'} desc={confirmKind === 'pre' ? 'Hepsiburada’ya ürün incelemesi için geri gönderim ön onayı iletilecek.' : 'Seçilen karar sağlayıcıya gönderilecek; yanıt alındığında durumu burada gösterilecek.'} size="md" onClose={() => { if (!decision.isPending) setConfirmation(null) }} foot={<><span className="rr-note" /><button type="button" className="rr-btn" disabled={decision.isPending} onClick={() => setConfirmation(null)}>Vazgeç</button><button type="button" className={`rr-btn ${confirmKind === 'reject' ? 'rr-solid' : 'rr-primary'}`} disabled={externalWritesDisabled || decision.isPending} onClick={() => decision.mutate(confirmation)} title={externalWritesDisabled ? 'Dış yazma kapalı' : undefined}>{decision.isPending ? 'Gönderiliyor…' : 'Kararı Onayla'}</button></>}>
      <div><span className={`rr-decision-tag rr-${confirmKind === 'reject' ? 'reject' : 'approve'}`}>{confirmKind === 'reject' ? 'Reddet' : confirmKind === 'pre' ? 'İncelemeye Geri Gönder' : 'Onayla'}</span></div>
      <div className="rr-h3">Seçilen Ürünler</div>
      <div className="rr-sum">{selectedLines.map(line => { const color = returnLineFieldValue(line, ['renk', 'color'], 'color'); const size = returnLineFieldValue(line, ['beden', 'size'], 'size'); return <div key={line.id}><RrThumb src={line.imageUrl} quantity={line.quantity} alt={line.title} /><span>{line.title}<small>Stok Kodu: {line.sku || '—'}{color ? ` · Renk: ${color}` : ''}{size ? ` · Beden: ${size}` : ''}</small></span><b className="rr-num">{formatMoney(line.unitPrice, item.currency)}</b></div> })}</div>
      {confirmExtra.length > 0 && <dl className="rr-kv">{confirmExtra.map(([label, value]) => <Fragment key={label}><dt>{label}</dt><dd>{value}</dd></Fragment>)}</dl>}
      <p className="rr-lead">{selectedLines.length} ürün satırı işleme alınacak.</p>
    </RrModal>}
  </section>
}
function ReturnInlineActions({ item, onNotice, selectedLineIds, onSelectedLineIdsChange, decisions = false }: { item: ReturnDetail; onNotice?: ReturnNoticeHandler; selectedLineIds?: string[]; onSelectedLineIdsChange?: (ids: string[]) => void; decisions?: boolean }) {
  const marketplaceName = returnPlatformName(item.platformCode)
  if (item.decisionPending) return <div className="rr-callout"><RrIcon name="info" /><span><b>Karar {marketplaceName}’a Gönderildi.</b> Bu iade kararı {marketplaceName}’a anlık olarak iletildi. {marketplaceName}’dan kesin durum yanıtı gelene kadar yeni bir karar gönderilemez.</span></div>
  return <><ReturnReceiptButton item={item} onNotice={onNotice} />{decisions && <ReturnDecisionActions item={item} onNotice={onNotice} selectedLineIds={selectedLineIds} onSelectedLineIdsChange={onSelectedLineIdsChange} />}</>
}
const returnPageSizes = [20, 50, 100, 200]
const approvedReturnSorts: readonly ApprovedReturnSort[] = ['APPROVED_DESC', 'APPROVED_ASC', 'ORDERED_DESC', 'ORDERED_ASC']
// The list state a returns address carries besides status and search (W16).
function returnListFromUrl(params: URLSearchParams) {
  return { from: readUrlText(params, 'from'), to: readUrlText(params, 'to'), pageSize: readUrlNumber(params, 'size', 20, returnPageSizes), pageNumber: readUrlNumber(params, 'page', 1), platforms: readUrlList(params, 'platforms'), cargo: readUrlText(params, 'cargo', 'ALL'), reason: readUrlText(params, 'reason', 'ALL'), invoice: readUrlText(params, 'invoice', 'ALL'), sort: readUrlChoice(params, 'sort', approvedReturnSorts, 'APPROVED_DESC') }
}
export function ReturnsPage() {
  const tabs = [['ALL', 'Tüm İadeler'], ['REQUESTED', 'Talep Oluşturulan'], ['SHIPPING', 'Kargoya Verilen'], ['ACTION_REQUIRED', 'Aksiyon Bekleyen'], ['APPROVED', 'Onaylanan'], ['REJECTED', 'Reddedilen'], ['REVIEW', 'Analiz'], ['DISPUTED', 'İhtilaflı'], ['SUSPENDED', 'Askıda İadeler']] as const
  const [searchParams, setSearchParams] = useSearchParams()
  const requestedStatus = searchParams.get('status') ?? 'ALL'
  const initialStatus = resolveReturnStatus(requestedStatus)
  const [status, setStatus] = useState<string>(initialStatus); const [initialList] = useState(() => returnListFromUrl(searchParams)); const [search, setSearch] = useState(() => searchParams.get('search') ?? ''); const [from, setFrom] = useState(initialList.from); const [to, setTo] = useState(initialList.to); const [pageSize, setPageSize] = useState(initialList.pageSize); const [pageNumber, setPageNumber] = useState(initialList.pageNumber); const [jumpPage, setJumpPage] = useState(''); const [selectedPlatforms, setSelectedPlatforms] = useState<string[]>(initialList.platforms); const [cargoFilter, setCargoFilter] = useState(initialList.cargo); const [reasonFilter, setReasonFilter] = useState(initialList.reason); const [invoiceStatusFilter, setInvoiceStatusFilter] = useState(initialList.invoice); const [approvedSort, setApprovedSort] = useState<ApprovedReturnSort>(initialList.sort)
  const client = useQueryClient(); const [notice, setNoticeState] = useState(''); const [noticeKind, setNoticeKind] = useState<ReturnNoticeKind>('info')
  // A bulk approval that did not fully go through keeps its toast until it is closed: the popups are gone by then.
  const [noticeSticky, setNoticeSticky] = useState(false)
  function setNotice(message: string, kind: ReturnNoticeKind = 'info', sticky = false) { setNoticeState(message); setNoticeKind(kind); setNoticeSticky(sticky) }
  const handleNotice: ReturnNoticeHandler = (message, kind) => setNotice(message, kind)
  useEffect(() => {
    if (!notice || noticeSticky) return
    const timeout = window.setTimeout(() => setNoticeState(''), noticeKind === 'info' ? 7000 : 5500)
    return () => window.clearTimeout(timeout)
  }, [notice, noticeKind, noticeSticky])
  const returnFilters: ReturnFilters = { status, search, from, to }
  const [appliedFilters, setAppliedFilters] = useState<ReturnFilters>(returnFilters)
  useEffect(() => {
    setStatus(initialStatus)
    setAppliedFilters(current => current.status === initialStatus ? current : { ...current, status: initialStatus })
  }, [initialStatus])
  // The server splits, filters, counts and pages the cards; the page reads one page and the summary.
  const refinements: ReturnRefinements = { platforms: selectedPlatforms, cargo: cargoFilter, reason: reasonFilter, invoiceStatus: invoiceStatusFilter }
  const sort = appliedFilters.status === 'APPROVED' ? approvedSort : null
  const [pageCursors, setPageCursors] = useState<Record<number, string | null>>({ 1: null })
  const listKey = JSON.stringify([appliedFilters, refinements, sort, pageSize])
  const [cursorKey, setCursorKey] = useState(listKey)
  if (cursorKey !== listKey) { setCursorKey(listKey); setPageCursors({ 1: null }); setPageNumber(1) }
  const cursor = pageNumber > 1 ? pageCursors[pageNumber] ?? null : null
  useListUrlSync({ status: appliedFilters.status, search: appliedFilters.search, from: appliedFilters.from, to: appliedFilters.to, platforms: selectedPlatforms, cargo: cargoFilter, reason: reasonFilter, invoice: invoiceStatusFilter, sort: approvedSort, size: pageSize, page: pageNumber }, { status: 'ALL', cargo: 'ALL', reason: 'ALL', invoice: 'ALL', sort: 'APPROVED_DESC', size: 20, page: 1 })
  const query = useQuery({ queryKey: ['returns', 'list', appliedFilters, refinements, sort, pageSize, pageNumber, cursor], queryFn: () => hubApi<Page<ReturnClaim>>(`/returns?${returnListParams(appliedFilters, refinements, { sort, limit: pageSize, after: cursor, page: pageNumber }).toString()}`), placeholderData: keepPreviousData, refetchInterval: 30_000 })
  const summary = useQuery({ queryKey: ['returns', 'summary', appliedFilters, refinements], queryFn: () => hubApi<ReturnSummary>(`/returns/summary?${returnSummaryParams(appliedFilters, refinements).toString()}`), placeholderData: keepPreviousData, refetchInterval: 30_000 })
  const nextCursor = query.data?.nextCursor ?? null
  if (nextCursor && !query.isPlaceholderData && pageCursors[pageNumber + 1] !== nextCursor) setPageCursors(current => ({ ...current, [pageNumber + 1]: nextCursor }))
  const connections = useQuery({ queryKey: ['connections', 'returns'], queryFn: () => loadAllPages<Connection>('/connections') })
  const returnConnections = activeReturnSyncConnections(connections.data?.items ?? [])
  const sync = useMutation({ mutationFn: () => enqueueReturnSyncs(returnConnections, connection => hubApi(`/connections/${connection.id}/return-sync-jobs`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() } })), onSuccess: async result => { notifyReturn(handleNotice, result.failed ? `${result.succeeded} bağlantıda iade eşitlemesi kuyruğa alındı; ${result.failed} bağlantıda başlatılamadı.` : `${result.succeeded} bağlantıda iade eşitlemesi kuyruğa alındı. İş tamamlandığında tüm iadeler otomatik yenilenir.`, result.failed ? 'error' : 'info'); await client.invalidateQueries({ queryKey: ['returns'] }) }, onError: value => notifyReturn(handleNotice, value instanceof Error ? value.message : 'İade eşitlemesi başlatılamadı.', 'error') })
  const [bulkIds, setBulkIds] = useState<string[]>([])
  const [bulkMode, setBulkMode] = useState<'APPROVE' | 'REJECT' | null>(null)
  // On 1025-1240px screens the buyer column folds into the order cell so the remaining columns keep their room.
  const noBuyer = useMediaQuery('(min-width: 1025px) and (max-width: 1240px)')
  // One page of cards: a claim with accepted and waiting lines arrives as two cards, each in its own tab.
  const visibleItems = query.data?.items ?? []
  const totalCount = query.data?.totalCount ?? visibleItems.length
  const invoiceStatuses = [['FATURA_BEKLIYOR', 'Fatura Bekliyor'], ['FATURA_ISLENIYOR', 'Fatura İletiliyor'], ['FATURA_PLATFORMA_AKTARILMADI', 'Fatura İletilemedi'], ['FATURA_KONTROLDE', 'Faturası Mevcut'], ['FATURA_REDDEDILDI', 'Fatura Oluşturulamadı'], ['FATURA_BELIRSIZ', 'Fatura Sonucu Belirsiz'], ['FATURA_IPTAL', 'Fatura İptal']] as const
  // The filter choices come from the current tab (search and dates applied), as before.
  const reasons = (summary.data?.reasons ?? []).map(reason => ({ value: reason.value, label: returnReasonPresentation(reason).label }))
  const platformOptions = marketplacePlatformOptions(connections.data?.items ?? [], (summary.data?.platforms ?? []).map(platform => ({ platformCode: platform.platformCode, displayName: platform.displayName || returnPlatformName(platform.platformCode) })))
  const cargoOptions = [...(summary.data?.cargoProviders ?? [])].sort((left, right) => left.localeCompare(right, 'tr-TR'))
  // Bulk approval is offered only for returns that wait for the seller; Hepsiburada's pre-approval review keeps its own step.
  const bulkEligible = (claim: ReturnClaim) => status === 'ACTION_REQUIRED' && returnGroup(claim.status) === 'ACTION_REQUIRED' && claim.rawStatus.replace(/[_-]/gu, '').toUpperCase() !== 'AWAITINGPREAPPROVAL'
  const bulkItems = visibleItems.filter(bulkEligible)
  const bulkSelected = bulkItems.filter(claim => bulkIds.includes(claim.id))
  const allBulkSelected = bulkItems.length > 0 && bulkSelected.length === bulkItems.length
  const totalPages = Math.max(1, Math.ceil(totalCount / pageSize)); const currentPage = Math.min(pageNumber, totalPages)
  const firstPage = Math.max(1, Math.min(currentPage - 1, totalPages - 3)); const pageList = Array.from({ length: Math.min(4, totalPages) }, (_, index) => firstPage + index)
  function applyFilters() { setAppliedFilters(returnFilters); setPageNumber(1) }
  function selectStatus(value: string) { const next = { ...returnFilters, status: value }; setStatus(value); setAppliedFilters(next); setSearchParams(current => { const params = new URLSearchParams(current); if (value === 'ALL') params.delete('status'); else params.set('status', value); return params }, { replace: true }); setPageNumber(1) }
  function selectInvoiceStatus(value: string) { setInvoiceStatusFilter(value); setPageNumber(1) }
  function clearFilters() { const next: ReturnFilters = { status: 'ALL', search: '', from: '', to: '' }; setStatus(next.status); setSearch(next.search); setFrom(next.from); setTo(next.to); setSelectedPlatforms([]); setCargoFilter('ALL'); setReasonFilter('ALL'); setInvoiceStatusFilter('ALL'); setAppliedFilters(next); setSearchParams(current => { const params = new URLSearchParams(current); params.delete('status'); return params }, { replace: true }); setPageNumber(1) }
  const tabCount = (value: string) => summary.isLoading ? '…' : summary.isError || !summary.data ? '—' : summary.data.tabCounts[value] ?? 0
  const metric = (value: string) => { const count = tabCount(value); return typeof count === 'number' ? count.toLocaleString('tr-TR') : count }
  const platformTriggerLabel = selectedPlatforms.length === 0 || selectedPlatforms.includes(RETURN_NO_PLATFORM) ? 'Tüm Platformlar' : selectedPlatforms.length === 1 ? platformOptions.find(option => option.value === selectedPlatforms[0])?.label ?? selectedPlatforms[0] : `${selectedPlatforms.length} Platform Seçili`
  const cargoPopOptions = [{ value: 'ALL', label: 'Tüm Kargolar' }, ...cargoOptions.map(value => ({ value, label: cargoLabel(value) }))]
  const invoicePopOptions = [{ value: 'ALL', label: 'Tüm Fatura Durumları' }, ...invoiceStatuses.map(([value, label]) => ({ value, label }))]
  const reasonPopOptions = [{ value: 'ALL', label: 'Tüm Sebepler' }, ...reasons]
  const statusPopOptions = tabs.map(([value, label]) => ({ value, label }))
  const sortOptions: Array<[ApprovedReturnSort, string, 'down' | 'up']> = [['APPROVED_DESC', 'Onaylanma Tarihi', 'down'], ['APPROVED_ASC', 'Onaylanma Tarihi', 'up'], ['ORDERED_DESC', 'Sipariş Tarihi', 'down'], ['ORDERED_ASC', 'Sipariş Tarihi', 'up']]
  const currentSort = sortOptions.find(option => option[0] === approvedSort) ?? sortOptions[0]
  const sheetField = (label: string, current: string, options: Array<{ value: string; label: string }>, pick: (value: string) => void, right?: boolean) => <div className="rr-fld rr-sheet-only"><small>{label}</small><RrAnchor trigger={(open, toggle) => <button type="button" className={`rr-trigger${open ? ' rr-on' : ''}`} onClick={toggle}><span>{options.find(option => option.value === current)?.label ?? options[0]?.label}</span><RrIcon name="chev" /></button>}>{close => <ReturnColumnPop label={label} options={options} value={current} right={right} onPick={value => { pick(value); close() }} />}</RrAnchor></div>
  const selectColumn = bulkItems.length > 0
  const tabLabel = tabs.find(tab => tab[0] === status)?.[1] ?? 'Tüm İadeler'
  return <section className="content f3 rr rr-returns">
    <div className="rr-ph"><div><div className="rr-crumb">Pazaryeri Operasyonu</div><h1>İadeler</h1><p>İade taleplerini inceleyin, onaylayın veya reddedin; kargo ve fatura durumunu izleyin.</p></div><div className="rr-acts"><button type="button" className="rr-btn" disabled={returnConnections.length === 0 || sync.isPending} onClick={() => sync.mutate()}><RrIcon name="refresh" />{sync.isPending ? 'Eşitleniyor…' : 'İade Senkronizasyonu'}</button></div></div>
    <div className="rr-metrics"><article className="rr-warn"><small>Aksiyon Bekleyen</small><strong><CountUp value={metric('ACTION_REQUIRED')} /></strong></article><article><small>Kargoya Verilen</small><strong><CountUp value={metric('SHIPPING')} /></strong></article><article><small>Onaylanan</small><strong><CountUp value={metric('APPROVED')} /></strong></article><article><small>Tüm İadeler</small><strong><CountUp value={metric('ALL')} /></strong></article></div>
    <div className="rr-fshell">
      <ViewTabsWithMore className="return-reference-tabs" more={[]} ariaLabel="İade durumları" value={status} onChange={selectStatus} items={tabs.map(([value, label]) => { const count = tabCount(value); return { value, label: viewTabLabel(({ ALL: 'all', REQUESTED: 'new', SHIPPING: 'shipping', ACTION_REQUIRED: 'waiting', APPROVED: 'done', REJECTED: 'cancelled', REVIEW: 'review', DISPUTED: 'disputed', SUSPENDED: 'paused' } as const)[value], label), count: typeof count === 'number' ? count.toLocaleString('tr-TR') : count, countTone: value === 'ACTION_REQUIRED' && typeof count === 'number' && count > 0 ? 'warn' as const : undefined } })} />
      <RrFilterCard active={[selectedPlatforms.length > 0, Boolean(from), cargoFilter !== 'ALL', invoiceStatusFilter !== 'ALL', reasonFilter !== 'ALL'].filter(Boolean).length}><div className={`rr-filters${status === 'APPROVED' ? ' rr-has-sort' : ''}`}>
        <label className="rr-fld rr-search"><small>İade Ara</small><span style={{ position: 'relative' }}><RrIcon name="search" style={{ position: 'absolute', left: 11, top: 10, color: 'var(--rr-subtle)' }} /><input className="rr-in" style={{ paddingLeft: 34 }} aria-label="İadelerde ara" placeholder="Müşteri, sipariş, iade kodu veya barkod ara" value={search} onChange={event => setSearch(event.target.value)} onKeyDown={event => { if (event.key === 'Enter') applyFilters() }} /></span></label>
        <div className="rr-fld"><small>İade Platformu</small><RrAnchor trigger={(open, toggle) => <button type="button" className={`rr-trigger${open ? ' rr-on' : ''}`} onClick={toggle} disabled={!platformOptions.length}><span>{platformTriggerLabel}</span><RrIcon name="chev" /></button>}>{() => <ReturnPlatformPop options={platformOptions} selected={selectedPlatforms} onChange={setSelectedPlatforms} />}</RrAnchor></div>
        <DateRangePicker className="rr-fld rr-date" from={from} to={to} onChange={range => { setFrom(range.from); setTo(range.to) }} label="İade Talep Tarihi" placeholder="Başlangıç – Bitiş" icon={<RrIcon name="cal" />} chevron={<RrIcon name="chev" />} />
        {status === 'APPROVED' && <div className="rr-fld"><small>Sıralama</small><RrAnchor trigger={(open, toggle) => <button type="button" className={`rr-trigger${open ? ' rr-on' : ''}`} onClick={toggle}><span>{currentSort[1]} · {currentSort[2] === 'down' ? 'Yeniden Eskiye' : 'Eskiden Yeniye'}</span><RrIcon name={currentSort[2]} /><RrIcon name="chev" /></button>}>{close => <div className="rr-pop rr-right" role="listbox" style={{ minWidth: 250 }}>{sortOptions.map(([value, label, direction]) => { const on = value === approvedSort; return <button key={value} type="button" className={`rr-opt${on ? ' rr-on' : ''}`} role="radio" aria-checked={on} onClick={() => { setApprovedSort(value); setPageNumber(1); close() }}><span className="rr-radio" aria-hidden="true" /><span className="rr-grow">{label}</span><small>{direction === 'down' ? 'Yeniden Eskiye' : 'Eskiden Yeniye'}</small><RrIcon name={direction} /></button> })}</div>}</RrAnchor></div>}
        {sheetField('Kargo Firması', cargoFilter, cargoPopOptions, setCargoFilter)}
        {sheetField('Fatura Durumu', invoiceStatusFilter, invoicePopOptions, selectInvoiceStatus)}
        {sheetField('İade Sebebi', reasonFilter, reasonPopOptions, setReasonFilter)}
        {sheetField('İade Durumu', status, statusPopOptions, selectStatus)}
        <div className="rr-fbtns"><button type="button" className="rr-btn" onClick={clearFilters}>Temizle</button><button type="button" className="rr-btn rr-primary" onClick={applyFilters}>Filtrele</button></div>
      </div></RrFilterCard>
    </div>
    {notice && <NoticeToast key={`${noticeKind}:${notice}`} kind={noticeKind} message={notice} onClose={() => setNoticeState('')} durationMs={noticeSticky ? undefined : noticeKind === 'info' ? 7000 : 5500} />}
    <div className="rr-panel"><div className="rr-panel-h"><div><h2>{tabLabel}</h2><p>Toplam {totalCount.toLocaleString('tr-TR')} iade bilgisi</p></div><RrPageSize value={pageSize} sizes={[20, 50, 100, 200]} onChange={setPageSize} /></div>
      <div className="rr-tblwrap">{query.isLoading ? <RrLoading /> : query.isError ? <RrError error={query.error} onRetry={() => void query.refetch()} /> : <table className={`rr-tbl${selectColumn ? ' rr-has-select' : ''}${noBuyer ? ' rr-no-buyer' : ''}`}>
        <colgroup><col className="rr-c-order" />{!noBuyer && <col className="rr-c-buyer" />}<col className="rr-c-prod" /><col className="rr-c-price" /><col className="rr-c-cargo" /><col className="rr-c-inv" /><col className="rr-c-reason" /><col className="rr-c-status" /><col className="rr-c-act" /></colgroup>
        <thead><tr>
          <ReturnHeadCell label="Sipariş Bilgileri" active={selectedPlatforms.length > 0} extra={selectColumn ? <input type="checkbox" className="rr-cb" checked={allBulkSelected} onChange={() => setBulkIds(allBulkSelected ? [] : bulkItems.map(claim => claim.id))} aria-label="Tüm iadeleri seç" style={{ marginRight: 8 }} /> : undefined} pop={() => <ReturnPlatformPop options={platformOptions} selected={selectedPlatforms} onChange={setSelectedPlatforms} />} />
          {!noBuyer && <ReturnHeadCell label="Alıcı" />}
          <ReturnHeadCell label="Bilgiler" />
          <ReturnHeadCell label="Birim Fiyat" />
          <ReturnHeadCell label="Kargo" active={cargoFilter !== 'ALL'} pop={close => <ReturnColumnPop label="Kargo Firması" options={cargoPopOptions} value={cargoFilter} onPick={value => { setCargoFilter(value); close() }} />} />
          <ReturnHeadCell label="Fatura" active={invoiceStatusFilter !== 'ALL'} pop={close => <ReturnColumnPop label="Fatura Durumu" options={invoicePopOptions} value={invoiceStatusFilter} onPick={value => { selectInvoiceStatus(value); close() }} />} />
          <ReturnHeadCell label="İade Sebebi" active={reasonFilter !== 'ALL'} pop={close => <ReturnColumnPop label="İade Sebebi" options={reasonPopOptions} value={reasonFilter} right onPick={value => { setReasonFilter(value); close() }} />} />
          <ReturnHeadCell label="Durum" active={status !== 'ALL'} pop={close => <ReturnColumnPop label="İade Durumu" options={statusPopOptions} value={status} right onPick={value => { selectStatus(value); close() }} />} />
          <ReturnHeadCell label="İşlemler" />
        </tr></thead>
        <tbody>{visibleItems.map(item => <ReturnReferenceRow key={item.rowKey ?? item.id} selectColumn={selectColumn} noBuyer={noBuyer} selection={bulkEligible(item) ? { checked: bulkIds.includes(item.id), onChange: () => setBulkIds(current => current.includes(item.id) ? current.filter(id => id !== item.id) : [...current, item.id]) } : undefined} item={item} order={null} onNotice={setNotice} />)}{!visibleItems.length && <tr><td className="rr-empty-row" colSpan={noBuyer ? 8 : 9}><div className="rr-empty"><span className="rr-ei"><RrIcon name="box" /></span><b>{summary.data?.unfilteredCount ? 'İade Bulunamadı' : 'Henüz İade Yok'}</b><span className="rr-lead">{summary.data?.unfilteredCount ? 'Seçili filtreyle eşleşen gösterilebilir iade yok.' : 'İadeleri eşitle düğmesiyle güncel talepleri çekin.'}</span>{summary.data?.unfilteredCount ? <button type="button" className="rr-btn rr-quiet" style={{ marginTop: 4 }} onClick={clearFilters}>Temizle</button> : null}</div></td></tr>}</tbody>
      </table>}</div>
      {!query.isLoading && !query.isError && visibleItems.length > 0 && <div className="rr-pager"><span>Toplam <b style={{ color: 'var(--rr-ink)' }}>{totalCount.toLocaleString('tr-TR')}</b> adet</span><div className="rr-pages"><button type="button" aria-label="Önceki" disabled={currentPage <= 1} onClick={() => setPageNumber(Math.max(1, currentPage - 1))}><RrIcon name="left" /></button>{pageList.map(page => <button key={page} type="button" className={page === currentPage ? 'rr-on' : undefined} aria-current={page === currentPage ? 'page' : undefined} onClick={() => setPageNumber(page)}>{page}</button>)}<button type="button" aria-label="Sonraki" disabled={currentPage >= totalPages} onClick={() => setPageNumber(Math.min(totalPages, currentPage + 1))}><RrIcon name="right" /></button></div><label className="rr-jump">Sayfaya Git<input className="rr-in" type="number" min={1} max={totalPages} placeholder={`1–${totalPages}`} aria-label="Sayfa numarası" value={jumpPage} onChange={event => setJumpPage(event.target.value)} onKeyDown={event => { if (event.key === 'Enter') { const page = Number(jumpPage); if (page >= 1) { setPageNumber(Math.min(totalPages, Math.floor(page))); setJumpPage('') } } }} /><button type="button" className="rr-btn" onClick={() => { const page = Number(jumpPage); if (page >= 1) { setPageNumber(Math.min(totalPages, Math.floor(page))); setJumpPage('') } }}>Git</button></label></div>}
    </div>
    {bulkSelected.length > 0 && <div className="rr-bulk" role="group" aria-label="Toplu işlemler"><span className="rr-grow">{bulkSelected.length} iade seçildi</span><button type="button" className="rr-btn rr-quiet" onClick={() => setBulkIds([])}>Vazgeç</button><button type="button" className="rr-btn rr-danger" onClick={() => setBulkMode('REJECT')}><RrIcon name="close" />Reddet</button><button type="button" className="rr-btn rr-primary" onClick={() => setBulkMode('APPROVE')}><RrIcon name="check" />Onayla</button></div>}
    {bulkMode && <ReturnBulkDecisionDialog mode={bulkMode} items={bulkSelected.map((claim): BulkDecisionItem => ({ id: claim.id, version: claim.version, orderNumber: displayOrderNumber(claim.orderNumber), customerName: claim.customerName, orderedAt: claim.orderedAt, packageNumber: claim.packageNumber, connectionId: claim.connectionId, platformCode: claim.platformCode, platformName: claim.platformDisplayName || returnPlatformName(claim.platformCode), reason: returnReasonSummary(claim), lines: (claim.lines ?? []).map(line => ({ id: line.id, title: line.title, imageUrl: line.imageUrl ?? claim.primaryImageUrl, quantity: productLineQuantity(line), sku: line.sku, barcode: formatMarketplaceBarcode(claim.platformCode, line.barcode) ?? line.barcode, color: returnLineFieldValue(line, ['renk', 'color'], 'color') ?? null, size: returnLineFieldValue(line, ['beden', 'size'], 'size') ?? null, modelCode: line.modelCode ?? null, reason: returnLineFieldValue(line, ['sebep', 'reason'], 'reasonText') })) }))} onClose={() => { setBulkMode(null); setBulkIds([]) }} onDone={(message, kind, details, target) => notifyReturn((text, tone) => setNotice(text, tone, tone !== 'success'), message, kind, details, target, { title: `Toplu İade ${bulkMode === 'APPROVE' ? 'Kabulü' : 'Reddi'} ${kind === 'success' ? 'İletildi' : kind === 'error' ? 'İletilemedi' : 'Kısmen Tamamlandı'}`, operation: bulkMode === 'APPROVE' ? 'Toplu iade kabulü' : 'Toplu iade reddi', platformCode: new Set(bulkSelected.map(claim => claim.platformCode)).size === 1 ? bulkSelected[0]?.platformCode : undefined })} />}
  </section>
}
// The separate detail route (/returns/:id), as the proposal's detail page.
export function ReturnDetailPage() {
  const { id = '' } = useParams(); const client = useQueryClient(); const [notice, setNoticeState] = useState(''); const [noticeKind, setNoticeKind] = useState<ReturnNoticeKind>('info')
  function setNotice(message: string, kind: ReturnNoticeKind = 'info') { setNoticeState(message); setNoticeKind(kind) }
  useEffect(() => {
    if (!notice) return
    const timeout = window.setTimeout(() => setNoticeState(''), noticeKind === 'info' ? 7000 : 5500)
    return () => window.clearTimeout(timeout)
  }, [notice, noticeKind])
  const query = useQuery({ queryKey: ['return', id], queryFn: () => hubApi<ReturnDetail>(`/returns/${id}`) })
  // Each stock decision is its own request: a decision does not change the return's version, so a key
  // made from the version and the body would replay the first of two equal decisions (audit 2026-10-20 W11).
  // The key is kept per pending decision instead (sendStockDisposition): a resend after a lost answer replays it.
  const dispositionRequest = useMutation({ mutationFn: ({ body, lineId }: { body: string; lineId: string }) => sendStockDisposition(pendingDispositionKeys.current, id, lineId, body, key => hubApi(`/returns/${id}/stock-dispositions`, { method: 'POST', headers: { 'Idempotency-Key': key }, body })) })
  const pendingDispositionKeys = useRef<PendingDispositionKeys>(new Map())
  const dispositionInFlight = useRef(false)
  async function disposition(event: FormEvent<HTMLFormElement>, line: ReturnLine) {
    event.preventDefault()
    if (!query.data || dispositionInFlight.current) return
    dispositionInFlight.current = true
    const data = new FormData(event.currentTarget)
    try {
      const kind = String(data.get('disposition'))
      await dispositionRequest.mutateAsync({ lineId: line.id, body: JSON.stringify({ returnLineId: line.id, disposition: kind, quantity: Number(data.get('quantity')), reason: data.get('reason') }) })
      notifyReturn(setNotice, kind === 'PASS' ? 'İade stok kararı kaydedildi; ürün stoğa eklendi.' : 'İade stok kararı kaydedildi; stok değişmedi.', 'success', undefined, undefined, { ...returnNotificationContext(query.data), title: 'İade Stok Kararı Kaydedildi', operation: 'İade stok kararı' })
      await Promise.all([client.invalidateQueries({ queryKey: ['return', id] }), ...(kind === 'PASS' ? [client.invalidateQueries({ queryKey: ['products'] })] : [])])
    } catch (reason) { const failure = notificationError(reason, 'Stok kararı kaydedilemedi.'); notifyReturn(setNotice, failure.message, 'error', undefined, undefined, { ...returnNotificationContext(query.data), title: 'İade Stok Kararı Kaydedilemedi', operation: 'İade stok kararı', code: failure.code }) }
    finally { dispositionInFlight.current = false }
  }
  if (query.isLoading) return <section className="content f3 rr"><RrLoading /></section>; if (query.isError || !query.data) return <section className="content f3 rr"><RrError error={query.error} onRetry={() => void query.refetch()} /></section>; const item = query.data; const lines = item.lines ?? []; const marketplaceName = returnPlatformName(item.platformCode)
  const reason = returnReasonPresentation(item)
  const critical = Boolean(item.actionDueAt && new Date(item.actionDueAt).getTime() - Date.now() < 86_400_000)
  return <section className="content f3 rr"><div className="rr-dpage">
    <div className="rr-ph"><div><Link to="/returns" className="rr-btn rr-link"><RrIcon name="left" /> İadeler</Link><div className="rr-crumb" style={{ marginTop: 6 }}>İade Detayı · Claim {item.externalClaimId}</div><h1>{displayOrderNumber(item.orderNumber)}</h1><p>{item.customerName} · {formatMoney(item.orderAmount, item.currency)}</p></div><div className="rr-acts"><RrBadge label={titleTr(statusLabel(item.status))} /></div></div>
    {notice && <NoticeToast key={`${noticeKind}:${notice}`} kind={noticeKind} message={notice} onClose={() => setNoticeState('')} durationMs={noticeKind === 'info' ? 7000 : 5500} />}
    <div className="rr-two"><article><div className="rr-h3">Talep ve Süre</div><dl className="rr-kv"><dt>Platform Durumu</dt><dd>{titleTr(statusLabel(item.rawStatus))}</dd><dt>Neden</dt><dd>{reason.label}{reason.explanation && <> · {reason.explanation}</>}</dd><dt>Sipariş Tarihi</dt><dd className="rr-num">{returnDateText(item.orderedAt)}</dd><dt>Otomatik İşlem</dt><dd style={critical ? { color: 'var(--rr-warn)' } : undefined}>{returnRemaining(item.actionDueAt)}{item.actionDueAtEstimated && item.actionDueAt ? ' (tahmini)' : ''}</dd><dt>Kargo</dt><dd className="rr-num">{cargoLabel(item.cargoProviderName)} · {item.cargoTrackingNumber ?? '—'}</dd>{returnAmountRows(item.returnAmounts).map(row => <Fragment key={row.label}><dt>{row.label}</dt><dd><ReturnAmountValue value={row.value} money={value => formatMoney(value, item.currency)} /></dd></Fragment>)}</dl></article>
      <article><div className="rr-h3">{marketplaceName} İade Kararı</div>{item.decisionPending ? <ReturnInlineActions item={item} onNotice={setNotice} /> : <>{item.allowedActions.includes('RECEIVE') && <ReturnReceiptButton item={item} onNotice={setNotice} />}{item.allowedActions.includes('APPROVE') || item.allowedActions.includes('REJECT') ? <ReturnDecisionActions item={item} onNotice={setNotice} /> : !item.allowedActions.includes('RECEIVE') && <div className="rr-callout"><RrIcon name="info" /><span><b>İade Aksiyonu Uygun Değil.</b> Pazaryerindeki iade durumu bu kayıtta henüz onay veya ret işlemine izin vermiyor.</span></div>}</>}</article></div>
    {item.platformCode === 'TRENDYOL' && <ReturnMarketplaceHistory returnId={item.id} lines={lines} />}
    {/* Stock intake: decided at approval, or afterwards here while units of an approved return are still undecided
        (the server accepts stock decisions only for approved lines, so a line approved on its own takes stock while the others wait). A rejected return has nothing to put back. */}
    {(item.stockDispositionAvailable ? lines.some(line => line.remainingQuantity > 0) : returnGroup(item.status) !== 'REJECTED' && returnGroup(item.status) !== 'APPROVED') && <>
    <article><div className="rr-h3">İade Paketi Ürünleri ve Stok Kabulü</div><div className="rr-callout rr-warn"><RrIcon name="alert" /><span><b>Stok otomatik artmaz.</b> Ürün fiziksel olarak kontrol edildikten sonra “Satılabilir” seçilirse stok artar; Karantina, Hasarlı ve Teslim Alınmadı seçimleri stoğu artırmaz.</span></div>
      {!lines.length ? <div className="rr-empty"><span className="rr-ei"><RrIcon name="box" /></span><b>İade Satırı Bulunamadı</b></div> : lines.map(line => <div className="rr-disp" key={line.id}><RrThumb src={line.imageUrl ?? item.primaryImageUrl} quantity={line.quantity} large alt={line.title} /><span className="rr-ptext"><b>{line.title}</b><small>{line.sku}{line.barcode ? ` · ${line.barcode}` : ''}</small><small>{line.quantity} adet · kalan karar {line.remainingQuantity}</small></span>{(line.stockDispositionAvailable ?? item.stockDispositionAvailable) && line.remainingQuantity > 0 ? <form onSubmit={event => disposition(event, line)}><select className="rr-in" name="disposition" aria-label="Stok kabul kararı"><option value="PASS">Satılabilir — Stoğa Ekle</option><option value="QUARANTINE">Karantina — Stoğa Ekleme</option><option value="DAMAGED">Hasarlı — Stoğa Ekleme</option><option value="NOT_RECEIVED">Teslim Alınmadı</option></select><input className="rr-in rr-num" name="quantity" type="number" min="0.01" max={line.remainingQuantity} step="0.01" defaultValue={line.remainingQuantity} required aria-label="Adet" /><input className="rr-in" name="reason" placeholder="Kontrol notu" required aria-label="Kontrol notu" /><button type="submit" className="rr-btn rr-primary" disabled={dispositionRequest.isPending}>{dispositionRequest.isPending ? 'Kaydediliyor…' : 'Kararı Kaydet'}</button></form> : <div style={{ gridColumn: 2, display: 'flex', flexWrap: 'wrap', gap: 8 }}><RrBadge label={line.remainingQuantity === 0 ? 'Tamamlandı' : line.status?.toUpperCase() === 'REJECTED' ? 'Reddedildi' : line.hasInventoryMapping ? 'Onay Bekliyor' : 'Stok Eşlemesi Gerekli'} /></div>}</div>)}
    </article>
    </>}
  </div></section>
}
