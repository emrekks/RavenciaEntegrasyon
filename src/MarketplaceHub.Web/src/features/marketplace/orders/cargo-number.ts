import type { Shipment } from '../shared'

// What the order row shows as the parcel's number. The carrier's tracking number wins; before it comes, the cargo
// barcode the marketplace gave the package (Hepsiburada gives it while the package is still Open: live 5543873658,
// 14 Nov 2026). The barcode is not a tracking number (for Aras Kargo the two differ), so it is named as a barcode in
// the tooltip and never used for tracking links or labels. Neither given: null, and the row keeps its waiting text.
export type CargoNumber = { value: string; isBarcode: boolean }

export const CARGO_BARCODE_TITLE = 'Hepsiburada’nın verdiği kargo barkodu; kargo takip numarası kargoya verildiğinde gelir'

export function cargoNumber(shipment: Pick<Shipment, 'cargoTrackingNumber' | 'cargoBarcode'> | null | undefined, orderTrackingNumber?: string | null): CargoNumber | null {
  const tracking = shipment?.cargoTrackingNumber?.trim() || orderTrackingNumber?.trim()
  if (tracking) return { value: tracking, isBarcode: false }
  const barcode = shipment?.cargoBarcode?.trim()
  return barcode ? { value: barcode, isBarcode: true } : null
}
