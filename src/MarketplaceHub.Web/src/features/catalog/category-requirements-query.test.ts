import { describe, expect, it } from 'vitest'
import { categoryRequirementsStaleTimeMs } from './category-requirements-query'

describe('category requirements query', () => {
  it('keeps a category\'s requirements for at least ten minutes', () => {
    expect(categoryRequirementsStaleTimeMs).toBeGreaterThanOrEqual(10 * 60_000)
  })
})
