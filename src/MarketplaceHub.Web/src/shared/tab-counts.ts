import { useEffect, useMemo, type ReactNode } from 'react'

// Tab counts arrive a moment after the tabs; until then the badge was missing or showed "…", so the tabs after it
// jumped sideways when the number came in. The last count each tab showed is remembered per tab set and shown while
// the fresh one loads, so the badge is there, about as wide, from the first paint.
type CountedItem = { value: string; count?: ReactNode }

const pending = (count: ReactNode) => count === undefined || count === null || count === '' || count === '…' || count === '...'
const remembered = (count: ReactNode): count is string | number => typeof count === 'number' || (typeof count === 'string' && /\d/.test(count))

export function useRememberedTabCounts<T extends CountedItem>(scope: string, items: T[]): T[] {
  const key = `rv-tab-counts:${scope}`
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
  return items.map(item => pending(item.count) && item.value in stored ? { ...item, count: stored[item.value] } : item)
}
