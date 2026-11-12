import { describe, expect, it } from 'vitest'
import { categoryDeactivationBody } from './category-deactivation'

describe('category deactivation request (9.1.21)', () => {
  it('keeps a sub-category under its parent', () => {
    expect(categoryDeactivationBody({ name: 'Elbise', parentId: 'parent-1' })).toEqual({ name: 'Elbise', parentId: 'parent-1', isActive: false })
  })

  it('leaves a root category at the root', () => {
    expect(categoryDeactivationBody({ name: 'Giyim', parentId: null })).toEqual({ name: 'Giyim', parentId: null, isActive: false })
  })
})
