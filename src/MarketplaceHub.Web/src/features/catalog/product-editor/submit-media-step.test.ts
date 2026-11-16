import { beforeEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ calls: [] as Array<{ method: string; path: string; body: unknown }>, uploads: 0, failUpload: false }))
vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  return {
    ...original,
    hubApi: vi.fn(async (path: string, init?: RequestInit) => {
      const body = init?.body instanceof FormData ? Object.fromEntries([...init.body.entries()].map(([key, value]) => [key, typeof value === 'string' ? value : 'file'])) : init?.body ? JSON.parse(String(init.body)) : null
      api.calls.push({ method: init?.method ?? 'GET', path, body })
      if (path === '/files/product-media') {
        if (api.failUpload) throw new original.ApiRequestError('Dosya yüklenemedi.', 500)
        return { fileAssetId: `f${++api.uploads}` }
      }
      if (path === '/files/product-media-family-reorder') return { changedProductCount: 2 }
      if (path === '/files/product-media-items') return { deletedCount: 1 }
      return {}
    })
  }
})

import { mediaStepParts, newMediaStepProgress, runMediaStep, type MediaStepInput } from './submit-media-step'
import type { Product } from '../shared'
import type { VariantDraft } from './editor-types'

const row = (key: string, mediaRefs: string[]): VariantDraft => ({ key, optionSignature: `Renk:${key}`, options: { Renk: key }, attributeValueIds: {}, sku: `SKU-${key}`, barcode: '', stock: 0, salePrice: 90, listPrice: 100, costPrice: null, mediaRefs })
const file = (id: string) => ({ id, file: new File([new Uint8Array(1)], `${id}.jpg`, { type: 'image/jpeg' }) })
const product = { id: 'p1', version: 2, mediaUrls: ['https://cdn/a.jpg'], variants: [{ id: 'v1', productId: 'p1', sku: 'SKU-siyah' }] } as unknown as Product
const input = (extra: Partial<MediaStepInput>): MediaStepInput => ({
  editing: true, product, rows: [row('siyah', [])], title: 'Elbise', mediaUrls: [], mediaFiles: [], mediaStrip: ['url|https://cdn/a.jpg'], draftMediaKeys: new Set(), pendingRemovalMediaKeys: new Set(), pendingFamilyMediaRemovals: [],
  familyMediaOrderDirty: false, chartKey: null, sizeChartUrl: null, initialSizeChartUrl: null, initialVariantMediaRefs: { v1: [] }, showProgress: () => undefined, onFamilyRemovalsWritten: vi.fn(), onFamilyOrderWritten: vi.fn(), ...extra
})

beforeEach(() => { api.calls.length = 0; api.uploads = 0; api.failUpload = false })

describe('runMediaStep', () => {
  it('writes nothing when the pictures did not change', async () => {
    const result = await runMediaStep(input({}), newMediaStepProgress())
    expect(api.calls).toEqual([])
    expect(result).toEqual({ galleryWriteCount: 0, cardColourLabels: [], familyChartMarked: false, removedFamilyMediaCount: 0, familyMediaChanged: false })
  })

  it('uploads each new file once, one at a time, then orders the model’s strip', async () => {
    const progress = newMediaStepProgress()
    const messages: string[] = []
    const onFamilyOrderWritten = vi.fn()
    const result = await runMediaStep(input({ mediaFiles: [file('n1'), file('n2')], mediaStrip: ['url|https://cdn/a.jpg', 'file|n1', 'file|n2'], showProgress: message => messages.push(message), onFamilyOrderWritten }), progress)
    // The new files have no colour: they stay in the card's own gallery (uploaded there), no other card is written.
    expect(api.calls.map(call => `${call.method} ${call.path}`)).toEqual(['POST /files/product-media', 'POST /files/product-media', 'PUT /files/product-media-family-reorder'])
    expect(api.calls.slice(0, 2).map(call => call.body)).toEqual([
      { file: 'file', productId: 'p1', mediaRole: 'GALLERY', sortOrder: '1', altText: 'Elbise' },
      { file: 'file', productId: 'p1', mediaRole: 'GALLERY', sortOrder: '2', altText: 'Elbise' }
    ])
    expect([...progress.uploadedMediaFileIds]).toEqual(['n1', 'n2'])
    expect([...progress.storedUrlByFileId.keys()]).toEqual(['n1', 'n2'])
    expect(messages).toHaveLength(2)
    expect(onFamilyOrderWritten).toHaveBeenCalledOnce()
    expect(result.familyMediaChanged).toBe(true)
    expect(api.calls[2].body).toEqual({ productId: 'p1', urlsInOrder: ['https://cdn/a.jpg', '/api/v1/files/product-media/f1/content', '/api/v1/files/product-media/f2/content'] })
  })

  it('a create writes its picture links to the one card first', async () => {
    await runMediaStep(input({ editing: false, mediaUrls: ['https://cdn/a.jpg', 'https://cdn/b.jpg'], initialVariantMediaRefs: {} }), newMediaStepProgress())
    expect(api.calls[0]).toEqual({ method: 'PUT', path: '/files/product-media-reconcile', body: expect.objectContaining({ productId: 'p1', variantId: null, items: [{ url: 'https://cdn/a.jpg', sortOrder: 0 }, { url: 'https://cdn/b.jpg', sortOrder: 1 }] }) })
  })

  it('keeps what it uploaded before a failed upload', async () => {
    const progress = newMediaStepProgress()
    api.failUpload = true
    await expect(runMediaStep(input({ mediaFiles: [file('n1')] }), progress)).rejects.toThrow('Dosya yüklenemedi.')
    expect(progress.uploadedMediaFileIds.size).toBe(0)
  })

  it('removes the other colours’ pictures Tümünü Temizle took off and says so', async () => {
    const onFamilyRemovalsWritten = vi.fn()
    const result = await runMediaStep(input({ pendingFamilyMediaRemovals: [{ url: 'https://cdn/z.jpg', mediaIds: ['m9'], sourceProductTitles: ['Bordo'] }], onFamilyRemovalsWritten }), newMediaStepProgress())
    expect(api.calls.map(call => `${call.method} ${call.path}`)).toContain('DELETE /files/product-media-items')
    expect(onFamilyRemovalsWritten).toHaveBeenCalledOnce()
    expect(result.removedFamilyMediaCount).toBe(1)
  })
})

describe('mediaStepParts', () => {
  const none = { galleryWriteCount: 0, cardColourLabels: [], familyChartMarked: false, removedFamilyMediaCount: 0, familyMediaChanged: false }
  it('names the size chart first, then the pictures, the removals and the order', () => {
    expect(mediaStepParts({ editing: true, sizeChartChanged: true, sizeChartUrl: 'x', mediaUrlCount: 0, mediaFileCount: 1 }, { ...none, galleryWriteCount: 1, cardColourLabels: ['Siyah +1'], removedFamilyMediaCount: 2 }))
      .toEqual({ chart: ['beden tablosu'], rest: ['renk kartlarının görselleri (Siyah +1)', 'modelden kaldırılan 2 görsel'] })
    expect(mediaStepParts({ editing: true, sizeChartChanged: true, sizeChartUrl: null, mediaUrlCount: 0, mediaFileCount: 0 }, { ...none, familyMediaChanged: true }))
      .toEqual({ chart: ['beden tablosu işareti kaldırıldı'], rest: ['modelin görsel sırası'] })
    expect(mediaStepParts({ editing: false, sizeChartChanged: false, sizeChartUrl: null, mediaUrlCount: 1, mediaFileCount: 0 }, none)).toEqual({ chart: [], rest: ['görseller'] })
  })
})
