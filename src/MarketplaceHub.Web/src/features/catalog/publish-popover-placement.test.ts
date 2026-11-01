import { afterEach, describe, expect, it } from 'vitest'
import { placePublishPopover, publishPopoverBounds } from './publish-popover-placement'

const rect = (top: number, bottom: number) => ({ top, bottom, left: 0, right: 100, width: 100, height: bottom - top, x: 0, y: top, toJSON: () => ({}) }) as DOMRect

// The publish step: a card's status badge, and the sticky Yayınla bar at the bottom of a 900 px window.
function layout(badge: [number, number], barTop: number | null, popup?: [number, number]) {
  document.body.innerHTML = `<div class="${popup ? 'product-publish-popup-body' : ''}"><div class="product-publish-main"><span class="badge"></span>${barTop === null ? '' : '<div class="publish-selection-footer"></div>'}</div></div>`
  Object.defineProperty(window, 'innerHeight', { configurable: true, value: 900 })
  const anchor = document.querySelector<HTMLElement>('.badge')!
  anchor.getBoundingClientRect = () => rect(...badge)
  const bar = document.querySelector<HTMLElement>('.publish-selection-footer')
  if (bar && barTop !== null) bar.getBoundingClientRect = () => rect(barTop, barTop + 56)
  const body = document.querySelector<HTMLElement>('.product-publish-popup-body')
  if (body && popup) body.getBoundingClientRect = () => rect(...popup)
  return anchor
}

afterEach(() => { document.body.innerHTML = '' })

describe('publish popover placement', () => {
  it('ends the room below at the sticky bar, not at the window edge', () => {
    const anchor = layout([600, 620], 844)
    expect(publishPopoverBounds(anchor).bottom).toBe(844)
    // 844 - 620 - 16 = 208 px below: a 240 px popover opens above instead of under the bar.
    expect(placePublishPopover(anchor, 240)).toEqual({ placement: 'top', maxHeight: null })
    expect(placePublishPopover(anchor, 180)).toEqual({ placement: 'bottom', maxHeight: null })
  })

  it('keeps to the popup body and cuts a popover too tall for either side', () => {
    const anchor = layout([300, 320], 560, [120, 616])
    // Above: 300 - 120 - 16 = 164; below: 560 - 320 - 16 = 224. A 400 px list opens below, cut to 224 px.
    expect(placePublishPopover(anchor, 400)).toEqual({ placement: 'bottom', maxHeight: 224 })
  })

  it('ignores a bar above the badge', () => {
    const anchor = layout([600, 620], 100)
    expect(publishPopoverBounds(anchor).bottom).toBe(900)
  })
})
