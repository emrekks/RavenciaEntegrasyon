import { describe, expect, it } from 'vitest'
import { shippingLabelAddressLines, spacedAddressText } from './shipping-label-address'

// Made-up addresses only.
describe('label address lines', () => {
  it('prints street, neighbourhood and district / city on lines of their own, as the Trendyol sticker does', () => {
    expect(shippingLabelAddressLines({ address1: 'Deneme Caddesi Örnek Sitesi Dış Kapı: D-12 Daire: 4', neighborhood: 'Örnek Mah', district: 'Merkez', city: 'İzmir' })).toEqual(['Deneme Caddesi Örnek Sitesi Dış Kapı: D-12 Daire: 4', 'Örnek Mah', 'Merkez / İzmir'])
  })

  it('takes the street part of a Trendyol fullAddress (address1, a run of spaces, district and city) when address1 is missing', () => {
    expect(shippingLabelAddressLines({ fullAddress: 'Deneme Mah. 1234 Sok. No:5     Merkez İzmir', district: 'Merkez', city: 'İzmir' })).toEqual(['Deneme Mah. 1234 Sok. No:5', 'Merkez / İzmir'])
  })

  it('never runs two words together: a space comes back between address words typed as one', () => {
    expect(spacedAddressText('A BLOKDAİRE 5')).toBe('A BLOK DAİRE 5')
    expect(spacedAddressText('No 12DAİRE 3')).toBe('No 12 DAİRE 3')
    expect(spacedAddressText('Kat 2   Daire  7')).toBe('Kat 2 Daire 7')
    // ordinary words that merely contain an address word stay whole
    expect(spacedAddressText('Mahmutbey Nokta Sokak')).toBe('Mahmutbey Nokta Sokak')
  })

  it('skips a line that would repeat the one before it, and falls back to the text when no fields came', () => {
    expect(shippingLabelAddressLines({ address1: 'Deneme Sok. 3', neighborhood: 'Deneme Sok. 3', district: 'Merkez', city: 'Merkez' })).toEqual(['Deneme Sok. 3', 'Merkez'])
    expect(shippingLabelAddressLines(null, 'Deneme Sok. 3 · Merkez / İzmir')).toEqual(['Deneme Sok. 3', 'Merkez / İzmir'])
    expect(shippingLabelAddressLines({ isDeliveryPoint: true, deliveryPointType: 'Locker', address1: 'Deneme AVM', district: 'Merkez', city: 'İzmir' })).toEqual(['Teslimat Noktası (Locker)', 'Deneme AVM', 'Merkez / İzmir'])
  })
})
