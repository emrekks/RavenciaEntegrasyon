import { describe, expect, it } from 'vitest'
import { authCell, durationText, errorRateCell, lagCell, queueCell, rateLimitCell, signalDetail, webhookCell, type ConnectionSignals } from './api-health-signals'

const now = Date.parse('2026-11-07T12:00:00Z')

describe('API Sağlığı signal cells', () => {
  it('shows a key refusal in red while no success followed it, otherwise Geçerli', () => {
    const rejected = authCell({ state: 'rejected', lastRejectedAt: '2026-11-07T10:00:00Z', lastRejectedCode: 'N11_CREDENTIALS_REJECTED', lastOkAt: null }, now)
    expect(rejected).toMatchObject({ text: '2 sa önce reddedildi', tone: 'bad' })
    expect(rejected.title).toContain('N11_CREDENTIALS_REJECTED')
    expect(authCell({ state: 'valid', lastRejectedAt: null, lastRejectedCode: null, lastOkAt: '2026-11-07T11:00:00Z' }, now)).toMatchObject({ text: 'Geçerli', tone: 'ok' })
    expect(authCell({ state: 'unknown', lastRejectedAt: null, lastRejectedCode: null, lastOkAt: null }, now).text).toBe('Bilinmiyor')
  })

  it('names the worst lagging stream with its interval; on time reads Güncel', () => {
    expect(lagCell({ stream: 'ORDERS', lagSeconds: 2400, expectedSeconds: 120, level: 'fault' })).toMatchObject({ text: 'Sipariş 40 dk geride', tone: 'bad' })
    expect(lagCell({ stream: 'ORDERS', lagSeconds: 2400, expectedSeconds: 120, level: 'fault' }).title).toContain('2 dk\'da bir')
    expect(lagCell({ stream: 'RETURNS', lagSeconds: 2000, expectedSeconds: 600, level: 'warn' }).tone).toBe('warn')
    expect(lagCell({ stream: 'QUESTIONS', lagSeconds: 100, expectedSeconds: 120, level: 'ok' }).text).toBe('Güncel')
    expect(lagCell(null).text).toBe('—')
  })

  it('computes the error rate from remote, our and quota failures and splits the tooltip', () => {
    const cell = errorRateCell({ attempts: 200, failed: 12, remoteSide: 3, ourSide: 2, rateLimited: 1, workflowWait: 6 })
    expect(cell).toMatchObject({ text: '%3', tone: 'warn' })
    expect(cell.title).toContain('3 pazaryeri tarafı (sunucu hatası, zaman aşımı, ağ) · 2 bizim taraf (ret) · 1 kota sınırı · 6 iş akışı beklemesi')
    expect(errorRateCell({ attempts: 10, failed: 0, remoteSide: 0, ourSide: 0, rateLimited: 0, workflowWait: 0 })).toMatchObject({ text: '%0', tone: 'ok' })
    expect(errorRateCell({ attempts: 0, failed: 0, remoteSide: 0, ourSide: 0, rateLimited: 0, workflowWait: 0 }).text).toBe('—')
  })

  it('shows the 429 count, or a quota pause while one is active', () => {
    expect(rateLimitCell({ refused24h: 0, waitingJobs: 0, pausedUntil: null, lastRunRefusals: 0 }, now)).toMatchObject({ text: '0', tone: 'ok' })
    expect(rateLimitCell({ refused24h: 4, waitingJobs: 1, pausedUntil: null, lastRunRefusals: 2 }, now)).toMatchObject({ text: '4', tone: 'warn' })
    expect(rateLimitCell({ refused24h: 1, waitingJobs: 1, pausedUntil: '2026-11-07T12:05:00Z', lastRunRefusals: 0 }, now).text).toBe('Kota Bekliyor')
  })

  it('shows the age of the oldest due job, and Yok when nothing waits', () => {
    expect(queueCell({ ageSeconds: 1800, jobType: 'N11_ORDER_SYNC', pending: 3 })).toMatchObject({ text: '30 dk', tone: 'ok' })
    expect(queueCell({ ageSeconds: 7200, jobType: 'N11_ORDER_SYNC', pending: 1 }).tone).toBe('warn')
    expect(queueCell(null).text).toBe('Yok')
  })

  it('shows the last notification where a webhook is used and says what is not measured', () => {
    const cell = webhookCell({ used: true, lastReceivedAt: '2026-11-07T11:48:00Z', healthy: true, lastOrderReadAt: '2026-11-07T11:58:00Z', fallbackReadSeconds: 900 }, now)
    expect(cell).toMatchObject({ text: '12 dk önce', tone: 'ok' })
    expect(cell.title).toContain('yedek okuma 15 dk\'da bir')
    expect(cell.title).toContain('Ölçülmüyor')
    expect(webhookCell({ used: true, lastReceivedAt: null, healthy: false, lastOrderReadAt: null, fallbackReadSeconds: null }, now)).toMatchObject({ text: 'Bildirim yok', tone: 'warn' })
    expect(webhookCell({ used: false, lastReceivedAt: null, healthy: false, lastOrderReadAt: null, fallbackReadSeconds: null }, now).text).toBe('Yok')
  })

  it('formats durations', () => {
    expect([durationText(30), durationText(2400), durationText(7200), durationText(3 * 86400)]).toEqual(['30 sn', '40 dk', '2 sa', '3 gün'])
  })
})

