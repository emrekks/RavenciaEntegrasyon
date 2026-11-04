import { describe, expect, it } from 'vitest'
import { automaticWritesBaselineMessage, automaticWritesBaselineWarning, orderSourceState, priceStockMetricTone, priceStockMonitorSummary, priceStockNeedsAttention, priceStockProblemSignature, priceStockWriteState, relativeAgeText, type PriceStockConnectionMonitor } from './integration-helpers'

function connection(overrides: Partial<PriceStockConnectionMonitor> = {}): PriceStockConnectionMonitor {
  return {
    connectionId: 'c1', platformCode: 'TRENDYOL', displayName: 'Trendyol', status: 'ACTIVE', masterSwitchOn: true, priceWriteEnabled: false, stockWriteEnabled: false,
    automaticWritesOpen: false, lastSuccessfulPushAt: null, queuedJobs: 0, waitingForOrderRead: 0, failedPushes7d: 0, manualReviewPushes: 0, invalidOffers: 0,
    notFoundLinks: 0, rejectedLinks: 0, latestFailure: null, invalidOfferSamples: [], orderSource: 'WEBHOOK', lastNotificationAt: null, orderReadIntervalSeconds: 480,
    ...overrides
  }
}

describe('price/stock monitor summary and tones', () => {
  it('mutes zero counts, makes failures bad and other counts a warning', () => {
    expect(priceStockMetricTone(0, 'failed')).toBe('muted')
    expect(priceStockMetricTone(0, 'queue')).toBe('muted')
    expect(priceStockMetricTone(2, 'failed')).toBe('bad')
    expect(priceStockMetricTone(1, 'queue')).toBe('warn')
    expect(priceStockMetricTone(3, 'invalid')).toBe('warn')
    expect(priceStockMetricTone(48, 'lost')).toBe('warn')
  })

  it('flags a connection with failures, held offers, lost listings or an unhealthy webhook, not a plain queue', () => {
    expect(priceStockNeedsAttention(connection())).toBe(false)
    expect(priceStockNeedsAttention(connection({ queuedJobs: 4 }))).toBe(false)
    expect(priceStockNeedsAttention(connection({ manualReviewPushes: 1 }))).toBe(true)
    expect(priceStockNeedsAttention(connection({ notFoundLinks: 48 }))).toBe(true)
    expect(priceStockNeedsAttention(connection({ orderSource: 'WEBHOOK_FALLBACK' }))).toBe(true)
  })

  it('counts connections, writes, order sources and those needing attention; an unreported source stays unknown', () => {
    const summary = priceStockMonitorSummary([
      connection({ connectionId: 'hb', orderSource: 'WEBHOOK', notFoundLinks: 48 }),
      connection({ connectionId: 'n11', orderSource: 'POLLING' }),
      connection({ connectionId: 'ty', orderSource: 'WEBHOOK_FALLBACK', masterSwitchOn: false }),
      connection({ connectionId: 'pz', orderSource: undefined, automaticWritesOpen: true, priceWriteEnabled: true, stockWriteEnabled: true }),
      connection({ connectionId: 'pt', priceWriteEnabled: true })
    ])
    expect(summary).toEqual({ connections: 5, writesOpen: 1, writesHeld: 1, writesOff: 3, webhook: 2, webhookTrouble: 1, polling: 1, unknownSource: 1, needsAttention: 2 })
    expect(orderSourceState({ orderSource: undefined, orderReadIntervalSeconds: 120 })).toEqual({ label: 'Bilinmiyor', detail: null, tone: 'off' })
  })

  it('says how long ago a time was and keeps an unknown time unknown', () => {
    const now = Date.parse('2026-11-04T12:00:00Z')
    expect(relativeAgeText(null, now)).toBeNull()
    expect(relativeAgeText('not a date', now)).toBeNull()
    expect(relativeAgeText('2026-11-04T11:59:40Z', now)).toBe('Az önce')
    expect(relativeAgeText('2026-11-04T12:00:30Z', now)).toBe('Az önce')
    expect(relativeAgeText('2026-11-04T11:48:00Z', now)).toBe('12 dk önce')
    expect(relativeAgeText('2026-11-04T07:00:00Z', now)).toBe('5 sa önce')
    expect(relativeAgeText('2026-11-01T12:00:00Z', now)).toBe('3 gün önce')
  })
})

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
