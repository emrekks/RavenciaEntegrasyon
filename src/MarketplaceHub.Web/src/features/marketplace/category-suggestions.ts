import { canonicalKey, matchingKey, valueMatchScore } from '../../shared/value-matching'

export type CategorySuggestion = { externalId: string; path: string; score: number }

// Category names that mean the same thing but do not fold to one key ("Tişört" / "T-Shirt").
const categorySynonyms: string[][] = [
  ['tisort', 't shirt', 'tshirt'], ['sweatshirt', 'sweat'], ['esofman', 'esofman takimi'], ['tayt', 'leggings'],
  ['mont', 'kaban'], ['sort', 'shorts'], ['jeans', 'jean', 'kot pantolon', 'kot'], ['etek', 'skirt'], ['elbise', 'dress'], ['ceket', 'jacket']
]
const categoryCanonical = new Map<string, string>()
for (const group of categorySynonyms) for (const name of group) categoryCanonical.set(matchingKey(name), matchingKey(group[0]))

function segments(path: string) {
  return path.replace(/\[TDG\]|\(\s*TDG\s*\)/gi, '').split(/\s*(?:\/|>|›)\s*/).map(item => item.trim()).filter(Boolean)
}

// Turkish plural suffix: "Tişörtler" and "Tişört" are the same category word.
const singular = (key: string) => key.length > 4 ? key.replace(/(ler|lar)$/, '') : key

function words(value: string) {
  const key = matchingKey(value)
  const whole = categoryCanonical.get(key) ?? categoryCanonical.get(singular(key))
  if (whole) return [whole]
  return key.split(' ').filter(word => word.length > 1).map(word => categoryCanonical.get(singular(word)) ?? canonicalKey(singular(word)))
}

// Qualifiers in parentheses ("Bluz (Hepsiburada)") say where a category came from, not what it is.
const withoutQualifiers = (value: string) => value.replace(/\([^)]*\)/g, ' ').trim()

function nameScore(local: string, remote: string) {
  const direct = valueMatchScore(local, remote)
  if (direct >= 90) return direct
  const left = [...new Set(words(local))]
  const right = new Set(words(remote))
  if (!left.length || !right.size) return 0
  const shared = left.filter(word => right.has(word)).length
  if (shared === left.length && shared === right.size) return 92
  // the remote name is the panel name plus extra words: "Pantolon" can be listed under "Pantolon Takım"
  if (shared === left.length) return 60
  // the remote name is only part of the panel name: "Tesettür Gömlek" is not a plain "Gömlek"; the missing
  // word is what the category is about, so this is a weak fallback that a real match hides
  if (shared === right.size) return 52
  // a prefix only counts when it is most of the word ("Bluz" / "Bluzlar", not "Tulum" / "Tül")
  const [short, long] = [matchingKey(local), matchingKey(remote)].sort((a, b) => a.length - b.length)
  if (short.length >= 4 && short.length / long.length >= 0.6 && long.startsWith(short)) return 70
  return Math.round(50 * shared / Math.max(left.length, right.size))
}

function leafScore(local: string, remote: string) {
  const cleanLocal = withoutQualifiers(local) || local
  // "Bluz & Tunik" is a leaf for either name
  const alternatives = [remote, ...remote.split(/\s*[&,]\s*/).filter(Boolean)]
  return Math.max(...alternatives.map(name => nameScore(cleanLocal, withoutQualifiers(name) || name)))
}

// Best marketplace categories for a panel category: the leaf names decide most of the score (70%),
// the parent path breaks ties between same-named leaves such as Kadın / Tişört and Erkek / Tişört
// (30%). Only candidates with a usable leaf match are returned; nothing is saved here.
export function suggestCategoryMappings(
  local: { name: string; path: string },
  remotes: readonly { externalId: string; name: string; path: string }[],
  limit = 3
): CategorySuggestion[] {
  const localSegments = segments(local.path)
  const localLeaf = localSegments.at(-1) ?? local.name
  const localParents = new Set(localSegments.slice(0, -1).flatMap(words))
  const scored: Array<CategorySuggestion & { leaf: number }> = []
  for (const remote of remotes) {
    const remoteSegments = segments(remote.path)
    const leaf = leafScore(localLeaf, remoteSegments.at(-1) ?? remote.name)
    if (leaf < 50) continue
    const remoteParents = new Set(remoteSegments.slice(0, -1).flatMap(words))
    const shared = [...localParents].filter(word => remoteParents.has(word)).length
    const parent = localParents.size ? shared / localParents.size : 0
    scored.push({ externalId: remote.externalId, path: remote.path, score: Math.round(leaf * 0.7 + parent * 30), leaf })
  }
  // When one category matches the panel name almost exactly, weaker leaf matches ("Gömlek" for "Tesettür Gömlek")
  // would only be noise next to it.
  const strongest = Math.max(0, ...scored.map(item => item.leaf))
  const kept = strongest >= 90 ? scored.filter(item => item.leaf >= 70) : scored
  return kept
    .sort((left, right) => right.score - left.score || left.path.length - right.path.length || left.path.localeCompare(right.path, 'tr-TR'))
    .slice(0, limit)
    .map(({ externalId, path, score }) => ({ externalId, path, score }))
}
