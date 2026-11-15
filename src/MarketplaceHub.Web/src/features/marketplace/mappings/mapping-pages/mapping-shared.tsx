import type { ApiAnswer } from '../../../../shared/api/api-answer'
import { createContext, useEffect, useRef, useState, type KeyboardEvent } from 'react'
import { Link } from 'react-router'
import { UiIcon } from '../../../../shared/components'
import { type ValueDraft } from '../value-drafts'
import { mappingStatusLabel } from '../../../../shared/status-labels'
import { viewTabLabel } from '../../../../shared/view-tabs'
import { focusOnMount } from '../../../../shared/focus'
import { useDismissable } from '../../../../shared/hooks/useDismissable'
import { useMenuFlip } from '../../../../shared/hooks/useMenuFlip'

// A mapping notice names the store whose mappings it is about; the connection is read when the notice is recorded.
export function useMappingNotificationConnection(connectionId: string) {
  const current = useRef(connectionId)
  useEffect(() => { current.current = connectionId })
  return current
}

export function MappingDeleteIcon() {
  return <UiIcon name="close" className="mapping-delete-icon" />
}
export type LocalCategory = { id: string; parentId?: string | null; name: string; path: string; depth: number; isLeaf: boolean; isActive: boolean; version: number }

export type LocalAttribute = { id: string; code: string; name: string; dataType: string; isActive: boolean; version: number; roles?: string[] | null; values: { id: string; value: string; sortOrder: number; isActive: boolean }[] }

export type ReferenceItem = ReferenceData['items'][number]
export type ReferenceData = ApiAnswer<'getReferenceDataCategories'>
export type CatalogMapping = ApiAnswer<'putMappingsAttributeValuesByLocalId'>

type SearchOption = { value: string; label: string; description?: string }
// triggerClassName lets a caller give the trigger another control's look (the brand page dresses it as the platform picker).
export function SearchableSelect({ label, value, options, placeholder, disabled = false, triggerClassName = '', onChange }: { label: string; value: string; options: SearchOption[]; placeholder: string; disabled?: boolean; triggerClassName?: string; onChange: (value: string) => void }) {
  const selected = options.find(option => option.value === value)
  const [query, setQuery] = useState('')
  const [open, setOpen] = useState(false)
  const normalized = query.trim().toLocaleLowerCase('tr-TR')
  const filtered = options.filter(option => !normalized || `${option.label} ${option.description ?? ''}`.toLocaleLowerCase('tr-TR').includes(normalized)).slice(0, normalized ? 500 : 100)
  const rootRef = useRef<HTMLLabelElement>(null)
  // It had no outside close at all: the list stayed open over the page until an option was chosen.
  useDismissable(open, [rootRef], () => { setOpen(false); setQuery('') })
  useMenuFlip(open, rootRef, '.searchable-select-menu')
  function toggleMenu() { if (disabled) return; setQuery(''); setOpen(current => !current) }
  function choose(option: SearchOption) { onChange(option.value); setQuery(''); setOpen(false) }
  function keyDown(event: KeyboardEvent<HTMLInputElement>) {
    if (event.key === 'Enter' && open && filtered.length) { event.preventDefault(); choose(filtered[0]) }
  }
  const listId = `search-select-${label.toLocaleLowerCase('tr-TR').replace(/[^a-z0-9]+/g, '-')}`
  return <label ref={rootRef} className={`searchable-select${open ? ' open' : ''}${disabled ? ' disabled' : ''}`}><span>{label}</span><button type="button" className={triggerClassName ? `searchable-select-trigger ${triggerClassName}` : 'searchable-select-trigger'} aria-expanded={open} aria-haspopup="listbox" disabled={disabled} onClick={toggleMenu}><span>{selected?.label ?? placeholder}</span><UiIcon name="chevronDown" /></button>{open && <div id={listId} className="searchable-select-menu" role="listbox"><div className="searchable-select-search"><UiIcon name="search" /><input ref={focusOnMount} role="combobox" aria-label={`${label} ara`} aria-controls={listId} aria-expanded={open} aria-autocomplete="list" value={query} placeholder={placeholder} onKeyDown={keyDown} onChange={event => { setQuery(event.target.value); if (value) onChange('') }} onBlur={() => window.setTimeout(() => setOpen(false), 120)} /></div><div className="searchable-select-options">{filtered.length ? filtered.map(option => <button type="button" role="option" aria-selected={option.value === value} key={option.value} onMouseDown={event => { event.preventDefault(); choose(option) }}><span>{option.label}</span>{option.description && <small>{option.description}</small>}</button>) : <span className="searchable-select-empty">Eşleşen kayıt bulunamadı.</span>}</div></div>}</label>
}
export function MappingViewTabs({ active }: { active: 'category' | 'brand' }) {
  return <div className="mapping-view-tabs rv-tabs rv-view-tabs" aria-label="Eşleştirme türü" role="tablist"><Link role="tab" aria-selected={active === 'category'} className={active === 'category' ? 'active is-active' : ''} to="/mappings/categories"><span className="rv-tab-label">{viewTabLabel('category', 'Kategori Eşleme')}</span></Link><Link role="tab" aria-selected={active === 'brand'} className={active === 'brand' ? 'active is-active' : ''} to="/mappings/categories?view=brands"><span className="rv-tab-label">{viewTabLabel('brand', 'Marka Eşleme')}</span></Link></div>
}
export function wait(milliseconds: number) { return new Promise(resolve => window.setTimeout(resolve, milliseconds)) }

