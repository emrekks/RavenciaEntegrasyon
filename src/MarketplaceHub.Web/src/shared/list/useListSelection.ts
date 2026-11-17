import { useState, type SetStateAction } from 'react'

export type ListSelectionOptions<T> = {
  /** Everything that makes it another list: filters, search, tab, sort, page size. A change clears the selection. */
  filterKey: string
  /**
   * The row of an id as the page shows it now. A row is copied when it is picked, so a pick on another page still has
   * what the bulk bar counts and a bulk action needs (selectedRows). Without it only the ids are kept.
   */
  rowOf?: (id: string) => T | undefined
  /** Runs with the clear, in the same render, for state kept beside the ids (Ürünler's copies of the picked cards). */
  onReset?: () => void
}

export type ListSelection<T = never> = {
  /** The picked ids in the order they were picked, on every page of the list, until the list changes. */
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
  /** The rows of `items` that are picked (the rows on screen). */
  selectedVisible: <R>(items: readonly R[], idOf: (item: R) => string) => R[]
  /**
   * Every picked row in pick order: the row as `items` (the page on screen) has it now, else the copy taken when it was
   * picked on another page. What a bulk bar counts and a bulk action works on (owner, 16 Nov 2026: "Hepsi korusun").
   */
  selectedRows: (items: readonly T[], idOf: (item: T) => string) => T[]
}

type SelectionState<T> = { ids: string[]; key: string; copies: Record<string, T> }

/**
 * One list's row selection with the owner's rule (WEB-SAYFALAR-11, 16 Nov 2026, on all four lists): another filter,
 * search, tab, sort or page size clears it; a page change keeps it, and the rows picked on other pages still count. The
 * clear happens in the render that shows the other list, so a bulk action can never reach rows of the list before.
 */
export function useListSelection<T = never>({ filterKey, rowOf, onReset }: ListSelectionOptions<T>): ListSelection<T> {
  const [state, setState] = useState<SelectionState<T>>(() => ({ ids: [], key: filterKey, copies: {} }))
  let ids = state.ids
  let copies = state.copies
  if (state.key !== filterKey) {
    ids = []
    copies = {}
    setState({ ids: [], key: filterKey, copies: {} })
    onReset?.()
  }
  const picked = new Set(ids)
  const update = (change: (current: string[]) => string[]) => setState(previous => {
    const next = change(previous.ids)
    if (next === previous.ids) return previous
    const kept: Record<string, T> = {}
    for (const id of next) {
      const copy = previous.copies[id] ?? rowOf?.(id)
      if (copy !== undefined) kept[id] = copy
    }
    return { ...previous, ids: next, copies: kept }
  })

  return {
    selectedIds: ids,
    has: id => picked.has(id),
    toggle: (id, checked) => update(current => {
      const on = checked ?? !current.includes(id)
      return on ? (current.includes(id) ? current : [...current, id]) : current.includes(id) ? current.filter(value => value !== id) : current
    }),
    toggleMany: (many, checked) => update(current => checked ? Array.from(new Set([...current, ...many])) : current.filter(id => !many.includes(id))),
    set: next => update(current => typeof next === 'function' ? next(current) : next),
    remove: gone => {
      const drop = new Set(gone)
      update(current => current.some(id => drop.has(id)) ? current.filter(id => !drop.has(id)) : current)
    },
    clear: () => update(current => current.length ? [] : current),
    selectedVisible: (items, idOf) => items.filter(item => picked.has(idOf(item))),
    selectedRows: (items, idOf) => {
      const shown = new Map(items.map(item => [idOf(item), item]))
      return ids.map(id => shown.get(id) ?? copies[id]).filter((row): row is T => row !== undefined)
    }
  }
}
