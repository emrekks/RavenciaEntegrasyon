import { describe, expect, it } from 'vitest'
import { automaticWritesBaselineMessage, automaticWritesBaselineWarning, priceStockProblemSignature, priceStockWriteState } from './integration-helpers'

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

  it('warns that changes made while writes were off are not sent', () => {
    expect(automaticWritesBaselineMessage('N11’e')).toContain(automaticWritesBaselineWarning)
    expect(automaticWritesBaselineWarning).toBe('Kapalıyken yapılan değişiklikler pazaryerine gönderilmedi. Açmadan önce pazaryerlerindeki stok ve fiyatları güncellemenizi öneririz.')
  })
})
