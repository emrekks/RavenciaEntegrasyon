import { describe, expect, it } from 'vitest'
import { visibleReturnClaims } from './return-claim-visibility'

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
