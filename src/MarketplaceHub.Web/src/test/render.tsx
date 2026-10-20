import { act, type ReactNode } from 'react'
import { createRoot, type Root } from 'react-dom/client'

;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true

const mounted: Array<{ root: Root; host: HTMLElement }> = []

/** Renders into a fresh element in the document; `cleanup()` (call it in afterEach) unmounts everything. */
export function render(element: ReactNode) {
  const host = document.createElement('div')
  document.body.append(host)
  const root = createRoot(host)
  act(() => root.render(element))
  mounted.push({ root, host })
  return { host, rerender: (next: ReactNode) => act(() => root.render(next)), unmount: () => act(() => root.unmount()) }
}

export function cleanup() {
  for (const { root, host } of mounted.splice(0)) {
    act(() => root.unmount())
    host.remove()
  }
}

/** Lets pending promises and React updates settle. */
export async function flush(times = 3) {
  for (let index = 0; index < times; index += 1) await act(async () => { await Promise.resolve() })
}

export function byText<T extends HTMLElement = HTMLElement>(root: ParentNode, selector: string, text: string) {
  const found = Array.from(root.querySelectorAll<T>(selector)).find(element => element.textContent?.includes(text))
  if (!found) throw new Error(`No ${selector} containing “${text}”`)
  return found
}

export function click(element: HTMLElement) {
  act(() => { element.dispatchEvent(new MouseEvent('click', { bubbles: true })) })
}

export function key(target: EventTarget, keyName: string, init: KeyboardEventInit = {}) {
  act(() => { target.dispatchEvent(new KeyboardEvent('keydown', { key: keyName, bubbles: true, cancelable: true, ...init })) })
}

/** Types into a controlled input the way React listens for it. */
export function type(input: HTMLInputElement | HTMLTextAreaElement, value: string) {
  const prototype = input instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype
  act(() => {
    Object.getOwnPropertyDescriptor(prototype, 'value')?.set?.call(input, value)
    input.dispatchEvent(new Event('input', { bubbles: true }))
  })
}

/** Waits (real timers) until the check stops throwing or returns a truthy value. */
export async function waitFor<T>(check: () => T, timeoutMs = 2000): Promise<NonNullable<T>> {
  const started = Date.now()
  let lastError: unknown
  while (Date.now() - started < timeoutMs) {
    try {
      const value = check()
      if (value !== false && value !== null && value !== undefined) return value as NonNullable<T>
    } catch (error) { lastError = error }
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 10)) })
  }
  throw lastError ?? new Error('waitFor timed out')
}
