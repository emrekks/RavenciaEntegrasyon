import { afterEach, describe, expect, it } from 'vitest'
import { appendNotification, readNotificationHistory } from './notifications'
import { clearPersonalStorage, scopedStorageKey, setStorageScope } from './storage-scope'

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
})
