import { matchingKey, valueMatchScore } from '../../shared/value-matching'

export type ValueCandidate = { externalId: string; name: string }

// Marketplace values that look like the panel value, closest first (see valueMatchScore: same
// name, folded letters, synonyms, prefix, contains, shared word). Used to offer the likely match
// at the top of the "Şimdi eşle" list; the user still picks.
export function rankValueCandidates(label: string, items: readonly ValueCandidate[], limit = 6) {
  if (!label.trim()) return []
  return items
    .map(item => ({ item, score: valueMatchScore(label, item.name) }))
    .filter(entry => entry.score > 0)
    .sort((left, right) => right.score - left.score || left.item.name.length - right.item.name.length || left.item.name.localeCompare(right.item.name, 'tr-TR'))
    .slice(0, limit)
    .map(entry => entry.item)
}

export function filterValueCandidates(query: string, items: readonly ValueCandidate[], limit = 30) {
  const needle = matchingKey(query)
  if (!needle) return []
  return items.filter(item => matchingKey(item.name).includes(needle)).slice(0, limit)
}
