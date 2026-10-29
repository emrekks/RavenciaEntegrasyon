import { describe, expect, it } from 'vitest'
import { isActionableReturnLine } from './return-claim-visibility'

describe('return lines', () => {
  it('treats lines without a known status as still actionable', () => {
    expect(isActionableReturnLine({ status: null })).toBe(true)
    expect(isActionableReturnLine({ status: 'ACTION_REQUIRED' })).toBe(true)
    expect(isActionableReturnLine({ status: 'APPROVED' })).toBe(false)
  })

  it('leaves a line to the sent decision that covers it, and only that line', () => {
    expect(isActionableReturnLine({ status: 'ACTION_REQUIRED', decisionPending: true })).toBe(false)
    expect(isActionableReturnLine({ status: 'ACTION_REQUIRED', decisionPending: false })).toBe(true)
  })
})
