import { canonicalKey, matchingKey } from '../../shared/value-matching'

// The same name however its separators are spaced: "S / M" is "S/M", "Lacivert - Kahve" is "Lacivert-Kahve".
export function normalizeReferenceValueLabel(value: string) {
  return value.trim().toLocaleLowerCase('tr-TR').replace(/\s*([/-])\s*/g, '$1').replace(/\s+/g, ' ')
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
    const remote = soleRemote(remotes)
    if (!remote) {
      ambiguousCount += remotes.length
      continue
    }
    if (locals.length > 1) ambiguousCount++
    else if (locals[0]) mappings.push({ localId: locals[0].id, externalId: remote.externalId })
  }

  // Second pass over what the exact names left: the same words once spacing, punctuation and Turkish letters are
  // folded, synonyms read alike and a filler word dropped ("Lacivert-Kahve" = "Lacivert - Kahverengi", "Hardal Rengi" =
  // "Hardal", "SIYAH/BEYAZ" = "Siyah / Beyaz"). Still one to one: a key shared by two values on either side is skipped.
  // The words in their own order first ("Beyaz-Mavi" = "Beyaz - Mavi" even though "Mavi - Beyaz" exists too), then in
  // any order for what is still open ("Kahve Lacivert" = "Lacivert - Kahverengi").
  const matchedLocals = new Set(mappings.map(item => item.localId))
  const matchedRemotes = new Set(mappings.map(item => item.externalId))
  const exactRemoteNames = new Set(remoteByName.keys())
  for (const ordered of [true, false]) {
    const looseLocals = groupByLooseKey(localValues.filter(local => !matchedLocals.has(local.id) && !exactRemoteNames.has(normalizeReferenceValueLabel(local.value))), local => local.value, ordered)
    const looseRemotes = groupByLooseKey(remoteValues, remote => remote.name, ordered)
    for (const [key, locals] of looseLocals) {
      const remote = soleRemote(looseRemotes.get(key) ?? [])
      if (locals.length === 1 && remote && !matchedRemotes.has(remote.externalId)) {
        mappings.push({ localId: locals[0].id, externalId: remote.externalId })
        matchedLocals.add(locals[0].id)
        matchedRemotes.add(remote.externalId)
      }
    }
  }

  return { mappings, ambiguousCount }
}

// The one marketplace value a name stands for. Hepsiburada lists some names twice, once with stray spaces ("Turuncu" and
// "Turuncu        ", "Sarı" twice; live 26 Oct 2026): when exactly one of them is written cleanly that one is meant;
// otherwise the name is ambiguous and left open.
// The panel's API client drops invisible control characters, so a twin like "Sarı" + U+008D arrives with the very same
// code as "Sarı": entries sharing one code are one value.
function soleRemote<T extends { name: string; externalId: string }>(remotes: readonly T[]): T | undefined {
  if (remotes.length === 1 || new Set(remotes.map(remote => remote.externalId)).size === 1) return remotes[0]
  const clean = remotes.filter(remote => remote.name === remote.name.trim() && !/\s{2,}/.test(remote.name))
  return clean.length === 1 ? clean[0] : undefined
}

// Words that only say what kind of value it is ("Hardal Rengi"); dropped unless the value is nothing else.
const fillerWords = new Set(['rengi', 'renk'])

// A value's words, folded (matchingKey), each read through the synonym list (kahve → kahverengi) and fillers dropped;
// ordered false also sorts them, so the order of a colour pair does not matter.
export function looseReferenceValueKey(value: string, ordered = false) {
  const words = matchingKey(value).split(' ').filter(Boolean).map(word => canonicalKey(word))
  const kept = words.filter(word => !fillerWords.has(word))
  const key = kept.length ? kept : words
  return (ordered ? key : key.sort()).join(' ')
}

function groupByLooseKey<T>(items: readonly T[], label: (item: T) => string, ordered: boolean) {
  const groups = new Map<string, T[]>()
  for (const item of items) {
    const key = looseReferenceValueKey(label(item), ordered)
    if (!key) continue
    groups.set(key, [...(groups.get(key) ?? []), item])
  }
  return groups
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
// aliases: the names a panel value carries on other marketplaces (platform bridge). The same name
// there counts as strong evidence (92, selected at once); weaker alias matches score 5 lower.
export const bridgedNameScore = 92

export function suggestValueMappings(
  unmatched: readonly { id: string; value: string }[],
  remoteValues: readonly { externalId: string; name: string }[],
  score: (panelLabel: string, remoteName: string) => number,
  autoScore: number,
  aliases: Readonly<Record<string, readonly string[]>> = {}
) {
  const automatic: ValueMappingSuggestion[] = []
  const review: ValueMappingSuggestion[] = []
  for (const local of unmatched) {
    let best: ValueMappingSuggestion | null = null
    for (const remote of remoteValues) {
      const direct = score(local.value, remote.name)
      const bridged = Math.max(0, ...(aliases[local.id] ?? []).map(alias => { const value = score(alias, remote.name); return value === 100 ? bridgedNameScore : value - 5 }))
      const value = Math.max(direct, bridged)
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
