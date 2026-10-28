import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ErrorToast, NoticeToast } from './NoticeToast'

;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true

let root: Root | undefined
let host: HTMLDivElement | undefined

function render(element: React.ReactNode) {
  host = document.createElement('div')
  document.body.append(host)
  root = createRoot(host)
  act(() => root?.render(element))
  return host
}

afterEach(() => {
  if (root) act(() => root?.unmount())
  host?.remove()
  root = undefined
  host = undefined
})

describe('notice toast', () => {
  it('uses capitalised titles per state and announces failures assertively', () => {
    const titles = (['success', 'pending', 'error', 'info'] as const).map(kind => {
      const view = render(<NoticeToast kind={kind} message="Mesaj" />)
      const title = view.querySelector('strong')?.textContent
      const role = view.querySelector('.app-toast')?.getAttribute('role')
      act(() => root?.unmount()); view.remove(); root = undefined; host = undefined
      return [title, role]
    })
    expect(titles).toEqual([['İşlem Başarılı', 'status'], ['İşlem Sürüyor', 'status'], ['İşlem Başarısız', 'alert'], ['Bilgi', 'status']])
  })

  it('shows a countdown bar for timed toasts, a running bar while pending and closes on request', () => {
    const onClose = vi.fn()
    const view = render(<NoticeToast kind="success" message="Tamam" durationMs={4000} onClose={onClose} />)
    expect(view.querySelector('.app-toast-progress')).not.toBeNull()
    expect((view.querySelector('.app-toast') as HTMLElement).style.getPropertyValue('--app-toast-duration')).toBe('4000ms')
    act(() => view.querySelector<HTMLButtonElement>('.app-toast-close')!.click())
    expect(onClose).toHaveBeenCalledTimes(1)
    act(() => root?.render(<NoticeToast kind="pending" message="Sürüyor" />))
    expect(view.querySelector('.app-toast-progress')).not.toBeNull()
    expect(view.querySelector('.app-toast')?.getAttribute('aria-busy')).toBe('true')
    expect(view.querySelector('.app-toast-close')).toBeNull()
  })

  it('shows the specific title an error toast is given instead of the generic one', () => {
    const view = render(<ErrorToast title="Kategori Kaydedilemedi" error={new Error('Ad zorunlu.')} />)
    expect(view.querySelector('strong')?.textContent).toBe('Kategori Kaydedilemedi')
    expect(view.querySelector('p')?.textContent).toBe('Ad zorunlu.')
  })
})
