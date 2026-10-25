import { describe, expect, it } from 'vitest'
import { byModel, groupOffers, keepAdvantage, keepFlash, keepPlus, leavesText, matches, starText, type AdvantageOffer, type FlashOffer, type PlusOffer } from './PromotionOffersPanel'

const product = { modelCode: 'RYP001', productName: 'Anne Penye Bluz', color: 'Siyah', imageUrl: null, stock: 11, promotions: null }

describe('promotion offers', () => {
  it('writes what a price leaves, a range for a model of different costs, and why it is not known', () => {
    expect(leavesText({ price: 359.02, rate: 14.1, commission: 50.62, remaining: 0.4 })).toBe('0,40 ₺ kalır · %0,1')
    expect(leavesText({ price: 426.46, rate: 21.5, commission: 91.69, remaining: -4.77, remainingHigh: 34.77 })).toBe('− 4,77 ₺ – 34,77 ₺ kalır')
    expect(leavesText({ price: 470.63, rate: null, commission: null, remaining: null })).toBe('Komisyon bilinmiyor')
    expect(leavesText({ price: 470.63, rate: 20.6, commission: 96.95, remaining: null })).toBe('Maliyet girilmemiş')
  })

  it('lists rows by model, colour and size', () => {
    const row = (modelCode: string, color: string, size: string) => ({ ...product, modelCode, color, size })
    const sorted = byModel([row('B', 'Bej', 'M'), row('A', 'Siyah', 'XL'), row('A', 'Siyah', 'M'), row('A', 'Bej', 'L')], item => item.size)
    expect(sorted.map(item => `${item.modelCode}-${item.color}-${item.size}`)).toEqual(['A-Bej-L', 'A-Siyah-M', 'A-Siyah-XL', 'B-Bej-M'])
  })

  it('finds a row by name, model code or any of its barcodes', () => {
    expect(matches({ ...product, barcodes: ['RYP00105', 'RYP00104'] }, 'ryp00104')).toBe(true)
    expect(matches({ ...product, barcode: 'RYP00101' }, 'bluz')).toBe(true)
    expect(matches({ ...product, barcode: 'RYP00101' }, 'MZ005')).toBe(false)
  })

  it('keeps the rows a filter asks for', () => {
    const plus = (gain: number | null, costPrice: number | null) => ({ ...product, barcode: 'B', sellerSku: null, size: 'M', costPrice, now: null, plus: null, gain }) as PlusOffer
    expect([plus(5, 300), plus(-61.54, 308), plus(null, null)].filter(row => keepPlus(row, '', 'gain')).length).toBe(1)
    expect([plus(5, 300), plus(-61.54, 308), plus(null, null)].filter(row => keepPlus(row, '', 'nocost')).length).toBe(1)
    const advantage = { ...product, barcode: 'B', size: 'M', costPrice: null, inTariff: true, currentStars: 1, now: null, stars: [] } as AdvantageOffer
    expect(keepAdvantage(advantage, '', '1')).toBe(true)
    expect(keepAdvantage(advantage, '', '0')).toBe(false)
    const flash = { ...product, barcodes: ['B'], costLow: 300, costHigh: 330, inTariff: true, campaign: true, now: null,
      slots: [{ slot: '24 Saat', start: null, end: null, price: { price: 426.46, rate: 21.5, commission: 91.69, remaining: -4.77, remainingHigh: 34.77 } }] } as FlashOffer
    expect(keepFlash(flash, '', 'loss')).toBe(true)
    expect(keepFlash(flash, '', 'nocost')).toBe(false)
  })

  it('names the stars a price earns', () => {
    expect(starText(0)).toBe('Yıldız yok')
    expect(starText(2)).toBe('★★ 2 yıldız')
  })
})

describe('promotion offer groups', () => {
  it('groups a model\'s sizes by colour, a row without a model code alone', () => {
    const row = (barcode: string, modelCode: string | null, color: string | null) => ({ ...product, barcode, modelCode, color })
    const groups = groupOffers([row('1', 'RYP001', 'Siyah'), row('2', 'RYP001', 'Siyah'), row('3', 'RYP001', 'Mavi'), row('4', null, null)])
    expect(groups.map(([key, rows]) => `${key}:${rows.map(item => item.barcode).join('')}`)).toEqual(['RYP001|Siyah:12', 'RYP001|Mavi:3', '4:4'])
  })
})
