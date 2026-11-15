import { describe, expect, it } from 'vitest'
import { ELEVATED_ROLES, isElevatedRole, lacksElevatedRole } from './elevated-role'

describe('elevated role', () => {
  it('is the owner and administrators, as RoleAuthorizationMiddleware.ElevatedRoles', () => {
    expect([...ELEVATED_ROLES]).toEqual(['OWNER', 'ADMINISTRATOR'])
    expect(isElevatedRole('owner')).toBe(true)
    expect(isElevatedRole('ADMINISTRATOR')).toBe(true)
    expect(isElevatedRole('OPERATIONS')).toBe(false)
  })

  it('takes only a known other role for a refusal', () => {
    expect(lacksElevatedRole('OPERATIONS')).toBe(true)
    expect(lacksElevatedRole('ACCOUNTING')).toBe(true)
    expect(lacksElevatedRole('OWNER')).toBe(false)
    expect(lacksElevatedRole(null)).toBe(false)
    expect(lacksElevatedRole(undefined)).toBe(false)
  })
})
