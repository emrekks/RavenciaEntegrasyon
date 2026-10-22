import { describe, expect, it } from 'vitest'
import { bandRange, commissionTariffPlatforms, nextBandHint, rateDisagrees, rateText } from './CommissionTariffsPanel'

describe('commission tariff presentation', () => {
  it('writes each band as the price range it covers', () => {
    expect(bandRange({ tier: 1, min: 470.64, max: null, rate: 21.5 })).toBe('≥ 470,64 ₺')
    expect(bandRange({ tier: 2, min: 435.98, max: 470.63, rate: 20.6 })).toBe('435,98 – 470,63 ₺')
    expect(bandRange({ tier: 4, min: null, max: 388.63, rate: 17.5 })).toBe('≤ 388,63 ₺')
    expect(rateText(21.5)).toBe('%21,5')
  })

  it('names the price for the next lower rate, and says when there is none or the price is in no band', () => {
    expect(nextBandHint({ basisPrice: 519.75, currentTier: 1, nextLowerBand: { tier: 2, min: 435.98, max: 470.63, rate: 20.6 } })).toBe('%20,6 için ≤ 470,63 ₺')
    expect(nextBandHint({ basisPrice: 300, currentTier: 4, nextLowerBand: null })).toBe('En düşük komisyon bandında')
    expect(nextBandHint({ basisPrice: 450, currentTier: null, nextLowerBand: null })).toBe('Fiyat hiçbir banda düşmüyor')
    expect(nextBandHint({ basisPrice: null, currentTier: null, nextLowerBand: null })).toBe('Komisyona esas fiyat yok')
  })

  it('flags a file rate that differs from the band the price falls in', () => {
    expect(rateDisagrees({ currentRate: 21.5, currentBandRate: 21.5 })).toBe(false)
    expect(rateDisagrees({ currentRate: 20.6, currentBandRate: 21.5 })).toBe(true)
    expect(rateDisagrees({ currentRate: null, currentBandRate: 21.5 })).toBe(false)
  })

  it('shows the tariffs only where the panel reads the export', () => {
    expect(commissionTariffPlatforms.has('TRENDYOL')).toBe(true)
    expect(commissionTariffPlatforms.has('HEPSIBURADA')).toBe(false)
  })
})
