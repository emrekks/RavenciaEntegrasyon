import { useState, type SetStateAction } from 'react'

export type ListSelectionOptions = {
  /** Everything that makes it another list: filters, search, tab, sort, page size. A change clears the selection. */
  filterKey: string
  /** The page shown. */
  page: number
  /**
   * true: the rows picked stay picked across the pages of the same list, and a bulk action works on all of them
   * (Ürünler). false: a page change clears the selection too (Siparişler, Faturalar, İadeler). The pages differ on purpose
   * until the owner decides (src/test/list-selection-rules.ts).
   */
  keepAcrossPages: boolean
  /** Runs with the clear, in the same render, for state kept beside the ids (Ürünler's copies of the picked cards). */
  onReset?: () => void
}

export type ListSelection = {
  /** The picked ids in the order they were picked; ids of rows no longer shown stay until the list changes. */
  selectedIds: string[]
  has: (id: string) => boolean
  /** Picks or drops one row; without `checked` it flips it. */
  toggle: (id: string, checked?: boolean) => void
  /** Picks or drops every id given (a page's "select all"), leaving the other picks alone. */
  toggleMany: (ids: string[], checked: boolean) => void
  set: (next: SetStateAction<string[]>) => void
  /** Drops ids a finished action took off the list. */
  remove: (ids: Iterable<string>) => void
  clear: () => void
  /** The rows of `items` that are picked: what a bulk bar counts on the lists that count only the rows on screen. */
  selectedVisible: <T>(items: readonly T[], idOf: (item: T) => string) => T[]
}

type SelectionState = { ids: string[]; key: string }

/**
 * One list's row selection with the owner's rule: another filter, search, tab, sort or page size clears it, and a page
 * change clears it unless keepAcrossPages. The clear happens in the render that shows the other list, so a bulk action
 * can never reach rows of the list before.
 */
export function useListSelection({ filterKey, page, keepAcrossPages, onReset }: ListSelectionOptions): ListSelection {
  const key = keepAcrossPages ? filterKey : `${filterKey}\u0000page ${page}`
  const [state, setState] = useState<SelectionState>(() => ({ ids: [], key }))
  let ids = state.ids
  if (state.key !== key) {
    ids = []
    setState({ ids: [], key })
    onReset?.()
  }
  const picked = new Set(ids)
  const update = (change: (current: string[]) => string[]) => setState(previous => ({ ...previous, ids: change(previous.ids) }))

  return {
    selectedIds: ids,
    has: id => picked.has(id),
    toggle: (id, checked) => update(current => {
      const on = checked ?? !current.includes(id)
      return on ? (current.includes(id) ? current : [...current, id]) : current.filter(value => value !== id)
    }),
    toggleMany: (many, checked) => update(current => checked ? Array.from(new Set([...current, ...many])) : current.filter(id => !many.includes(id))),
    set: next => update(current => typeof next === 'function' ? next(current) : next),
    remove: gone => {
      const drop = new Set(gone)
      update(current => current.some(id => drop.has(id)) ? current.filter(id => !drop.has(id)) : current)
    },
    clear: () => update(current => current.length ? [] : current),
    selectedVisible: (items, idOf) => items.filter(item => picked.has(idOf(item)))
  }
}
