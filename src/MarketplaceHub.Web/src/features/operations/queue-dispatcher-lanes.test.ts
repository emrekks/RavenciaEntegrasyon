import { describe, expect, it } from 'vitest'
import { isDispatcherModel, jobLaneText, laneCountsFor, laneLabel, lanePlatformTabs, laneSlotKind, type LaneMetric } from './queue-quota'

// Queue phase 2: the dispatcher's lanes (Worker:LaneModel=dispatcher) and the lane table's marketplace tabs.
describe('queue phase 2 dispatcher lanes', () => {
  const lane = (code: string, reserved: number | null, share: number | null, model: string, platforms: LaneMetric['platforms'] = null): LaneMetric => ({
    lane: code, attempts: 0, averageWaitSeconds: null, p95WaitSeconds: null, queued: 3, running: 2, slots: (reserved ?? 0) + (share ?? 0), targetSeconds: 5,
    recentP95WaitSeconds: null, oldestWaitSeconds: null, waitStatus: 'OK', alertSince: null, borrowed: null, waitCauses: null, reserved, share, platforms, model
  })

  it('names the work-type lanes and says what their slots are', () => {
    expect(laneLabel('SIPARIS_OKUMA')).toBe('Sipariş Okuma')
    expect(laneLabel('ARKA_PLAN')).toBe('Arka Plan')
    expect(laneSlotKind(lane('WEBHOOK', 1, 0, 'dispatcher'))?.text).toBe('Ayrılmış 1')
    expect(laneSlotKind(lane('KULLANICI', 1, 1, 'dispatcher'))?.text).toBe('Ayrılmış 1\nPay 1')
    expect(laneLabel('TOPLU')).toBe('Toplu İşler')
    expect(laneLabel('YAZMA')).toBe('Pazaryerine Yazma')
    expect(laneSlotKind(lane('SIPARIS_OKUMA', 3, 0, 'dispatcher'))?.text).toBe('Asgari 3')
    expect(laneSlotKind(lane('KATALOG', 0, 3, 'dispatcher'))?.text).toBe('Pay 3')
    // The earlier lanes send no such figures: nothing is shown, never a guess.
    expect(laneSlotKind(lane('HOT', null, null, 'flexible'))).toBeNull()
    expect(isDispatcherModel([lane('KATALOG', 0, 3, 'dispatcher')])).toBe(true)
    expect(isDispatcherModel([lane('HOT', null, null, 'flexible')])).toBe(false)
  })

  it('offers the marketplaces with work as tabs and shows a lane\'s jobs of one marketplace', () => {
    const lanes = [
      lane('SIPARIS_OKUMA', 3, 0, 'dispatcher', [{ platformCode: 'TRENDYOL', queued: 1, running: 1 }, { platformCode: 'N11', queued: 2, running: 1 }]),
      lane('ARKA_PLAN', 0, 2, 'dispatcher', [{ platformCode: '', queued: 3, running: 2 }])
    ]
    expect(lanePlatformTabs(lanes).map(tab => tab.value)).toEqual(['ALL', 'TRENDYOL', 'N11'])
    expect(laneCountsFor(lanes[0], 'N11')).toEqual({ running: 1, queued: 2 })
    expect(laneCountsFor(lanes[1], 'N11')).toEqual({ running: 0, queued: 0 })
    expect(laneCountsFor(lanes[1], 'ALL')).toEqual({ running: 2, queued: 3 })
  })

  it('says outside its share for the dispatcher lanes and on a borrowed slot for the earlier ones', () => {
    expect(jobLaneText([{ lane: 'ARKA_PLAN', attempts: 4, borrowed: 1 }]).title).toContain('1 payının dışında')
    expect(jobLaneText([{ lane: 'BACKGROUND', attempts: 4, borrowed: 1 }]).title).toContain('1 ödünç slotta')
  })
})
