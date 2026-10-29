import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// eslint-disable-next-line @typescript-eslint/no-explicit-any -- the question fixture is reshaped freely per test
const apiState = vi.hoisted(() => ({ customerHistory: [] as unknown[], calls: [] as Array<{ path: string; method: string; body?: string }>, templates: [] as Array<{ id: string; title: string; text: string; version: number; updatedAt: string }>, detail: null as any, answerFailure: false, staleOnce: false, templateId: 1, totalCount: 1, freshSync: false, emptyList: false }))

vi.mock('../../shared/api', () => ({
  loadAllPages: vi.fn(async () => ({ items: [
    { id: 'ty-store', platformCode: 'TRENDYOL', displayName: 'Trendyol Mağazası', status: 'ACTIVE' },
    { id: 'hb-store', platformCode: 'HEPSIBURADA', displayName: 'Hepsiburada Mağazası', status: 'ACTIVE' },
  ], nextCursor: null, hasMore: false })),
  hubApi: vi.fn(async (path: string, init?: RequestInit) => {
    const method = init?.method ?? 'GET'
    const body = typeof init?.body === 'string' ? init.body : undefined
    apiState.calls.push({ path, method, body })
    if (path === '/questions/sync-state' && apiState.freshSync) return [
      { connectionId: 'ty-store', platformCode: 'TRENDYOL', storeName: 'Trendyol Mağazası', historyImported: true, importedCount: 25, progressStatus: 'IDLE', lastSuccessAt: new Date().toISOString() },
    ]
    if (path === '/questions/sync-state') return [
      { connectionId: 'ty-store', platformCode: 'TRENDYOL', storeName: 'Trendyol Mağazası', historyImported: true, importedCount: 25, progressStatus: 'COMPLETED', lastSuccessAt: '2026-10-05T10:00:00Z' },
      { connectionId: 'hb-store', platformCode: 'HEPSIBURADA', storeName: 'Hepsiburada Mağazası', historyImported: false, importedCount: 3, progressStatus: 'INITIAL', lastSuccessAt: null },
    ]
    if (path.startsWith('/products?')) return { items: [{ title: 'Kadın triko bluz', primaryImageUrl: '/api/v1/files/product-media/asset-1/content', familyMediaUrls: [], variants: [{ sku: 'BLUZ-01', barcode: '86900001', modelCode: 'MZ001' }] }], nextCursor: null, hasMore: false }
    if (path === '/questions/sync') return { jobs: ['sync-1'], queuedConnections: 2 }
    if (path.endsWith('/customer-history')) return apiState.customerHistory
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
      if (apiState.staleOnce) { apiState.staleOnce = false; apiState.detail = { ...apiState.detail, version: apiState.detail.version + 3 }; throw Object.assign(new Error('Soru yenilendi. Güncel kaydı açıp tekrar deneyin.'), { code: 'QUESTION_STALE', status: 412 }) }
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
      const rowStatus = status === 'WAITING' && ['ANSWER_SUBMITTED', 'REJECTED'].includes(row.status) ? row.status : statusByFilter[status ?? 'WAITING']
      if (apiState.emptyList && params.get('limit') === '50') return { items: [], page: 1, limit: 50, totalCount: 0 }
      return { items: [{ ...row, status: rowStatus }], page: Number(params.get('page') ?? 1), limit: Number(params.get('limit') ?? 50), totalCount: apiState.totalCount }
    }
    throw new Error(`Test API fixture does not handle ${method} ${path}`)
  }),
}))

import { formatShortDate, QuestionsPage, questionSyncIsStale, syncSummary } from './QuestionsPage'

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

async function settle(ms = 15) {
  await act(async () => { await new Promise(resolve => setTimeout(resolve, ms)) })
}

