import { describe, expect, it } from 'vitest'
import { axisLabel, matrixCellText } from './DashboardPage'
import { addDays, channelTotals, dayKeys, daySeries, hourSeries, istanbulHour, otherCurrencies, periodDays, primaryCurrency, revenueReport, type Performance, type PerformanceRow } from './dashboard-performance'

const row = (day: string, connectionId: string, amount: number, extra: Partial<PerformanceRow> = {}): PerformanceRow =>
  ({ day, connectionId, currency: 'TRY', amount, orderCount: 1, productQuantity: 2, packageCount: 1, returnedAmount: 0, returnedOrderCount: 0, ...extra })

const performance = (current: PerformanceRow[], previous: PerformanceRow[] = [], partialDay = false): Performance => ({
  from: '2026-10-19', to: '2026-10-20', previousFrom: '2026-10-17', previousTo: '2026-10-18',
  currentStart: '2026-10-18T21:00:00Z', currentEnd: '2026-10-20T07:30:00Z', previousStart: '2026-10-16T21:00:00Z', previousEnd: '2026-10-18T07:30:00Z',
  partialDay,
  channels: [{ connectionId: 'ty-1', platformCode: 'TRENDYOL', displayName: 'Trendyol Ana', status: 'ACTIVE' }, { connectionId: 'ty-2', platformCode: 'TRENDYOL', displayName: 'Trendyol İkinci', status: 'ACTIVE' }],
  current, previous,
})

describe('dashboard periods', () => {
  it('asks for today so far, yesterday as one whole day, or for Istanbul days ending with today', () => {
    expect(periodDays('today', { from: '', to: '' }, '2026-10-20')).toEqual({ from: '2026-10-20', to: '2026-10-20' })
    expect(periodDays('yesterday', { from: '', to: '' }, '2026-10-20')).toEqual({ from: '2026-10-19', to: '2026-10-19' })
    expect(periodDays('yesterday', { from: '', to: '' }, '2026-03-01')).toEqual({ from: '2026-02-28', to: '2026-02-28' })
    expect(periodDays('7', { from: '', to: '' }, '2026-10-20')).toEqual({ from: '2026-10-14', to: '2026-10-20' })
    expect(periodDays('30', { from: '', to: '' }, '2026-03-01')).toEqual({ from: '2026-01-31', to: '2026-03-01' })
  })

  it('a half-picked custom range asks for nothing, a reversed one is put in order', () => {
    expect(periodDays('custom', { from: '2026-10-01', to: '' }, '2026-10-20')).toBeNull()
    expect(periodDays('custom', { from: '2026-10-10', to: '2026-10-01' }, '2026-10-20')).toEqual({ from: '2026-10-01', to: '2026-10-10' })
  })

  it('counts days without any clock', () => {
    expect(addDays('2026-03-29', 1)).toBe('2026-03-30')
    expect(dayKeys('2026-10-30', '2026-11-02')).toEqual(['2026-10-30', '2026-10-31', '2026-11-01', '2026-11-02'])
    expect(dayKeys('2026-10-02', '2026-10-01')).toEqual([])
  })

})

