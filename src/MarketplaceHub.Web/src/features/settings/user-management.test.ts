import { describe, expect, it } from 'vitest'
import { assignableRoles, availableUserActions, canWithdrawInvitation, invitationSentMessage, reauthBody, userLockReason, type ManagedUser, type PendingInvitation } from './user-management'

const user = (overrides: Partial<ManagedUser> = {}): ManagedUser => ({
  id: 'u1', displayName: 'Ayşe', email: 'ayse@example.com', role: 'OPERATIONS', active: true, lockedOut: false, mfaEnabled: false,
  passwordChangePending: false, lastLoginAt: null, createdAt: '2026-11-01T00:00:00Z', openSessions: 0, self: false, ...overrides
})

describe('user management rules', () => {
  it('lets only an owner give the owner role', () => {
    expect(assignableRoles('OWNER')).toContain('OWNER')
    expect(assignableRoles('ADMINISTRATOR')).not.toContain('OWNER')
    expect(assignableRoles('ADMINISTRATOR')).toEqual(['ADMINISTRATOR', 'OPERATIONS', 'ACCOUNTING', 'READONLY'])
  })

  it('offers nothing for your own account or, to an administrator, for an owner', () => {
    expect(availableUserActions(user({ self: true, role: 'OWNER' }), 'OWNER')).toEqual([])
    expect(userLockReason(user({ self: true }), 'OWNER')).toMatch(/Güvenlik/)
    expect(availableUserActions(user({ role: 'OWNER' }), 'ADMINISTRATOR')).toEqual([])
    expect(availableUserActions(user({ role: 'OWNER' }), 'OWNER')).toEqual(['role', 'password-reset', 'disable'])
  })

  it('offers the second-factor reset and session close only when there is something to reset or close', () => {
    expect(availableUserActions(user({ mfaEnabled: true, openSessions: 2 }), 'ADMINISTRATOR')).toEqual(['role', 'password-reset', 'mfa-reset', 'sessions', 'disable'])
    expect(availableUserActions(user({ active: false }), 'ADMINISTRATOR')).toEqual(['role', 'password-reset', 'enable'])
  })

  it('sends a six-digit code as the code and anything else as a recovery code', () => {
    expect(reauthBody('pw', '')).toEqual({ password: 'pw' })
    expect(reauthBody('pw', '123 456')).toEqual({ password: 'pw', code: '123456' })
    expect(reauthBody('pw', 'ABCD-EFGH-JKMN')).toEqual({ password: 'pw', recoveryCode: 'ABCD-EFGH-JKMN' })
  })

  // API-GUVENLIK-6: an existing account is invited; only an owner withdraws an invitation to the owner role.
  it('lets only an owner withdraw an owner invitation and tells the inviter an invitation was sent', () => {
    const invitation = (role: PendingInvitation['role']): PendingInvitation => ({ id: 'i1', displayName: 'Davetli', email: 'davetli@example.com', role, invitedAt: '2026-11-16T00:00:00Z', expiresAt: '2026-11-30T00:00:00Z', expired: false })
    expect(canWithdrawInvitation(invitation('OPERATIONS'), 'ADMINISTRATOR')).toBe(true)
    expect(canWithdrawInvitation(invitation('OWNER'), 'ADMINISTRATOR')).toBe(false)
    expect(canWithdrawInvitation(invitation('OWNER'), 'OWNER')).toBe(true)
    expect(invitationSentMessage('davetli@example.com', 'READONLY')).toContain('Salt Okunur rolüyle davet gönderildi')
    expect(invitationSentMessage('davetli@example.com', 'READONLY')).toContain('kabul edene kadar bu çalışma alanına erişemez')
  })
})
