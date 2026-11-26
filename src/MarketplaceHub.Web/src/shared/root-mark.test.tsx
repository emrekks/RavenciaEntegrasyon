import { act, useState } from 'react'
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, flush, render } from '../test/render'
import { holdRootMark, useRootMark, useTextEntryFocusMark } from './root-mark'

const root = document.documentElement
afterEach(() => { cleanup(); for (const name of ['data-test-mark', 'data-test-typing']) root.removeAttribute(name) })

function Owner({ value }: { value: string | null }) {
  useRootMark('data-test-mark', value)
  return null
}

describe('root marks (html[data-…] in place of a :has() on a high element)', () => {
  it('is set while its owner holds it and cleared when the owner lets go or goes away', () => {
    const { rerender, unmount } = render(<Owner value="task" />)
    expect(root.getAttribute('data-test-mark')).toBe('task')
    rerender(<Owner value={null} />)
    expect(root.hasAttribute('data-test-mark')).toBe(false)
    rerender(<Owner value="2" />)
    expect(root.getAttribute('data-test-mark')).toBe('2')
    unmount()
    expect(root.hasAttribute('data-test-mark')).toBe(false)
  })

  it('counts its owners: overlapping holds keep the mark until the last one goes, in either order, and the latest value wins', () => {
    const first = holdRootMark('data-test-mark')
    const second = holdRootMark('data-test-mark', 'x')
    expect(root.getAttribute('data-test-mark')).toBe('x')
    second()
    expect(root.getAttribute('data-test-mark')).toBe('')
    second()
    expect(root.hasAttribute('data-test-mark')).toBe(true)
    first()
    expect(root.hasAttribute('data-test-mark')).toBe(false)

    const a = holdRootMark('data-test-mark')
    const b = holdRootMark('data-test-mark')
    a()
    expect(root.hasAttribute('data-test-mark')).toBe(true)
    b()
    expect(root.hasAttribute('data-test-mark')).toBe(false)
  })

  it('keeps the mark while two components hold it and one of them unmounts', () => {
    function Pair() {
      const [both, setBoth] = useState(true)
      return <><Owner value="" />{both ? <Owner value="" /> : null}<button type="button" onClick={() => setBoth(false)}>drop</button></>
    }
    const { host, unmount } = render(<Pair />)
    expect(root.hasAttribute('data-test-mark')).toBe(true)
    act(() => host.querySelector('button')!.click())
    expect(root.hasAttribute('data-test-mark')).toBe(true)
    unmount()
    expect(root.hasAttribute('data-test-mark')).toBe(false)
  })
})

describe('text entry focus mark (the phone keyboard)', () => {
  function Typing() {
    useTextEntryFocusMark('data-test-typing')
    return null
  }
  const field = (html: string) => { const box = document.createElement('div'); box.innerHTML = html; document.body.append(box); return box.firstElementChild as HTMLElement }

  it('is set while a text box, text area, list box or editable area has focus and cleared when the focus leaves', () => {
    const elements = [field('<input type="text">'), field('<input type="search">'), field('<input>'), field('<textarea></textarea>'), field('<select><option>a</option></select>'), field('<div contenteditable="true"></div>')]
    render(<Typing />)
    for (const element of elements) {
      act(() => element.focus())
      expect(root.hasAttribute('data-test-typing')).toBe(true)
      act(() => element.blur())
      expect(root.hasAttribute('data-test-typing')).toBe(false)
    }
    elements.forEach(element => element.parentElement!.remove())
  })

  it('is not set for a check box, radio, button or submit input and moves straight from one text box to another', () => {
    const box = field('<input type="checkbox">')
    const radio = field('<input type="radio">')
    const button = field('<button type="button">b</button>')
    const one = field('<input type="text">')
    const two = field('<input type="text">')
    render(<Typing />)
    for (const element of [box, radio, button]) { act(() => element.focus()); expect(root.hasAttribute('data-test-typing')).toBe(false) }
    act(() => one.focus())
    act(() => two.focus())
    expect(root.hasAttribute('data-test-typing')).toBe(true)
    act(() => button.focus())
    expect(root.hasAttribute('data-test-typing')).toBe(false)
    ;[box, radio, button, one, two].forEach(element => element.parentElement!.remove())
  })

  it('is set on mount when a text box already has focus, clears when the focused box is removed, and goes with the component', async () => {
    const input = field('<input type="text">')
    input.focus()
    const { unmount } = render(<Typing />)
    expect(root.hasAttribute('data-test-typing')).toBe(true)
    act(() => input.parentElement!.remove())
    await flush()
    expect(root.hasAttribute('data-test-typing')).toBe(false)
    const again = field('<input type="text">')
    act(() => again.focus())
    expect(root.hasAttribute('data-test-typing')).toBe(true)
    unmount()
    expect(root.hasAttribute('data-test-typing')).toBe(false)
    again.parentElement!.remove()
  })
})
