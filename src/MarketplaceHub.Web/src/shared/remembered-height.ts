import { useLayoutEffect, useRef, useState, type CSSProperties } from 'react'

// A block whose content loads later (a list, a strip) keeps the height it had the last time it was shown on this kind
// of screen while it loads, so what sits under it does not jump down when the content arrives. Heights are kept apart
// for phones and wider screens.
export function useRememberedHeight<T extends HTMLElement>(key: string, loading: boolean): { ref: React.RefObject<T | null>; style: CSSProperties | undefined } {
  const ref = useRef<T>(null)
  const storageKey = `rv-height:${key}:${typeof window !== 'undefined' && window.matchMedia('(max-width: 760px)').matches ? 'phone' : 'wide'}`
  const [stored] = useState(() => { try { return Number(localStorage.getItem(storageKey)) || 0 } catch { return 0 } })
  useLayoutEffect(() => {
    const element = ref.current
    if (loading || !element) return
    try { localStorage.setItem(storageKey, String(Math.round(element.getBoundingClientRect().height))) } catch { /* storage unavailable */ }
  })
  return { ref, style: loading && stored ? { minHeight: stored } : undefined }
}
