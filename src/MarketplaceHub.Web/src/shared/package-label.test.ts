import { describe, expect, it } from 'vitest'
import { formatPackageLabel } from './package-label'

describe('formatPackageLabel', () => {
  it('drops the virtual order: prefix', () => {
    expect(formatPackageLabel('order:PTTEM-U43FJA722-181026')).toBe('PTTEM-U43FJA722-181026')
  })

  it('keeps a real package number as it is', () => {
    expect(formatPackageLabel('3271827381')).toBe('3271827381')
    expect(formatPackageLabel('reorder:12')).toBe('reorder:12')
  })

  it('passes missing values through', () => {
    expect(formatPackageLabel(null)).toBeNull()
    expect(formatPackageLabel(undefined)).toBeNull()
  })
})
