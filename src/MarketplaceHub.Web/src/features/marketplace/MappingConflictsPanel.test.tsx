import { act } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, waitFor } from '../../test/render'

// 3 Nov 2026 (Pazarama, Kalıp Tipi): "Relaxed" was mapped to Relaxed in one category and Rahat in another, and the panel
// could only say "save it again in the category you want". The row now lets the user pick one and use it everywhere.
type Call = { method: string; path: string; ifMatch?: string; body?: Record<string, unknown> }
const state = vi.hoisted(() => ({
  calls: [] as Call[],
  conflicts: null as unknown,
  lists: {} as Record<string, unknown>,
  fail: {} as Record<string, { status: number; code: string; message: string }>
}))

vi.mock('../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../shared/api')>()
  const respond = async (path: string, init?: RequestInit) => {
    const method = init?.method ?? 'GET'
    const headers = (init?.headers ?? {}) as Record<string, string>
    if (method !== 'GET') {
      state.calls.push({ method, path, ifMatch: headers['If-Match'], body: init?.body ? JSON.parse(String(init.body)) as Record<string, unknown> : undefined })
      const failure = Object.entries(state.fail).find(([needle]) => path.includes(needle))?.[1]
      if (failure) throw new original.ApiRequestError(failure.message, failure.status, failure.code)
      return method === 'PUT' ? { version: 1 } : true
    }
    if (path.startsWith('/mappings/conflicts')) return state.conflicts
    const list = Object.entries(state.lists).find(([needle]) => path.startsWith(needle))?.[1]
    if (list) return list
    throw new original.ApiRequestError('Bulunamadı', 404, 'NOT_FOUND')
  }
  return { ...original, hubApi: vi.fn(respond) }
})

import { MappingConflictsPanel } from './MappingConflictsPanel'

const scope = (category: string, name: string, version: number) => ({ scopeExternalId: `${category}/kalip`, categoryName: name, version })
function crossConflict(extraRahatCategory = false) {
  return {
    sameCategory: [],
    crossCategory: [{
      scopeExternalId: '', categoryName: '', attributeName: 'Kalıp Tipi', localValueId: 'v-relaxed', localValueName: 'Relaxed', templateExternalId: 'rahat',
      options: [
        { externalId: 'relaxed', name: 'Relaxed', version: 0, verifiedAt: null, categories: 1, scopes: [scope('jean', 'Jean', 2)] },
        { externalId: 'rahat', name: 'Rahat', version: 0, verifiedAt: null, categories: extraRahatCategory ? 2 : 1, scopes: [scope('pantolon', 'Pantolon', 3), ...(extraRahatCategory ? [scope('etek', 'Etek', 5)] : [])] }
      ]
    }]
  }
}
const valueList = (snapshotId: string, ids: string[]) => ({ snapshotId, resourceType: 'ATTRIBUTE_VALUES', items: ids.map(externalId => ({ externalId, name: externalId, isActive: true })) })

beforeEach(() => {
  state.calls = []
  state.fail = {}
  state.conflicts = crossConflict()
  state.lists = {
    '/reference-data/categories/pantolon/attributes/kalip/values': valueList('snap-pantolon', ['rahat', 'relaxed']),
    '/reference-data/categories/jean/attributes/kalip/values': valueList('snap-jean', ['relaxed']),
    '/reference-data/categories/etek/attributes/kalip/values': valueList('snap-etek', ['rahat'])
  }
})
afterEach(() => cleanup())

const settle = async () => { for (let index = 0; index < 6; index++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 5)) }) }
const button = (text: string) => [...document.querySelectorAll<HTMLButtonElement>('button')].find(item => item.textContent?.replace(/\s+/g, ' ').trim().startsWith(text))
const chip = (name: string) => [...document.querySelectorAll<HTMLButtonElement>('.mc-choice [role="radio"]')].find(item => item.textContent?.startsWith(name))!
const confirmButton = () => [...document.querySelectorAll<HTMLButtonElement>('.rv-confirm-dialog button')].find(item => item.textContent === 'Hepsinde Bunu Kullan')!
const dialogText = () => document.querySelector('.rv-confirm-dialog')?.textContent ?? ''

