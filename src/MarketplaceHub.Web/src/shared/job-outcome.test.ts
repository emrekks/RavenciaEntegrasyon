import { describe, expect, it } from 'vitest'
import { followJobOutcomes, groupedOutcomeFeedback, jobOutcome, jobOutcomeFeedback, outcomeTarget, type FollowedJob, type FollowedJobDetail, type JobOutcomeFeedback } from './job-outcome'

describe('many jobs of one press (Toplu Yayın, a store-wide push)', () => {
  const jobs: FollowedJob[] = [
    { jobId: 'a', platformName: 'Trendyol', what: 'ürün', label: 'Elbise' },
    { jobId: 'b', platformName: 'Trendyol', what: 'ürün', label: 'Gömlek' },
    { jobId: 'c', platformName: 'n11', what: 'ürün', label: 'Etek' },
    { jobId: 'd', platformName: 'n11', what: 'ürün', label: 'Ceket' }
  ]
  const one = (status: string, summary: string | null = null): FollowedJobDetail => ({ job: { id: 'x', status, lastErrorCode: null, lastErrorSummary: summary } })

  it('counts each marketplace by outcome and names the refused records with their reasons', () => {
    const outcomes = [
      jobOutcome(jobs[0], one('SUCCEEDED')),
      jobOutcome(jobs[1], one('BLOCKED', 'Desi eksik: GM-1 varyantına desi girin.')),
      jobOutcome(jobs[2], one('SUCCEEDED')),
      jobOutcome(jobs[3], one('LEASED'))
    ]
    expect(groupedOutcomeFeedback(jobs, outcomes, '2 Platforma')).toEqual({
      title: '2 Platforma Kısmen Aktarıldı', kind: 'warning', settled: false,
      lines: ['Trendyol: 1 ürün aktarıldı, 1 ürün aktarılamadı.', 'n11: 1 ürün aktarıldı, 1 ürün sürüyor.', 'Trendyol · Gömlek: Desi eksik: GM-1 varyantına desi girin.']
    })
  })

  it('reads the jobs in one call and never calls a running batch a success', async () => {
    const asked: string[][] = []
    let round = 0
    let time = 0
    const last = await followJobOutcomes(jobs, '2 Platforma', () => undefined, {
      now: () => (time += 1000), sleep: async () => undefined,
      readJobs: async ids => { asked.push([...ids]); round++; return new Map(ids.map(id => [id, one(round > 1 ? 'SUCCEEDED' : 'LEASED')])) },
      summarize: outcomes => groupedOutcomeFeedback(jobs, outcomes, '2 Platforma')
    })
    expect(asked[0]).toEqual(['a', 'b', 'c', 'd'])
    expect(round).toBe(2)
    expect(last).toMatchObject({ title: '2 Platforma Aktarıldı', kind: 'success', lines: ['Trendyol: 2 ürün aktarıldı.', 'n11: 2 ürün aktarıldı.'] })
  })
})

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
    expect(jobOutcomeFeedback([outcome], 'Trendyol’a')).toMatchObject({ title: 'İnceleme Bekliyor', kind: 'warning', settled: true })
  })

  it('names the refused variants and their reasons, and a partial result is not a success', () => {
    const outcome = jobOutcome(trendyol, detail('SUCCEEDED', {}, [variant('RYP003T-T17', 'SENT'), variant('RYP003T-T18', 'SENT'), variant('RYP003T-T20', 'APPROVED'), variant('RYP003T-T19', 'REJECTED', 'Desi eksik.')]))
    expect(outcome).toMatchObject({ state: 'partial', line: 'Trendyol: 3 varyant aktarıldı, 1 varyant aktarılamadı — RYP003T-T19: Desi eksik.', reason: 'RYP003T-T19: Desi eksik' })
    expect(jobOutcomeFeedback([outcome], 'Trendyol’a')).toMatchObject({ title: 'Trendyol’a Kısmen Aktarıldı', kind: 'warning' })
  })

  it('counts the variants left out as already live as live, not refused (RYP006: the missing colour sent)', () => {
    const live = (code: string) => variant(code, 'NOT_SENT', 'Daha önceki bir yayınla zaten yayında; bu gönderime alınmadı.')
    const outcome = jobOutcome(trendyol, detail('SUCCEEDED', {}, [live('RYP006-01'), live('RYP006-02'), variant('RYP006-19', 'SENT'), variant('RYP006-20', 'SENT')]))
    expect(outcome).toMatchObject({ state: 'succeeded', line: 'Trendyol: 2 varyant aktarıldı; 2 varyant zaten yayında.' })
    const mixed = jobOutcome(trendyol, detail('SUCCEEDED', {}, [live('RYP006-01'), variant('RYP006-19', 'SENT'), variant('RYP006-21', 'NOT_SENT', 'Desi eksik.')]))
    expect(mixed.line).toBe('Trendyol: 1 varyant aktarıldı; 1 varyant zaten yayında, 1 varyant aktarılamadı — RYP006-21: Desi eksik.')
  })

  it('reads a blocked job as not transferred, and a variant without a reason says so', () => {
    expect(jobOutcome(trendyol, detail('BLOCKED', { lastErrorSummary: 'Trendyol ürünü reddetti: kategori kapalı.' })).line).toBe('Trendyol: aktarılamadı — Trendyol ürünü reddetti: kategori kapalı.')
    expect(jobOutcome(trendyol, detail('SUCCEEDED', {}, [variant('A-1', 'REJECTED')])).line).toBe('Trendyol: aktarılamadı — A-1: neden bildirilmedi.')
  })

  it('keeps a retry after an error as scheduled and an unreadable job as unknown', () => {
    const scheduled = jobOutcome(trendyol, detail('RETRY_SCHEDULED', { lastErrorSummary: 'Trendyol istek sınırına ulaşıldı.', nextRetryAt: null }))
    expect(scheduled).toMatchObject({ state: 'scheduled', line: 'Trendyol: sırada — Trendyol istek sınırına ulaşıldı.' })
    expect(jobOutcomeFeedback([scheduled], 'Trendyol’a')).toMatchObject({ title: 'Aktarım Sırada', kind: 'info', settled: false })
    const unknown = jobOutcome(trendyol, null)
    expect(unknown.state).toBe('unknown')
    expect(jobOutcomeFeedback([unknown], 'Trendyol’a')).toMatchObject({ title: 'Sonuç Henüz Bilinmiyor', kind: 'info' })
  })

  it('titles the toast by the marketplaces it covers', () => {
    expect(outcomeTarget([trendyol])).toBe('Trendyol’a')
    expect(outcomeTarget([{ ...trendyol, platformName: 'Hepsiburada' }])).toBe('Hepsiburada’ya')
    expect(outcomeTarget([{ ...trendyol, platformName: 'n11' }])).toBe('n11’e')
    expect(outcomeTarget([{ ...trendyol, platformName: 'PttAVM' }])).toBe('PttAVM’ye')
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
    expect(updates[0][0]).toMatchObject({ title: 'İnceleme Bekliyor', kind: 'warning' })
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
