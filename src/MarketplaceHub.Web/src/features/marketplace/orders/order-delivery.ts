import { formatIstanbul } from '../../../shared/format-date'
import { onHoldOrderStatusText, overdueShipmentDays } from '../order-delivery-presentation'
import { orderDisplayShipment, remainingText, usableShipmentDueAt, type Order } from '../shared'

// What the delivery line says. The ring and the due line branch on `kind`, never on the label text, so a label's
// wording or casing can change without silently dropping the "kaldı" suffix, the red class or the shipped note.
export type DeliveryKind = 'cancelled' | 'delivered' | 'status' | 'shipping' | 'late' | 'remaining'
export type DeliveryInfo = { kind: DeliveryKind; label: string; value: string; note: string | null; overdue: boolean; lateDays?: number }

function longDate(value: string) { return formatIstanbul(new Date(value), { day: '2-digit', month: 'long', year: 'numeric', hour: '2-digit', minute: '2-digit' }) }

export function deliveryText(item: Order): DeliveryInfo {
  const shipment = orderDisplayShipment(item)
  const rawStatuses = [...(item.packages ?? []).map(value => value.rawStatus), ...(item.lines ?? []).map(value => value.rawStatus)]
  if (item.derivedStatus === 'CANCELLED') return { kind: 'cancelled', label: 'İptal Tarihi', value: shipment?.statusOccurredAt ? longDate(shipment.statusOccurredAt) : 'İptal zamanı bekleniyor', note: null, overdue: false }
  if (item.derivedStatus === 'DELIVERED') return { kind: 'delivered', label: 'Teslim Edildi', value: shipment?.statusOccurredAt ? longDate(shipment.statusOccurredAt) : 'Teslimat zamanı bekleniyor', note: null, overdue: false }
  if (item.derivedStatus === 'UNVERIFIED') return { kind: 'status', label: 'Sipariş Durumu', value: 'Durumu doğrulanmadı', note: 'Paket veya güncel sipariş yaşam döngüsü bilgisi bulunmadığından teslimat durumu doğrulanamadı.', overdue: false }
  if (item.derivedStatus === 'PAYMENT_PENDING') return { kind: 'status', label: 'Sipariş Durumu', value: 'Trendyol ödeme onayı bekleniyor', note: 'Ödeme onaylanana kadar paket işlemleri kullanılamaz.', overdue: false }
  if (item.platformCode.trim().toUpperCase() === 'HEPSIBURADA' && item.derivedStatus === 'UNDELIVERED') return { kind: 'status', label: 'Sipariş Durumu', value: onHoldOrderStatusText(item.platformCode, rawStatuses), note: null, overdue: false }
  if (['SHIPPED', 'UNDELIVERED'].includes(item.derivedStatus)) return { kind: 'shipping', label: 'Taşıma Durumunda', value: shipment?.statusOccurredAt ? longDate(shipment.statusOccurredAt) : 'Kargo zamanı bekleniyor', note: 'Kargoya teslim edildi', overdue: false }
  if (item.derivedStatus === 'ON_HOLD') return { kind: 'status', label: 'Sipariş Durumu', value: onHoldOrderStatusText(item.platformCode, rawStatuses), note: null, overdue: false }
  const dueAt = usableShipmentDueAt(item.shipmentDueAt)
  const overdueDays = overdueShipmentDays(item.derivedStatus, dueAt)
  if (overdueDays > 0) return { kind: 'late', label: '', value: `Siparişiniz ${overdueDays} gün gecikmiştir!`, note: 'Siparişinizi en kısa sürede kargoya teslim etmelisiniz.', overdue: true, lateDays: overdueDays }
  const urgent = Boolean(dueAt && new Date(dueAt).getTime() - Date.now() <= 86_400_000)
  return { kind: 'remaining', label: 'Kalan Süre', value: dueAt ? remainingText(dueAt) : item.isMicroExport ? 'Trendyol termin bilgisi göndermedi' : 'Termin zamanı bekleniyor', note: null, overdue: urgent }
}

// The line under the ring: time left, days late, or when the order was shipped, delivered or cancelled.
export function orderRingDetail(delivery: DeliveryInfo): { text: string; cls?: string } {
  if (delivery.kind === 'late') return { text: `${delivery.lateDays ?? 0} gün gecikti`, cls: 'bad' }
  if (delivery.kind === 'remaining') return { text: /kaldı|doldu|bekleniyor|göndermedi/u.test(delivery.value) ? delivery.value : `${delivery.value} kaldı`, cls: delivery.overdue ? 'crit' : undefined }
  if (delivery.kind === 'shipping') return { text: delivery.note ?? delivery.value }
  return { text: delivery.value }
}

// The time-left line under the order: shown while there is a deadline to meet or the order is late.
export function deliveryDueLine(delivery: DeliveryInfo) {
  return delivery.kind === 'remaining' || delivery.kind === 'late' || delivery.overdue ? orderRingDetail(delivery) : null
}
