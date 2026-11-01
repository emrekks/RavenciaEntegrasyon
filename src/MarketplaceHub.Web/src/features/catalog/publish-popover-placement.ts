// Where a publish card's popover may open: the visible part of the screen above the step's sticky bar (Yayınla) and
// inside the popup's scrolling body. A popover measured against the whole window opened under the bar and was cut off.
export function publishPopoverBounds(anchor: HTMLElement) {
  const anchorRect = anchor.getBoundingClientRect()
  let top = 0
  let bottom = window.innerHeight
  const scroller = anchor.closest('.product-publish-popup-body')
  if (scroller) {
    const box = scroller.getBoundingClientRect()
    top = Math.max(top, box.top)
    bottom = Math.min(bottom, box.bottom)
  }
  // The bar sticks to the bottom: only a bar below the anchor takes room from it.
  const bar = anchor.closest('.product-publish-main, .product-publish-step')?.querySelector('.publish-selection-footer')
  if (bar) {
    const barTop = bar.getBoundingClientRect().top
    if (barTop > anchorRect.top) bottom = Math.min(bottom, barTop)
  }
  return { top, bottom, anchor: anchorRect }
}

const gap = 16
const minimumHeight = 120

// Below when it fits, else above when that fits, else on the roomier side with its height cut to the room (it scrolls).
export function placePublishPopover(anchor: HTMLElement, height: number) {
  const { top, bottom, anchor: rect } = publishPopoverBounds(anchor)
  const below = bottom - rect.bottom - gap
  const above = rect.top - top - gap
  const placement: 'top' | 'bottom' = below >= height ? 'bottom' : above >= height || above > below ? 'top' : 'bottom'
  const room = placement === 'top' ? above : below
  return { placement, maxHeight: height > room ? Math.max(minimumHeight, room) : null }
}
