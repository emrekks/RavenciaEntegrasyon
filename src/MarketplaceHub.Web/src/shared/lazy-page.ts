import { createElement, lazy, type ComponentProps, type ComponentType, type LazyExoticComponent } from 'react'

// After a deploy the previous build's chunks are gone: the server answers their URLs with 404, so a tab that still runs
// the old build cannot load the next screen and stays stuck until F5. When a screen's code fails to load, reload once to
// pick up the new build (at most once in 30 seconds, so a real network outage cannot loop).
const reloadKey = 'rv-reloaded-for-new-build'

export function isChunkLoadError(error: unknown) {
  const message = error instanceof Error ? `${error.name} ${error.message}` : String(error)
  return /dynamically imported module|Importing a module script failed|module script|MIME type|ChunkLoadError|Loading chunk/i.test(message)
}

export function reloadForNewBuild() {
  try {
    const last = Number(sessionStorage.getItem(reloadKey) ?? 0)
    if (Date.now() - last < 30_000) return false
    sessionStorage.setItem(reloadKey, String(Date.now()))
  } catch { /* storage unavailable: still reload */ }
  window.location.reload()
  return true
}

// A dropped connection for a moment: the download is tried again after half a second and after 1.5 seconds more.
const loadRetryDelaysMs: readonly number[] = [500, 1500]

// Screens whose code failed to load, each with the step that gives it a fresh lazy component.
const failedPages = new Set<() => void>()

/**
 * React.lazy keeps a failed load for good. The error box calls this before it draws the screen again (Tekrar Dene, or
 * another address): every screen whose load failed gets a fresh lazy component and downloads its code again. Not done
 * inside the failed load itself, since React draws a failed tree once more at once and would loop on it.
 */
export function retryFailedLazyPages() {
  for (const renew of failedPages) renew()
  failedPages.clear()
}

/** A screen loaded on first use; a short network drop is tried again, a build replaced by a deploy reloads once. */
// eslint-disable-next-line @typescript-eslint/no-explicit-any -- same constraint as React.lazy
export function lazyPage<T extends ComponentType<any>>(loader: () => Promise<{ default: T }>, retryDelaysMs: readonly number[] = loadRetryDelaysMs) {
  const renew = () => { current = lazy(load) }
  const load = async (): Promise<{ default: T }> => {
    for (let attempt = 0; ; attempt++) {
      try {
        return await loader()
      } catch (error) {
        if (isChunkLoadError(error) && attempt < retryDelaysMs.length) { await new Promise(resolve => setTimeout(resolve, retryDelaysMs[attempt])); continue }
        if (isChunkLoadError(error) && reloadForNewBuild()) return new Promise(() => {})
        failedPages.add(renew)
        throw error
      }
    }
  }
  let current: LazyExoticComponent<T> = lazy(load)
  function LazyPage(props: ComponentProps<T>) {
    return createElement(current, props)
  }
  return LazyPage as unknown as LazyExoticComponent<T>
}
