import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const apiState = vi.hoisted(() => ({ calls: [] as Array<{ path: string; body: Record<string, unknown> }> }))

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  return {
    ...original,
    hubApi: vi.fn(async (path: string, init?: RequestInit) => {
      const body = JSON.parse(String(init?.body ?? '{}')) as Record<string, unknown>
      apiState.calls.push({ path, body })
      if (path === '/products/bulk-field') return { updatedCount: (body.productIds as string[]).length, unchangedCount: 0, variantCount: 6, failures: [] }
      throw new Error(`Test API fixture does not handle ${path}`)
    }),
    loadAllPages: vi.fn(async (path: string) => {
      if (path === '/catalog/brands') return { items: [{ id: 'b1', version: 1, name: 'Ravencia', isActive: true }, { id: 'b2', version: 1, name: 'Eski Marka', isActive: false }] }
      if (path === '/catalog/categories') return { items: [{ id: 'c1', version: 1, name: 'Bluz', path: 'Giyim > Bluz', depth: 1, isLeaf: true, isActive: true }] }
      throw new Error(`Test API fixture does not handle ${path}`)
    })
  }
})

import { BulkFieldDialog } from './BulkFieldDialog'

let container: HTMLDivElement
let root: Root

async function settle() {
  await act(async () => { await new Promise(resolve => setTimeout(resolve, 15)) })
}

async function renderDialog(onDone = vi.fn()) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  root = createRoot(container)
  await act(async () => { root.render(<QueryClientProvider client={client}><BulkFieldDialog groups={[['p1', 'p2'], ['p3']]} cardCount={2} variantCount={6} onClose={vi.fn()} onDone={onDone} /></QueryClientProvider>) })
  await settle()
  return onDone
}

const trigger = (label: string) => document.body.querySelector<HTMLButtonElement>(`.pm-trigger[aria-label^="${label}:"]`)!
async function pick(label: string, option: string) {
  await act(async () => { trigger(label).click() })
  const row = [...document.body.querySelectorAll<HTMLButtonElement>('[role="option"]')].find(item => item.textContent?.trim() === option)
  if (!row) throw new Error(`No option ${option}`)
  await act(async () => { row.click() })
  await settle()
}
const summary = () => document.body.querySelector('.bf-summary')!.textContent
const typeInto = async (input: HTMLInputElement | HTMLTextAreaElement, value: string) => {
  const setter = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(input), 'value')!.set!
  await act(async () => { setter.call(input, value); input.dispatchEvent(new Event('input', { bubbles: true })) })
}
const apply = async () => {
  const button = [...document.body.querySelectorAll<HTMLButtonElement>('.rr-m-foot button')].find(item => item.textContent === 'Uygula')!
  await act(async () => { button.click() })
  await settle()
}

beforeEach(() => {
  apiState.calls.length = 0
  container = document.createElement('div')
  document.body.appendChild(container)
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
  document.body.innerHTML = ''
})

describe('BulkFieldDialog', () => {
  it('opens on KDV Oranı and switches the control with the chosen field', async () => {
    await renderDialog()
    expect(document.body.querySelector('.rr-modal h2')?.textContent).toContain('Toplu Değer Ata')
    expect(trigger('Atanacak Alan').getAttribute('aria-label')).toBe('Atanacak Alan: KDV Oranı')
    expect(trigger('KDV Oranı')).not.toBeNull()

    await pick('Atanacak Alan', 'Desi')
    expect(document.body.querySelector<HTMLInputElement>('input[aria-label="Desi"]')).not.toBeNull()
    expect(document.body.querySelector('.pm-trigger[aria-label^="KDV Oranı:"]')).toBeNull()

    await pick('Atanacak Alan', 'Marka')
    await settle()
    await pick('Marka', 'Ravencia')
    expect(summary()).toBe('2 ürün (6 varyant) için Marka = Ravencia atanacak')
    await act(async () => { trigger('Marka').click() })
    expect([...document.body.querySelectorAll('[role="option"]')].map(item => item.textContent?.trim())).not.toContain('Eski Marka')

    await pick('Atanacak Alan', 'Açıklama')
    expect(document.body.querySelector('textarea[aria-label="Açıklama HTML kodu"]')).not.toBeNull()
  })

  it('names the value in the footer and sends only the chosen field, whole models per request', async () => {
    const onDone = await renderDialog()
    await pick('Atanacak Alan', 'Desi')
    expect(summary()).toBe('2 ürün (6 varyant) için Desi = — atanacak')
    await apply()
    expect(document.body.querySelector('.rr-err')?.textContent).toContain('Desi sıfırdan büyük')
    expect(apiState.calls).toHaveLength(0)

    await typeInto(document.body.querySelector<HTMLInputElement>('input[aria-label="Desi"]')!, '0,5')
    expect(summary()).toBe('2 ürün (6 varyant) için Desi = 0,5 atanacak')
    await apply()
    expect(apiState.calls).toEqual([{ path: '/products/bulk-field', body: { productIds: ['p1', 'p2', 'p3'], field: 'desi', value: '0.5' } }])
    expect(onDone).toHaveBeenCalledTimes(1)
    expect(onDone.mock.calls[0][0]).toMatchObject({ title: 'Desi Atandı', kind: 'success' })
  })

  it('sends the description cleaned as the editor cleans it', async () => {
    await renderDialog()
    await pick('Atanacak Alan', 'Açıklama')
    await typeInto(document.body.querySelector<HTMLTextAreaElement>('textarea[aria-label="Açıklama HTML kodu"]')!, '<p>Yeni <b>bluz</b></p><script>alert(1)</script>')
    expect(document.body.querySelector('.bf-preview-body')?.innerHTML).toBe('<p>Yeni <b>bluz</b></p>')
    await apply()
    expect(apiState.calls[0].body).toEqual({ productIds: ['p1', 'p2', 'p3'], field: 'description', value: '<p>Yeni <b>bluz</b></p>' })
  })

  it('sends Satış Durumu as the bulk sale switch does', async () => {
    await renderDialog()
    await pick('Atanacak Alan', 'Satış Durumu')
    await pick('Satış Durumu', 'Satışa Kapalı')
    expect(summary()).toBe('2 ürün (6 varyant) için Satış Durumu = Satışa Kapalı atanacak')
    await apply()
    expect(apiState.calls[0].body).toEqual({ productIds: ['p1', 'p2', 'p3'], field: 'status', value: 'ARCHIVED' })
  })
})
