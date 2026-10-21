import { QueryClient } from '@tanstack/react-query'
import { afterEach, describe, expect, it } from 'vitest'
import { clearQuerySnapshot, forgetPreviousSession, querySnapshotKey, restoreQuerySnapshot, saveQuerySnapshot } from './query-snapshot'
import { setStorageScope } from './storage-scope'

afterEach(() => { sessionStorage.clear(); setStorageScope(null, null) })

describe('reload snapshot', () => {
  it('shows the last answers after a reload and marks them to be asked for again', () => {
    setStorageScope('tenant', 'user')
    const before = new QueryClient()
    before.setQueryData(['orders', 'summary'], { new: 11 })
    saveQuerySnapshot(before)

    const after = new QueryClient()
    restoreQuerySnapshot(after)
    expect(after.getQueryData(['orders', 'summary'])).toEqual({ new: 11 })
    expect(after.getQueryState(['orders', 'summary'])?.isInvalidated).toBe(true)
  })

  it('keeps nothing for a signed-out page, an old snapshot or after sign-out', () => {
    const client = new QueryClient()
    client.setQueryData(['me'], { id: 'u' })
    saveQuerySnapshot(client)
    expect(sessionStorage.getItem(querySnapshotKey)).toBeNull()

    setStorageScope('tenant', 'user')
    saveQuerySnapshot(client, Date.now() - 16 * 60_000)
    const late = new QueryClient()
    restoreQuerySnapshot(late)
    expect(late.getQueryData(['me'])).toBeUndefined()

    saveQuerySnapshot(client)
    clearQuerySnapshot()
    saveQuerySnapshot(client)
    expect(sessionStorage.getItem(querySnapshotKey)).toBeNull()
    forgetPreviousSession(client)
  })
})
