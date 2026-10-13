import { describe, expect, it } from 'vitest'
import { addressLines } from './InvoiceWorkspacePreviewModal'

describe('invoice preview address', () => {
  it('leaves marketplace address identifiers out of the printed address', () => {
    const json = JSON.stringify({ addressId: '69707814-fd2a-4248-a325-5d346cc40df2', address: 'Ahmet Sarısaman caddesi no:4', district: 'LADİK', city: 'Konya', countryCode: 'TR', postalCode: '42440' })
    expect(addressLines(json)).toEqual(['Ahmet Sarısaman caddesi no:4', 'LADİK', 'Konya', 'TR', '42440'])
  })

  it('drops a bare GUID even when its key looks like an address field', () => {
    expect(addressLines(JSON.stringify({ fullAddress: '69707814-fd2a-4248-a325-5d346cc40df2', city: 'Konya' }))).toEqual(['Konya'])
  })
})
