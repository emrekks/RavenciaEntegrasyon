import './touch-tips.css'

// Touch screens have no hover: a tip that opens under the pointer either never shows or sticks after the finger
// lifts, and its desktop placement runs off a phone's edge. On a touch screen ((hover: none)) every hover-only tip
// listed below opens on a tap instead and closes on a second tap, a tap elsewhere, Esc, a scroll or a resize; it is
// moved to stay inside the window (12 px from each side, never under the sticky top bar) and opens above its trigger
// when there is no room below (or below when there is none above). Desktop behaviour is unchanged: nothing here runs
// while the pointer can hover. One document-level listener serves every page (installTouchTips, from the app shell).
//
// Two kinds of tip:
// - an element tip (a child of the trigger's host) is shown in place by the CSS for [data-touch-tip-open] in
//   touch-tips.css and the host's own stylesheet, then nudged into the window with the `translate` property;
// - an attribute tip (the text of data-tip and the like, drawn by a ::after on desktop) is shown as one bubble on the
//   body, placed with fixed coordinates.
// A trigger inside a link is left out: its tap opens the link (the dashboard's operation cards, for one).

type TipFamily = {
  // What the finger taps.
  trigger: string
  // The element that carries [data-touch-tip-open], found from the trigger with closest(); the trigger by default.
  host?: string
  // The tip inside the host (element tip), or the attribute holding the text (attribute tip).
  tip?: string
  attr?: string
  // A media query the family applies under (beside (hover: none)).
  media?: string
}

export const touchTipFamilies: TipFamily[] = [
  // Products list: a product card's marketplace logos ("Trendyol'da yayında: tüm varyantlar (28/28)").
  { trigger: '.rr-pm[data-tip]', attr: 'data-tip' },
  // Products list: the variant stock table of a card (its tap shows the table, not the quick edit).
  { trigger: '.rr-vhover:not(.rr-vhover-none)', tip: ':scope > .rr-vtip' },
  // Orders list: the marks beside an order number (delivery point, fast delivery, micro export).
  { trigger: '.rr-omark[data-tip]', attr: 'data-tip' },
  // Orders summary cards that do not open a list (Yeni Sipariş, İşleme Alınan, Geciken): the stores behind the figure.
  { trigger: '.rr-metrics article.rr-metric-tipped:not(.rr-metric-link)', tip: '.rr-metric-tip' },
  // Invoice and return failure reasons.
  { trigger: '.rr-reason-tip', tip: '.rr-reason-pop' },
  // Reports: the explanation behind a row's name or its "?".
  { trigger: '.reports-tip-host .reports-tip-trigger', host: '.reports-tip-host', tip: ':scope > .reports-hover-tip' },
  // Integrations: a connection's sign-in problem.
  { trigger: '.connection-auth-badge', tip: '.connection-auth-tip' },
  // Questions: a store's sync state (a line under the name on phones, a ::after on wider screens).
  { trigger: '.qi-sync .rv-questions-sync-item', tip: ':scope > div > small:not(.qi-sync-time)', media: '(max-width: 760px)' },
  { trigger: '.qi-sync .rv-questions-sync-item[data-detail]', attr: 'data-detail', media: '(min-width: 761px)' }
]

export const OPEN_ATTR = 'data-touch-tip-open'
const EDGE = 12
const GAP = 6
const SCROLL_SLOP = 8
// Elements that stay on top of the page while it scrolls; a tip never opens under them.
const STICKY_TOP = '.rv-topbar, .rv-view-tabs, .ss-gtabs, .mfs-header, [data-sticky-top]'

type Open = { host: HTMLElement; tip: HTMLElement; bubble: boolean }
let open: Open | null = null
let bubble: HTMLDivElement | null = null

const matches = (query: string) => typeof window.matchMedia === 'function' && window.matchMedia(query).matches
export const isTouchOnly = () => matches('(hover: none)')

