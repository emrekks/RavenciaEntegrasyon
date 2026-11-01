import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ToastStackProvider } from '../../shared/components/ToastStack'
import { OPERATION_FEEDBACK_TOAST_DURATION_MS, useOperationFeedbackToasts } from './operation-feedback-toast'

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
