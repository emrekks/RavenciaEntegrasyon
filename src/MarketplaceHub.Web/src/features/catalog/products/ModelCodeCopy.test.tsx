import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ModelCodeCopy } from './ModelCodeCopy'

const copyState = vi.hoisted(() => ({ texts: [] as string[] }))
vi.mock('../../../shared/copy-text', () => ({ copyText: async (text: string) => { copyState.texts.push(text); return true } }))

describe('ModelCodeCopy', () => {
  let host: HTMLDivElement
  let root: Root
  beforeEach(() => {
    (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true
    copyState.texts = []
    host = document.createElement('div')
    document.body.appendChild(host)
    root = createRoot(host)
  })
  afterEach(() => { act(() => root.unmount()); host.remove() })

  it('copies only the model code, keeps the click inside the button and shows it was copied', async () => {
    const rowClick = vi.fn()
    // React listens at the root (host), so a click that stops there never reaches the body.
    document.body.addEventListener('click', rowClick)
    act(() => root.render(<small>Model Kodu: <b>RYP007</b><ModelCodeCopy value="RYP007" /></small>))
    const button = host.querySelector('button')!
    expect(button.getAttribute('aria-label')).toBe('Model kodunu kopyala: RYP007')
    expect(button.type).toBe('button')
    await act(async () => { button.click() })
    expect(copyState.texts).toEqual(['RYP007'])
    document.body.removeEventListener('click', rowClick)
    expect(rowClick).not.toHaveBeenCalled()
    expect(button.getAttribute('aria-label')).toBe('Model kodu RYP007 kopyalandı')
    expect(button.title).toBe('Kopyalandı')
  })
})
