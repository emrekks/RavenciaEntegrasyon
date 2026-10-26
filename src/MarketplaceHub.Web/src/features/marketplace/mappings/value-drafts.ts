import { attributeValueMappingNeedsSave, planReferencePanelMappings, type ReferencePanelSelections } from '../attribute-value-mapping'

export type SavedValueMapping = { localId: string; externalId: string; version: number; snapshotId: string }

// A field's value choices not yet saved. The window keeps one per field (valueScope) until Kaydet or until it is
// closed, so moving to another field no longer drops them.
export type ValueDraft = {
  valueScope: string
  fieldName: string
  selections: ReferencePanelSelections
  saved: readonly SavedValueMapping[]
  snapshotId: string
  knownExternalIds: readonly string[]
}

export type ValueDraftOperation =
  | { kind: 'DELETE'; valueScope: string; localId: string; externalId: string; version: number }
  | { kind: 'PUT'; valueScope: string; localId: string; externalId: string; snapshotId: string; version: number | null }

export function groupSavedSelections(items: readonly { localId: string; externalId: string }[]): ReferencePanelSelections {
  const grouped: ReferencePanelSelections = {}
  for (const item of items) grouped[item.externalId] = [...(grouped[item.externalId] ?? []), item.localId]
  return grouped
}

const sameIds = (left: readonly string[], right: readonly string[]) => left.length === right.length && left.every(id => right.includes(id))

export function selectionsDiffer(selections: ReferencePanelSelections, saved: ReferencePanelSelections) {
  return [...new Set([...Object.keys(saved), ...Object.keys(selections)])].some(externalId => !sameIds(selections[externalId] ?? [], saved[externalId] ?? []))
}

// What saving a draft sends: removals of saved pairs no longer chosen, then the chosen pairs that are new or were
// saved against an older reference snapshot. Values the marketplace list no longer has are skipped.
export function valueDraftOperations(draft: ValueDraft): ValueDraftOperation[] {
  const operations: ValueDraftOperation[] = []
  for (const existing of draft.saved) {
    if ((draft.selections[existing.externalId] ?? []).includes(existing.localId)) continue
    operations.push({ kind: 'DELETE', valueScope: draft.valueScope, localId: existing.localId, externalId: existing.externalId, version: existing.version })
  }
  const known = new Set(draft.knownExternalIds)
  for (const planned of planReferencePanelMappings(draft.selections)) {
    if (!known.has(planned.externalId)) continue
    const existing = draft.saved.find(item => item.externalId === planned.externalId && item.localId === planned.localId)
    if (!attributeValueMappingNeedsSave(existing, planned.localId, planned.externalId, draft.snapshotId)) continue
    operations.push({ kind: 'PUT', valueScope: draft.valueScope, localId: planned.localId, externalId: planned.externalId, snapshotId: draft.snapshotId, version: existing?.version ?? null })
  }
  return operations
}

// Runs the requests a few at a time; every one is attempted and the refused ones are reported.
export async function runInBatches<T>(items: readonly T[], size: number, run: (item: T) => Promise<unknown>) {
  let saved = 0
  const failures: Array<{ item: T; error: unknown }> = []
  for (let index = 0; index < items.length; index += size) {
    const results = await Promise.allSettled(items.slice(index, index + size).map(run))
    results.forEach((result, offset) => { if (result.status === 'fulfilled') saved++; else failures.push({ item: items[index + offset], error: result.reason }) })
  }
  return { saved, failures }
}