function familyOf(target: Element) {
  for (const family of touchTipFamilies) {
    if (family.media && !matches(family.media)) continue
    const trigger = target.closest<HTMLElement>(family.trigger)
    if (!trigger || trigger.closest('a[href]')) continue
    const host = family.host ? trigger.closest<HTMLElement>(family.host) : trigger
    if (!host) continue
    if (family.attr) {
      const text = host.getAttribute(family.attr)
      if (text) return { family, host, text }
      continue
    }
    const tip = family.tip ? host.querySelector<HTMLElement>(family.tip) : null
    if (tip) return { family, host, tip }
  }
  return null
}

// The lowest edge of the bars stuck to the top of the window (0 when there are none).
export function stickyTopEdge() {
  let edge = 0
  for (const element of document.querySelectorAll<HTMLElement>(STICKY_TOP)) {
    const position = getComputedStyle(element).position
    if (position !== 'sticky' && position !== 'fixed') continue
    const rect = element.getBoundingClientRect()
    if (rect.height === 0 || rect.bottom <= 0 || rect.top > 120 || rect.height > window.innerHeight / 3) continue
    edge = Math.max(edge, rect.bottom)
  }
  return edge
}

type Box = { top: number; bottom: number; left: number; right: number; width: number; height: number }

// Where a tip of this size goes beside its anchor: the side it prefers when it fits there, else the other side, else
// the roomier side with its height cut to fit (then it scrolls). Left is kept EDGE px inside the window.
export function placeBeside(anchor: Box, width: number, height: number, prefer: 'above' | 'below', view = { width: window.innerWidth, height: window.innerHeight, top: stickyTopEdge() }) {
  const top = view.top ? view.top + GAP : EDGE
  const roomBelow = view.height - EDGE - anchor.bottom - GAP
  const roomAbove = anchor.top - GAP - top
  const fitsBelow = height <= roomBelow, fitsAbove = height <= roomAbove
  const below = prefer === 'below' ? fitsBelow || (!fitsAbove && roomBelow >= roomAbove) : !fitsAbove && (fitsBelow || roomBelow > roomAbove)
  const maxHeight = (below ? fitsBelow : fitsAbove) ? null : Math.max(96, below ? roomBelow : roomAbove)
  const shown = maxHeight ?? height
  const maxWidth = view.width - 2 * EDGE
  const shownWidth = Math.min(width, maxWidth)
  return {
    top: below ? anchor.bottom + GAP : anchor.top - GAP - shown,
    left: Math.max(EDGE, Math.min(anchor.left + anchor.width / 2 - shownWidth / 2, view.width - EDGE - shownWidth)),
    maxHeight,
    maxWidth: width > maxWidth ? maxWidth : null,
    below
  }
}

// Moves an element tip, shown in place, into the window: sideways to keep EDGE px from each side, and to the other side
// of its anchor (or cut in height) when it runs past the bottom of the window or under the sticky top bar.
export function fitTip(anchor: HTMLElement, tip: HTMLElement) {
  unfitTip(tip)
  let rect = tip.getBoundingClientRect()
  if (rect.width === 0 && rect.height === 0) return
  const styled: string[] = []
  const set = (property: string, value: string) => { tip.style.setProperty(property, value); styled.push(property) }
  const width = window.innerWidth, height = window.innerHeight
  if (rect.width > width - 2 * EDGE) { set('max-width', `${width - 2 * EDGE}px`); rect = tip.getBoundingClientRect() }
  const dx = rect.left < EDGE ? EDGE - rect.left : rect.right > width - EDGE ? Math.max(EDGE - rect.left, width - EDGE - rect.right) : 0
  const sticky = stickyTopEdge()
  const top = sticky ? sticky + GAP : EDGE
  let dy = 0
  if (rect.top < top || rect.bottom > height - EDGE) {
    const own = anchor.getBoundingClientRect()
    const below = rect.top + rect.height / 2 >= own.top + own.height / 2
    const place = placeBeside(own, rect.width, rect.height, below ? 'below' : 'above', { width, height, top: sticky })
    if (place.maxHeight !== null) { set('max-height', `${place.maxHeight}px`); set('overflow-y', 'auto') }
    dy = place.top - rect.top
  }
  if (dx || dy) set('translate', `${Math.round(dx)}px ${Math.round(dy)}px`)
  if (styled.length) tip.dataset.touchTipStyled = styled.join(' ')
}

