import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { settleUntilQuiet } from '../../../test/render'
import { ToastStackContext, type ToastStackApi } from '../../../shared/components/toast-stack-context'

// WEB-SAYFALAR-10 (scan 2026-11-15): pins what Kaydet / Ürünü Oluştur / Taslak / Yayınla send, written against the
// one-function submit before it was split into steps (buildUpdatePayload, buildCreatePayload, runMediaStep,
// runOfferWrites, runStockStep). Every write and the product reads are recorded in order (method, path, If-Match,
// whether an Idempotency-Key went, the body; random keys and file contents left out) with every toast the save shows,
// and compared with the recorded snapshot, so the split may not move a request, a header or a message.
const fixture = vi.hoisted(() => {
  const initial = () => ({
    version: 1,
    // The colour cards' versions (loadedFamilyVersions); raised by the product write only.
    familyVersion: 1,
    title: 'Yonca Desen Elbise',
    categoryId: 'c1',
    brandId: 'b1' as string | null,
    status: 'ACTIVE',
    mediaUrls: ['https://cdn.example.com/a.jpg', 'https://cdn.example.com/b.jpg'],
    variantMedia: { v1: ['https://cdn.example.com/a.jpg'], v2: ['https://cdn.example.com/a.jpg'], v3: ['https://cdn.example.com/b.jpg'], v4: ['https://cdn.example.com/b.jpg'] } as Record<string, string[]>,
    attributes: [{ attributeId: 'a-kumas', valueId: 'k-pamuk', textValue: null, numberValue: null }] as unknown[],
    onHand: { v1: 3, v2: 5, v3: 2, v4: 0 } as Record<string, number>,
    cost: { v1: 25, v2: null, v3: 30, v4: null } as Record<string, number | null>,
    requests: 0,
    uploads: 0,
    refusePublishFor: null as string | null,
    liveTy: true,
    refusePatch: null as null | { status: number; code?: string },
    // Answers the next PATCH with 412 without changing the product (a version raised by nothing the form shows).
    bumpVersionSilently: false,
    failUpload: false,
    // O2: values stored with more precision than a person may type (prices and costs keep four decimals, a stock may be fractional).
    precise: false,
    // A stock stored fractional (the server allows it); the form shows it as it is.
    fractionalStock: false,
    log: [] as Array<Record<string, unknown>>,
    toasts: [] as Array<Record<string, unknown>>
  })
  const state = initial()
  const statusFor = (connectionId: string, platformCode: string, offerId: string, price: number) => ({ connectionId, platformCode, platform: platformCode === 'N11' ? 'N11' : 'Trendyol', isLinked: state.liveTy && platformCode !== 'N11', isMatched: state.liveTy && platformCode !== 'N11', status: 'ACTIVE', offerId, offerVersion: 3, listPrice: price, salePrice: price, currency: 'TRY', vatRate: 20, vatInclusion: 'INCLUDED', roundingMode: 'HALF_EVEN', safetyStock: 0, offerStatus: 'ACTIVE', followsPanelPrice: true })
  const variant = (id: string, colour: string, size: string, sku: string, barcode: string) => ({
    id, version: 1, productId: 'p1', sku, barcode, modelCode: 'RYP006', optionSignature: `Renk: ${colour} | Beden: ${size}`, status: 'ACTIVE', weight: null, width: null, height: null, length: null, desi: 1,
    costPrice: state.precise && id === 'v1' ? 12.3456 : state.cost[id], onHand: state.fractionalStock && id === 'v1' ? 2.5 : state.onHand[id], available: state.onHand[id], inventoryVersion: state.onHand[id], offerId: `o-${id}`, listPrice: 100, salePrice: 90, defaultListPrice: state.precise ? 99.999 : 100, defaultSalePrice: state.precise ? 12.3456 : 90,
    currency: 'TRY', offerStatus: 'ACTIVE', priceVersion: 1, offerVersion: 3, vatRate: 20, vatInclusion: 'INCLUDED', roundingMode: null, safetyStock: 0, mediaUrls: state.variantMedia[id] ?? [], options: { Renk: colour, Beden: size }, attributes: [],
    platformStatuses: [statusFor('ty', 'TRENDYOL', `o-${id}`, 90), statusFor('n11', 'N11', `n-${id}`, 95)]
  })
  const product = () => ({
    id: 'p1', version: state.version, title: state.title, description: '<p>Yeterince uzun bir açıklama metni burada yer alır.</p>', brandId: state.brandId, categoryId: state.categoryId, status: state.status, updatedAt: '2026-11-01T10:00:00Z',
    variants: [variant('v1', 'Siyah', 'S', 'RYP006-SY-S', '8680000000011'), variant('v2', 'Siyah', 'M', 'RYP006-SY-M', '8680000000028'), variant('v3', 'Bordo', 'S', 'RYP006-BD-S', '8680000000035'), variant('v4', 'Bordo', 'M', 'RYP006-BD-M', '8680000000042')],
    primaryImageUrl: state.mediaUrls[0] ?? null, totalStock: 10, startingPrice: 90, currency: 'TRY', modelCode: 'RYP006', activePlatforms: ['TRENDYOL'], mediaUrls: state.mediaUrls, attributes: state.attributes, familyProductIds: ['p1'],
    familyVersions: { p1: state.familyVersion },
    trendyolWebColorValueId: 'wc-siyah', warrantyMonths: 24, deliveryDays: 2, vatRate: 20, vatInclusion: 'INCLUDED', defaultListPrice: state.precise ? 99.999 : 100, defaultSalePrice: state.precise ? 12.3456 : 90
  })
  const requirement = (attributeId: string, name: string, role: 'OPTION' | 'ATTRIBUTE', values: Array<{ id: string; value: string }>, extra: { isWebColor?: boolean; isRequired?: boolean; displayOrder?: number } = {}) => ({ attributeId, isRequired: extra.isRequired ?? false, allowsCustomValue: false, displayOrder: extra.displayOrder ?? (role === 'OPTION' ? 1 : 3), role, isWebColor: extra.isWebColor ?? false, attribute: { id: attributeId, version: 1, code: name.toUpperCase(), name, dataType: 'SINGLE_SELECT', values } })
  const renk = requirement('a-renk', 'Renk', 'OPTION', [{ id: 'r-siyah', value: 'Siyah' }, { id: 'r-bordo', value: 'Bordo' }, { id: 'r-mavi', value: 'Mavi' }])
  const beden = requirement('a-beden', 'Beden', 'OPTION', [{ id: 'b-s', value: 'S' }, { id: 'b-m', value: 'M' }, { id: 'b-l', value: 'L' }], { displayOrder: 2 })
  const webColor = requirement('a-wc', 'Web Color', 'ATTRIBUTE', [{ id: 'wc-siyah', value: 'Siyah' }, { id: 'wc-bordo', value: 'Bordo' }], { isWebColor: true })
  const kumas = requirement('a-kumas', 'Kumaş', 'ATTRIBUTE', [{ id: 'k-pamuk', value: 'Pamuk' }, { id: 'k-viskon', value: 'Viskon' }], { isRequired: true })
  const requirementsFor = (categoryId: string) => categoryId === 'c2' ? [renk, webColor] : [renk, beden, webColor, kumas]
  const connections = [
    { id: 'ty', platformCode: 'TRENDYOL', displayName: 'Trendyol Mağaza', externalStoreId: '930112', status: 'ACTIVE', externalWritesEnabled: true },
    { id: 'n11', platformCode: 'N11', displayName: 'N11 Mağaza', externalStoreId: '440011', status: 'ACTIVE', externalWritesEnabled: true }
  ]
  return { state, initial, product, requirementsFor, connections }
})

