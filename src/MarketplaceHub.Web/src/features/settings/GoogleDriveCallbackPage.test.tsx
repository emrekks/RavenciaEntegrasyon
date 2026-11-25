import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { MemoryRouter } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, describe, expect, it, vi } from 'vitest'

vi.mock('../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../shared/api')>()
  return { ...original, hubApi: vi.fn(async () => { throw new original.ApiRequestError('Google bağlantısı reddedildi.', 400, 'GOOGLE_CONSENT_DENIED') }) }
})

import { GoogleDriveCallbackPage } from './GoogleDriveCallbackPage'
import { firstRunPending, markFirstRun } from './offsite-backup'

;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true

let root: Root | null = null
afterEach(() => { act(() => root?.unmount()); root = null; sessionStorage.clear() })

describe('Google Drive callback', () => {
  it('drops the first-run flag when the connection fails, so a later connect sends no copy by itself', async () => {
    markFirstRun()
    const container = document.createElement('div')
    root = createRoot(container)
    await act(async () => {
      root!.render(<QueryClientProvider client={new QueryClient()}><MemoryRouter initialEntries={['/settings/google-drive/callback?error=access_denied&state=s1']}><GoogleDriveCallbackPage /></MemoryRouter></QueryClientProvider>)
    })
    for (let index = 0; index < 3; index++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 5)) })
    expect(firstRunPending()).toBe(false)
  })
})