describe('API Sağlığı detail window facts', () => {
  const row = { connectionId: 'c1', displayName: 'Pazarama', platformCode: 'PAZARAMA',
    auth: { state: 'rejected', lastRejectedAt: '2026-11-07T10:00:00Z', lastRejectedCode: 'PZ_401', lastOkAt: null },
    lag: { stream: 'ORDERS', lagSeconds: 2400, expectedSeconds: 120, level: 'fault' },
    errorRate: { attempts: 1425, failed: 1, remoteSide: 0, ourSide: 1, rateLimited: 0, workflowWait: 28 },
    rateLimit: { refused24h: 4, waitingJobs: 2, lastRunRefusals: 1, pausedUntil: null },
    queue: { jobType: 'ORDER_SYNC', ageSeconds: 7200, pending: 9 },
    webhook: { used: false, healthy: true, lastReceivedAt: null, lastOrderReadAt: null, fallbackReadSeconds: null } } as unknown as ConnectionSignals

  it('Hata Oranı: the title names the store and the 24 hours, the four counts and the rate come straight from the data', () => {
    const detail = signalDetail('errorRate', row)
    expect(detail.title).toBe('Hata Oranı · Pazarama (son 24 saat)')
    expect(Object.fromEntries(detail.facts)).toMatchObject({ 'Toplam deneme': '1.425', 'Bizim taraf (ret)': '1', 'Kota sınırı': '0', 'İş akışı beklemesi / diğer': '28', 'Hata oranı': '%0,1' })
    expect(detail.lead).toContain('toplam deneme')
  })

  it('the other signals list their stored values and invent nothing', () => {
    expect(Object.fromEntries(signalDetail('auth', row, now).facts)).toMatchObject({ Durum: 'Reddedildi', 'Son ret kodu': 'PZ_401' })
    expect(Object.fromEntries(signalDetail('lag', row).facts)).toMatchObject({ Akış: 'Sipariş', Seviye: 'Arıza', Zamanlama: expect.stringContaining('dk') })
    expect(Object.fromEntries(signalDetail('rateLimit', row, now).facts)).toMatchObject({ 'Kotaya takılan deneme (24 sa)': '4', 'Kota bekleyen iş': '2', 'Kota duraklaması': 'Yok' })
    expect(Object.fromEntries(signalDetail('queue', row).facts)).toMatchObject({ 'En eski çalışabilir iş': 'ORDER_SYNC', 'Bekleme süresi': '2 sa', 'Çalışmayı bekleyen toplam iş': '9' })
    expect(signalDetail('webhook', row).facts).toEqual([['Durum', 'Kullanılmıyor']])
  })
})
