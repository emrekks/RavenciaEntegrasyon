import type { BrandReferenceSlice } from '../features/marketplace/mappings/brand-reference'

type ReferenceItem = BrandReferenceSlice['items'][number]

/** A reference list item as the API writes it (every member), from the few a test names. */
export function testReferenceItem(overrides: Partial<ReferenceItem> & Pick<ReferenceItem, 'externalId' | 'name'>): ReferenceItem {
  return {
    parentExternalId: null, path: overrides.name, depth: 0, isLeaf: true, isActive: true, isRequired: null, allowsCustomValue: null, allowsMultipleValues: null,
    ...overrides
  }
}

/** A brand slice (summary, search or lookup answer) of the given items. */
export function testBrandSlice(items: ReferenceItem[], overrides: Partial<BrandReferenceSlice> = {}): BrandReferenceSlice {
  return { snapshotId: 'snap-1', resourceType: 'BRANDS', fetchedAt: '2026-10-28T12:00:00Z', itemCount: 808409, items, ...overrides }
}
