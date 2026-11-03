import { describe, expect, it } from 'vitest'
import { returnLabelContent } from './return-label'

const claim = { externalClaimId: 'CLM-1', orderNumber: '10001', status: 'APPROVED', customerName: 'Ayşe Yılmaz', cargoProviderName: 'Yurtiçi Kargo', cargoTrackingNumber: null, packageNumber: 'PK-77', platformCode: 'TRENDYOL' }
const line = { id: 'l1', title: 'Gömlek', quantity: 2, sku: 'SKU-9' } as never

describe('return label content', () => {
  it('names every field in Title Case, the same list for preview and print', () => {
    const label = returnLabelContent(claim, [line])
    expect(label.fields.map(([name]) => name)).toEqual(['İade Kayıt No', 'Müşteri', 'Kargo Firması', 'Kargo Takip No', 'Paket No'])
    for (const [name] of label.fields) expect(name.split(' ').every(word => word[0] === word[0].toLocaleUpperCase('tr-TR'))).toBe(true)
  })

  it('says the tracking number has not come yet and barcodes the package number instead', () => {
    const label = returnLabelContent(claim, [line])
    expect(Object.fromEntries(label.fields)['Kargo Takip No']).toBe('Henüz İletilmedi')
    expect(label.barcodeValue).toContain('PK-77')
    expect(label.products[0].text).toBe('Gömlek · 2 adet · SKU-9')
  })

  it('barcodes the tracking number once there is one', () => {
    expect(returnLabelContent({ ...claim, cargoTrackingNumber: ' 7330 ' }, []).barcodeValue).toBe('7330')
  })
})
