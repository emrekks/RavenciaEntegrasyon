import { describe, expect, it } from 'vitest'
import { publishStatusSteps, publishStepsAriaLabel, publishStepsFit, publishStepTitle, type PublishStepsInput } from './publish-status-steps'

const base: PublishStepsInput = { requiredGaps: 0, optionalWarnings: 0, live: false, fallbackLabel: 'Henüz yayınlanmadı' }
const steps = (input: Partial<PublishStepsInput>) => publishStatusSteps({ ...base, ...input })

describe('publishStatusSteps', () => {
  it('reads a never sent card that is ready as Hazırlık done, Gönderim next', () => {
    const result = steps({ actualStatus: 'UNKNOWN' })
    expect(result.steps).toEqual(['done', 'next', 'idle', 'idle'])
    expect(result.links).toEqual(['half', 'empty', 'empty'])
    expect(result.label).toBe('Yayına Hazır')
    expect(result.counts).toEqual({})
  })

  it('reads a required gap as Hazırlık failed, with the gap and warning counts', () => {
    const result = steps({ requiredGaps: 2, optionalWarnings: 6 })
    expect(result.steps).toEqual(['fail', 'idle', 'idle', 'idle'])
    expect(result.label).toBe('Zorunlu Eksik')
    expect(result.counts).toEqual({ required: 2, optional: 6 })
  })

  it('reads a live card as four done steps and drops the warnings', () => {
    const result = steps({ live: true, actualStatus: 'LIVE', optionalWarnings: 3 })
    expect(result.steps).toEqual(['done', 'done', 'done', 'done'])
    expect(result.links).toEqual(['full', 'full', 'full'])
    expect(result.counts).toEqual({})
  })

  it('reads a partly live card whose rest is ready as Gönderim next and a half Yayında ring', () => {
    const result = steps({ actualStatus: 'LIVE', partial: { live: 21, total: 42, unit: 'varyant' } })
    expect(result.steps).toEqual(['done', 'next', 'idle', 'half'])
    expect(result.share).toBe(50)
    expect(result.counts).toEqual({ partial: '21/42' })
    expect(result.label).toBe('Kısmen Yayında')
  })

  it('reads a partly live card whose rest is blocked as Hazırlık failed and a half ring', () => {
    const result = steps({ actualStatus: 'LIVE', requiredGaps: 1, optionalWarnings: 2, partial: { live: 21, total: 42, unit: 'varyant' } })
    expect(result.steps).toEqual(['fail', 'idle', 'idle', 'half'])
    expect(result.counts).toEqual({ partial: '21/42', required: 1, optional: 2 })
    expect(publishStepsAriaLabel('Trendyol', result)).toBe('Trendyol: Kısmen Yayında, 21/42 varyant yayında; 1 zorunlu eksik, 2 uyarı')
  })

  it('reads a sent card awaiting approval as Onay now', () => {
    expect(steps({ actualStatus: 'APPROVAL_PENDING' }).steps).toEqual(['done', 'done', 'now', 'idle'])
    expect(steps({ actualStatus: 'CREATE_ACCEPTED' }).label).toBe('Onay Bekliyor')
    expect(steps({ actualStatus: 'CREATE_RESULT_UNKNOWN' }).steps).toEqual(['done', 'done', 'now', 'idle'])
    // PTT AVM has no approval: its wait is our own check.
    expect(steps({ actualStatus: 'APPROVAL_PENDING', checking: true }).label).toBe('Kontrol Ediliyor')
  })

  it('reads a queued or running send as Gönderim now', () => {
    expect(steps({ actualStatus: 'QUEUED' }).steps).toEqual(['done', 'now', 'idle', 'idle'])
    expect(steps({ actualStatus: 'UNKNOWN', job: { running: true, failed: false, step: 'send' } }).steps).toEqual(['done', 'now', 'idle', 'idle'])
  })

  it('reads a refusal as Onay failed, a partial one with a half ring', () => {
    expect(steps({ actualStatus: 'CREATE_REJECTED' }).steps).toEqual(['done', 'done', 'fail', 'idle'])
    const partly = steps({ actualStatus: 'PARTIAL_REJECTED', partial: { live: 18, total: 21, unit: 'varyant' }, missingVariants: 3 })
    expect(partly.steps).toEqual(['done', 'done', 'fail', 'half'])
    expect(partly.share).toBe(86)
    expect(partly.counts).toEqual({ partial: '18/21', missing: { count: 3, word: 'ret' } })
  })

  it('keeps an unknown share unknown: a ring with no number, never 0', () => {
    const result = steps({ actualStatus: 'PARTIAL_REJECTED' })
    expect(result.steps).toEqual(['done', 'done', 'fail', 'half'])
    expect(result.share).toBeNull()
    expect(result.counts).toEqual({})
  })

  it('reads a card the marketplace no longer knows as Yayında failed', () => {
    const result = steps({ notFound: true, actualStatus: 'LIVE', missingVariants: 2 })
    expect(result.steps).toEqual(['done', 'done', 'done', 'fail'])
    expect(result.label).toBe('Bulunamadı')
    expect(result.counts).toEqual({ missing: { count: 2, word: 'yok' } })
  })

  it('marks the step a failed job stopped at', () => {
    expect(steps({ job: { running: false, failed: true, step: 'send' } }).steps).toEqual(['done', 'fail', 'idle', 'idle'])
    expect(steps({ job: { running: false, failed: true, step: 'approval' } }).steps).toEqual(['done', 'done', 'fail', 'idle'])
    // A failed status check says nothing about the listing.
    expect(steps({ job: { running: false, failed: true, step: null } }).steps).toEqual(['done', 'next', 'idle', 'idle'])
    expect(steps({ lastJobStatus: 'DEAD' }).steps).toEqual(['done', 'fail', 'idle', 'idle'])
  })

  it('lets a required gap lead over an earlier failure', () => {
    expect(steps({ requiredGaps: 1, job: { running: false, failed: true, step: 'send' } }).steps).toEqual(['fail', 'idle', 'idle', 'idle'])
  })

  it('leaves every step grey while the card is still loading or cannot publish', () => {
    expect(steps({ settling: true, requiredGaps: 2 })).toMatchObject({ steps: ['idle', 'idle', 'idle', 'idle'], counts: {} })
    expect(steps({ readOnly: true, fallbackLabel: 'Ürün Yayını Desteklenmiyor' })).toMatchObject({ steps: ['idle', 'idle', 'idle', 'idle'], label: 'Ürün Yayını Desteklenmiyor' })
  })

  it('reads a PTT AVM family to send again as Gönderim next', () => {
    expect(steps({ actualStatus: 'APPROVAL_PENDING', retry: true })).toMatchObject({ steps: ['done', 'next', 'idle', 'idle'], label: 'Tekrar Deneyin' })
  })
})

describe('publish steps words', () => {
  it('titles each step with its state', () => {
    const result = steps({ actualStatus: 'LIVE', partial: { live: 21, total: 42, unit: 'varyant' } })
    expect(result.steps.map((state, index) => publishStepTitle(index, state, result))).toEqual(['Hazırlık · Tamam', 'Gönderim · Sırada', 'Onay · Bekliyor', 'Yayında · 21/42'])
  })

  it('asks for more room the more counts a pill carries', () => {
    const one = publishStepsFit(steps({ optionalWarnings: 2 }))
    const three = publishStepsFit(steps({ actualStatus: 'LIVE', requiredGaps: 1, optionalWarnings: 2, partial: { live: 21, total: 42, unit: 'varyant' } }))
    expect(one).toBeLessThan(three)
    expect(publishStepsFit(steps({ live: true }))).toBeGreaterThanOrEqual(200)
  })
})