function lastQuestionRequest() {
  const request = [...apiState.calls].reverse().find(item => item.path.startsWith('/questions?') && item.path.includes('limit=50'))
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
  apiState.staleOnce = false
  apiState.templateId = 2
  apiState.totalCount = 1
  apiState.freshSync = false
  apiState.emptyList = false
  apiState.customerHistory = []
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

function chooseStatus(label: string) {
  act(() => container.querySelector<HTMLButtonElement>('.qi-status-btn')!.click())
  const option = Array.from(container.querySelectorAll<HTMLButtonElement>('.qi-status-menu [role="option"]')).find(item => item.textContent?.includes(label))
  if (!option) throw new Error(`Could not find status option “${label}”`)
  act(() => option.click())
}

function composer() {
  const found = container.querySelector<HTMLTextAreaElement>('.qi-composer textarea')
  if (!found) throw new Error('Could not find the answer box')
  return found
}

describe('QuestionsPage workspace flows', () => {
  it('shows the customer\'s earlier questions and their answers above the new question, oldest first', async () => {
    apiState.customerHistory = [
      { ...createQuestion(), id: 'q-before', status: 'ANSWERED', productName: 'Viskon elbise', questionText: 'Elbisenin boyu kaç?', createdAt: '2026-10-03T09:00:00Z', conversations: [{ author: 'customer', text: 'Elbisenin boyu kaç?', createdAt: '2026-10-03T09:00:00Z' }, { author: 'merchant', text: 'Boyu 120 cm.', createdAt: '2026-10-03T10:00:00Z' }] },
      { ...createQuestion(), id: 'q-first', status: 'EXPIRED', productName: 'Penye tunik', questionText: 'Tunik astarlı mı?', createdAt: '2026-09-20T09:00:00Z', conversations: [] },
    ]
    await renderPage()

    expect(apiState.calls.some(call => call.path === '/questions/q-product/customer-history')).toBe(true)
    const history = container.querySelector('.qi-customer-history')
    expect(history?.textContent).toContain('Müşterinin Önceki Sorular')
    const items = Array.from(history?.querySelectorAll('.qi-history-item') ?? []).map(item => item.textContent ?? '')
    expect(items).toHaveLength(2)
    expect(items[0]).toContain('Penye tunik')
    expect(items[0]).toContain('Süresi Doldu')
    expect(items[1]).toContain('Elbisenin boyu kaç?')
    expect(items[1]).toContain('Boyu 120 cm.')
    // The new question follows them.
    expect(history?.nextElementSibling?.textContent).toContain('Ürün boyu kaç santimetredir?')
  })

  it('shows no earlier block when the customer asked nothing before', async () => {
    await renderPage()
    expect(container.querySelector('.qi-customer-history')).toBeNull()
  })

  it('renders a duplicate marketplace seller reply once without sending another answer', async () => {
    apiState.detail = {
      ...createQuestion(),
      conversations: [
        { author: 'customer', text: 'Ürün boyu kaç santimetredir?', createdAt: '2026-10-05T09:00:00Z' },
        { author: 'merchant', text: 'Kolları düz kol, ürün yeni bir üründür.', createdAt: '2026-10-05T09:55:08Z' },
        { author: 'seller', text: 'Kolları düz kol, ürün yeni bir üründür.', createdAt: '2026-10-05T09:55:55Z' },
      ],
    }

    await renderPage()

    expect(container.querySelectorAll('.rv-question-conversation .rv-question-thread-message.is-seller')).toHaveLength(1)
    expect(apiState.calls.some(call => call.path.endsWith('/answer'))).toBe(false)
  })

  it('covers workspace tabs, every status filter, store filter, date range, and search', async () => {
    await renderPage()
    expect(lastQuestionRequest().get('kind')).toBe('PRODUCT')
    expect(lastQuestionRequest().get('status')).toBe('WAITING')
    expect(container.querySelector('[data-platform="trendyol"]')).not.toBeNull()
    expect(container.querySelector('[data-platform="hepsiburada"]')).not.toBeNull()
    expect(container.querySelector('.rv-questions-sync-item img')?.getAttribute('src')).toBe('/platforms/trendyol.webp')
    expect(container.querySelector('[data-platform="hepsiburada"] .rv-question-platform-logo')?.getAttribute('src')).toBe('/platforms/hepsiburada.webp')
    expect(container.querySelector('[data-platform="trendyol"]')?.textContent).toContain('Geçmiş tamamlandı')
    expect(button('Yenile').querySelector('.ui-icon-refresh')).not.toBeNull()
    expect(apiState.calls.some(call => call.path === '/questions/sync' && JSON.parse(call.body ?? '{}').kind === 'ALL')).toBe(true)
    expect(container.querySelector('.rv-question-thread-message.is-question time')?.textContent).toMatch(/^\d{2}\.\d{2}\.2026 · \d{2}:\d{2}$/)

    for (const [label, expected] of [['Cevap Bekleyenler', 'WAITING'], ['Cevaplananlar', 'ANSWERED'], ['Süresi Dolanlar', 'EXPIRED'], ['Diğer Durumlar', 'OTHER'], ['Tümü', 'ALL']]) {
      chooseStatus(label)
      await settle()
      expect(lastQuestionRequest().get('status')).toBe(expected)
      expect(container.querySelector('.qi-status-btn')?.textContent).toContain(label)
      expect(container.querySelector('.qi-status-menu')).toBeNull()
    }

    act(() => button('Filtre').click())
    const selects = container.querySelectorAll<HTMLSelectElement>('.rv-questions-filter-grid select')
    expect(Array.from(selects[0].options).map(option => option.textContent)).toContain('Trendyol Mağazası')
    expect(Array.from(selects[0].options).map(option => option.textContent)).toContain('Hepsiburada Mağazası')
    select(selects[0], 'ty-store')
    act(() => button('Tarih Aralığı Seçin').click())
    const calendarDays = Array.from(document.body.querySelectorAll<HTMLButtonElement>('.date-range-picker-grid button'))
    act(() => calendarDays.find(day => day.getAttribute('aria-label') === '1 Ekim 2026')!.click())
    const endDate = Array.from(document.body.querySelectorAll<HTMLButtonElement>('.date-range-picker-grid button')).find(day => day.getAttribute('aria-label') === '5 Ekim 2026')!
    act(() => endDate.click())
    input(container.querySelector<HTMLInputElement>('.rv-questions-search input')!, 'bluz 01')
    // The search waits until typing stops.
    expect(apiState.calls.some(call => call.path.includes('search=bluz'))).toBe(false)
    await settle(350)
    await settle()
    let params = lastQuestionRequest()
    expect(params.get('connectionId')).toBe('ty-store')
    expect(params.get('dateFrom')).toBe('2026-10-01T00:00:00.000+03:00')
    expect(params.get('dateTo')).toBe('2026-10-05T23:59:59.999+03:00')
    expect(params.get('search')).toBe('bluz 01')
    expect(container.querySelector('.qi-filter-btn')?.textContent).toContain('2')

    act(() => button('Sipariş Soruları').click())
    await settle()
    params = lastQuestionRequest()
    expect(params.get('kind')).toBe('ORDER')
    expect(params.get('platform')).toBeNull()
    const orderStore = container.querySelectorAll<HTMLSelectElement>('.rv-questions-filter-grid select')[0]
    expect(Array.from(orderStore.options).map(option => option.textContent)).toContain('Hepsiburada Mağazası')
    expect(Array.from(orderStore.options).map(option => option.textContent)).not.toContain('Trendyol Mağazası')
    act(() => button('Uygula').click())
    expect(container.querySelector('[role="dialog"]')).toBeNull()
    expect(container.querySelector('.rv-question-meta a')?.getAttribute('href')).toContain('HB-12345')
    expect(container.querySelector('.rv-question-card')?.textContent).toContain('Sipariş #HB-12345')
    expect(container.querySelector('.rv-question-card')?.textContent).toContain('Ürün boyu kaç santimetredir?')
    input(composer(), 'x')
    expect(button('Gönder').disabled).toBe(false)

    act(() => button('Ürün Soruları').click())
    await settle()
    expect(lastQuestionRequest().get('kind')).toBe('PRODUCT')
    expect(lastQuestionRequest().get('platform')).toBeNull()
    act(() => button('Hazır Cevaplar').click())
    await settle()
    expect(container.querySelector('.rv-question-template-editor')).not.toBeNull()
    expect(container.querySelector('.qi-templates-modal')?.textContent).toContain('Ölçü bilgisi')
  })

  it('creates, edits, and deletes shared quick replies using mocked API calls', async () => {
    await renderPage()
    act(() => button('Hazır Cevaplar').click())
    await settle()
    const fields = container.querySelectorAll<HTMLInputElement | HTMLTextAreaElement>('.rv-question-template-editor input, .rv-question-template-editor textarea')
    input(fields[0] as HTMLInputElement, 'Kargo bilgisi')
    input(fields[1] as HTMLTextAreaElement, 'Kargonuz yola çıktığında takip bağlantısı paylaşılır.')
    act(() => button('Hazır Cevabı Oluştur').click())
    await settle()
    expect(apiState.calls.some(call => call.path === '/question-templates' && call.method === 'POST')).toBe(true)
    expect(container.textContent).toContain('Kargo bilgisi')

    act(() => container.querySelectorAll<HTMLButtonElement>('.rv-question-template-card button')[2].click())
    await settle()
    const editFields = container.querySelectorAll<HTMLInputElement | HTMLTextAreaElement>('.rv-question-template-editor input, .rv-question-template-editor textarea')
    input(editFields[0] as HTMLInputElement, 'Kargo güncellemesi')
    input(editFields[1] as HTMLTextAreaElement, 'Yeni takip bağlantısını siparişlerimden görebilirsiniz.')
    act(() => button('Değişiklikleri Kaydet').click())
    await settle()
    expect(apiState.calls.some(call => call.path.startsWith('/question-templates/template-') && call.method === 'PUT')).toBe(true)
    expect(container.textContent).toContain('Kargo güncellemesi')

    act(() => container.querySelectorAll<HTMLButtonElement>('.rv-question-template-card button')[3].click())
    await settle()
    // One click only asks; nothing is deleted until the dialog is confirmed.
    expect(apiState.calls.some(call => call.method === 'DELETE')).toBe(false)
    expect(document.querySelector('.qi-template-delete-modal')?.textContent).toContain('Kargo güncellemesi')
    act(() => Array.from(document.querySelectorAll<HTMLButtonElement>('.qi-template-delete-modal button')).find(item => item.textContent?.includes('Hazır Cevabı Sil'))!.click())
    await settle()
    expect(apiState.calls.some(call => call.path.startsWith('/question-templates/template-') && call.method === 'DELETE')).toBe(true)
    expect(container.textContent).not.toContain('Kargo güncellemesi')
  })

  it('shows conversation history and rejection reasons, inserts a quick reply, and submits the edited answer', async () => {
    await renderPage()
    const detail = () => container.querySelector('.qi-detail')
    expect(detail()?.textContent).toContain('Ölçüleri kontrol ediyorum.')
    expect(detail()?.textContent).toContain('Ret nedeni: Yanıt eksik bilgi içeriyor.')
    expect(Array.from(container.querySelectorAll('.rv-question-thread-message p')).map(message => message.textContent)).toEqual(['Ürün boyu kaç santimetredir?', 'Ölçüleri kontrol ediyorum.'])

    act(() => button('Ölçü bilgisi').click())
    const editor = composer()
    expect(editor.value).toBe('Ürün ölçülerini ürün sayfasında bulabilirsiniz.')
    expect(editor.maxLength).toBe(2000)
    input(editor, 'kısa')
    expect(button('Gönder').disabled).toBe(true)
    input(editor, 'x'.repeat(2001))
    expect(button('Gönder').disabled).toBe(true)
    input(editor, 'Ürün uzunluğu 65 cm. Ölçü bilgisi ürün sayfasında da yer alıyor.')
    expect(button('Gönder').disabled).toBe(false)
    expect(container.querySelector('.rv-answer-bar-meta')?.textContent).toContain('10–2000 karakter')
    act(() => button('Gönder').click())
    await settle()
    expect(apiState.calls.some(call => call.path.endsWith('/answer') && call.method === 'POST')).toBe(true)
    expect(detail()?.textContent).toContain('Cevap Gönderildi · Doğrulanıyor')
    expect(detail()?.textContent).toContain('Ürün uzunluğu 65 cm.')
    expect(container.querySelector('.qi-composer')).toBeNull()
  })

  it('answers the selected question from the inline box with that row version', async () => {
    await renderPage()
    expect(container.querySelector('.rv-question-card')?.textContent).toContain('Ürün boyu kaç santimetredir?')
    expect(container.querySelector('.qi-detail .rv-question-content')?.textContent).toContain('Ürün boyu kaç santimetredir?')
    input(composer(), 'Ürün uzunluğu 65 cm olarak ölçülmüştür.')
    act(() => button('Gönder').click())
    await settle()
    const answerRequest = apiState.calls.find(call => call.path === '/questions/q-product/answer')
    expect(answerRequest?.method).toBe('POST')
    expect(JSON.parse(answerRequest?.body ?? '{}')).toEqual({ text: 'Ürün uzunluğu 65 cm olarak ölçülmüştür.', version: 2 })
  })

  it('asks in the panel dialog, not the browser, before an N11 answer that cannot be changed', async () => {
    apiState.detail = { ...createQuestion(), platformCode: 'N11' }
    const browserConfirm = vi.spyOn(window, 'confirm')
    await renderPage()
    input(composer(), 'Ürün uzunluğu 65 cm olarak ölçülmüştür.')
    act(() => button('Gönder').click())
    await settle()
    expect(browserConfirm).not.toHaveBeenCalled()
    expect(apiState.calls.some(call => call.path === '/questions/q-product/answer')).toBe(false)
    expect(document.querySelector('.qi-confirm-modal')?.textContent).toContain('Cevap Bir Kez Gönderilir')
    act(() => button('Cevabı Gönder').click())
    await settle()
    expect(apiState.calls.find(call => call.path === '/questions/q-product/answer')?.method).toBe('POST')
    browserConfirm.mockRestore()
  })

  it('shows the question date for answered questions instead of an expired reply deadline', async () => {
    apiState.detail = { ...createQuestion(), expiresAt: '2026-10-04T09:00:00Z' }
    await renderPage()
    chooseStatus('Cevaplananlar')
    await settle()

    expect(container.querySelector('.rv-question-deadline')).toBeNull()
    expect(container.querySelector('.qi-facts')?.textContent).toContain('Soru Tarihi')
    expect(container.querySelector('.rv-question-age')?.textContent).toMatch(/2026/)
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

  it('links a product question to the product page the marketplace reported', async () => {
    apiState.detail = { ...createQuestion(), productUrl: 'https://www.trendyol.com/ravencia/kadin-bluz-p-123456' }
    await renderPage()
    expect(container.querySelector<HTMLAnchorElement>('.rv-question-product-link')?.getAttribute('href')).toBe('https://www.trendyol.com/ravencia/kadin-bluz-p-123456')
  })

  it('links a product question to its marketplace product search', async () => {
    await renderPage()

    const link = container.querySelector<HTMLAnchorElement>('.rv-question-product-link')
    expect(link?.textContent).toBe('Kadın triko bluz')
    expect(link?.getAttribute('href')).toBe('https://www.trendyol.com/sr?q=86900001')
    expect(link?.getAttribute('target')).toBe('_blank')
    expect(link?.getAttribute('rel')).toBe('noopener noreferrer')
  })

  it('shows the marketplace logo with the store and the status and deadline in the fact strip', async () => {
    apiState.detail = { ...createQuestion(), storeName: 'Trendyol' }
    await renderPage()

    const context = container.querySelector('.rv-question-platform-context')!
    expect(context.querySelector('img')?.getAttribute('src')).toBe('/platforms/trendyol.webp')
    expect(context.textContent).toContain('Trendyol')
    expect(container.querySelector('.qi-facts .qi-tag')?.textContent).toBe('Cevap Bekliyor')
    expect(container.querySelector('.qi-facts')?.textContent).toContain('Kalan Süre')
    expect(container.querySelector('.qi-item-logo img')?.getAttribute('src')).toBe('/platforms/trendyol.webp')
  })

  it('renders the platform rejection when answer submission fails and refreshes the active question kind', async () => {
    await renderPage()
    act(() => button('Yenile').click())
    await settle()
    expect(apiState.calls.some(call => call.path === '/questions/sync' && call.method === 'POST' && JSON.parse(call.body ?? '{}').kind === 'PRODUCT')).toBe(true)

    apiState.answerFailure = true
    input(composer(), 'Ürün uzunluğu 65 cm. Ayrıntılar sayfada yazıyor.')
    act(() => button('Gönder').click())
    await settle()
    expect(container.querySelector('.rv-question-send-error')?.textContent).toBe('Pazaryeri cevabı kabul etmedi.')
    // The marketplace's own words do not decide the tone: a failure is a red failure toast, not a green success.
    const toast = container.querySelector('.app-toast')
    expect(toast?.classList.contains('app-toast-error')).toBe(true)
    expect(toast?.textContent).toContain('İşlem Başarısız')
  })

  it('keeps a reported Hepsiburada question and an expired rejection closed', async () => {
    apiState.detail = { ...createQuestion(), platformCode: 'HEPSIBURADA', status: 'REJECTED', expiresAt: new Date(Date.now() + 86_400_000).toISOString() }
    await renderPage()

    expect(container.querySelector('.qi-composer')).toBeNull()
    expect(container.querySelector('.qi-closed')?.textContent).toContain('Pazaryerince kapatıldı')
  })

  it('lets a rejected answer be written again and shows why it was rejected', async () => {
    // A Trendyol rejection may be rewritten until the question expires.
    apiState.detail = { ...createQuestion(), status: 'REJECTED', expiresAt: new Date(Date.now() + 86_400_000).toISOString() }
    await renderPage()

    expect(container.querySelector('.qi-composer')).not.toBeNull()
    expect(container.querySelector('.qi-composer .rv-question-rejection')?.textContent).toContain('Yanıt eksik bilgi içeriyor.')
    input(composer(), 'Ürün uzunluğu 65 cm. Ayrıntılar ürün sayfasında.')
    act(() => button('Gönder').click())
    await settle()
    expect(apiState.calls.some(call => call.path === '/questions/q-product/answer' && call.method === 'POST')).toBe(true)
  })

  it('puts an answer that never reached the marketplace back as the draft', async () => {
    apiState.detail = { ...createQuestion(), answerSubmissionStatus: 'NOT_DELIVERED', pendingAnswerText: 'Ürün uzunluğu 65 cm olarak ölçülmüştür.' }
    await renderPage()

    expect(composer().value).toBe('Ürün uzunluğu 65 cm olarak ölçülmüştür.')
    expect(container.querySelector('.qi-composer')?.textContent).toContain('pazaryerine ulaşmadı')
  })

  it('says a failing store sync failed even after its history was imported', () => {
    const failed = { connectionId: 'ty-store', platformCode: 'TRENDYOL', storeName: 'Trendyol', historyImported: true, importedCount: 3, progressStatus: 'FAILED' }
    expect(syncSummary(failed, 'PRODUCT').state).toBe('Son aktarım başarısız')
    expect(syncSummary({ ...failed, progressStatus: 'IDLE' }, 'PRODUCT').state).toBe('Geçmiş tamamlandı')
    expect(questionSyncIsStale([{ progressStatus: 'IDLE', lastSuccessAt: new Date(Date.now() - 20 * 60_000).toISOString() }], Date.now())).toBe(true)
    expect(questionSyncIsStale([{ progressStatus: 'IDLE', lastSuccessAt: new Date().toISOString() }], Date.now())).toBe(false)
  })

  it('does not start a sync on every visit when the stores were read recently', async () => {
    apiState.freshSync = true
    await renderPage()

    expect(apiState.calls.some(call => call.path === '/questions/sync')).toBe(false)
  })

  it('keeps the open question when it leaves the current list', async () => {
    await renderPage()
    expect(container.querySelector('.qi-detail')?.textContent).toContain('Ürün boyu kaç santimetredir?')
    apiState.emptyList = true
    await act(async () => { await client.invalidateQueries({ queryKey: ['questions'] }) })
    // The emptied list arrives first, the question's own read right after it.
    for (let attempt = 0; attempt < 20 && !apiState.calls.some(call => call.path === '/questions/q-product'); attempt++) await settle(25)
    await settle(50)

    expect(container.querySelector('.qi-detail')?.textContent).toContain('Ürün boyu kaç santimetredir?')
    expect(apiState.calls.some(call => call.path === '/questions/q-product')).toBe(true)
  })

  it('sends the answer again with the current version when the question was refreshed in the background', async () => {
    await renderPage()
    apiState.staleOnce = true
    const current = apiState.detail.version + 3
    input(composer(), 'Ürün uzunluğu 65 cm. Ayrıntılar sayfada yazıyor.')
    act(() => button('Gönder').click())
    await settle()
    const answers = apiState.calls.filter(call => call.path.endsWith('/answer'))
    expect(answers).toHaveLength(2)
    expect(JSON.parse(answers[1].body ?? '{}').version).toBe(current)
    expect(container.querySelector('[role="alert"]')).toBeNull()
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

// Audit 29 Oct 2026, W-5: the sync strip's time is Istanbul's even when the browser's clock is on another zone.
describe('QuestionsPage sync strip time', () => {
  // A browser on UTC: its local clock reads are the UTC ones.
  const spies: Array<{ mockRestore: () => void }> = []
  beforeEach(() => {
    for (const [local, utc] of [['getFullYear', 'getUTCFullYear'], ['getMonth', 'getUTCMonth'], ['getDate', 'getUTCDate'], ['getHours', 'getUTCHours'], ['getMinutes', 'getUTCMinutes']] as const)
      spies.push(vi.spyOn(Date.prototype, local).mockImplementation(function (this: Date) { return this[utc]() }))
  })
  afterEach(() => { spies.splice(0).forEach(spy => spy.mockRestore()) })

  it('writes the last sync in Istanbul time, not the browser\'s', () => {
    expect(formatShortDate('2026-10-29T11:05:00Z', new Date('2026-10-29T12:00:00Z'))).toBe('29.10 · 14:05')
    // 31 December 22:30 UTC is already 1 January in Istanbul: a date of this year, shown without the year.
    expect(formatShortDate('2026-12-31T22:30:00Z', new Date('2027-01-02T09:00:00Z'))).toBe('01.01 · 01:30')
  })
})
