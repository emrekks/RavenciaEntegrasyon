import { cargoLabel } from '../../../shared/components'
import { formatPackageLabel } from '../../../shared/package-label'
import { statusLabel } from '../../../shared/status-labels'
import { titleTr } from '../returns-ui'
import { displayOrderNumber, productLineQuantity, returnPlatformName, type OrderLine } from '../shared'

export type ReturnLabelSource = {
  externalClaimId: string
  orderNumber: string
  status: string
  customerName: string
  cargoProviderName: string | null
  cargoTrackingNumber: string | null
  packageNumber: string | null
  platformCode: string
}

// What a return label says, built once: the preview in the dialog and the printed page show the same fields, in the
// same order, with the same Title Case names (they had drifted apart: the print had lowercase names and Paket No, the
// preview Müşteri).
export function returnLabelContent(item: ReturnLabelSource, lines: OrderLine[]) {
  const packageLabel = formatPackageLabel(item.packageNumber)?.trim() || ''
  const trackingNumber = item.cargoTrackingNumber?.trim() || ''
  const barcodeValue = trackingNumber || packageLabel || item.externalClaimId
  return {
    eyebrow: `${returnPlatformName(item.platformCode)} · İade Paketi`,
    title: `Sipariş ${displayOrderNumber(item.orderNumber)}`,
    status: titleTr(statusLabel(item.status)),
    fields: [
      ['İade Kayıt No', item.externalClaimId],
      ['Müşteri', item.customerName || '—'],
      ['Kargo Firması', cargoLabel(item.cargoProviderName)],
      ['Kargo Takip No', trackingNumber || 'Henüz İletilmedi'],
      ['Paket No', packageLabel || '—']
    ] as Array<[string, string]>,
    barcodeValue,
    products: lines.map(line => ({ key: line.id, text: `${line.title} · ${productLineQuantity(line)} adet${line.sku ? ` · ${line.sku}` : ''}` }))
  }
}
