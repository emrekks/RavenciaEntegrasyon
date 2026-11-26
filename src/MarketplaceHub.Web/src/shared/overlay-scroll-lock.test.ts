import { afterEach, describe, expect, it, vi } from 'vitest'
import { installOverlayScrollLock } from './overlay-scroll-lock'

describe('overlay scroll lock', () => {
  afterEach(() => { document.body.innerHTML = ''; vi.restoreAllMocks() })

  it('pins the page while an overlay is open and puts it back at the same place after', async () => {
    Object.defineProperty(window, 'scrollY', { value: 420, configurable: true })
    const scrollTo = vi.spyOn(window, 'scrollTo').mockImplementation(() => {})
    const uninstall = installOverlayScrollLock()
    const overlay = document.createElement('div'); overlay.className = 'workspace-modal-backdrop'
    document.body.append(overlay)
    await new Promise(resolve => requestAnimationFrame(() => resolve(null)))
    expect(document.body.style.position).toBe('fixed')
    expect(document.body.style.top).toBe('-420px')
    overlay.remove()
    await new Promise(resolve => requestAnimationFrame(() => resolve(null)))
    expect(document.body.style.position).toBe('')
    expect(scrollTo).toHaveBeenCalledWith(0, 420)
    uninstall()
  })

  it('does not pin the page for the phone full-screen order detail', async () => {
    const uninstall = installOverlayScrollLock()
    const sheet = document.createElement('div'); sheet.className = 'rr-sheet rr-sheet-full'; sheet.setAttribute('aria-modal', 'true')
    document.body.append(sheet)
    await new Promise(resolve => requestAnimationFrame(() => resolve(null)))
    expect(document.body.style.position).toBe('')
    uninstall()
    sheet.remove()
  })

  it('does not pin the page for the phone full-screen menu (it keeps the bottom bar where the page left it)', async () => {
    const uninstall = installOverlayScrollLock()
    const menu = document.createElement('div'); menu.className = 'rv-fullmenu'; menu.setAttribute('aria-modal', 'true')
    document.body.append(menu)
    await new Promise(resolve => requestAnimationFrame(() => resolve(null)))
    expect(document.body.style.position).toBe('')
    uninstall()
  })

  // A class or attribute change on <html> restyles every element of the page (10 000 on Siparişler): opening a window must
  // leave the root as it was.
  it('leaves the root element untouched while an overlay is open', async () => {
    vi.spyOn(window, 'scrollTo').mockImplementation(() => {})
    const before = document.documentElement.getAttributeNames().map(name => `${name}=${document.documentElement.getAttribute(name)}`)
    const uninstall = installOverlayScrollLock()
    const overlay = document.createElement('div'); overlay.className = 'rr-overlay'
    document.body.append(overlay)
    await new Promise(resolve => requestAnimationFrame(() => resolve(null)))
    expect(document.body.style.position).toBe('fixed')
    expect(document.documentElement.getAttributeNames().map(name => `${name}=${document.documentElement.getAttribute(name)}`)).toEqual(before)
    overlay.remove()
    await new Promise(resolve => requestAnimationFrame(() => resolve(null)))
    uninstall()
  })
})

// Home Screen app (iOS standalone): pinning body drew every window short of the screen's bottom edge, so the page is held by its touch moves instead.
describe('overlay scroll lock in a Home Screen app', () => {
  it('does not pin body and stops a touch move outside a scrolling part of the window', async () => {
    const original = window.matchMedia
    window.matchMedia = ((query: string) => ({ matches: query.includes('standalone'), media: query, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {}, onchange: null, dispatchEvent: () => false })) as typeof window.matchMedia
    const uninstall = installOverlayScrollLock()
    const overlay = document.createElement('div'); overlay.className = 'rr-overlay'
    document.body.append(overlay)
    await new Promise(resolve => requestAnimationFrame(() => resolve(null)))
    expect(document.body.style.position).toBe('')
    const move = new Event('touchmove', { bubbles: true, cancelable: true })
    overlay.dispatchEvent(move)
    expect(move.defaultPrevented).toBe(true)
    overlay.remove()
    await new Promise(resolve => requestAnimationFrame(() => resolve(null)))
    const after = new Event('touchmove', { bubbles: true, cancelable: true })
    document.body.dispatchEvent(after)
    expect(after.defaultPrevented).toBe(false)
    uninstall()
    window.matchMedia = original
  })
})
