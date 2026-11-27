import { describe, expect, it } from 'vitest'
import { filterJobsByTab, jobPlatformCode, jobPlatformTabs, activeJobTypes, countsByStatus, formatSeconds, laneLabel, laneTotals, usagePercent, usageTone, windowSince, type JobTypeMetric } from './queue-quota'
import { jobsMainTab, withJobsMainTab } from './jobs-url-state'

const metric = (jobType: string, queued: number, running: number, attempts: number): JobTypeMetric =>
  ({ jobType, queued, running, attempts, failed: 0, averageWaitSeconds: null, p95WaitSeconds: null, averageDurationSeconds: null, p95DurationSeconds: null })
// A lane within its wait target (GET /jobs/metrics/lanes, worker lanes step 1).
const withinTarget = (targetSeconds: number) =>
  ({ targetSeconds, recentP95WaitSeconds: null, oldestWaitSeconds: null, waitStatus: 'OK', alertSince: null, borrowed: null, waitCauses: null })

describe('Kuyruk ve Kota', () => {
  it('adds the lanes up and names them in plain words', () => {
    expect(laneTotals([
      { lane: 'HOT', attempts: 10, averageWaitSeconds: 1, p95WaitSeconds: 2, queued: 3, running: 2, slots: 3, ...withinTarget(60) },
      { lane: 'USER', attempts: 4, averageWaitSeconds: null, p95WaitSeconds: null, queued: 0, running: 1, slots: 2, ...withinTarget(5) }
    ])).toEqual({ running: 3, queued: 3, slots: 5, attempts: 14 })
    expect(laneLabel('HOT')).toBe('Öncelikli İşler')
    expect(laneLabel('NEW_LANE')).toBe('New Lane')
  })

  it('shows usage against a limit, full above it', () => {
    expect(usagePercent(2, 3)).toBe(67)
    expect(usagePercent(9, 3)).toBe(100)
    expect(usagePercent(1, 0)).toBe(0)
    expect([usageTone(10), usageTone(60), usageTone(95)]).toEqual(['ok', 'busy', 'full'])
  })

  it('formats waits and durations', () => {
    expect(formatSeconds(null)).toBe('—')
    expect(formatSeconds(8.4)).toBe('8 sn')
    expect(formatSeconds(210)).toBe('3,5 dk')
    expect(formatSeconds(4320)).toBe('1,2 sa')
  })

  it('lists busy job types first and leaves idle ones out', () => {
    expect(activeJobTypes([metric('A', 0, 0, 5), metric('B', 4, 1, 0), metric('C', 0, 0, 0)]).map(row => row.jobType)).toEqual(['B', 'A'])
  })

  it('adds the job counts up by state, open work first', () => {
    expect(countsByStatus([{ status: 'SUCCEEDED', jobType: 'A', count: 5 }, { status: 'PENDING', jobType: 'A', count: 2 }, { status: 'SUCCEEDED', jobType: 'B', count: 1 }]))
      .toEqual([{ status: 'PENDING', label: 'Bekliyor', count: 2 }, { status: 'SUCCEEDED', label: 'Başarılı', count: 6 }])
  })

  it('reads the window from the minute', () => {
    expect(windowSince('1', Date.parse('2026-11-13T10:30:45Z'))).toBe('2026-11-13T09:30:00.000Z')
  })

  it('has its own address beside the other tabs', () => {
    const opened = withJobsMainTab(new URLSearchParams('kategori=fatura&sekme=loglar&kayit=gonderimler'), 'queue')
    expect(opened.toString()).toBe('kategori=fatura&sekme=kuyruk-kota')
    expect(jobsMainTab(opened)).toBe('queue')
    expect(jobsMainTab(withJobsMainTab(opened, 'notifications'))).toBe('notifications')
  })

  it('groups job types by platform tab', () => {
    expect(jobPlatformCode('HEPSIBURADA_ORDER_SYNC')).toBe('HEPSIBURADA')
    expect(jobPlatformCode('PTTAVM_CATALOG')).toBe('PTTAVM')
    expect(jobPlatformCode('QUESTION_SYNC')).toBeNull()
    expect(jobPlatformCode('INVOICE_SUBMIT')).toBeNull()
    const rows = [metric('TRENDYOL_ORDER_SYNC', 1, 0, 1), metric('PAZARAMA_CATALOG', 0, 0, 2), metric('TRENDYOL_CATALOG', 0, 0, 1), metric('QUESTION_SYNC', 0, 0, 3)]
    expect(jobPlatformTabs(rows)).toEqual([
      { value: 'ALL', label: 'Tümü', count: 4 }, { value: 'TRENDYOL', label: 'Trendyol', count: 2 },
      { value: 'PAZARAMA', label: 'Pazarama', count: 1 }, { value: 'GENERAL', label: 'Genel', count: 1 }
    ])
    expect(filterJobsByTab(rows, 'TRENDYOL').map(row => row.jobType)).toEqual(['TRENDYOL_ORDER_SYNC', 'TRENDYOL_CATALOG'])
    expect(filterJobsByTab(rows, 'GENERAL').map(row => row.jobType)).toEqual(['QUESTION_SYNC'])
    expect(filterJobsByTab(rows, 'N11')).toHaveLength(4)
  })
})

