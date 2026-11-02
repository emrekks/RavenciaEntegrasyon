import { useCallback, useEffect, useState } from 'react'

function scrollParent(element: HTMLElement): HTMLElement | null {
  for (let node = element.parentElement; node; node = node.parentElement) {
    const overflowY = window.getComputedStyle(node).overflowY
    if (overflowY === 'auto' || overflowY === 'scroll') return node
  }
  return null
}

// Every 1 % of the bar's height, so the observer reports the moment the bar is fully above the edge.
const thresholds = Array.from({ length: 101 }, (_, index) => index / 100)

// Tells whether a `position: sticky; bottom: 0` bar is stuck to the bottom of its scroller (the list continues below
// it) or rests at its natural place after the last block. IntersectionObserver with the root's bottom pulled up 1 px:
// a stuck bar touches that edge and is cut by it, a resting bar lies fully inside. No scroll or resize listeners, so
// nothing can get out of step. Returns [ref callback for the bar, stuck]; stuck stays true where observers are missing.
export function useStuckBottom<T extends HTMLElement = HTMLDivElement>(): [(element: T | null) => void, boolean] {
  const [element, setElement] = useState<T | null>(null)
  const [stuck, setStuck] = useState(true)
  const ref = useCallback((node: T | null) => setElement(node), [])
  useEffect(() => {
    if (!element || typeof IntersectionObserver === 'undefined') return undefined
    const observer = new IntersectionObserver(entries => {
      const entry = entries[entries.length - 1]
      if (!entry) return
      const edge = entry.rootBounds ? entry.rootBounds.bottom : window.innerHeight - 1
      setStuck(entry.boundingClientRect.bottom > edge || entry.intersectionRatio < 1)
    }, { root: scrollParent(element), rootMargin: '0px 200px -1px 200px', threshold: thresholds })
    observer.observe(element)
    return () => observer.disconnect()
  }, [element])
  return [ref, stuck]
}
