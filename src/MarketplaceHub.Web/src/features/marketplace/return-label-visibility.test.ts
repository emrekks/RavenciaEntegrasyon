import { describe, expect, it } from 'vitest'
import { canPrintReturnLabel } from './return-label-visibility'

describe('return label visibility', () => {
  it('shows the print action only for rejected returns', () => {
    expect(canPrintReturnLabel('REJECTED')).toBe(true)
    expect(canPrintReturnLabel(' rejected ')).toBe(true)
    expect(canPrintReturnLabel('REQUESTED')).toBe(false)
    expect(canPrintReturnLabel('ACTION_REQUIRED')).toBe(false)
    expect(canPrintReturnLabel('APPROVED')).toBe(false)
    expect(canPrintReturnLabel('CANCELLED')).toBe(false)
    expect(canPrintReturnLabel(null)).toBe(false)
  })
})
