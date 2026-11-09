import { describe, expect, it } from 'vitest'
import { lostListingLines, priceStockMonitorNotice, statusExtras } from './PriceStockMonitorPanel'
import { automaticWritesBaselineMessage, automaticWritesBaselineWarning, orderSourceHint, orderSourceState, priceStockMetricTone, priceStockMonitorSummary, priceStockNeedsAttention, priceStockProblemSignature, priceStockWriteState, relativeAgeText, type PriceStockConnectionMonitor } from './integration-helpers'

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

  it('folds the counts into one problem marker whose tooltip keeps every number; a clean row has none', () => {
    expect(statusExtras(connection()).problem).toBeNull()
    expect(statusExtras(connection({ queuedJobs: 4 })).attention).toBe(false)
    const lost = statusExtras(connection({ notFoundLinks: 48, failedPushes7d: 1, manualReviewPushes: 2, invalidOffers: 3 }))
    expect(lost.problem).toMatchObject({ text: '54', tone: 'bad' })
    expect(lost.problem!.title).toContain('1 başarısız gönderim · 2 kısmi / inceleme bekliyor · 3 gönderilemeyen teklif')
    expect(lost.problem!.title).toContain('Pazaryerinde bulunamayan ilan: 48 varyant bulunamadı.')
    expect(statusExtras(connection({ notFoundLinks: 48 })).problem).toMatchObject({ text: '48', tone: 'warn' })
    expect(statusExtras(connection()).write).toMatchObject({ label: 'Kapalı', tone: 'off' })
  })

  // Live 9 Nov 2026: Shopify's "48 kayıp ilan" were two products gone from the store, shown as a price/stock send problem
  // while writes were off. The tooltip names them and says no send is involved.
  const shopifyLost = connection({
    connectionId: 'shopify', platformCode: 'SHOPIFY', displayName: 'Shopify', notFoundLinks: 48,
    lostListings: [
      { productId: 'p1', title: 'Kışlık Yonca Çiçek Desen', healthStatus: 'NOT_FOUND', variants: 24, skus: Array.from({ length: 24 }, (_, i) => `MZ050YNC-${String(i + 1).padStart(2, '0')}`), since: '2026-11-03T00:14:48Z' },
      { productId: 'p2', title: 'Kışlık Yonca Yaka Etek', healthStatus: 'NOT_FOUND', variants: 24, skus: ['MZ036YNC-01', 'MZ036YNC-24'], since: '2026-11-03T00:14:48Z' }
    ]
  })

  it('names the lost listings by product and stock codes and says no send is involved while writes are off', () => {
    const lines = lostListingLines(shopifyLost)
    expect(lines[0]).toBe('Pazaryerinde bulunamayan ilan: 48 varyant bulunamadı (2 ürün).')
    expect(lines.join('\n')).toContain('Bu bir gönderim hatası değil')
    expect(lines).toContain('Otomatik fiyat-stok gönderimi kapalı; bu ilanlara bir şey gönderilmiyor.')
    expect(lines).toContain('• Kışlık Yonca Çiçek Desen · 24 varyant bulunamadı (MZ050YNC-01 … MZ050YNC-24) · 03.11.2026 tarihinden beri')
    expect(lines).toContain('• Kışlık Yonca Yaka Etek · 24 varyant bulunamadı (MZ036YNC-01, MZ036YNC-24) · 03.11.2026 tarihinden beri')
    expect(lostListingLines(connection())).toEqual([])
    expect(lostListingLines({ ...shopifyLost, automaticWritesOpen: true }).join('\n')).not.toContain('bu ilanlara bir şey gönderilmiyor')
  })

  it('tells lost listings apart from price/stock send problems in the notice above the table', () => {
    expect(priceStockMonitorNotice(undefined)).toBeNull()
    expect(priceStockMonitorNotice([connection()])).toBeNull()
    const lostOnly = priceStockMonitorNotice([shopifyLost, connection()])!
    expect(lostOnly).not.toContain('fiyat/stok gönderimi')
    expect(lostOnly).toBe('Shopify: 48 varyantın ilanı pazaryerinde bulunamıyor. Ayrıntı ve ürün listesi Sorun hücresinin ipucunda.')
    expect(priceStockMonitorNotice([shopifyLost, connection({ connectionId: 'ty', failedPushes7d: 2 })]))
      .toBe('1 bağlantıda sorunlu fiyat/stok gönderimi var. Shopify: 48 varyantın ilanı pazaryerinde bulunamıyor. Ayrıntı ve ürün listesi Sorun hücresinin ipucunda.')
  })

  it('keeps the order-source details in a tooltip sentence', () => {
    const now = Date.parse('2026-11-07T12:00:00Z')
    expect(orderSourceHint({ orderSource: 'WEBHOOK', orderReadIntervalSeconds: 480, lastNotificationAt: '2026-11-07T11:00:00Z' }, now)).toBe('Yeni siparişler bildirimle gelir · yedek okuma 8 dakikada bir · son bildirim 1 saat önce')
    expect(orderSourceHint({ orderSource: 'POLLING', orderReadIntervalSeconds: 120, lastNotificationAt: null }, now)).toBe('Bu bağlantıda bildirim yok · siparişler 2 dakikada bir okunuyor')
    expect(orderSourceHint({ orderSource: undefined as never, orderReadIntervalSeconds: null, lastNotificationAt: null }, now)).toBe('Sipariş kaynağı bildirilmedi.')
  })

  it('warns that changes made while writes were off are not sent', () => {
    expect(automaticWritesBaselineMessage('N11’e')).toContain(automaticWritesBaselineWarning)
    expect(automaticWritesBaselineWarning).toBe('Kapalıyken yapılan değişiklikler pazaryerine gönderilmedi. Açmadan önce pazaryerlerindeki stok ve fiyatları güncellemenizi öneririz.')
  })
})
