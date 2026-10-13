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
