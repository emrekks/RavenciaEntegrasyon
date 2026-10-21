// What one person saw in the panel (notification history, remembered tab counts) is kept in the browser under a key
// of its tenant and user, so a colleague signing in on the same browser, or the same person in another tenant, never
// sees it. Signing out removes it altogether.
let scope = 'anonymous'

/** The scope of one tenant and user (`anonymous` when either is missing). */
export function storageScopeOf(tenantId: string | null | undefined, userId: string | null | undefined) {
  return tenantId && userId ? `${tenantId}:${userId}` : 'anonymous'
}

export function setStorageScope(tenantId: string | null | undefined, userId: string | null | undefined) {
  scope = storageScopeOf(tenantId, userId)
}

export function currentStorageScope() {
  return scope
}

/** The browser-storage key of `base` for the signed-in tenant and user. */
export function scopedStorageKey(base: string) {
  return `${base}@${scope}`
}

// Every key that holds one person's view; the unscoped keys of earlier versions start with the same names.
const personalKeyPrefixes = ['ravencia.notification-history', 'rv-tab-counts:', 'rv-question-sync-count']

/** Removes every person's notification history and remembered counts from this browser (at sign-out). */
export function clearPersonalStorage(storage: Pick<Storage, 'length' | 'key' | 'removeItem'> | null = typeof localStorage === 'undefined' ? null : localStorage) {
  if (!storage) return
  try {
    const keys: string[] = []
    for (let index = 0; index < storage.length; index++) {
      const key = storage.key(index)
      if (key && personalKeyPrefixes.some(prefix => key.startsWith(prefix))) keys.push(key)
    }
    for (const key of keys) storage.removeItem(key)
  } catch {
    // A blocked store has nothing to clear.
  }
}
