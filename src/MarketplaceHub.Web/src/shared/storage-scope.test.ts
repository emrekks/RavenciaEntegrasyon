import { afterEach, describe, expect, it } from 'vitest'
import { appendNotification, readNotificationHistory } from './notifications'
import { clearPersonalStorage, deviceStorageKeyPrefixes, personalStorageKeyPrefixes, scopedStorageKey, setStorageScope } from './storage-scope'

describe('per-person browser storage', () => {
  afterEach(() => { setStorageScope(null, null); localStorage.clear() })

  it('keeps notification history apart per tenant and user', () => {
    setStorageScope('tenant-a', 'user-1')
    appendNotification('Fatura oluşturulamadı.', 'error')
    expect(readNotificationHistory()).toHaveLength(1)

    setStorageScope('tenant-a', 'user-2')
    expect(readNotificationHistory()).toHaveLength(0)
    setStorageScope('tenant-b', 'user-1')
    expect(readNotificationHistory()).toHaveLength(0)

    setStorageScope('tenant-a', 'user-1')
    expect(readNotificationHistory()).toHaveLength(1)
    expect(scopedStorageKey('rv-tab-counts:Soru durumu')).toBe('rv-tab-counts:Soru durumu@tenant-a:user-1')
  })

  it('sign-out removes every person’s history and remembered counts, old unscoped keys too', () => {
    localStorage.setItem('ravencia.notification-history', '[]')
    localStorage.setItem('ravencia.notification-history@tenant-a:user-1', '[]')
    localStorage.setItem('rv-tab-counts:Siparişler@tenant-a:user-1', '{}')
    localStorage.setItem('rv-question-sync-count', '4')
    localStorage.setItem('ravencia.sidebarPinned', 'true')

    clearPersonalStorage()

    expect(Object.keys(localStorage)).toEqual(['ravencia.sidebarPinned'])
  })

  it('sign-out removes the background notices and the last commission tariff too, keeps device choices (WEB-ALTYAPI-9)', () => {
    localStorage.setItem('ravencia.attention-notices@tenant-a:user-1', '{}')
    localStorage.setItem('price-stock-monitor-notified@tenant-a:user-1', '{}')
    localStorage.setItem('rv-commission-tariff-last', '{"connectionId":"c1","tariffId":"t1"}')
    localStorage.setItem('ravencia.rememberedEmail', 'kisi@example.com')
    clearPersonalStorage()
    expect(Object.keys(localStorage)).toEqual(['ravencia.rememberedEmail'])
  })

  it('sign-out removes the tab’s "Sonra Karar Ver" on workspace invitations from sessionStorage, keeps the build reload mark', () => {
    sessionStorage.setItem('rv-invitations-later@tenant-a:user-1', '1')
    sessionStorage.setItem('rv-reloaded-for-new-build', '1')
    clearPersonalStorage()
    expect(Object.keys(sessionStorage)).toEqual(['rv-reloaded-for-new-build'])
    sessionStorage.clear()
  })

  it('names every browser-storage key the app writes as personal (cleared at sign-out) or as this device’s', () => {
    // Every localStorage.setItem / writeStoredItem call of the sources, with its key resolved from a literal, a constant
    // of the same file, a template's fixed start or scopedStorageKey(...) around one of those.
    const sources = import.meta.glob<string>(['../**/*.ts', '../**/*.tsx', '!../**/*.test.ts', '!../**/*.test.tsx'], { query: '?raw', import: 'default', eager: true })
    // Helpers that write a key their caller gives (the callers' keys are checked where they are named).
    const keyTakingHelpers = ['/local-storage.ts', '/recent-keys-storage.ts', '/CollapsibleLibrary.tsx']
    const resolve = (expression: string, text: string): string | null => {
      const trimmed = expression.trim()
      const literal = /^['`]([^'`$]*)/.exec(trimmed)
      if (literal) return literal[1]
      const scoped = /^scopedStorageKey\((.*)\)$/.exec(trimmed)
      if (scoped) return resolve(scoped[1], text)
      const call = /^(\w+)\(\)$/.exec(trimmed)
      if (call) {
        const body = new RegExp(`function ${call[1]}\\(\\)[^{]*\\{([^}]*)\\}`).exec(text) ?? new RegExp(`const ${call[1]} = \\(\\) => ([^\\n]*)`).exec(text)
        const returned = body && /(?:return\s+)?(scopedStorageKey\([^)]*\)|['`][^'`]*['`])/.exec(body[1])
        return returned ? resolve(returned[1], text) : null
      }
      if (!/^\w+$/.test(trimmed)) return null
      const constant = new RegExp(`const ${trimmed}\\s*=\\s*([^\\n;]+)`).exec(text)
      return constant && !constant[1].trim().startsWith(trimmed) ? resolve(constant[1], text) : null
    }
    const known = [...personalStorageKeyPrefixes, ...deviceStorageKeyPrefixes]
    const unresolved: string[] = []
    const unnamed: string[] = []
    let writes = 0
    for (const [file, text] of Object.entries(sources)) {
      if (keyTakingHelpers.some(helper => file.endsWith(helper))) continue
      for (const match of text.matchAll(/(?:localStorage\.setItem|sessionStorage\.setItem|writeStoredItem|writeSessionItem)\(\s*([^,]+),/g)) {
        writes++
        const key = resolve(match[1], text)
        if (key === null) unresolved.push(`${file}: ${match[1]}`)
        else if (!known.some(prefix => key.startsWith(prefix))) unnamed.push(`${file}: ${key}`)
      }
    }
    expect(writes).toBeGreaterThan(10)
    expect(unresolved).toEqual([])
    expect(unnamed).toEqual([])
  })
})
