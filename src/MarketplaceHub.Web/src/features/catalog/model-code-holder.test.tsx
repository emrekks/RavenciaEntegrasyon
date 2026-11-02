import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const hubApi = vi.hoisted(() => vi.fn(async (path: string) => {
  if (path === '/catalog/model-codes/holder?code=MZ021Y-CD') return { taken: true, productId: 'p-1', productTitle: 'Kadın Bluz', message: "Bu model kodu 'Kadın Bluz' ürününde kullanılıyor. Yeni renk veya beden eklemek için o ürünü düzenleyin." }
  if (path.startsWith('/catalog/model-codes/holder?code=')) return { taken: false }
  throw new Error(`Test API fixture does not handle ${path}`)
}))

vi.mock('../../shared/api', async importOriginal => ({ ...await importOriginal<typeof import('../../shared/api')>(), hubApi }))

import { blockingModelCodeHolder, ModelCodeHolderNotice, modelCodeHolderMessage, modelCodeToCheck, useModelCodeHolder } from './model-code-holder'

let container: HTMLDivElement
let root: Root
let client: QueryClient

beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  container = document.createElement('div')
  document.body.append(container)
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  root = createRoot(container)
  hubApi.mockClear()
})

afterEach(() => {
  act(() => root.unmount())
  client.clear()
  container.remove()
  vi.useRealTimers()
})

function Probe({ code, enabled }: { code: string; enabled: boolean }) {
  const holder = useModelCodeHolder(code, enabled)
  return <div><span data-testid="state">{holder ? 'blocked' : 'free'}</span><ModelCodeHolderNotice holder={holder} /></div>
}

async function renderProbe(code: string, enabled = true) {
  await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter><Probe code={code} enabled={enabled} /></MemoryRouter></QueryClientProvider>) })
}

async function settle() {
  await act(async () => { vi.advanceTimersByTime(500) })
  await act(async () => { await vi.runAllTimersAsync() })
}

describe('model code holder', () => {
  it('asks only about a non-empty code within the field length', () => {
    expect(modelCodeToCheck('  MZ021Y-CD ')).toBe('MZ021Y-CD')
    expect(modelCodeToCheck('   ')).toBe('')
    expect(modelCodeToCheck('x'.repeat(161))).toBe('')
  })

  it('blocks only on a taken answer that names the holder', () => {
    expect(blockingModelCodeHolder({ taken: false })).toBeNull()
    expect(blockingModelCodeHolder(undefined)).toBeNull()
    expect(blockingModelCodeHolder({ taken: true, productId: 'p-1', productTitle: 'Bluz' })).toEqual({ productId: 'p-1', message: modelCodeHolderMessage('Bluz') })
    expect(modelCodeHolderMessage('Bluz')).toBe('Kullanımda: Bluz.')
    expect(modelCodeHolderMessage('Burgu Kumaş Bağcıklı Tunik Uzun Ad')).toBe('Kullanımda: Burgu Kumaş Bağcıklı Tunik….')
  })

  it('shows the message with a link to the holder once the typing settles', async () => {
    vi.useFakeTimers()
    await renderProbe('')
    await renderProbe('MZ021Y-CD')
    expect(hubApi).not.toHaveBeenCalled()
    expect(container.querySelector('[data-testid="state"]')?.textContent).toBe('free')
    await settle()
    expect(hubApi).toHaveBeenCalledWith('/catalog/model-codes/holder?code=MZ021Y-CD')
    expect(container.querySelector('[data-testid="state"]')?.textContent).toBe('blocked')
    const notice = container.querySelector('.model-code-holder-notice')
    expect(notice?.textContent).toContain('Kullanımda: Kadın Bluz.')
    expect(notice?.querySelector('a')?.getAttribute('href')).toBe('/products/p-1')
    expect(notice?.querySelector('a')?.textContent).toBe('Ürünü Aç')
  })

  it('leaves a free code, an empty code and an existing card alone', async () => {
    vi.useFakeTimers()
    await renderProbe('YENI-01')
    await settle()
    expect(container.querySelector('[data-testid="state"]')?.textContent).toBe('free')
    await renderProbe('')
    await settle()
    expect(container.querySelector('[data-testid="state"]')?.textContent).toBe('free')
    hubApi.mockClear()
    await renderProbe('MZ021Y-CD', false)
    await settle()
    expect(hubApi).not.toHaveBeenCalled()
    expect(container.querySelector('.model-code-holder-notice')).toBeNull()
  })
})
