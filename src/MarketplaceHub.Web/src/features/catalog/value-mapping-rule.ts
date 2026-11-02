// When a panel value counts as mapped to a marketplace value: one rule for the publish check (its gaps and chips) and
// the category fields window (which values it marks as still needing a mapping), so the two never disagree.
// A mapping counts when it is verified, made on the marketplace's current value list, and its value is still active there.
export type ValueMappingRow = { localId: string; externalId: string; status: string; snapshotId: string }

export function currentValueMapping<T extends ValueMappingRow>(mappings: readonly T[] | undefined, localId: string, snapshotId: string, isActive: (externalId: string) => boolean): T | undefined {
  return mappings?.find(mapping => mapping.localId === localId && mapping.status === 'VERIFIED' && mapping.snapshotId === snapshotId && isActive(mapping.externalId))
}

// In the window: a value the publish check named stays marked until the user picks a marketplace value for it (a pick
// not saved yet counts, Kaydet then makes it last) or its saved mapping counts by the rule above.
export function valueStillNeedsMapping(picked: string, saved: ValueMappingRow | undefined, snapshotId: string, isActive: (externalId: string) => boolean) {
  if (!picked) return true
  if (picked !== saved?.externalId) return false
  return !currentValueMapping(saved ? [saved] : [], saved.localId, snapshotId, isActive)
}
