import { useEffect, useRef, useState } from 'react'

// Seconds before the next automatic try: 10, 20, 40, then every 60 so a long outage does not keep the server busy.
export function autoRetryDelaySeconds(attempt: number) {
  return Math.min(60, 10 * 2 ** Math.max(0, attempt))
}

export type AutoRetry = { left: number; total: number; stopped: boolean; retrying: boolean; now: () => void; stop: () => void }

// A failed load counts down and tries again by itself. Each new error (a failed try gives a new one) starts a longer
// countdown; the person can try at once or stop the countdown. Without a retry action there is nothing to count down.
export function useAutoRetry(error: unknown, onRetry?: () => void): AutoRetry | null {
  const retryRef = useRef(onRetry)
  retryRef.current = onRetry
  const attempts = useRef(0)
  const [state, setState] = useState(() => ({ total: autoRetryDelaySeconds(0), left: autoRetryDelaySeconds(0), stopped: false }))
  const enabled = Boolean(onRetry)

  useEffect(() => {
    const total = autoRetryDelaySeconds(attempts.current)
    setState({ total, left: total, stopped: false })
  }, [error])

  useEffect(() => {
    if (!enabled || state.stopped) return
    // A try that ends without a new error (or never ends) still gets the next countdown after a while.
    if (state.left <= 0) {
      const fallback = window.setTimeout(() => { const total = autoRetryDelaySeconds(attempts.current); setState({ total, left: total, stopped: false }) }, 20_000)
      return () => window.clearTimeout(fallback)
    }
    const timer = window.setTimeout(() => {
      if (state.left > 1) { setState(current => ({ ...current, left: current.left - 1 })); return }
      attempts.current += 1
      setState(current => ({ ...current, left: 0 }))
      retryRef.current?.()
    }, 1000)
    return () => window.clearTimeout(timer)
  }, [enabled, state])

  if (!enabled) return null
  return {
    ...state,
    retrying: state.left === 0 && !state.stopped,
    now: () => { attempts.current += 1; setState(current => ({ ...current, left: 0, stopped: false })); retryRef.current?.() },
    stop: () => setState(current => ({ ...current, stopped: true }))
  }
}

export function autoRetryLine(retry: AutoRetry) {
  if (retry.stopped) return 'Otomatik deneme durduruldu.'
  if (retry.retrying) return 'Yeniden deneniyor…'
  return `${retry.left} saniye içinde yeniden denenecek.`
}
