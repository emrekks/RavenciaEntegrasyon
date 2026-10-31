import { describe, expect, it } from 'vitest'
import { amountRange, bandLimitHint, bandLimitText, bandRange, commissionTariffPlatforms, defaultPeriod, exportMessage, groupRangeText, periodDays, periodTabLabel, tariffGroups, weekAverage, weekTabLabel, type CommissionTariff, selectionBody, selectionState, type TariffPick, filterQuery, nextSort, noFilter, groupByModel, nextBandHint, noBulk, ownChoice, pickedTier, priceRange, rateDisagrees, rateText, remainingText, shareRangeText, type CommissionTariffRow, type TariffBulk } from './CommissionTariffsPanel'

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

  it('runs the sizes of a colour in size order', () => {
    const sized = (id: string, size: string) => ({ ...row(id, 'MZ040RCY'), color: 'Bej', size }) as CommissionTariffRow
    const [[, rows]] = groupByModel([sized('1', 'M'), sized('2', 'XL'), sized('3', '2XL'), sized('4', 'L'), sized('5', 'S')])
    expect(rows.map(item => item.size)).toEqual(['S', 'M', 'L', 'XL', '2XL'])
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
  // Over a cost of 160 band 1 leaves the most money (248, %155 of the cost) and so the most on the cost too.
  const costs = { cost: 160, cargo: 98.34, serviceFee: 13.19, stoppage: 4.28, netVat: -6.52, vatRate: 10 }
  const row = { choices: [{ tier: 1, price: 519.75, rate: 21.5, commission: 111.75, remaining: 248, costs }, { tier: 2, price: 470.63, rate: 20.6, commission: 96.95, remaining: 213.68, costs },
    { tier: 4, price: 388.63, rate: 17.5, commission: 68.01, remaining: 200, costs }] }

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
    expect(pickedTier(row, undefined, bulk({ kind: 'rate' }))).toBe(1)
    // Band 2 leaves 213,68, %133,55 of the cost: kept for at least %133 or 200 lira, left out for %134 or 250 lira.
    expect(pickedTier(row, undefined, bulk({ kind: 'tier', tier: 2, minRate: 133 }))).toBe(2)
    expect(pickedTier(row, undefined, bulk({ kind: 'tier', tier: 2, minRate: 134 }))).toBeNull()
    expect(pickedTier(row, undefined, bulk({ kind: 'tier', tier: 2, minAmount: 200 }))).toBe(2)
    expect(pickedTier(row, undefined, bulk({ kind: 'tier', tier: 2, minAmount: 250 }))).toBeNull()
    // Below a value too (Verilenden küçük değerler): kept under %134 or 250 lira, left out under %133 or 200 lira.
    expect(pickedTier(row, undefined, bulk({ kind: 'tier', tier: 2, maxRate: 134 }))).toBe(2)
    expect(pickedTier(row, undefined, bulk({ kind: 'tier', tier: 2, maxRate: 133 }))).toBeNull()
    expect(pickedTier(row, undefined, bulk({ kind: 'tier', tier: 2, maxAmount: 250 }))).toBe(2)
    expect(pickedTier(row, undefined, bulk({ kind: 'tier', tier: 2, maxAmount: 200 }))).toBeNull()
    // Without a cost the condition cannot be met; the band alone still can.
    const unknown = { choices: row.choices.map(choice => ({ ...choice, remaining: null })) }
    expect(pickedTier(unknown, undefined, bulk({ kind: 'tier', tier: 2, minRate: 10 }))).toBeNull()
    expect(pickedTier(unknown, undefined, bulk({ kind: 'tier', tier: 2 }))).toBe(2)
    expect(pickedTier(unknown, undefined, bulk({ kind: 'amount' }))).toBeNull()
  })

  it('works out a price of the seller\'s own as the server does', () => {
    // 470,63 is band 2: %20,6 of it is 96,95; with the cargo, fee and VAT rate a band of the row was priced at, a cost of
    // 160 leaves 104,39 (the server's UnitProfit gives the same).
    const priced = [{ tier: 1, price: 519.75, rate: 21.5, commission: 111.75, remaining: 136.26, costs: { cost: 160, cargo: 98.34, serviceFee: 13.19, stoppage: 4.28, netVat: -6.52, vatRate: 10 } }]
    expect(ownChoice({ bands, costPrice: 160, choices: priced }, 470.63)).toEqual({ tier: 2, price: 470.63, rate: 20.6, commission: 96.95, remaining: 104.39, costs: { cost: 160, cargo: 98.34, serviceFee: 13.19, stoppage: 4.28, netVat: -6.52, vatRate: 10 } })
    // Melontik's 470,64 at %21,5: 101,19 of commission and a net VAT of −7,22 leave 100,86.
    expect(ownChoice({ bands, costPrice: 160, choices: priced }, 470.64)).toMatchObject({ commission: 101.19, remaining: 100.86, costs: { netVat: -7.22 } })
    expect(ownChoice({ bands, costPrice: null, choices: priced }, 400)?.remaining).toBeNull()
    expect(ownChoice({ bands, costPrice: 160 }, 400)?.remaining).toBeNull()
    expect(ownChoice({ bands: [{ tier: 1, min: 500, max: null, rate: 20 }], costPrice: 100 }, 450)).toBeNull()
    // %15 of 33,30 is 4,995: the server rounds it half away from zero to 5,00 (the float product gave 4,99).
    expect(ownChoice({ bands: [{ tier: 1, min: null, max: null, rate: 15 }], costPrice: null }, 33.3)?.commission).toBe(5)
    // 10,005 is 10,01 on the server too, not the float's 10,00.
    expect(ownChoice({ bands: [{ tier: 1, min: null, max: null, rate: 15 }], costPrice: null }, 10.005)?.price).toBe(10.01)
  })

  it('writes what a price leaves with its share, and says so when the cost is not entered', () => {
    expect(remainingText({ tier: 2, price: 470.63, rate: 20.6, commission: 96.95, remaining: 104.39, costs: { cost: 160, cargo: 98.34, serviceFee: 13.19, stoppage: 4.28, netVat: -6.52, vatRate: 10 } })).toBe('104,39 ₺ Kâr (%65,24)')
    expect(remainingText({ tier: 1, price: 470.64, rate: 21.5, commission: 101.19, remaining: -12.5, costs: { cost: 160, cargo: 98.34, serviceFee: 13.19, stoppage: 4.28, netVat: -6.52, vatRate: 10 } })).toBe('−12,50 ₺ Zarar (%−7,81)')
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

describe('commission tariff filters', () => {
  it('sends only the filled fields, a stock only when it is a number', () => {
    expect(filterQuery(noFilter)).toEqual({})
    expect(filterQuery({ ...noFilter, name: ' penye ', model: 'MZ005', minStock: '1', maxStock: 'abc', category: 'Bluz', tier: '2' }))
      .toEqual({ name: 'penye', model: 'MZ005', minStock: '1', category: 'Bluz', tier: '2' })
    expect(filterQuery({ ...noFilter, maxStock: '12,5' })).toEqual({ maxStock: '12.5' })
  })
})

describe('commission tariff header order', () => {
  it('goes up, then down, then back to no order, and is sent with the filter', () => {
    const up = nextSort(noFilter, 'tier2')
    expect([up.sort, up.dir]).toEqual(['tier2', 'asc'])
    const down = nextSort(up, 'tier2')
    expect([down.sort, down.dir]).toEqual(['tier2', 'desc'])
    expect(nextSort(down, 'tier2').sort).toBeUndefined()
    expect(nextSort(down, 'name')).toMatchObject({ sort: 'name', dir: 'asc' })
    expect(filterQuery(down)).toEqual({ sort: 'tier2', dir: 'desc' })
  })
})

describe('commission tariff profit range', () => {
  it('sends a band with the profit range typed for it, nothing without a band or a range', () => {
    expect(filterQuery({ ...noFilter, profitTier: '4', minProfit: '100', maxShare: '35,5' })).toEqual({ profitTier: '4', minProfit: '100', maxShare: '35.5' })
    expect(filterQuery({ ...noFilter, profitTier: '2', minProfit: '−20' })).toEqual({ profitTier: '2', minProfit: '-20' })
    expect(filterQuery({ ...noFilter, profitTier: '4' })).toEqual({})
    expect(filterQuery({ ...noFilter, minProfit: '100' })).toEqual({})
  })
})

describe('bandLimitText', () => {
  // Trendyol's 29 Ekim tariff: 563,84 ₺ ve üzeri, 510,42–563,83, 457,01–510,41, 457,00 ₺ ve altı (each limit included).
  const bands = [{ tier: 1, min: 563.84, max: null, rate: 21.5 }, { tier: 2, min: 510.42, max: 563.83, rate: 20.8 }, { tier: 3, min: 457.01, max: 510.41, rate: 19.3 }, { tier: 4, min: null, max: 457, rate: 17.5 }]
  it('states the top band from its lower limit, the bottom band up to its upper one and the bands between as ranges', () => {
    expect(bands.map(band => bandLimitText([band]))).toEqual(['563,84 ₺ ve üzeri', '510,42 – 563,83 ₺', '457,01 – 510,41 ₺', '457,00 ₺ ve altı'])
    expect(bandLimitText([undefined])).toBeNull()
  })
  it('over a model\'s variants covers the prices of all of them', () => {
    expect(bandLimitText([{ tier: 1, min: 463.13, max: null, rate: 21.5 }, { tier: 1, min: 464.63, max: null, rate: 21.5 }])).toBe('463,13 – 464,63 ₺ ve üzeri')
    expect(bandLimitText([bands[1], { tier: 2, min: 500.02, max: 553.5, rate: 20.8 }])).toBe('500,02 – 563,83 ₺')
    expect(bandLimitText([bands[3], { tier: 4, min: null, max: 450, rate: 17.5 }])).toBe('450,00 – 457,00 ₺ ve altı')
  })
  it('says in words that both limits are included', () => {
    expect(bandLimitHint([bands[1]])).toBe('510,42 ₺ ile 563,83 ₺ arası (ikisi de dahil)')
    expect(bandLimitHint([bands[0]])).toBe('563,84 ₺ ve üzeri (563,84 ₺ dahil)')
    expect(bandLimitHint([bands[3]])).toBe('457,00 ₺ ve altı (457,00 ₺ dahil)')
    expect(bandLimitHint([bands[1], bands[1]])).toBe('510,42 ₺ ile 563,83 ₺ arası (ikisi de dahil)')
    expect(bandLimitHint([undefined])).toBeUndefined()
  })
})

describe('saved tariff picks', () => {
  it('sends the picks in row order and reads them back as they were', () => {
    const picks = new Map<string, TariffPick>([['b', { tier: 2, price: null }], ['a', { tier: null, price: 400.1 }], ['c', { tier: null, price: null }]])
    const bulk: TariffBulk = { kind: 'tier', tier: 4, minRate: 20, minAmount: null, maxRate: null, maxAmount: null }
    const body = selectionBody(picks, bulk)
    expect(body).toEqual({ bulk: 'tier', tier: 4, minRate: 20, minAmount: null, maxRate: null, maxAmount: null,
      choices: [{ rowId: 'a', tier: null, price: 400.1 }, { rowId: 'b', tier: 2, price: null }, { rowId: 'c', tier: null, price: null }] })
    const state = selectionState(body)
    expect(state.bulk).toEqual(bulk)
    expect([...state.picks].sort()).toEqual([...picks].sort())
    // The same picks in another order read the same, so an unchanged page sends nothing.
    expect(JSON.stringify(selectionBody(state.picks, state.bulk))).toBe(JSON.stringify(body))
  })

  it('keeps no condition without a pick for every row and reads nothing saved as no pick', () => {
    expect(selectionBody(new Map(), { ...noBulk, minRate: 5 })).toEqual({ bulk: null, tier: null, minRate: null, minAmount: null, maxRate: null, maxAmount: null, choices: [] })
    expect(selectionState(undefined)).toEqual({ picks: new Map(), bulk: noBulk })
    expect(selectionState({ bulk: 'other', tier: 3, minRate: 1, minAmount: null, maxRate: null, maxAmount: null, choices: [] }).bulk).toEqual(noBulk)
    expect(selectionState({ bulk: 'amount', tier: null, minRate: null, minAmount: 50, maxRate: null, maxAmount: null, choices: [] }).bulk).toEqual({ kind: 'amount', tier: 1, minRate: null, minAmount: 50, maxRate: null, maxAmount: null })
  })

  it('tells how many prices the Trendyol file carries and when it was written again', () => {
    expect(exportMessage(12, false)).toMatch(/^12 ürünün fiyatı Trendyol tarife dosyasına yazıldı/)
    expect(exportMessage(Number.NaN, false)).toMatch(/^Seçilen ürünlerin fiyatı/)
    expect(exportMessage(1, true)).toContain('aynı tarife dosyasını bir kez daha yükleyin')
  })
})

describe('commission tariff periods', () => {
  // Trendyol's file of 31 Oct 2026: a 3-day and a 4-day period, stored as a tariff each (the end exclusive, in UTC).
  const period = (id: string, start: string, end: string, values: Partial<CommissionTariff> = {}) =>
    ({ id, platformCode: 'TRENDYOL', periodStart: start, periodEnd: end, periodText: id, fileName: '930112.xlsx', uploadedAt: start, rowCount: 641, matchedRowCount: 600, isCurrent: false, ...values }) as CommissionTariff
  const three = period('p3', '2026-10-29T05:00:00Z', '2026-11-01T05:00:00Z', { periodLabel: '3 Gün', groupId: 'p3' })
  const four = period('p4', '2026-11-01T05:00:00Z', '2026-11-05T05:00:00Z', { periodLabel: '4 Gün', groupId: 'p3' })
  const older = period('old', '2026-10-22T05:00:00Z', '2026-10-29T05:00:00Z')

  it('lists a file once with its periods in order, a tariff of an older server as a file of its own', () => {
    const groups = tariffGroups([four, three, older])
    expect(groups.map(group => `${group.id}:${group.periods.map(item => item.id).join(',')}`)).toEqual(['p3:p3,p4', 'old:old'])
  })

  it('opens the period in force now, else the first', () => {
    expect(defaultPeriod([three, { ...four, isCurrent: true }])?.id).toBe('p4')
    expect(defaultPeriod([three, four])?.id).toBe('p3')
    expect(defaultPeriod([])).toBeNull()
  })

  it('names the span and each period as the tabs show them, up to the last minute of each', () => {
    expect(groupRangeText([three, four])).toBe('29 Eki 2026 – 5 Kas 2026')
    expect(periodTabLabel(three)).toBe('3 Gün (29 Eki – 1 Kas 2026)')
    expect(periodTabLabel(four)).toBe('4 Gün (1 Kas – 5 Kas 2026)')
    // Without the file's name for it, a period is named by its days.
    expect(periodTabLabel(older)).toBe('7 Gün (22 Eki – 29 Eki 2026)')
  })

  it('names the two periods together as the 7-day tab and weighs what a price leaves by each period\'s days', () => {
    expect([periodDays(three), periodDays(four)]).toEqual([3, 4])
    expect(weekTabLabel([three, four])).toBe('7 Gün (29 Eki – 5 Kas 2026)')
    // 3 days leaving 100 ₺ and 4 days leaving 135 ₺: (3 × 100 + 4 × 135) / 7 = 120 ₺ a sale.
    expect(weekAverage([{ days: 3, value: 100 }, { days: 4, value: 135 }])).toBe(120)
    expect(weekAverage([{ days: 3, value: 173.49 }, { days: 4, value: 176.77 }])).toBe(175.36)
    expect(weekAverage([{ days: 3, value: -10 }, { days: 4, value: 4 }])).toBe(-2)
    // A period whose profit is not known leaves the week's unknown.
    expect(weekAverage([{ days: 3, value: 100 }, { days: 4, value: null }])).toBeNull()
    expect(weekAverage([])).toBeNull()
  })

  it('tells how many products took the open period\'s price where the periods\' prices differ', () => {
    expect(exportMessage(3, false, 1, '3 Gün')).toContain('1 üründe iki dönem için farklı fiyat seçili; dosya ürün başına tek fiyat taşıdığından bu ürünlere 3 Gün döneminin fiyatı yazıldı.')
    expect(exportMessage(3, false, 0, '3 Gün')).not.toContain('farklı fiyat')
  })
})

describe('shareRangeText', () => {
  // Profit on cost, as a single price's badge gives it: 51,39 on a cost of 100 is %51,39.
  const choice = (remaining: number | null, cost = 100) => ({ tier: 2, price: 500, rate: 20, commission: 100, remaining, costs: { cost, cargo: 0, serviceFee: 0, stoppage: 0, netVat: 0, vatRate: 10 } })
  it('gives the lowest and highest share of the variants whose profit is known', () => {
    expect(shareRangeText([choice(51.39), choice(60.1), choice(null)])).toBe('(%51,39 – %60,10)')
    expect(shareRangeText([choice(51.39), choice(51.39)])).toBe('(%51,39)')
    expect(shareRangeText([choice(-12.5), choice(40, 200)])).toBe('(%−12,50 – %20,00)')
    expect(shareRangeText([choice(null)])).toBeNull()
  })
})
