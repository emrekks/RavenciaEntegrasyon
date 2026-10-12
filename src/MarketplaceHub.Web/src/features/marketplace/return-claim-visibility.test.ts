import { describe, expect, it } from 'vitest'
import { isActionableReturnLine, splitMixedReturnClaims, visibleReturnClaims } from './return-claim-visibility'

describe('visible return claims', () => {
  it('hides cancelled claims while keeping active, approved, and rejected claims', () => {
    const claims = [
      { id: 'active', status: 'REQUESTED' },
      { id: 'approved', status: 'APPROVED' },
      { id: 'rejected', status: 'REJECTED' },
      { id: 'cancelled', status: 'CANCELLED' },
      { id: 'alternate-cancelled', status: ' canceled ' },
    ]

    expect(visibleReturnClaims(claims).map(claim => claim.id)).toEqual(['active', 'approved', 'rejected'])
  })
})

describe('mixed return claims', () => {
  const claim = (lineStatuses: Record<string, string>) => ({ id: 'c1', status: 'ACTION_REQUIRED', rawStatus: 'WaitingInAction', productCount: 2, lines: [{ id: 'black' }, { id: 'green' }], lineStatuses })

  it('splits a claim with one accepted and one waiting line into two cards', () => {
    const [waiting, approved] = splitMixedReturnClaims([claim({ black: 'APPROVED', green: 'ACTION_REQUIRED' })])
    expect(waiting).toMatchObject({ status: 'ACTION_REQUIRED', productCount: 1, rowKey: 'c1:waiting', lines: [{ id: 'green' }] })
    expect(approved).toMatchObject({ status: 'APPROVED', productCount: 1, rowKey: 'c1:approved', lines: [{ id: 'black' }] })
  })

  it('keeps a claim whole when every line is in the same state', () => {
    const settled = { ...claim({ black: 'APPROVED', green: 'APPROVED' }), status: 'APPROVED' }
    expect(splitMixedReturnClaims([settled])).toEqual([settled])
    const open = claim({ black: 'ACTION_REQUIRED', green: 'ACTION_REQUIRED' })
    expect(splitMixedReturnClaims([open])).toEqual([open])
  })

  it('treats lines without a known status as still actionable', () => {
    expect(isActionableReturnLine({ status: null })).toBe(true)
    expect(isActionableReturnLine({ status: 'ACTION_REQUIRED' })).toBe(true)
    expect(isActionableReturnLine({ status: 'APPROVED' })).toBe(false)
  })
})