import { filterByMarketplace, laneWaitCauseLines, latencySeconds, latencySourceLabel, latencyState, marketplaceTabs, quotaPeakText, type QuotaUsage } from './queue-quota'
describe('queue phase 0', () => {
  const bucket = (platformCode: string, limitPerMinute: number | null, peakMinuteRequests: number): QuotaUsage =>
    ({ platformCode, storeName: 'Mağaza', bucket: `${platformCode}:X`, label: 'Okuma', limitPerMinute, lastHourRequests: 10, peakMinuteRequests, averagePerMinute: 0.2, shared: false })

  it('tabs the quota buckets by marketplace and shows a bucket without a limit by its use alone', () => {
    const rows = [bucket('PAZARAMA', 30, 27), bucket('TRENDYOL', 12, 8), bucket('TRENDYOL', null, 3)]
    expect(marketplaceTabs(rows)).toEqual([
      { value: 'ALL', label: 'Tümü', count: 3 }, { value: 'TRENDYOL', label: 'Trendyol', count: 2 }, { value: 'PAZARAMA', label: 'Pazarama', count: 1 }
    ])
    expect(filterByMarketplace(rows, 'TRENDYOL')).toHaveLength(2)
    expect(filterByMarketplace(rows, 'N11')).toHaveLength(3)
    expect(quotaPeakText(rows[0])).toBe('27 / 30 dk')
    expect(quotaPeakText(rows[2])).toBe('3 / dk')
  })

  it('shows the panel’s sub-second part of an order with a decimal', () => {
    expect([latencySeconds(0.4), latencySeconds(8), latencySeconds(84), latencySeconds(null)]).toEqual(['0,4 sn', '8 sn', '1,4 dk', '—'])
  })

  it('names how an order came and colours a flow only when it has a target', () => {
    expect([latencySourceLabel('ALL'), latencySourceLabel('WEBHOOK'), latencySourceLabel('POLLING')]).toEqual(['Tüm Siparişler', 'Webhook ile', 'Okuma ile'])
    expect(latencyState('OK')).toEqual({ label: 'Hedefte', tone: 'ok' })
    expect(latencyState('WARN')?.tone).toBe('warn')
    expect(latencyState('BAD')?.tone).toBe('bad')
    expect(latencyState(null)).toBeNull()
  })

  it('lists a lane’s wait causes once any attempt recorded them', () => {
    expect(laneWaitCauseLines(null)).toEqual([])
    expect(laneWaitCauseLines({ attempts: 2, waitSeconds: 50, lockSeconds: 25, backoffSeconds: 4, quotaHoldSeconds: 0, slotSeconds: 5, unknownSeconds: 16 })).toEqual([
      'Bekleme nedeni (2 deneme, toplam 50 sn)', 'Aynı bağlantının kilidi: 25 sn', 'Kota geri çekilmesi: 4 sn', 'Kota bekletmesi: 0 sn', 'Boş slot yok: 5 sn', 'Nedeni bilinmeyen: 16 sn'
    ])
  })
})

import { laneWaitState as waitState } from './queue-quota'
describe('lane wait state', () => {
  it('names the three states the API reports', () => {
    expect(waitState('OK')).toEqual({ label: 'Hedefte', tone: 'ok' })
    expect(waitState('OVER_TARGET')).toEqual({ label: 'Hedef Aşıldı', tone: 'warn' })
    expect(waitState('ALERT')).toEqual({ label: 'Uyarı', tone: 'bad' })
  })
})
