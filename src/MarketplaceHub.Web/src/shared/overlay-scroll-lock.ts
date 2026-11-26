import { isHomeScreenApp } from './phone-shell'

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
  // The phone's full-screen menu is aria-modal but covers the page itself and keeps the bottom bar: pinning body (position: fixed) for it
  // made a Home Screen app measure the fixed layers against a different box than with the menu closed.
  // The phone's full-screen order detail covers the page the same way and scrolls its own body (overscroll contained): pinned, a Home
  // Screen app drew it short of the screen's bottom edge (owner, 26 Nov 2026, iPhone 16 Pro).
  '[aria-modal="true"]:not(.rv-fullmenu):not(.rr-sheet-full)',
].join(', ')

let pinned: { y: number; body: [string, string, string, string, string] } | null = null

// Home Screen app: body is not pinned (that is what drew every window short of the screen's bottom edge). The page is held by refusing the
// touch moves that would scroll it: a move inside something that scrolls within the window goes on (its overscroll is contained in CSS,
// styles/phone-spacing.css), any other move while a window is open is stopped.
let touchHold: ((event: TouchEvent) => void) | null = null

function scrollsInside(target: EventTarget | null) {
  for (let element = target instanceof Element ? target : null; element && element !== document.body; element = element.parentElement) {
    const style = getComputedStyle(element)
    if (/(auto|scroll)/.test(style.overflowY) && element.scrollHeight > element.clientHeight + 1) return true
    if (/(auto|scroll)/.test(style.overflowX) && element.scrollWidth > element.clientWidth + 1) return true
  }
  return false
}

function holdTouches() {
  if (touchHold) return
  touchHold = event => { if (event.cancelable && !scrollsInside(event.target)) event.preventDefault() }
  document.addEventListener('touchmove', touchHold, { passive: false })
}

function releaseTouches() {
  if (!touchHold) return
  document.removeEventListener('touchmove', touchHold)
  touchHold = null
}

function pin() {
  if (isHomeScreenApp()) { holdTouches(); return }
  if (pinned) return
  const y = window.scrollY
  const body = document.body.style
  pinned = { y, body: [body.position, body.top, body.left, body.right, body.width] }
  body.position = 'fixed'; body.top = `-${y}px`; body.left = '0'; body.right = '0'; body.width = '100%'
}

function unpin() {
  releaseTouches()
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
