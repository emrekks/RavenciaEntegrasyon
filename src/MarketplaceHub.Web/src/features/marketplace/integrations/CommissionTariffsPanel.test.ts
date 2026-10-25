import { describe, expect, it } from 'vitest'
import { amountRange, bandRange, commissionTariffPlatforms, groupByModel, nextBandHint, noBulk, ownChoice, pickedTier, priceRange, rateDisagrees, rateText, remainingText, type CommissionTariffRow, type TariffBulk } from './CommissionTariffsPanel'

describe('commission tariff models', () => {
  const row = (id: string, modelCode: string | null) => ({ id, barcode: `B${id}`, modelCode }) as CommissionTariffRow

  it('puts a model\'s variants under it in their order, a row without a model code standing alone', () => {
    const groups = groupByModel([row('1', 'MZ005'), row('2', 'MZ005'), row('3', null), row('4', ' '), row('5', 'MZ005')])
    expect(groups.map(([key, rows]) => `${key}:${rows.map(item => item.id).join('')}`)).toEqual(['MZ005|:125', 'B3:3', 'B4:4'])
  })

  it('groups a model\'s sizes by colour, each colour its own row', () => {
    const colored = (id: string, color: string) => ({ ...row(id, 'MZ019CD'), color }) as CommissionTariffRow
    const groups = groupByModel([colored('1', 'Bej'), colored('2', 'Siyah'), colored('3', 'Bej'), colored('4', 'Mürdüm')])
    expect(groups.map(([key, rows]) => `${key}:${rows.map(item => item.id).join('')}`)).toEqual(['MZ019CD|Bej:13', 'MZ019CD|Siyah:2', 'MZ019CD|Mürdüm:4'])
  })

  it('writes a model\'s prices and what they leave as ranges, one value when all agree', () => {
    expect(priceRange([791.01, 752.64, 899])).toBe('752,64 – 899,00 ₺')
    expect(priceRange([752.64, 752.64])).toBe('752,64 ₺')
    expect(amountRange([276.09, -12.5])).toBe('− 12,50 – 276,09 ₺')
    expect(amountRange([276.09])).toBe('276,09 ₺')
  })
})

const bands = [{ tier: 1, min: 470.64, max: null, rate: 21.5 }, { tier: 2, min: 435.98, max: 470.63, rate: 20.6 }, { tier: 3, min: 388.64, max: 435.97, rate: 19.4 }, { tier: 4, min: null, max: 388.63, rate: 17.5 }]
const bulk = (values: Partial<TariffBulk>): TariffBulk => ({ ...noBulk, ...values })

describe('commission tariff picks', () => {
  // Band 1 leaves the most money (248, %47,7 of its price), band 4 the largest share (200, %51,5).
  const row = { choices: [{ tier: 1, price: 519.75, rate: 21.5, commission: 111.75, remaining: 248 }, { tier: 2, price: 470.63, rate: 20.6, commission: 96.95, remaining: 213.68 },
    { tier: 4, price: 388.63, rate: 17.5, commission: 68.01, remaining: 200 }] }

  it('takes a row its own pick, else the pick for every row, and leaves it out when neither applies', () => {
    expect(pickedTier(row, { tier: 2, price: null }, bulk({ kind: 'amount' }))).toBe(2)
    expect(pickedTier(row, { tier: null, price: 480 }, noBulk)).toBe('own')
    expect(pickedTier(row, { tier: null, price: null }, bulk({ kind: 'amount' }))).toBeNull()
    expect(pickedTier(row, undefined, noBulk)).toBeNull()
  })

  it('picks for every row by band, by the most money left or by the largest share, as far as the condition allows', () => {
    expect(pickedTier(row, undefined, bulk({ kind: 'tier', tier: 2 }))).toBe(2)
    expect(pickedTier(row, undefined, bulk({ kind: 'tier', tier: 3 }))).toBeNull()
    expect(pickedTier(row, undefined, bulk({ kind: 'amount' }))).toBe(1)
    expect(pickedTier(row, undefined, bulk({ kind: 'rate' }))).toBe(4)
    // Band 2 leaves %45,4 and 213,68: kept for at least %45 or 200 lira, left out for %46 or 250 lira.
    expect(pickedTier(row, undefined, bulk({ kind: 'tier', tier: 2, minRate: 45 }))).toBe(2)
    expect(pickedTier(row, undefined, bulk({ kind: 'tier', tier: 2, minRate: 46 }))).toBeNull()
    expect(pickedTier(row, undefined, bulk({ kind: 'tier', tier: 2, minAmount: 200 }))).toBe(2)
    expect(pickedTier(row, undefined, bulk({ kind: 'tier', tier: 2, minAmount: 250 }))).toBeNull()
    // Without a cost the condition cannot be met; the band alone still can.
    const unknown = { choices: row.choices.map(choice => ({ ...choice, remaining: null })) }
    expect(pickedTier(unknown, undefined, bulk({ kind: 'tier', tier: 2, minRate: 10 }))).toBeNull()
    expect(pickedTier(unknown, undefined, bulk({ kind: 'tier', tier: 2 }))).toBe(2)
    expect(pickedTier(unknown, undefined, bulk({ kind: 'amount' }))).toBeNull()
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
