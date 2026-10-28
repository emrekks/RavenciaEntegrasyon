import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ToastStackProvider } from '../../shared/components/ToastStack'
import { OPERATION_FEEDBACK_TOAST_DURATION_MS, OperationFeedbackToast, useOperationFeedbackToasts, type OperationFeedback } from './operation-feedback-toast'

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

describe('operation feedback toast lifecycle', () => {
  it('stays visible while the operation is pending and starts its dismissal countdown at completion', () => {
    vi.useFakeTimers()
    const onClose = vi.fn()
    host = document.createElement('div')
    document.body.append(host)
    root = createRoot(host)

    const pending: OperationFeedback = { message: 'Ürün değişiklikleri kaydediliyor…', kind: 'info', persistent: true }
    act(() => root?.render(<OperationFeedbackToast feedback={pending} onClose={onClose} />))

    expect(host.querySelector('.app-toast-pending')?.getAttribute('aria-busy')).toBe('true')
    act(() => { vi.advanceTimersByTime(OPERATION_FEEDBACK_TOAST_DURATION_MS * 3) })
    expect(onClose).not.toHaveBeenCalled()
    expect(host.querySelector('.app-toast-pending')).not.toBeNull()

    const completed: OperationFeedback = { message: 'Ürün güncellendi.', kind: 'success' }
    act(() => root?.render(<OperationFeedbackToast feedback={completed} onClose={onClose} />))
    expect(host.querySelector('.app-toast-pending')).toBeNull()

    act(() => { vi.advanceTimersByTime(OPERATION_FEEDBACK_TOAST_DURATION_MS - 1) })
    expect(onClose).not.toHaveBeenCalled()
    act(() => { vi.advanceTimersByTime(1) })
    expect(onClose).toHaveBeenCalledTimes(1)
  })

  it('keeps a refused publication on screen as an error until it is closed', () => {
    vi.useFakeTimers()
    const onClose = vi.fn()
    host = document.createElement('div')
    document.body.append(host)
    root = createRoot(host)

    const refused: OperationFeedback = { message: 'Ürün güncellendi. Uyarı: Shopify için yayın işi açılamadı.', kind: 'error', persistent: true }
    act(() => root?.render(<OperationFeedbackToast feedback={refused} onClose={onClose} />))

    expect(host.querySelector('.app-toast-pending')).toBeNull()
    act(() => { vi.advanceTimersByTime(OPERATION_FEEDBACK_TOAST_DURATION_MS * 3) })
    expect(onClose).not.toHaveBeenCalled()
    expect(host.textContent).toContain('Shopify için yayın işi açılamadı.')
  })

  it('shows a structured result with its own title, one list item per line and its own reading time', () => {
    vi.useFakeTimers()
    const onClose = vi.fn()
    host = document.createElement('div')
    document.body.append(host)
    root = createRoot(host)

    const started: OperationFeedback = {
      title: 'Yayın Başlatıldı',
      message: 'Ürün bilgileri kaydedildi. Trendyol: ürün (3 varyant) gönderim kuyruğuna alındı.',
      lines: ['Ürün bilgileri kaydedildi.', 'Trendyol: ürün (3 varyant) gönderim kuyruğuna alındı.'],
      kind: 'success',
      durationMs: 6000
    }
    act(() => root?.render(<OperationFeedbackToast feedback={started} onClose={onClose} />))

    expect(host.querySelector('.app-toast-success strong')?.textContent).toBe('Yayın Başlatıldı')
    expect([...host.querySelectorAll('.app-toast-lines li')].map(item => item.textContent)).toEqual(started.lines)
    act(() => { vi.advanceTimersByTime(5999) })
    expect(onClose).not.toHaveBeenCalled()
    act(() => { vi.advanceTimersByTime(1) })
    expect(onClose).toHaveBeenCalledTimes(1)
  })
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
