import { useCallback, useState } from 'react'
import { scopedStorageKey } from './storage-scope'

// The last sort a person picked on a list page comes back the next time: after leaving the page, changing a tab, reloading
// and signing in again. It is kept per person (tenant and user in the key) and per page, in this browser; the key survives
// sign-out (deviceStorageKeyPrefixes). An address that names a sort wins; a stored value that is no longer an option is
// ignored. "Temizle" clears the other filters and never calls remember, so it never resets the sort.
const prefix = 'rv-sort:'

export function readStickySort<T extends string>(page: string, allowed: readonly T[]): T | null {
  try {
    const stored = localStorage.getItem(scopedStorageKey(`${prefix}${page}`))
    return allowed.find(item => item === stored) ?? null
  } catch { return null }
}

export function rememberSort(page: string, value: string) {
  try { localStorage.setItem(scopedStorageKey(`${prefix}${page}`), value) } catch { /* a private window keeps nothing */ }
}

/** The sort a list opens with: the address's, else the remembered one, else the page's default. */
export function initialSort<T extends string>(page: string, allowed: readonly T[], fallback: T, fromUrl?: string | null): T {
  const named = fromUrl?.trim().toUpperCase()
  const url = allowed.find(item => item.toUpperCase() === named)
  return url ?? readStickySort(page, allowed) ?? fallback
}

/** A sort state that opens on the remembered sort and remembers every pick. */
export function useStickySort<T extends string>(page: string, allowed: readonly T[], fallback: T, fromUrl?: string | null) {
  const [value, setValue] = useState<T>(() => initialSort(page, allowed, fallback, fromUrl))
  const pick = useCallback((next: T) => { setValue(next); rememberSort(page, next) }, [page])
  return [value, pick] as const
}

// "Sayfa Başına" is kept the same way: the size a person last picked on a list page opens that page again (the address still wins).
const sizePrefix = 'rv-page-size:'

export function storedPageSize(page: string, allowed: readonly number[], fallback: number): number {
  try {
    const stored = Number(localStorage.getItem(scopedStorageKey(`${sizePrefix}${page}`)))
    return allowed.includes(stored) ? stored : fallback
  } catch { return fallback }
}

export function rememberPageSize(page: string, size: number) {
  try { localStorage.setItem(scopedStorageKey(`${sizePrefix}${page}`), String(size)) } catch { /* a private window keeps nothing */ }
}
