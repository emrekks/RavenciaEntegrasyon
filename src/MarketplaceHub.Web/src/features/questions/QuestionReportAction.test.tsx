import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { resetAnsweredIdempotencyKeys } from '../../shared/idempotency'

const apiState = vi.hoisted(() => ({ calls: [] as Array<{ path: string; method: string; body?: string; key?: string }>, reportFailure: null as string | null }))

const hepsiburadaRules = {
  platformCode: 'HEPSIBURADA', supported: true, unsupportedReason: null, allowedStatuses: ['WAITING_FOR_ANSWER'], resultStatus: 'REJECTED', noteMinLength: 10, noteMaxLength: 1900,
  reasons: [{ code: 'ADVERTISEMENT', label: 'Reklam', noteRequired: false }, { code: 'OTHER', label: 'Diğer', noteRequired: true }],
}
const trendyolRules = { platformCode: 'TRENDYOL', supported: false, unsupportedReason: 'Trendyol soru bildirmeyi yalnızca Trendyol Satıcı Paneli’nden kabul ediyor.', allowedStatuses: [], resultStatus: null, noteMinLength: 0, noteMaxLength: 0, reasons: [] }

vi.mock('../../shared/api', () => ({
  hubApi: vi.fn(async (path: string, init?: RequestInit) => {
    const method = init?.method ?? 'GET'
    const body = typeof init?.body === 'string' ? init.body : undefined
    const key = (init?.headers as Record<string, string> | undefined)?.['Idempotency-Key']
    apiState.calls.push({ path, method, body, key })
    if (path === '/questions/report-reasons?platform=HEPSIBURADA') return hepsiburadaRules
    if (path === '/questions/report-reasons?platform=TRENDYOL') return trendyolRules
    if (path === '/questions/q-1/report' && method === 'POST') {
      if (apiState.reportFailure) throw Object.assign(new Error(apiState.reportFailure), { status: 409, code: 'QUESTION_ALREADY_CLOSED' })
      return { id: 'q-1', status: 'REJECTED', version: 4 }
    }
    throw new Error(`Test API fixture does not handle ${method} ${path}`)
  }),
}))

import { QuestionReportAction, questionReportBlock, type ReportableQuestion } from './QuestionReportAction'

const waiting = (platformCode = 'HEPSIBURADA'): ReportableQuestion => ({ id: 'q-1', platformCode, status: 'WAITING_FOR_ANSWER', version: 3, externalQuestionId: '5000123', expiresAt: new Date(Date.now() + 3_600_000).toISOString() })

let container: HTMLDivElement
let root: Root
let client: QueryClient
let notices: Array<{ message: string; kind: string }>

async function settle(ms = 15) {
  await act(async () => { await new Promise(resolve => setTimeout(resolve, ms)) })
}

async function render(question: ReportableQuestion) {
  client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  root = createRoot(container)
  await act(async () => { root.render(<QueryClientProvider client={client}><QuestionReportAction question={question} onNotice={notice => notices.push(notice)} /></QueryClientProvider>) })
  await settle()
}

const trigger = () => container.querySelector<HTMLButtonElement>('.qi-report-button')!
const dialog = () => document.querySelector<HTMLElement>('.qi-report-modal')
const confirmButton = () => Array.from(dialog()!.querySelectorAll<HTMLButtonElement>('button')).find(item => item.textContent?.includes('Soruyu Bildir'))!

function choose(label: string) {
  const radio = Array.from(dialog()!.querySelectorAll<HTMLLabelElement>('.qi-report-reasons label')).find(item => item.textContent?.includes(label))!.querySelector('input')!
  act(() => radio.click())
}

function type(element: HTMLTextAreaElement, value: string) {
  act(() => {
    Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value')?.set?.call(element, value)
    element.dispatchEvent(new Event('input', { bubbles: true }))
  })
}

beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  apiState.calls = []
  apiState.reportFailure = null
  notices = []
  resetAnsweredIdempotencyKeys()
  container = document.createElement('div')
  document.body.append(container)
})

afterEach(() => {
  act(() => root?.unmount())
  client?.clear()
  container.remove()
})

describe('QuestionReportAction', () => {
  it('reports a waiting Hepsiburada question with the chosen reason and an Idempotency-Key', async () => {
    await render(waiting())
    expect(trigger().disabled).toBe(false)
    act(() => trigger().click())
    expect(dialog()?.textContent).toContain('cevaplanmadan kapatılır')
    expect(confirmButton().disabled).toBe(true)

    choose('Reklam')
    expect(confirmButton().disabled).toBe(false)
    act(() => confirmButton().click())
    await settle()

    const request = apiState.calls.find(call => call.path === '/questions/q-1/report')!
    expect(request.method).toBe('POST')
    expect(JSON.parse(request.body!)).toEqual({ version: 3, reasonCode: 'ADVERTISEMENT', note: null })
    expect(request.key).toMatch(/^question-report:q-1:v3:/)
    expect(notices).toEqual([{ message: 'Soru Hepsiburada’ya bildirildi; cevaplanmadan kapatılacak.', kind: 'success' }])
    expect(dialog()).toBeNull()
  })

  it('asks for a note when the reason needs one', async () => {
    await render(waiting())
    act(() => trigger().click())
    choose('Diğer')
    expect(confirmButton().disabled).toBe(true)
    const note = dialog()!.querySelector<HTMLTextAreaElement>('textarea')!
    type(note, 'kısa')
    expect(confirmButton().disabled).toBe(true)
    type(note, 'Rakip mağazaya yönlendiriyor')
    expect(confirmButton().disabled).toBe(false)
    act(() => confirmButton().click())
    await settle()

    expect(JSON.parse(apiState.calls.find(call => call.path === '/questions/q-1/report')!.body!)).toEqual({ version: 3, reasonCode: 'OTHER', note: 'Rakip mağazaya yönlendiriyor' })
  })

  it('shows the marketplace refusal as an error notice', async () => {
    apiState.reportFailure = 'Soru pazaryerinde artık cevap beklemiyor. Güncel durum alındı.'
    await render(waiting())
    act(() => trigger().click())
    choose('Reklam')
    act(() => confirmButton().click())
    await settle()

    expect(notices).toEqual([{ message: 'Soru pazaryerinde artık cevap beklemiyor. Güncel durum alındı.', kind: 'error' }])
  })

  it('is disabled with the reason where the marketplace takes no report', async () => {
    await render(waiting('TRENDYOL'))
    expect(trigger().disabled).toBe(true)
    expect(container.querySelector('.qi-report-trigger')?.getAttribute('title')).toContain('Satıcı Paneli')
    expect(apiState.calls.some(call => call.method === 'POST')).toBe(false)
  })

  it('follows the state rules', () => {
    expect(questionReportBlock({ ...waiting(), status: 'ANSWERED' }, hepsiburadaRules)).toBe('Yalnızca cevap bekleyen sorular bildirilebilir.')
    expect(questionReportBlock({ ...waiting(), expiresAt: new Date(Date.now() - 1000).toISOString() }, hepsiburadaRules)).toBe('Cevap süresi dolan soru bildirilemez.')
    expect(questionReportBlock({ ...waiting(), answerSubmissionStatus: 'UNKNOWN' }, hepsiburadaRules)).toContain('işleniyor')
    expect(questionReportBlock(waiting(), undefined)).toContain('yükleniyor')
    expect(questionReportBlock(waiting(), hepsiburadaRules)).toBeNull()
  })
})
