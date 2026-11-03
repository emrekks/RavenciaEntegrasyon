// "Hepsinde Bunu Kullan" for a panel value mapped to different marketplace values across categories: which
// categories change, which are skipped because the chosen value is not in their current value list, and the saves.

export type ConflictScope = { scopeExternalId: string; categoryName: string; version: number }
export type ConflictOption = { externalId: string; name: string; version: number; verifiedAt: string | null; categories: number; scopes?: ConflictScope[] | null }
export type Conflict = { scopeExternalId: string; categoryName: string; attributeName: string; localValueId: string; localValueName: string; options: ConflictOption[]; templateExternalId: string | null }
export type Conflicts = { sameCategory: Conflict[]; crossCategory: Conflict[] }

export type ValueList = { snapshotId: string; items: Array<{ externalId: string; isActive?: boolean | null }> }

// One category where the chosen value replaces the others.
export type UnifyTarget = {
  scopeExternalId: string
  categoryName: string
  // The mappings to remove there, with the versions the server held when the plan was made.
  remove: Array<{ externalId: string; name: string; version: number }>
  // The chosen value is not mapped there yet: it is added with this snapshot. Null when it already is.
  snapshotId: string | null
}
export type UnifySkip = { scopeExternalId: string; categoryName: string; reason: string; fromNames: string[] }
export type UnifyPlan = { targets: UnifyTarget[]; skipped: UnifySkip[]; anchor: { scopeExternalId: string; version: number } | null }

export const scopeCategory = (scope: string) => scope.slice(0, Math.max(0, scope.indexOf('/')))
export const scopeAttribute = (scope: string) => scope.slice(scope.indexOf('/') + 1)
export const crossConflictAttribute = (conflict: Conflict) => {
  const scope = conflict.options.flatMap(option => option.scopes ?? [])[0]?.scopeExternalId
  return scope ? scopeAttribute(scope) : ''
}
export const crossConflictKey = (conflict: Conflict) => `${crossConflictAttribute(conflict) || conflict.attributeName}:${conflict.localValueId}`

// `valueLists` holds each category's current marketplace value list (or the reason it could not be read).
export function planUnify(conflict: Conflict, chosenExternalId: string, valueLists: ReadonlyMap<string, ValueList | Error>, platformLabel: string): UnifyPlan {
  const byScope = new Map<string, { categoryName: string; mappings: Array<{ externalId: string; name: string; version: number }> }>()
  for (const option of conflict.options) for (const scope of option.scopes ?? []) {
    const entry = byScope.get(scope.scopeExternalId) ?? { categoryName: scope.categoryName, mappings: [] }
    entry.mappings.push({ externalId: option.externalId, name: option.name, version: scope.version })
    byScope.set(scope.scopeExternalId, entry)
  }
  const chosenName = conflict.options.find(option => option.externalId === chosenExternalId)?.name ?? chosenExternalId
  const targets: UnifyTarget[] = []
  const skipped: UnifySkip[] = []
  let anchor: UnifyPlan['anchor'] = null
  for (const [scopeExternalId, { categoryName, mappings }] of [...byScope.entries()].sort(([left], [right]) => left.localeCompare(right))) {
    const existing = mappings.find(item => item.externalId === chosenExternalId)
    const remove = mappings.filter(item => item.externalId !== chosenExternalId)
    if (existing && !anchor) anchor = { scopeExternalId, version: existing.version }
    if (!remove.length) continue
    if (existing) { targets.push({ scopeExternalId, categoryName, remove, snapshotId: null }); continue }
    const list = valueLists.get(scopeExternalId)
    const fromNames = remove.map(item => item.name)
    if (!list || list instanceof Error) { skipped.push({ scopeExternalId, categoryName, fromNames, reason: `değer listesi okunamadı${list instanceof Error && list.message ? ` (${list.message})` : ''}` }); continue }
    // Only a value still offered in that category's current list is written; an id from another category is never sent.
    if (!list.items.some(item => item.externalId === chosenExternalId && item.isActive !== false)) { skipped.push({ scopeExternalId, categoryName, fromNames, reason: `“${chosenName}” bu kategorinin ${platformLabel} değer listesinde yok` }); continue }
    targets.push({ scopeExternalId, categoryName, remove, snapshotId: list.snapshotId })
  }
  return { targets, skipped, anchor }
}

// The categories whose value lists the plan needs: those where the chosen value is not mapped yet.
export function scopesNeedingValueList(conflict: Conflict, chosenExternalId: string) {
  const holding = new Set(conflict.options.find(option => option.externalId === chosenExternalId)?.scopes?.map(scope => scope.scopeExternalId) ?? [])
  return [...new Set(conflict.options.filter(option => option.externalId !== chosenExternalId).flatMap(option => option.scopes ?? []).map(scope => scope.scopeExternalId))].filter(scope => !holding.has(scope))
}

// The line under a row once a value is picked: "1 kategoride Rahat → Relaxed olacak". Each category's value list is
// checked only when the change is asked for, so a category that does not offer the value is named in the confirm.
export function choicePreview(conflict: Conflict, chosenExternalId: string, chosenName: string) {
  // Every category holding another value changes, also one that holds the chosen value beside it (the other goes).
  const others = conflict.options.filter(option => option.externalId !== chosenExternalId)
  const changing = new Set(others.flatMap(option => option.scopes ?? []).map(scope => scope.scopeExternalId)).size
  const from = others.map(option => option.name).join(', ')
  const template = conflict.templateExternalId === chosenExternalId ? '' : `; yeni kategoriler için şablon olur`
  return changing ? `${changing} kategoride ${from} → ${chosenName} olacak${template}.` : `Kategorilerde değer değişmez${template}.`
}

export const changeLine = (target: UnifyTarget, chosenName: string) => `${target.categoryName}: ${target.remove.map(item => item.name).join(', ')} → ${chosenName}`

// "Relaxed: 1 kategoride Rahat → Relaxed"
export function unifySummary(conflict: Conflict, plan: UnifyPlan, chosenName: string) {
  const from = [...new Set(plan.targets.flatMap(target => target.remove.map(item => item.name)))]
  return `${conflict.localValueName}: ${plan.targets.length} kategoride ${from.join(', ')} → ${chosenName}`
}
