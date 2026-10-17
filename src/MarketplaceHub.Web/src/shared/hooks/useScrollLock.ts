import { useEffect } from 'react'

type MainLockState = { count: number; previousOverflow: string }
type ScrollLock = { main: HTMLElement | null }

const activeLocks = new Set<ScrollLock>()
const mainLockStates = new Map<HTMLElement, MainLockState>()
let previousBodyOverflow = ''

function acquire(lockMain: boolean) {
  if (!activeLocks.size) previousBodyOverflow = document.body.style.overflow

  const main = lockMain ? document.querySelector<HTMLElement>('.app-shell.stitch-shell > main') : null
  if (main) {
    const state = mainLockStates.get(main)
    if (state) {
      state.count += 1
    } else {
      mainLockStates.set(main, { count: 1, previousOverflow: main.style.overflow })
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
