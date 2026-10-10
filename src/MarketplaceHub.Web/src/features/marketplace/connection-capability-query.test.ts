import { describe, expect, it } from 'vitest'
import { shouldLoadConnectionCapabilities } from './connection-capability-query'

describe('shouldLoadConnectionCapabilities', () => {
  it('loads connection-scoped write evidence for Shopify and Hepsiburada', () => {
    expect(shouldLoadConnectionCapabilities('SHOPIFY')).toBe(true)
    expect(shouldLoadConnectionCapabilities('HEPSIBURADA')).toBe(true)
  })

  it('does not load marketplace write evidence for unsupported platforms', () => {
    expect(shouldLoadConnectionCapabilities('TRENDYOL')).toBe(false)
    expect(shouldLoadConnectionCapabilities(undefined)).toBe(false)
  })
})
