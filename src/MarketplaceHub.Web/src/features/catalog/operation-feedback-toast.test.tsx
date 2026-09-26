import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { OPERATION_FEEDBACK_TOAST_DURATION_MS, OperationFeedbackToast, type OperationFeedback } from './operation-feedback-toast'

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

    expect(host.querySelector('.operation-feedback-toast.is-pending')?.getAttribute('aria-busy')).toBe('true')
    act(() => { vi.advanceTimersByTime(OPERATION_FEEDBACK_TOAST_DURATION_MS * 3) })
    expect(onClose).not.toHaveBeenCalled()
    expect(host.querySelector('.operation-feedback-toast.is-pending')).not.toBeNull()

    const completed: OperationFeedback = { message: 'Ürün güncellendi.', kind: 'success' }
    act(() => root?.render(<OperationFeedbackToast feedback={completed} onClose={onClose} />))
    expect(host.querySelector('.operation-feedback-toast.is-pending')).toBeNull()

    act(() => { vi.advanceTimersByTime(OPERATION_FEEDBACK_TOAST_DURATION_MS - 1) })
    expect(onClose).not.toHaveBeenCalled()
    act(() => { vi.advanceTimersByTime(1) })
    expect(onClose).toHaveBeenCalledTimes(1)
  })
})
