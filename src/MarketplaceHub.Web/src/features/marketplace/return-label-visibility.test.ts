import { describe, expect, it } from 'vitest'
import { canPrintReturnLabel } from './return-label-visibility'

describe('return label visibility', () => {
  it('keeps the label available until a rejected return has an outbound shipment', () => {
    expect(canPrintReturnLabel('REJECTED', false)).toBe(true)
    expect(canPrintReturnLabel(' rejected ', true)).toBe(false)
    expect(canPrintReturnLabel('REQUESTED', false)).toBe(false)
    expect(canPrintReturnLabel('ACTION_REQUIRED', false)).toBe(false)
    expect(canPrintReturnLabel('APPROVED', false)).toBe(false)
    expect(canPrintReturnLabel('CANCELLED', false)).toBe(false)
    expect(canPrintReturnLabel(null, false)).toBe(false)
  })
})
