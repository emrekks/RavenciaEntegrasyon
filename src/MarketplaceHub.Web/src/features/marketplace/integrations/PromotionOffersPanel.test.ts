import { describe, expect, it } from 'vitest'
import { advantagePrices, byModel, flashPrices, groupOffers, keepOffer, leavesText, matches, noOfferFilter, plusPrices, starText, type AdvantageOffer, type FlashOffer, type OfferFilter, type PlusOffer } from './PromotionOffersPanel'

const product = { modelCode: 'RYP001', productName: 'Anne Penye Bluz', color: 'Siyah', imageUrl: null, stock: 11, promotions: null }

describe('promotion offers', () => {
  it('writes what a price leaves, a range for a model of different costs, and why it is not known', () => {
    expect(leavesText({ price: 359.02, rate: 14.1, commission: 50.62, remaining: 0.4 })).toBe('0,40 ₺ Kâr')
    expect(leavesText({ price: 400, rate: 14.1, commission: 56.4, remaining: 74.61, costs: { cost: 160, cargo: 98.34, serviceFee: 13.19, stoppage: 3.64, netVat: -6.18, vatRate: 10 } })).toBe('74,61 ₺ Kâr (%46,63)')
    expect(leavesText({ price: 426.46, rate: 21.5, commission: 91.69, remaining: -4.77, remainingHigh: 34.77 })).toBe('− 4,77 ₺ – 34,77 ₺ Kâr')
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

  it('keeps the rows the filter panel asks for', () => {
    const plus = { ...product, barcode: 'RYP00101', sellerSku: null, size: 'M', costPrice: 308, category: 'Bluz', brand: 'Ravencia', now: null, gain: -61.54,
      plus: { price: 359.02, rate: 14.1, commission: 50.62, remaining: 0.4 } } as PlusOffer
    const keep = (filter: Partial<OfferFilter>) => keepOffer(plus, { ...noOfferFilter, ...filter }, plusPrices)
    expect(keep({ name: 'penye', model: 'ryp', barcode: '00101', category: 'Bluz', brand: 'Ravencia' })).toBe(true)
    expect(keep({ category: 'Etek' })).toBe(false)
    expect(keep({ minStock: '5', maxStock: '11' })).toBe(true)
    expect(keep({ minStock: '12' })).toBe(false)
    // Kolon Seçimi: Plus leaves 0,40 (%0,1).
    expect(keep({ column: 'plus', minProfit: '0', maxProfit: '1' })).toBe(true)
    expect(keep({ column: 'plus', minShare: '5' })).toBe(false)
    // A range without a column filters nothing.
    expect(keep({ minProfit: '100' })).toBe(true)
    const advantage = { ...product, barcode: 'B', size: 'M', costPrice: null, inTariff: true, currentStars: 1, now: null,
      stars: [{ star: 2, min: null, max: 435.97, price: { price: 435.97, rate: null, commission: null, remaining: null } }] } as AdvantageOffer
    // An unknown commission meets no profit range.
    expect(keepOffer(advantage, { ...noOfferFilter, column: '2', minProfit: '-1000' }, advantagePrices)).toBe(false)
    const flash = { ...product, barcodes: ['B'], costLow: 300, costHigh: 330, inTariff: true, campaign: true, now: null,
      slots: [{ slot: '24 Saat', start: null, end: null, price: { price: 426.46, rate: 21.5, commission: 91.69, remaining: -4.77, remainingHigh: 34.77 } }] } as FlashOffer
    expect(keepOffer(flash, { ...noOfferFilter, column: '24 Saat', maxProfit: '0' }, flashPrices)).toBe(true)
    expect(keepOffer(flash, { ...noOfferFilter, barcode: 'b' }, flashPrices)).toBe(true)
  })

  it('names the stars a price earns', () => {
    expect(starText(0)).toBe('Etiket yok')
    expect(starText(2)).toBe('★★ Çok Avantajlı Ürün')
  })
})

describe('promotion offer groups', () => {
  it('groups a model\'s sizes by colour, a row without a model code alone', () => {
    const row = (barcode: string, modelCode: string | null, color: string | null) => ({ ...product, barcode, modelCode, color })
    const groups = groupOffers([row('1', 'RYP001', 'Siyah'), row('2', 'RYP001', 'Siyah'), row('3', 'RYP001', 'Mavi'), row('4', null, null)], item => item.barcode)
    expect(groups.map(([key, rows]) => `${key}:${rows.map(item => item.barcode).join('')}`)).toEqual(['RYP001|Siyah:12', 'RYP001|Mavi:3', '4:4'])
  })
})