// The reads a save makes on its own (the product after a 412, the refetch after the save); the page's other reads
// (mappings, statuses, the taken-codes check) depend on render timing, not on the submit.
const recordedRead = (path: string) => path === '/products/p1'

// Random ids (an event id, a new row's key) are named, not pinned.
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
const withoutRandomIds = (value: unknown): unknown => typeof value === 'string' ? uuid.test(value) ? '<uuid>' : value
  : Array.isArray(value) ? value.map(withoutRandomIds)
    : value && typeof value === 'object' ? Object.fromEntries(Object.entries(value).map(([key, item]) => [key, withoutRandomIds(item)]))
      : value

function bodyForLog(body: unknown) {
  if (typeof body === 'string') return withoutRandomIds(JSON.parse(body))
  if (body instanceof FormData) return Object.fromEntries([...body.entries()].map(([key, value]) => [key, typeof value === 'string' ? value : `File(${(value as File).name})`]))
  return body ?? null
}

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const { state } = fixture
  const respond = async (path: string, init?: RequestInit) => {
    state.requests++
    const method = init?.method ?? 'GET'
    const headers = new Headers(init?.headers)
    if (method !== 'GET' ? !path.startsWith('/catalog/variant-codes/taken') : recordedRead(path)) {
      state.log.push({ method, path, ...(headers.get('If-Match') ? { ifMatch: headers.get('If-Match') } : {}), ...(headers.get('Idempotency-Key') ? { idempotencyKey: true } : {}), ...(init?.body !== undefined ? { body: bodyForLog(init.body) } : {}) })
    }
    const body = typeof init?.body === 'string' ? JSON.parse(init.body) as Record<string, unknown> : null
    if (path === '/products' && method === 'POST') return { ...fixture.product(), id: 'p1', status: String(body?.status ?? 'ACTIVE'), title: String(body?.title ?? '') }
    if (path === '/products/p1' && method === 'PATCH') {
      if (state.refusePatch) throw new original.ApiRequestError('Modelin başka bir rengi siz düzenlerken değişti; sayfayı yenileyip tekrar deneyin.', state.refusePatch.status, state.refusePatch.code)
      if (state.bumpVersionSilently) { state.bumpVersionSilently = false; state.version++ }
      const ifMatch = headers.get('If-Match')
      if (ifMatch !== `"v${state.version}"`) throw new original.ApiRequestError('Sürüm değişti.', 412)
      state.title = String(body?.title ?? state.title)
      if (typeof body?.categoryId === 'string') state.categoryId = body.categoryId
      for (const update of (body?.variantUpdates ?? []) as Array<{ id: string; costPrice: number | null }>) if (update.costPrice != null) state.cost[update.id] = update.costPrice
      state.version++
      state.familyVersion++
      return fixture.product()
    }
    if (path.startsWith('/inventory/') && method === 'PUT') { state.onHand[path.split('/')[2]] = Number((body as { onHand: number }).onHand); return {} }
    if (path === '/files/product-media' && method === 'POST') {
      if (state.failUpload) throw new original.ApiRequestError('Dosya yüklenemedi.', 500)
      state.uploads++
      state.version++
      return { fileAssetId: `f${state.uploads}`, version: state.version }
    }
    if (path === '/files/product-media-reconcile') {
      const request = body as { productId: string; variantId: string | null; items: Array<{ url: string }> }
      if (request.variantId) state.variantMedia[request.variantId] = (request.items ?? []).map(item => item.url)
      else state.mediaUrls = (request.items ?? []).map(item => item.url)
      state.version++
      return { version: state.version, productVersions: { p1: state.version } }
    }
    if (path === '/files/product-media-family-reorder') return { changedProductCount: 1 }
    if (path === '/products/p1') return fixture.product()
    if (path.startsWith('/catalog/categories/') && path.includes('/attribute-requirements')) return fixture.requirementsFor(path.split('/')[3])
    if (path.startsWith('/catalog/variant-codes/taken')) return { items: [] }
    if (path.startsWith('/products/p1/publication-status/')) return path.endsWith('/ty') && state.liveTy ? { actualStatus: 'ACTIVE', lastJobStatus: 'SUCCEEDED', profileId: null } : { actualStatus: 'UNKNOWN', lastJobStatus: null, profileId: null }
    if (path.startsWith('/products/p1/listing-profiles/') && method === 'GET') return null
    if (path.startsWith('/products/p1/listing-profiles/')) return { version: 1 }
    if (path.startsWith('/products/p1/publication-jobs')) {
      if (state.refusePublishFor && (body as { connectionId?: string } | null)?.connectionId === state.refusePublishFor) throw new original.ApiRequestError('Kategori eşleşmesi eksik.', 422, 'PUBLICATION_REFUSED')
      return { jobId: `j-${(body as { connectionId?: string } | null)?.connectionId ?? 'x'}`, status: 'PENDING' }
    }
    if (path.startsWith('/publication-jobs/') || path.startsWith('/jobs/')) return { job: { id: path.split('/')[2], status: 'SUCCEEDED', lastErrorSummary: null }, variantResults: { items: [] } }
    if (method === 'POST' && path === '/channel-offers') return { id: 'x', version: 4, listPrice: 1, salePrice: 1, currency: 'TRY' }
    if (method === 'PATCH' && path.startsWith('/channel-offers/')) return { id: path.split('/')[2], version: 4, listPrice: Number(body?.listPrice ?? 1), salePrice: Number(body?.salePrice ?? 1), currency: 'TRY' }
    if (path.startsWith('/mappings/categories?')) return fixture.connections.map(connection => ({ id: `cm-${connection.id}`, connectionId: connection.id, snapshotId: 's1', localId: state.categoryId, scopeExternalId: '', externalId: `${connection.id}-cat`, status: 'VERIFIED', verifiedAt: null, version: 1 }))
    if (path.startsWith('/mappings/categories/')) return { id: 'cm1', connectionId: 'ty', snapshotId: 's1', localId: state.categoryId, scopeExternalId: '', externalId: 'ty-cat', status: 'VERIFIED', verifiedAt: null, version: 1 }
    if (path.startsWith('/reference-data/categories?')) return { snapshotId: 's1', resourceType: 'CATEGORIES', fetchedAt: '2026-11-01T10:00:00Z', items: [{ externalId: 'ty-cat', parentExternalId: null, name: 'Elbise', path: 'Giyim > Elbise', depth: 0, isLeaf: true, isActive: true, isRequired: false, allowsCustomValue: false, allowsMultipleValues: false }, { externalId: 'n11-cat', parentExternalId: null, name: 'Elbise', path: 'Giyim > Elbise', depth: 0, isLeaf: true, isActive: true, isRequired: false, allowsCustomValue: false, allowsMultipleValues: false }] }
    if (path.startsWith('/reference-data/categories/')) return { snapshotId: 'a1', resourceType: 'CATEGORY_ATTRIBUTES', fetchedAt: '2026-11-01T10:00:00Z', items: [] }
    if (path.startsWith('/mappings/attributes?') || path.startsWith('/mappings/attribute-values?') || path.includes('/attribute-defaults')) return []
    if (path.startsWith('/mappings/conflicts') || path.startsWith('/mappings/attribute-name-hints')) return {}
    if (path.includes('/capabilities')) return []
    return null
  }
  const pages = async (path: string) => {
    state.requests++
    return {
      items: path.startsWith('/connections') ? fixture.connections
        : path.startsWith('/catalog/categories') ? [{ id: 'c1', name: 'Elbise', isLeaf: true, isActive: true, path: 'Elbise', depth: 0, version: 1 }, { id: 'c2', name: 'Bluz', isLeaf: true, isActive: true, path: 'Bluz', depth: 0, version: 1 }]
          : path.startsWith('/catalog/brands') ? [{ id: 'b1', name: 'Ravencia', isActive: true }]
            : [],
      nextCursor: null,
      hasMore: false
    }
  }
  return { ...original, hubApi: vi.fn(respond), hubPoll: vi.fn(respond), loadAllPages: vi.fn(pages) }
})

