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

  const missingValues: string[] = []
  const mappings: Array<{ localId: string; externalId: string }> = []
  let ambiguousCount = 0
  for (const [normalized, remotes] of remoteByName) {
    if (remotes.length !== 1) {
      ambiguousCount += remotes.length
      continue
    }
    const locals = localByName.get(normalized) ?? []
    if (locals.length > 1) ambiguousCount++
    else if (locals[0]) mappings.push({ localId: locals[0].id, externalId: remotes[0].externalId })
    else missingValues.push(remotes[0].name)
  }

  return { missingValues, mappings, ambiguousCount }
}

export function attributeValueMappingNeedsSave(
  existing: { externalId: string; snapshotId: string } | undefined,
  externalId: string,
  snapshotId: string
) {
  return Boolean(externalId) && (!existing || existing.externalId !== externalId || existing.snapshotId !== snapshotId)
}
