import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { NoticeToast } from './NoticeToast'
import { ToastStackProvider } from './ToastStack'
import { useToastStack, type ToastStackApi } from './toast-stack-context'

;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true

let root: Root | undefined
let host: HTMLDivElement | undefined
let stack: ToastStackApi | undefined

function Capture() {
  stack = useToastStack()
  return null
}

function render(children: React.ReactNode = null) {
  host = document.createElement('div')
  document.body.append(host)
  root = createRoot(host)
  act(() => root?.render(<ToastStackProvider><Capture />{children}</ToastStackProvider>))
  return host
}

const messages = () => [...host!.querySelectorAll('.app-toast-stack > .app-toast p')].map(item => item.textContent)

afterEach(() => {
  if (root) act(() => root?.unmount())
  host?.remove()
  root = undefined
  host = undefined
  stack = undefined
  vi.useRealTimers()
})

describe('toast stack provider', () => {
  it('stacks toasts in arrival order, each with its own timer, and keeps an error until it is closed', () => {
    vi.useFakeTimers()
    render()
    act(() => stack!.show({ kind: 'success', message: 'Bir', durationMs: 3000 }))
    act(() => { vi.advanceTimersByTime(1000) })
    act(() => stack!.show({ kind: 'error', title: 'Yayın Reddedildi', message: 'İki' }))
    act(() => stack!.show({ kind: 'info', message: 'Üç', durationMs: 3000 }))
    expect(messages()).toEqual(['Bir', 'İki', 'Üç'])
    expect([...host!.querySelectorAll('.app-toast-stack > .app-toast')].map(item => item.getAttribute('role'))).toEqual(['status', 'alert', 'status'])

    act(() => { vi.advanceTimersByTime(2000) })
    expect(messages()).toEqual(['İki', 'Üç'])
    act(() => { vi.advanceTimersByTime(1000) })
    expect(messages()).toEqual(['İki'])
    act(() => { vi.advanceTimersByTime(60_000) })
    expect(messages()).toEqual(['İki'])

    act(() => host!.querySelector<HTMLButtonElement>('.app-toast-close')!.click())
    expect(messages()).toEqual([])
  })

  it('restarts the timer of an identical toast instead of showing it twice', () => {
    vi.useFakeTimers()
    render()
    act(() => stack!.show({ kind: 'success', message: 'Kaydedildi', durationMs: 3000 }))
    act(() => { vi.advanceTimersByTime(2500) })
    act(() => stack!.show({ kind: 'success', message: 'Kaydedildi', durationMs: 3000 }))
    expect(messages()).toEqual(['Kaydedildi'])
    act(() => { vi.advanceTimersByTime(2500) })
    expect(messages()).toEqual(['Kaydedildi'])
    act(() => { vi.advanceTimersByTime(500) })
    expect(messages()).toEqual([])
  })

  it('draws a toast a page renders itself into the same column', () => {
    const view = render(<NoticeToast kind="info" message="Sayfa bildirimi" />)
    act(() => stack!.show({ kind: 'success', message: 'Yığın bildirimi', durationMs: 3000 }))
    expect(view.querySelectorAll('.app-toast-stack > .app-toast')).toHaveLength(2)
    expect(messages()).toContain('Sayfa bildirimi')
  })
})
