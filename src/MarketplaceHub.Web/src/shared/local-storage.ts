// localStorage throws (SecurityError, QuotaExceededError) when the browser blocks site data or is in some private
// modes. What the panel keeps there is a convenience (theme, pinned menu, remembered e-mail), so a blocked storage reads
// as empty and a failed write is dropped instead of taking the shell down.
export function readStoredItem(key: string): string | null {
  try { return localStorage.getItem(key) } catch { return null }
}

// A refused write is said once per key in the console, so a full quota is visible instead of settings silently not saving.
const warnedKeys = new Set<string>()

export function writeStoredItem(key: string, value: string) {
  try {
    localStorage.setItem(key, value)
  } catch {
    // Blocked or full storage: the choice lasts for this page only.
    if (!warnedKeys.has(key)) { warnedKeys.add(key); console.warn('Tarayıcı deposuna yazılamadı (dolu ya da kapalı).', key) }
  }
}

// The same for this tab's sessionStorage: kept until the tab closes or sign-out clears it (storage-scope.ts).
export function readSessionItem(key: string): string | null {
  try { return sessionStorage.getItem(key) } catch { return null }
}

export function writeSessionItem(key: string, value: string) {
  try { sessionStorage.setItem(key, value) } catch { /* blocked storage: the choice lasts for this page only */ }
}

export function removeStoredItem(key: string) {
  try { localStorage.removeItem(key) } catch { /* blocked storage: nothing was kept */ }
}
