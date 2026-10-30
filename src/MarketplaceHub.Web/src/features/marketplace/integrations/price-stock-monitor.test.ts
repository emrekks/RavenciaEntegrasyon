import { describe, expect, it } from 'vitest'
import { automaticWritesBaselineMessage, automaticWritesBaselineWarning, orderSourceState, priceStockProblemSignature, priceStockWriteState } from './integration-helpers'

describe('price/stock monitor helpers', () => {
  it('names the automatic write state of a connection', () => {
    expect(priceStockWriteState({ masterSwitchOn: true, priceWriteEnabled: true, stockWriteEnabled: true, automaticWritesOpen: true })).toEqual({ label: 'Açık', tone: 'on' })
    expect(priceStockWriteState({ masterSwitchOn: false, priceWriteEnabled: true, stockWriteEnabled: true, automaticWritesOpen: false }).label).toBe('Kapalı · Ana Anahtar')
    expect(priceStockWriteState({ masterSwitchOn: true, priceWriteEnabled: false, stockWriteEnabled: true, automaticWritesOpen: false })).toEqual({ label: 'Bekletiliyor · Fiyat Kapalı', tone: 'partial' })
    expect(priceStockWriteState({ masterSwitchOn: true, priceWriteEnabled: false, stockWriteEnabled: false, automaticWritesOpen: false }).tone).toBe('off')
  })

  it('changes the problem signature only when the problems change', () => {
    const base = { connectionId: 'c1', failedPushes7d: 0, manualReviewPushes: 0, invalidOffers: 0, latestFailure: null }
    expect(priceStockProblemSignature(base)).toBeNull()
    const failing = { ...base, failedPushes7d: 1, latestFailure: { jobId: 'j1', errorCode: 'PRICE_INVENTORY_REJECTED', errorSummary: null, status: 'Blocked', at: null } }
    expect(priceStockProblemSignature(failing)).toBe(priceStockProblemSignature({ ...failing }))
    expect(priceStockProblemSignature({ ...failing, invalidOffers: 2 })).not.toBe(priceStockProblemSignature(failing))
  })

  it('names where new orders come from and the cadence the read runs at', () => {
    expect(orderSourceState({ orderSource: 'WEBHOOK', orderReadIntervalSeconds: 480 })).toEqual({ label: 'Webhook', detail: 'Yedek okuma 8 dk', tone: 'on' })
    expect(orderSourceState({ orderSource: 'WEBHOOK_FALLBACK', orderReadIntervalSeconds: 120 })).toEqual({ label: 'Webhook Sorunlu', detail: 'Sık okuma 2 dk', tone: 'partial' })
    expect(orderSourceState({ orderSource: 'POLLING', orderReadIntervalSeconds: 120 })).toEqual({ label: 'Düzenli Okuma', detail: 'Her 2 dk', tone: 'off' })
    expect(orderSourceState({ orderSource: 'POLLING', orderReadIntervalSeconds: null }).detail).toBe('Sipariş okuması kapalı')
    expect(orderSourceState({ orderSource: 'POLLING', orderReadIntervalSeconds: 90 }).detail).toBe('Her 90 sn')
  })

  it('warns that changes made while writes were off are not sent', () => {
    expect(automaticWritesBaselineMessage('N11’e')).toContain(automaticWritesBaselineWarning)
    expect(automaticWritesBaselineWarning).toBe('Kapalıyken yapılan değişiklikler pazaryerine gönderilmedi. Açmadan önce pazaryerlerindeki stok ve fiyatları güncellemenizi öneririz.')
  })
})
