import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { testConnection } from '../../../test/connection'
import { PlatformUpdateDialog, type PlatformUpdateTarget } from './PlatformUpdateDialog'

;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true

const hubApi = vi.hoisted(() => vi.fn())
vi.mock('../../../shared/api', async importOriginal => ({ ...await importOriginal<typeof import('../../../shared/api')>(), hubApi }))

let root: Root | undefined
let host: HTMLDivElement | undefined

afterEach(() => {
  if (root) act(() => root?.unmount())
  host?.remove()
  document.body.innerHTML = ''
  root = undefined
  host = undefined
  hubApi.mockReset()
})

const target = (id: string, platformCode: string): PlatformUpdateTarget => ({
  connection: testConnection({ id, platformCode, displayName: `${platformCode} Mağaza`, externalStoreId: `${id}-store`, status: 'ACTIVE' }),
  capabilities: { writable: true, content: true }
})

// Review findings (a) and (b) through the window: the summary asks each picked store for the cards the send reaches there
// (previewCards, the send's own rule), and says when the editor holds edits not saved yet.
describe('PlatformUpdateDialog summary', () => {
  it('asks each picked store for the cards Gönder reaches there and notes unsaved edits', async () => {
    hubApi.mockResolvedValue({ maxProducts: 50, connections: [] })
    const cards = vi.fn((connection: { id: string }) => connection.id === 'ty' ? ['siyah', 'bordo'] : ['siyah'])
    host = document.createElement('div')
    document.body.append(host)
    root = createRoot(host)
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    await act(async () => root?.render(<QueryClientProvider client={client}><PlatformUpdateDialog targets={[target('ty', 'TRENDYOL'), target('hb', 'HEPSIBURADA'), target('pz', 'PAZARAMA')]} selectedIds={['ty', 'hb']} onSelectedIdsChange={vi.fn()} fields={['TITLE']} onFieldsChange={vi.fn()} openGroups={['product', 'shipping', 'sales']} onOpenGroupsChange={vi.fn()} submitting={false} onClose={vi.fn()} onSend={vi.fn()} previewCards={cards} previewUnsaved /></QueryClientProvider>))
    // The summary is a lazily loaded chunk: on a busy machine its import outlasts a fixed number of ticks (the cause of the
    // flaky failure). Wait for the request itself, then for the note, instead of counting ticks.
    await vi.waitFor(() => expect(hubApi).toHaveBeenCalledTimes(1), { timeout: 4000, interval: 10 })
    await vi.waitFor(() => expect(document.querySelector('.platform-update-preview-note.is-unsaved')).not.toBeNull(), { timeout: 4000, interval: 10 })
    expect(JSON.parse(hubApi.mock.calls[0][1].body)).toEqual({ targets: [{ connectionId: 'ty', productIds: ['siyah', 'bordo'] }, { connectionId: 'hb', productIds: ['siyah'] }], fields: ['TITLE'] })
    expect(document.querySelector('.platform-update-preview-note.is-unsaved')?.textContent).toContain('Kaydedilmemiş değişiklikler bu özette yok')
  })
})
