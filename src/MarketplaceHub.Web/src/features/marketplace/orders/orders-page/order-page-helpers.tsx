import { useState } from 'react'
import { ApiRequestError } from '../../../../shared/api'
import { RrIcon } from '../../../../shared/ui/rr-components'
import { notificationLinks, type NotificationContext } from '../../../../shared/notifications'
import { IntegrationFeedback, Order, displayOrderNumber } from '../../shared'
import { personName } from '../../../../shared/person-name'
import type { ApiAnswer } from '../../../../shared/api/api-answer'

export const invoiceCreationDisabledHelp = 'Fatura oluşturmak için Entegrasyonlar > bu bağlantı ayarlarından “Fatura oluşturma” seçeneğini açın.'
export type ProductImagePreview = { url: string; fallbackUrl?: string | null; fallbackUrls?: string[]; title: string; modelCode?: string | null; variant?: string | null }

const invoiceCreationStatuses = new Set(['FATURA_BEKLIYOR', 'FATURA_INCELENECEK', 'FATURA_REDDEDILDI', 'FATURA_ISLENIYOR'])
export function isInvoicePending(item: Pick<Order, 'invoiceStatus'>) { return invoiceCreationStatuses.has(item.invoiceStatus) }
// No cargo label before the invoice: for a micro export (its export invoice first) and for an order whose export status the
// one rule could not determine (MicroExportRule, Belirsiz): its invoice waits for the owner's review first (review D-1).
export const microExportLabelBlockedText = 'Mikro ihracat faturası kesilmeden kargo etiketi yazdırılamaz.'
export const unknownExportLabelBlockedText = 'Siparişin ihracat durumu belirlenemedi; fatura incelenmeden kargo etiketi yazdırılamaz.'
export function labelBlockedByInvoiceReason(item: Pick<Order, 'isMicroExport' | 'invoiceStatus' | 'marks'>) {
  if (!isInvoicePending(item)) return null
  return item.isMicroExport ? microExportLabelBlockedText : item.marks?.microExportUnknownReason ? unknownExportLabelBlockedText : null
}
export function isMicroExportLabelBlocked(item: Pick<Order, 'isMicroExport' | 'invoiceStatus' | 'marks'>) { return labelBlockedByInvoiceReason(item) !== null }

export type OrderDetail = ApiAnswer<'getOrdersById'>
// The server could not confirm an action yet (it reads the marketplace and updates the panel) or
// queued it because the marketplace quota was full: neither is a failure, and neither should be resent.
export function isPendingAction(error: unknown): error is ApiRequestError {
  return error instanceof ApiRequestError && (error.code === 'EXTERNAL_EFFECT_VERIFYING' || error.code === 'ACTION_QUEUED')
}
export type IntegrationFeedbackHandler = (message: string, kind: IntegrationFeedback['kind'] | 'pending', context?: NotificationContext) => void
// The order a notice is about: its number (opening the list on it), its marketplace and store.
export function orderNotificationContext(item: Pick<Order, 'orderNumber' | 'platformCode' | 'connectionId' | 'platformDisplayName'>): NotificationContext {
  return { platformCode: item.platformCode, connectionId: item.connectionId ?? undefined, storeName: item.platformDisplayName || undefined, subject: { kind: 'order', id: item.orderNumber, label: displayOrderNumber(item.orderNumber), href: notificationLinks.order(item.orderNumber.trim().replace(/^#+/u, '')) } }
}

function meaningfulText(value: unknown): value is string { return typeof value === 'string' && /[\p{L}\p{N}]/u.test(value) }
// Some marketplaces (N11) send the street line on its own and the district and
// city as separate fields; the label and address views append the missing ones.
function completeAddressLine(fullAddress: string, data: Record<string, unknown>) { const lowered = fullAddress.toLocaleLowerCase('tr-TR'); const missing = [data.district, data.city].filter(meaningfulText).map(item => item.trim()).filter(item => !lowered.includes(item.toLocaleLowerCase('tr-TR'))); return missing.length ? [fullAddress, missing.join(' / ')].join(' · ') : fullAddress }

export function addressDataText(data: Record<string, unknown>, platformCode: string) { const fullAddress = typeof data.fullAddress === 'string' ? data.fullAddress.trim() : ''; const pointPrefix = data.isDeliveryPoint === true ? `Teslimat noktası${typeof data.deliveryPointType === 'string' ? ` (${data.deliveryPointType})` : ''}: ` : ''; if (meaningfulText(fullAddress)) return pointPrefix + completeAddressLine(fullAddress, data); const seen = new Set<string>(); const parts = ['address1', 'address2', 'neighborhood', 'district', 'countyName', 'city', 'province', 'provinceCode', 'country', 'countryCode', 'zip', 'postalCode'].map(key => data[key]).filter(meaningfulText).map(item => item.trim()).filter(item => { const normalized = item.toLocaleLowerCase('tr-TR'); if (seen.has(normalized)) return false; seen.add(normalized); return true }); return parts.length ? pointPrefix + parts.join(' · ') : platformCode === 'SHOPIFY' ? 'Shopify adres bilgisi bulunamadı' : 'Trendyol adres bilgisini maskeledi' }

export function customerDisplayName(item: Order) {
  if (item.customerName && item.customerName !== '—' && item.customerName.trim() !== '') return personName(item.customerName)
  // The server already resolves the name from the customer, invoice and delivery snapshots.
  const shipName = item.shipmentAddress?.fullName
  return shipName && meaningfulText(shipName) ? personName(shipName.trim()) : '—'
}

// The panel changes a package's carrier only on Trendyol (the server refuses the others with
// CARGO_PROVIDER_CHANGE_UNSUPPORTED), and only before the package is handed to the carrier.
export function canChangeOrderCargo(item: Pick<Order, 'platformCode' | 'derivedStatus'>) {
  return item.platformCode.trim().toUpperCase() === 'TRENDYOL' && !['PAYMENT_PENDING', 'SHIPPED', 'UNDELIVERED', 'DELIVERED', 'RETURNED', 'RETURN_IN_TRANSIT', 'CANCELLED'].includes(item.derivedStatus.trim().toUpperCase())
}

// A checkbox with its explanation. On phones the explanation stays folded behind an info button and opens on a tap.
export function RrHintCheck({ checked, onChange, label, hint }: { checked: boolean; onChange: (value: boolean) => void; label: string; hint: string }) {
  const [hintOpen, setHintOpen] = useState(false)
  return <><label className="rr-cklabel"><input type="checkbox" className="rr-cb" checked={checked} onChange={event => onChange(event.target.checked)} />{label}<button type="button" className="rr-hint-btn" aria-label="Açıklamayı göster" aria-expanded={hintOpen} onClick={event => { event.preventDefault(); setHintOpen(open => !open) }}><RrIcon name="info" size={15} /></button></label><p className={`rr-lead rr-hint${hintOpen ? ' rr-on' : ''}`} style={{ marginTop: -8 }}>{hint}</p></>
}
