import { describe, expect, it } from 'vitest'
import { isActionableReturnLine } from './return-claim-visibility'

describe('return lines', () => {
  it('treats lines without a known status as still actionable', () => {
    expect(isActionableReturnLine({ status: null })).toBe(true)
    expect(isActionableReturnLine({ status: 'ACTION_REQUIRED' })).toBe(true)
    expect(isActionableReturnLine({ status: 'APPROVED' })).toBe(false)
  })
})
