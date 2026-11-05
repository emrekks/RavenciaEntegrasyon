import { describe, expect, it } from 'vitest'
import { customerRequestNote, finalizedWithFor } from './return-finalized-with'
import { statusLabel } from '../../shared/status-labels'
import { titleTr } from './returns-ui'

describe('Hepsiburada approval choice', () => {
  it('preselects what the customer asked for, and nothing when the claim does not say', () => {
    expect(finalizedWithFor('REFUND')).toBe('Refund')
    expect(finalizedWithFor('change')).toBe('Change')
    expect(finalizedWithFor(null)).toBe('')
    expect(finalizedWithFor('OTHER')).toBe('')
    expect(customerRequestNote('CHANGE')).toBe('Müşteri İsteği: Değişim')
    expect(customerRequestNote(undefined)).toContain('bilinmiyor')
  })
})

describe('return approved in part', () => {
  it('reads "Kısmen Onaylandı"', () => {
    expect(titleTr(statusLabel('PARTIALLY_APPROVED'))).toBe('Kısmen Onaylandı')
  })
})
