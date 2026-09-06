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
