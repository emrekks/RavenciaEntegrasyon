import { describe, expect, it } from 'vitest'
import { bandRange, commissionTariffPlatforms, nextBandHint, ownChoice, pickedTier, rateDisagrees, rateText, remainingText } from './CommissionTariffsPanel'

const bands = [{ tier: 1, min: 470.64, max: null, rate: 21.5 }, { tier: 2, min: 435.98, max: 470.63, rate: 20.6 }, { tier: 3, min: 388.64, max: 435.97, rate: 19.4 }, { tier: 4, min: null, max: 388.63, rate: 17.5 }]

describe('commission tariff picks', () => {
  const row = { choices: [{ tier: 1, price: 519.75, rate: 21.5, commission: 111.75, remaining: 248 }, { tier: 2, price: 470.63, rate: 20.6, commission: 96.95, remaining: 213.68 }], recommendedTier: 1 }

  it('takes a row its own pick, else the pick for every row, and leaves it out when neither applies', () => {
    expect(pickedTier(row, { tier: 2, price: null }, 'recommended')).toBe(2)
    expect(pickedTier(row, { tier: null, price: 480 }, '')).toBe('own')
    expect(pickedTier(row, { tier: null, price: null }, 'recommended')).toBeNull()
    expect(pickedTier(row, undefined, 'recommended')).toBe(1)
    expect(pickedTier(row, undefined, '2')).toBe(2)
    expect(pickedTier(row, undefined, '4')).toBeNull()
    expect(pickedTier(row, undefined, '')).toBeNull()
  })

  it('works out a price of the seller\'s own as the server does', () => {
    // 470,63 is band 2: %20,6 of it is 96,95, and 160 of cost leaves 213,68.
    expect(ownChoice({ bands, costPrice: 160 }, 470.63)).toEqual({ tier: 2, price: 470.63, rate: 20.6, commission: 96.95, remaining: 213.68 })
    expect(ownChoice({ bands, costPrice: null }, 400)?.remaining).toBeNull()
    expect(ownChoice({ bands: [{ tier: 1, min: 500, max: null, rate: 20 }], costPrice: 100 }, 450)).toBeNull()
  })

  it('writes what a price leaves with its share, and says so when the cost is not entered', () => {
    expect(remainingText({ tier: 2, price: 470.63, rate: 20.6, commission: 96.95, remaining: 213.68 })).toBe('213,68 ₺ kalır · %45,4')
    expect(remainingText({ tier: 1, price: 100, rate: 20, commission: 20, remaining: null })).toBe('Maliyet girilmemiş')
  })
})

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