describe('dashboard performance', () => {
  it('fills every day and keeps other currencies out of the lira total', () => {
    const rows = [row('2026-10-20', 'ty-1', 500), row('2026-10-20', 'ty-1', 30, { currency: 'USD' })]
    const series = daySeries(rows, '2026-10-19', '2026-10-20', 'ALL', primaryCurrency(rows))

    expect(series.map(point => point.amount)).toEqual([0, 500])
    expect(otherCurrencies(rows, 'ALL', 'TRY')).toEqual([{ currency: 'USD', amount: 30, orders: 1 }])
    expect(primaryCurrency([row('2026-10-20', 'ty-1', 30, { currency: 'EUR' })])).toBe('EUR')
  })

  it('keeps two stores of one marketplace apart', () => {
    const totals = channelTotals(performance([row('2026-10-20', 'ty-1', 500), row('2026-10-20', 'ty-2', 200)], [row('2026-10-18', 'ty-2', 100)]), 'TRY')

    expect(totals.map(item => [item.connectionId, item.name, item.amount, item.previousAmount])).toEqual([['ty-1', 'Trendyol Ana', 500, 0], ['ty-2', 'Trendyol İkinci', 200, 100]])
  })

  it('filters by the picked store and counts returns inside the revenue', () => {
    const rows = [row('2026-10-20', 'ty-1', 500, { returnedAmount: 80, returnedOrderCount: 1 }), row('2026-10-20', 'ty-2', 200)]
    const [, day] = daySeries(rows, '2026-10-19', '2026-10-20', 'ty-1', 'TRY')

    expect(day.amount).toBe(580)
    expect(day.returnedAmount).toBe(80)
    expect(day.returnedOrderCount).toBe(1)
  })

  it('exports the chosen period and store with its counts and the day as sent', () => {
    const report = revenueReport(performance([row('2026-10-20', 'ty-2', 1234.5, { orderCount: 3, productQuantity: 4, packageCount: 2, returnedAmount: 10, returnedOrderCount: 1 })]), 'ty-2', 'Trendyol İkinci')

    expect(report.name).toBe('ravencia-ciro-2026-10-19_2026-10-20-trendyol-ikinci.csv')
    expect(report.content.charCodeAt(0)).toBe(0xfeff)
    const lines = report.content.slice(1).split('\r\n')
    expect(lines[0]).toBe('"Tarih";"Para Birimi";"Ciro";"Sipariş";"Ürün Adedi";"Paket";"İade Tutarı";"İade Sipariş"')
    expect(lines[2]).toBe('"2026-10-20";"TRY";"1.244,50";"3";"4";"2";"10,00";"1"')
    expect(revenueReport(performance([]), 'ALL', '').name).toBe('ravencia-ciro-2026-10-19_2026-10-20-tum-platformlar.csv')
  })
})

describe('integration matrix cells', () => {
  it('names the state instead of calling every delay a stop', () => {
    expect(matrixCellText({ state: 'warn', lastSuccessAt: null, lastProblemAt: null })).toBe('Gecikiyor')
    expect(matrixCellText({ state: 'bad', lastSuccessAt: null, lastProblemAt: null })).toBe('Hata Var')
    expect(matrixCellText({ state: 'off', lastSuccessAt: null, lastProblemAt: null })).toBe('İşlem Yok')
    expect(matrixCellText({ state: 'ok', lastSuccessAt: new Date().toISOString(), lastProblemAt: null })).toBe('Az önce')
  })
})

describe('hourly series of a one-day period', () => {
  const row = (hour: string, amount: number, connectionId = 'store-1') => ({ hour, connectionId, currency: 'TRY', amount, orderCount: 1, productQuantity: 2, packageCount: 1, returnedAmount: 0, returnedOrderCount: 0 })

  it('fills every hour up to the last one and adds the stores up', () => {
    const points = hourSeries([row('2026-10-20T09:00', 100), row('2026-10-20T09:00', 50, 'store-2'), row('2026-10-20T14:00', 30)], '2026-10-20', 'ALL', 'TRY', 14)
    expect(points).toHaveLength(15)
    expect(points[0]).toMatchObject({ day: '2026-10-20T00:00', label: '00:00', amount: 0 })
    expect(points[9]).toMatchObject({ label: '09:00', amount: 150, orderCount: 2 })
    expect(points[14]).toMatchObject({ label: '14:00', amount: 30 })
    expect(hourSeries([row('2026-10-20T09:00', 100), row('2026-10-20T09:00', 50, 'store-2')], '2026-10-20', 'store-2', 'TRY', 23)[9].amount).toBe(50)
  })

  it('reads the Istanbul hour of an instant', () => {
    expect(istanbulHour('2026-10-20T20:28:00Z')).toBe(23)
    expect(istanbulHour('2026-10-20T21:05:00Z')).toBe(0)
    expect(istanbulHour('not a date')).toBe(23)
  })
})

describe('revenue chart axis (W-F15)', () => {
  it('labels the axis in the currency the chart shows', () => {
    expect(axisLabel(0)).toBe('₺0')
    expect(axisLabel(850)).toBe('₺850')
    expect(axisLabel(1500)).toBe('₺1,5 B')
    expect(axisLabel(1500, 'USD')).toBe('$1,5 B')
    expect(axisLabel(850, 'EUR')).toBe('€850')
  })
})
