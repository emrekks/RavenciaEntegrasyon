import { useEffect } from 'react'

type MainLockState = { count: number; previousOverflow: string }
type ScrollLock = { main: HTMLElement | null; root: boolean }
type LockOptions = { root?: boolean }

const activeLocks = new Set<ScrollLock>()
const mainLockStates = new Map<HTMLElement, MainLockState>()
let previousBodyOverflow = ''
let rootLocks = 0
let previousRootStyles: { html: [string, string, string]; body: [string, string] } | null = null

// The value put back when the last lock goes. A lock never remembers 'hidden' as the page's own value: when two overlays
// locked the page at once, the second one used to remember the first one's 'hidden' and put it back after both closed,
// which left the page unable to scroll (the phone menu did this).
const unlocked = (value: string) => value === 'hidden' ? '' : value

// A root lock also holds the document itself still: html clipped to the screen, no rubber-band scrolling (the phone
// sign-in screen, which must fit one screen; iOS Safari otherwise still drags the page).
function lockRoot() {
  rootLocks += 1
  if (rootLocks > 1) return
  const html = document.documentElement.style
  const body = document.body.style
  previousRootStyles = { html: [unlocked(html.overflow), html.height, html.overscrollBehavior], body: [body.height, body.overscrollBehavior] }
  html.height = '100%'; html.overflow = 'hidden'; html.overscrollBehavior = 'none'
  body.height = '100%'; body.overscrollBehavior = 'none'
}

function unlockRoot() {
  rootLocks = Math.max(0, rootLocks - 1)
  if (rootLocks || !previousRootStyles) return
  const html = document.documentElement.style
  const body = document.body.style
  ;[html.overflow, html.height, html.overscrollBehavior] = previousRootStyles.html
  ;[body.height, body.overscrollBehavior] = previousRootStyles.body
  previousRootStyles = null
}

function acquire(lockMain: boolean, { root = false }: LockOptions = {}) {
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
  if (root) lockRoot()

  const lock = { main, root }
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
  if (lock.root) unlockRoot()

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
  const html = document.documentElement.style
  if (html.overflow === 'hidden') {
    html.overflow = ''; html.height = ''; html.overscrollBehavior = ''
    document.body.style.height = ''; document.body.style.overscrollBehavior = ''
  }
  rootLocks = 0
  previousRootStyles = null
}

/** Locks the page for an overlay that is not a hook user; call the returned function when it closes. */
export function lockPageScroll(lockMain = false, options?: LockOptions) {
  return acquire(lockMain, options)
}

/**
 * Keeps body and the application scroll owner locked until every overlay closes. `root` also holds the document
 * (html) still with no rubber-band scrolling, for a screen that must fit the viewport.
 */
export function useScrollLock(enabled: boolean, lockMain = false, { root = false }: LockOptions = {}) {
  useEffect(() => {
    if (!enabled) return
    return acquire(lockMain, { root })
  }, [enabled, lockMain, root])
}