async function renderPanel() {
  const notices: string[] = []
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const invalidate = vi.spyOn(client, 'invalidateQueries')
  render(<QueryClientProvider client={client}><MappingConflictsPanel connectionId="pazarama" platformLabel="Pazarama" onNotice={value => notices.push(value)} expanded /></QueryClientProvider>)
  await waitFor(() => document.querySelector('.mc-choice'))
  return { notices, invalidate }
}
async function chooseAndApply(name: string) {
  await act(async () => { chip(name).click() })
  await act(async () => { button('Hepsinde Bunu Kullan')!.click() })
  await settle()
}

describe('Kategoriler Arasında Farklı Değer', () => {
  it('makes the option chips selectable and keeps the row action off until one is picked', async () => {
    await renderPanel()
    const apply = button('Hepsinde Bunu Kullan')!
    expect(apply.disabled).toBe(true)
    expect(chip('Rahat').textContent).toContain('Şablon')

    expect(apply.classList.contains('secondary')).toBe(true)
    expect(document.querySelector('.mc-choice-preview')).toBeNull()

    await act(async () => { chip('Relaxed').click() })
    expect(chip('Relaxed').getAttribute('aria-checked')).toBe('true')
    expect(chip('Relaxed').querySelector('.mc-choice-check')).not.toBeNull()
    expect(chip('Rahat').getAttribute('aria-checked')).toBe('false')
    expect(chip('Rahat').querySelector('.mc-choice-check')).toBeNull()
    expect(button('Hepsinde Bunu Kullan')!.disabled).toBe(false)
    expect(button('Hepsinde Bunu Kullan')!.classList.contains('secondary')).toBe(false)
    expect(document.querySelector('.mc-choice-preview')?.textContent).toBe('1 kategoride Rahat → Relaxed olacak; yeni kategoriler için şablon olur.')

    // arrow keys move the choice like a radio group
    await act(async () => { chip('Relaxed').dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true })) })
    expect(chip('Relaxed').getAttribute('aria-checked')).toBe('false')
    expect(chip('Rahat').getAttribute('aria-checked')).toBe('true')
    expect(document.querySelector('.mc-choice-preview')?.textContent).toBe('1 kategoride Relaxed → Rahat olacak.')
  })

  it('previews what changes and writes nothing when the confirm is cancelled', async () => {
    await renderPanel()
    await chooseAndApply('Relaxed')

    expect(dialogText()).toContain('Relaxed: 1 kategoride Rahat → Relaxed')
    expect(dialogText()).toContain('Pantolon: Rahat → Relaxed')
    expect(dialogText()).toContain('“Relaxed” yeni kategoriler için şablon olur.')
    await act(async () => { button('Vazgeç')!.click() })
    await settle()
    expect(state.calls).toEqual([])
  })

  it('saves the chosen value in each differing category with its snapshot, removes the old one by version and refreshes', async () => {
    const { notices, invalidate } = await renderPanel()
    await chooseAndApply('Relaxed')
    await act(async () => { confirmButton().click() })
    await settle()
    expect(state.calls).toEqual([
      { method: 'PUT', path: '/mappings/attribute-values/v-relaxed', ifMatch: undefined, body: { connectionId: 'pazarama', snapshotId: 'snap-pantolon', scopeExternalId: 'pantolon/kalip', externalId: 'relaxed', status: 'VERIFIED', updateTemplate: true } },
      { method: 'DELETE', path: '/mappings/attribute-values/v-relaxed?connectionId=pazarama&scopeExternalId=pantolon%2Fkalip&externalId=rahat', ifMatch: '"v3"', body: undefined }
    ])
    expect(notices.at(-1)).toBe('“Relaxed” için 1 kategoride Relaxed kullanılıyor (Pantolon). Yeni kategoriler “Relaxed” alır.')
    const keys = invalidate.mock.calls.map(([filters]) => JSON.stringify(filters?.queryKey))
    expect(keys).toEqual(expect.arrayContaining([JSON.stringify(['mapping-conflicts', 'pazarama']), JSON.stringify(['attribute-value-mappings', 'pazarama', 'pantolon/kalip']), JSON.stringify(['publication-attribute-readiness'])]))
  })

  it('skips a category whose value list does not offer the chosen value and never writes it there', async () => {
    state.conflicts = crossConflict(true)
    const { notices } = await renderPanel()
    await chooseAndApply('Relaxed')

    expect(dialogText()).toContain('Pantolon: Rahat → Relaxed')
    expect(dialogText()).toContain('Etek: “Relaxed” bu kategorinin Pazarama değer listesinde yok; Rahat kalır.')
    await act(async () => { confirmButton().click() })
    await settle()

    expect(state.calls.map(call => `${call.method} ${call.path}`)).toEqual([
      'PUT /mappings/attribute-values/v-relaxed',
      'DELETE /mappings/attribute-values/v-relaxed?connectionId=pazarama&scopeExternalId=pantolon%2Fkalip&externalId=rahat'
    ])
    expect(state.calls.some(call => String(call.body?.scopeExternalId ?? call.path).includes('etek'))).toBe(false)
    expect(notices.at(-1)).toContain('Atlandı: Etek (“Relaxed” bu kategorinin Pazarama değer listesinde yok).')
  })

  it('names the category that failed and why when only part of the change is saved', async () => {
    state.conflicts = crossConflict(true)
    state.lists['/reference-data/categories/etek/attributes/kalip/values'] = valueList('snap-etek', ['rahat', 'relaxed'])
    state.fail['scopeExternalId=etek%2Fkalip'] = { status: 412, code: 'CONCURRENCY_CONFLICT', message: 'Kayıt sürümü değişti; güncel sürüm v6.' }
    const { notices } = await renderPanel()
    await chooseAndApply('Relaxed')
    expect(dialogText()).toContain('Relaxed: 2 kategoride Rahat → Relaxed')
    await act(async () => { confirmButton().click() })
    await settle()

    expect(notices.at(-1)).toContain('“Relaxed” için 1 kategoride Relaxed kullanılıyor (Pantolon).')
    expect(notices.at(-1)).toContain('Kaydedilemedi: Etek — Eşleme bu sırada başka bir işlemle değişti.')
  })

  it('only re-saves the chosen value as the template when every category already holds it', async () => {
    state.conflicts = { sameCategory: [], crossCategory: [{ ...crossConflict().crossCategory[0], templateExternalId: 'rahat', options: [
      { externalId: 'relaxed', name: 'Relaxed', version: 0, verifiedAt: null, categories: 2, scopes: [scope('jean', 'Jean', 2), scope('pantolon', 'Pantolon', 4)] },
      { externalId: 'rahat', name: 'Rahat', version: 0, verifiedAt: null, categories: 1, scopes: [scope('pantolon', 'Pantolon', 3)] }
    ] }] }
    await renderPanel()
    await chooseAndApply('Relaxed')
    await act(async () => { confirmButton().click() })
    await settle()

    expect(state.calls.map(call => `${call.method} ${call.path} ${call.ifMatch ?? ''}`.trim())).toEqual([
      'DELETE /mappings/attribute-values/v-relaxed?connectionId=pazarama&scopeExternalId=pantolon%2Fkalip&externalId=rahat "v3"',
      'PUT /mappings/attribute-values/v-relaxed "v2"'
    ])
    expect(state.calls[1].body).toMatchObject({ snapshotId: 'snap-jean', scopeExternalId: 'jean/kalip', externalId: 'relaxed', updateTemplate: true })
  })
})