export function normalizedAttributeKey(value: string) { return value.replace(/[\s_-]+/g, '').toLocaleUpperCase('tr-TR') }
export function isWebColorAttributeName(value: string) { return ['WEBCOLOR', 'WEBCOLOUR', 'WEBRENK'].includes(normalizedAttributeKey(value)) }
export function isPanelColorAttributeName(value: string) { return ['RENK', 'COLOR', 'COLOUR'].includes(normalizedAttributeKey(value)) }
export function isVariantOptionAttributeName(value: string) { return isPanelColorAttributeName(value) || isWebColorAttributeName(value) || ['BEDEN', 'SIZE'].includes(normalizedAttributeKey(value)) }

export function isOptionAttribute(attribute: Pick<LocalAttribute, 'code' | 'name' | 'roles'>) {
  const normalizedName = normalizedAttributeKey(attribute.name)
  return attribute.code.trim().toLowerCase().startsWith('option-')
    || attribute.roles?.some(role => role.toUpperCase() === 'OPTION') === true
    || isVariantOptionAttributeName(normalizedName)
}

export function cleanMarketplaceCategoryPath(value: string) {
  return value.replace(/\[TDG\]\s*/gi, '').replace(/\(\s*TDG\s*\)\s*/gi, '').replace(/\s*\/\s*/g, ' / ').trim()
}
export function categoryMappingStatus(item: CatalogMapping) {
  if (item.status !== 'VERIFIED' && item.status !== 'ACTIVE') return { className: 'error', label: mappingStatusLabel(item.status), title: 'Kategori eşleştirmesi doğrulanmadı.' }
  if (item.missingRequiredAttributeCount === null || item.missingRequiredAttributeCount === undefined) return { className: 'pending', label: 'Kontrol Bekliyor', title: 'Zorunlu kategori alanları için güncel kontrol verisi henüz hazır değil.' }
  if (item.missingRequiredAttributeCount > 0) return { className: 'warning', label: 'Zorunlu Alan Eksik', title: `${item.missingRequiredAttributeCount} zorunlu kategori alanı eşlenmedi.` }
  return { className: 'active', label: 'Aktif', title: 'Kategori ve zorunlu alan eşlemeleri hazır.' }
}

// The window's unsaved value choices, one draft per field, kept until Kaydet (which saves every field's) or until
// the window closes: moving to another field no longer drops them.
// `kept`: the fields whose drafts the last Kaydet kept (a request refused); their tables keep the choices.
export type ValueDraftStore = { drafts: ReadonlyMap<string, ValueDraft>; generation: number; kept: ReadonlySet<string>; saving: boolean; put: (draft: ValueDraft) => void; drop: (valueScope: string) => void; saveAll: () => Promise<void> }
export const ValueDraftContext = createContext<ValueDraftStore | null>(null)

export const valueCollator = new Intl.Collator('tr-TR', { sensitivity: 'base', numeric: true })
