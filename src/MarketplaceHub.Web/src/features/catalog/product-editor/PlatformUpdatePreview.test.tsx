import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { ProductUpdatePreviewConnection } from '../../../shared/api/generated/api-types'
import { PlatformUpdatePreview } from './PlatformUpdatePreview'
import { previewStores } from './platform-update-preview'

;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true

const hubApi = vi.hoisted(() => vi.fn())
vi.mock('../../../shared/api', async importOriginal => ({ ...await importOriginal<typeof import('../../../shared/api')>(), hubApi }))

let root: Root | undefined
let host: HTMLDivElement | undefined
let client: QueryClient

beforeEach(() => {
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  hubApi.mockReset()
})

afterEach(() => {
  if (root) act(() => root?.unmount())
  host?.remove()
  root = undefined
  host = undefined
})

const trendyol: ProductUpdatePreviewConnection = {
  connectionId: 'ty',
  platformCode: 'TRENDYOL',
  displayName: 'Trendyol Mağaza',
  models: [{
    label: 'MZ300',
    fields: [
      { field: 'TITLE', unit: 'COLOR', state: 'CHANGES', changed: 2, same: 0, unknown: 1, changes: [{ label: 'Siyah', before: 'Kadın Siyah Eski Bluz', after: 'Kadın Siyah Triko Bluz' }, { label: 'Bordo', before: 'Eski', after: 'Yeni' }] },
      { field: 'DESCRIPTION', unit: 'COLOR', state: 'CHANGES', changed: 1, same: 2, unknown: 0, changes: [{ label: 'Siyah', before: 'eski metin…', after: 'yeni metin…', beforeLength: 812, afterLength: 640 }] },
      { field: 'DESI', unit: 'VARIANT', state: 'UNKNOWN', changed: 0, same: 2, unknown: 6, changes: [] }
    ]
  }],
  unknownFields: ['IMAGES', 'VAT_RATE'],
  problems: [{ productId: 'p3', label: 'MZ300 · Gri', code: 'LISTING_PROFILE_REQUIRED', message: 'Yayın kaydı yok.' }],
  knownValuesReadAt: null
}

const sameStore: ProductUpdatePreviewConnection = { connectionId: 'hb', platformCode: 'HEPSIBURADA', displayName: 'Hepsiburada Mağaza', models: [{ label: 'MZ300', fields: [{ field: 'TITLE', unit: 'COLOR', state: 'SAME', changed: 0, same: 3, unknown: 0, changes: [] }] }], unknownFields: [], problems: [], knownValuesReadAt: null }

async function render(productIds: string[], fields = ['TITLE', 'DESI'] as const) {
  host = document.createElement('div')
  document.body.append(host)
  root = createRoot(host)
  await act(async () => root?.render(<QueryClientProvider client={client}><PlatformUpdatePreview productIds={productIds} connectionIds={['ty', 'hb']} fields={[...fields, 'PRICE']} /></QueryClientProvider>))
  for (let tick = 0; tick < 5; tick++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 0)) })
}

const text = () => host?.textContent ?? ''

describe('platform update preview', () => {
  it('counts the colours and sizes per field and keeps unknown apart from unchanged', () => {
    const [store, same] = previewStores([trendyol, sameStore])
    expect(store.rows.map(row => [row.label, row.text])).toEqual([
      ['Ürün Adı', '2 Renk Değişecek · 1 Renk Bilinmiyor'],
      ['Açıklama', '1 Renk Değişecek'],
      ['Desi', 'Pazaryerindeki Değer Bilinmiyor · 8 Beden']
    ])
    expect(store.rows[1].changes[0].lengths).toBe('812 karakter → 640 karakter')
    expect(store.unknownFields).toBe('Görseller, KDV Oranı')
    expect(store.allSame).toBe(false)
    expect(same.allSame).toBe(true)
  })

  it('asks the server with the product fields only and shows the changes, unknown fields and problems', async () => {
    hubApi.mockResolvedValue({ maxProducts: 50, connections: [trendyol, sameStore] })
    await render(['p1', 'p2', 'p3'])
    const [path, init] = hubApi.mock.calls[0]
    expect(path).toBe('/products/update-preview')
    expect(JSON.parse(init.body)).toEqual({ productIds: ['p1', 'p2', 'p3'], connectionIds: ['ty', 'hb'], fields: ['TITLE', 'DESI'] })
    expect(text()).toContain('Ürün Adı2 Renk Değişecek · 1 Renk Bilinmiyor')
    expect(text()).toContain('Kadın Siyah Eski Bluz')
    expect(text()).toContain('Görseller, KDV OranıPazaryerindeki Değer Bilinmiyor')
    expect(text()).toContain('Hepsiburada MağazaPazaryerindeki Değerlerle Aynı')
    expect(text()).toContain('1 kart için özet hazırlanamadı')
  })

  it('says when the summary failed, without holding anything back', async () => {
    hubApi.mockRejectedValue(new Error('Sunucuya ulaşılamadı.'))
    await render(['p1'])
    expect(host?.querySelector('[role="alert"]')?.textContent).toContain('Değişiklik özeti alınamadı: Sunucuya ulaşılamadı.')
  })

  it('shows a loading line while it is prepared', async () => {
    hubApi.mockReturnValue(new Promise(() => {}))
    await render(['p1'])
    expect(text()).toContain('Değişiklik özeti hazırlanıyor…')
  })

  it('does not ask above the cap and says so', async () => {
    await render(Array.from({ length: 51 }, (_, index) => `p${index}`))
    expect(hubApi).not.toHaveBeenCalled()
    expect(text()).toContain('Değişiklik özeti en çok 50 ürün için gösterilir; Gönder bundan etkilenmez.')
  })

  it('shows nothing when only the price or the stock goes', async () => {
    host = document.createElement('div')
    document.body.append(host)
    root = createRoot(host)
    await act(async () => root?.render(<QueryClientProvider client={client}><PlatformUpdatePreview productIds={['p1']} connectionIds={['ty']} fields={['PRICE', 'STOCK']} /></QueryClientProvider>))
    expect(hubApi).not.toHaveBeenCalled()
    expect(text()).toBe('')
  })
})
