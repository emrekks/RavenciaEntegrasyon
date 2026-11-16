import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { WorkspaceInvitationsPrompt } from './WorkspaceInvitationsPrompt'

const sent = vi.hoisted(() => [] as Array<{ path: string; method: string | undefined }>)
vi.mock('../shared/api', async importOriginal => ({
  ...await importOriginal<typeof import('../shared/api')>(),
  hubApi: vi.fn(async (path: string, init?: RequestInit) => {
    sent.push({ path, method: init?.method })
    return path.endsWith('/accept') ? { tenantId: 't1', workspaceName: 'X Tekstil' } : undefined
  })
}))

const invitation = { id: 'i1', workspaceName: 'X Tekstil', invitedBy: 'Ayşe Yılmaz', role: 'READONLY', invitedAt: '2026-11-16T09:00:00Z', expiresAt: '2026-11-30T09:00:00Z' }

let container: HTMLDivElement
let root: Root
const closed: string[] = []
const render = () => act(() => root.render(<QueryClientProvider client={new QueryClient()}>
  <WorkspaceInvitationsPrompt invitations={[invitation]} onClose={() => closed.push('close')} onSignOut={() => closed.push('sign-out')} />
</QueryClientProvider>))
const button = (label: string) => Array.from(document.querySelectorAll<HTMLButtonElement>('button')).find(item => item.textContent === label)!

beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  sent.length = 0
  closed.length = 0
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
})
afterEach(() => { act(() => root.unmount()); container.remove() })

// API-GUVENLIK-6: the invited account sees who invited it to which workspace with which role, and answers it.
describe('WorkspaceInvitationsPrompt', () => {
  it('lists the workspace, the inviter and the role, and accepts with Kabul Et', async () => {
    render()
    const text = document.body.textContent ?? ''
    expect(text).toContain('X Tekstil')
    expect(text).toContain('Ayşe Yılmaz')
    expect(text).toContain('Salt Okunur')

    await act(async () => { button('Kabul Et').click() })

    expect(sent).toEqual([{ path: '/auth/invitations/i1/accept', method: 'POST' }])
    expect(document.body.textContent).toContain('X Tekstil çalışma alanına katıldınız')
    act(() => button('Oturumu Kapat ve Geç').click())
    expect(closed).toEqual(['sign-out'])
  })

  it('declines with Reddet', async () => {
    render()
    await act(async () => { button('Reddet').click() })
    expect(sent).toEqual([{ path: '/auth/invitations/i1/decline', method: 'POST' }])
  })
})
