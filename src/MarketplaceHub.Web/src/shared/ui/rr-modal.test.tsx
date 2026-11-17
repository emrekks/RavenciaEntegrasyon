import { act, useState } from 'react'
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render } from '../../test/render'
import { RrModal } from './rr-components'

afterEach(cleanup)

const frame = () => act(async () => { await new Promise(resolve => window.requestAnimationFrame(() => resolve(undefined))) })
const wait = (ms: number) => act(async () => { await new Promise(resolve => setTimeout(resolve, ms)) })
const press = (key: string, shiftKey = false) => act(() => { document.dispatchEvent(new KeyboardEvent('keydown', { key, shiftKey, bubbles: true, cancelable: true })) })

// The one window (one-modal migration, 17 Nov 2026): the windows moved onto RrModal keep a confirmation's semantics,
// trap focus, close on Escape unless locked, give focus back to their opener, and can be drawn inside a page's scope.
function Opener({ locked = false, container }: { locked?: boolean; container?: Element | null }) {
  const [open, setOpen] = useState(false)
  return <>
    <button type="button" onClick={() => setOpen(true)}>Aç</button>
    {open && <RrModal role="alertdialog" describedBy="rr-modal-test-question" crumb="Varyantlar" title="Varyantı Sil" size="md" locked={locked} container={container} layerClass="test-scope" onClose={() => setOpen(false)}
      foot={<><span className="rr-note" /><button type="button" className="rr-btn" data-autofocus>Vazgeç</button><button type="button" className="rr-btn rr-solid">Sil</button></>}>
      <p id="rr-modal-test-question">Bu varyant Kaydet’te silinir.</p>
    </RrModal>}
  </>
}

describe('RrModal', () => {
  it('reads as a confirmation, traps Tab, closes on Escape and gives focus back to the opener', async () => {
    const { host } = render(<Opener />)
    const opener = host.querySelector('button')!
    opener.focus()
    act(() => opener.click())
    await frame()
    const dialog = document.querySelector<HTMLElement>('.rr-modal')!
    expect(dialog.getAttribute('role')).toBe('alertdialog')
    expect(dialog.getAttribute('aria-label')).toBe('Varyantı Sil')
    expect(dialog.getAttribute('aria-describedby')).toBe('rr-modal-test-question')
    expect(dialog.classList.contains('rr-md')).toBe(true)
    // drawn in the body by default, outside the page's container
    expect(host.contains(dialog)).toBe(false)
    // focus starts on the marked control; Tab and Shift+Tab keep it inside the window (jsdom lays nothing out, so the
    // exact order is checked in a real browser, not here)
    expect(document.activeElement?.textContent).toBe('Vazgeç')
    const buttons = [...dialog.querySelectorAll<HTMLButtonElement>('button')]
    buttons[buttons.length - 1].focus()
    press('Tab')
    expect(dialog.contains(document.activeElement)).toBe(true)
    press('Tab', true)
    expect(dialog.contains(document.activeElement)).toBe(true)
    press('Escape')
    await wait(220)
    await frame()
    expect(document.querySelector('.rr-modal')).toBeNull()
    expect(document.activeElement).toBe(opener)
  })

  it('stays open on Escape while locked', async () => {
    const { host } = render(<Opener locked />)
    act(() => host.querySelector('button')!.click())
    await frame()
    press('Escape')
    await wait(220)
    expect(document.querySelector('.rr-modal')).not.toBeNull()
  })

  it('is drawn in the given container, its layer named with the page scope', async () => {
    const { host } = render(<Opener container={document.body.appendChild(document.createElement('main'))} />)
    act(() => host.querySelector('button')!.click())
    await frame()
    const layer = document.querySelector('main > .rr-layer')
    expect(layer?.classList.contains('test-scope')).toBe(true)
    expect(layer?.querySelector('.rr-modal')).not.toBeNull()
    document.querySelector('main')?.remove()
  })
})
