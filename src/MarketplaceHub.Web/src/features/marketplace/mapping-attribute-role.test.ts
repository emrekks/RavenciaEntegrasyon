import { describe, expect, it } from 'vitest'
import { resolveAttributeMappingRole } from './mapping-attribute-role'

describe('resolveAttributeMappingRole', () => {
  it('uses an explicit category assignment when present', () => {
    expect(resolveAttributeMappingRole('OPTION', false)).toBe('OPTION')
    expect(resolveAttributeMappingRole('attribute', true)).toBe('ATTRIBUTE')
  })

  it('falls back to the panel attribute role for unmapped assignments', () => {
    expect(resolveAttributeMappingRole(undefined, true)).toBe('OPTION')
    expect(resolveAttributeMappingRole(null, false)).toBe('ATTRIBUTE')
  })
})
