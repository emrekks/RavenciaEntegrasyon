import { createElement, useEffect, useMemo, type ReactNode } from 'react'
import { scopedStorageKey } from './storage-scope'

// Tab counts arrive a moment after the tabs; until then the badge was missing or showed "…", so the tabs after it
// jumped sideways when the number came in. The last count each tab showed is remembered per tab set and shown while
// the fresh one loads, dimmed and titled "Güncelleniyor" so it is not read as the current number. A count that could
// not be read is passed in as "—" by the caller and shown as that: an old number never stands in for a failed read.
type CountedItem = { value: string; count?: ReactNode }
export type RememberedCountItem<T extends CountedItem> = Omit<T, 'count'> & { count?: T['count'] | ReactNode }

/** What a caller passes as the count when its read failed. */
export const unknownCount = '—'

const pending = (count: ReactNode) => count === undefined || count === null || count === '' || count === '…' || count === '...'
const remembered = (count: ReactNode): count is string | number => typeof count === 'number' || (typeof count === 'string' && /\d/.test(count))

/** A remembered count, shown while the fresh one loads: dimmed, and titled so it reads as "being updated". */
export function staleCount(count: string | number): ReactNode {
  return createElement('span', { className: 'rv-count-stale', title: 'Güncelleniyor', 'aria-label': `${count}, güncelleniyor` }, count)
}

export function withRememberedCounts<T extends CountedItem>(items: T[], stored: Record<string, string | number>): Array<RememberedCountItem<T>> {
  return items.map(item => pending(item.count) && item.value in stored ? { ...item, count: staleCount(stored[item.value]) } : item)
}

export function useRememberedTabCounts<T extends CountedItem>(scope: string, items: T[]): Array<RememberedCountItem<T>> {
  const key = scopedStorageKey(`rv-tab-counts:${scope}`)
  const stored = useMemo<Record<string, string | number>>(() => {
    try { return JSON.parse(localStorage.getItem(key) ?? '{}') as Record<string, string | number> } catch { return {} }
  }, [key])
  const fresh = items.filter(item => remembered(item.count)).map(item => `${item.value}=${String(item.count)}`).join('|')
  useEffect(() => {
    if (!fresh) return
    const next = { ...stored }
    for (const item of items) if (remembered(item.count)) next[item.value] = item.count
    try { localStorage.setItem(key, JSON.stringify(next)) } catch { /* storage unavailable: counts just load as before */ }
  // eslint-disable-next-line react-hooks/exhaustive-deps -- `fresh` already covers every remembered count
  }, [key, fresh])
  return withRememberedCounts(items, stored)
}
