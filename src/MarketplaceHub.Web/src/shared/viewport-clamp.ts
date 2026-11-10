// A small menu or card that hangs off its trigger (position: absolute inside a card, a table cell or an image tile, or
// position: fixed) keeps the side its stylesheet gives it, but never runs past the window's edge: on a phone the
// right-hand card's menu would otherwise open half off the screen. clampIntoViewport moves it sideways by rewriting its
// left/right offsets, so EDGE px stay free on each side, and narrows it when the window is narrower than the menu.
// Nothing changes while it fits, so a desktop menu stays where its stylesheet puts it.
//
// The box is measured from its layout (offsetLeft/offsetWidth), not getBoundingClientRect: the menus' opening
// animation (rv-motion-pop, styles/motion.css) scales and translates them, which would skew a measured rect. The
// offsets, not `translate`, are moved for the same reason: that animation runs on `translate`.
export const VIEWPORT_EDGE = 12
const MOVED = 'viewportClamp'

function layoutLeft(element: HTMLElement) {
  const parent = element.offsetParent
  if (!(parent instanceof HTMLElement)) return element.offsetLeft
  return parent.getBoundingClientRect().left + parent.clientLeft - parent.scrollLeft + element.offsetLeft
}

/** Takes back what clampIntoViewport set. */
export function unclampViewport(element: HTMLElement) {
  for (const property of element.dataset[MOVED]?.split(' ') ?? []) element.style.removeProperty(property)
  delete element.dataset[MOVED]
}

export function clampIntoViewport(element: HTMLElement | null, edge = VIEWPORT_EDGE) {
  if (!element) return
  unclampViewport(element)
  const width = element.offsetWidth
  if (width === 0 && element.offsetHeight === 0) return
  const view = document.documentElement.clientWidth || window.innerWidth
  const set: string[] = []
  let shown = width
  if (width > view - 2 * edge) { element.style.setProperty('max-width', `${view - 2 * edge}px`); set.push('max-width'); shown = element.offsetWidth }
  const left = layoutLeft(element)
  const right = left + shown
  const dx = left < edge ? edge - left : right > view - edge ? Math.max(edge - left, view - edge - right) : 0
  if (Math.round(dx) !== 0) {
    const style = getComputedStyle(element)
    const cssLeft = parseFloat(style.left)
    const cssRight = parseFloat(style.right)
    if (Number.isFinite(cssLeft)) { element.style.setProperty('left', `${Math.round(cssLeft + dx)}px`); set.push('left') }
    if (Number.isFinite(cssRight)) { element.style.setProperty('right', `${Math.round(cssRight - dx)}px`); set.push('right') }
  }
  if (set.length) element.dataset[MOVED] = set.join(' ')
}

