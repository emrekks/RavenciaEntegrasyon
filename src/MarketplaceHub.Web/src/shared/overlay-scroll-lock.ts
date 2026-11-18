// While any window, drawer or popup is open, the page behind it stays still: on iOS Safari a hidden overflow on body does
// not stop a drag from moving the page (and the window with it), so the body is pinned in place at the scroll position it
// had, and put back exactly there when the last overlay closes. Only the window's own content scrolls.
// Nothing else on the page changes: a class on <html> (the former `has-overlay`, read by no rule) made the browser restyle
// every element of the page on each open and close (~10 000 on Siparişler, 300-500 ms on a 4x slowed CPU).
export const OVERLAY_SELECTOR = [
  '.workspace-modal-backdrop',
  '.missing-value-mapping-backdrop',
  '.value-mapping-quick-value-backdrop',
  '.rv-overlay',
  '.rr-overlay',
  '[aria-modal="true"]',
].join(', ')

let pinned: { y: number; body: [string, string, string, string, string] } | null = null

function pin() {
  if (pinned) return
  const y = window.scrollY
  const body = document.body.style
  pinned = { y, body: [body.position, body.top, body.left, body.right, body.width] }
  body.position = 'fixed'; body.top = `-${y}px`; body.left = '0'; body.right = '0'; body.width = '100%'
}

function unpin() {
  if (!pinned) return
  const { y, body: [position, top, left, right, width] } = pinned
  const body = document.body.style
  body.position = position; body.top = top; body.left = left; body.right = right; body.width = width
  pinned = null
  window.scrollTo(0, y)
}

const sync = () => { if (document.querySelector(OVERLAY_SELECTOR)) pin(); else unpin() }

/** Watches the page for overlays and pins it while one is open; returns the uninstall function. */
export function installOverlayScrollLock() {
  let frame = 0
  const schedule = () => { if (!frame) frame = requestAnimationFrame(() => { frame = 0; sync() }) }
  const observer = new MutationObserver(schedule)
  observer.observe(document.body, { childList: true, subtree: true })
  sync()
  return () => { observer.disconnect(); if (frame) cancelAnimationFrame(frame); unpin() }
}
