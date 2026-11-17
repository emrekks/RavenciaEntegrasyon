import { readStoredItem, writeStoredItem } from './local-storage'

// A bounded list of keys this browser remembers (e.g. which order labels were printed here), newest last. Each key
// carries when it was added; a write drops keys older than the age limit and the oldest beyond the count limit, so the
// list cannot grow until the site's storage quota is full and every other setting stops saving (WEB-ALTYAPI-5).
// Stored as [{ key, at }]; an older plain string[] list is read too, its keys treated as added now (their age is not
// known), so the answer for recent records is not lost by the change of format.

export type RecentKeyLimits = { limit?: number; maxAgeMs?: number }
type Entry = { key: string; at: number }

export const recentKeyDefaultLimit = 3000
export const recentKeyDefaultMaxAgeMs = 90 * 24 * 60 * 60 * 1000

// The last list read per storage key with its raw text: a list read for every row of a table is parsed once.
const parsed = new Map<string, { raw: string | null; entries: Entry[] }>()

function readEntries(storageKey: string, now: number): Entry[] {
  const raw = readStoredItem(storageKey)
  const cached = parsed.get(storageKey)
  if (cached && cached.raw === raw) return cached.entries
  let entries: Entry[] = []
  try {
    const value = JSON.parse(raw ?? '[]') as unknown
    if (Array.isArray(value)) {
      entries = value.flatMap((item): Entry[] => typeof item === 'string'
        ? [{ key: item, at: now }]
        : item && typeof item === 'object' && typeof (item as Entry).key === 'string'
          ? [{ key: (item as Entry).key, at: Number.isFinite((item as Entry).at) ? (item as Entry).at : now }]
          : [])
    }
  } catch { /* unreadable: starts empty */ }
  parsed.set(storageKey, { raw, entries })
  return entries
}

/** The remembered keys, oldest first. */
export function readRecentKeys(storageKey: string, now = Date.now()): string[] {
  return readEntries(storageKey, now).map(entry => entry.key)
}

/** Adds (or refreshes) a key and writes the list back within its limits. */
export function rememberRecentKey(storageKey: string, key: string, { limit = recentKeyDefaultLimit, maxAgeMs = recentKeyDefaultMaxAgeMs }: RecentKeyLimits = {}, now = Date.now()) {
  const kept = readEntries(storageKey, now).filter(entry => entry.key !== key && now - entry.at <= maxAgeMs)
  const next = [...kept, { key, at: now }].slice(-limit)
  writeStoredItem(storageKey, JSON.stringify(next))
  // Keyed by what storage now holds: after a refused write this page still knows the new key.
  parsed.set(storageKey, { raw: readStoredItem(storageKey), entries: next })
}
