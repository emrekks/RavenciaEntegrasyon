import { describe, expect, it } from 'vitest'
import type { ShippingLabelBlock } from '../shipping'
import { shippingLabelBlockParts } from './shipping-label-preview'

const block = (patch: Partial<ShippingLabelBlock>): ShippingLabelBlock => ({ id: 'b', kind: 'address', title: 'Teslimat adresi', fields: ['customerName', 'address'], align: 'left', text: '', ...patch })

describe('label designer preview follows the print rules', () => {
  it('shows the address block as title, recipient name, then the address lines', () => {
    expect(shippingLabelBlockParts(block({ fields: ['address', 'customerName'] }), { showCustomerPhone: true })).toEqual([
      { type: 'title', text: 'Teslimat adresi' },
      { type: 'field', field: 'customerName' },
      { type: 'lines', field: 'address' }
    ])
  })

  it('hides the customer contact unless the settings allow it', () => {
    const info = block({ kind: 'orderInfo', fields: ['orderNumber', 'customerEmail'] })
    expect(shippingLabelBlockParts(info, { showCustomerPhone: false })).toEqual([{ type: 'field', field: 'orderNumber' }])
    expect(shippingLabelBlockParts(info, { showCustomerPhone: true })).toHaveLength(2)
  })

  it('draws a barcode block’s barcode first and then its other fields', () => {
    expect(shippingLabelBlockParts(block({ kind: 'trackingBarcode', fields: ['trackingNumber', 'cargoProvider'] }), { showCustomerPhone: false })).toEqual([
      { type: 'barcode', field: 'trackingNumber' },
      { type: 'field', field: 'cargoProvider' }
    ])
  })

  it('prints the sender address as lines and a custom block with its title and text', () => {
    expect(shippingLabelBlockParts(block({ kind: 'sender', fields: ['senderName', 'senderAddress'], text: 'İade adresi' }), { showCustomerPhone: false })).toEqual([
      { type: 'text', text: 'İade adresi' },
      { type: 'field', field: 'senderName' },
      { type: 'lines', field: 'senderAddress' }
    ])
    expect(shippingLabelBlockParts(block({ kind: 'custom', title: 'Not', fields: [], text: 'Kırılabilir' }), { showCustomerPhone: false })).toEqual([
      { type: 'title', text: 'Not' },
      { type: 'text', text: 'Kırılabilir' }
    ])
  })
})
