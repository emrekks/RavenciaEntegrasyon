import { describe, expect, it } from 'vitest'
import { addressLines } from './InvoiceWorkspacePreviewModal'

describe('invoice preview address', () => {
  it('prints the same fields in the same order as the fiscal payload', () => {
    const json = JSON.stringify({ addressId: '69707814-fd2a-4248-a325-5d346cc40df2', address: 'Ahmet Sarısaman caddesi no:4', district: 'LADİK', city: 'Konya', countryCode: 'TR', postalCode: '42440' })
    expect(addressLines(json)).toEqual(['Ahmet Sarısaman caddesi no:4', 'LADİK', 'Konya', 'TR', '42440'])
  })

  it('strips an address id that prefixes the street text', () => {
    expect(addressLines(JSON.stringify({ address: '591f7cbf-f1fa-4ba5-9ace-6fda660c6558, Onur Mahallesi 45030 Sokak No 26', city: 'Adana' }))).toEqual(['Onur Mahallesi 45030 Sokak No 26', 'Adana'])
  })

  it('uses only one street field like the payload does and finds nested addresses', () => {
    expect(addressLines(JSON.stringify({ invoiceAddress: { fullAddress: 'Tam adres', address1: 'Kısa adres', city: 'Adana' } }))).toEqual(['Tam adres', 'Adana'])
  })
})
