import { describe, expect, it } from 'vitest'
import { sameProductSnapshotIgnoringVersion } from './product-save-version'

describe('product save version recovery', () => {
  it('allows a retry when only product/variant versions and timestamps changed', () => {
    const loaded = {
      id: 'product-1', version: 13, updatedAt: '2026-09-28T10:00:00Z',
      variants: [{ id: 'variant-1', version: 2, sku: 'SKU-1', updatedAt: '2026-09-28T10:00:00Z' }]
    }
    const latest = {
      id: 'product-1', version: 14, updatedAt: '2026-09-28T10:05:00Z',
      variants: [{ id: 'variant-1', version: 3, sku: 'SKU-1', updatedAt: '2026-09-28T10:05:00Z' }]
    }

    expect(sameProductSnapshotIgnoringVersion(loaded, latest)).toBe(true)
  })

  it('detects a real content change and prevents a stale form from overwriting it', () => {
    expect(sameProductSnapshotIgnoringVersion(
      { id: 'product-1', version: 13, title: 'Önceki ad' },
      { id: 'product-1', version: 14, title: 'Yeni ad' }
    )).toBe(false)
  })

  it('compares arrays in order while ignoring object key order', () => {
    expect(sameProductSnapshotIgnoringVersion(
      { version: 1, details: { sku: 'SKU-1', color: 'Bordo' }, images: ['one.jpg', 'two.jpg'] },
      { images: ['one.jpg', 'two.jpg'], details: { color: 'Bordo', sku: 'SKU-1' }, version: 2 }
    )).toBe(true)
    expect(sameProductSnapshotIgnoringVersion(
      { images: ['one.jpg', 'two.jpg'] },
      { images: ['two.jpg', 'one.jpg'] }
    )).toBe(false)
  })
})
