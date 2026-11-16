import { act } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render } from '../../test/render'
import { useListSelection, type ListSelection } from './useListSelection'

type Row = { id: string; name: string }
let selection: ListSelection<Row>
let seenInRender: string[] = []

function Probe({ filterKey, rows = [], onReset }: { filterKey: string; rows?: Row[]; onReset?: () => void }) {
  selection = useListSelection<Row>({ filterKey, rowOf: id => rows.find(row => row.id === id), onReset })
  seenInRender.push(selection.selectedIds.join(','))
  return null
}

afterEach(() => { cleanup(); seenInRender = [] })

describe('useListSelection', () => {
  it('picks, flips, picks a page and drops what an action removed', () => {
    render(<Probe filterKey="a" />)
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
    const { rerender } = render(<Probe filterKey="a" onReset={onReset} />)
    act(() => selection.set(['1', '2']))
    seenInRender = []
    rerender(<Probe filterKey="b" onReset={onReset} />)
    expect(selection.selectedIds).toEqual([])
    expect(seenInRender.every(ids => ids === '')).toBe(true)
    expect(onReset).toHaveBeenCalled()
  })

  // Owner, 16 Nov 2026 ("Hepsi korusun"): a page change keeps the picks, and a row picked on another page is still a
  // selected row, from the copy taken when it was picked.
  it('keeps the picks across pages with the copy of each row taken when it was picked', () => {
    const pageOne = [{ id: '1', name: 'one' }, { id: '2', name: 'two' }]
    const pageTwo = [{ id: '3', name: 'three' }]
    const { rerender } = render(<Probe filterKey="a" rows={pageOne} />)
    act(() => selection.toggle('1'))
    rerender(<Probe filterKey="a" rows={pageTwo} />)
    act(() => selection.toggle('3'))
    expect(selection.selectedIds).toEqual(['1', '3'])
    expect(selection.selectedRows(pageTwo, row => row.id)).toEqual([{ id: '1', name: 'one' }, { id: '3', name: 'three' }])
    // The row on screen wins over the copy: it is the newer read.
    expect(selection.selectedRows([{ id: '1', name: 'one, renamed' }], row => row.id).map(row => row.name)).toEqual(['one, renamed', 'three'])
    // A dropped pick forgets its copy.
    act(() => selection.toggle('1'))
    rerender(<Probe filterKey="a" rows={[]} />)
    expect(selection.selectedRows([], row => row.id)).toEqual([{ id: '3', name: 'three' }])
  })

  it('selectedVisible leaves out picks that are not on the rows given', () => {
    render(<Probe filterKey="a" />)
    act(() => selection.set(['1', 'gone']))
    expect(selection.selectedVisible([{ id: '1' }, { id: '2' }], item => item.id)).toEqual([{ id: '1' }])
    expect(selection.selectedIds).toEqual(['1', 'gone'])
    // An id picked without a row to copy is no row.
    expect(selection.selectedRows([], row => row.id)).toEqual([])
  })
})
