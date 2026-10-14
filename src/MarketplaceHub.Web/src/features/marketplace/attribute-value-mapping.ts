export function normalizeReferenceValueLabel(value: string) {
  return value.trim().toLocaleLowerCase('tr-TR').replace(/\s*\/\s*/g, '/')
}

export function hasDirectReferenceValue(localValue: string, remoteValues: readonly string[]) {
  const normalizedLocalValue = normalizeReferenceValueLabel(localValue)
  return remoteValues.some(value => normalizeReferenceValueLabel(value) === normalizedLocalValue)
}

export function planDirectReferenceValues(
  localValues: readonly { id: string; value: string }[],
  remoteValues: readonly { externalId: string; name: string }[]
) {
  const localByName = new Map<string, typeof localValues[number][]>()
  for (const local of localValues) {
    const normalized = normalizeReferenceValueLabel(local.value)
    const group = localByName.get(normalized) ?? []
    group.push(local)
    localByName.set(normalized, group)
  }
  const remoteByName = new Map<string, typeof remoteValues[number][]>()
  for (const remote of remoteValues) {
    const normalized = normalizeReferenceValueLabel(remote.name)
    const group = remoteByName.get(normalized) ?? []
    group.push(remote)
    remoteByName.set(normalized, group)
  }

  const mappings: Array<{ localId: string; externalId: string }> = []
  let ambiguousCount = 0
  for (const [normalized, remotes] of remoteByName) {
    const locals = localByName.get(normalized) ?? []
    if (!locals.length) continue
    if (remotes.length !== 1) {
      ambiguousCount += remotes.length
      continue
    }
    if (locals.length > 1) ambiguousCount++
    else if (locals[0]) mappings.push({ localId: locals[0].id, externalId: remotes[0].externalId })
  }

  return { mappings, ambiguousCount }
}

export function planMissingReferencePanelValues(
  localValues: readonly { id: string; value: string }[],
  remoteValues: readonly { externalId: string; name: string }[],
  selections: ReferencePanelSelections
) {
  const localIds = new Set(localValues.map(value => value.id))
  const localNames = new Set(localValues.map(value => normalizeReferenceValueLabel(value.value)))
  const plannedNames = new Set<string>()
  const values: string[] = []
  for (const remote of remoteValues) {
    const normalized = normalizeReferenceValueLabel(remote.name)
    const selectedLocalIds = selections[remote.externalId] ?? []
    if (selectedLocalIds.some(id => localIds.has(id)) || !normalized || localNames.has(normalized) || plannedNames.has(normalized)) continue
    plannedNames.add(normalized)
    values.push(remote.name.trim())
  }
  return values
}

export function valueMappingRowClassName(hasSelection: boolean, isRequired: boolean) {
  return `value-mapping-row${hasSelection ? '' : ' is-empty'} ${isRequired ? 'is-required' : 'is-optional'}`
}

// One marketplace value can be fed by several panel values, so every marketplace value keeps a list.
export type ReferencePanelSelections = Record<string, string[]>

export function updatePanelValueReferenceSelection(
  selections: ReferencePanelSelections,
  externalId: string,
  localIds: readonly string[],
  allowReuse = false
): ReferencePanelSelections {
  const wanted = [...new Set(localIds.filter(Boolean))]
  const next: ReferencePanelSelections = {}
  for (const [selectedExternalId, selectedLocalIds] of Object.entries(selections)) {
    if (selectedExternalId === externalId) continue
    // a panel value goes to one marketplace value unless the platform allows reuse
    const kept = allowReuse ? selectedLocalIds : selectedLocalIds.filter(id => !wanted.includes(id))
    if (kept.length) next[selectedExternalId] = kept
  }
  if (wanted.length) next[externalId] = wanted
  return next
}

export function togglePanelValueReferenceSelection(
  selections: ReferencePanelSelections,
  externalId: string,
  localId: string,
  allowReuse = false
): ReferencePanelSelections {
  const current = selections[externalId] ?? []
  return updatePanelValueReferenceSelection(selections, externalId, current.includes(localId) ? current.filter(id => id !== localId) : [...current, localId], allowReuse)
}

export function planReferencePanelMappings(selections: ReferencePanelSelections) {
  return Object.entries(selections).flatMap(([externalId, localIds]) => localIds.map(localId => ({ localId, externalId })))
}

export function attributeValueMappingNeedsSave(
  existing: { localId: string; externalId: string; snapshotId: string } | undefined,
  localId: string,
  externalId: string,
  snapshotId: string
) {
  return Boolean(localId && externalId) && (!existing || existing.localId !== localId || existing.externalId !== externalId || existing.snapshotId !== snapshotId)
}

export type ValueMappingSuggestion = { localId: string; localLabel: string; externalId: string; remoteName: string; score: number }

// Best marketplace value for each panel value that has none yet. High-confidence ones (same name,
// folded letters or synonyms: score >= autoScore) can be selected at once; the rest (score >= 50)
// wait for the user's approval. Nothing is saved here.
export function suggestValueMappings(
  unmatched: readonly { id: string; value: string }[],
  remoteValues: readonly { externalId: string; name: string }[],
  score: (panelLabel: string, remoteName: string) => number,
  autoScore: number
) {
  const automatic: ValueMappingSuggestion[] = []
  const review: ValueMappingSuggestion[] = []
  for (const local of unmatched) {
    let best: ValueMappingSuggestion | null = null
    for (const remote of remoteValues) {
      const value = score(local.value, remote.name)
      if (value < 50) continue
      if (!best || value > best.score || (value === best.score && remote.name.length < best.remoteName.length))
        best = { localId: local.id, localLabel: local.value, externalId: remote.externalId, remoteName: remote.name, score: value }
    }
    if (!best) continue
    if (best.score >= autoScore) automatic.push(best)
    else review.push(best)
  }
  return { automatic, review: review.sort((left, right) => right.score - left.score || left.localLabel.localeCompare(right.localLabel, 'tr-TR')) }
}

// The value mapping screen lists the panel values products use; values already selected stay
// visible. When no product uses the attribute yet, nothing is hidden.
export function visiblePanelValues<T extends { id: string }>(values: readonly T[], usedIds: readonly string[] | undefined, selectedIds: readonly string[], showAll: boolean) {
  const used = new Set(usedIds ?? [])
  if (showAll || used.size === 0) return { visible: [...values], hiddenCount: 0 }
  const selected = new Set(selectedIds)
  const visible = values.filter(value => used.has(value.id) || selected.has(value.id))
  return { visible, hiddenCount: values.length - visible.length }
}

// A marketplace value row shows the "Şablon" badge while every saved mapping of it was filled from
// another category's decision and the user has not changed the selection yet.
export function isTemplateFilledRow(saved: readonly { localId: string; source?: string | null }[], selectedLocalIds: readonly string[]) {
  if (!saved.length || !saved.every(item => item.source === 'TEMPLATE')) return false
  return saved.length === selectedLocalIds.length && saved.every(item => selectedLocalIds.includes(item.localId))
}
