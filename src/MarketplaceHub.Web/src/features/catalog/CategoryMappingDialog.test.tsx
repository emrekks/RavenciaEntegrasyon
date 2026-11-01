import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const state = vi.hoisted(() => ({ mapping: null as null | Record<string, unknown> }))

vi.mock('../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../shared/api')>()
  const respond = async (path: string) => {
    if (path.startsWith('/reference-data/categories')) return { snapshotId: 's2', items: [{ externalId: '385', name: 'Bluz', path: 'Giyim & Aksesuar / Kadın / Giyim / Bluz', isLeaf: true, isActive: true }, { externalId: '900', name: 'Gömlek', path: 'Giyim / Gömlek', isLeaf: true, isActive: true }] }
    if (path.startsWith('/mappings/categories/')) return state.mapping
    return null
  }
  return { ...original, hubApi: vi.fn(respond) }
})

import { CategoryMappingDialog } from './CategoryMappingDialog'

;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true

let container: HTMLDivElement
let root: Root
beforeEach(() => { container = document.createElement('div'); document.body.append(container); root = createRoot(container) })
afterEach(() => { act(() => root.unmount()); container.remove() })

async function open() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  await act(async () => { root.render(<QueryClientProvider client={client}><CategoryMappingDialog connectionId="ptt" platformName="PTT AVM" categoryId="c1" categoryName="Bluz" onClose={vi.fn()} onSaved={vi.fn()} /></QueryClientProvider>) })
  for (let index = 0; index < 5; index++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 10)) })
}
const saveButton = () => [...document.querySelectorAll<HTMLButtonElement>('.category-mapping-dialog footer button')].find(item => item.textContent === 'Eşlemeyi Kaydet')!
const dialogText = () => document.querySelector('.category-mapping-dialog')?.textContent ?? ''

describe('CategoryMappingDialog with a saved mapping', () => {
  it('says a valid saved mapping needs no save', async () => {
    state.mapping = { externalId: '385', version: 1, status: 'VERIFIED', snapshotId: 's2' }
    await open()
    expect(saveButton().disabled).toBe(true)
    expect(dialogText()).toContain('Bu eşleme kayıtlı ve geçerli')
  })

  it('says why a saved mapping no longer counts and lets the same category be saved again', async () => {
    state.mapping = { externalId: '385', version: 1, status: 'VERIFIED', snapshotId: 's1' }
    await open()
    expect(dialogText()).toContain('eski PTT AVM kategori listesine göre yapılmış')
    expect(saveButton().disabled).toBe(false)
  })

  it('names a saved category the store no longer lists', async () => {
    state.mapping = { externalId: '111', version: 1, status: 'VERIFIED', snapshotId: 's2' }
    await open()
    expect(dialogText()).toContain('Kayıtlı kategori artık PTT AVM kategori listesinde yok')
    expect(saveButton().disabled).toBe(false)
  })

  it('takes a category with no saved mapping', async () => {
    state.mapping = null
    await open()
    expect(saveButton().disabled).toBe(true)
  })
})
