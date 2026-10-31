import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import { byText, cleanup, click, key, render, type, waitFor } from '../../test/render'
import { DialogSurface, Modal } from './DesignSystem'
import { useConfirmDialog, usePromptDialog } from './ConfirmDialog'
import { httpsUrlProblem } from '../safe-url'

// jsdom has no layout: every element reports offsetParent null. Give elements a parent so focus order can be checked.
const offsetParent = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'offsetParent')
beforeAll(() => { Object.defineProperty(HTMLElement.prototype, 'offsetParent', { configurable: true, get() { return this.parentNode } }) })
afterAll(() => { if (offsetParent) Object.defineProperty(HTMLElement.prototype, 'offsetParent', offsetParent) })
afterEach(cleanup)

const tab = (shift = false) => key(document, 'Tab', { shiftKey: shift })

describe('modal window', () => {
  it('moves focus in, keeps Tab inside and closes on Escape', async () => {
    const onClose = vi.fn()
    render(<><button type="button">Arka plan</button><Modal open title="Düzenle" onClose={onClose} footer={<button type="button">Kaydet</button>}><input aria-label="Ad" data-autofocus /></Modal></>)
    const name = await waitFor(() => document.activeElement?.getAttribute('aria-label') === 'Ad' && document.activeElement)
    expect(name).toBeTruthy()
    const save = byText(document.body, 'button', 'Kaydet')
    save.focus()
    tab()
    expect(document.activeElement?.getAttribute('aria-label')).toBe('Kapat')
    tab(true)
    expect(document.activeElement).toBe(save)
    key(document, 'Escape')
    expect(onClose).toHaveBeenCalledTimes(1)
  })

  it('lets only the top window answer Escape', () => {
    const outer = vi.fn()
    const inner = vi.fn()
    // the confirmation opens over an edit window that is already open
    const { rerender } = render(<Modal open title="Dış" onClose={outer}><p>Form</p></Modal>)
    rerender(<Modal open title="Dış" onClose={outer}><DialogSurface onClose={inner} aria-label="İç"><button type="button">Tamam</button></DialogSurface></Modal>)
    key(document, 'Escape')
    expect(inner).toHaveBeenCalledTimes(1)
    expect(outer).not.toHaveBeenCalled()
  })

  it('gives a hand-built window the dialog role and modal state', () => {
    render(<DialogSurface onClose={() => {}} aria-labelledby="t" className="workspace-modal"><h2 id="t">Başlık</h2></DialogSurface>)
    const dialog = document.querySelector('.workspace-modal')!
    expect(dialog.getAttribute('role')).toBe('dialog')
    expect(dialog.getAttribute('aria-modal')).toBe('true')
    expect(dialog.getAttribute('tabindex')).toBe('-1')
  })
})

function ConfirmProbe({ onResult }: { onResult: (value: boolean) => void }) {
  const [confirm, dialog] = useConfirmDialog()
  return <>{dialog}<button type="button" onClick={() => void confirm({ title: 'Paket Açılsın mı?', message: 'Paket açılacak.', confirmLabel: 'Paketi Aç', destructive: true }).then(onResult)}>Sor</button></>
}

function PromptProbe({ onResult }: { onResult: (value: string | null) => void }) {
  const [prompt, dialog] = usePromptDialog()
  return <>{dialog}<button type="button" onClick={() => void prompt({ title: 'Bağlantı Ekle', label: 'Bağlantı adresi', initialValue: 'https://', validate: httpsUrlProblem, confirmLabel: 'Ekle' }).then(onResult)}>Sor</button></>
}

describe('panel confirmation and address windows', () => {
  it('answers yes only from the confirm button and no from Escape', async () => {
    const onResult = vi.fn()
    render(<ConfirmProbe onResult={onResult} />)
    click(byText(document.body, 'button', 'Sor'))
    expect(document.querySelector('[role="dialog"]')?.textContent).toContain('Paket açılacak.')
    key(document, 'Escape')
    await waitFor(() => onResult.mock.calls.length === 1)
    expect(onResult).toHaveBeenLastCalledWith(false)
    click(byText(document.body, 'button', 'Sor'))
    click(byText(document.body, 'button', 'Paketi Aç'))
    await waitFor(() => onResult.mock.calls.length === 2)
    expect(onResult).toHaveBeenLastCalledWith(true)
    expect(document.querySelector('[role="dialog"]')).toBeNull()
  })

  it('opens on the page body, over a window that is already open, not inside the page section', () => {
    // A page section is its own stacking layer (its motion) and the import window is portalled to the body: a
    // confirmation drawn inside the section stayed behind that window and the start button seemed to do nothing.
    render(<section className="rr-products" style={{ transform: 'translateY(0)' }}><ConfirmProbe onResult={() => {}} /></section>)
    click(byText(document.body, 'button', 'Sor'))
    const overlay = document.querySelector('.rv-confirm-dialog')!.parentElement!
    expect(overlay.parentElement).toBe(document.body)
    expect(overlay.closest('section.rr-products')).toBeNull()
  })

  it('refuses an address that is not https and returns a good one', async () => {
    const onResult = vi.fn()
    render(<PromptProbe onResult={onResult} />)
    click(byText(document.body, 'button', 'Sor'))
    const input = document.querySelector<HTMLInputElement>('[role="dialog"] input')!
    type(input, 'http://ornek.com/a.jpg')
    click(byText(document.body, 'button', 'Ekle'))
    expect(document.querySelector('[role="dialog"] [role="alert"]')?.textContent).toBe('Adres https:// ile başlamalı.')
    expect(onResult).not.toHaveBeenCalled()
    type(input, 'https://ornek.com/a.jpg')
    click(byText(document.body, 'button', 'Ekle'))
    await waitFor(() => onResult.mock.calls.length === 1)
    expect(onResult).toHaveBeenCalledWith('https://ornek.com/a.jpg')
  })

  it('keeps the page from using the browser prompt or confirm', () => {
    const pageSources = import.meta.glob<string>(['../../**/*.tsx', '!../../**/*.test.tsx'], { query: '?raw', import: 'default', eager: true })
    const offenders = Object.entries(pageSources).filter(([, text]) => /window\.(confirm|prompt|alert)\(/.test(text)).map(([file]) => file)
    expect(offenders).toEqual([])
  })
})
