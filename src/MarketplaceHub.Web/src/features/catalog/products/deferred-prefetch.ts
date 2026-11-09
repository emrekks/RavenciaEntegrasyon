// The products list reads its next page ahead, but not while the page's own pictures are still coming: the next page
// is a large answer and took the bandwidth the first screen's thumbnails needed (OPTIMIZASYON-PLANI P5, 9 Nov 2026).
// It starts once the pictures on screen have loaded (or failed) and the browser is idle, at the latest after
// maxWaitMs, or at once when the person scrolls near the end of the list (nearEnd comes into view).
type IdleWindow = Window & {
  requestIdleCallback?: (callback: () => void, options?: { timeout: number }) => number
  cancelIdleCallback?: (handle: number) => void
}
export type DeferredPrefetchOptions = {
  root?: ParentNode | null
  nearEnd?: Element | null
  maxWaitMs?: number
  idleTimeoutMs?: number
  view?: IdleWindow
}

// The pictures on screen not loaded yet; a lazy picture below the fold never loads by itself, so it is not waited for.
export function pendingVisibleImages(root: ParentNode, view: Pick<Window, 'innerHeight' | 'innerWidth'>): HTMLImageElement[] {
  return [...root.querySelectorAll('img')].filter(image => {
    if (image.complete) return false
    const box = image.getBoundingClientRect()
    return box.bottom > 0 && box.right > 0 && box.top < view.innerHeight && box.left < view.innerWidth
  })
}

export function scheduleDeferredPrefetch(run: () => void, options: DeferredPrefetchOptions = {}): () => void {
  const view = options.view ?? (window as IdleWindow)
  const maxWaitMs = options.maxWaitMs ?? 5000
  const idleTimeoutMs = options.idleTimeoutMs ?? 1500
  let done = false
  const cleanups: Array<() => void> = []
  const cancel = () => { while (cleanups.length) cleanups.pop()!() }
  const fire = () => {
    if (done) return
    done = true
    cancel()
    run()
  }
  const whenIdle = () => {
    if (done) return
    if (view.requestIdleCallback) {
      const handle = view.requestIdleCallback(fire, { timeout: idleTimeoutMs })
      cleanups.push(() => view.cancelIdleCallback?.(handle))
    } else {
      const handle = view.setTimeout(fire, 200)
      cleanups.push(() => view.clearTimeout(handle))
    }
  }

  const deadline = view.setTimeout(fire, maxWaitMs)
  cleanups.push(() => view.clearTimeout(deadline))

  if (options.nearEnd && typeof IntersectionObserver === 'function') {
    const observer = new IntersectionObserver(entries => { if (entries.some(entry => entry.isIntersecting)) fire() }, { rootMargin: '600px 0px' })
    observer.observe(options.nearEnd)
    cleanups.push(() => observer.disconnect())
  }

  const pending = options.root ? pendingVisibleImages(options.root, view) : []
  if (!pending.length) whenIdle()
  else {
    let left = pending.length
    const settle = () => { left -= 1; if (left === 0) whenIdle() }
    for (const image of pending) {
      image.addEventListener('load', settle, { once: true })
      image.addEventListener('error', settle, { once: true })
      cleanups.push(() => { image.removeEventListener('load', settle); image.removeEventListener('error', settle) })
    }
  }
  return () => { done = true; cancel() }
}
