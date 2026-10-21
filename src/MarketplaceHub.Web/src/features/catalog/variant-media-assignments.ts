export type VariantMediaAssignmentDrafts = Record<string, string[]>

export function variantMediaAssignmentKey(groupId: string, valueId: string) {
  return `${groupId}:${valueId}`
}

export function readVariantMediaAssignmentDraft(
  drafts: VariantMediaAssignmentDrafts,
  key: string,
  fallback: readonly string[] = []
) {
  return [...(drafts[key] ?? fallback)]
}

export function updateVariantMediaAssignmentDraft(
  drafts: VariantMediaAssignmentDrafts,
  key: string,
  refs: readonly string[]
): VariantMediaAssignmentDrafts {
  return { ...drafts, [key]: [...refs] }
}

export type VariantMediaReconcileRequest = { variantId: string; optionSignature: string; items: Array<{ url: string; sortOrder: number }> }

/**
 * The variant image assignments that can be saved on their own, without the rest of the form: one request per row
 * whose pictures changed. Null when any changed row is a variant not saved yet or uses a picture still waiting to be
 * uploaded (`file|` reference); those assignments wait for the product save.
 */
export function variantMediaReconcileRequests(
  rows: ReadonlyArray<{ key: string; optionSignature: string; mediaRefs: readonly string[] }>,
  savedRefsByVariantId: Readonly<Record<string, readonly string[] | undefined>>
): VariantMediaReconcileRequest[] | null {
  const requests: VariantMediaReconcileRequest[] = []
  for (const row of rows) {
    const saved = savedRefsByVariantId[row.key]
    if (!saved) return null
    if (saved.length === row.mediaRefs.length && saved.every((reference, index) => reference === row.mediaRefs[index])) continue
    if (row.mediaRefs.some(reference => !reference.startsWith('url|'))) return null
    requests.push({ variantId: row.key, optionSignature: row.optionSignature, items: row.mediaRefs.map((reference, sortOrder) => ({ url: reference.slice(4), sortOrder })) })
  }
  return requests
}
