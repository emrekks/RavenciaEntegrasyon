import { useEffect, useLayoutEffect, type RefObject } from 'react'

// A tab strip that scrolls sideways (phones, long tab sets): the active tab is brought fully into view on mount and
// whenever it changes, and the strip carries data-more-start / data-more-end while tabs wait off-screen on that side
// (view-tabs.css draws the edge fade from them). Only the strip's own scrollLeft moves: scrollIntoView would also
// scroll the page vertically.
const SIDE_PADDING = 8

/** The scrollLeft that makes `tab` fully visible inside `strip` with a little room, or null when it already is. */
export function scrollLeftForTab(strip: HTMLElement, tab: HTMLElement, padding = SIDE_PADDING): number | null {
  if (strip.scrollWidth <= strip.clientWidth + 1) return null
  const stripRect = strip.getBoundingClientRect()
  const tabRect = tab.getBoundingClientRect()
  const startGap = tabRect.left - stripRect.left - padding
  const endGap = tabRect.right - stripRect.right + padding
  let delta = 0
  if (startGap < 0) delta = startGap
  else if (endGap > 0) delta = Math.min(endGap, startGap) // never push the tab's start out of view when it is wider than the strip
  if (Math.abs(delta) < 1) return null
  const max = Math.max(0, strip.scrollWidth - strip.clientWidth)
  return Math.min(max, Math.max(0, strip.scrollLeft + delta))
}

export function revealActiveTab(strip: HTMLElement | null, smooth: boolean) {
  const tab = strip?.querySelector<HTMLElement>('[aria-selected="true"]')
  if (!strip || !tab) return
  const left = scrollLeftForTab(strip, tab)
  if (left === null) return
  if (smooth && typeof strip.scrollTo === 'function') strip.scrollTo({ left, behavior: 'smooth' })
  else strip.scrollLeft = left
}

export function updateStripFade(strip: HTMLElement) {
  const overflowing = strip.scrollWidth > strip.clientWidth + 1
  const start = overflowing && strip.scrollLeft > 1
  const end = overflowing && strip.scrollLeft + strip.clientWidth < strip.scrollWidth - 1
  strip.toggleAttribute('data-more-start', start)
  strip.toggleAttribute('data-more-end', end)
}

export function useTabStripScroll(ref: RefObject<HTMLElement | null>, activeValue: string) {
  useLayoutEffect(() => {
    revealActiveTab(ref.current, false)
    if (ref.current) updateStripFade(ref.current)
  }, [ref])
  useEffect(() => {
    const strip = ref.current
    if (!strip) return
    revealActiveTab(strip, true)
    updateStripFade(strip)
  }, [ref, activeValue])
  useEffect(() => {
    const strip = ref.current
    if (!strip) return
    const refresh = () => updateStripFade(strip)
    strip.addEventListener('scroll', refresh, { passive: true })
    const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(refresh)
    observer?.observe(strip)
    // Fonts and counts settle after the first paint and move the tabs: look again once they have.
    let cancelled = false
    void document.fonts?.ready?.then(() => { if (!cancelled) { revealActiveTab(strip, false); refresh() } })
    return () => { cancelled = true; strip.removeEventListener('scroll', refresh); observer?.disconnect() }
  }, [ref])
}
