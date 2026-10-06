import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const apiState = vi.hoisted(() => ({ calls: [] as Array<{ path: string; method: string; body?: string }>, templates: [] as Array<{ id: string; title: string; text: string; version: number; updatedAt: string }>, detail: null as any, answerFailure: false, templateId: 1, totalCount: 1 }))

vi.mock('../../shared/api', () => ({
  loadAllPages: vi.fn(async () => ({ items: [
    { id: 'ty-store', platformCode: 'TRENDYOL', displayName: 'Trendyol Mağazası', status: 'ACTIVE' },
    { id: 'hb-store', platformCode: 'HEPSIBURADA', displayName: 'Hepsiburada Mağazası', status: 'ACTIVE' },
  ], nextCursor: null, hasMore: false })),
  hubApi: vi.fn(async (path: string, init?: RequestInit) => {
    const method = init?.method ?? 'GET'
    const body = typeof init?.body === 'string' ? init.body : undefined
    apiState.calls.push({ path, method, body })
    if (path === '/questions/sync-state') return [
      { connectionId: 'ty-store', platformCode: 'TRENDYOL', storeName: 'Trendyol Mağazası', historyImported: true, importedCount: 25, progressStatus: 'COMPLETED', lastSuccessAt: '2026-10-05T10:00:00Z' },
      { connectionId: 'hb-store', platformCode: 'HEPSIBURADA', storeName: 'Hepsiburada Mağazası', historyImported: false, importedCount: 3, progressStatus: 'INITIAL', lastSuccessAt: null },
    ]
    if (path.startsWith('/products?')) return { items: [{ title: 'Kadın triko bluz', primaryImageUrl: '/api/v1/files/product-media/asset-1/content', familyMediaUrls: [], variants: [{ sku: 'BLUZ-01', barcode: '86900001', modelCode: 'MZ001' }] }], nextCursor: null, hasMore: false }
    if (path === '/questions/sync') return { jobs: ['sync-1'], queuedConnections: 2 }
    if (path === '/question-templates') {
      if (method === 'GET') return apiState.templates
      if (method === 'POST') {
        const value = JSON.parse(body ?? '{}') as { title: string; text: string }
        const created = { id: `template-${apiState.templateId++}`, ...value, version: 1, updatedAt: '2026-10-05T10:00:00Z' }
        apiState.templates = [...apiState.templates, created]
        return created
      }
    }
    const templateMatch = path.match(/^\/question-templates\/(.+)$/)
    if (templateMatch) {
      const index = apiState.templates.findIndex(template => template.id === templateMatch[1])
      if (method === 'PUT' && index >= 0) {
        const value = JSON.parse(body ?? '{}') as { title: string; text: string }
        apiState.templates[index] = { ...apiState.templates[index], ...value, version: apiState.templates[index].version + 1 }
        return apiState.templates[index]
      }
      if (method === 'DELETE' && index >= 0) { apiState.templates.splice(index, 1); return undefined }
    }
    if (path === '/questions/q-product/answer' || path === '/questions/q-order/answer') {
      if (apiState.answerFailure) throw new Error('Pazaryeri cevabı kabul etmedi.')
      if (apiState.detail) apiState.detail = { ...apiState.detail, status: 'ANSWER_SUBMITTED', conversations: [...apiState.detail.conversations, { author: 'merchant', text: JSON.parse(body ?? '{}').text, createdAt: '2026-10-05T12:00:00Z' }] }
      return apiState.detail
    }
    const detailMatch = path.match(/^\/questions\/(q-product|q-order)$/)
    if (detailMatch) return detailMatch[1] === 'q-order' ? { ...apiState.detail, id: 'q-order', kind: 'ORDER', platformCode: 'HEPSIBURADA', storeName: 'Hepsiburada Mağazası', externalOrderNumber: 'HB-12345' } : apiState.detail
    if (path.startsWith('/questions?')) {
      const params = new URLSearchParams(path.slice(path.indexOf('?') + 1))
      const kind = params.get('kind')
      const status = params.get('status')
      const statusByFilter: Record<string, string> = { WAITING: 'WAITING_FOR_ANSWER', ANSWERED: 'ANSWERED', EXPIRED: 'EXPIRED', OTHER: 'REJECTED', ALL: 'WAITING_FOR_ANSWER' }
      const row = kind === 'ORDER' ? { ...apiState.detail, id: 'q-order', kind: 'ORDER', platformCode: 'HEPSIBURADA', storeName: 'Hepsiburada Mağazası', externalOrderNumber: 'HB-12345' } : { ...apiState.detail, id: 'q-product', kind: 'PRODUCT' }
      const rowStatus = status === 'WAITING' && row.status === 'ANSWER_SUBMITTED' ? row.status : statusByFilter[status ?? 'WAITING']
      return { items: [{ ...row, status: rowStatus }], page: Number(params.get('page') ?? 1), limit: Number(params.get('limit') ?? 50), totalCount: apiState.totalCount }
    }
    throw new Error(`Test API fixture does not handle ${method} ${path}`)
  }),
}))

