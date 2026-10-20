// Code the marketplace routes share (orders, shipments, returns, integrations, mappings): types, helpers and the
// windows several screens open. Each route lives in its own module so it loads as its own chunk.
import { marketplaceProductSearchUrl } from '../questions/marketplace-product-link'
import { Fragment, type ReactNode } from 'react'
import { UiIcon } from '../../shared/components'
import { ValueBadge } from '../../shared/components/ValueBadge'
import { invoiceStatusDisplayLabel } from '../../shared/status-labels'
import './reference-filters.css'
import './order-invoice-cell.css'
import './orders-responsive.css'
import './marketplace-polish.css'
import { RrAnchor, RrIcon, RrLogo, RrRing, type RingTone } from './returns-ui'
import './orders-sade.css'
import { type NotificationDetail, type NotificationTarget } from '../../shared/notifications'
import { formatMarketplaceBarcode } from './return-reference-presentation'
import { formatDateTime, formatIstanbul } from '../../shared/format-date'

// supportedFilters: filters the endpoint applies itself (orders: 'labelPrinted' once the server keeps labelPrintedAt).
export type Page<T> = { items: T[]; nextCursor: string | null; hasMore: boolean; totalCount?: number | null; supportedFilters?: string[] }
export type Connection = { id: string; publicId: string; platformCode: string; environment: string; displayName: string; externalStoreId: string; status: string; apiVersion: string; lastTestedAt: string | null; lastSuccessAt: string | null; lastErrorCode: string | null; hasCredential: boolean; externalWritesEnabled: boolean; invoiceCreationEnabled: boolean; n11ShipmentTemplate?: string | null; n11PreparingDay?: number | null; version: number }
export type OrderLine = { id: string; sku: string; barcode: string | null; title: string; quantity?: number; orderedQuantity: number; cancelledQuantity: number; shippedQuantity: number; allocatedQuantity?: number; deliveredQuantity: number; returnedQuantity: number; unitPrice: number; vatRate: number; rawStatus: string; variantId: string | null; modelCode: string | null; optionSignature: string | null; imageUrl: string | null }
export type OrderInfoItem = { label: string; value: string; url?: string | null }
// The list carries only the delivery fields a label prints; full address snapshots are in the order detail.
export type OrderAddress = { fullName: string | null; fullAddress: string | null; address1: string | null; address2: string | null; neighborhood: string | null; district: string | null; city: string | null; postalCode: string | null; countryCode: string | null; isDeliveryPoint: boolean; deliveryPointType: string | null }
export type Order = { id: string; labelPrintedAt?: string | null; orderNumber: string; derivedStatus: string; currency: string; grossAmount: number; discountAmount: number; netAmount: number; orderedAt: string; shippedAt?: string | null; lineCount: number; packageCount: number; version: number; connectionId: string | null; platformCode: string; platformDisplayName: string; customerName: string; customerEmail: string | null; customerTaxOrIdentityNumber: string | null; orderType: string; isMicroExport: boolean; shipmentAddress: OrderAddress | null; shipmentDueAt: string | null; isDeadlineCritical: boolean; invoiceStatus: string; invoiceId: string | null; invoiceDocumentUrl: string | null; cargoProviderName: string | null; cargoTrackingNumber: string | null; primaryImageUrl: string | null; productQuantity: number; lines: OrderLine[] | null; packages: Shipment[] | null; invoiceCreationEnabled?: boolean; platformInfo?: OrderInfoItem[] | null }
export function ProductTitleLink({ platformCode, line }: { platformCode: string; line: { title: string; barcode?: string | null; sku?: string | null; modelCode?: string | null } }) {
  const href = marketplaceProductSearchUrl(platformCode, line.barcode, line.sku, line.modelCode, line.title)
  return href ? <a className="product-title-link" href={href} target="_blank" rel="noopener noreferrer" onClick={event => event.stopPropagation()}>{line.title}</a> : <>{line.title}</>
}
export function productLineQuantity(line: { quantity?: number; orderedQuantity?: number; cancelledQuantity?: number }) {
  const orderedQuantity = line.orderedQuantity ?? line.quantity ?? 0
  return Math.max(0, orderedQuantity - (line.cancelledQuantity ?? 0))
}
export type Shipment = { id: string; orderId: string; orderNumber: string; externalPackageId: string; status: string; rawStatus: string; cargoTrackingNumber: string | null; cargoProviderName: string | null; statusOccurredAt: string; version: number; isResend: boolean }
export type ReturnLine = { status?: string | null; id: string; externalLineId: string; orderLineId: string; sku: string; barcode: string | null; title: string; quantity: number; disposedQuantity: number; remainingQuantity: number; unitPrice: number; imageUrl: string | null; hasInventoryMapping: boolean; optionSignature?: string | null; modelCode?: string | null; color?: string | null; size?: string | null; approvedAt?: string | null; systemNote?: string | null; reasonText?: string | null; sellerDescription?: string | null; platformApprovalReason?: string | null; platformDescription?: string | null };
export type IntegrationFeedback = { kind: 'success' | 'error' | 'info'; message: string; details?: NotificationDetail[]; target?: NotificationTarget | null }
export function Empty({ children }: { children: string }) { return <div className="empty"><span className="empty-icon" aria-hidden="true"><UiIcon name="box" /></span><strong>Kayıt yok</strong><p>{children}</p></div> }
export function Badge({ value, tooltip }: { value: string; tooltip?: string }) { return <ValueBadge value={value} title={tooltip ?? value} statusClass /> }
export function DateText({ value }: { value: string | null }) { return <>{value ? formatDateTime(value) : '—'}</> }
export function usableShipmentDueAt(value: string | null) { if (!value) return null; const timestamp = new Date(value).getTime(); return Number.isFinite(timestamp) && timestamp >= Date.UTC(2000, 0, 1) ? value : null }
export function remainingText(value: string | null) {
  if (!value) return 'Termin bilgisi gelmedi'
  const ms = new Date(value).getTime() - Date.now()
  if (ms <= 0) return 'Süre doldu'
  const totalMinutes = Math.floor(ms / 60_000)
  const days = Math.floor(totalMinutes / (24 * 60))
  const hours = Math.floor((totalMinutes % (24 * 60)) / 60)
  const minutes = totalMinutes % 60
  if (days === 0 && hours === 0) return `${minutes} dakika kaldı`
  if (days === 0) return `0 gün ${hours} saat ${minutes > 0 ? `${minutes} dakika` : ''}`.trim()
  return `${days} gün ${hours} saat ${minutes > 0 ? `${minutes} dakika` : ''}`.trim()
}
export function displayOrderNumber(value: string) { const normalized = value.trim().replace(/^#+/, ''); return `#${normalized || '—'}` }
export function optionRows(value: string | null) { const parts = (value ?? '').split(/[|,·;]+/).map(item => item.trim()).filter(Boolean); const fields = parts.map(part => { const match = part.match(/^([^:=-]+)\s*[:=-]\s*(.+)$/); return match ? { label: match[1].trim(), value: match[2].trim() } : null }).filter((item): item is { label: string; value: string } => item !== null); return fields.length ? fields : value ? [{ label: 'Seçenek', value }] : [] }
export function returnLineField(line: OrderLine | ReturnLine, labels: string[], property?: keyof ReturnLine) { const extra = line as Partial<ReturnLine>; const direct = property ? String(extra[property] ?? '').trim() : ''; if (direct) return direct; const signature = (line as OrderLine & { optionSignature?: string | null }).optionSignature ?? null; return optionRows(signature).find(option => labels.some(label => option.label.toLocaleLowerCase('tr-TR').includes(label.toLocaleLowerCase('tr-TR'))))?.value ?? '—' }
export function returnOptionalValue(value: unknown) { const normalized = String(value ?? '').trim(); return normalized && normalized !== '—' ? normalized : null }
export function returnLineFieldValue(line: OrderLine | ReturnLine, labels: string[], property?: keyof ReturnLine) { return returnOptionalValue(returnLineField(line, labels, property)) }
export function orderDisplayShipment(item: Pick<Order, 'platformCode' | 'packages'>) { const packages = item.packages ?? []; if (item.platformCode !== 'SHOPIFY') return packages[0]; const visiblePackages = packages.filter(shipment => !(shipment.externalPackageId.startsWith('order:') && shipment.externalPackageId.endsWith(':remainder'))); return visiblePackages.find(shipment => Boolean(shipment.cargoTrackingNumber)) ?? visiblePackages.find(shipment => shipment.status !== 'CANCELLED') ?? visiblePackages[0] }
// Returns list row (docs/tasarim-onerileri/iadeler-yeni.html): order info, buyer, products with a unit price column whose
// prices line up with each product, cargo, invoice, reason, a status ring and the "İşlemler" menu.
export function returnDateText(value: string | null | undefined) { if (!value) return '—'; const date = new Date(value); return Number.isNaN(date.getTime()) ? value : formatIstanbul(date, { day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit' }) }
export const invoiceRing: Record<string, [RingTone, number]> = { 'Fatura Bekliyor': ['wait', 0.2], 'Fatura İletiliyor': ['move', 0.66], 'Faturası Mevcut': ['ok', 1], 'Fatura İletilemedi': ['bad', 0.66], 'Fatura Oluşturulamadı': ['bad', 0.33], 'Fatura İptal': ['off', 0] }
export const invoiceFailureText: Record<string, string> = { 'Fatura İletilemedi': 'Fatura pazar yerine iletilemedi. Ayrıntılar Faturalar sayfasında.', 'Fatura Oluşturulamadı': 'Fatura oluşturulamadı. Ayrıntılar Faturalar sayfasında.' }
export function ReturnInvoiceRing({ status, isMicroExport }: { status: string; isMicroExport: boolean }) {
  const base = invoiceStatusDisplayLabel(status, false)
  const label = invoiceStatusDisplayLabel(status, isMicroExport)
  const [tone, progress] = invoiceRing[base] ?? ['off', 0]
  // Short states ("Faturası Mevcut") stay on one line; long ones break only between word pairs, so
  // "Mikro İhracat Faturası Bekliyor" takes two lines in the narrow column instead of three.
  const words = label.split(' ')
  const shown = label.length <= 18 ? label.replace(/ /g, ' ') : words.length >= 4 ? words.reduce<string[]>((pairs, word, index) => index % 2 ? [...pairs.slice(0, -1), `${pairs[pairs.length - 1]}\u00a0${word}`] : [...pairs, word], []).join(' ') : label
  const ring = <RrRing tone={tone} progress={progress} label={shown} />
  // eslint-disable-next-line jsx-a11y/no-noninteractive-tabindex -- tooltip trigger: focusable so keyboards can read why the invoice failed
  return invoiceFailureText[base] ? <span className="rr-reason-tip" tabIndex={0}>{ring}<span className="rr-reason-pop" role="tooltip"><b>{label}</b>{invoiceFailureText[base]}</span></span> : ring
}
// Codes first (barcode, stock code, model code), then the variant (colour, size).
export function returnLineFacts(platformCode: string | null | undefined, line: OrderLine | ReturnLine): Array<[string, string]> {
  const facts: Array<[string, string | null | undefined]> = [['Barkod', formatMarketplaceBarcode(platformCode ?? '', line.barcode) ?? line.barcode], ['Stok Kodu', line.sku], ['Model Kodu', (line as { modelCode?: string | null }).modelCode], ['Renk', returnLineFieldValue(line, ['renk', 'color'], 'color')], ['Beden', returnLineFieldValue(line, ['beden', 'size'], 'size')]]
  return facts.filter((fact): fact is [string, string] => Boolean(fact[1]?.trim()))
}
// Colour and size share one line, split by a short divider.
export function ReturnFactLines({ facts }: { facts: Array<[string, string]> }) {
  const variant = facts.filter(([label]) => label === 'Renk' || label === 'Beden')
  return <>{facts.filter(([label]) => label !== 'Renk' && label !== 'Beden').map(([label, value]) => <small key={label}><span>{label}:</span> {value}</small>)}{variant.length > 0 && <small className="rr-pair">{variant.map(([label, value], index) => <Fragment key={label}>{index > 0 && <i className="rr-sep-v" aria-hidden="true" />}<span>{label}:</span>&nbsp;{value}</Fragment>)}</small>}</>
}
export function returnPlatformName(platformCode: string | null | undefined) {
  switch (platformCode?.toUpperCase()) {
    case 'HEPSIBURADA': return 'Hepsiburada'
    case 'N11': return 'N11'
    case 'PAZARAMA': return 'Pazarama'
    case 'SHOPIFY': return 'Shopify'
    case 'TRENDYOL': return 'Trendyol'
    default: return 'Pazar yeri'
  }
}
export type ReturnNoticeKind = 'success' | 'error' | 'info'
export function notificationKindFromMessage(message: string): ReturnNoticeKind {
  const normalized = message.toLocaleLowerCase('tr-TR')
  if (/(hata|başarısız|kaydedilemedi|güncellenemedi|silinemedi|oluşturulamadı|alınamadı|bulunamadı|zorunlu|seçin|geçersiz|uygulanamadı|okunamadı|yenilenemedi|eşlenemedi)/u.test(normalized)) return 'error'
  if (/(kuyruğa|işleniyor|bekleniyor|salt-okunur|değişiklik yok|henüz)/u.test(normalized)) return 'info'
  return 'success'
}
// Column filter popover: one radio list, "Filtreyi Temizle" once a value is picked.
export function ReturnColumnPop({ label, options, value, onPick, right, width = 240 }: { label: string; options: Array<{ value: string; label: string }>; value: string; onPick: (value: string) => void; right?: boolean; width?: number }) {
  const active = value !== 'ALL'
  return <div className={`rr-pop${right ? ' rr-right' : ''}`} role="dialog" style={{ minWidth: width }}><div className="rr-ph">{label}</div>{options.map((option, index) => { const on = active ? option.value === value : index === 0; return <button key={option.value} type="button" className={`rr-opt${on ? ' rr-on' : ''}`} role="radio" aria-checked={on} onClick={() => onPick(option.value)}><span className="rr-radio" aria-hidden="true" /><span className="rr-grow">{option.label}</span></button> })}{active && <div className="rr-foot"><span /><button type="button" className="rr-btn rr-quiet" style={{ height: 28 }} onClick={() => onPick('ALL')}>Filtreyi Temizle</button></div>}</div>
}
export function ReturnPlatformPop({ options, selected, onChange }: { options: Array<{ value: string; label: string }>; selected: string[]; onChange: (value: string[]) => void }) {
  const all = selected.length === 0
  const checked = (code: string) => all || selected.includes(code)
  const toggle = (code: string, on: boolean) => { const current = all ? options.map(option => option.value) : selected.filter(value => value !== RETURN_NO_PLATFORM); const next = on ? Array.from(new Set([...current, code])) : current.filter(value => value !== code); onChange(next.length === options.length ? [] : next.length === 0 ? [RETURN_NO_PLATFORM] : next) }
  return <div className="rr-pop" role="dialog" style={{ minWidth: 260 }}><div className="rr-ph">İade Platformu<button type="button" className="rr-btn rr-link" onClick={() => onChange(all ? [RETURN_NO_PLATFORM] : [])}>{all ? 'Tümünü Kaldır' : 'Tümünü Seç'}</button></div>{options.map(option => <label key={option.value} className="rr-opt"><input type="checkbox" className="rr-cb" checked={checked(option.value)} onChange={event => toggle(option.value, event.target.checked)} /><RrLogo code={option.value} /><span className="rr-grow">{option.label}</span></label>)}</div>
}
export const RETURN_NO_PLATFORM = '__NONE__'
export function ReturnHeadCell({ label, active, extra, pop }: { label: string; active?: boolean; extra?: ReactNode; pop?: (close: () => void) => ReactNode }) {
  return <th className={active ? 'rr-has-filter' : undefined}><span className="rr-hf">{extra}{label}{pop && <RrAnchor trigger={(_open, toggle) => <button type="button" className="rr-fbtn" onClick={toggle} aria-label={`${label} filtresini aç`}><RrIcon name="filter" /></button>}>{pop}</RrAnchor>}</span></th>
}
export type FieldState = 'done' | 'partial' | 'none'
// Status is read from the shape: a filled check when ready, a clock when values are still missing,
// an outlined plus when the field has no panel attribute yet.
export function MappingStateGlyph({ state, required = false }: { state: FieldState; required?: boolean }) {
  if (state === 'done') return <svg className="cm-glyph is-done" viewBox="0 0 16 16" aria-label="Tamam" role="img"><circle cx="8" cy="8" r="7" fill="currentColor" /><path d="m5 8.2 2.1 2.1 3.9-4.2" fill="none" stroke="var(--rv-color-surface)" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" /></svg>
  if (state === 'partial') return <svg className="cm-glyph is-partial" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" aria-label="Değerleri eksik" role="img"><circle cx="8" cy="8" r="6.2" opacity=".35" /><path d="M8 1.8a6.2 6.2 0 0 1 5.9 4.3" /><path d="M8 5.6v2.6l1.8 1.1" /></svg>
  return <svg className={`cm-glyph${required ? ' is-required' : ''}`} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" aria-label="Eşlenmedi" role="img"><circle cx="8" cy="8" r="6.2" strokeDasharray="2.6 2.2" /></svg>
}
