import { describe, expect, it } from 'vitest'
import { quickPlatformUpdateTargets } from './platform-update-targets'

describe('quick platform update targets', () => {
  it('only keeps selected active writable platforms, in selection order', () => {
    expect(quickPlatformUpdateTargets(['shop-a', 'readonly', 'shop-b', 'shop-a'], ['shop-b', 'shop-a']))
      .toEqual(['shop-a', 'shop-b'])
  })

  it('does not target every platform when none is selected', () => {
    expect(quickPlatformUpdateTargets([], ['shop-a', 'shop-b'])).toEqual([])
  })
})
