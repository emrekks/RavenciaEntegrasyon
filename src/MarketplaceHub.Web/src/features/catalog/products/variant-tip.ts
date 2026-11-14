import { fitTip, isTouchOnly, unfitTip } from '../../../shared/touch-tips'

// The variant stock table of a product row ("7 seçenek / 28 varyant").
//
// On a wide screen it is placed on the window, not inside the list's scroll box (which cut off every colour past the
// box's edge): under its row, or above it when the window has more room there, in the browser's top layer
// (popover="manual") so no sticky card, tab bar or sidebar covers it.
// Phones and tablets (up to 1024 px) show the list as cards: there the table opens inside its card at the card's width
// (products-responsive.css, tablet-catalog.css), under its trigger, and fitTip only moves it above the trigger when the
// window has no room below. A fixed position written here on a wider screen is taken off there.
//
// Open and close, one table at a time:
// - it opens when the pointer enters "N seçenek" or Tab brings the keyboard focus to the cell (a focus given back by a
//   closed dialog, a click or the window coming back to the front never opens it);
// - it closes when the pointer has left both "N seçenek" and the table for CLOSE_DELAY ms, when the cell loses focus
//   without the pointer on it, on Esc, a scroll outside the table, a press outside it, a resize, the window losing
//   focus, and when another row's table opens.
// A manual popover never closes by itself and its fixed place does not follow a scroll, so every one of these has to
// close it here; before this, a focus without the pointer opened it and nothing but a blur closed it again.
// Touch screens ((hover: none)) are left to touch-tips.ts: a tap toggles it, a tap elsewhere closes it.

export const CLOSE_DELAY = 150
// Set on a cell whose table was closed while the pointer may still be on it, so the CSS :hover rule does not show the
// table again at its stale place; taken off when the pointer enters again.
export const SHUT_ATTR = 'data-vtip-shut'

let open: HTMLElement | null = null
let hovered: HTMLElement | null = null
let closeTimer: ReturnType<typeof setTimeout> | null = null

const tipOf = (anchor: HTMLElement) => anchor.querySelector<HTMLElement>(':scope > .rr-vtip')

function cancelClose() {
  if (closeTimer !== null) clearTimeout(closeTimer)
  closeTimer = null
}

function hidePopover(tip: HTMLElement) {
  if (typeof tip.hidePopover === 'function' && tip.matches(':popover-open')) tip.hidePopover()
}

function place(anchor: HTMLElement, tip: HTMLElement) {
  if (window.matchMedia('(max-width: 1024px)').matches) {
    hidePopover(tip)
    for (const property of ['position', 'transform', 'top', 'left', 'right', 'bottom', 'max-height', 'margin']) tip.style.removeProperty(property)
    anchor.removeAttribute('data-up')
    fitTip(anchor, tip)
    return
  }
  unfitTip(tip)
  if (typeof tip.showPopover === 'function') {
    tip.setAttribute('popover', 'manual')
    if (!tip.matches(':popover-open')) tip.showPopover()
  }
  const edge = 8, offset = 6
  const own = anchor.getBoundingClientRect()
  Object.assign(tip.style, { position: 'fixed', transform: 'none', right: 'auto', bottom: 'auto', maxHeight: 'none', margin: '0' })
  const needed = tip.scrollHeight || 260
  const below = window.innerHeight - own.bottom - offset - edge
  const above = own.top - offset - edge
  const up = below < needed && above > below
  const height = Math.max(96, Math.min(needed, up ? above : below))
  anchor.toggleAttribute('data-up', up)
  tip.style.maxHeight = `${height}px`
  tip.style.top = `${up ? own.top - offset - height : own.bottom + offset}px`
  tip.style.left = `${Math.max(edge, Math.min(own.left, window.innerWidth - tip.offsetWidth - edge))}px`
}

export function openVariantTip(anchor: HTMLElement) {
  cancelClose()
  if (open && open !== anchor) closeVariantTip()
  const tip = tipOf(anchor)
  if (!tip) return
  anchor.removeAttribute(SHUT_ATTR)
  if (open !== anchor) listen(true)
  open = anchor
  place(anchor, tip)
}

export function closeVariantTip() {
  cancelClose()
  const anchor = open
  open = null
  listen(false)
  if (!anchor) return
  const tip = tipOf(anchor)
  if (tip) hidePopover(tip)
  anchor.setAttribute(SHUT_ATTR, '')
}

export const openVariantTipAnchor = () => open

// The pointer entered "N seçenek" or the open table.
export function variantTipPointerEnter(anchor: HTMLElement) {
  if (isTouchOnly()) return
  hovered = anchor
  if (open === anchor) cancelClose()
  else openVariantTip(anchor)
}

// The pointer left "N seçenek" or the open table: it closes unless the pointer reaches either again in time, or the
// keyboard focus holds it.
export function variantTipPointerLeave(anchor: HTMLElement) {
  if (hovered === anchor) hovered = null
  if (open !== anchor) return
  cancelClose()
  closeTimer = setTimeout(() => {
    closeTimer = null
    if (open === anchor && hovered !== anchor && !anchor.matches(':focus-visible')) closeVariantTip()
  }, CLOSE_DELAY)
}

// Keyboard focus is focus reached with Tab: a focus the browser draws as :focus-visible after a dialog closed with Esc
// or the window came back to the front is not someone reading the table, and nothing would close it under a still pointer.
let tabbedAt = 0
const TAB_WINDOW = 1000
if (typeof document !== 'undefined') document.addEventListener('keydown', event => { if (event.key === 'Tab') tabbedAt = Date.now() }, true)

export function variantTipFocus(anchor: HTMLElement) {
  if (isTouchOnly() || Date.now() - tabbedAt > TAB_WINDOW || !anchor.matches(':focus-visible')) return
  openVariantTip(anchor)
}

export function variantTipBlur(anchor: HTMLElement) {
  if (open === anchor && hovered !== anchor) closeVariantTip()
}

function onKeyDown(event: KeyboardEvent) {
  if (event.key === 'Escape') closeVariantTip()
}

function onScroll(event: Event) {
  const tip = open && tipOf(open)
  if (tip && event.target instanceof Node && tip.contains(event.target)) return
  closeVariantTip()
}

function onPointerDown(event: PointerEvent) {
  if (open && event.target instanceof Node && open.contains(event.target)) return
  closeVariantTip()
}

const onWindowChange = () => closeVariantTip()

let listening = false
function listen(on: boolean) {
  if (on === listening) return
  listening = on
  const method = on ? 'addEventListener' : 'removeEventListener'
  document[method]('keydown', onKeyDown as EventListener)
  document[method]('scroll', onScroll, { capture: true })
  document[method]('pointerdown', onPointerDown as EventListener, true)
  window[method]('resize', onWindowChange)
  window[method]('blur', onWindowChange)
}
