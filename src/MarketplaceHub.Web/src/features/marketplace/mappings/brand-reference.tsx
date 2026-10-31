// Marketplace brand lists (Trendyol keeps 808k brands, Pazarama 104k) stay on the server: the brand mapping page reads
// the list's size and age, searches it as the user types and resolves the saved mappings' ids to names. Nothing here
// downloads the whole list, and nothing here starts a brand list read: the worker refreshes each list once a day.
import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { hubApi } from '../../../shared/api'
import { UiIcon } from '../../../shared/components'
import { focusOnMount } from '../../../shared/focus'
import { useDismissable } from '../../../shared/hooks/useDismissable'

export type BrandReferenceItem = { externalId: string; name: string; isActive: boolean }
// One slice of the connection's current brand snapshot: no items for the summary, the matches of a search or lookup.
export type BrandReferenceSlice = { snapshotId: string; resourceType: string; fetchedAt: string; itemCount: number; items: BrandReferenceItem[] }

const day = 24 * 60 * 60_000
export const brandSearchDebounceMs = 250
export const brandSearchLimit = 50
// The server answers at most 500 ids per lookup; a smaller batch keeps the URL short.
export const brandLookupBatchSize = 200

/** Every brand reference query of a connection starts with this key, so a finished brand read refreshes them all. */
export const brandReferenceKey = (connectionId: string) => ['reference-brands', connectionId] as const
const snapshotKey = (snapshot: BrandReferenceSlice | undefined) => [snapshot?.snapshotId ?? '', snapshot?.fetchedAt ?? ''] as const

export function brandReferencePath(connectionId: string, action: 'summary' | 'search' | 'lookup', params: Record<string, string> = {}) {
  return `/reference-data/brands/${action}?${new URLSearchParams({ connectionId, ...params }).toString()}`
}

/** The brand list's id, age and size; it answers REFERENCE_SNAPSHOT_UNAVAILABLE while the connection has none. */
export function useBrandReferenceSummary(connectionId: string, enabled = true) {
  return useQuery({
    queryKey: [...brandReferenceKey(connectionId), 'summary'],
    queryFn: ({ signal }) => hubApi<BrandReferenceSlice>(brandReferencePath(connectionId, 'summary'), { signal }),
    enabled: enabled && !!connectionId,
    retry: false,
    staleTime: 5 * 60_000
  })
}

/** The brands of the current list with these ids, in batches; an id the list no longer holds is left out. */
export async function lookupBrandReferences(connectionId: string, ids: readonly string[], signal?: AbortSignal) {
  const unique = [...new Set(ids.filter(Boolean))]
  const batches: string[][] = []
  for (let index = 0; index < unique.length; index += brandLookupBatchSize) batches.push(unique.slice(index, index + brandLookupBatchSize))
  const answers = await Promise.all(batches.map(batch => hubApi<BrandReferenceSlice>(brandReferencePath(connectionId, 'lookup', { ids: batch.join(',') }), { signal })))
  return answers.flatMap(answer => answer.items)
}

/** Names of the given brand ids, cached for a day per snapshot (a new brand list gets new keys). */
export function useBrandReferenceLookup(connectionId: string, snapshot: BrandReferenceSlice | undefined, ids: readonly string[]) {
  const sorted = useMemo(() => [...new Set(ids.filter(Boolean))].sort(), [ids])
  const query = useQuery({
    queryKey: [...brandReferenceKey(connectionId), 'lookup', ...snapshotKey(snapshot), sorted.join(',')],
    queryFn: ({ signal }) => lookupBrandReferences(connectionId, sorted, signal),
    enabled: !!connectionId && !!snapshot && sorted.length > 0,
    retry: false,
    staleTime: day,
    gcTime: day,
    placeholderData: keepPreviousData
  })
  const byId = useMemo(() => new Map((query.data ?? []).map(item => [item.externalId, item])), [query.data])
  // The previous ids' names stand in while new ones load; a name missing from them is still loading, not unknown.
  return { byId, isLoading: sorted.length > 0 && !query.isError && (query.isLoading || query.isPlaceholderData), isError: query.isError }
}

export function useDebouncedValue<T>(value: T, delayMs: number) {
  const [debounced, setDebounced] = useState(value)
  useEffect(() => {
    const timeout = window.setTimeout(() => setDebounced(value), delayMs)
    return () => window.clearTimeout(timeout)
  }, [value, delayMs])
  return debounced
}

/** The server-side brand search behind the pickers: debounced, cached for a day per snapshot and query. */
export function useBrandReferenceSearch(connectionId: string, snapshot: BrandReferenceSlice | undefined, text: string, enabled: boolean) {
  const term = useDebouncedValue(text.trim(), brandSearchDebounceMs)
  const query = useQuery({
    queryKey: [...brandReferenceKey(connectionId), 'search', ...snapshotKey(snapshot), term],
    queryFn: ({ signal }) => hubApi<BrandReferenceSlice>(brandReferencePath(connectionId, 'search', { q: term, limit: String(brandSearchLimit) }), { signal }),
    enabled: enabled && !!connectionId && !!snapshot,
    retry: false,
    staleTime: day,
    gcTime: day,
    placeholderData: keepPreviousData
  })
  const settled = term === text.trim() && !query.isFetching
  return { items: query.data?.items ?? [], searching: !settled, isError: query.isError }
}

