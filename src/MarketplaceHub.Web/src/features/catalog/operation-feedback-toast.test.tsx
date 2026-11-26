import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ToastStackProvider } from '../../shared/components/ToastStack'
import { recordsJobOutcome } from '../../shared/job-outcome-notice'
import { readNotificationHistory } from '../../shared/notifications'
import { OPERATION_FEEDBACK_TOAST_DURATION_MS, recordOperationFeedback, useOperationFeedbackToasts } from './operation-feedback-toast'

;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true

let root: Root | undefined
let host: HTMLDivElement | undefined

afterEach(() => {
  if (root) act(() => root?.unmount())
  host?.remove()
  root = undefined
  host = undefined
  vi.useRealTimers()
})

describe('operation feedback in the toast column', () => {
  let feedback: ReturnType<typeof useOperationFeedbackToasts> | undefined
  function Editor() {
    feedback = useOperationFeedbackToasts()
    return null
  }
  const shown = () => [...(host?.querySelectorAll('.app-toast-stack > .app-toast') ?? [])].map(item => item.querySelector('strong')?.textContent)

  it('stacks feedback below earlier toasts, turns the running save into its result in place and takes back its errors', () => {
    vi.useFakeTimers()
    host = document.createElement('div')
    document.body.append(host)
    root = createRoot(host)
    act(() => root?.render(<ToastStackProvider><Editor /></ToastStackProvider>))

    act(() => feedback!.show({ title: 'Görsel Seçildi', message: '2 görsel seçildi.', kind: 'info' }))
    act(() => feedback!.show({ title: 'Ürün Kaydediliyor', message: 'Kaydediliyor…', kind: 'info', persistent: true, key: 'save' }))
    act(() => feedback!.show({ title: 'Barkod Gerekli', message: 'Önce barkod girin.', kind: 'error', persistent: true }))
    expect(shown()).toEqual(['Görsel Seçildi', 'Ürün Kaydediliyor', 'Barkod Gerekli'])
    expect(host.querySelector('.app-toast-pending')?.getAttribute('aria-busy')).toBe('true')

    act(() => feedback!.show({ title: 'Ürün Kaydedildi', message: 'Ürün güncellendi.', kind: 'success', key: 'save' }))
    expect(shown()).toEqual(['Görsel Seçildi', 'Ürün Kaydedildi', 'Barkod Gerekli'])

    act(() => feedback!.dismissErrors())
    expect(shown()).toEqual(['Görsel Seçildi', 'Ürün Kaydedildi'])
    act(() => { vi.advanceTimersByTime(OPERATION_FEEDBACK_TOAST_DURATION_MS) })
    expect(shown()).toEqual([])
  })

  it('takes a running toast away when its screen closes, but leaves finished results on screen', () => {
    host = document.createElement('div')
    document.body.append(host)
    root = createRoot(host)
    act(() => root?.render(<ToastStackProvider><Editor /></ToastStackProvider>))
    act(() => feedback!.show({ title: 'Ürün Kaydedildi', message: 'Tamam.', kind: 'success' }))
    act(() => feedback!.show({ title: 'Ürün Oluşturuluyor', message: 'Oluşturuluyor…', kind: 'info', persistent: true, key: 'save' }))
    act(() => root?.render(<ToastStackProvider>{null}</ToastStackProvider>))
    expect(shown()).toEqual(['Ürün Kaydedildi'])
  })
})

// D-8: the product editor's and Toplu Yayın's warnings reach the notification history like the job outcome notices; a
// running note stays a toast.
describe('operation feedback in the notification history', () => {
  afterEach(() => localStorage.clear())

  it('keeps a finished warning and a followed final result, not a running or interim note', () => {
    recordOperationFeedback({ title: 'Yayın Uyarısı', message: 'Pazarama: 2 kart atlandı, fiyatı yok.', kind: 'warning' })
    recordOperationFeedback({ title: 'Yayın Sürüyor', message: 'Kartlar hazırlanıyor.', kind: 'warning', persistent: true })
    recordOperationFeedback({ title: 'Aktarım Sürüyor', message: 'Örnek ürün bekliyor.', kind: 'warning', record: false })
    recordOperationFeedback({ title: "Trendyol'a Kısmen Aktarıldı", message: '2 ürün aktarıldı, 1 ürün reddedildi.', kind: 'warning', record: recordsJobOutcome('warning', false) })
    recordOperationFeedback({ title: 'Aktarıldı', message: 'Örnek ürün aktarıldı.', kind: 'success', record: recordsJobOutcome('success', false) })
    expect(readNotificationHistory().map(item => item.title).sort()).toEqual(['Aktarıldı', 'Trendyol\'a Kısmen Aktarıldı', 'Yayın Uyarısı'])
    expect(recordsJobOutcome('warning', true)).toBeUndefined()
    expect(recordsJobOutcome('info', false)).toBeUndefined()
  })
})
