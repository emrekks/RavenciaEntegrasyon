import { normalizeReferenceValueLabel } from '../marketplace/attribute-value-mapping'

export type ValueCandidate = { externalId: string; name: string }

function words(value: string) {
  return normalizeReferenceValueLabel(value).split(/[\s/,.-]+/).filter(Boolean)
}

// Marketplace values that look like the panel value, closest first: same name, then names that
// start with or contain it (or the other way round), then names sharing a word. Used to offer the
// likely match at the top of the "Şimdi eşle" list; the user still picks.
export function rankValueCandidates(label: string, items: readonly ValueCandidate[], limit = 6) {
  const target = normalizeReferenceValueLabel(label)
  const targetWords = new Set(words(label))
  if (!target) return []
  const scored: Array<{ item: ValueCandidate; score: number }> = []
  for (const item of items) {
    const name = normalizeReferenceValueLabel(item.name)
    let score = 0
    if (name === target) score = 100
    else if (name.startsWith(target) || target.startsWith(name)) score = 70
    else if (name.includes(target) || target.includes(name)) score = 50
    else if (words(item.name).some(word => targetWords.has(word))) score = 30
    if (score > 0) scored.push({ item, score })
  }
  return scored
    .sort((left, right) => right.score - left.score || left.item.name.length - right.item.name.length || left.item.name.localeCompare(right.item.name, 'tr-TR'))
    .slice(0, limit)
    .map(entry => entry.item)
}

export function filterValueCandidates(query: string, items: readonly ValueCandidate[], limit = 30) {
  const needle = normalizeReferenceValueLabel(query)
  if (!needle) return []
  return items.filter(item => normalizeReferenceValueLabel(item.name).includes(needle)).slice(0, limit)
}
