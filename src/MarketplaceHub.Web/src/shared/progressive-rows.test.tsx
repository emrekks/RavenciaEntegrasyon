import { act } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render } from '../test/render'
import { firstFrameRows, PendingRows, useProgressiveRows } from './progressive-rows'

const rows = (prefix: string, count = 50) => Array.from({ length: count }, (_, index) => ({ id: `${prefix}${index}` }))

function List({ items, remountKey }: { items: Array<{ id: string }>; remountKey?: string }) {
  const progressive = useProgressiveRows(items, item => item.id, remountKey)
  return <table><tbody ref={progressive.containerRef}>{progressive.rows.map(item => <tr key={item.id} className="row"><td>{item.id}</td></tr>)}<PendingRows height={progressive.pendingHeight} colSpan={1} /></tbody></table>
}

const shown = () => document.querySelectorAll('tr.row').length
const frames: FrameRequestCallback[] = []
async function afterPaint() {
  await act(async () => {
    frames.splice(0).forEach(callback => callback(performance.now()))
    await new Promise(resolve => setTimeout(resolve, 0))
  })
}

afterEach(() => { cleanup(); vi.restoreAllMocks(); Object.defineProperty(window, 'scrollY', { configurable: true, value: 0 }) })

describe('useProgressiveRows', () => {
  // Drawing 50 order rows in the frame of a tab change kept it ~1 s on a 4x slowed phone; the first rows fill the screen.
  it('draws the first rows of a new list in its first frame and the rest once that frame is on screen', async () => {
    vi.spyOn(window, 'requestAnimationFrame').mockImplementation(callback => { frames.push(callback); return frames.length })
    const view = render(<List items={rows('a', 5)} />)
    expect(shown()).toBe(5)
    view.rerender(<List items={rows('b')} />)
    expect(shown()).toBe(firstFrameRows)
    await afterPaint()
    expect(shown()).toBe(50)
    expect(document.querySelector('.rv-rows-pending')).toBeNull()
    // The same rows again (a refetch): nothing leaves the screen.
    view.rerender(<List items={rows('b')} />)
    expect(shown()).toBe(50)
  })

  it('keeps the page as long as before with a spacer of the rows still to come', async () => {
    vi.spyOn(window, 'requestAnimationFrame').mockImplementation(callback => { frames.push(callback); return frames.length })
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(function (this: HTMLElement) { const height = this.tagName === 'TBODY' ? this.querySelectorAll('tr.row').length * 40 : 0; return { height, width: 0, top: 0, left: 0, right: 0, bottom: height, x: 0, y: 0, toJSON: () => ({}) } })
    const view = render(<List items={rows('a')} />)
    await afterPaint()
    await afterPaint()
    view.rerender(<List items={rows('b')} />)
    expect(shown()).toBe(firstFrameRows)
    expect(document.querySelector<HTMLElement>('.rv-rows-pending td')?.style.height).toBe(`${(50 - firstFrameRows) * 40}px`)
  })

  // A status tab mounts the previous tab's rows again while its own load (Siparişler, İadeler): they are drawn like a new
  // list, even from a scrolled page (the tab change takes it to its top).
  it('draws the same rows again progressively when their holder is mounted again', async () => {
    vi.spyOn(window, 'requestAnimationFrame').mockImplementation(callback => { frames.push(callback); return frames.length })
    const view = render(<List items={rows('a')} remountKey="tab-0" />)
    await afterPaint()
    expect(shown()).toBe(50)
    Object.defineProperty(window, 'scrollY', { configurable: true, value: 900 })
    view.rerender(<List items={rows('a')} remountKey="tab-1" />)
    expect(shown()).toBe(firstFrameRows)
    await afterPaint()
    expect(shown()).toBe(50)
  })

  // A new order arriving on top while the person reads row 40: the list is drawn whole, nothing disappears for a frame.
  it('draws the whole list at once when the page is scrolled down', () => {
    const view = render(<List items={rows('a')} />)
    Object.defineProperty(window, 'scrollY', { configurable: true, value: 900 })
    view.rerender(<List items={rows('b')} />)
    expect(shown()).toBe(50)
  })
})
