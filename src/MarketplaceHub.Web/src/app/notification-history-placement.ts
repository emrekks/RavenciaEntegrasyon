// Where the notification history panel sits: under the bell, right edges aligned, kept 16 px inside the window.
export type NotificationHistoryPlacement = { top: number; left: number; width: number }

export function notificationHistoryPlacement(anchor: Pick<DOMRect, 'right' | 'bottom'>, viewportWidth: number): NotificationHistoryPlacement {
  const width = Math.min(400, viewportWidth - 32)
  const left = Math.max(16, Math.min(anchor.right - width, viewportWidth - width - 16))
  return { top: anchor.bottom + 10, left, width }
}

// Follows the bell while the panel is open. The position is written on the panel's own style, not kept in React state:
// as state, every scroll event of the page re-rendered the whole app with the open page under it (a 33-variant product
// editor spent ~90 ms per scroll event). Scroll and resize are read once per frame and the style is only touched
// when the place really changed. Placed once at once, so the panel never paints at its default spot.
export function followNotificationBell(anchor: () => HTMLElement | null, panel: () => HTMLElement | null) {
  let last = ''
  let frame = 0
  const place = () => {
    frame = 0
    const bell = anchor()
    const target = panel()
    if (!bell || !target) return
    // A phone opens it as a bottom sheet (notifications.css): no place of its own.
    if (window.innerWidth <= 760) { if (last !== 'sheet') { target.style.top = ''; target.style.left = ''; target.style.width = ''; last = 'sheet' } return }
    const { top, left, width } = notificationHistoryPlacement(bell.getBoundingClientRect(), window.innerWidth)
    const key = `${top}|${left}|${width}`
    if (key === last) return
    last = key
    target.style.top = `${top}px`
    target.style.left = `${left}px`
    target.style.width = `${width}px`
  }
  const schedule = () => { if (!frame) frame = window.requestAnimationFrame(place) }
  place()
  window.addEventListener('resize', schedule)
  window.addEventListener('scroll', schedule, { capture: true, passive: true })
  return () => {
    if (frame) window.cancelAnimationFrame(frame)
    window.removeEventListener('resize', schedule)
    window.removeEventListener('scroll', schedule, true)
  }
}
