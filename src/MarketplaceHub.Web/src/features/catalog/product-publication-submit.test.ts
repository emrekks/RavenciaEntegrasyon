import { describe, expect, it } from 'vitest'
import { productPublicationTargets } from './product-publication-submit'

describe('product publication submit targets', () => {
  it('does not enqueue marketplace jobs for a panel-only save', () => {
    expect(productPublicationTargets(true, ['trendyol-connection'])).toEqual([])
  })

  it('keeps selected marketplace targets for an explicit publish/update action', () => {
    expect(productPublicationTargets(false, ['trendyol-connection'])).toEqual(['trendyol-connection'])
  })
})
