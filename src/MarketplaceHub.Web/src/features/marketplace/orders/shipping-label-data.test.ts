import { describe, expect, it } from 'vitest'
import { labelPersonalData, loadOrderLabelDetails, type OrderLabelDetail } from './shipping-label-data'

const listAddress = { fullName: 'Liste Adı', fullAddress: 'Liste adresi', address1: null, address2: null, neighborhood: null, district: null, city: 'Ankara', postalCode: null, countryCode: 'TR', isDeliveryPoint: false, deliveryPointType: null }
const detailAddress = { ...listAddress, fullAddress: 'Detay adresi', city: 'İzmir' }

describe('shipping label personal data', () => {
  it('prints the e-mail, name and address from the order detail', () => {
    const detail: OrderLabelDetail = { id: 'o1', customerName: 'Ayşe Yılmaz', customerEmail: ' ayse@example.com ', shipmentAddress: detailAddress }

    expect(labelPersonalData({ customerName: 'Liste Adı', shipmentAddress: listAddress }, detail)).toEqual({ customerName: 'Ayşe Yılmaz', shipmentAddress: detailAddress, customerEmail: 'ayse@example.com' })
  })

  it('keeps the list name and address when the detail has none, and never invents an e-mail', () => {
    const detail: OrderLabelDetail = { id: 'o1', customerName: '—', customerEmail: null, shipmentAddress: null }

    expect(labelPersonalData({ customerName: 'Liste Adı', shipmentAddress: listAddress }, detail)).toEqual({ customerName: 'Liste Adı', shipmentAddress: listAddress, customerEmail: '—' })
    expect(labelPersonalData({ customerName: 'Liste Adı', shipmentAddress: null }, undefined).customerEmail).toBe('—')
  })

  it('reads each selected order once, a few at a time', async () => {
    let running = 0
    let peak = 0
    const requested: string[] = []
    const details = await loadOrderLabelDetails(['a', 'b', 'c', 'a', 'd', 'e', 'f'], async id => {
      requested.push(id)
      running++
      peak = Math.max(peak, running)
      await new Promise(resolve => setTimeout(resolve, 5))
      running--
      return { id, customerName: `Alıcı ${id}`, customerEmail: `${id}@example.com` }
    }, 3)

    expect(requested.sort()).toEqual(['a', 'b', 'c', 'd', 'e', 'f'])
    expect(peak).toBe(3)
    expect(details.get('e')?.customerEmail).toBe('e@example.com')
  })
})
