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
