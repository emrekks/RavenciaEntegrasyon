import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, waitFor } from '../../test/render'

// Scan 25 Nov 2026 (D-4): a failed read of the connection's defaults said "Henüz varsayılan yok" and offered to add one,
// so a saved default could be overwritten unseen. Unknown is not none.
const state = vi.hoisted(() => ({ fail: true }))
vi.mock('../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../shared/api')>()
  return { ...original, hubApi: vi.fn(async () => { if (state.fail) throw new Error('Sunucu yanıt vermedi'); return [{ externalAttributeId: '7', attributeName: 'Menşei', valueName: 'TR' }] }) }
})

import { AttributeDefaultsPanel } from './AttributeDefaultsPanel'

afterEach(() => { cleanup(); state.fail = true })

function show() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(<QueryClientProvider client={client}><AttributeDefaultsPanel connectionId="c1" categoryScope="900" platformLabel="Trendyol" expanded remoteAttributes={[{ externalId: '7', name: 'Menşei', isActive: true, isRequired: true, allowsCustomValue: false }] as never} onNotice={() => undefined} /></QueryClientProvider>)
}

const text = () => document.body.textContent ?? ''

describe('AttributeDefaultsPanel', () => {
  it('says the defaults could not be read and offers no new default while they are unknown', async () => {
    show()
    await waitFor(() => document.querySelector('[role=alert]'))
    expect(document.querySelector('[role=alert]')?.textContent).toContain('yüklenemedi')
    expect(text()).not.toContain('Henüz varsayılan yok')
    expect(text()).not.toContain('Yeni Varsayılan')
    expect(document.querySelector('.panel-summary-count')?.textContent).toBe('—')
    expect([...document.querySelectorAll('button')].some(button => button.textContent === 'Yeniden Dene')).toBe(true)
  })

  it('lists the saved defaults once the read answers', async () => {
    state.fail = false
    show()
    await waitFor(() => document.querySelector('.attribute-defaults-list'))
    expect(text()).toContain('Yeni Varsayılan')
    expect(document.querySelector('.panel-summary-count')?.textContent).toBe('1')
  })
})
