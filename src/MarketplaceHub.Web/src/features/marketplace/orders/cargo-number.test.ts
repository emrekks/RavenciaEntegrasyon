import { describe, expect, it } from 'vitest'
import { cargoNumber } from './cargo-number'

describe('cargo number of an order row', () => {
  it('shows the cargo barcode Hepsiburada gave an Open package before any tracking number', () => {
    expect(cargoNumber({ cargoTrackingNumber: null, cargoBarcode: '62700000000002' })).toEqual({ value: '62700000000002', isBarcode: true })
  })

  it('lets the tracking number win once it comes, also when it differs from the barcode', () => {
    expect(cargoNumber({ cargoTrackingNumber: '6520000000001', cargoBarcode: '62200000000001' })).toEqual({ value: '6520000000001', isBarcode: false })
    expect(cargoNumber({ cargoTrackingNumber: null, cargoBarcode: '62200000000001' }, '6520000000001')).toEqual({ value: '6520000000001', isBarcode: false })
  })

  it('gives nothing when the marketplace gave neither, so the waiting text stays', () => {
    expect(cargoNumber({ cargoTrackingNumber: null, cargoBarcode: null })).toBeNull()
    expect(cargoNumber({ cargoTrackingNumber: ' ', cargoBarcode: '' })).toBeNull()
    expect(cargoNumber(undefined)).toBeNull()
  })
})
