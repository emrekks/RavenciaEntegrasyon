import { describe, expect, it } from 'vitest'
import { followJobOutcomes, jobOutcome, jobOutcomeFeedback, outcomeTarget, type FollowedJob, type FollowedJobDetail, type JobOutcomeFeedback } from './job-outcome'

const trendyol: FollowedJob = { jobId: 'j1', platformName: 'Trendyol', what: 'ürün bilgisi' }
const detail = (status: string, extra: Partial<FollowedJobDetail['job']> = {}, items: NonNullable<FollowedJobDetail['variantResults']>['items'] = []): FollowedJobDetail => ({
  job: { id: 'j1', status, lastErrorCode: null, lastErrorSummary: null, ...extra },
  variantResults: { source: items.length ? 'RECORDED' : 'NONE', items }
})
const variant = (stockCode: string, status: string, reason: string | null = null) => ({ barcode: null, stockCode, status, reason })

describe('the result of a followed marketplace job', () => {
  it('reads a finished job as transferred', () => {
    expect(jobOutcome(trendyol, detail('SUCCEEDED'))).toEqual({ state: 'succeeded', line: 'Trendyol: ürün bilgisi aktarıldı.' })
  })

  // RYP003T, 7 Nov 2026: the update stopped for review 1.3 s after a success toast.
  it('reads a job stopped for review with its plain reason, never as a success', () => {
    const outcome = jobOutcome(trendyol, detail('MANUAL_REVIEW', { lastErrorCode: 'PRODUCT_UPDATE_STATE_INCOMPLETE', lastErrorSummary: 'Bu ürünün pazaryeri kaydı panelde eksik; güncelleme pazaryerine gönderilmedi.' }))
    expect(outcome.state).toBe('review')
    expect(outcome.line).toBe('Trendyol: inceleme bekliyor — Bu ürünün pazaryeri kaydı panelde eksik; güncelleme pazaryerine gönderilmedi.')
    expect(jobOutcomeFeedback([outcome], 'Trendyol’a')).toMatchObject({ title: 'İnceleme Bekliyor', kind: 'error', settled: true })
  })

  it('names the refused variants and their reasons, and a partial result is not a success', () => {
    const outcome = jobOutcome(trendyol, detail('SUCCEEDED', {}, [variant('RYP003T-T17', 'SENT'), variant('RYP003T-T18', 'SENT'), variant('RYP003T-T20', 'APPROVED'), variant('RYP003T-T19', 'REJECTED', 'Desi eksik.')]))
    expect(outcome).toEqual({ state: 'partial', line: 'Trendyol: 3 varyant aktarıldı, 1 varyant aktarılamadı — RYP003T-T19: Desi eksik.' })
    expect(jobOutcomeFeedback([outcome], 'Trendyol’a')).toMatchObject({ title: 'Kısmen Aktarıldı', kind: 'error' })
  })

  it('reads a blocked job as not transferred, and a variant without a reason says so', () => {
    expect(jobOutcome(trendyol, detail('BLOCKED', { lastErrorSummary: 'Trendyol ürünü reddetti: kategori kapalı.' })).line).toBe('Trendyol: aktarılamadı — Trendyol ürünü reddetti: kategori kapalı.')
    expect(jobOutcome(trendyol, detail('SUCCEEDED', {}, [variant('A-1', 'REJECTED')])).line).toBe('Trendyol: aktarılamadı — A-1: neden bildirilmedi.')
  })

  it('keeps a retry after an error as scheduled and an unreadable job as unknown', () => {
    const scheduled = jobOutcome(trendyol, detail('RETRY_SCHEDULED', { lastErrorSummary: 'Trendyol istek sınırına ulaşıldı.', nextRetryAt: null }))
    expect(scheduled).toEqual({ state: 'scheduled', line: 'Trendyol: sırada — Trendyol istek sınırına ulaşıldı.' })
    expect(jobOutcomeFeedback([scheduled], 'Trendyol’a')).toMatchObject({ title: 'Aktarım Sırada', kind: 'info', settled: false })
    const unknown = jobOutcome(trendyol, null)
    expect(unknown.state).toBe('unknown')
    expect(jobOutcomeFeedback([unknown], 'Trendyol’a')).toMatchObject({ title: 'Sonuç Henüz Bilinmiyor', kind: 'info' })
  })

  it('titles the toast by the marketplaces it covers', () => {
    expect(outcomeTarget([trendyol])).toBe('Trendyol’a')
    expect(outcomeTarget([{ ...trendyol, platformName: 'Hepsiburada' }])).toBe('Hepsiburada’ya')
    expect(outcomeTarget([{ ...trendyol, platformName: 'n11' }])).toBe('n11’e')
    expect(outcomeTarget([{ ...trendyol, platformName: 'PTT AVM' }])).toBe('PTT AVM’ye')
    expect(outcomeTarget([{ ...trendyol, platformName: 'Shopify' }])).toBe('Shopify’a')
    expect(outcomeTarget([trendyol, { ...trendyol, platformName: 'n11' }])).toBe('2 Platforma')
    expect(jobOutcomeFeedback([jobOutcome(trendyol, detail('SUCCEEDED'))], 'Trendyol’a')).toEqual({ title: 'Trendyol’a Aktarıldı', kind: 'success', lines: ['Trendyol: ürün bilgisi aktarıldı.'], settled: true })
  })
})