import { NewProductPage, ProductDetailPage } from './ProductEditorPages'

;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true

const toastStack: ToastStackApi = {
  show: input => { fixture.state.toasts.push({ kind: input.kind, title: input.title ?? null, message: input.message, ...(input.lines?.length ? { lines: [...input.lines] } : {}) }) },
  dismiss: () => undefined,
  dismissKey: key => { fixture.state.toasts.push({ dismissed: key }) },
  dismissWhere: () => undefined
}

let container: HTMLDivElement
let root: Root
let client: QueryClient
const where = { path: '' }
function Where() { where.path = useLocation().pathname; return null }

beforeEach(() => {
  Object.assign(fixture.state, fixture.initial())
  Element.prototype.scrollIntoView = () => undefined
  let blob = 0
  URL.createObjectURL = vi.fn(() => `blob:${++blob}`)
  URL.revokeObjectURL = vi.fn()
  localStorage.clear()
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
  document.body.innerHTML = ''
})

const settle = () => settleUntilQuiet(() => fixture.state.requests)

async function renderAt(path: string, state?: unknown, page?: React.ReactNode) {
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  await act(async () => {
    root.render(<ToastStackContext.Provider value={toastStack}><QueryClientProvider client={client}><MemoryRouter initialEntries={[{ pathname: path, state }]}><Where /><main>{page ?? <Routes>
      <Route path="/products/new" element={<NewProductPage />} />
      <Route path="/products/:id" element={<ProductDetailPage />} />
    </Routes>}</main></MemoryRouter></QueryClientProvider></ToastStackContext.Provider>)
  })
  await settle()
}

