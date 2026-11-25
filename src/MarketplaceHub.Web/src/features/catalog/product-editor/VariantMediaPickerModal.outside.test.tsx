import { act, type ReactNode } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { Modal } from '../../../shared/components'
import { VariantMediaPickerModal } from './VariantMediaPickerModal'

;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true

let root: Root | null = null
afterEach(() => { act(() => root?.unmount()); root = null; document.body.innerHTML = '' })

function mount(node: ReactNode) {
  const container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
  act(() => root!.render(node))
}
const outside = (selector: string) => act(() => { document.body.querySelector(selector)!.dispatchEvent(new MouseEvent('mousedown', { bubbles: true })) })

describe('windows with unsaved changes and a click outside (scan D-11)', () => {
  it('the image picker stays open once its ticks changed', () => {
    const onClose = vi.fn()
    const picker = (refs: string[]) => <VariantMediaPickerModal mode="variant" options={[]} chartKey={null} selectedRefs={refs} onRefsChange={() => undefined} onApply={() => undefined} onClose={onClose} />
    mount(picker(['a']))
    act(() => root!.render(picker(['a', 'b'])))
    outside('.variant-media-picker-backdrop')
    expect(onClose).not.toHaveBeenCalled()
    act(() => root!.render(picker(['a'])))
    outside('.variant-media-picker-backdrop')
    expect(onClose).toHaveBeenCalledTimes(1)
  })

  it('a window asked to keep open ignores a click outside, the cross still closes it', () => {
    const onClose = vi.fn()
    mount(<Modal open title="Beden Tablosu Oluştur" onClose={onClose} keepOpenOnOutsideClick>İçerik</Modal>)
    outside('.rv-overlay')
    expect(onClose).not.toHaveBeenCalled()
    act(() => { document.body.querySelector<HTMLButtonElement>('.rv-modal header button')!.click() })
    expect(onClose).toHaveBeenCalledTimes(1)
  })
})
