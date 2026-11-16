import { act } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render } from '../../test/render'
import { useListSelection, type ListSelection } from './useListSelection'

let selection: ListSelection
let seenInRender: string[] = []

function Probe({ filterKey, page, keepAcrossPages, onReset }: { filterKey: string; page: number; keepAcrossPages: boolean; onReset?: () => void }) {
  selection = useListSelection({ filterKey, page, keepAcrossPages, onReset })
  seenInRender.push(selection.selectedIds.join(','))
  return null
}

afterEach(() => { cleanup(); seenInRender = [] })

describe('useListSelection', () => {
  it('picks, flips, picks a page and drops what an action removed', () => {
    render(<Probe filterKey="a" page={1} keepAcrossPages={false} />)
    act(() => selection.toggle('1'))
    act(() => selection.toggle('2', true))
    act(() => selection.toggle('2', true))
    expect(selection.selectedIds).toEqual(['1', '2'])
    act(() => selection.toggle('1'))
    expect(selection.has('1')).toBe(false)
    act(() => selection.toggleMany(['2', '3', '4'], true))
    expect(selection.selectedIds).toEqual(['2', '3', '4'])
    act(() => selection.toggleMany(['3'], false))
    act(() => selection.remove(new Set(['4'])))
    expect(selection.selectedIds).toEqual(['2'])
    act(() => selection.clear())
    expect(selection.selectedIds).toEqual([])
  })

  it('a filter change clears it before the other list is drawn, and runs onReset with it', () => {
    const onReset = vi.fn()
    const { rerender } = render(<Probe filterKey="a" page={1} keepAcrossPages onReset={onReset} />)
    act(() => selection.set(['1', '2']))
    seenInRender = []
    rerender(<Probe filterKey="b" page={1} keepAcrossPages onReset={onReset} />)
    expect(selection.selectedIds).toEqual([])
    expect(seenInRender.every(ids => ids === '')).toBe(true)
    expect(onReset).toHaveBeenCalled()
  })

  it('keeps the picks across pages only when asked to', () => {
    const { rerender } = render(<Probe filterKey="a" page={1} keepAcrossPages />)
    act(() => selection.set(['1']))
    rerender(<Probe filterKey="a" page={2} keepAcrossPages />)
    expect(selection.selectedIds).toEqual(['1'])
    cleanup()
    const other = render(<Probe filterKey="a" page={1} keepAcrossPages={false} />)
    act(() => selection.set(['1']))
    other.rerender(<Probe filterKey="a" page={2} keepAcrossPages={false} />)
    expect(selection.selectedIds).toEqual([])
  })

  it('selectedVisible leaves out picks that are not on the rows given', () => {
    render(<Probe filterKey="a" page={1} keepAcrossPages={false} />)
    act(() => selection.set(['1', 'gone']))
    expect(selection.selectedVisible([{ id: '1' }, { id: '2' }], item => item.id)).toEqual([{ id: '1' }])
    expect(selection.selectedIds).toEqual(['1', 'gone'])
  })
})
