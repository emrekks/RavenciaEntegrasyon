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

// Every key that holds one person's view, removed at sign-out; the unscoped keys of earlier versions start with the same
// names. A new key goes here or in deviceStorageKeyPrefixes (storage-scope.test.ts reads every write in the sources).
export const personalStorageKeyPrefixes = [
  'ravencia.notification-history', 'rv-tab-counts:', 'rv-question-sync-count', 'rv-appearance-settings',
  // Background notices already raised, and the store and tariff the commission tab last opened on.
  'ravencia.attention-notices', 'price-stock-monitor-notified', 'rv-commission-tariff-last'
] as const

// Keys that belong to this browser rather than to the person signed in, kept at sign-out on purpose: layout and theme
// choices, the remembered sign-in e-mail ("Beni Hatırla" is for the next sign-in), label settings and templates, the
// labels printed on this device, and the answered idempotency keys (a resend after sign-in must not repeat an answered
// request).
export const deviceStorageKeyPrefixes = [
  'ravencia.sidebarPinned', 'ravencia.rememberedEmail', 'ravencia.rememberMe', 'ravencia.visualTheme',
  'ravencia.shippingLabelSettings', 'ravencia.shippingLabelTemplates', 'ravencia.printedShippingLabels',
  'rv-answered-idempotency-keys', 'rv.priceSuggestion.assumptions', 'commission-table-zoom', 'rv-category-library-open', 'rv-height:'
] as const

const personalKeyPrefixes: readonly string[] = personalStorageKeyPrefixes

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
