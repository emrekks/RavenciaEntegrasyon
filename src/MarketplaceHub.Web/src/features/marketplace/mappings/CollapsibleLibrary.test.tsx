import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { cleanup, click, render } from '../../../test/render'
import { CollapsibleLibrary, libraryOpenStorageKey } from './CollapsibleLibrary'

const toggleOf = (host: HTMLElement) => host.querySelector<HTMLButtonElement>('.library-collapse-toggle')!
const view = (props: { count?: number; forceOpen?: boolean; openSignal?: number } = {}) => <CollapsibleLibrary title="Kategori Listesi" count={props.count ?? 3} forceOpen={props.forceOpen} openSignal={props.openSignal}><p>içerik</p></CollapsibleLibrary>

describe('CollapsibleLibrary', () => {
  beforeEach(() => localStorage.clear())
  afterEach(cleanup)

  it('is closed by default and shows the count', () => {
    const { host } = render(view({ count: 1234 }))
    expect(toggleOf(host).textContent).toContain('Kategori Listesi (1.234)')
    expect(toggleOf(host).getAttribute('aria-expanded')).toBe('false')
    expect(host.querySelector('.library-collapse-region')?.hasAttribute('inert')).toBe(true)
  })

  it('opens and closes on click and remembers the choice', () => {
    const first = render(view())
    click(toggleOf(first.host))
    expect(toggleOf(first.host).getAttribute('aria-expanded')).toBe('true')
    expect(first.host.querySelector('.library-collapse-region')?.hasAttribute('inert')).toBe(false)
    expect(localStorage.getItem(libraryOpenStorageKey)).toBe('1')
    first.unmount()
    const second = render(view())
    expect(toggleOf(second.host).getAttribute('aria-expanded')).toBe('true')
    click(toggleOf(second.host))
    expect(toggleOf(second.host).getAttribute('aria-expanded')).toBe('false')
    expect(localStorage.getItem(libraryOpenStorageKey)).toBe('0')
  })

  it('opens by itself for a search or a just-added entry', () => {
    const { host, rerender } = render(view())
    rerender(view({ forceOpen: true }))
    expect(toggleOf(host).getAttribute('aria-expanded')).toBe('true')
    click(toggleOf(host))
    expect(toggleOf(host).getAttribute('aria-expanded')).toBe('false')
    rerender(view({ openSignal: 1 }))
    expect(toggleOf(host).getAttribute('aria-expanded')).toBe('true')
  })

  it('stays closed and does not throw when storage is blocked', () => {
    const original = Storage.prototype.getItem
    Storage.prototype.getItem = () => { throw new Error('blocked') }
    try {
      const { host } = render(view({ count: 0 }))
      expect(toggleOf(host).getAttribute('aria-expanded')).toBe('false')
    } finally { Storage.prototype.getItem = original }
  })
})
