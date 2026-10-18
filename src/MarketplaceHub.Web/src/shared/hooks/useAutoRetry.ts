import { useEffect, useRef, useState } from 'react'

// Seconds before the next automatic try: 10, 20, 40, then every 60 so a long outage does not keep the server busy.
export function autoRetryDelaySeconds(attempt: number) {
  return Math.min(60, 10 * 2 ** Math.max(0, attempt))
}

// Failed loads of the last few minutes, kept outside the components: a page usually shows its loading state while it
// tries again, so the error box is built anew after every failed try and could not count them itself.
const failureWindowMs = 5 * 60_000
const recentFailures: number[] = []
export function noteLoadFailure(now = Date.now()) {
  while (recentFailures.length && now - recentFailures[0] > failureWindowMs) recentFailures.shift()
  recentFailures.push(now)
  return recentFailures.length - 1
}

export type AutoRetry = { left: number; total: number; stopped: boolean; retrying: boolean; now: () => void; stop: () => void }

function countdown() {
  const total = autoRetryDelaySeconds(noteLoadFailure())
  return { total, left: total, stopped: false }
}

// A failed load counts down and tries again by itself; every failure in a row waits longer. The person can try at
// once or stop the countdown. Without a retry action there is nothing to count down.
export function useAutoRetry(error: unknown, onRetry?: () => void): AutoRetry | null {
  const retryRef = useRef(onRetry)
  retryRef.current = onRetry
  const shownError = useRef(error)
  const [state, setState] = useState(() => onRetry ? countdown() : { total: 10, left: 10, stopped: false })
  const enabled = Boolean(onRetry)

  // A failed try that keeps the box on screen brings a new error: the next countdown starts.
  useEffect(() => {
    if (shownError.current === error) return
    shownError.current = error
    setState(countdown())
  }, [error])

  useEffect(() => {
    if (!enabled || state.stopped) return
    // A try that ends without a new error (or never ends) still gets the next countdown after a while.
    if (state.left <= 0) {
      const fallback = window.setTimeout(() => setState(countdown()), 20_000)
      return () => window.clearTimeout(fallback)
    }
    const timer = window.setTimeout(() => {
      if (state.left > 1) { setState(current => ({ ...current, left: current.left - 1 })); return }
      setState(current => ({ ...current, left: 0 }))
      retryRef.current?.()
    }, 1000)
    return () => window.clearTimeout(timer)
  }, [enabled, state])

  if (!enabled) return null
  return {
    ...state,
    retrying: state.left === 0 && !state.stopped,
    now: () => { setState(current => ({ ...current, left: 0, stopped: false })); retryRef.current?.() },
    stop: () => setState(current => ({ ...current, stopped: true }))
  }
}

export function autoRetryLine(retry: AutoRetry) {
  if (retry.stopped) return 'Otomatik deneme durduruldu.'
  if (retry.retrying) return 'Yeniden deneniyor…'
  return `${retry.left} saniye içinde yeniden denenecek.`
}
