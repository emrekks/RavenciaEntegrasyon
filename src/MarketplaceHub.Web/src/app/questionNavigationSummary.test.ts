import { describe, expect, it } from 'vitest'
import { questionWaitingSummaryPath } from './questionNavigationSummary'

describe('question navigation summary', () => {
  it('counts waiting product and order questions together', () => {
    const params = new URLSearchParams(questionWaitingSummaryPath.split('?')[1])

    expect(params.get('kind')).toBe('ALL')
    expect(params.get('status')).toBe('WAITING')
    expect(params.get('page')).toBe('1')
    expect(params.get('limit')).toBe('1')
  })
})
