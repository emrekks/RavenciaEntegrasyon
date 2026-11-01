// localStorage throws (SecurityError, QuotaExceededError) when the browser blocks site data or is in some private
// modes. What the panel keeps there is a convenience (theme, pinned menu, remembered e-mail), so a blocked storage reads
// as empty and a failed write is dropped instead of taking the shell down.
export function readStoredItem(key: string): string | null {
  try { return localStorage.getItem(key) } catch { return null }
}

export function writeStoredItem(key: string, value: string) {
  try { localStorage.setItem(key, value) } catch { /* blocked storage: the choice lasts for this page only */ }
}

export function removeStoredItem(key: string) {
  try { localStorage.removeItem(key) } catch { /* blocked storage: nothing was kept */ }
}
