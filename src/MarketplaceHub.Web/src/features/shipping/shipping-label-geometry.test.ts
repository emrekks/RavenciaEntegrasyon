import { describe, expect, it } from 'vitest'
import { draggedShippingLabelPosition, resizedShippingLabelPosition, shippingLabelBlockOutline, shippingLabelDragStarted } from './shipping-label-geometry'

// A 100 x 100 mm sticker drawn 400 px wide: 4 px a percent.
const paper = { width: 400, height: 400 }

describe('label designer: click or drag', () => {
  it('a press that moves less than the threshold is a click, not a drag', () => {
    expect(shippingLabelDragStarted({ x: 0, y: 0 })).toBe(false)
    expect(shippingLabelDragStarted({ x: 2, y: 2 })).toBe(false)
    expect(shippingLabelDragStarted({ x: 3, y: 3 })).toBe(true)
    expect(shippingLabelDragStarted({ x: 0, y: -4 })).toBe(true)
  })

  it('moves the block by the pointer movement, so the grabbed spot stays under the pointer', () => {
    const start = { x: 22.5, y: 5, width: 47.5, height: 12.5 }
    // no movement: exactly where it was (an off-grid start is not snapped)
    expect(draggedShippingLabelPosition(start, 0, { x: 0, y: 0 }, paper)).toEqual(start)
    expect(draggedShippingLabelPosition(start, 0, { x: 40, y: 20 }, paper)).toEqual({ ...start, x: 32.5, y: 10 })
    // the paper's zoom does not matter: half the drawn size, half the pixels, the same move
    expect(draggedShippingLabelPosition(start, 0, { x: 20, y: 10 }, { width: 200, height: 200 })).toEqual({ ...start, x: 32.5, y: 10 })
  })

  it('keeps a turned block on the paper by its turned outline', () => {
    // 49 wide, 19 high, turned 90°: on the paper 19 wide and 49 high
    const start = { x: 63, y: 36, width: 49, height: 19 }
    const right = draggedShippingLabelPosition(start, 90, { x: 400, y: 0 }, paper)
    expect(shippingLabelBlockOutline(right, 90, paper).right).toBeCloseTo(100)
    const bottom = draggedShippingLabelPosition(start, 90, { x: 0, y: 400 }, paper)
    expect(shippingLabelBlockOutline(bottom, 90, paper).bottom).toBeCloseTo(100)
  })
})

describe('label designer: resize in the block’s own axes', () => {
  it('an upright block grows from the dragged edge and keeps the opposite one', () => {
    const start = { x: 10, y: 10, width: 40, height: 20 }
    expect(resizedShippingLabelPosition(start, 0, 'e', { x: 40, y: 0 }, paper)).toEqual({ x: 10, y: 10, width: 50, height: 20 })
    expect(resizedShippingLabelPosition(start, 0, 'w', { x: -20, y: 0 }, paper)).toEqual({ x: 5, y: 10, width: 45, height: 20 })
    expect(resizedShippingLabelPosition(start, 0, 'n', { x: 0, y: 20 }, paper)).toEqual({ x: 10, y: 15, width: 40, height: 15 })
  })

  it('a block turned 90° grows on the paper as its visible edge is dragged, its opposite edge staying put', () => {
    const start = { x: 50, y: 36, width: 49, height: 19 }
    const before = shippingLabelBlockOutline(start, 90, paper)
    // Turned 90° clockwise, the block's own top edge (n) is drawn on the right. Dragging it 20 px (5%) to the left
    // narrows it; to the right widens it.
    expect(shippingLabelBlockOutline(resizedShippingLabelPosition(start, 90, 'n', { x: -20, y: 0 }, paper), 90, paper).right).toBeCloseTo(before.right - 5)
    const grown = resizedShippingLabelPosition(start, 90, 'n', { x: 20, y: 0 }, paper)
    const after = shippingLabelBlockOutline(grown, 90, paper)
    expect(grown.height).toBeCloseTo(24)
    expect(after.right).toBeCloseTo(before.right + 5)
    expect(after.left).toBeCloseTo(before.left)
    expect(after.top).toBeCloseTo(before.top)
    expect(after.bottom).toBeCloseTo(before.bottom)
    // Its own right edge (e) is drawn at the bottom: dragging down makes it longer, the top stays.
    const longer = shippingLabelBlockOutline(resizedShippingLabelPosition(start, 90, 'e', { x: 0, y: 20 }, paper), 90, paper)
    expect(longer.bottom).toBeCloseTo(before.bottom + 5)
    expect(longer.top).toBeCloseTo(before.top)
    expect(longer.left).toBeCloseTo(before.left)
    // A horizontal pull on that bottom edge does nothing to its length.
    expect(resizedShippingLabelPosition(start, 90, 'e', { x: 40, y: 0 }, paper)).toEqual(start)
  })

  it('a block turned 270° maps its edges the other way round', () => {
    const start = { x: 30, y: 30, width: 40, height: 20 }
    const before = shippingLabelBlockOutline(start, 270, paper)
    // Turned 270°, the block's own top edge (n) is drawn on the left: dragging it left widens the block on the paper.
    const after = shippingLabelBlockOutline(resizedShippingLabelPosition(start, 270, 'n', { x: -20, y: 0 }, paper), 270, paper)
    expect(after.left).toBeCloseTo(before.left - 5)
    expect(after.right).toBeCloseTo(before.right)
  })

  it('stops at the minimum size and at the paper edge', () => {
    const start = { x: 10, y: 10, width: 40, height: 20 }
    expect(resizedShippingLabelPosition(start, 0, 'e', { x: -400, y: 0 }, paper).width).toBe(8)
    expect(resizedShippingLabelPosition(start, 0, 's', { x: 0, y: -400 }, paper).height).toBe(5)
    expect(resizedShippingLabelPosition(start, 0, 'e', { x: 1000, y: 0 }, paper)).toEqual({ x: 10, y: 10, width: 90, height: 20 })
    const turned = { x: 63, y: 36, width: 49, height: 19 }
    const grown = resizedShippingLabelPosition(turned, 90, 'e', { x: 0, y: 1000 }, paper)
    expect(shippingLabelBlockOutline(grown, 90, paper).bottom).toBeLessThanOrEqual(100.01)
    expect(shippingLabelBlockOutline(grown, 90, paper).top).toBeCloseTo(shippingLabelBlockOutline(turned, 90, paper).top)
  })
})