function need<T extends Element>(element: T | null | undefined, what: string): T {
  if (!element) throw new Error(`not found: ${what}`)
  return element
}
const q = <T extends Element = HTMLElement>(selector: string) => need(document.querySelector<T>(selector), selector)
const labelled = (text: string) => need([...container.querySelectorAll('label')].find(label => label.textContent?.startsWith(text)), `label ${text}`)
const inputOf = (text: string) => need(labelled(text).querySelector('input'), `input ${text}`)
const titleInput = () => q<HTMLInputElement>('.product-title-field input')

function setValue(input: HTMLInputElement | HTMLTextAreaElement, value: string) {
  const proto = input instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype
  const setter = Object.getOwnPropertyDescriptor(proto, 'value')!.set!
  act(() => { setter.call(input, value); input.dispatchEvent(new Event('input', { bubbles: true })) })
}
async function click(element: Element) { await act(async () => { (element as HTMLElement).click() }) }
async function submit(intent: 'save' | 'create' | 'draft') {
  const button = q<HTMLButtonElement>(`button[data-submit-intent="${intent}"]`)
  // Only what this press sends and says is pinned, not what opening the page did.
  fixture.state.log.length = 0
  fixture.state.toasts.length = 0
  await act(async () => { button.form!.requestSubmit(button) })
  await settle()
}
function pickFiles(names: string[]) {
  const input = q<HTMLInputElement>('input[aria-label="Ürün görsellerini dosya olarak seç"]')
  const files = names.map(name => new File([new Uint8Array(10)], name, { type: 'image/jpeg' }))
  Object.defineProperty(input, 'files', { value: files, configurable: true })
  act(() => { input.dispatchEvent(new Event('change', { bubbles: true })) })
}
async function fillRows(prefix: string) {
  const rows = [...document.querySelectorAll<HTMLInputElement>('input[aria-label$=" stok kodu"]')]
  rows.forEach((input, index) => {
    const label = input.getAttribute('aria-label')!.replace(/ stok kodu$/, '')
    setValue(input, `${prefix}-${index + 1}`)
    const barcode = document.querySelector<HTMLInputElement>(`input[aria-label="${label} barkod"]`)
    if (barcode) setValue(barcode, `${prefix}B${index + 1}`)
  })
  await settle()
}