import { QuestionsPage } from './QuestionsPage'

const createQuestion = () => ({
  id: 'q-product', connectionId: 'ty-store', platformCode: 'TRENDYOL', storeName: 'Trendyol Mağazası', externalQuestionId: 'ext-1', kind: 'PRODUCT', status: 'WAITING_FOR_ANSWER',
  questionText: 'Ürün boyu kaç santimetredir?', productName: 'Kadın triko bluz', productImageUrl: 'https://images.example/expired-product.jpg', productSku: 'BLUZ-01', productBarcode: '86900001', productModelCode: 'MZ001', customerName: 'Ayşe', externalOrderNumber: null,
  conversations: [{ author: 'customer', text: 'Ürün boyu kaç santimetredir?', createdAt: '2026-10-05T09:00:00Z' }, { author: 'merchant', text: 'Ölçüleri kontrol ediyorum.', createdAt: '2026-10-05T09:30:00Z', rejectionReason: 'Yanıt eksik bilgi içeriyor.' }],
  createdAt: '2026-10-05T09:00:00Z', expiresAt: '2026-10-06T09:00:00Z', lastRemoteModifiedAt: '2026-10-05T09:30:00Z', lastSyncedAt: '2026-10-05T10:00:00Z', version: 2,
})

let container: HTMLDivElement
let root: Root
let client: QueryClient

function button(label: string) {
  const found = Array.from(container.querySelectorAll<HTMLButtonElement>('button')).find(item => item.textContent?.includes(label))
  if (!found) throw new Error(`Could not find button containing “${label}”`)
  return found
}

function select(element: HTMLSelectElement, value: string) {
  act(() => { element.value = value; element.dispatchEvent(new Event('change', { bubbles: true })) })
}

function input(element: HTMLInputElement | HTMLTextAreaElement, value: string) {
  act(() => {
    const setter = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(element), 'value')?.set
    setter?.call(element, value)
    element.dispatchEvent(new Event('input', { bubbles: true }))
    element.dispatchEvent(new Event('change', { bubbles: true }))
  })
}

async function settle() {
  await act(async () => { await new Promise(resolve => setTimeout(resolve, 15)) })
}

function lastQuestionRequest() {
  const request = [...apiState.calls].reverse().find(item => item.path.startsWith('/questions?'))
  if (!request) throw new Error('No question list request was made')
  return new URLSearchParams(request.path.slice(request.path.indexOf('?') + 1))
}

async function renderPage() {
  client = new QueryClient({ defaultOptions: { queries: { retry: false, refetchInterval: false }, mutations: { retry: false } } })
  root = createRoot(container)
  await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter><QuestionsPage /></MemoryRouter></QueryClientProvider>) })
  await settle()
}

beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  apiState.calls = []
  apiState.templates = [{ id: 'template-1', title: 'Ölçü bilgisi', text: 'Ürün ölçülerini ürün sayfasında bulabilirsiniz.', version: 1, updatedAt: '2026-10-05T10:00:00Z' }]
  apiState.detail = createQuestion()
  apiState.answerFailure = false
  apiState.templateId = 2
  apiState.totalCount = 1
  container = document.createElement('div')
  document.body.append(container)
  vi.spyOn(window, 'scrollTo').mockImplementation(() => undefined)
})

afterEach(() => {
  act(() => root?.unmount())
  client?.clear()
  container.remove()
  vi.restoreAllMocks()
})

