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
    const selectedLocalId = selections[remote.externalId]
    if ((selectedLocalId && localIds.has(selectedLocalId)) || !normalized || localNames.has(normalized) || plannedNames.has(normalized)) continue
    plannedNames.add(normalized)
    values.push(remote.name.trim())
  }
  return values
}

export function valueMappingRowClassName(hasSelection: boolean, isRequired: boolean) {
  return `value-mapping-row${hasSelection ? '' : ' is-empty'} ${isRequired ? 'is-required' : 'is-optional'}`
}

export type ReferencePanelSelections = Record<string, string>

export function updatePanelValueReferenceSelection(
  selections: ReferencePanelSelections,
  externalId: string,
  localId: string,
  allowReuse = false
): ReferencePanelSelections {
  const next = { ...selections }
  if (!localId) {
    delete next[externalId]
    return next
  }
  if (!allowReuse) {
    for (const [selectedExternalId, selectedLocalId] of Object.entries(next)) {
      if (selectedExternalId !== externalId && selectedLocalId === localId) delete next[selectedExternalId]
    }
  }
  next[externalId] = localId
  return next
}

export function planReferencePanelMappings(selections: ReferencePanelSelections) {
  return Object.entries(selections).map(([externalId, localId]) => ({ localId, externalId }))
}

export function attributeValueMappingNeedsSave(
  existing: { localId: string; externalId: string; snapshotId: string } | undefined,
  localId: string,
  externalId: string,
  snapshotId: string
) {
  return Boolean(localId && externalId) && (!existing || existing.localId !== localId || existing.externalId !== externalId || existing.snapshotId !== snapshotId)
}