type BrandPickerProps = {
  connectionId: string
  snapshot: BrandReferenceSlice | undefined
  value: string
  // The chosen brand's name when the page already knows it (a lookup or the last choice).
  selectedName?: string
  placeholder: string
  disabled?: boolean
  onChange: (externalId: string, item: BrandReferenceItem) => void
}

function brandOptions(items: BrandReferenceItem[], value: string, selectedName: string | undefined, text: string) {
  // The chosen brand leads an empty search, as the old full-list picker did.
  const selected = value && selectedName && !text.trim() ? [{ externalId: value, name: selectedName, isActive: true }] : []
  return [...selected, ...items.filter(item => item.externalId !== selected[0]?.externalId)]
}

function emptyText(searching: boolean, isError: boolean, text: string) {
  if (isError) return 'Marka listesi okunamadı. Yeniden deneyin.'
  if (searching) return 'Aranıyor…'
  return text.trim() ? 'Aramayla eşleşen marka bulunamadı.' : 'Marka listesi boş. Senkronize Et ile yeniden deneyin.'
}

/** The brand page's platform brand picker: a trigger and a searchable panel listing the server's matches. */
export function BrandReferencePicker({ connectionId, snapshot, value, selectedName, placeholder, disabled = false, onChange }: BrandPickerProps) {
  const [open, setOpen] = useState(false)
  const [text, setText] = useState('')
  const rootRef = useRef<HTMLDivElement>(null)
  useDismissable(open, [rootRef], () => setOpen(false))
  const search = useBrandReferenceSearch(connectionId, snapshot, text, open)
  const options = brandOptions(search.items, value, selectedName, text)
  function choose(item: BrandReferenceItem) { onChange(item.externalId, item); setOpen(false) }
  function keyDown(event: KeyboardEvent<HTMLInputElement>) {
    if (event.key === 'Enter' && options.length && !search.searching) { event.preventDefault(); choose(options[0]) }
  }
  useEffect(() => { if (!open) setText('') }, [open])
  return <div className="platform-category-picker-wrap" ref={rootRef}>
    <button type="button" className="panel-category-picker-trigger" aria-expanded={open} onClick={() => setOpen(current => !current)} disabled={disabled}><span>{value ? selectedName ?? value : placeholder}</span><UiIcon name="chevronDown" /></button>
    {open && <div className="panel-category-picker platform-category-picker brand-reference-picker" role="dialog" aria-label="Platform markası seçimi">
      <div><input ref={focusOnMount} aria-label="Platform markalarında ara" value={text} onChange={event => setText(event.target.value)} onKeyDown={keyDown} placeholder="Marka adı veya marka no ara..." /><button type="button" aria-label="Platform marka menüsünü kapat" onClick={() => setOpen(false)}><UiIcon name="close" /></button></div>
      <div role="listbox" aria-busy={search.searching}>{options.length ? options.map(item => <button type="button" role="option" aria-selected={item.externalId === value} key={item.externalId} onClick={() => choose(item)}><span>{item.name}</span><small>Marka no: {item.externalId}</small></button>) : <span>{emptyText(search.searching, search.isError, text)}</span>}</div>
    </div>}
  </div>
}

/** The same search in the look of SearchableSelect, for the saved mappings table and the edit dialog. */
export function BrandReferenceSelect({ label, platformLabel, connectionId, snapshot, value, selectedName, placeholder, disabled = false, onChange }: BrandPickerProps & { label: string; platformLabel: string }) {
  const [open, setOpen] = useState(false)
  const [text, setText] = useState('')
  const rootRef = useRef<HTMLLabelElement>(null)
  useDismissable(open, [rootRef], () => { setOpen(false); setText('') })
  const search = useBrandReferenceSearch(connectionId, snapshot, text, open)
  const options = brandOptions(search.items, value, selectedName, text)
  function toggle() { if (disabled) return; setText(''); setOpen(current => !current) }
  function choose(item: BrandReferenceItem) { onChange(item.externalId, item); setText(''); setOpen(false) }
  function keyDown(event: KeyboardEvent<HTMLInputElement>) {
    if (event.key === 'Enter' && open && options.length && !search.searching) { event.preventDefault(); choose(options[0]) }
  }
  const listId = `brand-reference-select-${connectionId}`
  return <label ref={rootRef} className={`searchable-select${open ? ' open' : ''}${disabled ? ' disabled' : ''}`}><span>{label}</span>
    <button type="button" className="searchable-select-trigger" aria-expanded={open} aria-haspopup="listbox" disabled={disabled} onClick={toggle}><span>{value ? selectedName ?? value : placeholder}</span><UiIcon name="chevronDown" /></button>
    {open && <div id={listId} className="searchable-select-menu" role="listbox" aria-busy={search.searching}><div className="searchable-select-search"><UiIcon name="search" /><input ref={focusOnMount} role="combobox" aria-label={`${label || `${platformLabel} markası`} ara`} aria-controls={listId} aria-expanded={open} aria-autocomplete="list" value={text} placeholder={placeholder} onKeyDown={keyDown} onChange={event => setText(event.target.value)} /></div>
      <div className="searchable-select-options">{options.length ? options.map(item => <button type="button" role="option" aria-selected={item.externalId === value} key={item.externalId} onMouseDown={event => { event.preventDefault(); choose(item) }}><span>{item.name}</span><small>{platformLabel} marka no: {item.externalId}</small></button>) : <span className="searchable-select-empty">{emptyText(search.searching, search.isError, text)}</span>}</div>
    </div>}
  </label>
}
