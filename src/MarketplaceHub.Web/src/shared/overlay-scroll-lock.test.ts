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
})
