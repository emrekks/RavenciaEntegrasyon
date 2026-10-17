import { lazy, type ComponentType } from 'react'

// After a deploy the previous build's chunks are gone: the server answers their URLs with the app page, so a tab that
// still runs the old build cannot load the next screen and stays stuck until F5. When a screen's code fails to load,
// reload once to pick up the new build (at most once in 30 seconds, so a real network outage cannot loop).
const reloadKey = 'rv-reloaded-for-new-build'

function isChunkLoadError(error: unknown) {
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

// eslint-disable-next-line @typescript-eslint/no-explicit-any -- same constraint as React.lazy
export function lazyPage<T extends ComponentType<any>>(loader: () => Promise<{ default: T }>) {
  return lazy(() => loader().catch((error: unknown): Promise<{ default: T }> => {
    if (isChunkLoadError(error) && reloadForNewBuild()) return new Promise(() => {})
    throw error
  }))
}