// What the press did: the requests in order, the toasts in order, the page it ended on and whether the form is clean.
function outcome() {
  const unload = new Event('beforeunload', { cancelable: true })
  window.dispatchEvent(unload)
  return {
    requests: fixture.state.log,
    toasts: fixture.state.toasts,
    path: where.path,
    dirty: unload.defaultPrevented,
    stale: Boolean(document.querySelector('.product-stale-banner'))
  }
}

describe('what a save sends and says (characterization, WEB-SAYFALAR-10)', () => {
  it('create draft: a new card with required fields missing is kept as a draft', async () => {
    await renderAt('/products/new')
    setValue(titleInput(), 'Taslak Ürün')
    setValue(inputOf('Stok Kodu'), 'DRAFT-1')
    await settle()
    await submit('create')
    expect(outcome()).toMatchSnapshot()
  })

  it('create draft: Taslak Olarak Kaydet', async () => {
    await renderAt('/products/new')
    setValue(titleInput(), 'Açık Taslak')
    setValue(inputOf('Stok Kodu'), 'DRAFT-2')
    await settle()
    await submit('draft')
    expect(outcome()).toMatchSnapshot()
  })

  it('create draft: Taslak Olarak Kaydet without a stock code is refused before any write', async () => {
    await renderAt('/products/new')
    setValue(titleInput(), 'Açık Taslak')
    await settle()
    await submit('draft')
    expect(outcome()).toMatchSnapshot()
  })

  it('create active: a copy of a product with its codes changed', async () => {
    await renderAt('/products/new', { duplicateProductId: 'p1' })
    setValue(titleInput(), 'Kopya Ürün')
    await fillRows('KOPYA')
    await submit('create')
    expect(outcome()).toMatchSnapshot()
  })

  it('update: a title change', async () => {
    await renderAt('/products/p1')
    setValue(titleInput(), 'Yonca Desen Elbise Yeni')
    await settle()
    await submit('save')
    expect(outcome()).toMatchSnapshot()
  })

  it('update with a media change: two new files', async () => {
    await renderAt('/products/p1')
    pickFiles(['yeni-1.jpg', 'yeni-2.jpg'])
    await settle()
    await submit('save')
    expect(outcome()).toMatchSnapshot()
  })

  it('update with a media change whose upload fails: the card is saved, the pictures are not', async () => {
    await renderAt('/products/p1')
    setValue(titleInput(), 'Yeni Ad')
    pickFiles(['yeni-1.jpg'])
    await settle()
    fixture.state.failUpload = true
    await submit('save')
    expect(outcome()).toMatchSnapshot()
  })

  it('update with channel prices: Trendyol switched to its own price', async () => {
    await renderAt('/products/p1')
    await click(q('button.marketplace-pricing-open'))
    await click(q('input[aria-label="Trendyol Mağaza için özel fiyat"]'))
    await settle()
    const inputs = [...document.querySelectorAll<HTMLInputElement>('fieldset[aria-label="Trendyol Mağaza pazaryeri fiyatlandırması"] input[inputmode="decimal"]')]
    expect(inputs).toHaveLength(2)
    setValue(inputs[0], '130')
    setValue(inputs[1], '111')
    await settle()
    await submit('save')
    expect(outcome()).toMatchSnapshot()
  })

  // B1 (23 Nov 2026): the editor's number boxes were type="number"; a tr-TR browser gave "1.250" as 1.25. Each is now a
  // Turkish text box: what is typed is what the request carries, or the save is refused.
  const sentBodies = () => JSON.stringify(fixture.state.log)
  async function confirmJump() {
    const confirm = [...document.querySelectorAll<HTMLButtonElement>('.price-jump-confirm-modal button')].find(button => button.textContent === 'Onayla ve Kaydet')
    expect(confirm, 'price-jump confirmation').toBeDefined()
    await click(confirm!)
    await settle()
  }
  it('sends "1.250" typed in the variant table as 1250, after naming the jump', async () => {
    await renderAt('/products/p1')
    for (const [box, typed] of [['satış fiyatı', '1.200'], ['liste fiyatı', '1.250'], ['maliyeti', '1.000,50'], ['stok', '1.000']]) {
      const input = q<HTMLInputElement>(`input[aria-label="Renk:Siyah_Beden:S ${box}"]`)
      expect(input.type).toBe('text')
      setValue(input, typed)
    }
    await settle()
    await submit('save')
    expect(document.querySelector('.price-jump-confirm-modal')?.textContent).toContain('Üç Katından Fazla Artıyor: 90,00 → 1.200,00')
    expect(fixture.state.log.filter(entry => entry.method !== 'GET')).toEqual([])
    await confirmJump()
    expect(sentBodies()).toContain('"defaultSalePrice":1200')
    expect(sentBodies()).toContain('"defaultListPrice":1250')
    expect(sentBodies()).toContain('"costPrice":1000.5')
    expect(sentBodies()).toContain('"onHand":1000')
    expect(sentBodies()).not.toMatch(/:1\.2[05]?[,}]/)
  })
  it.each(['1.25', '1,250', '1250.50'])('refuses "%s" typed in the variant table and sends no price', async typed => {
    await renderAt('/products/p1')
    setValue(q<HTMLInputElement>('input[aria-label="Renk:Siyah_Beden:S satış fiyatı"]'), typed)
    await settle()
    expect(q<HTMLInputElement>('input[aria-label="Renk:Siyah_Beden:S satış fiyatı"]').getAttribute('aria-invalid')).toBe('true')
    await submit('save')
    expect(fixture.state.log.filter(entry => entry.method !== 'GET')).toEqual([])
  })

  // O2: a value stored with four decimals (99.999, 12.3456) is shown as held and an untouched save sends exactly what it
  // sent before this branch: the stored numbers, nothing refused, no extra request.
  it('saves an untouched record holding four-decimal prices and cost as it was', async () => {
    const untouchedSave = async (precise: boolean) => {
      fixture.state.precise = precise
      await renderAt('/products/p1')
      setValue(titleInput(), 'Yeni Ad')
      await settle()
      await submit('save')
      const log = JSON.parse(JSON.stringify(fixture.state.log)) as Array<Record<string, unknown>>
      act(() => root.unmount()); container.remove(); document.body.innerHTML = ''; localStorage.clear(); container = document.createElement('div'); document.body.append(container); root = createRoot(container)
      Object.assign(fixture.state, fixture.initial())
      return log
    }
    const plain = await untouchedSave(false)
    const precise = await untouchedSave(true)
    expect(precise.map(entry => [entry.method, entry.path])).toEqual(plain.map(entry => [entry.method, entry.path]))
    const replaced = JSON.stringify(plain).split('"defaultListPrice":100,').join('"defaultListPrice":99.999,').split('"defaultSalePrice":90,').join('"defaultSalePrice":12.3456,')
    expect(JSON.stringify(precise)).toBe(replaced)
  })

  it('saves an untouched record holding a fractional stock, sending no stock', async () => {
    fixture.state.fractionalStock = true
    await renderAt('/products/p1')
    expect(q<HTMLInputElement>('input[aria-label="Renk:Siyah_Beden:S stok"]').value).toBe('2,5')
    setValue(titleInput(), 'Yeni Ad')
    await settle()
    await submit('save')
    const writes = fixture.state.log.filter(entry => entry.method !== 'GET')
    expect(writes.map(entry => [entry.method, entry.path])).toEqual([['PATCH', '/products/p1']])
    expect(JSON.stringify(writes)).not.toContain('onHand')
  })

  it('update with a stock and cost change', async () => {
    await renderAt('/products/p1')
    setValue(q<HTMLInputElement>('input[aria-label="Renk:Siyah_Beden:S stok"]'), '9')
    setValue(q<HTMLInputElement>('input[aria-label="Renk:Siyah_Beden:S maliyeti"]'), '40')
    await settle()
    await submit('save')
    expect(outcome()).toMatchSnapshot()
  })

  it('stale version: a newer version with other changes refuses the save (412)', async () => {
    await renderAt('/products/p1')
    setValue(titleInput(), 'Benim Değişikliğim')
    await settle()
    fixture.state.title = 'Başka Sekmede Değişti'
    fixture.state.version++
    await act(async () => { await client.invalidateQueries({ queryKey: ['product', 'p1'] }) })
    await settle()
    await submit('save')
    expect(outcome()).toMatchSnapshot()
  })

  it('stale version: a version raised by nothing the form shows is retried once (412 then the latest version)', async () => {
    await renderAt('/products/p1')
    setValue(titleInput(), 'Benim Değişikliğim')
    await settle()
    fixture.state.bumpVersionSilently = true
    await submit('save')
    expect(outcome()).toMatchSnapshot()
  })

  it('family conflict: another colour card changed meanwhile (409 FAMILY_CHANGED)', async () => {
    await renderAt('/products/p1')
    setValue(titleInput(), 'Yeni Ad')
    await settle()
    fixture.state.refusePatch = { status: 409, code: 'FAMILY_CHANGED' }
    await submit('save')
    expect(outcome()).toMatchSnapshot()
  })

  it('an active card with a required field emptied stops before any write', async () => {
    await renderAt('/products/p1')
    setValue(inputOf('Liste Fiyatı'), '0')
    await settle()
    await submit('save')
    expect(outcome()).toMatchSnapshot()
  })

  it('publish: two marketplaces, one refused, with the offer writes', async () => {
    fixture.state.refusePublishFor = 'n11'
    fixture.state.liveTy = false
    await renderAt('/products/p1', undefined, <NewProductPage editProductId="p1" publishOnly />)
    for (const head of [...document.querySelectorAll<HTMLButtonElement>('button.publish-platform-card-head')]) if (!head.disabled) await click(head)
    await settle()
    const publish = q<HTMLButtonElement>('.publish-selection-footer button[type="submit"]')
    fixture.state.log.length = 0
    fixture.state.toasts.length = 0
    await click(publish)
    await settle()
    expect(outcome()).toMatchSnapshot()
  })
})
