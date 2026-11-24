import { describe, expect, it } from 'vitest'
import { formatPackageLabel } from './package-label'

describe('formatPackageLabel', () => {
  it('drops the virtual order: prefix', () => {
    expect(formatPackageLabel('order:PTTEM-UTEST0001-181026')).toBe('PTTEM-UTEST0001-181026')
  })

  it('keeps a real package number as it is', () => {
    expect(formatPackageLabel('3270000001')).toBe('3270000001')
    expect(formatPackageLabel('reorder:12')).toBe('reorder:12')
  })

  it('passes missing values through', () => {
    expect(formatPackageLabel(null)).toBeNull()
    expect(formatPackageLabel(undefined)).toBeNull()
  })
})
