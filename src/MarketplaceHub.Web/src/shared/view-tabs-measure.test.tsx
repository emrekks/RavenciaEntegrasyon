import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render } from '../test/render'
import { ViewTabsWithMore } from './view-tabs'

const items = () => ['a', 'b', 'c', 'd', 'e'].map(value => ({ value, label: value.toUpperCase(), count: 3 }))

afterEach(() => { cleanup(); vi.restoreAllMocks(); localStorage.clear() })

describe('ViewTabsWithMore measuring', () => {
  // Reading the row's width lays out the whole page first (~250 ms on Siparişler, 4x slowed CPU). A render that shows the
  // same tabs (a refetch, a row's state, a new items array with the same content) must not read it again.
  it('reads the row once for the same tabs, however often the page renders', () => {
    const reads = vi.fn(() => 300)
    vi.spyOn(HTMLElement.prototype, 'scrollWidth', 'get').mockImplementation(reads)
    vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockImplementation(() => 300)
    const view = render(<ViewTabsWithMore items={items()} more={[]} value="a" onChange={() => undefined} ariaLabel="Ölçüm" />)
    const first = reads.mock.calls.length
    expect(first).toBeGreaterThan(0)
    for (let index = 0; index < 5; index += 1) view.rerender(<ViewTabsWithMore items={items()} more={[]} value="a" onChange={() => undefined} ariaLabel="Ölçüm" />)
    expect(reads.mock.calls.length).toBe(first)
    view.rerender(<ViewTabsWithMore items={items()} more={[]} value="b" onChange={() => undefined} ariaLabel="Ölçüm" />)
    expect(reads.mock.calls.length).toBeGreaterThan(first)
  })

  // The tabs that do not fit leave together from one reading, last first, never the first or the selected one; the Diğer
  // button that appears then takes room too, and the result is the one moving a tab per reading gave.
  it('moves every tab that does not fit into Diğer at once', () => {
    // Every tab (Diğer too) is 100 px wide; the row has room for 250.
    vi.spyOn(HTMLElement.prototype, 'scrollWidth', 'get').mockImplementation(function (this: HTMLElement) { return this.getAttribute('role') === 'tablist' ? this.querySelectorAll(':scope > [role=tab]').length * 100 : 0 })
    vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockImplementation(function (this: HTMLElement) { return this.getAttribute('role') === 'tablist' ? 250 : 0 })
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(() => ({ width: 100, height: 36, top: 0, left: 0, right: 100, bottom: 36, x: 0, y: 0, toJSON: () => ({}) }))
    render(<ViewTabsWithMore items={items()} more={[]} value="a" onChange={() => undefined} ariaLabel="Ölçüm" />)
    expect([...document.querySelectorAll('[role=tablist] > [role=tab]:not(.rv-view-tabs-more)')].map(tab => tab.textContent)).toEqual(['A3'])
    expect(document.querySelector('.rv-view-tabs-more')).not.toBeNull()
  })
})
