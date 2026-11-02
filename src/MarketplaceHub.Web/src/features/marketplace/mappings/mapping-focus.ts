import type { MappingPlatformCode } from '../mapping-platforms'

// The marketplaces whose categories have a field list the category fields window maps (Eşleştirmeler shows the same).
const fieldWindowPlatforms: readonly MappingPlatformCode[] = ['TRENDYOL', 'HEPSIBURADA', 'N11', 'PAZARAMA']
export function attributeMappingPlatform(platformCode: string): MappingPlatformCode | null {
  const code = platformCode.trim().toUpperCase()
  return fieldWindowPlatforms.find(item => item === code) ?? null
}

// What one product's publish check found missing on a marketplace, as the category fields window reads it: the fields
// the product's required gaps are about first, then those of its optional warnings, each with the panel values whose
// value mapping is missing.
export type MappingFocusItem = { externalAttributeId: string; required: boolean; label: string; details: string[]; localValueIds: string[]; panelAttribute?: string }

type FocusIssue = { attribute: string; detail: string; externalAttributeId?: string; localValueId?: string; panelAttribute?: string }

// One item per marketplace field: a field in both lists counts as required; a gap without a field (a product check, the
// category mapping) is not the window's business.
export function mappingFocusItems(requiredIssues: readonly FocusIssue[], optionalWarnings: readonly FocusIssue[]): MappingFocusItem[] {
  const items = new Map<string, MappingFocusItem>()
  const add = (issue: FocusIssue, required: boolean) => {
    const id = issue.externalAttributeId
    if (!id) return
    const item = items.get(id) ?? { externalAttributeId: id, required, label: issue.attribute, details: [], localValueIds: [] }
    if (!item.details.includes(issue.detail)) item.details.push(issue.detail)
    if (issue.localValueId && !item.localValueIds.includes(issue.localValueId)) item.localValueIds.push(issue.localValueId)
    if (issue.panelAttribute && !item.panelAttribute) item.panelAttribute = issue.panelAttribute
    items.set(id, item)
  }
  requiredIssues.forEach(issue => add(issue, true))
  optionalWarnings.forEach(issue => add(issue, false))
  return [...items.values()].sort((left, right) => Number(right.required) - Number(left.required))
}

// "Sonraki Eksik" with a product in view: the field after the chosen one in the product's gaps (required ones, then the
// optional ones); from a field outside them, the first gap. After the last gap it falls back to the window's own next open
// field, and only then wraps round to the first gap.
export function nextMissingEntryKey(focusKeys: readonly string[], activeKey: string | undefined, fallbackKey: string | undefined): string | undefined {
  const index = activeKey ? focusKeys.indexOf(activeKey) : -1
  const after = index >= 0 ? focusKeys[index + 1] : focusKeys[0]
  if (after) return after
  if (fallbackKey && fallbackKey !== activeKey) return fallbackKey
  return focusKeys[0] && focusKeys[0] !== activeKey ? focusKeys[0] : undefined
}
