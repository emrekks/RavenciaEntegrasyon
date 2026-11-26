import { useLayoutEffect, useRef, useState } from 'react'

// eslint-disable-next-line @typescript-eslint/no-explicit-any -- any handler shape; the keys and signatures come from T
type Handlers = Record<string, (...args: any[]) => unknown>

/**
 * The same handlers as one object that never changes, each calling the version of the last render. A list hands it to
 * its memoised rows, so picking a row or opening a window does not redraw every other row only because the page made
 * new closures (Siparişler, 26 Nov 2026). The handlers run on user events, after the render that made them is on screen.
 */
export function useStableHandlers<T extends Handlers>(handlers: T): T {
  const latest = useRef(handlers)
  useLayoutEffect(() => { latest.current = handlers })
  const [stable] = useState(() => Object.fromEntries(Object.keys(handlers).map(key => [key, (...args: unknown[]) => latest.current[key](...args)])) as T)
  return stable
}
