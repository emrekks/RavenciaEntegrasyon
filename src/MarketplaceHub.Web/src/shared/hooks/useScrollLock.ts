import { useEffect } from 'react'

type MainLockState = { count: number; previousOverflow: string }
type ScrollLock = { main: HTMLElement | null }

const activeLocks = new Set<ScrollLock>()
const mainLockStates = new Map<HTMLElement, MainLockState>()
let previousBodyOverflow = ''

// The value put back when the last lock goes. A lock never remembers 'hidden' as the page's own value: when two overlays
// locked the page at once, the second one used to remember the first one's 'hidden' and put it back after both closed,
// which left the page unable to scroll (the phone menu did this).
const unlocked = (value: string) => value === 'hidden' ? '' : value

function acquire(lockMain: boolean) {
  if (!activeLocks.size) previousBodyOverflow = unlocked(document.body.style.overflow)

  const main = lockMain ? document.querySelector<HTMLElement>('.app-shell.stitch-shell > main') : null
  if (main) {
    const state = mainLockStates.get(main)
    if (state) {
      state.count += 1
    } else {
      mainLockStates.set(main, { count: 1, previousOverflow: unlocked(main.style.overflow) })
    }
    main.style.overflow = 'hidden'
  }

  const lock = { main }
  activeLocks.add(lock)
  document.body.style.overflow = 'hidden'
  return () => release(lock)
}

function release(lock: ScrollLock) {
  if (!activeLocks.delete(lock)) return

  if (lock.main) {
    const state = mainLockStates.get(lock.main)
    if (state) {
      state.count -= 1
      if (state.count <= 0) {
        lock.main.style.overflow = state.previousOverflow
        mainLockStates.delete(lock.main)
      }
    }
  }

  if (!activeLocks.size) document.body.style.overflow = previousBodyOverflow
}

/**
 * The page is unlocked while no overlay holds the lock, even if a style was left behind (a browser that restored a
 * page from its cache, a component that went away without its cleanup). Safe to call at any time.
 */
export function releaseOrphanedScrollLock() {
  if (activeLocks.size) return
  if (document.body.style.overflow === 'hidden') document.body.style.overflow = ''
  const main = document.querySelector<HTMLElement>('.app-shell.stitch-shell > main')
  if (main && main.style.overflow === 'hidden' && !mainLockStates.has(main)) main.style.overflow = ''
}

/** Locks the page for an overlay that is not a hook user; call the returned function when it closes. */
export function lockPageScroll(lockMain = false) {
  return acquire(lockMain)
}

/** Keeps body and the application scroll owner locked until every overlay closes. */
export function useScrollLock(enabled: boolean, lockMain = false) {
  useEffect(() => {
    if (!enabled) return
    return acquire(lockMain)
  }, [enabled, lockMain])
}

// Any open dialog locks the page behind it, including the hand-built windows that do not call useScrollLock:
// a modal dialog (aria-modal) or a modal backdrop in the document keeps body and the app's main scroller still.
const DIALOG_SELECTOR = '[aria-modal="true"], [class*="modal-backdrop"]'

export function installDialogScrollLock() {
  let release: (() => void) | null = null
  let scheduled = false
  const sync = () => {
    scheduled = false
    const open = Boolean(document.querySelector(DIALOG_SELECTOR))
    if (open && !release) release = acquire(true)
    else if (!open && release) { release(); release = null }
  }
  const schedule = () => { if (!scheduled) { scheduled = true; requestAnimationFrame(sync) } }
  new MutationObserver(schedule).observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['aria-modal', 'class'] })
  schedule()
}
