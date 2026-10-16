// Scrollbars stay out of sight until their area is scrolled or the pointer rests on the bar itself. While scrolling
// the scrolled element carries `is-scrolling` for a moment; the element whose bar is under the pointer carries
// `is-scrollbar-hover`. The styles in design-system.css only paint the thumb for these two classes.
const scrollingClass = 'is-scrolling'
const hoverClass = 'is-scrollbar-hover'
const visibleAfterScrollMs = 900

function overflowScrolls(value: string) {
  return value === 'auto' || value === 'scroll' || value === 'overlay'
}

// The element whose scrollbar is under the point, if any.
function scrollbarAt(x: number, y: number): Element | null {
  const root = document.documentElement
  if (root.scrollHeight > root.clientHeight && x >= root.clientWidth) return root
  if (root.scrollWidth > root.clientWidth && y >= root.clientHeight) return root
  let element = document.elementFromPoint(x, y)
  while (element && element !== document.body && element !== root) {
    if (element instanceof HTMLElement) {
      const verticalBar = element.offsetWidth - element.clientWidth - element.clientLeft * 2
      const horizontalBar = element.offsetHeight - element.clientHeight - element.clientTop * 2
      if (verticalBar > 0 || horizontalBar > 0) {
        const style = getComputedStyle(element)
        const rect = element.getBoundingClientRect()
        if (verticalBar > 0 && overflowScrolls(style.overflowY) && x >= rect.left + element.clientLeft + element.clientWidth && x <= rect.right) return element
        if (horizontalBar > 0 && overflowScrolls(style.overflowX) && y >= rect.top + element.clientTop + element.clientHeight && y <= rect.bottom) return element
      }
    }
    element = element.parentElement
  }
  return null
}

export function installScrollbarVisibility() {
  const timers = new WeakMap<Element, number>()
  document.addEventListener('scroll', event => {
    const target = event.target === document ? document.documentElement : event.target
    if (!(target instanceof Element)) return
    target.classList.add(scrollingClass)
    window.clearTimeout(timers.get(target))
    timers.set(target, window.setTimeout(() => target.classList.remove(scrollingClass), visibleAfterScrollMs))
  }, { capture: true, passive: true })

  let hovered: Element | null = null
  let frame = 0
  let pointer = { x: 0, y: 0 }
  const setHovered = (next: Element | null) => {
    if (next === hovered) return
    hovered?.classList.remove(hoverClass)
    next?.classList.add(hoverClass)
    hovered = next
  }
  document.addEventListener('pointermove', event => {
    pointer = { x: event.clientX, y: event.clientY }
    if (frame) return
    frame = window.requestAnimationFrame(() => {
      frame = 0
      setHovered(scrollbarAt(pointer.x, pointer.y))
    })
  }, { passive: true })
  document.documentElement.addEventListener('pointerleave', () => setHovered(null))
}
