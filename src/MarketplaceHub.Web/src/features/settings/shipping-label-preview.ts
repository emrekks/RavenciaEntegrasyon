import type { ShippingLabelBlock, ShippingLabelField } from '../shipping'

// What one label block shows, in order. The label designer's preview follows the same rules as the printed label
// (OrdersPage's renderLabelBlock, after the S1 fix): a barcode block draws its barcode then its other fields; the
// delivery address block shows its title, then the recipient's name, then the address lines; the other blocks show
// their text and fields. Address fields print as plain lines without a caption, every other field as caption + value.
// The customer contact field shows only when the settings allow it (barcode blocks print every field they hold).
export type ShippingPreviewPart =
  | { type: 'barcode'; field: 'trackingNumber' | 'packageNumber' }
  | { type: 'title'; text: string }
  | { type: 'text'; text: string }
  | { type: 'field'; field: ShippingLabelField }
  | { type: 'lines'; field: 'address' | 'senderAddress' }

export const addressBlockTitle = 'Teslimat Adresi'

function fieldPart(field: ShippingLabelField): ShippingPreviewPart {
  return field === 'address' || field === 'senderAddress' ? { type: 'lines', field } : { type: 'field', field }
}

export function shippingLabelBlockParts(block: ShippingLabelBlock, options: { showCustomerPhone: boolean }): ShippingPreviewPart[] {
  const barcodeField = block.kind === 'trackingBarcode' ? 'trackingNumber' : block.kind === 'packageBarcode' ? 'packageNumber' : null
  if (barcodeField && block.fields.includes(barcodeField)) {
    return [{ type: 'barcode', field: barcodeField }, ...block.fields.filter(field => field !== barcodeField).map(fieldPart)]
  }
  const fields = block.fields.filter(field => options.showCustomerPhone || field !== 'customerEmail')
  const text: ShippingPreviewPart[] = block.text ? [{ type: 'text', text: block.text }] : []
  if (block.kind === 'address') {
    // Name first, then the address, then anything else the block holds.
    const ordered = [...fields.filter(field => field === 'customerName'), ...fields.filter(field => field === 'address'), ...fields.filter(field => field !== 'customerName' && field !== 'address')]
    return [{ type: 'title', text: addressBlockTitle }, ...ordered.map(fieldPart)]
  }
  if (block.kind === 'orderInfo' || block.kind === 'sender') return [...text, ...fields.map(fieldPart)]
  return [{ type: 'title', text: block.title }, ...text, ...fields.map(fieldPart)]
}