describe('following the jobs of one press', () => {
  const clock = () => {
    let time = 0
    return { now: () => time, sleep: async (ms: number) => { time += ms } }
  }

  it('turns "Aktarım Başladı" into the failure as soon as the job stops', async () => {
    const statuses = ['LEASED', 'MANUAL_REVIEW']
    const updates: Array<[JobOutcomeFeedback, boolean]> = []
    await followJobOutcomes([trendyol], 'Trendyol’a', (feedback, interim) => updates.push([feedback, interim]), {
      ...clock(), readJob: async () => detail(statuses.shift() ?? 'MANUAL_REVIEW', { lastErrorSummary: 'Desi eksik: RYP003T-T19 varyantına desi girin.' })
    })
    expect(updates).toHaveLength(1)
    expect(updates[0][1]).toBe(false)
    expect(updates[0][0]).toMatchObject({ title: 'İnceleme Bekliyor', kind: 'error' })
  })

  it('says the transfer is still running after the short wait, then gives the result when it comes', async () => {
    let reads = 0
    const updates: Array<[JobOutcomeFeedback, boolean]> = []
    await followJobOutcomes([trendyol], 'Trendyol’a', (feedback, interim) => updates.push([feedback, interim]), {
      ...clock(), shortWaitMs: 3000, pollMs: 1000, slowPollMs: 5000,
      readJob: async () => detail(++reads > 6 ? 'SUCCEEDED' : 'LEASED')
    })
    expect(updates.map(([feedback, interim]) => [feedback.title, interim])).toEqual([['Aktarım Sürüyor', true], ['Trendyol’a Aktarıldı', false]])
    expect(updates[0][0].lines).toEqual(['Trendyol: ürün bilgisi aktarımı sürüyor; sonuç gelince bildirilecek.'])
  })

  it('leaves a job that never settled unknown, never a success', async () => {
    const updates: Array<[JobOutcomeFeedback, boolean]> = []
    const last = await followJobOutcomes([trendyol], 'Trendyol’a', (feedback, interim) => updates.push([feedback, interim]), {
      ...clock(), shortWaitMs: 2000, longWaitMs: 10_000, pollMs: 1000, slowPollMs: 5000, readJob: async () => detail('LEASED')
    })
    expect(last.title).toBe('Sonuç Henüz Bilinmiyor')
    expect(last.kind).toBe('info')
    expect(last.lines[0]).toContain('sonucu henüz bilinmiyor')
    expect(updates.at(-1)?.[1]).toBe(false)
  })

  it('reads a job that cannot be read as unknown and keeps following it', async () => {
    let reads = 0
    const last = await followJobOutcomes([trendyol], 'Trendyol’a', () => undefined, {
      ...clock(), readJob: async () => { if (++reads < 3) throw new Error('ağ hatası'); return detail('SUCCEEDED') }
    })
    expect(last.title).toBe('Trendyol’a Aktarıldı')
  })
})
