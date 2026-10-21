import { QueryClient } from '@tanstack/react-query'
import { afterEach, describe, expect, it, vi } from 'vitest'

afterEach(() => { sessionStorage.clear(); vi.resetModules() })

// A fresh copy of the modules, as after F5: nothing of the previous page load is remembered but sessionStorage.
async function pageLoad() {
  const snapshot = await import('./query-snapshot')
  const scope = await import('./storage-scope')
  // What App does with each /me answer: the snapshot is told first, then the browser-storage scope is set.
  const confirm = (client: QueryClient, tenantId: string, userId: string) => {
    snapshot.confirmQuerySnapshotScope(client, tenantId, userId)
    scope.setStorageScope(tenantId, userId)
  }
  return { ...snapshot, ...scope, confirm }
}

function stored() {
  const text = sessionStorage.getItem('rv-query-snapshot')
  return text === null ? null : JSON.parse(text) as { scope: string; state: { queries: Array<{ queryKey: unknown[] }> } }
}

describe('reload snapshot', () => {
  it('shows the last answers after a reload and marks them to be asked for again', async () => {
    const first = await pageLoad()
    const before = new QueryClient()
    first.confirm(before, 'tenant', 'user')
    before.setQueryData(['orders', 'summary'], { new: 11 })
    first.saveQuerySnapshot(before)

    vi.resetModules()
    const second = await pageLoad()
    const after = new QueryClient()
    second.restoreQuerySnapshot(after)
    expect(after.getQueryData(['orders', 'summary'])).toEqual({ new: 11 })
    expect(after.getQueryState(['orders', 'summary'])?.isInvalidated).toBe(true)
    second.confirm(after, 'tenant', 'user')
    expect(after.getQueryData(['orders', 'summary'])).toEqual({ new: 11 })
    expect(stored()?.scope).toBe('tenant:user')
  })

  it('never keeps the session itself, so a reload waits for /me', async () => {
    const first = await pageLoad()
    const before = new QueryClient()
    first.confirm(before, 'tenant', 'user')
    before.setQueryData(['me'], { id: 'user', tenantId: 'tenant', state: 'ACTIVE' })
    before.setQueryData(['orders', 'summary'], { new: 11 })
    first.saveQuerySnapshot(before)
    expect(stored()?.state.queries.map(query => query.queryKey[0])).toEqual(['orders'])

    vi.resetModules()
    const second = await pageLoad()
    const after = new QueryClient()
    second.restoreQuerySnapshot(after)
    expect(after.getQueryData(['me'])).toBeUndefined()
  })

  it('drops every restored answer and the snapshot when /me names another tenant or user', async () => {
    const first = await pageLoad()
    const before = new QueryClient()
    first.confirm(before, 'tenant-x', 'user')
    before.setQueryData(['orders', 'summary'], { new: 11 })
    before.setQueryData(['buyers', 'b1'], { name: 'X buyer' })
    first.saveQuerySnapshot(before)

    // Another tab signed out and into tenant Y with the same cookie; this tab reloads.
    vi.resetModules()
    const second = await pageLoad()
    const after = new QueryClient()
    second.restoreQuerySnapshot(after)
    // Until /me answers, nothing restored is written back under any scope.
    second.saveQuerySnapshot(after)
    expect(stored()?.scope).toBe('tenant-x:user')

    second.confirm(after, 'tenant-y', 'user')
    expect(after.getQueryData(['orders', 'summary'])).toBeUndefined()
    expect(after.getQueryData(['buyers', 'b1'])).toBeUndefined()
    expect(stored()).toBeNull()

    // Answers read for Y afterwards are kept under Y only.
    after.setQueryData(['orders', 'summary'], { new: 2 })
    second.saveQuerySnapshot(after)
    expect(stored()?.scope).toBe('tenant-y:user')
  })

  it('drops the answers when the scope changes later in the same page load', async () => {
    const page = await pageLoad()
    const client = new QueryClient()
    page.confirm(client, 'tenant-x', 'user')
    client.setQueryData(['orders', 'summary'], { new: 11 })
    page.saveQuerySnapshot(client)
    page.confirm(client, 'tenant-y', 'user')
    expect(client.getQueryData(['orders', 'summary'])).toBeUndefined()
    expect(stored()).toBeNull()
  })

  it('keeps nothing for a signed-out page, an old snapshot or after sign-out', async () => {
    const page = await pageLoad()
    const client = new QueryClient()
    client.setQueryData(['orders', 'summary'], { new: 11 })
    page.saveQuerySnapshot(client)
    expect(stored()).toBeNull()

    page.confirm(client, 'tenant', 'user')
    page.saveQuerySnapshot(client, Date.now() - 16 * 60_000)
    const late = new QueryClient()
    page.restoreQuerySnapshot(late)
    expect(late.getQueryData(['orders', 'summary'])).toBeUndefined()

    page.saveQuerySnapshot(client)
    page.clearQuerySnapshot()
    page.saveQuerySnapshot(client)
    expect(stored()).toBeNull()

    // A new sign-in writes again once its /me is known.
    page.forgetPreviousSession(client)
    client.setQueryData(['orders', 'summary'], { new: 3 })
    page.saveQuerySnapshot(client)
    expect(stored()).toBeNull()
    page.confirm(client, 'tenant', 'user')
    page.saveQuerySnapshot(client)
    expect(stored()?.scope).toBe('tenant:user')
  })
})