describe('QuestionsPage workspace flows', () => {
  it('covers workspace tabs, every status filter, platform/store filters, date range, and search', async () => {
    await renderPage()
    expect(lastQuestionRequest().get('kind')).toBe('PRODUCT')
    expect(lastQuestionRequest().get('status')).toBe('WAITING')
    expect(container.querySelector('[data-platform="trendyol"]')).not.toBeNull()
    expect(container.querySelector('[data-platform="hepsiburada"]')).not.toBeNull()
    expect(container.querySelector('.rv-questions-sync-item img')?.getAttribute('src')).toBe('/platforms/trendyol.png')
    expect(container.querySelector('[data-platform="hepsiburada"] .rv-question-platform-logo')?.getAttribute('src')).toBe('/platforms/hepsiburada.png')
    expect(container.querySelector('.rv-questions-notice')).toBeNull()
    expect(button('Yenile').querySelector('.ui-icon-refresh')).not.toBeNull()
    const trendSync = button('Trendyol verilerini çek')
    act(() => trendSync.click())
    await settle()
    expect(JSON.parse(apiState.calls.filter(call => call.path === '/questions/sync').at(-1)?.body ?? '{}')).toEqual({ kind: 'ALL', platformCode: 'TRENDYOL' })
    expect(container.querySelector('.rv-question-thread-message.is-question time')?.textContent).toMatch(/^\d{2}\.\d{2}\.2026 · \d{2}:\d{2}$/)

    for (const [label, expected] of [['Cevap bekleyenler', 'WAITING'], ['Cevaplananlar', 'ANSWERED'], ['Süresi dolanlar', 'EXPIRED'], ['Diğer durumlar', 'OTHER'], ['Tümü', 'ALL']]) {
      act(() => button(label).click())
      await settle()
      expect(lastQuestionRequest().get('status')).toBe(expected)
      expect(button(label).getAttribute('aria-selected')).toBe('true')
    }

    const selects = container.querySelectorAll<HTMLSelectElement>('.rv-questions-filter-grid select')
    expect(container.querySelector('.rv-question-platform-fixed')).toBeNull()
    expect(Array.from(selects[0].options).map(option => option.textContent)).toContain('Trendyol Mağazası')
    expect(Array.from(selects[0].options).map(option => option.textContent)).toContain('Hepsiburada Mağazası')
    select(selects[0], 'ty-store')
    const dates = container.querySelectorAll<HTMLInputElement>('.rv-questions-filter-grid input[type="date"]')
    input(dates[0], '2026-10-01')
    input(dates[1], '2026-10-05')
    input(container.querySelector<HTMLInputElement>('.rv-questions-search input')!, 'bluz 01')
    await settle()
    let params = lastQuestionRequest()
    expect(params.get('connectionId')).toBe('ty-store')
    expect(params.get('dateFrom')).toBe(new Date('2026-10-01T00:00:00').toISOString())
    expect(params.get('dateTo')).toBe(new Date('2026-10-05T23:59:59').toISOString())
    expect(params.get('search')).toBe('bluz 01')

    act(() => button('Sipariş soruları').click())
    await settle()
    params = lastQuestionRequest()
    expect(params.get('kind')).toBe('ORDER')
    expect(params.get('platform')).toBe('HEPSIBURADA')
    expect(container.querySelector('.rv-question-platform-fixed')).toBeNull()
    const orderStore = container.querySelectorAll<HTMLSelectElement>('.rv-questions-filter-grid select')[0]
    expect(Array.from(orderStore.options).map(option => option.textContent)).toContain('Hepsiburada Mağazası')
    expect(Array.from(orderStore.options).map(option => option.textContent)).not.toContain('Trendyol Mağazası')
    expect(container.querySelector('.rv-question-meta a')?.getAttribute('href')).toContain('HB-12345')
    expect(container.querySelector('[role="dialog"]')).toBeNull()
    expect(container.querySelector('.rv-question-card')?.textContent).toContain('Sipariş #HB-12345')
    expect(container.querySelector('.rv-question-card')?.textContent).toContain('Ürün boyu kaç santimetredir?')
    act(() => button('Cevap yaz').click())
    await settle()
    input(container.querySelector<HTMLTextAreaElement>('.rv-question-inline-compose textarea')!, 'x')
    expect(button('Gönder').disabled).toBe(false)
    act(() => button('Cevabı kapat').click())
    await settle()

    act(() => button('Ürün soruları').click())
    await settle()
    expect(lastQuestionRequest().get('kind')).toBe('PRODUCT')
    expect(lastQuestionRequest().get('platform')).toBeNull()
    act(() => button('Hazır cevaplar').click())
    await settle()
    expect(container.querySelector('.rv-question-template-editor')).not.toBeNull()
    expect(container.textContent).toContain('Ölçü bilgisi')
  })

  it('creates, edits, and deletes shared quick replies using mocked API calls', async () => {
    await renderPage()
    act(() => button('Hazır cevaplar').click())
    await settle()
    const fields = container.querySelectorAll<HTMLInputElement | HTMLTextAreaElement>('.rv-question-template-editor input, .rv-question-template-editor textarea')
    input(fields[0] as HTMLInputElement, 'Kargo bilgisi')
    input(fields[1] as HTMLTextAreaElement, 'Kargonuz yola çıktığında takip bağlantısı paylaşılır.')
    act(() => button('Hazır cevabı oluştur').click())
    await settle()
    expect(apiState.calls.some(call => call.path === '/question-templates' && call.method === 'POST')).toBe(true)
    expect(container.textContent).toContain('Kargo bilgisi')

    act(() => container.querySelectorAll<HTMLButtonElement>('.rv-question-template-card button')[2].click())
    await settle()
    const editFields = container.querySelectorAll<HTMLInputElement | HTMLTextAreaElement>('.rv-question-template-editor input, .rv-question-template-editor textarea')
    input(editFields[0] as HTMLInputElement, 'Kargo güncellemesi')
    input(editFields[1] as HTMLTextAreaElement, 'Yeni takip bağlantısını siparişlerimden görebilirsiniz.')
    act(() => button('Değişiklikleri kaydet').click())
    await settle()
    expect(apiState.calls.some(call => call.path.startsWith('/question-templates/template-') && call.method === 'PUT')).toBe(true)
    expect(container.textContent).toContain('Kargo güncellemesi')

    act(() => container.querySelectorAll<HTMLButtonElement>('.rv-question-template-card button')[3].click())
    await settle()
    expect(apiState.calls.some(call => call.path.startsWith('/question-templates/template-') && call.method === 'DELETE')).toBe(true)
    expect(container.textContent).not.toContain('Kargo güncellemesi')
  })

  it('shows conversation history and rejection reasons, inserts a reply bubble, and submits the edited answer', async () => {
    await renderPage()
    expect(container.querySelector('[role="dialog"]')).toBeNull()
    expect(container.querySelector('.rv-question-card')?.textContent).toContain('Ölçüleri kontrol ediyorum.')
    expect(container.querySelector('.rv-question-card')?.textContent).toContain('Ret nedeni: Yanıt eksik bilgi içeriyor.')
    expect(Array.from(container.querySelectorAll('.rv-question-thread-message p')).map(message => message.textContent)).toEqual(['Ürün boyu kaç santimetredir?', 'Ölçüleri kontrol ediyorum.'])

    act(() => button('Cevap yaz').click())
    act(() => button('Ölçü bilgisi').click())
    const editor = container.querySelector<HTMLTextAreaElement>('.rv-question-inline-compose textarea')!
    expect(editor.value).toBe('Ürün ölçülerini ürün sayfasında bulabilirsiniz.')
    expect(editor.maxLength).toBe(2000)
    input(editor, 'kısa')
    expect(button('Gönder').disabled).toBe(true)
    input(editor, 'x'.repeat(2001))
    expect(button('Gönder').disabled).toBe(true)
    input(editor, 'Ürün uzunluğu 65 cm. Ölçü bilgisi ürün sayfasında da yer alıyor.')
    expect(button('Gönder').disabled).toBe(false)
    expect(container.querySelector('.rv-question-inline-compose .rv-question-compose-footer')?.textContent).toContain('10–2000 karakter')
    act(() => button('Gönder').click())
    await settle()
    expect(apiState.calls.some(call => call.path.endsWith('/answer') && call.method === 'POST')).toBe(true)
    expect(container.querySelector('[role="dialog"]')).toBeNull()
    expect(container.querySelector('.rv-question-inline-compose')).toBeNull()
    expect(container.querySelector('.rv-question-actions')?.textContent).toContain('Cevap gönderildi · doğrulanıyor')
    expect(container.querySelector('.rv-question-card')?.textContent).toContain('Ürün uzunluğu 65 cm.')
  })

  it('opens the quick reply directly below its question and sends it with that row version', async () => {
    await renderPage()
    expect(container.querySelector('.rv-question-card .rv-question-content')?.textContent).toContain('Ürün boyu kaç santimetredir?')
    act(() => Array.from(container.querySelectorAll<HTMLButtonElement>('button')).find(item => item.textContent?.trim() === 'Cevap yaz')?.click())
    await settle()
    expect(container.querySelector('.rv-question-inline-compose')).not.toBeNull()
    const editor = container.querySelector<HTMLTextAreaElement>('.rv-question-inline-compose textarea')!
    input(editor, 'Ürün uzunluğu 65 cm olarak ölçülmüştür.')
    act(() => button('Gönder').click())
    await settle()
    const answerRequest = apiState.calls.find(call => call.path === '/questions/q-product/answer')
    expect(answerRequest?.method).toBe('POST')
    expect(JSON.parse(answerRequest?.body ?? '{}')).toEqual({ text: 'Ürün uzunluğu 65 cm olarak ölçülmüştür.', version: 2 })
    expect(container.querySelector('.rv-question-inline-compose')).toBeNull()
  })

  it('shows the question date for answered questions instead of an expired reply deadline', async () => {
    apiState.detail = { ...createQuestion(), expiresAt: '2026-10-04T09:00:00Z' }
    await renderPage()
    act(() => button('Cevaplananlar').click())
    await settle()

    expect(container.querySelector('.rv-question-deadline')).toBeNull()
    expect(container.querySelector('.rv-question-age')?.textContent).toContain('Soru tarihi:')
    expect(container.textContent).not.toContain('Süre doldu kaldı')
  })

  it('tries the connection-aware product image lookup for barcode, SKU, and model code', async () => {
    apiState.detail = { ...createQuestion(), connectionId: 'hb-store', platformCode: 'HEPSIBURADA', productImageUrl: 'https://images.example/broken.jpg', productSku: 'HBCV000073363P', productBarcode: '86900002', productModelCode: 'MZ001Y8' }
    await renderPage()
    const image = container.querySelector<HTMLImageElement>('.rv-question-product img')!
    expect(image.getAttribute('src')).toBe('https://images.example/broken.jpg')
    act(() => image.dispatchEvent(new Event('error')))
    await settle()
    expect(new URL(image.getAttribute('src')!, 'https://panel.ravencia.test').searchParams.get('productName')).toBe('Kadın triko bluz')
    expect(new URL(image.getAttribute('src')!, 'https://panel.ravencia.test').searchParams.get('barcode')).toBe('86900002')
    act(() => image.dispatchEvent(new Event('error')))
    await settle()
    expect(new URL(container.querySelector<HTMLImageElement>('.rv-question-product img')!.getAttribute('src')!, 'https://panel.ravencia.test').searchParams.get('barcode')).toBe('HBCV000073363P')
    act(() => container.querySelector<HTMLImageElement>('.rv-question-product img')!.dispatchEvent(new Event('error')))
    await settle()
    expect(new URL(container.querySelector<HTMLImageElement>('.rv-question-product img')!.getAttribute('src')!, 'https://panel.ravencia.test').searchParams.get('barcode')).toBe('MZ001Y8')
    expect(apiState.calls.some(call => call.path.startsWith('/products?'))).toBe(false)
  })

  it('uses a circular marketplace logo instead of a dotted platform badge', async () => {
    apiState.detail = { ...createQuestion(), storeName: 'Trendyol' }
    await renderPage()

    expect(container.querySelector('.rv-question-platform-icon[aria-label="Trendyol"] img')?.getAttribute('src')).toBe('/platforms/trendyol.png')
    expect(container.querySelector('.rv-question-meta .rv-badge')).toBeNull()
    expect(container.querySelector('.rv-question-meta')?.textContent).not.toContain('TrendyolTrendyol')
  })

  it('renders the platform rejection when answer submission fails and refreshes the active question kind', async () => {
    await renderPage()
    act(() => button('Yenile').click())
    await settle()
    expect(apiState.calls.some(call => call.path === '/questions/sync' && call.method === 'POST' && JSON.parse(call.body ?? '{}').kind === 'PRODUCT')).toBe(true)

    act(() => button('Cevap yaz').click())
    await settle()
    apiState.answerFailure = true
    input(container.querySelector<HTMLTextAreaElement>('.rv-question-inline-compose textarea')!, 'Ürün uzunluğu 65 cm. Ayrıntılar sayfada yazıyor.')
    act(() => button('Gönder').click())
    await settle()
    expect(container.querySelector('[role="alert"]')?.textContent).toBe('Pazaryeri cevabı kabul etmedi.')
  })

  it('moves through every available result page and disables pagination at both ends', async () => {
    apiState.totalCount = 102
    await renderPage()
    expect(container.querySelector('.rv-questions-pagination')?.textContent).toContain('Sayfa 1 / 3')
    act(() => button('Sonraki').click())
    await settle()
    expect(lastQuestionRequest().get('page')).toBe('2')
    expect(container.querySelector('.rv-questions-pagination')?.textContent).toContain('Sayfa 2 / 3')
    act(() => button('Sonraki').click())
    await settle()
    expect(lastQuestionRequest().get('page')).toBe('3')
    expect(button('Sonraki').disabled).toBe(true)
    act(() => button('Önceki').click())
    await settle()
    expect(container.querySelector('.rv-questions-pagination')?.textContent).toContain('Sayfa 2 / 3')
    act(() => button('Önceki').click())
    await settle()
    expect(container.querySelector('.rv-questions-pagination')?.textContent).toContain('Sayfa 1 / 3')
    expect(button('Önceki').disabled).toBe(true)
  })
})
