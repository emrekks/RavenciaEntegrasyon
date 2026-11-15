import { Suspense } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { byText, cleanup, click, render, waitFor } from '../test/render'
import { ErrorBoundary } from './components/ErrorBoundary'
import { isChunkLoadError, lazyPage } from './lazy-page'

const reloadKey = 'rv-reloaded-for-new-build'

function Screen() { return <p className="loaded">Faturalar</p> }

beforeEach(() => {
  vi.spyOn(console, 'error').mockImplementation(() => {})
  // A reload already happened a moment ago: this failure must show the box instead of reloading again.
  sessionStorage.setItem(reloadKey, String(Date.now()))
})
afterEach(() => { cleanup(); sessionStorage.clear(); vi.restoreAllMocks() })

describe('lazy pages', () => {
  it('downloads the screen again when Tekrar Dene is pressed after its code failed to load', async () => {
    let calls = 0
    const loader = vi.fn(() => ++calls === 1
      ? Promise.reject(new TypeError('Failed to fetch dynamically imported module: /assets/InvoicesPage-old.js'))
      : Promise.resolve({ default: Screen }))
    const Page = lazyPage(loader, [])
    const { host } = render(<ErrorBoundary><Suspense fallback={<p>Yükleniyor</p>}><Page /></Suspense></ErrorBoundary>)
    await waitFor(() => host.querySelector('[role="alert"]'))
    expect(host.textContent).toContain('Ekranın kodu indirilemedi')
    click(byText(host, 'button', 'Tekrar Dene'))
    await waitFor(() => host.querySelector('.loaded'))
    expect(loader).toHaveBeenCalledTimes(2)
  })

  it('tries a failed download again by itself before giving up', async () => {
    let calls = 0
    const loader = vi.fn(() => ++calls < 3 ? Promise.reject(new TypeError('Failed to fetch dynamically imported module')) : Promise.resolve({ default: Screen }))
    const Page = lazyPage(loader, [1, 1])
    const { host } = render(<ErrorBoundary><Suspense fallback={null}><Page /></Suspense></ErrorBoundary>)
    await waitFor(() => host.querySelector('.loaded'))
    expect(loader).toHaveBeenCalledTimes(3)
    expect(host.querySelector('[role="alert"]')).toBeNull()
  })

  it('reloads once for a new build, never again within 30 seconds', async () => {
    // jsdom cannot reload; the guard's time stamp shows the reload was asked for.
    sessionStorage.removeItem(reloadKey)
    const loader = () => Promise.reject(new TypeError('Failed to fetch dynamically imported module'))
    const First = lazyPage(loader, [])
    const first = render(<ErrorBoundary><Suspense fallback={null}><First /></Suspense></ErrorBoundary>)
    await waitFor(() => sessionStorage.getItem(reloadKey))
    const reloadedAt = sessionStorage.getItem(reloadKey)
    const Second = lazyPage(loader, [])
    const { host } = render(<ErrorBoundary><Suspense fallback={null}><Second /></Suspense></ErrorBoundary>)
    await waitFor(() => host.querySelector('[role="alert"]'))
    expect(sessionStorage.getItem(reloadKey)).toBe(reloadedAt)
    // The page that asked for the reload waits for it instead of showing the box.
    expect(first.host.querySelector('[role="alert"]')).toBeNull()
  })

  it('tells a chunk load failure from other errors', () => {
    expect(isChunkLoadError(new TypeError('Failed to fetch dynamically imported module: x.js'))).toBe(true)
    expect(isChunkLoadError(new Error('Cannot read properties of undefined'))).toBe(false)
  })
})
