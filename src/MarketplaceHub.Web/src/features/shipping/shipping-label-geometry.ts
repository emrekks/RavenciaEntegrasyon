import type { ShippingLabelBlockPosition } from './shipping-label'

// The label designer's pointer geometry. A block's position is its upright box in percent of the paper (x and width of
// the paper's width, y and height of its height); a turned block is turned around the centre of that box. Pointer
// movement comes in screen pixels of the paper as it is drawn (its zoom included), so every step below works in those
// pixels and the paper's drawn size, which keeps the math right at any zoom and on a paper that is not square.

export type ShippingLabelResizeEdge = 'n' | 'e' | 's' | 'w'
export type ShippingLabelPaperPx = { width: number; height: number }
export type ShippingLabelPointerDelta = { x: number; y: number }

// A press that moves less than this is a click: it selects the block and never moves it.
export const shippingLabelDragThresholdPx = 4
// Moves and resizes land on half a percent of the paper (0.5 mm on a 100 mm sticker), fine enough that a dragged
// block stays under the pointer.
export const shippingLabelSnapPercent = 0.5
export const shippingLabelMinimumBlockSize = { width: 8, height: 5 }

export function shippingLabelDragStarted(delta: ShippingLabelPointerDelta, threshold = shippingLabelDragThresholdPx) {
  return Math.hypot(delta.x, delta.y) >= threshold
}

const snap = (value: number, step: number) => Math.round(value / step) * step
const tidy = (value: number) => Math.round(value * 1000) / 1000

// The turned block's outline on the paper, in percent of the paper.
export function shippingLabelBlockOutline(position: ShippingLabelBlockPosition, rotation: number, paper: ShippingLabelPaperPx) {
  const radians = (rotation * Math.PI) / 180
  const widthPx = (position.width / 100) * paper.width
  const heightPx = (position.height / 100) * paper.height
  const outlineWidth = (Math.abs(widthPx * Math.cos(radians)) + Math.abs(heightPx * Math.sin(radians))) / paper.width * 100
  const outlineHeight = (Math.abs(widthPx * Math.sin(radians)) + Math.abs(heightPx * Math.cos(radians))) / paper.height * 100
  const centreX = position.x + position.width / 2
  const centreY = position.y + position.height / 2
  return { left: centreX - outlineWidth / 2, top: centreY - outlineHeight / 2, right: centreX + outlineWidth / 2, bottom: centreY + outlineHeight / 2 }
}

function within(value: number, minimum: number, maximum: number) {
  return Math.min(Math.max(minimum, maximum), Math.max(minimum, value))
}

// A drag moves the block by the pointer's movement from where it was pressed, so the spot under the pointer stays
// under it (no jump to a grab offset); the turned outline stays on the paper.
export function draggedShippingLabelPosition(start: ShippingLabelBlockPosition, rotation: number, delta: ShippingLabelPointerDelta, paper: ShippingLabelPaperPx, step = shippingLabelSnapPercent): ShippingLabelBlockPosition {
  const outline = shippingLabelBlockOutline(start, rotation, paper)
  const minX = start.x - outline.left
  const minY = start.y - outline.top
  const x = start.x + snap((delta.x / paper.width) * 100, step)
  const y = start.y + snap((delta.y / paper.height) * 100, step)
  return { ...start, x: tidy(within(x, minX, 100 - start.width - minX)), y: tidy(within(y, minY, 100 - start.height - minY)) }
}

// A resize works in the block's own, turned axes: the pointer's movement is turned back into them, the handle's edge
// moves with it and the opposite edge stays where it is on the paper. On a block turned 90° the handle drawn on the
// right is its own top edge, so dragging it to the right makes the block taller in its own axes, wider on the paper.
export function resizedShippingLabelPosition(start: ShippingLabelBlockPosition, rotation: number, edge: ShippingLabelResizeEdge, delta: ShippingLabelPointerDelta, paper: ShippingLabelPaperPx, step = shippingLabelSnapPercent, minimum = shippingLabelMinimumBlockSize): ShippingLabelBlockPosition {
  const radians = (rotation * Math.PI) / 180
  const cos = Math.cos(radians)
  const sin = Math.sin(radians)
  const ownX = delta.x * cos + delta.y * sin
  const ownY = -delta.x * sin + delta.y * cos
  const horizontal = edge === 'e' || edge === 'w'
  const sign = edge === 'e' || edge === 's' ? 1 : -1
  const axisPx = horizontal ? paper.width : paper.height
  const startSize = horizontal ? start.width : start.height
  const smallest = horizontal ? minimum.width : minimum.height
  const place = (size: number): ShippingLabelBlockPosition => {
    // The centre moves half the growth towards the dragged edge, in the block's axes, then turned onto the paper.
    const half = (sign * ((size - startSize) / 100) * axisPx) / 2
    const ownShiftX = horizontal ? half : 0
    const ownShiftY = horizontal ? 0 : half
    const centreX = ((start.x + start.width / 2) / 100) * paper.width + ownShiftX * cos - ownShiftY * sin
    const centreY = ((start.y + start.height / 2) / 100) * paper.height + ownShiftX * sin + ownShiftY * cos
    const width = horizontal ? size : start.width
    const height = horizontal ? start.height : size
    return { x: tidy((centreX / paper.width) * 100 - width / 2), y: tidy((centreY / paper.height) * 100 - height / 2), width: tidy(width), height: tidy(height) }
  }
  const fits = (position: ShippingLabelBlockPosition) => {
    const outline = shippingLabelBlockOutline(position, rotation, paper)
    return outline.left >= -0.01 && outline.top >= -0.01 && outline.right <= 100.01 && outline.bottom <= 100.01
  }
  const wanted = Math.max(smallest, snap(startSize + (sign * (horizontal ? ownX : ownY) / axisPx) * 100, step))
  const next = place(wanted)
  if (fits(next) || wanted <= startSize || !fits(place(startSize))) return next
  // Grows only as far as the paper allows: the largest size between the start and the wanted one that still fits.
  let low = startSize
  let high = wanted
  for (let index = 0; index < 30; index++) {
    const middle = (low + high) / 2
    if (fits(place(middle))) low = middle
    else high = middle
  }
  return place(Math.max(startSize, Math.floor(low / step) * step))
}
