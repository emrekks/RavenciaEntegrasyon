import { describe, expect, it } from 'vitest'
import {
  emptySendLogFilters, hasSendLogFilters, istanbulDayToIso, listPriceChanged, priceChange, quantityChange, sendLogFilterCount, sendLogQueryString,
  sendLogResultGroups, sendLogResultLabel, sendLogResultOptions, sendLogResultTone, sendLogResultTooltip, sendLogTriggerGroups, sendLogTriggerLabel,
  sendLogTriggerOptions
} from './price-stock-send-log'

describe('price/stock send log values', () => {
  it('shows a stock change as before → after', () => {
    expect(quantityChange(5, 4)).toEqual({ text: '5 → 4', omitted: false, changed: true })
    expect(quantityChange(5, 5)).toEqual({ text: '5 → 5', omitted: false, changed: false })
    expect(quantityChange(1250, 1.5).text).toBe('1.250 → 1,5')
  })

  it('shows an unknown earlier quantity as a dash and a stock left out of the request as not sent', () => {
    expect(quantityChange(null, 4).text).toBe('— → 4')
    expect(quantityChange(5, null)).toEqual({ text: 'Gönderilmedi', omitted: true, changed: false })
  })

  it('shows prices in lira, kuruş only when there are any', () => {
    expect(priceChange(799, 749, 'TRY').text).toBe('₺799 → ₺749')
    expect(priceChange(799.9, 749.5, 'TRY').text).toBe('₺799,90 → ₺749,50')
    expect(priceChange(null, 749, 'TRY').text).toBe('— → ₺749')
    expect(priceChange(799, null, 'TRY').omitted).toBe(true)
  })

  it('shows the list price only when the request carried a new one', () => {
    expect(listPriceChanged({ previousListPrice: 900, newListPrice: 850 })).toBe(true)
    expect(listPriceChanged({ previousListPrice: 900, newListPrice: 900 })).toBe(false)
    expect(listPriceChanged({ previousListPrice: 900, newListPrice: null })).toBe(false)
    expect(listPriceChanged({ previousListPrice: null, newListPrice: 850 })).toBe(true)
  })
})

describe('price/stock send log results and triggers', () => {
  it('labels and colours every result the server sends', () => {
    expect(sendLogResultOptions).toEqual(['SUBMITTED', 'APPLIED', 'UNCHANGED', 'REJECTED', 'FAILED', 'SUPERSEDED', 'UNKNOWN'])
    expect(sendLogResultOptions.map(sendLogResultLabel)).toEqual(['Gönderildi', 'Uygulandı', 'Zaten Güncel', 'Reddedildi', 'Başarısız', 'Yeni Sürüm Geldi', 'Sonuç Belirsiz'])
    expect(sendLogResultOptions.map(sendLogResultTone)).toEqual(['neutral', 'success', 'success', 'danger', 'danger', 'warning', 'warning'])
  })

  it('shows an unknown result or trigger as it came', () => {
    expect(sendLogResultLabel('NEW_STATE')).toBe('NEW_STATE')
    expect(sendLogResultTone('NEW_STATE')).toBe('neutral')
    expect(sendLogTriggerLabel('SOMETHING')).toBe('SOMETHING')
  })

  it('labels every trigger the server sends', () => {
    expect(sendLogTriggerOptions.map(sendLogTriggerLabel)).toEqual(['Sipariş', 'Manuel Stok', 'Fiyat Değişikliği', 'Pazaryeri Stok Aktarımı', 'Stok Değişikliği', 'Kontrol Taraması', 'Panelden Gönderim', 'Otomatik'])
  })

  it('groups every result and every trigger exactly once', () => {
    const results = sendLogResultGroups.flatMap(group => group.values)
    const triggers = sendLogTriggerGroups.flatMap(group => group.values)
    expect([...results].sort()).toEqual([...sendLogResultOptions].sort())
    expect([...triggers].sort()).toEqual([...sendLogTriggerOptions].sort())
    expect(sendLogResultGroups.map(group => group.label)).toEqual(['Başarılı', 'Sorunlu', 'Bekleyen', 'Diğer'])
    expect(sendLogTriggerGroups.map(group => group.label)).toEqual(['Otomatik', 'Panelden', 'Aktarım'])
  })

  it('puts the marketplace error after the meaning of the result', () => {
    expect(sendLogResultTooltip({ result: 'REJECTED', errorCode: 'TY-400', errorMessage: 'Fiyat aralık dışı' })).toBe('Pazaryeri satırı reddetti. — TY-400: Fiyat aralık dışı')
    expect(sendLogResultTooltip({ result: 'APPLIED', errorCode: null, errorMessage: null })).toBe('Pazaryeri değerleri aldı.')
  })
})

describe('price/stock send log request', () => {
  it('turns Istanbul days into UTC instants', () => {
    expect(istanbulDayToIso('2026-10-19')).toBe('2026-10-18T21:00:00.000Z')
    expect(istanbulDayToIso('2026-10-19', true)).toBe('2026-10-19T20:59:59.999Z')
    expect(istanbulDayToIso('')).toBe('')
    expect(istanbulDayToIso('19.10.2026')).toBe('')
  })

  it('asks for the first page with no filters', () => {
    expect(sendLogQueryString(emptySendLogFilters)).toBe('?limit=50')
    expect(hasSendLogFilters(emptySendLogFilters)).toBe(false)
  })

  it('sends every filter and the cursor', () => {
    const filters = { search: '  8680001  ', platforms: ['trendyol', 'HEPSIBURADA', 'TRENDYOL'], results: ['REJECTED', 'FAILED'], triggers: ['ORDER'], from: '2026-10-19', to: '2026-10-20' }
    const params = new URLSearchParams(sendLogQueryString(filters, 'cursor-1', 100, true).slice(1))
    expect(Object.fromEntries(params)).toEqual({
      limit: '100', search: '8680001', platforms: 'TRENDYOL,HEPSIBURADA', results: 'REJECTED,FAILED', triggers: 'ORDER',
      from: '2026-10-18T21:00:00.000Z', to: '2026-10-20T20:59:59.999Z', order: 'asc', after: 'cursor-1'
    })
    expect(hasSendLogFilters(filters)).toBe(true)
    // The date range counts once, however many of its ends are set.
    expect(sendLogFilterCount(filters)).toBe(5)
    expect(sendLogFilterCount({ ...emptySendLogFilters, to: '2026-10-20' })).toBe(1)
  })

  it('keeps the page size and the search within the server limits', () => {
    expect(new URLSearchParams(sendLogQueryString(emptySendLogFilters, null, 500).slice(1)).get('limit')).toBe('100')
    expect(new URLSearchParams(sendLogQueryString({ ...emptySendLogFilters, search: 'x'.repeat(150) }).slice(1)).get('search')).toHaveLength(100)
  })
})