function showBubble(host: HTMLElement, text: string) {
  if (!bubble) {
    bubble = document.createElement('div')
    bubble.className = 'touch-tip-bubble'
    bubble.id = 'touch-tip-bubble'
    bubble.setAttribute('role', 'tooltip')
  }
  if (!bubble.isConnected) document.body.append(bubble)
  bubble.textContent = text
  bubble.style.cssText = `left:${EDGE}px;top:0;max-width:${Math.min(280, window.innerWidth - 2 * EDGE)}px;visibility:hidden`
  const place = placeBeside(host.getBoundingClientRect(), bubble.offsetWidth, bubble.offsetHeight, 'above')
  bubble.style.cssText = `left:${place.left}px;top:${place.top}px;max-width:${Math.min(280, window.innerWidth - 2 * EDGE)}px${place.maxHeight !== null ? `;max-height:${place.maxHeight}px;overflow-y:auto` : ''}`
  host.setAttribute('aria-describedby', bubble.id)
  return bubble
}

export function closeTouchTip() {
  if (!open) return
  const { host, tip } = open
  host.removeAttribute(OPEN_ATTR)
  if (open.bubble) { tip.remove(); if (host.getAttribute('aria-describedby') === tip.id) host.removeAttribute('aria-describedby') }
  else unfitTip(tip)
  open = null
}

// Takes back what fitTip set on a tip.
export function unfitTip(tip: HTMLElement) {
  for (const property of tip.dataset.touchTipStyled?.split(' ') ?? []) tip.style.removeProperty(property)
  delete tip.dataset.touchTipStyled
}

function openTouchTip(found: NonNullable<ReturnType<typeof familyOf>>) {
  closeTouchTip()
  found.host.setAttribute(OPEN_ATTR, '')
  if ('text' in found) open = { host: found.host, tip: showBubble(found.host, found.text!), bubble: true }
  else { open = { host: found.host, tip: found.tip!, bubble: false }; fitTip(found.host, found.tip!) }
}

let start: { x: number; y: number; id: number } | null = null

function onPointerDown(event: PointerEvent) {
  start = event.pointerType === 'mouse' || !isTouchOnly() ? null : { x: event.clientX, y: event.clientY, id: event.pointerId }
}

function onPointerUp(event: PointerEvent) {
  if (!start || start.id !== event.pointerId) return
  const moved = Math.hypot(event.clientX - start.x, event.clientY - start.y) > SCROLL_SLOP
  start = null
  if (moved || !(event.target instanceof Element)) return
  const target = event.target
  // A tap inside the open tip (scrolling its table, reading it) leaves it open.
  if (open && open.tip.contains(target)) return
  const found = familyOf(target)
  if (found && open && open.host === found.host) { closeTouchTip(); return }
  if (found) { openTouchTip(found); return }
  closeTouchTip()
}

function onKeyDown(event: KeyboardEvent) {
  if (event.key === 'Escape' && open) closeTouchTip()
}

function onScroll(event: Event) {
  if (!open) return
  // Scrolling the open tip's own list keeps it open.
  if (event.target instanceof Node && open.tip.contains(event.target)) return
  closeTouchTip()
}

const onResize = () => closeTouchTip()
const onPointerCancel = () => { start = null }

let installed = 0

// Registers the listeners once for the whole app; the returned function removes them (the last caller's call does).
export function installTouchTips() {
  if (installed++ === 0) {
    document.addEventListener('pointerdown', onPointerDown, true)
    document.addEventListener('pointerup', onPointerUp, true)
    document.addEventListener('pointercancel', onPointerCancel, true)
    document.addEventListener('keydown', onKeyDown)
    document.addEventListener('scroll', onScroll, { capture: true, passive: true })
    window.addEventListener('resize', onResize)
  }
  return () => {
    if (--installed > 0) return
    closeTouchTip()
    document.removeEventListener('pointerdown', onPointerDown, true)
    document.removeEventListener('pointerup', onPointerUp, true)
    document.removeEventListener('pointercancel', onPointerCancel, true)
    document.removeEventListener('keydown', onKeyDown)
    document.removeEventListener('scroll', onScroll, true)
    window.removeEventListener('resize', onResize)
  }
}
