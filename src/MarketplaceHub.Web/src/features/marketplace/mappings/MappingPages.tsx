// Eşleştirmeler (/mappings/categories, /mappings/attributes).
import { useRememberedHeight } from '../../../shared/remembered-height'
import './mapping-design-system.css'
import { createContext, Fragment, useCallback, useContext, useEffect, useEffectEvent, useMemo, useRef, useState, type ChangeEvent, type KeyboardEvent, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { Link, useSearchParams } from 'react-router'
import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiRequestError, hubApi, loadAllPages } from '../../../shared/api'
import { Busy, ErrorBox, Modal, Pagination, UiIcon, useConfirmDialog, DialogSurface } from '../../../shared/components'
import { isStaleMappingError, mappingSaveErrorMessage, refreshMappingConflicts, saveAllMappings } from './mapping-refresh'
import { ValuePicker, pickerOptions } from './ValuePicker'
import { groupSavedSelections, runInBatches, selectionsDiffer, valueDraftOperations, type ValueDraft, type ValueDraftOperation } from './value-drafts'
import { newIdempotencyKey } from '../../../shared/idempotency'
import { MessageToast } from '../../../shared/components/NoticeToast'
import { mappingStatusLabel } from '../../../shared/status-labels'
import { platformLogoClass, platformLogoSource } from '../../../shared/platform-logos'
import { MobileActionMenu } from '../../../shared/MobileActionMenu'
import { PanelValueMultiSelect } from '../PanelValueMultiSelect'
import { useScrollLock } from '../../../shared/hooks/useScrollLock'
import { appendNotification } from '../../../shared/notifications'
import { sortByTurkishName } from '../attribute-sorting'
import { formatPanelColorValue, usesCustomPanelColorValue } from '../color-value-format'
import { AttributeDefaultsPanel, type AttributeDefault } from '../AttributeDefaultsPanel'
import { offersProductFields, showsProductFields, parseProductFieldOption, productFieldLabel, productFieldOptionValue, productFieldSources } from '../product-field-sources'
import { MappingConflictsPanel } from '../MappingConflictsPanel'
import { autoMatchScore, canonicalKey, valueMatchScore } from '../../../shared/value-matching'
import { attributeValueMappingNeedsSave, isTemplateFilledRow, suggestValueMappings, type ValueMappingSuggestion, visiblePanelValues, normalizeReferenceValueLabel, planDirectReferenceValues, planMissingReferencePanelValues, planReferencePanelMappings, togglePanelValueReferenceSelection, updatePanelValueReferenceSelection, valueMappingRowClassName } from '../attribute-value-mapping'
import { isReferenceSyncJobTerminal, shouldAutoSyncHepsiburadaValues, waitForReferenceSyncJobs } from '../reference-value-sync'
import { activeMappingConnectionsForPlatform, mappingPlatformDefinitions, mappingPlatformLabel, type MappingPlatformCode } from '../mapping-platforms'
import { resolveAttributeMappingRole } from '../mapping-attribute-role'
import { suggestCategoryMappings } from '../category-suggestions'
import { viewTabLabel } from '../../../shared/view-tabs'
import { formatDateTime } from '../../../shared/format-date'
import { focusOnMount } from '../../../shared/focus'
import { Badge, Connection, FieldState, MappingStateGlyph, Page, notificationKindFromMessage } from '../shared'
import { useDismissable } from '../../../shared/hooks/useDismissable'
// Styles of this route only (their rules are scoped to its own containers).
import '../value-mapping-modal.css'
import '../mapping-workspace.css'
import '../category-workbench.css'

function MappingDeleteIcon() {
  return <UiIcon name="close" className="mapping-delete-icon" />
}
type LocalCategory = { id: string; name: string; path: string; depth: number; isLeaf: boolean; isActive: boolean; version: number }
type LocalBrand = { id: string; name: string; isActive: boolean; version: number }
type LocalAttribute = { id: string; code: string; name: string; dataType: string; isActive: boolean; version: number; roles?: string[] | null; values: { id: string; value: string; sortOrder: number; isActive: boolean }[] }
type CategoryRequirementView = { attributeId: string; isRequired: boolean; allowsCustomValue: boolean; displayOrder: number; role: 'ATTRIBUTE' | 'OPTION'; attribute: LocalAttribute }
type AttributeRequirementCommand = { attributeId: string; isRequired: boolean; allowsCustomValue: boolean; displayOrder: number; role: 'ATTRIBUTE' | 'OPTION' }
type ReferenceItem = { externalId: string; parentExternalId: string | null; name: string; path: string; depth: number; isLeaf: boolean; isActive: boolean; isRequired: boolean | null; allowsCustomValue: boolean | null; allowsMultipleValues: boolean | null }
type ReferenceData = { snapshotId: string; resourceType: string; fetchedAt: string; items: ReferenceItem[] }
type CatalogMapping = { id: string; connectionId: string; snapshotId: string; localId: string; scopeExternalId: string; externalId: string; status: string; verifiedAt: string | null; version: number; missingRequiredAttributeCount?: number | null; source?: string | null }
type MappingTransferBundle = { format: 'RAVENCIA_MAPPING_BUNDLE'; version: 1; exportedAt: string; categories: LocalCategory[]; attributes: LocalAttribute[]; categoryMappings: CatalogMapping[]; attributeMappings: CatalogMapping[]; attributeValueMappings: CatalogMapping[] }
type BrandMappingTransferBundle = { format: 'RAVENCIA_BRAND_MAPPING_BUNDLE'; version: 1; exportedAt: string; brands: LocalBrand[]; mappings: CatalogMapping[] }
type MappingTransferScope = 'categories' | 'options' | 'attributes' | 'mappings'
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
  function toggleMenu() { if (disabled) return; setQuery(''); setOpen(current => !current) }
  function choose(option: SearchOption) { onChange(option.value); setQuery(''); setOpen(false) }
  function keyDown(event: KeyboardEvent<HTMLInputElement>) {
    if (event.key === 'Enter' && open && filtered.length) { event.preventDefault(); choose(filtered[0]) }
  }
  const listId = `search-select-${label.toLocaleLowerCase('tr-TR').replace(/[^a-z0-9]+/g, '-')}`
  return <label ref={rootRef} className={`searchable-select${open ? ' open' : ''}${disabled ? ' disabled' : ''}`}><span>{label}</span><button type="button" className={triggerClassName ? `searchable-select-trigger ${triggerClassName}` : 'searchable-select-trigger'} aria-expanded={open} aria-haspopup="listbox" disabled={disabled} onClick={toggleMenu}><span>{selected?.label ?? placeholder}</span><UiIcon name="chevronDown" /></button>{open && <div id={listId} className="searchable-select-menu" role="listbox"><div className="searchable-select-search"><UiIcon name="search" /><input ref={focusOnMount} role="combobox" aria-label={`${label} ara`} aria-controls={listId} aria-expanded={open} aria-autocomplete="list" value={query} placeholder={placeholder} onKeyDown={keyDown} onChange={event => { setQuery(event.target.value); if (value) onChange('') }} onBlur={() => window.setTimeout(() => setOpen(false), 120)} /></div><div className="searchable-select-options">{filtered.length ? filtered.map(option => <button type="button" role="option" aria-selected={option.value === value} key={option.value} onMouseDown={event => { event.preventDefault(); choose(option) }}><span>{option.label}</span>{option.description && <small>{option.description}</small>}</button>) : <span className="searchable-select-empty">Eşleşen kayıt bulunamadı.</span>}</div></div>}</label>
}
function MappingViewTabs({ active }: { active: 'category' | 'brand' | 'attribute' }) {
  return <div className="mapping-view-tabs rv-tabs rv-view-tabs" aria-label="Eşleştirme türü" role="tablist"><Link role="tab" aria-selected={active === 'category'} className={active === 'category' ? 'active is-active' : ''} to="/mappings/categories"><span className="rv-tab-label">{viewTabLabel('category', 'Kategori Eşleme')}</span></Link><Link role="tab" aria-selected={active === 'brand'} className={active === 'brand' ? 'active is-active' : ''} to="/mappings/categories?view=brands"><span className="rv-tab-label">{viewTabLabel('brand', 'Marka Eşleme')}</span></Link>{active === 'attribute' && <Link role="tab" aria-selected className="active is-active" to="/mappings/attributes"><span className="rv-tab-label"><span className="rv-tab-text" data-text="Özellik Eşleme">Özellik Eşleme</span></span></Link>}</div>
}
function wait(milliseconds: number) { return new Promise(resolve => window.setTimeout(resolve, milliseconds)) }
function displayAttributeName(value: string) { return value.replace(/\[(?:A-)?TDG\][\s_-]*/gi, '').replace(/^\(?(?:A-)?TDG\)?[\s_-]*/gi, '').replace(/_/g, ' ').replace(/\s+/g, ' ').trim() }
function normalizedAttributeKey(value: string) { return value.replace(/[\s_-]+/g, '').toLocaleUpperCase('tr-TR') }
function isWebColorAttributeName(value: string) { return ['WEBCOLOR', 'WEBCOLOUR', 'WEBRENK'].includes(normalizedAttributeKey(value)) }
function isPanelColorAttributeName(value: string) { return ['RENK', 'COLOR', 'COLOUR'].includes(normalizedAttributeKey(value)) }
function isVariantOptionAttributeName(value: string) { return isPanelColorAttributeName(value) || isWebColorAttributeName(value) || ['BEDEN', 'SIZE'].includes(normalizedAttributeKey(value)) }
function isSizeAttributeName(value: string) {
  const key = normalizedAttributeKey(value).replace(/[^0-9A-Z]/g, '')
  return key === 'BEDEN' || key === 'SIZE' || key.endsWith('BEDEN') || key.endsWith('SIZE')
}
function isSizeAttribute(attribute: Pick<LocalAttribute, 'name' | 'code'>) { return isSizeAttributeName(attribute.name) || isSizeAttributeName(attribute.code) }
export function MappingPage() {
  const [searchParams] = useSearchParams()
  useEffect(() => {
    const main = document.querySelector<HTMLElement>('.app-shell.stitch-shell > main')
    if (main) main.scrollTop = 0
  }, [])
  return searchParams.get('view') === 'brands' ? <BrandMappingPage /> : <CategoryMappingWorkspace />
}
const supportedAttributeMappingPlatforms = mappingPlatformDefinitions.filter(platform => platform.code === 'TRENDYOL' || platform.code === 'HEPSIBURADA' || platform.code === 'N11' || platform.code === 'PAZARAMA')
function CategoryMappingWorkspace() {
  const [confirm, confirmDialog] = useConfirmDialog()
  const client = useQueryClient()
  const [connectionId, setConnectionId] = useState(''); const [selectedPlatformCode, setSelectedPlatformCode] = useState<MappingPlatformCode>('TRENDYOL'); const [localId, setLocalId] = useState(''); const [externalId, setExternalId] = useState(''); const [notice, setNotice] = useState(''); const [categoryName, setCategoryName] = useState(''); const [categoryLibrarySearch, setCategoryLibrarySearch] = useState(''); const [categoryLibraryOpen, setCategoryLibraryOpen] = useState(false); const [categoryLibrarySort, setCategoryLibrarySort] = useState<'NAME_ASC' | 'NAME_DESC'>('NAME_ASC'); const [externalSearch, setExternalSearch] = useState(''); const [externalPickerOpen, setExternalPickerOpen] = useState(false); const [savedSearch, setSavedSearch] = useState(''); const [savedSort, setSavedSort] = useState<'NAME_ASC' | 'NAME_DESC'>('NAME_ASC'); const [savedPage, setSavedPage] = useState(1); const [advancedOpen, setAdvancedOpen] = useState(false); const [panelPickerOpen, setPanelPickerOpen] = useState(false); const [panelPickerSearch, setPanelPickerSearch] = useState(''); const [exportOpen, setExportOpen] = useState(false); const [exportSelection, setExportSelection] = useState<Record<MappingTransferScope, boolean>>({ categories: true, options: true, attributes: true, mappings: true }); const [transferOpen, setTransferOpen] = useState(false); const [transferBundle, setTransferBundle] = useState<MappingTransferBundle | null>(null); const [transferSelection, setTransferSelection] = useState<Record<MappingTransferScope, boolean>>({ categories: true, options: true, attributes: true, mappings: true }); const [transferBusy, setTransferBusy] = useState(false)
  const autoSelectedConnectionPlatform = useRef(false)
  useScrollLock(exportOpen || transferOpen || advancedOpen || panelPickerOpen, true)
  useEffect(() => {
    if (!notice) return
    const kind = notificationKindFromMessage(notice)
    appendNotification(notice, kind)
    document.documentElement.dataset.rvMappingFeedback = kind
    const timeout = window.setTimeout(() => setNotice(''), kind === 'info' ? 7000 : 5500)
    return () => window.clearTimeout(timeout)
  }, [notice])
  useEffect(() => {
    if (!advancedOpen) return
    const closeOnEscape = (event: globalThis.KeyboardEvent) => {
      if (event.key !== 'Escape' || event.defaultPrevented) return
      event.preventDefault()
      setAdvancedOpen(false)
    }
    document.addEventListener('keydown', closeOnEscape)
    return () => document.removeEventListener('keydown', closeOnEscape)
  }, [advancedOpen])
  const connections = useQuery({ queryKey: ['connections', 'mapping'], queryFn: () => loadAllPages<Connection>('/connections') })
  useEffect(() => {
    if (!connections.data || autoSelectedConnectionPlatform.current) return
    const availablePlatform = mappingPlatformDefinitions.find(platform => activeMappingConnectionsForPlatform(connections.data.items, platform.code).length > 0)
    if (!availablePlatform) return
    autoSelectedConnectionPlatform.current = true
    setSelectedPlatformCode(current => activeMappingConnectionsForPlatform(connections.data!.items, current).length ? current : availablePlatform.code)
  }, [connections.data])
  const localCategories = useQuery({ queryKey: ['categories', 'mapping'], queryFn: () => loadAllPages<LocalCategory>('/catalog/categories') })
  const categoryListHeight = useRememberedHeight<HTMLDivElement>('category-library', localCategories.isLoading)
  const localAttributes = useQuery({ queryKey: ['attributes', 'mapping-builder'], queryFn: () => loadAllPages<LocalAttribute>('/catalog/attributes') })
  const references = useQuery({ queryKey: ['reference-categories', connectionId], queryFn: () => hubApi<ReferenceData>(`/reference-data/categories?connectionId=${encodeURIComponent(connectionId)}`), enabled: !!connectionId, retry: false })
  const mapping = useQuery({ queryKey: ['category-mapping', localId, connectionId], queryFn: () => hubApi<CatalogMapping | null>(`/mappings/categories/${localId}?connectionId=${encodeURIComponent(connectionId)}`), enabled: !!localId && !!connectionId, retry: false })
  const categoryMappings = useQuery({ queryKey: ['category-mappings', connectionId], queryFn: () => hubApi<CatalogMapping[]>(`/mappings/categories?connectionId=${encodeURIComponent(connectionId)}`), enabled: !!connectionId, retry: false })
  const syncCategories = useMutation({ mutationFn: () => {
    if (!connectionId) throw new Error(`Aktif ${mappingPlatformLabel(selectedPlatformCode)} bağlantısı bulunamadı.`)
    return hubApi(`/connections/${connectionId}/reference-sync-jobs?resourceType=CATEGORIES`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: '{}' })
  }, onSuccess: async () => {
    setNotice(`${mappingPlatformLabel(selectedPlatformCode)} kategori listesi salt-okunur eşitleme kuyruğuna alındı.`)
    for (const delay of [500, 1000, 2000, 4000]) {
      await wait(delay)
      await client.invalidateQueries({ queryKey: ['reference-categories', connectionId] })
    }
  }, onError: reason => setNotice(reason instanceof Error ? reason.message : 'Kategori eşitleme başlatılamadı.') })
  const save = useMutation({ mutationFn: () => { if (!references.data || !localId || !externalId) throw new Error(`Panel kategorisi ve ${mappingPlatformDefinitions.find(item => item.code === selectedPlatformCode)?.label ?? 'platform'} kategorisi zorunludur.`); return hubApi<CatalogMapping>(`/mappings/categories/${localId}`, { method: 'PUT', headers: mapping.data ? { 'If-Match': `"v${mapping.data.version}"` } : {}, body: JSON.stringify({ connectionId, snapshotId: references.data.snapshotId, externalId, status: 'VERIFIED' }) }) }, onSuccess: async value => { setNotice('Kategori eşleştirmesi kaydedildi.'); setExternalId(value.externalId); client.setQueryData(['category-mapping', localId, connectionId], value); client.setQueryData(['category-mapping', localId, connectionId, 'embedded'], value); client.setQueryData<CatalogMapping[]>(['category-mappings', connectionId], current => current ? [...current.filter(item => item.localId !== value.localId), value] : [value]); await Promise.all([client.invalidateQueries({ queryKey: ['category-mapping', localId, connectionId] }), client.invalidateQueries({ queryKey: ['category-mapping', localId, connectionId, 'embedded'] }), client.invalidateQueries({ queryKey: ['category-mappings', connectionId] })]) }, onError: async reason => { setNotice(mappingSaveErrorMessage(reason, 'Eşleme kaydedilemedi.')); if (isStaleMappingError(reason)) await Promise.all([client.invalidateQueries({ queryKey: ['category-mapping', localId, connectionId] }), client.invalidateQueries({ queryKey: ['category-mappings', connectionId] })]) }, onSettled: () => refreshMappingConflicts(client, connectionId) })
  const createCategory = useMutation({ mutationFn: () => { if (!categoryName.trim()) throw new Error('Kategori adı zorunludur.'); return hubApi<LocalCategory>('/catalog/categories', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ name: categoryName.trim(), parentId: null }) }) }, onSuccess: async category => { client.setQueryData<Page<LocalCategory>>(['categories', 'mapping'], current => current ? { ...current, items: [...current.items.filter(item => item.id !== category.id), category] } : current); setLocalId(category.id); setExternalId(''); setCategoryName(''); setNotice(`“${category.name}” panel kategorisi oluşturuldu ve seçildi.`); await client.invalidateQueries({ queryKey: ['categories', 'mapping'] }) }, onError: reason => setNotice(reason instanceof Error ? reason.message : 'Kategori oluşturulamadı.') })
  useEffect(() => { setExternalId(mapping.data?.externalId ?? '') }, [mapping.data])
  useEffect(() => {
    if (!categoryLibraryOpen) return
    const closeOnSearchBarClick = (event: globalThis.MouseEvent) => {
      const target = event.target
      if (target instanceof Element && target.closest('.category-library-search-control')) setCategoryLibraryOpen(false)
    }
    document.addEventListener('click', closeOnSearchBarClick)
    return () => document.removeEventListener('click', closeOnSearchBarClick)
  }, [categoryLibraryOpen])
  useEffect(() => {
    if (!panelPickerOpen) return
    const positionPicker = () => {
      const trigger = document.querySelector<HTMLElement>('.mapping-category-builder .mapping-reference-local .panel-category-picker-trigger')
      const picker = document.querySelector<HTMLElement>('.mapping-category-builder .mapping-reference-local > .panel-category-picker')
      if (!trigger || !picker) return
      const rect = trigger.getBoundingClientRect()
      const pickerWidth = Math.round(rect.width)
      const gap = 6
      const viewportPadding = 16
      const spaceBelow = Math.max(0, window.innerHeight - rect.bottom - gap - viewportPadding)
      const spaceAbove = Math.max(0, rect.top - gap - viewportPadding)
      const naturalHeight = Math.min(420, Math.max(0, picker.scrollHeight))
      const opensUp = naturalHeight > spaceBelow && spaceAbove > spaceBelow
      const availableHeight = Math.max(120, Math.min(420, opensUp ? spaceAbove : spaceBelow))
      const pickerHeight = Math.min(naturalHeight || availableHeight, availableHeight)
      const top = opensUp ? rect.top - pickerHeight - gap : Math.min(rect.bottom + gap, window.innerHeight - pickerHeight - viewportPadding)
      const left = Math.min(Math.max(16, rect.left), Math.max(16, window.innerWidth - pickerWidth - 16))
      picker.style.setProperty('position', 'fixed')
      picker.style.setProperty('top', `${Math.round(top)}px`)
      picker.style.setProperty('left', `${Math.round(left)}px`)
      picker.style.setProperty('width', `${pickerWidth}px`)
      picker.style.setProperty('max-height', `${Math.round(availableHeight)}px`)
    }
    positionPicker()
    document.querySelector<HTMLInputElement>('.mapping-category-builder .mapping-reference-local > .panel-category-picker input')?.focus({ preventScroll: true })
    window.addEventListener('resize', positionPicker)
    window.addEventListener('scroll', positionPicker, true)
    return () => { window.removeEventListener('resize', positionPicker); window.removeEventListener('scroll', positionPicker, true) }
  }, [panelPickerOpen])
  // The two category pickers are fixed to the screen, so their trigger and panel are found where they are drawn.
  const pickerPart = (selector: string) => ({ get current() { return document.querySelector(selector) } })
  useDismissable(panelPickerOpen, [pickerPart('.mapping-category-builder .mapping-reference-local .panel-category-picker-trigger'), pickerPart('.mapping-category-builder .mapping-reference-local > .panel-category-picker')], () => setPanelPickerOpen(false))
  useDismissable(externalPickerOpen, [pickerPart('.mapping-category-builder .mapping-reference-target .panel-category-picker-trigger'), pickerPart('.mapping-category-builder .mapping-reference-target .panel-category-picker')], () => setExternalPickerOpen(false))
  const activeMappingConnections = connections.data?.items.filter(item => mappingPlatformDefinitions.some(platform => platform.code === item.platformCode.toUpperCase()) && ['ACTIVE', 'VERIFIED'].includes(item.status.toUpperCase())) ?? []
  // Platforms without an active connection are hidden from the tabs; until any connection is known every tab stays.
  const shownMappingPlatforms = mappingPlatformDefinitions.filter(platform => !activeMappingConnections.length || activeMappingConnections.some(item => item.platformCode.toUpperCase() === platform.code))
  const selectedPlatform = mappingPlatformDefinitions.find(platform => platform.code === selectedPlatformCode) ?? mappingPlatformDefinitions[0]
  const selectedPlatformConnections = activeMappingConnectionsForPlatform(activeMappingConnections, selectedPlatform.code)
  const firstSelectedConnectionId = selectedPlatformConnections[0]?.id ?? ''
  useEffect(() => {
    setConnectionId(current => current === firstSelectedConnectionId ? current : firstSelectedConnectionId)
    setLocalId('')
    setExternalId('')
    setExternalSearch('')
    setPanelPickerOpen(false)
  }, [firstSelectedConnectionId, selectedPlatformCode])
  const activeCategories = localCategories.data?.items.filter(item => item.isActive) ?? []
  const filteredCategoryLibrary = [...activeCategories.filter(item => !categoryLibrarySearch.trim() || item.name.toLocaleLowerCase('tr-TR').includes(categoryLibrarySearch.trim().toLocaleLowerCase('tr-TR')))].sort((left, right) => categoryLibrarySort === 'NAME_DESC' ? right.name.localeCompare(left.name, 'tr') : left.name.localeCompare(right.name, 'tr'))
  const localLeaves = activeCategories.filter(item => item.isLeaf)
  const panelPickerItems = localLeaves.filter(item => !panelPickerSearch.trim() || item.path.toLocaleLowerCase('tr-TR').includes(panelPickerSearch.trim().toLocaleLowerCase('tr-TR'))).slice(0, 80)
  // Shopify's categories are the store's own product types: a flat list, read in Turkish alphabetical order.
  const productTypeCategories = selectedPlatformCode === 'SHOPIFY'
  const externalLeaves = useMemo(() => {
    const leaves = references.data?.items.filter(item => item.isActive && item.isLeaf) ?? []
    return productTypeCategories ? [...leaves].sort((left, right) => left.name.localeCompare(right.name, 'tr')) : leaves
  }, [references.data, productTypeCategories])
  const externalNoun = productTypeCategories ? 'ürün türü' : 'kategori'
  const externalNounPlural = productTypeCategories ? 'ürün türlerinde' : 'kategorilerinde'
  const archiveCategory = useMutation({ mutationFn: (category: LocalCategory) => hubApi<LocalCategory>(`/catalog/categories/${category.id}`, { method: 'PATCH', headers: { 'If-Match': `"v${category.version}"` }, body: JSON.stringify({ name: category.name, parentId: null, isActive: false }) }), onSuccess: async (_, category) => { if (localId === category.id) { setLocalId(''); setExternalId('') }; await client.invalidateQueries({ queryKey: ['categories', 'mapping'] }); setNotice(`“${category.name}” panel kategorilerinden kaldırıldı.`) }, onError: reason => setNotice(reason instanceof Error ? reason.message : 'Kategori kaldırılamadı.') })
  // The delete uses the version the server holds now (not the one the list showed), and the lists and the conflict
  // panel are reloaded whatever the outcome.
  const removeMapping = useMutation({ mutationFn: async (item: CatalogMapping) => { const current = await hubApi<CatalogMapping | null>(`/mappings/categories/${item.localId}?connectionId=${encodeURIComponent(connectionId)}`); if (!current || current.externalId !== item.externalId) throw new Error('Bu eşleştirme bu sırada değişti veya kaldırıldı. Liste yenilendi.'); return hubApi<boolean>(`/mappings/categories/${item.localId}?connectionId=${encodeURIComponent(connectionId)}`, { method: 'DELETE', headers: { 'If-Match': `"v${current.version}"` } }) }, onSuccess: (_, item) => { if (localId === item.localId) { setExternalId(''); client.setQueryData(['category-mapping', localId, connectionId], null) }; setNotice('Kategori eşleştirmesi kaldırıldı.') }, onError: reason => setNotice(mappingSaveErrorMessage(reason, 'Eşleştirme kaldırılamadı.')), onSettled: async (_, __, item) => { await Promise.all([client.invalidateQueries({ queryKey: ['category-mappings', connectionId] }), client.invalidateQueries({ queryKey: ['category-mapping', item.localId, connectionId] }), refreshMappingConflicts(client, connectionId)]) } })
  const selectedExternal = externalLeaves.find(item => item.externalId === externalId)
  const selectedLocal = localLeaves.find(item => item.id === localId)
  // Up to three likely marketplace categories for a panel category that has no mapping yet; a click only selects, Save still decides.
  const categorySuggestions = useMemo(() => selectedLocal && !mapping.data && !mapping.isLoading ? suggestCategoryMappings(selectedLocal, externalLeaves) : [], [selectedLocal, mapping.data, mapping.isLoading, externalLeaves])
  const normalizedExternalSearch = externalSearch.trim().toLocaleLowerCase('tr-TR')
  const matchingExternal = externalLeaves.filter(item => !normalizedExternalSearch || `${item.path} ${item.name} ${item.externalId}`.toLocaleLowerCase('tr-TR').includes(normalizedExternalSearch))
  const visibleExternal = [...(selectedExternal ? [selectedExternal] : []), ...matchingExternal.filter(item => item.externalId !== selectedExternal?.externalId)].slice(0, 20)
  const savedMappings = categoryMappings.data ?? []
  const savedRows = savedMappings.map(item => ({ item, local: localLeaves.find(category => category.id === item.localId), external: externalLeaves.find(category => category.externalId === item.externalId) })).filter(row => !savedSearch.trim() || `${row.local?.path ?? row.item.localId} ${row.external?.path ?? row.item.externalId}`.toLocaleLowerCase('tr-TR').includes(savedSearch.trim().toLocaleLowerCase('tr-TR')))
  const sortedSavedRows = [...savedRows].sort((left, right) => {
    const leftName = (left.local?.path ?? left.item.localId).toLocaleLowerCase('tr-TR')
    const rightName = (right.local?.path ?? right.item.localId).toLocaleLowerCase('tr-TR')
    return savedSort === 'NAME_DESC' ? rightName.localeCompare(leftName, 'tr') : leftName.localeCompare(rightName, 'tr')
  })
  const savedPageSize = 20
  const savedPageCount = Math.max(1, Math.ceil(sortedSavedRows.length / savedPageSize))
  const currentSavedPage = Math.min(savedPage, savedPageCount)
  const visibleSavedRows = sortedSavedRows.slice((currentSavedPage - 1) * savedPageSize, currentSavedPage * savedPageSize)
  async function exportSelectedMappings() {
    if (!connectionId) { setNotice(`Aktarım için önce ${selectedPlatform.label} bağlantısı seçin.`); return }
    if (!Object.values(exportSelection).some(Boolean)) { setNotice('En az bir dışa aktarma alanı seçin.'); return }
    setTransferBusy(true)
    try {
      const attributeMappings: CatalogMapping[] = []
      const attributeValueMappings: CatalogMapping[] = []
      if (exportSelection.mappings) {
        for (const categoryMapping of savedMappings) {
          const scoped = await hubApi<CatalogMapping[]>(`/mappings/attributes?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(categoryMapping.externalId)}`)
          attributeMappings.push(...scoped)
          for (const attributeMapping of scoped) {
            const values = await hubApi<CatalogMapping[]>(`/mappings/attribute-values?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(`${categoryMapping.externalId}/${attributeMapping.externalId}`)}`)
            attributeValueMappings.push(...values)
          }
        }
      }
      const allAttributes = localAttributes.data?.items ?? []
      const selectedAttributes = allAttributes.filter(attribute => exportSelection.options && isOptionAttribute(attribute) || exportSelection.attributes && !isOptionAttribute(attribute))
      const bundle: MappingTransferBundle = { format: 'RAVENCIA_MAPPING_BUNDLE', version: 1, exportedAt: new Date().toISOString(), categories: exportSelection.categories ? activeCategories : [], attributes: selectedAttributes, categoryMappings: exportSelection.mappings ? savedMappings : [], attributeMappings, attributeValueMappings }
      const url = URL.createObjectURL(new Blob([JSON.stringify(bundle, null, 2)], { type: 'application/json;charset=utf-8' }))
      const anchor = document.createElement('a')
      anchor.href = url
      anchor.download = 'ravencia-eslestirme-yedegi.json'
      anchor.rel = 'noopener'
      document.body.appendChild(anchor)
      anchor.click()
      window.setTimeout(() => { anchor.remove(); URL.revokeObjectURL(url) }, 1000)
      setExportOpen(false)
      setNotice(`${bundle.categories.length} kategori, ${bundle.attributes.length} özellik ve ${bundle.categoryMappings.length + bundle.attributeMappings.length + bundle.attributeValueMappings.length} eşleşme dışa aktarıldı.`)
    } catch (reason) { setNotice(reason instanceof Error ? reason.message : 'Eşleştirme aktarımı oluşturulamadı.') } finally { setTransferBusy(false) }
  }
  async function importMappings() {
    if (!transferBundle || !connectionId) return
    if (!Object.values(transferSelection).some(Boolean)) { setNotice('En az bir aktarım alanı seçin.'); return }
    setTransferBusy(true)
    try {
      const categoryIdMap = new Map<string, string>()
      const attributeIdMap = new Map<string, string>()
      const valueIdMap = new Map<string, string>()
      const targetCategories = [...activeCategories]
      const targetAttributes = [...(localAttributes.data?.items ?? [])]
      if (transferSelection.categories) {
        for (const source of [...transferBundle.categories].sort((left, right) => left.depth - right.depth)) {
          const parentPath = source.path.split(' / ').slice(0, -1).join(' / ')
          const existing = targetCategories.find(item => item.id === source.id || item.path === source.path || (item.depth === source.depth && item.name === source.name && !parentPath))
          const target = existing ?? await hubApi<LocalCategory>('/catalog/categories', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ name: source.name, parentId: parentPath ? targetCategories.find(item => item.path === parentPath)?.id ?? null : null }) })
          categoryIdMap.set(source.id, target.id); if (!targetCategories.some(item => item.id === target.id)) targetCategories.push(target)
        }
      }
      const importAttributes = transferBundle.attributes.filter(attribute => transferSelection.options && isOptionAttribute(attribute) || transferSelection.attributes && !isOptionAttribute(attribute))
      for (const source of importAttributes) {
        const existing = targetAttributes.find(item => item.id === source.id || item.code.toLocaleLowerCase('tr-TR') === source.code.toLocaleLowerCase('tr-TR') || item.name.toLocaleLowerCase('tr-TR') === source.name.toLocaleLowerCase('tr-TR'))
        const target = existing ?? await hubApi<LocalAttribute>('/catalog/attributes', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ code: source.code, name: source.name, dataType: source.dataType, selectionMode: source.values.length ? 'SINGLE' : null, unit: null, values: source.values.map((value, index) => ({ value: value.value, sortOrder: index })) }) })
        attributeIdMap.set(source.id, target.id); if (!targetAttributes.some(item => item.id === target.id)) targetAttributes.push(target)
        const missingValues = source.values.filter(value => !target.values.some(existingValue => existingValue.value.toLocaleLowerCase('tr-TR') === value.value.toLocaleLowerCase('tr-TR')))
        if (missingValues.length) await hubApi<LocalAttribute>(`/catalog/attributes/${target.id}/values`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify(missingValues.map((value, index) => ({ value: value.value, sortOrder: target.values.length + index }))) })
        for (const sourceValue of source.values) { const targetValue = target.values.find(value => value.value.toLocaleLowerCase('tr-TR') === sourceValue.value.toLocaleLowerCase('tr-TR')); if (targetValue) valueIdMap.set(sourceValue.id, targetValue.id) }
      }
      if (transferSelection.mappings) {
        if (!references.data) throw new Error('Kategori reference verisi hazır değil; önce kategori eşitlemesini tamamlayın.')
        for (const mappingItem of transferBundle.categoryMappings) {
          const targetLocalId = categoryIdMap.get(mappingItem.localId) ?? targetCategories.find(item => item.id === mappingItem.localId)?.id
          if (!targetLocalId) continue
          await hubApi(`/mappings/categories/${targetLocalId}`, { method: 'PUT', body: JSON.stringify({ connectionId, snapshotId: references.data.snapshotId, externalId: mappingItem.externalId, status: 'VERIFIED' }) })
        }
        const attributeSnapshots = new Map<string, ReferenceData>()
        for (const mappingItem of transferBundle.attributeMappings) {
          const categoryScope = mappingItem.scopeExternalId
          const snapshot = attributeSnapshots.get(categoryScope) ?? await hubApi<ReferenceData>(`/reference-data/categories/${encodeURIComponent(categoryScope)}/attributes?connectionId=${encodeURIComponent(connectionId)}`)
          attributeSnapshots.set(categoryScope, snapshot)
          const targetLocalId = attributeIdMap.get(mappingItem.localId) ?? targetAttributes.find(item => item.id === mappingItem.localId)?.id
          if (!targetLocalId || !snapshot.items.some(item => item.externalId === mappingItem.externalId)) continue
          await hubApi(`/mappings/attributes/${targetLocalId}`, { method: 'PUT', body: JSON.stringify({ connectionId, snapshotId: snapshot.snapshotId, scopeExternalId: categoryScope, externalId: mappingItem.externalId, status: 'VERIFIED', role: isOptionAttribute(transferBundle.attributes.find(item => item.id === mappingItem.localId) ?? { code: '', name: '', roles: [] }) ? 'OPTION' : 'ATTRIBUTE' }) })
        }
        for (const mappingItem of transferBundle.attributeValueMappings) {
          const [categoryScope, remoteAttributeId] = mappingItem.scopeExternalId.split('/')
          const snapshot = await hubApi<ReferenceData>(`/reference-data/categories/${encodeURIComponent(categoryScope)}/attributes/${encodeURIComponent(remoteAttributeId)}/values?connectionId=${encodeURIComponent(connectionId)}`)
          const targetLocalId = valueIdMap.get(mappingItem.localId)
          if (!targetLocalId || !snapshot.items.some(item => item.externalId === mappingItem.externalId)) continue
          await hubApi(`/mappings/attribute-values/${targetLocalId}`, { method: 'PUT', body: JSON.stringify({ connectionId, snapshotId: snapshot.snapshotId, scopeExternalId: mappingItem.scopeExternalId, externalId: mappingItem.externalId, status: 'VERIFIED' }) })
        }
      }
      setTransferOpen(false); setTransferBundle(null); setNotice('Seçtiğiniz eşleştirme alanları güvenli şekilde içe aktarıldı.'); await Promise.all([client.invalidateQueries({ queryKey: ['categories', 'mapping'] }), client.invalidateQueries({ queryKey: ['attributes', 'mapping-builder'] }), client.invalidateQueries({ queryKey: ['category-mappings', connectionId] }), client.invalidateQueries({ queryKey: ['attribute-mappings'] })])
    } catch (reason) { setNotice(reason instanceof Error ? reason.message : 'Eşleştirme aktarımı uygulanamadı.') } finally { setTransferBusy(false) }
  }
  function readMappingBundle(event: ChangeEvent<HTMLInputElement>) {
    const file = event.currentTarget.files?.[0]
    event.currentTarget.value = ''
    if (!file) return
    void file.text().then(text => {
      const parsed = JSON.parse(text) as Partial<MappingTransferBundle>
      if (parsed.format !== 'RAVENCIA_MAPPING_BUNDLE' || parsed.version !== 1 || !Array.isArray(parsed.categories) || !Array.isArray(parsed.attributes)) throw new Error('Geçerli bir Ravencia eşleştirme yedeği seçin.')
      setTransferBundle({ format: 'RAVENCIA_MAPPING_BUNDLE', version: 1, exportedAt: parsed.exportedAt ?? new Date().toISOString(), categories: parsed.categories, attributes: parsed.attributes, categoryMappings: parsed.categoryMappings ?? [], attributeMappings: parsed.attributeMappings ?? [], attributeValueMappings: parsed.attributeValueMappings ?? [] })
      setTransferOpen(true)
    }).catch(reason => setNotice(reason instanceof Error ? reason.message : 'Eşleştirme yedeği okunamadı.'))
  }
  const localCategoryManagerSection = (
    <article className="mapping-reference-card mapping-local-category-builder">
      <div className="mapping-section-heading"><div><h2><UiIcon name="addCategory" />1. Yeni Kategori Ekle</h2><p>Panel kategorilerini ekleyin, arayın veya kaldırın.</p></div></div>
      <div className="mapping-local-manager-dialog mapping-local-manager-dialog-standalone">
        <form className="platform-category-create" onSubmit={event => { event.preventDefault(); createCategory.mutate() }}><label>Yeni Panel Kategorisi<input aria-label="Yeni panel kategorisi adı" maxLength={160} value={categoryName} onChange={event => setCategoryName(event.target.value)} placeholder="Örn. Anne Bluz" required /></label><button disabled={createCategory.isPending}>{createCategory.isPending ? 'Ekleniyor…' : 'Kategori Ekle'}</button></form>
        <div className="category-library-tools"><div className={categoryLibraryOpen ? 'category-library-search-control is-open' : 'category-library-search-control'}><input aria-label="Eklenen panel kategorilerinde ara" value={categoryLibrarySearch} onFocus={() => setCategoryLibraryOpen(true)} onClick={() => setCategoryLibraryOpen(true)} onChange={event => setCategoryLibrarySearch(event.target.value)} placeholder="Kategori ara…" /><button type="button" className="category-library-search-toggle" aria-label={categoryLibraryOpen ? 'Kategorileri kapat' : 'Kategorileri aç'} aria-expanded={categoryLibraryOpen} onMouseDown={event => event.preventDefault()} onClick={() => setCategoryLibraryOpen(value => !value)}><span aria-hidden="true" /></button></div><select aria-label="Panel kategorilerini sırala" value={categoryLibrarySort} onChange={event => setCategoryLibrarySort(event.target.value as 'NAME_ASC' | 'NAME_DESC')}><option value="NAME_ASC">A–Z</option><option value="NAME_DESC">Z–A</option></select><span>{filteredCategoryLibrary.length.toLocaleString('tr-TR')} sonuç</span></div>
        <div className="category-library-list" ref={categoryListHeight.ref} style={categoryListHeight.style}>{filteredCategoryLibrary.slice(0, 100).map(category => <span key={category.id} className={category.id === localId ? 'active' : ''}><button type="button" onClick={() => { setLocalId(category.id); setExternalId('') }}>{category.name}</button><button type="button" className="category-library-remove" aria-label="Kategoriyi kaldır" onClick={() => archiveCategory.mutate(category)} disabled={archiveCategory.isPending}><UiIcon name="close" /></button></span>)}</div>
      </div>
    </article>
  )
  function selectMappingConnection(nextConnectionId: string) {
    setConnectionId(nextConnectionId)
    setLocalId('')
    setExternalId('')
    setExternalSearch('')
    setSavedPage(1)
    setAdvancedOpen(false)
    setExternalPickerOpen(false)
    setPanelPickerOpen(false)
    setNotice('')
  }
  function selectMappingPlatform(code: MappingPlatformCode) {
    setSelectedPlatformCode(code)
    setConnectionId(activeMappingConnectionsForPlatform(activeMappingConnections, code)[0]?.id ?? '')
    setLocalId('')
    setExternalId('')
    setExternalSearch('')
    setExternalPickerOpen(false)
    setPanelPickerOpen(false)
    setNotice('')
  }
  const mappingPlatformTabs = (
    <>
      <div className="mapping-category-platform-tabs mapping-platform-tabs rv-tabs rv-view-tabs" role="tablist" aria-label="Kategori eşleştirme pazaryeri">
        {shownMappingPlatforms.map(platform => {
          const available = activeMappingConnections.some(item => item.platformCode.toUpperCase() === platform.code)
          return <button type="button" role="tab" aria-selected={selectedPlatformCode === platform.code} className={`${selectedPlatformCode === platform.code ? 'active is-active' : ''}${available ? '' : ' is-unavailable'}`} title={available ? `${platform.label} kategorilerini göster` : `${platform.label} için aktif bağlantı yok`} onClick={() => selectMappingPlatform(platform.code)}><span className="rv-tab-label">{platformLogoSource(platform.code) && <img className="rv-view-tab-logo" src={platformLogoSource(platform.code)!} alt="" aria-hidden="true" />}{platform.label}</span></button>
        })}
      </div>
      {!connectionId && !connections.isLoading && !selectedPlatformConnections.length && (autoSelectedConnectionPlatform.current || !activeMappingConnections.length) ? <div className="unknown mapping-connection-empty" role="status"><strong>{selectedPlatform.label} bağlantısı gerekli</strong><p>Kategori referanslarını alıp eşleme yapabilmek için önce aktif bir {selectedPlatform.label} bağlantısı kurun.</p><Link className="button-link" to="/integrations">Entegrasyonlara Git</Link></div> : <div className="mapping-reference-refresh-row">{selectedPlatformConnections.length > 1 && <label className="mapping-reference-connection-select">Bağlantı<select aria-label="Mağaza bağlantısı" value={connectionId} onChange={event => selectMappingConnection(event.target.value)}>{selectedPlatformConnections.map(item => <option value={item.id} key={item.id}>{item.displayName} · {item.environment === 'PRODUCTION' ? 'Canlı' : 'Stage'} · {item.externalStoreId}</option>)}</select></label>}</div>}
    </>
  )
  return <section className="content f3 mapping-page mapping-workspace stitch-reference-mapping">{confirmDialog}<header className="mapping-reference-heading"><div><h1>Eşleştirme Ayarları</h1></div><div className="mapping-header-tools"><div className="mapping-reference-heading-actions" aria-label="Eşleştirme yedeği işlemleri"><div className="mapping-transfer-actions"><MobileActionMenu><button type="button" className="secondary" title="Dışa aktarılacak alanları seç" onClick={() => { setExportSelection({ categories: true, options: true, attributes: true, mappings: true }); setExportOpen(true) }} disabled={transferBusy || !connectionId}><UiIcon name="download" size={18} />{transferBusy ? 'Hazırlanıyor…' : 'Dışa Aktar'}</button><label className="mapping-transfer-upload" title="JSON eşleştirme yedeğini içe aktar"><UiIcon name="import" size={18} />İçe Aktar<input type="file" accept="application/json,.json" onChange={readMappingBundle} disabled={transferBusy} /></label></MobileActionMenu></div></div></div></header><MappingViewTabs active="category" />{notice && <MessageToast message={notice} onClose={() => setNotice('')} />}{localCategoryManagerSection}
     {/* eslint-disable-next-line jsx-a11y/click-events-have-key-events, jsx-a11y/no-noninteractive-element-interactions -- a click on the dimmed area closes the panel; the summary toggles it from the keyboard */}
     <article className="mapping-reference-card mapping-category-builder"><header className="mapping-builder-head"><h2><UiIcon name="link" />2. Yeni Kategori Eşleştirme</h2><button type="button" className="secondary" disabled={!connectionId || syncCategories.isPending} onClick={() => syncCategories.mutate()}><UiIcon name="refresh" />{syncCategories.isPending ? 'Kuyruğa Alınıyor…' : 'Senkronize Et'}</button></header>{mappingPlatformTabs}<div className="mapping-reference-grid"><div className="mapping-link-rail" aria-hidden="true"><UiIcon name="link" /><span>Eşleştir</span></div><section className="mapping-reference-local"><label>Panel Kategorisi<button type="button" className="panel-category-picker-trigger" aria-expanded={panelPickerOpen} onClick={() => setPanelPickerOpen(value => !value)} disabled={!connectionId}><span>{localLeaves.find(item => item.id === localId)?.path ?? 'Panel Kategorisi Seçin...'}</span><UiIcon name="chevronDown" /></button></label>{panelPickerOpen && <div className="panel-category-picker" role="dialog" aria-label="Panel kategorisi seçimi"><div><input aria-label="Panel kategorilerinde ara" value={panelPickerSearch} onChange={event => setPanelPickerSearch(event.target.value)} placeholder="Panel kategorilerinde ara..." /></div><div role="listbox">{panelPickerItems.length ? panelPickerItems.map(item => <button type="button" role="option" aria-selected={item.id === localId} key={item.id} onClick={() => { setLocalId(item.id); setExternalId(''); setExternalSearch(''); setPanelPickerOpen(false); setNotice('') }}>{item.path}</button>) : <span>Aramaya uygun panel kategorisi bulunamadı.</span>}</div></div>}<details className="mapping-local-manager" onClick={event => { if (event.target === event.currentTarget) event.currentTarget.removeAttribute('open') }}><summary><UiIcon name="settings" /><span><strong>Lokal Kategorileri Yönet</strong><small>Kategori ekleyin, arayın veya arşivleyin.</small></span><UiIcon name="chevronDown" /></summary><div className="mapping-local-manager-dialog"><header><div><strong>1. Yeni Kategori Ekle</strong><small>Panel kategorileri ekleyin, arayın veya kaldırın.</small></div><button type="button" aria-label="Kategori yönetim penceresini kapat" onClick={event => { event.preventDefault(); event.stopPropagation(); event.currentTarget.closest('details')?.removeAttribute('open') }}><UiIcon name="close" /></button></header><form className="platform-category-create" onSubmit={event => { event.preventDefault(); createCategory.mutate() }}><label>Yeni Panel Kategorisi<input aria-label="Yeni panel kategorisi adı" maxLength={160} value={categoryName} onChange={event => setCategoryName(event.target.value)} placeholder="Örn. Anne Bluz" required /></label><button disabled={createCategory.isPending}>{createCategory.isPending ? 'Ekleniyor…' : 'Kategori Ekle'}</button></form><div className="category-library-tools"><div className={categoryLibraryOpen ? 'category-library-search-control is-open' : 'category-library-search-control'}><input aria-label="Eklenen panel kategorilerinde ara" value={categoryLibrarySearch} onFocus={() => setCategoryLibraryOpen(true)} onClick={() => setCategoryLibraryOpen(true)} onChange={event => setCategoryLibrarySearch(event.target.value)} placeholder="Kategori ara…" /><button type="button" className="category-library-search-toggle" aria-label={categoryLibraryOpen ? 'Kategorileri kapat' : 'Kategorileri aç'} aria-expanded={categoryLibraryOpen} onMouseDown={event => event.preventDefault()} onClick={() => setCategoryLibraryOpen(value => !value)}><span aria-hidden="true" /></button></div><select aria-label="Panel kategorilerini sırala" value={categoryLibrarySort} onChange={event => setCategoryLibrarySort(event.target.value as 'NAME_ASC' | 'NAME_DESC')}><option value="NAME_ASC">A–Z</option><option value="NAME_DESC">Z–A</option></select><span>{filteredCategoryLibrary.length.toLocaleString('tr-TR')} sonuç</span></div><div className="category-chip-list">{filteredCategoryLibrary.slice(0, 100).map(category => <span key={category.id} className={category.id === localId ? 'active' : ''}><button type="button" onClick={() => { setLocalId(category.id); setExternalId('') }}>{category.name}</button><button type="button" className="category-chip-remove" aria-label="Kategoriyi kaldır" onClick={() => archiveCategory.mutate(category)} disabled={archiveCategory.isPending}><UiIcon name="close" /></button></span>)}</div></div></details></section><section className="mapping-reference-target"><label>{selectedPlatform.label} {productTypeCategories ? "Ürün Türü" : "Kategorisi"}</label><div className="mapping-target-controls"><div className="platform-category-picker-wrap"><button type="button" className="panel-category-picker-trigger" aria-expanded={externalPickerOpen} onClick={() => setExternalPickerOpen(value => !value)} disabled={!connectionId || !localId || references.isError || references.isLoading}><span title={selectedExternal ? cleanMarketplaceCategoryPath(selectedExternal.path) : undefined}>{selectedExternal ? cleanMarketplaceCategoryPath(selectedExternal.path) : `${selectedPlatform.label} ${productTypeCategories ? 'Ürün Türü' : 'Kategori'} Seçin...`}</span><UiIcon name="chevronDown" /></button>{externalPickerOpen && <div className="panel-category-picker platform-category-picker" role="dialog" aria-label={`${selectedPlatform.label} ${externalNoun} seçimi`}><div><input ref={focusOnMount} aria-label={`${selectedPlatform.label} ${externalNounPlural} ara`} value={externalSearch} onChange={event => setExternalSearch(event.target.value)} placeholder={references.isLoading ? 'Kategoriler yükleniyor...' : `${selectedPlatform.label} ${externalNounPlural} ara...`} /></div><div role="listbox">{visibleExternal.length ? visibleExternal.map(item => { const path = cleanMarketplaceCategoryPath(item.path); const ambiguous = path === item.name && externalLeaves.filter(category => cleanMarketplaceCategoryPath(category.path) === path).length > 1; return <button type="button" role="option" aria-selected={item.externalId === externalId} key={item.externalId} onClick={() => { setExternalId(item.externalId); setExternalPickerOpen(false) }} title={path}><span>{path}</span>{ambiguous && <small>Kategori no: {item.externalId}</small>}</button> }) : <span>{references.isError ? 'Kategori listesi alınamadı. Salt-okunur eşitlemeyi yeniden deneyin.' : 'Aramayla eşleşen kategori bulunamadı.'}</span>}</div></div>}</div></div></section></div>{categorySuggestions.length > 0 && <div className="category-suggestions" aria-label="Önerilen kategoriler"><span>Önerilen {selectedPlatform.label} kategorileri</span>{categorySuggestions.map(item => <button type="button" key={item.externalId} aria-pressed={item.externalId === externalId} className={item.externalId === externalId ? 'active' : ''} title={`Benzerlik ${item.score}/100`} onClick={() => { setExternalId(item.externalId); setExternalPickerOpen(false) }}>{cleanMarketplaceCategoryPath(item.path)}</button>)}</div>}<footer><span>{references.isLoading ? 'Kategori listesi yükleniyor…' : references.data ? `${externalLeaves.length.toLocaleString('tr-TR')} ${productTypeCategories ? 'ürün türü' : 'yaprak kategori'} · Güncellenme ${formatDateTime(references.data.fetchedAt)}` : references.isError ? 'Güncel kategori listesi bulunamadı' : 'Kategori listesi henüz alınmadı'}{mapping.data ? ` · Kayıt v${mapping.data.version}` : ''}</span><button type="button" disabled={!localId || !externalId || save.isPending || mapping.isLoading} onClick={() => save.mutate()}><UiIcon name="externalLink" />{save.isPending ? 'Kaydediliyor…' : mapping.data ? 'Eşleştirmeyi Güncelle' : 'Eşleştirmeyi Kaydet'}</button></footer></article>
     <article className="mapping-reference-card mapping-option-summary panel-library-section"><div className="mapping-section-heading"><div><h2><UiIcon name="grid" />3. Ürün Seçenekleri</h2><p>Renk ve beden gibi varyant seçeneklerini panele kaydedin.</p></div></div><PanelAttributeLibraryBuilder role="OPTION" attributes={localAttributes.data?.items.filter(item => item.isActive) ?? []} onNotice={setNotice} /></article>
 <article id="category-value-mapping" className="mapping-reference-card mapping-attribute-summary panel-library-section"><div className="mapping-section-heading"><div><h2><UiIcon name="slidersHorizontal" />4. Kategori Özellikleri ve Değerleri</h2><p>Ürün özelliklerini ve değerlerini doğrudan panele kaydedin.</p></div></div><PanelAttributeLibraryBuilder role="ATTRIBUTE" attributes={localAttributes.data?.items.filter(item => item.isActive) ?? []} onNotice={setNotice} /></article>
    <article className="mapping-reference-card mapping-saved-card">
      <header>
        <h2><UiIcon name="equal" />5. Kayıtlı Kategori Eşleştirmeleri</h2>
        <div className="mapping-saved-header-tools">
          <div className="mapping-saved-tabs mapping-platform-tabs rv-tabs rv-view-tabs" role="tablist" aria-label="Kayıtlı eşleştirme platformları">
            {shownMappingPlatforms.filter(platform => hasCategoryAttributes(platform.code)).map(platform => {
              const available = activeMappingConnections.some(item => item.platformCode.toUpperCase() === platform.code)
              return <button type="button" role="tab" aria-selected={selectedPlatformCode === platform.code} className={`${selectedPlatformCode === platform.code ? 'active is-active' : ''}${available ? '' : ' is-unavailable'}`} title={available ? `${platform.label} eşleştirmelerini göster` : `${platform.label} için aktif bağlantı yok`} onClick={() => { setSelectedPlatformCode(platform.code); setSavedPage(1); setLocalId(''); setExternalId(''); setExternalSearch(''); setNotice('') }}><span className="rv-tab-label">{platformLogoSource(platform.code) && <img className="rv-view-tab-logo" src={platformLogoSource(platform.code)!} alt="" aria-hidden="true" />}{platform.label}</span></button>
            })}
          </div>
          <input aria-label="Kayıtlı eşleştirmelerde ara" value={savedSearch} onChange={event => { setSavedSearch(event.target.value); setSavedPage(1) }} placeholder="Eşleştirme ara..." />
          <button type="button" className="mapping-icon-button mapping-sort-button" aria-label={savedSort === 'NAME_ASC' ? 'Kayıtlı eşleştirmeleri Z-A sırala' : 'Kayıtlı eşleştirmeleri A-Z sırala'} title={savedSort === 'NAME_ASC' ? 'Z-A sırala' : 'A-Z sırala'} onClick={() => { setSavedSort(current => current === 'NAME_ASC' ? 'NAME_DESC' : 'NAME_ASC'); setSavedPage(1) }}>{savedSort === 'NAME_ASC' ? 'A–Z' : 'Z–A'}</button>
        </div>
      </header>
      <div className="mapping-saved-table"><table><thead><tr><th>Panel Kategorisi</th><th>Platform</th><th>Platform Kategorisi</th><th>Durum</th><th>İşlemler</th></tr></thead><tbody>{sortedSavedRows.length ? visibleSavedRows.map(({ item, local, external }) => { const status = categoryMappingStatus(item); return <tr key={item.id}><td>{local?.path ?? item.localId}</td><td><span className="ms-platform"><img className={platformLogoClass(selectedPlatform.code)} src={platformLogoSource(selectedPlatform.code) ?? '/platforms/trendyol.webp'} alt="" aria-hidden="true" />{selectedPlatform.label}</span></td><td>{cleanMarketplaceCategoryPath(external?.path ?? item.externalId)}</td><td><span className={`mapping-table-status ${status.className}`} title={status.title}><MappingStateGlyph state={status.className === 'active' ? 'done' : status.className === 'pending' ? 'none' : 'partial'} />{status.label}</span></td><td><div className="mapping-row-actions">{hasCategoryAttributes(selectedPlatformCode) && <button type="button" className="mapping-feature-button" aria-label="Kategori özelliklerini düzenle" onClick={() => { setLocalId(item.localId); setExternalId(item.externalId); setAdvancedOpen(true) }}>Kategori Özellikleri</button>}<button type="button" className="mapping-icon-button mapping-delete-button" aria-label="Eşleştirmeyi sil" disabled={removeMapping.isPending} onClick={() => void confirm({ title: 'Kategori Eşleştirmesi Kaldırılsın mı?', message: 'Bu kategori eşleştirmesi kaldırılacak.', confirmLabel: 'Kaldır', destructive: true }).then(accepted => { if (accepted) removeMapping.mutate(item) })}><MappingDeleteIcon /></button></div></td></tr> }) : <tr><td className="mapping-empty-table-cell" colSpan={5}>{categoryMappings.isLoading ? `${selectedPlatform.label} eşleştirmeleri yükleniyor…` : !connectionId ? `${selectedPlatform.label} için aktif bağlantı bulunamadı.` : <div className="empty mapping-table-empty">Kayıtlı kategori eşleştirmesi bulunamadı.</div>}</td></tr>}</tbody></table></div>
      <footer><span>Toplam {sortedSavedRows.length.toLocaleString('tr-TR')} eşleştirme</span><Pagination className="pagination-controls" page={currentSavedPage} totalPages={savedPageCount} hasNext={currentSavedPage < savedPageCount} onPageChange={setSavedPage} onPrevious={() => setSavedPage(value => Math.max(1, value - 1))} onNext={() => setSavedPage(value => Math.min(savedPageCount, value + 1))} disabled={categoryMappings.isFetching} ariaLabel="Kayıtlı kategori eşleştirmeleri sayfaları" /></footer>
    </article>
{exportOpen && <div className="workspace-modal-backdrop mapping-transfer-backdrop" role="presentation" onMouseDown={() => { if (!transferBusy) setExportOpen(false) }}><DialogSurface as="section" onClose={() => { if (!transferBusy) setExportOpen(false) }} className="workspace-modal mapping-transfer-modal mapping-export-modal" aria-labelledby="mapping-export-title" onMouseDown={event => event.stopPropagation()}><header><div><h2 id="mapping-export-title">Dışa Aktarılacak Alanları Seçin</h2><p>Yedeğe dahil edilecek alanları seçin. Seçimleriniz tek bir JSON dosyasında toplanır.</p></div><button type="button" className="modal-close" onClick={() => setExportOpen(false)} disabled={transferBusy} aria-label="Dışa aktarma penceresini kapat"><UiIcon name="close" /></button></header><div className="mapping-transfer-options mapping-export-options"><label className="mapping-transfer-all-option"><input type="checkbox" checked={Object.values(exportSelection).every(Boolean)} onChange={event => setExportSelection({ categories: event.target.checked, options: event.target.checked, attributes: event.target.checked, mappings: event.target.checked })} /><span><strong>Tümü</strong><small>Yukarıdaki tüm alanları tek seferde seçer.</small></span><em>{Object.values(exportSelection).filter(Boolean).length}/4 seçili</em></label><div className="mapping-export-scope-grid"><label><input type="checkbox" checked={exportSelection.categories} onChange={event => setExportSelection(current => ({ ...current, categories: event.target.checked }))} /><span><strong>Panel Kategorileri</strong><small>Aktif kategori kayıtlarını aktarır.</small></span></label><label><input type="checkbox" checked={exportSelection.options} onChange={event => setExportSelection(current => ({ ...current, options: event.target.checked }))} /><span><strong>Ürün Seçenekleri</strong><small>Renk ve beden değerlerini aktarır.</small></span></label><label><input type="checkbox" checked={exportSelection.attributes} onChange={event => setExportSelection(current => ({ ...current, attributes: event.target.checked }))} /><span><strong>Kategori Özellikleri</strong><small>Özellik ve kayıtlı değerlerini aktarır.</small></span></label><label><input type="checkbox" checked={exportSelection.mappings} onChange={event => setExportSelection(current => ({ ...current, mappings: event.target.checked }))} /><span><strong>Kategori Eşleştirmeleri</strong><small>Kategori, özellik ve değer eşleşmelerini aktarır.</small></span></label></div></div><footer><span className="mapping-export-footer-hint">{Object.values(exportSelection).filter(Boolean).length ? 'Seçilen alanlar dışa aktarılmaya hazır.' : 'En az bir alan seçin.'}</span><button type="button" className="secondary" onClick={() => setExportOpen(false)} disabled={transferBusy}>Vazgeç</button><button type="button" onClick={() => void exportSelectedMappings()} disabled={transferBusy || !Object.values(exportSelection).some(Boolean)}>{transferBusy ? 'Hazırlanıyor…' : 'Seçilenleri Dışa Aktar'}</button></footer></DialogSurface></div>}
{transferOpen && transferBundle && <div className="workspace-modal-backdrop mapping-transfer-backdrop" role="presentation" onMouseDown={() => { if (!transferBusy) { setTransferOpen(false); setTransferBundle(null) } }}><DialogSurface as="section" onClose={() => { if (!transferBusy) { setTransferOpen(false); setTransferBundle(null) } }} className="workspace-modal mapping-transfer-modal" aria-labelledby="mapping-transfer-title" onMouseDown={event => event.stopPropagation()}><header><div><h2 id="mapping-transfer-title">İçe Aktarma Alanlarını Seçin</h2><p>{transferBundle.categories.length} kategori · {transferBundle.attributes.length} özellik · {transferBundle.categoryMappings.length + transferBundle.attributeMappings.length + transferBundle.attributeValueMappings.length} eşleşme</p></div><button type="button" className="modal-close" onClick={() => { setTransferOpen(false); setTransferBundle(null) }} disabled={transferBusy} aria-label="İçe aktarmayı kapat"><UiIcon name="close" /></button></header><div className="mapping-transfer-options">{([['categories', 'Kategoriler', 'Panel kategorilerini eksik olanları ekleyerek güncelle'], ['options', 'Ürün Seçenekleri', 'option- ile başlayan seçenek başlıklarını güncelle'], ['attributes', 'Ürün Özellikleri', 'Ürün özelliklerini ve değerlerini güncelle'], ['mappings', 'Eşleşmeler', 'Kategori, özellik ve değer eşleşmelerini uygula']] as Array<[MappingTransferScope, string, string]>).map(([scope, label, description]) => <label key={scope}><input type="checkbox" checked={transferSelection[scope]} onChange={event => setTransferSelection(current => ({ ...current, [scope]: event.target.checked }))} /><span><strong>{label}</strong><small>{description}</small></span></label>)}</div><p className="mapping-transfer-warning">Mevcut kayıtlar silinmez; aynı kod/ad ve değerler güncellenir, eksikler eklenir. Eşleşmeler seçili aktif {selectedPlatform.label} bağlantısının güncel snapshot’ı ile doğrulanır.</p><footer><button type="button" className="secondary" onClick={() => { setTransferOpen(false); setTransferBundle(null) }} disabled={transferBusy}>Vazgeç</button><button type="button" onClick={() => void importMappings()} disabled={transferBusy}>{transferBusy ? 'İçe Aktarılıyor…' : 'Seçilenleri Güncelle'}</button></footer></DialogSurface></div>}
      {advancedOpen && <div className="workspace-modal-backdrop mapping-advanced-backdrop" role="presentation" onMouseDown={() => setAdvancedOpen(false)}><DialogSurface as="section" onClose={() => setAdvancedOpen(false)} className="workspace-modal mapping-advanced-modal" aria-labelledby="mapping-advanced-title" onMouseDown={event => event.stopPropagation()}><header><div><p className="eyebrow">Kategori Çalışma Alanı</p><h2 id="mapping-advanced-title">Kategori Alanları ve Değerleri</h2><p>Pazaryeri alanlarını panel özellikleriyle eşleyin ve değer eşlemelerini tamamlayın.</p></div><button type="button" className="modal-close" onClick={() => setAdvancedOpen(false)} aria-label="Kategori çalışma alanını kapat"><UiIcon name="close" /></button></header><div className="mapping-advanced-modal-body"><section className="mapping-popup-content"><CategoryAttributeWorkspace platformCode={selectedPlatformCode} connectionId={connectionId} localCategoryId={localId} localCategories={localLeaves} localAttributes={localAttributes.data?.items.filter(item => item.isActive) ?? []} onNotice={setNotice} /></section></div></DialogSurface></div>}
  </section>
}
function slug(value: string) { return value.toLocaleLowerCase('tr-TR').replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '') || `attr-${Date.now()}` }
function attributeCodeForRole(title: string, role: 'ATTRIBUTE' | 'OPTION') { return `${role === 'OPTION' ? 'option-' : 'attribute-'}${slug(title)}` }
function isOptionAttribute(attribute: Pick<LocalAttribute, 'code' | 'name' | 'roles'>) {
  const normalizedName = normalizedAttributeKey(attribute.name)
  return attribute.code.trim().toLowerCase().startsWith('option-')
    || attribute.roles?.some(role => role.toUpperCase() === 'OPTION') === true
    || isVariantOptionAttributeName(normalizedName)
}
function cleanMarketplaceCategoryPath(value: string) {
  return value.replace(/\[TDG\]\s*/gi, '').replace(/\(\s*TDG\s*\)\s*/gi, '').replace(/\s*\/\s*/g, ' / ').trim()
}
function categoryMappingStatus(item: CatalogMapping) {
  if (item.status !== 'VERIFIED' && item.status !== 'ACTIVE') return { className: 'error', label: mappingStatusLabel(item.status), title: 'Kategori eşleştirmesi doğrulanmadı.' }
  if (item.missingRequiredAttributeCount === null || item.missingRequiredAttributeCount === undefined) return { className: 'pending', label: 'Kontrol bekliyor', title: 'Zorunlu kategori alanları için güncel kontrol verisi henüz hazır değil.' }
  if (item.missingRequiredAttributeCount > 0) return { className: 'warning', label: 'Zorunlu Alan Eksik', title: `${item.missingRequiredAttributeCount} zorunlu kategori alanı eşlenmedi.` }
  return { className: 'active', label: 'Aktif', title: 'Kategori ve zorunlu alan eşlemeleri hazır.' }
}
function AttributeCategoryAssignmentModal({ attribute, role, categories, onClose, onNotice }: { attribute: LocalAttribute; role: 'ATTRIBUTE' | 'OPTION'; categories: LocalCategory[]; onClose: () => void; onNotice: (value: string) => void }) {
  const client = useQueryClient()
  const [search, setSearch] = useState('')
  const [selectedCategoryIds, setSelectedCategoryIds] = useState<string[]>([])
  const [initialCategoryIds, setInitialCategoryIds] = useState<string[]>([])
  const [requirementsByCategory, setRequirementsByCategory] = useState<Record<string, CategoryRequirementView[]>>({})
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')
  const categoryKey = categories.map(category => `${category.id}:${category.version}`).join(',')
  const activeCategories = categories.filter(category => category.isActive && category.isLeaf)
  const normalizedSearch = search.trim().toLocaleLowerCase('tr-TR')
  const visibleCategories = activeCategories.filter(category => !normalizedSearch || `${category.path} ${category.name}`.toLocaleLowerCase('tr-TR').includes(normalizedSearch)).slice().sort((left, right) => left.path.localeCompare(right.path, 'tr-TR', { sensitivity: 'base', numeric: true }))

  useScrollLock(true, true)

  const currentActiveCategories = useEffectEvent(() => activeCategories)
  useEffect(() => {
    let cancelled = false
    const categoriesNow = currentActiveCategories()
    setLoading(true)
    setError('')
    void Promise.all(categoriesNow.map(async category => [category.id, await hubApi<CategoryRequirementView[]>(`/catalog/categories/${category.id}/attribute-requirements`)] as const))
      .then(entries => {
        if (cancelled) return
        const nextRequirements = Object.fromEntries(entries) as Record<string, CategoryRequirementView[]>
        const nextSelected = categoriesNow.filter(category => nextRequirements[category.id]?.some(item => item.attributeId === attribute.id)).map(category => category.id)
        setRequirementsByCategory(nextRequirements)
        setSelectedCategoryIds(nextSelected)
        setInitialCategoryIds(nextSelected)
      })
      .catch(reason => { if (!cancelled) setError(reason instanceof Error ? reason.message : 'Kategori kapsamı alınamadı.') })
      .finally(() => { if (!cancelled) setLoading(false) })
    return () => { cancelled = true }
  }, [attribute.id, categoryKey])

  function isSelected(categoryId: string) { return selectedCategoryIds.includes(categoryId) }
  function toggleCategory(categoryId: string) {
    setSelectedCategoryIds(current => current.includes(categoryId) ? current.filter(id => id !== categoryId) : [...current, categoryId])
  }
  function toCommand(item: CategoryRequirementView | AttributeRequirementCommand): AttributeRequirementCommand {
    return { attributeId: item.attributeId, isRequired: item.isRequired, allowsCustomValue: item.allowsCustomValue, displayOrder: item.displayOrder, role: item.role }
  }
  async function save() {
    if (loading || saving) return
    setSaving(true)
    setError('')
    const initial = new Set(initialCategoryIds)
    const selected = new Set(selectedCategoryIds)
    const changedCategories = activeCategories.filter(category => initial.has(category.id) !== selected.has(category.id))
    try {
      for (const category of changedCategories) {
        const current = requirementsByCategory[category.id] ?? []
        const existing = current.find(item => item.attributeId === attribute.id)
        const next = current.filter(item => item.attributeId !== attribute.id).map(toCommand)
        if (selected.has(category.id)) {
          const displayOrder = existing?.displayOrder ?? Math.max(-1, ...current.map(item => item.displayOrder)) + 1
          next.push({ attributeId: attribute.id, isRequired: existing?.isRequired ?? false, allowsCustomValue: existing?.allowsCustomValue ?? attribute.values.length === 0, displayOrder, role })
        }
        next.sort((left, right) => left.displayOrder - right.displayOrder)
        await hubApi(`/catalog/categories/${category.id}/attribute-requirements`, { method: 'PUT', headers: { 'If-Match': `"v${category.version}"` }, body: JSON.stringify(next) })
        await client.invalidateQueries({ queryKey: ['category-requirements', category.id] })
        client.setQueryData<Page<LocalCategory>>(['categories', 'mapping-builder-assignment'], currentCategories => currentCategories ? { ...currentCategories, items: currentCategories.items.map(item => item.id === category.id ? { ...item, version: item.version + 1 } : item) } : currentCategories)
        client.setQueryData<Page<LocalCategory>>(['categories', 'mapping'], currentCategories => currentCategories ? { ...currentCategories, items: currentCategories.items.map(item => item.id === category.id ? { ...item, version: item.version + 1 } : item) } : currentCategories)
      }
      onNotice(changedCategories.length ? `${attribute.name} için ${selectedCategoryIds.length} kategori kapsamı kaydedildi.` : 'Kategori kapsamındaki değişiklik bulunmuyor.')
      onClose()
    } catch (reason) {
      const message = reason instanceof Error ? reason.message : 'Kategori kapsamı kaydedilemedi.'
      setError(message)
      onNotice(message)
    } finally { setSaving(false) }
  }

  return <DialogSurface as="div" onClose={onClose} className="panel-attribute-modal panel-attribute-category-modal" aria-labelledby="attribute-category-assignment-title"><div className="panel-attribute-modal-card panel-attribute-category-modal-card"><header><div><strong id="attribute-category-assignment-title">{attribute.name} Kategorileri</strong><small>{role === 'OPTION' ? 'Ürün seçeneği' : 'Ürün özelliği'} · görüneceği kategorileri seçin</small></div><button type="button" aria-label="Kategori kapsamı penceresini kapat" onClick={onClose} disabled={saving}><UiIcon name="close" /></button></header><div className="panel-attribute-category-search"><input data-autofocus aria-label="Kategorilerde ara" value={search} onChange={event => setSearch(event.target.value)} placeholder="Kategori yolu veya adı ara..." /><span>{selectedCategoryIds.length} seçili</span></div>{loading ? <div className="panel-attribute-category-list panel-attribute-category-list-loading" role="status" aria-live="polite" aria-busy="true" aria-label="Kategori kapsamları yükleniyor">{Array.from({ length: Math.max(12, activeCategories.length) }, (_, index) => <span className="panel-attribute-category-skeleton" key={index} aria-hidden="true" />)}</div> : error && !Object.keys(requirementsByCategory).length ? <div className="mapping-empty-row attribute-feedback is-error">{error}</div> : <div className="panel-attribute-category-list" role="listbox" aria-label="Özelliğin görüneceği kategoriler">{visibleCategories.length ? visibleCategories.map(category => <button type="button" role="option" aria-selected={isSelected(category.id)} className={isSelected(category.id) ? 'active' : ''} key={category.id} onClick={() => toggleCategory(category.id)}><span>{category.path}</span><i aria-hidden="true">{isSelected(category.id) ? <UiIcon name="check" /> : null}</i></button>) : <div className="mapping-empty-row">Aramaya uygun kategori bulunamadı.</div>}</div>}<footer><button type="button" className="secondary" onClick={onClose} disabled={saving}>Vazgeç</button><button type="button" onClick={() => void save()} disabled={loading || saving || !!error && !Object.keys(requirementsByCategory).length}>{saving ? 'Kaydediliyor…' : 'Kapsamı Kaydet'}</button></footer></div></DialogSurface>
}
function PanelAttributeLibraryBuilder({ role, attributes, onNotice }: { role: 'ATTRIBUTE' | 'OPTION'; attributes: LocalAttribute[]; onNotice: (value: string) => void }) {
  const client = useQueryClient()
  const [title, setTitle] = useState(''); const [values, setValues] = useState<string[]>([]); const [valueDraft, setValueDraft] = useState(''); const [editingId, setEditingId] = useState(''); const [newValues, setNewValues] = useState(''); const [editingValue, setEditingValue] = useState<{ id: string; value: string } | null>(null); const [renamingTitle, setRenamingTitle] = useState<string | null>(null); const [categoryAssignmentId, setCategoryAssignmentId] = useState(''); const [feedback, setFeedback] = useState(''); const [feedbackTone, setFeedbackTone] = useState<'success' | 'error'>('success'); const [sortDirection, setSortDirection] = useState<1 | -1>(1)
  const isOption = role === 'OPTION'
  const [confirm, confirmDialog] = useConfirmDialog()
  const matchingRecords = attributes.filter(attribute => !isWebColorAttributeName(attribute.name) && (isOption ? isOptionAttribute(attribute) : !isOptionAttribute(attribute)))
  const records = isOption ? matchingRecords : sortByTurkishName(matchingRecords)
  const editingAttribute = records.find(attribute => attribute.id === editingId) ?? null
  const categoryAssignmentAttribute = records.find(attribute => attribute.id === categoryAssignmentId) ?? null
  const categories = useQuery({ queryKey: ['categories', 'mapping-builder-assignment'], queryFn: () => loadAllPages<LocalCategory>('/catalog/categories'), enabled: !!categoryAssignmentAttribute })
  function showFeedback(message: string, tone: 'success' | 'error' = 'success') { setFeedback(message); setFeedbackTone(tone) }
  useScrollLock(Boolean(editingAttribute), true)
  useEffect(() => { setSortDirection(1) }, [editingId])
  function updateCache(attribute: LocalAttribute) {
    client.setQueryData<Page<LocalAttribute>>(['attributes', 'mapping-builder'], current => {
      if (!current) return current
      const found = current.items.some(item => item.id === attribute.id)
      return { ...current, items: found ? current.items.map(item => item.id === attribute.id ? attribute : item) : [...current.items, attribute] }
    })
  }
  async function create() {
    try {
      if (!title.trim()) return showFeedback(isOption ? 'Seçenek başlığı girin.' : 'Özellik başlığı girin.', 'error')
      const parsedValues = values
      const attribute = await hubApi<LocalAttribute>('/catalog/attributes', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ code: attributeCodeForRole(title, role), name: title.trim(), dataType: parsedValues.length ? 'SINGLE_SELECT' : 'TEXT', selectionMode: parsedValues.length ? 'SINGLE' : null, unit: null, values: parsedValues.map((value, index) => ({ value, sortOrder: index })) }) })
      updateCache(attribute); setEditingId(attribute.id); setTitle(''); setValues([]); setValueDraft(''); showFeedback(`${attribute.name} panele kaydedildi.`); onNotice(`${attribute.name} panele kaydedildi.`)
    } catch (reason) { showFeedback(reason instanceof Error ? reason.message : 'Kayıt oluşturulamadı.', 'error') }
  }
  function addInitialValues(nextValues: string[]) {
    setValues(current => {
      const merged = [...current]
      nextValues.forEach(value => { if (!merged.some(existing => existing.toLocaleLowerCase('tr-TR') === value.toLocaleLowerCase('tr-TR'))) merged.push(value) })
      return merged
    })
  }
  async function addValues(attributeId: string, source: string) {
    try {
      const parsedValues = source.split(',').map(value => value.trim()).filter(Boolean)
      if (!parsedValues.length) return showFeedback('Eklenecek değerleri Enter veya Ekle ile girin.', 'error')
      const attribute = await hubApi<LocalAttribute>(`/catalog/attributes/${attributeId}/values`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify(parsedValues.map((value, index) => ({ value, sortOrder: index }))) })
      updateCache(attribute); setNewValues(''); showFeedback('Yeni değerler panele kaydedildi.'); onNotice('Yeni değerler panele kaydedildi.')
    } catch (reason) { showFeedback(reason instanceof Error ? reason.message : 'Değerler kaydedilemedi.', 'error') }
  }
  // The attribute's shown name; its code and every mapping that points at it stay. Options (Renk, Beden) are matched
  // to variant options by name, so only attributes get the pencil.
  async function renameAttribute(attribute: LocalAttribute, nextName: string) {
    const name = nextName.trim()
    if (!name || name === attribute.name) { setRenamingTitle(null); return }
    try {
      const saved = await hubApi<LocalAttribute>(`/catalog/attributes/${attribute.id}`, { method: 'PATCH', headers: { 'If-Match': `"v${attribute.version}"` }, body: JSON.stringify({ name }) })
      updateCache(saved); setRenamingTitle(null); showFeedback(`Başlık “${saved.name}” olarak değiştirildi.`)
    } catch (reason) { showFeedback(reason instanceof Error ? reason.message : 'Başlık değiştirilemedi.', 'error') }
  }
  async function removeAttribute(attribute: LocalAttribute) {
    if (!(await confirm({ title: 'Başlık Kaldırılsın mı?', message: `“${attribute.name}” ve bağlı değerleri kaldırılacak.`, confirmLabel: 'Kaldır', destructive: true }))) return
    try {
      await hubApi<LocalAttribute>(`/catalog/attributes/${attribute.id}`, { method: 'DELETE', headers: { 'If-Match': `"v${attribute.version}"` } })
      client.setQueryData<Page<LocalAttribute>>(['attributes', 'mapping-builder'], current => current ? { ...current, items: current.items.filter(item => item.id !== attribute.id) } : current)
      setEditingId(''); showFeedback(`${attribute.name} kaldırıldı.`)
    } catch (reason) { showFeedback(reason instanceof Error ? reason.message : 'Başlık kaldırılamadı.', 'error') }
  }
  async function removeValue(attributeId: string, valueId: string) {
    try { const attribute = await hubApi<LocalAttribute>(`/catalog/attributes/${attributeId}/values/${valueId}`, { method: 'DELETE' }); updateCache(attribute); setEditingValue(null); showFeedback('Değer kaldırıldı.') } catch (reason) { showFeedback(reason instanceof Error ? reason.message : 'Değer kaldırılamadı.', 'error') }
  }
  async function updateValue(attributeId: string, originalId: string, nextValue: string, sortOrder: number) {
    const value = nextValue.trim()
    if (!value) return showFeedback('Değer boş olamaz.', 'error')
    try {
      const attribute = await hubApi<LocalAttribute>(`/catalog/attributes/${attributeId}/values/${originalId}`, { method: 'PUT', headers: { 'If-Match': `"v${editingAttribute?.version ?? 0}"` }, body: JSON.stringify({ value, sortOrder }) })
      updateCache(attribute); setEditingValue(null); showFeedback('Değer güncellendi.')
    } catch (reason) { showFeedback(reason instanceof Error ? reason.message : 'Değer güncellenemedi.', 'error') }
  }
  async function sortValuesAlphabetically(attribute: LocalAttribute, direction: 1 | -1) {
    const current = attribute.values.filter(value => value.isActive).slice().sort((left, right) => left.sortOrder - right.sortOrder || left.value.localeCompare(right.value, 'tr'))
    const next = current.slice().sort((left, right) => direction * left.value.localeCompare(right.value, 'tr-TR', { sensitivity: 'base', numeric: true }) || current.indexOf(left) - current.indexOf(right))
    if (next.every((value, index) => value.id === current[index]?.id)) {
      showFeedback(direction === 1 ? 'Değerler zaten A–Z sıralı.' : 'Değerler zaten Z–A sıralı.')
      return true
    }
    try {
      const updated = await hubApi<LocalAttribute>(`/catalog/attributes/${attribute.id}/values/order`, { method: 'PUT', headers: { 'If-Match': `"v${attribute.version}"` }, body: JSON.stringify({ valueIds: next.map(value => value.id) }) })
      updateCache(updated)
      return true
    } catch (reason) { showFeedback(reason instanceof Error ? reason.message : 'Değerler sıralanamadı.', 'error'); return false }
  }
  async function toggleSortValues(attribute: LocalAttribute) {
    const direction = sortDirection
    if (await sortValuesAlphabetically(attribute, direction)) setSortDirection(direction === 1 ? -1 : 1)
  }
  const editingValues = editingAttribute?.values.filter(value => value.isActive).slice().sort((left, right) => left.sortOrder - right.sortOrder || left.value.localeCompare(right.value, 'tr')) ?? []
  return <div className="panel-attribute-library">{confirmDialog}<div className="panel-attribute-create"><label>{isOption ? 'Yeni Seçenek Başlığı' : 'Yeni Özellik Başlığı'}<input value={title} onChange={event => setTitle(event.target.value)} placeholder={isOption ? 'Örn. Beden, Renk' : 'Örn. Materyal, Kol Boyu'} /></label><label>İlk Değerler <TokenValueInput values={values} draft={valueDraft} onDraftChange={setValueDraft} onAdd={addInitialValues} onRemove={value => setValues(current => current.filter(item => item !== value))} placeholder="Değer yazın, Enter veya Ekle" /></label><button type="button" onClick={() => void create()}><UiIcon name="save" />Panele Kaydet</button></div>{feedback && <p className={`attribute-feedback ${feedbackTone === 'success' ? 'is-success' : 'is-error'}`} role="status">{feedback}</p>}<div className="panel-attribute-records">{records.length ? records.map(attribute => <article className={attribute.id === editingId || attribute.id === categoryAssignmentId ? 'selected' : ''} key={attribute.id}><button type="button" className="panel-attribute-record-main" aria-label={`${attribute.name}: değerler, kategoriler ve silme`} onClick={() => setEditingId(attribute.id)}><strong>{attribute.name}</strong><small>{attribute.values.filter(value => value.isActive).length ? `${attribute.values.filter(value => value.isActive).length} değer kaydedildi` : 'Serbest değer'}</small></button></article>) : <div className="mapping-empty-row"><span>{isOption ? 'Henüz panel seçeneği oluşturulmadı.' : 'Henüz panel özelliği oluşturulmadı.'}</span></div>}</div>{editingAttribute && <DialogSurface as="div" onClose={() => { setEditingId(''); setEditingValue(null) }} className="panel-attribute-modal" aria-label={`${editingAttribute.name} değer düzenleme`}><div className="panel-attribute-modal-card"><header><div>{renamingTitle !== null ? <form className="panel-attribute-title-edit" onSubmit={event => { event.preventDefault(); void renameAttribute(editingAttribute, renamingTitle) }}><input ref={focusOnMount} value={renamingTitle} maxLength={160} aria-label="Başlık adı" onChange={event => setRenamingTitle(event.target.value)} onKeyDown={event => { if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); setRenamingTitle(null) } }} /><button type="submit" aria-label="Başlık adını kaydet"><UiIcon name="check" /></button><button type="button" aria-label="Vazgeç" onClick={() => setRenamingTitle(null)}><UiIcon name="close" /></button></form> : <span className="panel-attribute-title"><strong>{editingAttribute.name}</strong>{!isOption && <button type="button" className="panel-attribute-title-pencil" aria-label={`${editingAttribute.name} başlığını yeniden adlandır`} title="Başlığı yeniden adlandır" onClick={() => setRenamingTitle(editingAttribute.name)}><UiIcon name="edit" /></button>}</span>}<small>{isOption ? 'Panel seçeneği' : 'Panel özelliği'} · değerleri yönetin</small></div><div className="panel-attribute-modal-actions"><button type="button" className="secondary" onClick={() => { const id = editingAttribute.id; setEditingId(''); setEditingValue(null); setCategoryAssignmentId(id) }}>Kategoriler</button><button type="button" className="secondary is-danger" onClick={() => void removeAttribute(editingAttribute)}>Başlığı Sil</button></div><button type="button" aria-label="Düzenleme penceresini kapat" onClick={() => { setEditingId(''); setEditingValue(null) }}><UiIcon name="close" /></button></header><div className="panel-attribute-add-value"><input value={newValues} onChange={event => setNewValues(event.target.value)} placeholder="Yeni değerleri virgülle yazın" /><button type="button" disabled={!newValues.trim()} onClick={() => void addValues(editingAttribute.id, newValues)}>Değer Ekle</button><div className="panel-attribute-sort-actions" aria-label="Değerleri alfabetik sırala"><button type="button" title={`Değerleri ${sortDirection === 1 ? 'A–Z' : 'Z–A'} sırala`} aria-label={`Değerleri ${sortDirection === 1 ? 'A–Z' : 'Z–A'} sırala`} onClick={() => void toggleSortValues(editingAttribute)}>{sortDirection === 1 ? 'A–Z' : 'Z–A'}</button></div></div><div className="panel-attribute-value-editor">{editingValues.length ? editingValues.map(value => <div key={value.id}>{editingValue?.id === value.id ? <input ref={focusOnMount} value={editingValue.value} onChange={event => setEditingValue({ id: value.id, value: event.target.value })} /> : <span>{value.value}</span>}<div>{editingValue?.id === value.id ? <><button type="button" aria-label={`${value.value} değerini kaydet`} onClick={() => void updateValue(editingAttribute.id, value.id, editingValue.value, value.sortOrder)}><UiIcon name="check" /></button><button type="button" aria-label="Değer düzenlemeyi iptal et" onClick={() => setEditingValue(null)}><UiIcon name="close" /></button></> : <><button type="button" aria-label={`${value.value} değerini düzenle`} onClick={() => setEditingValue({ id: value.id, value: value.value })}><UiIcon name="edit" /></button><button type="button" aria-label={`${value.value} değerini sil`} onClick={() => void removeValue(editingAttribute.id, value.id)}><UiIcon name="close" /></button></>}</div></div>) : <span className="panel-value-empty">Henüz değer eklenmedi.</span>}</div></div></DialogSurface>}{categoryAssignmentAttribute && <AttributeCategoryAssignmentModal attribute={categoryAssignmentAttribute} role={role} categories={categories.data?.items ?? []} onClose={() => setCategoryAssignmentId('')} onNotice={message => { showFeedback(message); onNotice(message) }} />}</div>
}
function TokenValueInput({ values, draft, onDraftChange, onAdd, onRemove, placeholder }: { values: string[]; draft: string; onDraftChange: (value: string) => void; onAdd: (values: string[]) => void; onRemove: (value: string) => void; placeholder: string }) {
  function addDraft() {
    const nextValues = draft.split(',').map(value => value.trim()).filter(Boolean)
    if (!nextValues.length) return
    onAdd(nextValues)
    onDraftChange('')
  }
  return <div className="panel-value-token-input"><div className="panel-value-token-entry"><input value={draft} onChange={event => onDraftChange(event.target.value)} onKeyDown={event => { if (event.key === 'Enter' || event.key === ',') { event.preventDefault(); addDraft() } }} placeholder={placeholder} /><button type="button" onClick={addDraft} disabled={!draft.trim()}>Ekle</button></div>{values.length > 0 && <div className="panel-value-token-list" aria-label="Eklenen ilk değerler">{values.map(value => <span className="panel-value-token" key={value}><span>{value}</span><button type="button" aria-label={`${value} değerini kaldır`} onClick={() => onRemove(value)}><UiIcon name="close" /></button></span>)}</div>}</div>
}
function CategoryAttributeWorkspace({ platformCode, connectionId, localCategoryId, localCategories, localAttributes, onNotice }: { platformCode: MappingPlatformCode; connectionId: string; localCategoryId: string; localCategories: LocalCategory[]; localAttributes: LocalAttribute[]; onNotice: (value: string) => void }) {
  const client = useQueryClient()
  const platformLabel = mappingPlatformLabel(platformCode)
  const [attributeCategoryId, setAttributeCategoryId] = useState(localCategoryId)
  const categoryMappings = useQuery({ queryKey: ['category-mappings', connectionId], queryFn: () => hubApi<CatalogMapping[]>(`/mappings/categories?connectionId=${encodeURIComponent(connectionId)}`), enabled: !!connectionId, retry: false })
  const matchedCategories = (categoryMappings.data ?? []).flatMap(mapping => {
    const category = localCategories.find(item => item.id === mapping.localId)
    return category ? [{ category, mapping }] : []
  })
  const matchedCategoryIds = matchedCategories.map(item => item.category.id).join(',')
  // matchedCategories is rebuilt every render; its ids (matchedCategoryIds) say when it really changed
  const keepAttributeCategoryMatched = useEffectEvent(() => {
    if (!matchedCategories.length) { if (attributeCategoryId) setAttributeCategoryId(''); return }
    if (!matchedCategories.some(item => item.category.id === attributeCategoryId)) setAttributeCategoryId(matchedCategories[0].category.id)
  })
  useEffect(() => keepAttributeCategoryMatched(), [attributeCategoryId, matchedCategoryIds])
  const followLocalCategory = useEffectEvent(() => {
    if (localCategoryId && matchedCategories.some(item => item.category.id === localCategoryId)) setAttributeCategoryId(localCategoryId)
  })
  useEffect(() => followLocalCategory(), [localCategoryId, matchedCategoryIds])
  const categoryRequirements = useQuery({ queryKey: ['category-requirements', attributeCategoryId, 'attribute-workspace'], queryFn: () => hubApi<CategoryRequirementView[]>(`/catalog/categories/${attributeCategoryId}/attribute-requirements`), enabled: !!attributeCategoryId })
  const categoryMapping = useQuery({ queryKey: ['category-mapping', attributeCategoryId, connectionId, 'embedded'], queryFn: () => hubApi<CatalogMapping | null>(`/mappings/categories/${attributeCategoryId}?connectionId=${encodeURIComponent(connectionId)}`), enabled: !!attributeCategoryId && !!connectionId, retry: false })
  const categoryScope = categoryMapping.data?.externalId ?? ''
  // Same cache entry as the category mapping card, so the target path rarely costs a request.
  const categoryReferences = useQuery({ queryKey: ['reference-categories', connectionId], queryFn: () => hubApi<ReferenceData>(`/reference-data/categories?connectionId=${encodeURIComponent(connectionId)}`), enabled: !!connectionId && !!categoryScope, retry: false, staleTime: 5 * 60_000 })
  const targetCategoryPath = cleanMarketplaceCategoryPath(categoryReferences.data?.items.find(item => item.externalId === categoryScope)?.path ?? '') || (categoryScope ? `Kategori no: ${categoryScope}` : '')
  const targetCategoryMapping = matchedCategories.find(item => item.category.id === attributeCategoryId)?.mapping
  const targetCategoryStatus = targetCategoryMapping ? categoryMappingStatus(targetCategoryMapping) : null
  const references = useQuery({ queryKey: ['reference-attributes', connectionId, categoryScope, 'embedded'], queryFn: () => hubApi<ReferenceData>(`/reference-data/categories/${encodeURIComponent(categoryScope)}/attributes?connectionId=${encodeURIComponent(connectionId)}`), enabled: !!connectionId && !!categoryScope, retry: false })
  const sync = useMutation({ mutationFn: () => hubApi(`/connections/${connectionId}/reference-sync-jobs?resourceType=CATEGORY_ATTRIBUTES&parentExternalId=${encodeURIComponent(categoryScope)}`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: '{}' }), onSuccess: async () => { onNotice(`${platformLabel} kategori özellikleri salt-okunur eşitleme kuyruğuna alındı.`); for (const delay of [500, 1000, 2000, 4000, 8000, 15000]) { await new Promise(resolve => window.setTimeout(resolve, delay)); await client.invalidateQueries({ queryKey: ['reference-attributes', connectionId, categoryScope, 'embedded'] }) } }, onError: reason => onNotice(reason instanceof Error ? reason.message : 'Kategori özellikleri eşitlenemedi.') })
  // A category whose field list was never read is read once by itself when the window opens, instead of asking for it.
  const startAttributeSync = sync.mutate
  const attributeSyncPending = sync.isPending
  useEffect(() => {
    if (!categoryScope || attributeSyncPending || !references.isError || !(references.error instanceof ApiRequestError) || references.error.code !== 'REFERENCE_SNAPSHOT_UNAVAILABLE') return
    const autoSyncKey = `${connectionId}:${categoryScope}`
    if (autoQueuedCategoryAttributeSyncs.has(autoSyncKey)) return
    autoQueuedCategoryAttributeSyncs.add(autoSyncKey)
    startAttributeSync()
  }, [categoryScope, connectionId, references.isError, references.error, attributeSyncPending, startAttributeSync])
  const mappings = useQuery({ queryKey: ['attribute-mappings', connectionId, categoryScope], queryFn: () => hubApi<CatalogMapping[]>(`/mappings/attributes?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(categoryScope)}`), enabled: !!connectionId && !!categoryScope, retry: false })
  const requirements = categoryRequirements.data ?? []
  const remoteItems = references.data?.items.filter(item => item.isActive).map(item => ({ ...item, name: displayAttributeName(item.name) })) ?? []
  const categoryAttributes = requirements.map(item => item.attribute).filter(attribute => attribute.isActive !== false)
  const allPanelAttributes = [...localAttributes, ...categoryAttributes].filter((attribute, index, items) => items.findIndex(candidate => candidate.id === attribute.id) === index)
  const panelColorAttribute = allPanelAttributes.find(attribute => attribute.isActive !== false && isPanelColorAttributeName(attribute.name))
    ?? allPanelAttributes.find(attribute => attribute.isActive !== false && isWebColorAttributeName(attribute.name))
  const requirementOptionIds = new Set(requirements.filter(item => item.role === 'OPTION').map(item => item.attributeId))
  const webColorRemote = platformCode === 'TRENDYOL' ? remoteItems.find(item => isWebColorAttributeName(item.name)) : undefined
  // Section 3 creates global panel attributes. They are not required to have a
  // row in category_requirements before they can be mapped to a marketplace
  // field; the mapping workspace is where that category-specific relationship
  // is established. Keep explicit OPTION requirements as a fallback for
  // existing records whose code predates the role prefix convention.
  const optionAttributes = platformCode === 'TRENDYOL' ? allPanelAttributes
    .filter(attribute => requirementOptionIds.has(attribute.id) || isOptionAttribute(attribute))
    // Web Color and the real Renk field can share the panel Renk source, but
    // they are separate marketplace mappings. Keep panel Renk in the normal
    // option list so both cards can be configured independently.
    .filter(attribute => !isWebColorAttributeName(attribute.name))
    .filter((item, index, items) => items.findIndex(candidate => candidate.id === item.id) === index)
    .sort((left, right) => Number(requirements.some(item => item.role === 'OPTION' && item.attributeId === right.id && item.isRequired)) - Number(requirements.some(item => item.role === 'OPTION' && item.attributeId === left.id && item.isRequired)) || left.name.localeCompare(right.name, 'tr')) : []
  const allMappings = mappings.data ?? []
  const existingWebColorMapping = webColorRemote ? allMappings.find(item => item.externalId === webColorRemote.externalId) ?? null : null
  const specialColorAttribute = allPanelAttributes.find(item => item.id === existingWebColorMapping?.localId) ?? panelColorAttribute
  const specialColorOption = webColorRemote && specialColorAttribute ? { attribute: specialColorAttribute, remote: webColorRemote } : null
  const optionAttributeIds = new Set([...optionAttributes.map(item => item.id), ...(specialColorOption ? [specialColorOption.attribute.id] : [])])
  const sizeRemoteItems = remoteItems.filter(item => isSizeAttributeName(item.name))
  const sizeRemoteExternalIds = new Set(sizeRemoteItems.map(item => item.externalId))
  const sizeOptionAttributeIds = new Set(optionAttributes.filter(isSizeAttribute).map(item => item.id))
  const productAttributes = allPanelAttributes.filter(attribute => attribute.isActive !== false)
  const categoryOptionIds = new Set(requirements.filter(item => item.role === 'OPTION').map(item => item.attributeId))
  const colorRemoteExternalIds = new Set(remoteItems.filter(item => isPanelColorAttributeName(item.name)).map(item => item.externalId))
  const colorLocalAttributeIds = new Set(optionAttributes.filter(attribute => isPanelColorAttributeName(attribute.name)).map(attribute => attribute.id))
  const optionMappings = allMappings.filter(item => optionAttributeIds.has(item.localId) && !colorRemoteExternalIds.has(item.externalId))
  const optionAttributeNames = new Set([...optionAttributes.filter(attribute => !sizeOptionAttributeIds.has(attribute.id)), ...(specialColorOption ? [specialColorOption.attribute] : [])].map(attribute => normalizedAttributeKey(attribute.name)))
  const optionExternalIds = new Set(optionMappings.filter(item => !sizeRemoteExternalIds.has(item.externalId)).map(item => item.externalId))
  const productMappings = platformCode === 'TRENDYOL' ? allMappings.filter(item => !optionAttributeIds.has(item.localId) || sizeRemoteExternalIds.has(item.externalId) || (colorLocalAttributeIds.has(item.localId) && colorRemoteExternalIds.has(item.externalId))) : allMappings
  const productMappingByExternal = new Map(productMappings.map(item => [item.externalId, item]))
  const optionRemoteItems = platformCode === 'TRENDYOL' ? remoteItems.filter(item => isVariantOptionAttributeName(item.name) && !isPanelColorAttributeName(item.name) && !sizeRemoteExternalIds.has(item.externalId) && item.externalId !== webColorRemote?.externalId && !isWebColorAttributeName(item.name)).sort((left, right) => Number(right.isRequired) - Number(left.isRequired) || left.name.localeCompare(right.name, 'tr')) : []
  const productRemoteItems = (platformCode === 'TRENDYOL' ? remoteItems.filter(item => !optionExternalIds.has(item.externalId) && item.externalId !== webColorRemote?.externalId && !isWebColorAttributeName(item.name) && (!optionAttributeNames.has(normalizedAttributeKey(item.name)) || isPanelColorAttributeName(item.name))) : remoteItems).slice().sort((left, right) => Number(right.isRequired) - Number(left.isRequired) || left.name.localeCompare(right.name, 'tr'))
  const optionCount = new Set([...optionAttributes.map(item => item.id), ...(specialColorOption ? [specialColorOption.attribute.id] : [])]).size
  const productSync = useMutation({ mutationFn: () => hubApi(`/connections/${connectionId}/product-sync-jobs`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: '{}' }), onSuccess: () => { onNotice('Ürünler salt-okunur eşitleme kuyruğuna alındı; pazar yerine yazma yapılmaz.') }, onError: reason => onNotice(reason instanceof Error ? reason.message : 'Ürün eşitleme başlatılamadı.') })
  // Bulk "same name" matching for the whole category: first the headings (attributes and option groups), then every value of the mapped headings.
  const [bulkBusy, setBulkBusy] = useState<'' | 'HEADINGS' | 'VALUES' | 'TEMPLATES'>('')
  const [selectedEntryKey, setSelectedEntryKey] = useState('')
  const valueDrafts = useValueDraftStore(connectionId, onNotice)
  const [extraPanel, setExtraPanel] = useState<'' | 'defaults' | 'conflicts'>('')
  const [collapsedGroups, setCollapsedGroups] = useState<string[]>([])
  // Same cache entries as the two panels, so the header buttons can show how many items each holds.
  const attributeDefaults = useQuery({ queryKey: ['attribute-defaults', connectionId], queryFn: () => hubApi<AttributeDefault[]>(`/connections/${connectionId}/attribute-defaults`), retry: false, enabled: !!connectionId && (platformCode === 'TRENDYOL' || platformCode === 'HEPSIBURADA' || platformCode === 'N11') }).data
  const attributeDefaultsCount = attributeDefaults?.length ?? 0
  // Fields filled from the product itself (Kombin ID = Model Kodu), connection wide.
  const fieldDefaultByExternal = new Map((attributeDefaults ?? []).filter(item => item.sourceField).map(item => [item.externalAttributeId, item]))
  const mappingConflictData = useQuery({ queryKey: ['mapping-conflicts', connectionId], queryFn: () => hubApi<{ sameCategory?: unknown[]; crossCategory?: unknown[] }>(`/mappings/conflicts?connectionId=${encodeURIComponent(connectionId)}`), retry: false, staleTime: 30_000, enabled: !!connectionId }).data
  const mappingConflictCount = (mappingConflictData?.sameCategory?.length ?? 0) + (mappingConflictData?.crossCategory?.length ?? 0)
  // Value progress of every mapped field, read from the same cache entries the value table uses.
  const mappedValueFields = [...productRemoteItems.filter(item => productMappingByExternal.has(item.externalId)), ...(webColorRemote && existingWebColorMapping ? [webColorRemote] : [])]
  const valueMappingQueries = useQueries({ queries: mappedValueFields.map(item => ({ queryKey: ['attribute-value-mappings', connectionId, `${categoryScope}/${item.externalId}`], queryFn: () => hubApi<CatalogMapping[]>(`/mappings/attribute-values?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(`${categoryScope}/${item.externalId}`)}`), retry: false, staleTime: 30_000, enabled: !!connectionId && !!categoryScope })) })
  const valueMappingsByExternal = new Map(mappedValueFields.map((item, index) => [item.externalId, valueMappingQueries[index]?.data]))
  const attributeNameHints = useQuery({ queryKey: ['attribute-name-hints', connectionId], queryFn: () => hubApi<Record<string, string[]>>(`/mappings/attribute-name-hints?connectionId=${encodeURIComponent(connectionId)}`), retry: false, staleTime: 5 * 60_000, enabled: !!connectionId })
  // Brings over the attribute and value decisions made for the connection's other categories.
  async function fillFromOtherCategories() {
    if (bulkBusy) return
    setBulkBusy('TEMPLATES'); onNotice('')
    try {
      const result = await hubApi<{ attributes: number; values: number }>(`/mappings/templates/fill?connectionId=${encodeURIComponent(connectionId)}&categoryExternalId=${encodeURIComponent(categoryScope)}`, { method: 'POST', body: '{}' })
      await client.invalidateQueries({ queryKey: ['attribute-mappings', connectionId, categoryScope] })
      await client.invalidateQueries({ queryKey: ['attribute-value-mappings', connectionId] })
      await client.invalidateQueries({ queryKey: ['publication-attribute-readiness'] })
      onNotice(result.attributes || result.values
        ? `Diğer kategorilerdeki eşlemelerden ${result.attributes} başlık ve ${result.values} değer getirildi.`
        : 'Diğer kategorilerden getirilecek yeni eşleme yok. Değer listesi henüz gelmemiş başlıklar, liste gelince otomatik dolar.')
    } catch (reason) {
      onNotice(reason instanceof Error ? reason.message : 'Diğer kategorilerden doldurma tamamlanamadı.')
    } finally { setBulkBusy('') }
  }
  async function matchHeadingsByName() {
    if (!references.data || bulkBusy) return
    setBulkBusy('HEADINGS'); onNotice('')
    const snapshotIdNow = references.data.snapshotId
    let matched = 0; let bridged = 0; let ambiguous = 0; let failed = 0
    const put = async (localAttribute: LocalAttribute, remote: ReferenceItem, role: 'ATTRIBUTE' | 'OPTION', viaHint = false) => {
      try {
        await hubApi<CatalogMapping>(`/mappings/attributes/${localAttribute.id}`, { method: 'PUT', body: JSON.stringify({ connectionId, snapshotId: snapshotIdNow, scopeExternalId: categoryScope, externalId: remote.externalId, status: 'VERIFIED', role }) })
        if (viaHint) bridged++; else matched++
      } catch { failed++ }
    }
    try {
      const localByName = new Map<string, LocalAttribute[]>()
      for (const attribute of productAttributes) localByName.set(normalizedAttributeKey(attribute.name), [...(localByName.get(normalizedAttributeKey(attribute.name)) ?? []), attribute])
      // Second pass for headings without a same-named panel attribute: synonyms (Materyal ≈ Kumaş
      // Tipi) and the name the panel attribute has on the other marketplace (platform bridge).
      const hintedByKey = new Map<string, LocalAttribute[]>()
      for (const attribute of productAttributes) {
        const keys = new Set([canonicalKey(attribute.name), ...(attributeNameHints.data?.[attribute.id] ?? []).map(canonicalKey)])
        for (const key of keys) hintedByKey.set(key, [...(hintedByKey.get(key) ?? []), attribute])
      }
      for (const remote of productRemoteItems) {
        if (productMappingByExternal.has(remote.externalId)) continue
        const candidates = localByName.get(normalizedAttributeKey(remote.name)) ?? []
        if (candidates.length > 1) { ambiguous++; continue }
        if (candidates.length === 1) { await put(candidates[0], remote, categoryOptionIds.has(candidates[0].id) || isOptionAttribute(candidates[0]) ? 'OPTION' : 'ATTRIBUTE'); continue }
        const hinted = [...new Set(hintedByKey.get(canonicalKey(remote.name)) ?? [])]
        if (hinted.length === 1) await put(hinted[0], remote, categoryOptionIds.has(hinted[0].id) || isOptionAttribute(hinted[0]) ? 'OPTION' : 'ATTRIBUTE', true)
        else if (hinted.length > 1) ambiguous++
      }
      for (const option of optionAttributes.filter(item => !sizeOptionAttributeIds.has(item.id))) {
        if (optionMappings.some(mapping => mapping.localId === option.id && optionRemoteItems.some(remote => remote.externalId === mapping.externalId))) continue
        const remotes = optionRemoteItems.filter(remote => normalizedAttributeKey(remote.name) === normalizedAttributeKey(option.name))
        if (remotes.length > 1) { ambiguous++; continue }
        if (remotes.length === 1) await put(option, remotes[0], 'OPTION')
      }
      await client.invalidateQueries({ queryKey: ['attribute-mappings', connectionId, categoryScope] })
      await client.invalidateQueries({ queryKey: ['publication-attribute-readiness'] })
      onNotice(matched || bridged
        ? `${matched ? `${matched} başlık aynı adlı panel özelliğiyle` : ''}${matched && bridged ? ', ' : ''}${bridged ? `${bridged} başlık eş anlamlısı ya da diğer pazaryerindeki eşlemesi üzerinden` : ''} eşlendi.${ambiguous ? ` Birden fazla adayı olan ${ambiguous} başlık atlandı.` : ''}${failed ? ` ${failed} başlık eşlenemedi.` : ''}`
        : failed ? `Başlık eşlemeleri kaydedilemedi (${failed}).` : `Yeni eşleşen başlık bulunamadı.${ambiguous ? ` Birden fazla adayı olan ${ambiguous} başlık atlandı.` : ''}`)
    } finally { setBulkBusy('') }
  }
  async function matchAllValuesByName() {
    if (!references.data || bulkBusy) return
    setBulkBusy('VALUES'); onNotice('')
    let pairs = 0; let headings = 0; let skipped = 0; let unavailable = 0; let failed = 0
    try {
      const seen = new Set<string>()
      const targets: Array<{ externalId: string; valueScope: string; remote: ReferenceItem; localValues: LocalAttribute['values'] }> = []
      for (const mapping of allMappings) {
        if (mapping.status !== 'VERIFIED' || seen.has(`${mapping.localId}|${mapping.externalId}`)) continue
        seen.add(`${mapping.localId}|${mapping.externalId}`)
        const local = allPanelAttributes.find(item => item.id === mapping.localId)
        const remote = references.data.items.find(item => item.externalId === mapping.externalId && item.isActive)
        const localValues = (local?.values ?? []).filter(item => item.isActive)
        if (!local || !remote || !localValues.length) continue
        if (platformCode === 'TRENDYOL' && usesCustomPanelColorValue(remote.name)) continue
        targets.push({ externalId: mapping.externalId, valueScope: `${categoryScope}/${mapping.externalId}`, remote, localValues })
      }
      // A field's marketplace value list is read from the marketplace the first time its tab opens. Fields never opened
      // have none yet, so read those lists here first (the same read the tab starts) and match once they have arrived.
      const valueListUrl = (externalId: string) => `/reference-data/categories/${encodeURIComponent(categoryScope)}/attributes/${encodeURIComponent(externalId)}/values?connectionId=${encodeURIComponent(connectionId)}`
      const isListMissing = (reason: unknown) => reason instanceof ApiRequestError && reason.code === 'REFERENCE_SNAPSHOT_UNAVAILABLE'
      const lists = new Map<string, ReferenceData>()
      const missing: typeof targets = []
      await runInBatches(targets, 4, async target => {
        try { lists.set(target.externalId, await hubApi<ReferenceData>(valueListUrl(target.externalId))) } catch (reason) { if (isListMissing(reason) && target.remote.allowsCustomValue !== true) missing.push(target) }
      })
      if (missing.length) {
        onNotice(`${missing.length} başlığın değer listesi ${platformLabel} üzerinden alınıyor; bitince değerler eşlenecek…`)
        const jobs = new Map<string, string>()
        await runInBatches(missing, 4, async target => {
          const job = await hubApi<{ jobId: string }>(`/connections/${connectionId}/reference-sync-jobs?resourceType=ATTRIBUTE_VALUES&parentExternalId=${encodeURIComponent(target.valueScope)}`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: '{}' })
          jobs.set(target.externalId, job.jobId)
        })
        await waitForReferenceSyncJobs([...jobs.values()], async jobId => (await hubApi<{ job: { status: string } }>(`/jobs/${jobId}`, { cache: 'no-store' })).job.status)
        await runInBatches(missing.filter(target => jobs.has(target.externalId)), 4, async target => {
          lists.set(target.externalId, await hubApi<ReferenceData>(valueListUrl(target.externalId)))
        })
        await client.invalidateQueries({ queryKey: ['reference-attribute-values', connectionId] })
      }
      for (const { externalId, valueScope, localValues, remote } of targets) {
        const valueReferences = lists.get(externalId)
        // a free-text field without a list has nothing to match
        if (!valueReferences) { if (remote.allowsCustomValue !== true) unavailable++; continue }
        try {
          const existing = await hubApi<CatalogMapping[]>(`/mappings/attribute-values?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(valueScope)}`)
          const plan = planDirectReferenceValues(localValues, valueReferences.items.filter(item => item.isActive))
          let touched = false
          for (const pair of plan.mappings) {
            const current = existing.find(item => item.localId === pair.localId && item.externalId === pair.externalId)
            if (!attributeValueMappingNeedsSave(current, pair.localId, pair.externalId, valueReferences.snapshotId)) continue
            // a panel value goes to one marketplace value (Hepsiburada may reuse it), so never add a second target
            if (platformCode !== 'HEPSIBURADA' && existing.some(item => item.localId === pair.localId && item.externalId !== pair.externalId)) { skipped++; continue }
            try {
              await hubApi<CatalogMapping>(`/mappings/attribute-values/${pair.localId}`, { method: 'PUT', headers: current ? { 'If-Match': `"v${current.version}"` } : {}, body: JSON.stringify({ connectionId, snapshotId: valueReferences.snapshotId, scopeExternalId: valueScope, externalId: pair.externalId, status: 'VERIFIED' }) })
              pairs++; touched = true
            } catch { failed++ }
          }
          if (touched) headings++
        } catch { unavailable++ }
      }
      await client.invalidateQueries({ queryKey: ['attribute-value-mappings'] })
      await client.invalidateQueries({ queryKey: ['publication-attribute-readiness'] })
      const extra = `${skipped ? ` ${skipped} değer başka bir karşılığa bağlı olduğu için atlandı.` : ''}${unavailable ? ` ${unavailable} başlığın değer listesi okunamadı; önce alanları yenileyin.` : ''}${failed ? ` ${failed} değer eşlenemedi.` : ''}`
      onNotice(pairs ? `${headings} başlıkta ${pairs} değer aynı adlı karşılığıyla eşlendi.${extra}` : `Yeni birebir eşleşen değer bulunamadı.${extra}`)
    } finally { setBulkBusy('') }
  }
  // On phones the field list is one sideways row; the chosen field (also when Sonraki Eksik picks it) is brought into
  // view. The hooks sit above the early returns; the effect reads the chosen chip from the list and scrolls only when it changed.
  const fieldNavRef = useRef<HTMLElement>(null)
  const scrolledFieldRef = useRef<Element | null>(null)
  useEffect(() => {
    const nav = fieldNavRef.current
    const item = nav?.querySelector<HTMLElement>('.cm-item.is-selected') ?? null
    if (item === scrolledFieldRef.current) return
    scrolledFieldRef.current = item
    if (!nav || !item || nav.scrollWidth <= nav.clientWidth) return
    const navBox = nav.getBoundingClientRect(), itemBox = item.getBoundingClientRect()
    nav.scrollLeft += itemBox.left - navBox.left - (navBox.width - itemBox.width) / 2
  })
  if (!connectionId) return <article className="panel mapping-step"><div className="editor-section-title"><span>4</span><div><h2>{platformLabel} Kategori ve Özellik Eşlemeleri</h2><p>Önce 1. adımdaki kategori eşlemesini tamamlayın.</p></div></div><div className="unknown"><strong>Bağlantı seçilmedi</strong><p>Önce aktif {platformLabel} bağlantısını seçin.</p></div></article>
  if (categoryMappings.isLoading) return <article className="panel mapping-step"><div className="editor-section-title"><span>4</span><div><h2>{platformLabel} Kategori ve Özellik Eşlemeleri</h2><p>1. adımdaki eşleşmiş kategoriler hazırlanıyor.</p></div></div><Busy text="Kategori eşlemeleri yükleniyor…" /></article>
  if (!matchedCategories.length) return <article className="panel mapping-step"><div className="editor-section-title"><span>4</span><div><h2>{platformLabel} Kategori ve Özellik Eşlemeleri</h2><p>Yalnız 1. adımda eşleşen kategorilerin özellikleri burada açılır.</p></div></div><div className="unknown"><strong>Kategori eşlemesi bekleniyor</strong><p>Önce 1. adımda en az bir panel kategorisini {platformLabel} kategorisiyle eşleştirin.</p></div></article>
  if (!categoryMapping.data) return <article className="panel mapping-step"><div className="editor-section-title"><span>4</span><div><h2>{platformLabel} Kategori ve Özellik Eşlemeleri</h2><p>Ürün özellikleri ve seçenekleri kendi adımlarında tanımladıktan sonra değer karşılıklarını eşleyin.</p></div></div><div className="unknown"><strong>Kategori eşlemesi bekleniyor</strong><p>Önce 1. adımda panel kategorisini {platformLabel} kategorisiyle eşleştirin.</p></div></article>

  const mappingEntries: MappingEntry[] = [
    ...(specialColorOption ? [(() => { const colorValues = (allPanelAttributes.find(attribute => attribute.id === existingWebColorMapping?.localId)?.values ?? []).filter(value => value.isActive); const colorDone = new Set((valueMappingsByExternal.get(specialColorOption.remote.externalId) ?? []).filter(mapping => colorValues.some(value => value.id === mapping.localId)).map(mapping => mapping.localId)).size; const colorState: FieldState = !existingWebColorMapping ? 'none' : colorValues.length && colorDone < colorValues.length ? 'partial' : 'done'; return { key: 'web-color', name: 'Web Color', required: true, state: colorState, meta: !existingWebColorMapping ? 'Eşlenmedi' : colorValues.length ? (colorDone < colorValues.length ? `${colorDone}/${colorValues.length} değer` : `${colorValues.length} değer`) : 'Eşlendi', node: <WebColorMappingCard connectionId={connectionId} categoryScope={categoryScope} snapshotId={references.data!.snapshotId} localAttribute={specialColorOption.attribute} localAttributes={productAttributes} categoryOptionIds={categoryOptionIds} remoteAttribute={specialColorOption.remote} existingMapping={existingWebColorMapping} existingMappings={allMappings} onNotice={onNotice} /> } })()] : []),
    ...(optionRemoteItems.length > 0 ? optionAttributes.filter(item => !sizeOptionAttributeIds.has(item.id)).map(item => {
      const mapped = optionMappings.some(mapping => mapping.localId === item.id && optionRemoteItems.some(remote => remote.externalId === mapping.externalId))
      return { key: `option-${item.id}`, name: item.name, required: false, state: (mapped ? 'done' : 'none') as FieldState, meta: mapped ? 'Seçenek' : 'Eşlenmedi', node: <OptionMappingCard connectionId={connectionId} categoryScope={categoryScope} snapshotId={references.data!.snapshotId} localAttribute={item} remoteAttributes={optionRemoteItems} existingMapping={optionMappings.find(mapping => mapping.localId === item.id && optionRemoteItems.some(remote => remote.externalId === mapping.externalId)) ?? null} onNotice={onNotice} /> }
    }) : []),
    ...productRemoteItems.map(item => {
      // n11's Marka (attribute 1) is always sent as the product's own brand; a panel attribute mapped to it is not sent.
      if (platformCode === 'N11' && item.externalId === n11BrandAttributeId) return { key: `attribute-${item.externalId}`, name: item.name, required: item.isRequired === true, state: 'done' as FieldState, meta: 'Ürün bilgisi: Marka', remote: item, mapping: null, productBrand: true }
      const current = productMappingByExternal.get(item.externalId) ?? null
      const fieldDefault = !current && offersProductFields(platformCode, item.allowsCustomValue) ? fieldDefaultByExternal.get(item.externalId) : undefined
      if (fieldDefault?.sourceField) return { key: `attribute-${item.externalId}`, name: item.name, required: item.isRequired === true, state: 'done' as FieldState, meta: `Ürün bilgisi: ${productFieldLabel(fieldDefault.sourceField)}`, remote: item, mapping: null, fieldDefault }
      const local = current ? productAttributes.find(attribute => attribute.id === current.localId) : undefined
      const activeValues = local?.values.filter(value => value.isActive) ?? []
      const needsValues = !!current && activeValues.length > 0 && item.allowsCustomValue !== true && !(platformCode === 'TRENDYOL' && usesCustomPanelColorValue(item.name))
      const saved = valueMappingsByExternal.get(item.externalId)
      const done = needsValues && saved ? new Set(saved.filter(mapping => activeValues.some(value => value.id === mapping.localId)).map(mapping => mapping.localId)).size : null
      const state: FieldState = !current ? 'none' : needsValues && done !== null && done < activeValues.length ? 'partial' : 'done'
      const meta = !current ? 'Eşlenmedi' : !needsValues ? 'Eşlendi' : done !== null && done < activeValues.length ? `${done}/${activeValues.length} değer` : `${activeValues.length} değer`
      return { key: `attribute-${item.externalId}`, name: item.name, required: item.isRequired === true, state, meta, remote: item, mapping: current }
    })
  ]
  const requiredEntries = mappingEntries.filter(entry => entry.required)
  const optionalEntries = mappingEntries.filter(entry => !entry.required)
  const activeEntry = mappingEntries.find(entry => entry.key === selectedEntryKey) ?? mappingEntries.find(entry => entry.required && entry.state !== 'done') ?? mappingEntries[0]
  const activeIndex = activeEntry ? mappingEntries.indexOf(activeEntry) : -1
  const nextOpenEntry = [...mappingEntries.slice(activeIndex + 1), ...mappingEntries.slice(0, Math.max(0, activeIndex))].sort((left, right) => Number(right.required) - Number(left.required)).find(entry => entry.state !== 'done')
  const entryGroups = [{ title: 'Zorunlu', items: requiredEntries }, { title: 'İsteğe Bağlı', items: optionalEntries }].filter(group => group.items.length > 0)
  const requiredReady = requiredEntries.filter(entry => entry.state === 'done').length
  const openRequired = requiredEntries.length - requiredReady
  const footerHint = openRequired ? `${openRequired} zorunlu alan bekliyor` : 'Zorunlu alanların hepsi hazır'
  const goToNext = nextOpenEntry ? () => setSelectedEntryKey(nextOpenEntry.key) : undefined
  const dataLoading = references.isLoading || mappings.isLoading || categoryRequirements.isLoading
  const dataFailed = references.isError || mappings.isError || categoryRequirements.isError
  return <article className="panel mapping-step cm-card">
    <header className="cm-page-head"><h2>Kategori Eşleştirmeleri</h2><p>{platformLabel} alanlarını panel özellikleri ve değerleriyle eşleyin.</p></header>
    <div className="cm-context">
      <label className="cm-field"><small>Panel Kategorisi</small><select value={attributeCategoryId} onChange={event => setAttributeCategoryId(event.target.value)}>{matchedCategories.map(({ category }) => <option key={category.id} value={category.id}>{category.path}</option>)}</select></label>
      <span className="cm-link cm-context-link" aria-hidden="true"><svg viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round"><path d="M6.6 9.4 9.4 6.6" /><path d="M7.4 4.6 8.6 3.4a2.6 2.6 0 0 1 3.7 3.7l-1.2 1.2" /><path d="M8.6 11.4 7.4 12.6a2.6 2.6 0 0 1-3.7-3.7l1.2-1.2" /></svg></span>
      <div className="cm-field cm-target"><small>{platformLabel} Kategorisi</small><span className="cm-path"><strong title={targetCategoryPath}>{targetCategoryPath}</strong>{targetCategoryStatus && <span className={`mapping-table-status ${targetCategoryStatus.className}`} title={targetCategoryStatus.title}>{targetCategoryStatus.label}</span>}</span></div>
      {!dataLoading && !dataFailed && requiredEntries.length > 0 && <div className="cm-progress" aria-label={`Zorunlu alanlar: ${requiredReady} / ${requiredEntries.length}`}><div><span>Zorunlu Alanlar</span><b>{requiredReady} / {requiredEntries.length}</b></div><div className="cm-track"><i style={{ width: `${Math.round(requiredReady / requiredEntries.length * 100)}%` }} /></div></div>}
      {!dataLoading && !dataFailed && <div className="cm-actions" role="toolbar" aria-label="Kategori eşleme işlemleri">
      <button type="button" className="secondary" title="Önce adı aynı olan panel başlıklarını, bulunamazsa eş anlamlısını (ör. Materyal ≈ Kumaş Tipi) ya da diğer pazaryerinde eşlendiği adı toplu eşler. Birden fazla aday varsa atlar." disabled={sync.isPending || bulkBusy !== ''} onClick={() => void matchHeadingsByName()}>{bulkBusy === 'HEADINGS' ? 'Başlıklar Eşleniyor…' : 'Başlıkları Eşle'}</button>
      <button type="button" className="secondary" title="Eşlenmiş tüm başlıklarda adı aynı olan değerleri toplu eşler" disabled={sync.isPending || bulkBusy !== ''} onClick={() => void matchAllValuesByName()}>{bulkBusy === 'VALUES' ? 'Değerler Eşleniyor…' : 'Tüm Değerleri Eşle'}</button>
      <button type="button" className="secondary" title={`Aynı ${platformLabel} başlığını kullanan diğer kategorilerde yaptığınız başlık ve değer eşlemelerini bu kategoriye getirir. Elle yaptığınız ve sildiğiniz eşlemelere dokunmaz.`} disabled={sync.isPending || bulkBusy !== ''} onClick={() => void fillFromOtherCategories()}>{bulkBusy === 'TEMPLATES' ? 'Getiriliyor…' : 'Kategorilerden Doldur'}</button>
      {(platformCode === 'TRENDYOL' || platformCode === 'HEPSIBURADA') && categoryScope && <button type="button" className="secondary" title={`Ürünlerde boş kalan alanlar için ${platformLabel}’a gönderilecek sabit değerler`} onClick={() => setExtraPanel('defaults')}>Platform Varsayılanları <span className="cm-count">{attributeDefaultsCount}</span></button>}
      <button type="button" className={`secondary${mappingConflictCount ? ' cm-has-conflicts' : ''}`} title={`Aynı panel değeri birden fazla ${platformLabel} değerine bağlanmış eşlemeler`} onClick={() => setExtraPanel('conflicts')}>Gözden Geçirilecek <span className="cm-count">{mappingConflictCount}</span></button>
      {platformCode === 'TRENDYOL' && optionCount > 0 && <button type="button" className="secondary" title="Trendyol’daki ürünleri salt-okunur okuyarak ürün seçeneklerini günceller; Trendyol’a yazma yapmaz." disabled={productSync.isPending} onClick={() => productSync.mutate()}>{productSync.isPending ? 'Ürünler Eşitleniyor…' : 'Seçenekleri Eşitle'}</button>}
      <button type="button" className="secondary" title={`Bu kategorinin ${platformLabel} alan listesini salt-okunur olarak yeniden okur.`} disabled={sync.isPending} onClick={() => sync.mutate()}><UiIcon name="refresh" />{sync.isPending ? 'Güncelleniyor…' : 'Alanları Güncelle'}</button>
      </div>}
    </div>
    {dataLoading ? <Busy text="Kategori özellikleri ve eşlemeler yükleniyor…" /> : dataFailed ? <div className="unknown"><strong>Güncel kategori özellik verisi alınamadı</strong><p>Seçili {platformLabel} kategorisinin özellik listesini yeniden eşitleyin.</p><button type="button" disabled={sync.isPending} onClick={() => sync.mutate()}>{sync.isPending ? 'Kuyruğa Alınıyor…' : 'Kategori Özelliklerini Eşitle'}</button></div> : <>
      <ValueDraftContext.Provider value={valueDrafts}>
      <div className="cm-layout">
        <nav ref={fieldNavRef} className="cm-nav" aria-label="Kategori özellikleri">
          {entryGroups.map(group => <Fragment key={group.title}>
            <button type="button" className={`cm-section${collapsedGroups.includes(group.title) ? ' is-collapsed' : ''}`} aria-expanded={!collapsedGroups.includes(group.title)} onClick={() => setCollapsedGroups(current => current.includes(group.title) ? current.filter(title => title !== group.title) : [...current, group.title])}><svg className="cm-section-chevron" viewBox="0 0 16 16" aria-hidden="true"><path d="m4.5 6.5 3.5 3.5 3.5-3.5" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" /></svg><span>{group.title}</span><span>{group.items.filter(entry => entry.state === 'done').length} / {group.items.length}</span></button>
            {!collapsedGroups.includes(group.title) && group.items.map(entry => <button type="button" key={entry.key} className={`cm-item${entry.key === activeEntry?.key ? ' is-selected' : ''}${entry.remote && valueDrafts.drafts.has(`${categoryScope}/${entry.remote.externalId}`) ? ' is-unsaved' : ''}`} title={entry.remote && valueDrafts.drafts.has(`${categoryScope}/${entry.remote.externalId}`) ? 'Kaydedilmemiş değer seçimleri var' : undefined} aria-current={entry.key === activeEntry?.key ? 'true' : undefined} onClick={() => setSelectedEntryKey(entry.key)}><MappingStateGlyph state={entry.state} required={entry.required} /><span className="cm-name">{entry.name}</span><span className="cm-meta">{entry.meta}</span></button>)}
          </Fragment>)}
        </nav>
        <section className="cm-detail" key={activeEntry?.key ?? 'empty'}>
          {!activeEntry ? <p className="cm-empty">Bu kategoride eşlenecek {platformLabel} alanı yok.</p>
            : activeEntry.remote && activeEntry.productBrand ? <ProductBrandField remote={activeEntry.remote} required={activeEntry.required} platformLabel={platformLabel} footerHint={footerHint} onNext={goToNext} />
            : activeEntry.remote ? <FieldDetail platformCode={platformCode} platformLabel={platformLabel} connectionId={connectionId} categoryScope={categoryScope} snapshotId={references.data!.snapshotId} localAttributes={activeEntry.localAttributes ?? productAttributes} categoryOptionIds={categoryOptionIds} remote={activeEntry.remote} mapping={activeEntry.mapping ?? null} fieldDefault={activeEntry.fieldDefault} role={activeEntry.role} required={activeEntry.required} footerHint={footerHint} onNext={goToNext} onNotice={onNotice} />
            : <>
              <FieldHeading name={activeEntry.name} required={activeEntry.required} platformLabel={platformLabel} />
              <div className="cm-legacy-card">{activeEntry.node}</div>
              <FieldFooter hint={footerHint} onNext={goToNext} />
            </>}
        </section>
      </div>
      </ValueDraftContext.Provider>
      <div className="cm-legend" aria-label="Durum simgeleri"><span><MappingStateGlyph state="done" /> Tamam</span><span><MappingStateGlyph state="partial" /> Değerleri Eksik</span><span><MappingStateGlyph state="none" /> Eşlenmedi</span></div>
    </>}
    <Modal open={extraPanel === 'defaults'} title="Platform Varsayılanları" description={`Ürünlerde boş kalan alanlar için ${platformLabel}’a gönderilecek sabit değerler.`} onClose={() => setExtraPanel('')} className="cm-extra-modal">
      {categoryScope && references.data && <AttributeDefaultsPanel connectionId={connectionId} categoryScope={categoryScope} platformLabel={platformLabel} remoteAttributes={references.data.items} onNotice={onNotice} expanded />}
    </Modal>
    <Modal open={extraPanel === 'conflicts'} title="Gözden Geçirilecek Eşlemeler" description={`Aynı panel değeri birden fazla ${platformLabel} değerine bağlanmış eşlemeler.`} onClose={() => setExtraPanel('')} className="cm-extra-modal">
      {mappingConflictCount ? <MappingConflictsPanel connectionId={connectionId} platformLabel={platformLabel} onNotice={onNotice} expanded /> : <p className="cm-empty">Gözden geçirilecek eşleme yok.</p>}
    </Modal>
  </article>
}
type MappingEntry = { key: string; name: string; required: boolean; state: FieldState; meta: string; node?: ReactNode; remote?: ReferenceItem; mapping?: CatalogMapping | null; localAttributes?: LocalAttribute[]; role?: 'ATTRIBUTE' | 'OPTION'; fieldDefault?: AttributeDefault; productBrand?: boolean }
const n11BrandAttributeId = '1'
function ProductBrandField({ remote, required, platformLabel, footerHint, onNext }: { remote: ReferenceItem; required: boolean; platformLabel: string; footerHint: string; onNext?: () => void }) {
  return <>
    <FieldHeading name={remote.name} required={required} platformLabel={platformLabel} />
    <div className="cm-form">
      <div className="cm-field"><small>{platformLabel} Alanı</small><span className="cm-readonly">{remote.name}</span></div>
      <span className="cm-link" aria-hidden="true"><svg viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round"><path d="M6.6 9.4 9.4 6.6" /><path d="M7.4 4.6 8.6 3.4a2.6 2.6 0 0 1 3.7 3.7l-1.2 1.2" /><path d="M8.6 11.4 7.4 12.6a2.6 2.6 0 0 1-3.7-3.7l1.2-1.2" /></svg></span>
      <div className="cm-field"><small>Panel Özelliği</small><span className="cm-readonly">Marka (Ürün Bilgisi)</span></div>
    </div>
    <p className="cm-empty">Gönderimde her ürünün kendi markası yazılır; markası seçilmemiş üründe gönderim durur. Bu alan için eşleme gerekmez.</p>
    <FieldFooter hint={footerHint} onNext={onNext} />
  </>
}
function FieldHeading({ name, required }: { name: string; required: boolean; platformLabel?: string }) {
  return <>
    <div className="cm-detail-head"><h4>{name}</h4><span className={`cm-tag${required ? ' is-required' : ''}`}>{required ? 'Zorunlu' : 'İsteğe Bağlı'}</span></div>
  </>
}
function FieldFooter({ hint, dirty = false, saving = false, onSave, onNext }: { hint: string; dirty?: boolean; saving?: boolean; onSave?: () => void; onNext?: () => void }) {
  // Kaydet saves the unsaved value choices of every field of the window (ValueDraftContext).
  const store = useContext(ValueDraftContext)
  const pending = store?.drafts.size ?? 0
  const unsaved = dirty || pending > 0
  const busy = saving || store?.saving === true
  const save = onSave ?? (pending ? () => void store?.saveAll() : undefined)
  return <div className={`cm-foot${unsaved ? ' is-dirty' : ''}`}>
    <span>{unsaved ? pending > 1 ? `${pending} alanda kaydedilmemiş değişiklik var` : 'Kaydedilmemiş değişiklikler var' : hint}</span>
    <span className="cm-grow" />
    {onNext && <button type="button" className="secondary" onClick={onNext}>Sonraki Eksik</button>}
    <button type="button" disabled={!save || !unsaved || busy} onClick={save}>{busy ? 'Kaydediliyor…' : 'Kaydet'}</button>
  </div>
}
// One marketplace field: which panel attribute feeds it, then a table of the attribute's values.
function FieldDetail({ platformCode, platformLabel, connectionId, categoryScope, snapshotId, localAttributes, categoryOptionIds, remote, mapping, fieldDefault, role: fixedRole, required, footerHint, onNext, onNotice }: { platformCode: MappingPlatformCode; platformLabel: string; connectionId: string; categoryScope: string; snapshotId: string; localAttributes: LocalAttribute[]; categoryOptionIds: Set<string>; remote: ReferenceItem; mapping: CatalogMapping | null; fieldDefault?: AttributeDefault; role?: 'ATTRIBUTE' | 'OPTION'; required: boolean; footerHint: string; onNext?: () => void; onNotice: (value: string) => void }) {
  const client = useQueryClient()
  const [busy, setBusy] = useState(false)
  const selectable = localAttributes.filter(item => item.isActive !== false)
  const panelOptions = selectable.filter(item => categoryOptionIds.has(item.id) || isOptionAttribute(item))
  const panelAttributes = selectable.filter(item => !categoryOptionIds.has(item.id) && !isOptionAttribute(item))
  const selected = mapping ? localAttributes.find(item => item.id === mapping.localId) : undefined
  const values = selected?.values.filter(value => value.isActive) ?? []
  const customPanelColorValues = platformCode === 'TRENDYOL' && usesCustomPanelColorValue(remote.name)
  const productFields = offersProductFields(platformCode, remote.allowsCustomValue)
  const fieldSource = !mapping && fieldDefault?.sourceField ? fieldDefault.sourceField : null
  const selectedValue = mapping?.localId ?? (fieldSource ? productFieldOptionValue(fieldSource) : '')
  async function choose(localId: string) {
    if (busy || selectedValue === localId) return
    const source = parseProductFieldOption(localId)
    setBusy(true)
    try {
      // A product field and a panel attribute exclude each other: choosing one drops the other.
      if (fieldSource && source === null) await hubApi(`/connections/${connectionId}/attribute-defaults/${encodeURIComponent(remote.externalId)}`, { method: 'DELETE' })
      if (source) {
        if (mapping) await hubApi<boolean>(`/mappings/attributes/${mapping.localId}?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(categoryScope)}&externalId=${encodeURIComponent(mapping.externalId)}`, { method: 'DELETE', headers: { 'If-Match': `"v${mapping.version}"` } })
        await hubApi(`/connections/${connectionId}/attribute-defaults/${encodeURIComponent(remote.externalId)}`, { method: 'PUT', body: JSON.stringify({ attributeName: remote.name, sourceField: source }) })
        onNotice(`${remote.name} artık her ürünün ${productFieldLabel(source)} bilgisiyle gönderilecek.`)
        return
      }
      if (mapping) await hubApi<boolean>(`/mappings/attributes/${mapping.localId}?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(categoryScope)}&externalId=${encodeURIComponent(mapping.externalId)}`, { method: 'DELETE', headers: { 'If-Match': `"v${mapping.version}"` } })
      if (localId) {
        const chosen = localAttributes.find(item => item.id === localId)
        const role = fixedRole ?? (categoryOptionIds.has(localId) || (chosen && isOptionAttribute(chosen)) ? 'OPTION' : 'ATTRIBUTE')
        await hubApi<CatalogMapping>(`/mappings/attributes/${localId}`, { method: 'PUT', body: JSON.stringify({ connectionId, snapshotId, scopeExternalId: categoryScope, externalId: remote.externalId, status: 'VERIFIED', role }) })
      }
      onNotice(localId ? `${remote.name} eşlemesi kaydedildi.` : `${remote.name} eşlemesi kaldırıldı.`)
    } catch (reason) {
      onNotice(reason instanceof Error ? reason.message : 'Özellik eşlemesi kaydedilemedi.')
    } finally {
      await client.invalidateQueries({ queryKey: ['attribute-mappings', connectionId, categoryScope] })
      await client.invalidateQueries({ queryKey: ['attribute-defaults', connectionId] })
      await client.invalidateQueries({ queryKey: ['publication-attribute-readiness'] })
      setBusy(false)
    }
  }
  return <>
    <FieldHeading name={remote.name} required={required} platformLabel={platformLabel} />
    <div className="cm-form">
      <div className="cm-field"><small>{platformLabel} Alanı</small><span className="cm-readonly">{remote.name}</span></div>
      <span className="cm-link" aria-hidden="true"><svg viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round"><path d="M6.6 9.4 9.4 6.6" /><path d="M7.4 4.6 8.6 3.4a2.6 2.6 0 0 1 3.7 3.7l-1.2 1.2" /><path d="M8.6 11.4 7.4 12.6a2.6 2.6 0 0 1-3.7-3.7l1.2-1.2" /></svg></span>
      <div className="cm-field"><small>Panel Özelliği</small><div className="cm-select-row">
        <select aria-label={`${remote.name} için panel özelliği`} value={selectedValue} disabled={busy} onChange={event => void choose(event.target.value)}>
          <option value="">Seçin…</option>
          {showsProductFields(platformCode) && <optgroup label="Ürün Bilgileri">{productFieldSources.map(item => <option key={item.value} value={productFieldOptionValue(item.value)} disabled={!productFields}>{productFields ? item.label : `${item.label} (bu alan listeden seçilir)`}</option>)}</optgroup>}
          {panelOptions.length > 0 && <optgroup label="Panel Seçenekleri">{panelOptions.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</optgroup>}
          {panelAttributes.length > 0 && <optgroup label="Panel Özellikleri">{panelAttributes.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</optgroup>}
        </select>
      </div></div>
      <button type="button" className="secondary cm-quiet cm-unlink" disabled={(!mapping && !fieldSource) || busy} onClick={() => void choose('')}>Kaldır</button>
    </div>
    {fieldSource ? <><p className="cm-empty">Gönderimde her varyantın kendi {productFieldLabel(fieldSource)} bilgisi yazılır; boş olan varyantta gönderim durur ve hangi ürün olduğu söylenir. Bu seçim {platformLabel} bağlantısının bütün kategorilerinde geçerlidir.</p><FieldFooter hint={footerHint} onNext={onNext} /></>
      : !mapping || !selected ? <><p className="cm-empty">Bir panel özelliği seçtiğinizde değerleri burada eşlersiniz.</p><FieldFooter hint={footerHint} onNext={onNext} /></>
      : !values.length ? <><p className="cm-empty">“{selected.name}” özelliğinde henüz değer yok. Değerleri özellik kütüphanesinden ekleyin.</p><FieldFooter hint={footerHint} onNext={onNext} /></>
      : customPanelColorValues || remote.allowsCustomValue === true ? <><div className="cm-legacy-card"><AttributeValueMappingEditor connectionId={connectionId} categoryScope={categoryScope} attribute={selected} externalAttributeId={remote.externalId} platformLabel={platformLabel} customPanelColorValues={customPanelColorValues} marketplaceAllowsFreeText={remote.allowsCustomValue === true} required={required} /></div><FieldFooter hint={footerHint} onNext={onNext} /></>
      : <FieldValueTable key={`${categoryScope}/${remote.externalId}/${selected.id}`} platformCode={platformCode} platformLabel={platformLabel} connectionId={connectionId} categoryScope={categoryScope} attribute={selected} remote={remote} required={required} footerHint={footerHint} onNext={onNext} onNotice={onNotice} />}
  </>
}
// The window's unsaved value choices, one draft per field, kept until Kaydet (which saves every field's) or until
// the window closes: moving to another field no longer drops them.
type ValueDraftStore = { drafts: ReadonlyMap<string, ValueDraft>; generation: number; saving: boolean; put: (draft: ValueDraft) => void; drop: (valueScope: string) => void; saveAll: () => Promise<void> }
const ValueDraftContext = createContext<ValueDraftStore | null>(null)
function useValueDraftStore(connectionId: string, onNotice: (value: string) => void): ValueDraftStore {
  const client = useQueryClient()
  const [drafts, setDrafts] = useState<ReadonlyMap<string, ValueDraft>>(() => new Map())
  const [generation, setGeneration] = useState(0)
  const [saving, setSaving] = useState(false)
  const put = useCallback((draft: ValueDraft) => setDrafts(current => new Map(current).set(draft.valueScope, draft)), [])
  const drop = useCallback((valueScope: string) => setDrafts(current => { if (!current.has(valueScope)) return current; const next = new Map(current); next.delete(valueScope); return next }), [])
  const saveAll = useCallback(async () => {
    if (saving || !drafts.size) return
    setSaving(true)
    const pending = [...drafts.values()]
    try {
      const operations = pending.flatMap(valueDraftOperations)
      const send = (operation: ValueDraftOperation) => operation.kind === 'DELETE'
        ? hubApi<boolean>(`/mappings/attribute-values/${operation.localId}?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(operation.valueScope)}&externalId=${encodeURIComponent(operation.externalId)}`, { method: 'DELETE', headers: { 'If-Match': `"v${operation.version}"` } })
        : hubApi<CatalogMapping>(`/mappings/attribute-values/${operation.localId}`, { method: 'PUT', headers: operation.version !== null ? { 'If-Match': `"v${operation.version}"` } : {}, body: JSON.stringify({ connectionId, snapshotId: operation.snapshotId, scopeExternalId: operation.valueScope, externalId: operation.externalId, status: 'VERIFIED', updateTemplate: true }) })
      // Removals first, so a value moved to another marketplace value is free before its new pair is sent.
      const removed = await runInBatches(operations.filter(item => item.kind === 'DELETE'), 4, send)
      const added = await runInBatches(operations.filter(item => item.kind === 'PUT'), 4, send)
      const saved = removed.saved + added.saved
      const failures = [...removed.failures, ...added.failures]
      const fields = pending.map(draft => draft.fieldName).join(', ')
      onNotice(failures.length
        ? `${fields}: ${saved} değer eşlemesi kaydedildi, ${failures.length} kaydedilemedi. ${mappingSaveErrorMessage(failures[0].error, 'Değer eşlemeleri kaydedilemedi.')}`
        : saved ? `${pending.length > 1 ? `${pending.length} alanda` : `${fields}:`} ${saved} değer eşlemesi kaydedildi.` : 'Değer eşlemelerinde değişiklik yok.')
    } finally {
      // The lists are always reloaded, so every field shows what the server now holds.
      await Promise.all(pending.map(draft => client.invalidateQueries({ queryKey: ['attribute-value-mappings', connectionId, draft.valueScope] }).catch(() => undefined)))
      await client.invalidateQueries({ queryKey: ['publication-attribute-readiness'] }).catch(() => undefined)
      await refreshMappingConflicts(client, connectionId).catch(() => undefined)
      setDrafts(new Map())
      setGeneration(value => value + 1)
      setSaving(false)
    }
  }, [client, connectionId, drafts, onNotice, saving])
  return useMemo(() => ({ drafts, generation, saving, put, drop, saveAll }), [drafts, generation, saving, put, drop, saveAll])
}
const valueCollator = new Intl.Collator('tr-TR', { sensitivity: 'base', numeric: true })
// Panel values down the side, the marketplace value each one is sent as on the right.
function FieldValueTable({ platformCode, platformLabel, connectionId, categoryScope, attribute, remote, required, footerHint, onNext, onNotice }: { platformCode: MappingPlatformCode; platformLabel: string; connectionId: string; categoryScope: string; attribute: LocalAttribute; remote: ReferenceItem; required: boolean; footerHint: string; onNext?: () => void; onNotice: (value: string) => void }) {
  const store = useContext(ValueDraftContext)
  const valueScope = `${categoryScope}/${remote.externalId}`
  const references = useQuery({ queryKey: ['reference-attribute-values', connectionId, valueScope], queryFn: () => hubApi<ReferenceData>(`/reference-data/categories/${encodeURIComponent(categoryScope)}/attributes/${encodeURIComponent(remote.externalId)}/values?connectionId=${encodeURIComponent(connectionId)}`), retry: false, staleTime: 5 * 60_000 })
  const mappings = useQuery({ queryKey: ['attribute-value-mappings', connectionId, valueScope], queryFn: () => hubApi<CatalogMapping[]>(`/mappings/attribute-values?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(valueScope)}`), retry: false })
  const [selections, setSelections] = useState<Record<string, string[]> | null>(() => store?.drafts.get(valueScope)?.selections ?? null)
  const [search, setSearch] = useState('')
  const [onlyMissing, setOnlyMissing] = useState(false)
  // Once the value list is missing (422) the field stays on the editor that asks the marketplace for it. Flipping back to
  // the spinner while that editor refetched the same query unmounted it, and its remount refetched again: an endless
  // loop of requests that froze the page (26 Oct 2026, Hepsiburada Ek Özellik).
  // Once the list has arrived and nothing is fetching (the editor's sync brought it), the field returns to the value
  // table: the table reads the same fresh cache entry, so the switch asks for nothing and cannot loop.
  const [valuesUnavailable, setValuesUnavailable] = useState(false)
  if (references.isError && !valuesUnavailable) setValuesUnavailable(true)
  if (valuesUnavailable && references.isSuccess && !references.isFetching && references.data) setValuesUnavailable(false)
  const generation = store?.generation ?? 0
  const [seenGeneration, setSeenGeneration] = useState(generation)
  // After Kaydet every field starts again from what the server holds.
  if (seenGeneration !== generation) { setSeenGeneration(generation); setSelections(null) }
  useEffect(() => { if (mappings.data && selections === null) setSelections(groupSavedSelections(mappings.data)) }, [mappings.data, selections])
  // A Hepsiburada value may reuse a panel value that already feeds another Hepsiburada value.
  const allowReuse = platformCode === 'HEPSIBURADA'
  // The marketplace list can be long (Hepsiburada Renk: 17,254 values): sorted A–Z and folded for the search once.
  const remoteValues = useMemo(() => {
    if (!references.data || !mappings.data) return []
    const mappedExternalIds = new Set(mappings.data.map(item => item.externalId))
    // One entry per code: the API client's control-character clean-up can leave two entries with the same code ("Sarı").
    const seenCodes = new Set<string>()
    const listed = references.data.items.filter(item => (item.isActive || mappedExternalIds.has(item.externalId)) && !seenCodes.has(item.externalId) && Boolean(seenCodes.add(item.externalId)))
    // A twin padded with stray spaces (Hepsiburada "Turuncu" + 8 no-break spaces) looks the same as the clean value; it is
    // left out of the list unless it is already mapped.
    const cleanNames = new Set(listed.filter(item => item.name === item.name.trim()).map(item => item.name))
    return listed.filter(item => item.name === item.name.trim() || !cleanNames.has(item.name.trim()) || mappedExternalIds.has(item.externalId)).sort((left, right) => valueCollator.compare(left.name, right.name))
  }, [references.data, mappings.data])
  const options = useMemo(() => pickerOptions(remoteValues), [remoteValues])
  const saved = useMemo(() => groupSavedSelections(mappings.data ?? []), [mappings.data])
  const dirty = selections !== null && selectionsDiffer(selections, saved)
  const putDraft = store?.put, dropDraft = store?.drop
  const snapshotId = references.data?.snapshotId ?? ''
  useEffect(() => {
    if (!selections || !mappings.data || !snapshotId) return
    if (dirty) putDraft?.({ valueScope, fieldName: remote.name, selections, saved: mappings.data, snapshotId, knownExternalIds: remoteValues.map(item => item.externalId) })
    else dropDraft?.(valueScope)
  }, [dirty, selections, mappings.data, snapshotId, remoteValues, valueScope, remote.name, putDraft, dropDraft])
  if (valuesUnavailable || references.isError) return <><div className="cm-legacy-card"><AttributeValueMappingEditor connectionId={connectionId} categoryScope={categoryScope} attribute={attribute} externalAttributeId={remote.externalId} platformLabel={platformLabel} autoSyncMissingValues={remote.allowsCustomValue !== true} allowReusablePanelValues={allowReuse} required={required} /></div><FieldFooter hint={footerHint} onNext={onNext} /></>
  if (mappings.isError) return <ErrorBox error={mappings.error} onRetry={() => void mappings.refetch()} />
  if (references.isLoading || mappings.isLoading || !selections || !references.data || !mappings.data) return <Busy text="Değerler ve eşlemeler yükleniyor…" />
  const localValues = attribute.values.filter(item => item.isActive).slice().sort((left, right) => valueCollator.compare(left.value, right.value))
  const targetOf = (current: Record<string, string[]>, localId: string) => Object.keys(current).find(externalId => current[externalId].includes(localId)) ?? ''
  const doneCount = localValues.filter(item => targetOf(selections, item.id)).length
  const query = search.trim().toLocaleLowerCase('tr-TR')
  const rows = localValues.filter(item => (!query || item.value.toLocaleLowerCase('tr-TR').includes(query)) && (!onlyMissing || !targetOf(selections, item.id)))
  function setTarget(localId: string, externalId: string) {
    setSelections(current => {
      const base = current ?? {}
      const previous = targetOf(base, localId)
      const next: Record<string, string[]> = {}
      for (const [key, ids] of Object.entries(base)) {
        const kept = allowReuse && key !== previous ? ids : ids.filter(id => id !== localId)
        if (kept.length) next[key] = kept
      }
      if (externalId) next[externalId] = [...(next[externalId] ?? []).filter(id => id !== localId), localId]
      return next
    })
  }
  function matchSameNames() {
    const plan = planDirectReferenceValues(localValues, references.data!.items.filter(item => item.isActive))
    const open = plan.mappings.filter(item => !targetOf(selections!, item.localId))
    if (open.length) setSelections(current => open.reduce((next, item) => ({ ...next, [item.externalId]: [...(next[item.externalId] ?? []).filter(id => id !== item.localId), item.localId] }), current ?? {}))
    onNotice(open.length ? `${open.length} değer aynı adlı ${platformLabel} değeriyle eşlendi; kalıcı olması için Kaydet’e basın.` : 'Yeni birebir eşleşen değer bulunamadı.')
  }
  return <>
    <div className="cm-vtools">
      <div className="cm-vtools-title"><h5>Değer Eşlemeleri</h5><span>{doneCount} / {localValues.length}</span></div>
      <div className="cm-vtools-actions">
        <input className="cm-search" type="search" value={search} onChange={event => setSearch(event.target.value)} placeholder="Değer ara" aria-label="Panel değeri ara" />
        <button type="button" className={`secondary cm-toggle${onlyMissing ? ' is-on' : ''}`} aria-pressed={onlyMissing} onClick={() => setOnlyMissing(value => !value)}>Yalnızca Eksikler</button>
        <button type="button" className="secondary" onClick={matchSameNames}>Birebir Eşle</button>
      </div>
    </div>
    <div className="cm-vtable" role="table" aria-label={`${remote.name} değer eşlemeleri`}>
      <div className="cm-vrow is-head" role="row"><span role="columnheader">Panel Değeri</span><span role="columnheader">{platformLabel} Değeri</span><span aria-hidden="true" /></div>
      <div className="cm-vscroll">
        {rows.length ? rows.map(item => {
          const target = targetOf(selections, item.id)
          return <div className={`cm-vrow${target ? '' : ' is-open'}`} role="row" key={item.id}>
            <span className="cm-pv" role="cell">{item.value}</span>
            <span role="cell"><ValuePicker value={target} options={options} label={`${item.value} için ${platformLabel} değeri`} onChange={externalId => setTarget(item.id, externalId)} /></span>
            <span className="cm-vstate" role="cell"><MappingStateGlyph state={target ? 'done' : 'none'} /></span>
          </div>
        }) : <p className="cm-empty">{onlyMissing ? 'Eşlenmemiş değer kalmadı.' : 'Aramaya uyan değer yok.'}</p>}
      </div>
    </div>
    <FieldFooter hint={footerHint} dirty={dirty} onNext={onNext} />
  </>
}
function MappingValueModal({ open, title, description, onClose, children }: { open: boolean; title: string; description: string; onClose: () => void; children: ReactNode }) {
  const displayedDescription = description.startsWith('Panel değerlerini mevcut Trendyol seçeneklerine eşleyin.')
    ? 'Trendyol değerlerini karşılık gelen panel değerleriyle eşleyin. Trendyol’a yeni değer gönderilmez.'
    : description
  return <Modal open={open} title={title} description={displayedDescription} onClose={onClose} className="mapping-values-modal">{children}</Modal>
}
export function OptionMappingCard({ connectionId, categoryScope, snapshotId, localAttribute, remoteAttributes, existingMapping, onNotice }: { connectionId: string; categoryScope: string; snapshotId: string; localAttribute: LocalAttribute; remoteAttributes: ReferenceItem[]; existingMapping: CatalogMapping | null; onNotice: (value: string) => void }) {
  const client = useQueryClient(); const [externalId, setExternalId] = useState(existingMapping?.externalId ?? ''); const [showValues, setShowValues] = useState(false)
  useEffect(() => { setExternalId(existingMapping?.externalId ?? ''); setShowValues(false) }, [existingMapping?.externalId])
  const selectedRemote = remoteAttributes.find(item => item.externalId === externalId)
  async function save() {
    try {
      if (!externalId) throw new Error('Trendyol seçenek karşılığını seçin.')
      await hubApi<CatalogMapping>(`/mappings/attributes/${localAttribute.id}`, { method: 'PUT', headers: existingMapping ? { 'If-Match': `"v${existingMapping.version}"` } : {}, body: JSON.stringify({ connectionId, snapshotId, scopeExternalId: categoryScope, externalId, status: 'VERIFIED', role: 'OPTION' }) })
      onNotice(`${localAttribute.name} seçenek eşlemesi kaydedildi.`); await client.invalidateQueries({ queryKey: ['attribute-mappings', connectionId, categoryScope] })
    } catch (reason) { onNotice(reason instanceof Error ? reason.message : 'Seçenek eşlemesi kaydedilemedi.') }
  }
  async function remove() {
    if (!existingMapping) return
    try { await hubApi<boolean>(`/mappings/attributes/${localAttribute.id}?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(categoryScope)}&externalId=${encodeURIComponent(existingMapping.externalId)}`, { method: 'DELETE', headers: { 'If-Match': `"v${existingMapping.version}"` } }); setExternalId(''); onNotice(`${localAttribute.name} seçenek eşlemesi kaldırıldı.`); await client.invalidateQueries({ queryKey: ['attribute-mappings', connectionId, categoryScope] }) } catch (reason) { onNotice(reason instanceof Error ? reason.message : 'Seçenek eşlemesi kaldırılamadı.') }
  }
  const mapped = !!existingMapping && existingMapping.externalId === externalId
  const isColorOption = isPanelColorAttributeName(localAttribute.name)
  const customPanelColorValues = usesCustomPanelColorValue(selectedRemote?.name ?? '')
  const cardTitle = isColorOption ? 'Renk' : `Panel: ${localAttribute.name}`
  const cardDescription = mapped
    ? (isColorOption ? 'Trendyol’dan çekilen düz renk · Kaynak: Panel Renk' : `Trendyol: ${selectedRemote?.name ?? existingMapping?.externalId}`)
    : (isColorOption ? 'Trendyol’dan çekilen düz renk alanı' : 'Varyant üreten panel seçeneği')
  return <section className={`attribute-mapping-card option-mapping-card ${mapped ? '' : 'unmapped'}`}><div className="attribute-mapping-card-head"><span className="attr-source">OPT</span><div><strong>{cardTitle}</strong><small>{cardDescription}</small></div></div><div className="mapping-fields compact"><label>Trendyol Seçenek/Özellik Karşılığı<select aria-label={`${localAttribute.name} Trendyol seçenek karşılığı`} value={externalId} onChange={event => setExternalId(event.target.value)}><option value="">Karşılık Seçin</option>{remoteAttributes.map(item => <option key={item.externalId} value={item.externalId}>{item.name}{item.isRequired ? ' · Zorunlu' : ''}</option>)}</select></label>{mapped ? <button type="button" className="secondary" onClick={() => void remove()}>Eşlemeyi Kaldır</button> : <button type="button" onClick={() => void save()} disabled={!externalId}>Kaydet</button>}</div>{mapped && localAttribute.values.length > 0 && <><button type="button" className="value-mapping-toggle" onClick={() => setShowValues(value => !value)}>{showValues ? 'Seçenek Değerleri Açık' : customPanelColorValues ? `Gönderilecek Renkleri Gör (${localAttribute.values.length})` : `Seçenek Değerlerini Eşle (${localAttribute.values.length})`}</button><MappingValueModal open={showValues && Boolean(selectedRemote)} title={customPanelColorValues ? 'Trendyol’a Gönderilecek Panel Renkleri' : `${localAttribute.name} Değer Eşlemeleri`} description={customPanelColorValues ? 'Normal Renk alanı sabit değer listesi kullanmaz; panel renkleri baş harfleri büyük biçimde gönderilir.' : 'Panel değerlerini mevcut Trendyol seçeneklerine eşleyin. Yeni değer Trendyol’a gönderilmez.'} onClose={() => setShowValues(false)}>{selectedRemote && <AttributeValueMappingEditor connectionId={connectionId} categoryScope={categoryScope} attribute={localAttribute} externalAttributeId={selectedRemote.externalId} customPanelColorValues={customPanelColorValues} required={selectedRemote.isRequired === true} />}</MappingValueModal></>}</section>
}
export function WebColorMappingCard({ connectionId, categoryScope, snapshotId, localAttribute, localAttributes, categoryOptionIds, remoteAttribute, existingMapping, existingMappings, onNotice }: { connectionId: string; categoryScope: string; snapshotId: string; localAttribute: LocalAttribute; localAttributes: LocalAttribute[]; categoryOptionIds: Set<string>; remoteAttribute: ReferenceItem; existingMapping: CatalogMapping | null; existingMappings: CatalogMapping[]; onNotice: (value: string) => void }) {
  const client = useQueryClient(); const [localId, setLocalId] = useState(existingMapping?.localId ?? localAttribute.id); const [showValues, setShowValues] = useState(false)
  useEffect(() => { setLocalId(existingMapping?.localId ?? localAttribute.id) }, [existingMapping?.localId, localAttribute.id])
  const selectableAttributes = [localAttribute, ...localAttributes].filter((attribute, index, items) => attribute.isActive !== false && items.findIndex(candidate => candidate.id === attribute.id) === index)
  void showValues; void setShowValues; void remove; void categoryOptionIds
  const selectedAttribute = selectableAttributes.find(attribute => attribute.id === localId) ?? localAttribute
  const currentMapping = existingMappings.find(item => item.localId === localId && item.externalId === remoteAttribute.externalId) ?? (existingMapping?.localId === localId ? existingMapping : null)
  const mapped = currentMapping?.externalId === remoteAttribute.externalId
  async function save() {
    try {
      if (!localId) throw new Error('Web Color için panel özelliğini seçin.')
      await hubApi<CatalogMapping>(`/mappings/attributes/${localId}`, { method: 'PUT', headers: currentMapping ? { 'If-Match': `"v${currentMapping.version}"` } : {}, body: JSON.stringify({ connectionId, snapshotId, scopeExternalId: categoryScope, externalId: remoteAttribute.externalId, status: 'VERIFIED', role: 'ATTRIBUTE' }) })
      onNotice(`${selectedAttribute.name} → Trendyol Web Color eşlemesi kaydedildi.`); setShowValues(true); await client.invalidateQueries({ queryKey: ['attribute-mappings', connectionId, categoryScope] })
    } catch (reason) { onNotice(reason instanceof Error ? reason.message : 'Web Color eşlemesi kaydedilemedi.') }
  }
  async function remove() {
    if (!currentMapping) return
    try { await hubApi<boolean>(`/mappings/attributes/${currentMapping.localId}?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(categoryScope)}&externalId=${encodeURIComponent(currentMapping.externalId)}`, { method: 'DELETE', headers: { 'If-Match': `"v${currentMapping.version}"` } }); setShowValues(false); onNotice('Panel Renk → Trendyol Web Color eşlemesi kaldırıldı.'); await client.invalidateQueries({ queryKey: ['attribute-mappings', connectionId, categoryScope] }) } catch (reason) { onNotice(reason instanceof Error ? reason.message : 'Web Color eşlemesi kaldırılamadı.') }
  }
  return <>
    <div className="cm-form">
      <div className="cm-field"><small>Trendyol Alanı</small><span className="cm-readonly">Web Color</span></div>
      <span className="cm-link" aria-hidden="true"><svg viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round"><path d="M6.6 9.4 9.4 6.6" /><path d="M7.4 4.6 8.6 3.4a2.6 2.6 0 0 1 3.7 3.7l-1.2 1.2" /><path d="M8.6 11.4 7.4 12.6a2.6 2.6 0 0 1-3.7-3.7l1.2-1.2" /></svg></span>
      <div className="cm-field"><small>Panel Özelliği</small><span className="cm-readonly">{selectedAttribute.name}</span></div>
      {!mapped && <button type="button" className="cm-webcolor-link" onClick={() => void save()}>Bağla</button>}
    </div>
    {mapped ? (selectedAttribute.values.length > 0 ? <div className="cm-webcolor"><WebColorValueMappingEditor connectionId={connectionId} categoryScope={categoryScope} attribute={selectedAttribute} externalAttributeId={remoteAttribute.externalId} /></div> : <p className="cm-empty">Panel {selectedAttribute.name} özelliğinde henüz değer yok.</p>)
      : <p className="cm-empty">Web Color, panel {selectedAttribute.name} değerlerinden gönderilir. Değer eşlemek için önce bağlayın.</p>}
  </>
}
export function AttributeMappingPage() {
  const client = useQueryClient()
  const [selectedPlatformCode, setSelectedPlatformCode] = useState<MappingPlatformCode>('TRENDYOL')
  const [connectionId, setConnectionId] = useState('')
  const [categoryId, setCategoryId] = useState('')
  const [localId, setLocalId] = useState('')
  const [externalId, setExternalId] = useState('')
  const [notice, setNotice] = useState('')
  useEffect(() => {
    if (!notice) return
    const kind = notificationKindFromMessage(notice)
    appendNotification(notice, kind)
    document.documentElement.dataset.rvMappingFeedback = kind
    const timeout = window.setTimeout(() => setNotice(''), kind === 'info' ? 7000 : 5500)
    return () => window.clearTimeout(timeout)
  }, [notice])
  const connections = useQuery({ queryKey: ['connections', 'attribute-mapping'], queryFn: () => loadAllPages<Connection>('/connections') })
  const autoSelectedMappingPlatform = useRef(false)
  useEffect(() => {
    if (!connections.data || autoSelectedMappingPlatform.current) return
    autoSelectedMappingPlatform.current = true
    const items = connections.data.items
    const selectedConnections = activeMappingConnectionsForPlatform(items, selectedPlatformCode)
    const platform = selectedConnections.length
      ? mappingPlatformDefinitions.find(item => item.code === selectedPlatformCode)
      : supportedAttributeMappingPlatforms.find(item => activeMappingConnectionsForPlatform(items, item.code).length > 0)
    if (!platform) return
    const platformConnections = activeMappingConnectionsForPlatform(items, platform.code)
    setSelectedPlatformCode(platform.code)
    if (platformConnections.length) setConnectionId(platformConnections[0].id)
  }, [connections.data, selectedPlatformCode])
  const categories = useQuery({ queryKey: ['categories', 'attribute-mapping'], queryFn: () => loadAllPages<LocalCategory>('/catalog/categories') })
  const localAttributes = useQuery({ queryKey: ['attributes', 'mapping'], queryFn: () => loadAllPages<LocalAttribute>('/catalog/attributes') })
  const categoryRequirements = useQuery({ queryKey: ['category-requirements', categoryId, 'attribute-mapping'], queryFn: () => hubApi<CategoryRequirementView[]>(`/catalog/categories/${categoryId}/attribute-requirements`), enabled: !!categoryId })
  const categoryMapping = useQuery({ queryKey: ['category-mapping', categoryId, connectionId], queryFn: () => hubApi<CatalogMapping | null>(`/mappings/categories/${categoryId}?connectionId=${encodeURIComponent(connectionId)}`), enabled: !!categoryId && !!connectionId, retry: false })
  const categoryScope = categoryMapping.data?.externalId ?? ''
  const references = useQuery({ queryKey: ['reference-attributes', connectionId, categoryScope], queryFn: () => hubApi<ReferenceData>(`/reference-data/categories/${encodeURIComponent(categoryScope)}/attributes?connectionId=${encodeURIComponent(connectionId)}`), enabled: !!connectionId && !!categoryScope, retry: false })
  const mapping = useQuery({ queryKey: ['attribute-mapping', localId, connectionId, categoryScope], queryFn: () => hubApi<CatalogMapping | null>(`/mappings/attributes/${localId}?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(categoryScope)}`), enabled: !!localId && !!connectionId && !!categoryScope, retry: false })
  const sync = useMutation({
    mutationFn: () => hubApi(`/connections/${connectionId}/reference-sync-jobs?resourceType=CATEGORY_ATTRIBUTES&parentExternalId=${encodeURIComponent(categoryScope)}`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: '{}' }),
    onSuccess: async () => {
      setNotice(`${mappingPlatformLabel(selectedPlatformCode)} kategori özellikleri salt-okunur eşitleme kuyruğuna alındı.`)
      for (const delay of [500, 1000, 2000, 4000]) {
        await wait(delay)
        await client.invalidateQueries({ queryKey: ['reference-attributes', connectionId, categoryScope] })
      }
    },
    onError: reason => setNotice(reason instanceof Error ? reason.message : 'Özellik eşitleme başlatılamadı.')
  })
  const save = useMutation({
    mutationFn: () => {
      if (!references.data || !localId || !externalId) throw new Error(`Bağlantı, kategori, panel özelliği ve ${mappingPlatformLabel(selectedPlatformCode)} özelliği zorunludur.`)
      if (categoryRequirements.isLoading) throw new Error('Panel kategori özelliği rolü yükleniyor; eşlemeyi tekrar deneyin.')
      const localAttribute = localAttributes.data?.items.find(item => item.id === localId)
      const role = resolveAttributeMappingRole(categoryRequirements.data?.find(item => item.attributeId === localId)?.role, localAttribute ? isOptionAttribute(localAttribute) : false)
      return hubApi<CatalogMapping>(`/mappings/attributes/${localId}`, { method: 'PUT', headers: mapping.data ? { 'If-Match': `"v${mapping.data.version}"` } : {}, body: JSON.stringify({ connectionId, snapshotId: references.data.snapshotId, scopeExternalId: categoryScope, externalId, status: 'VERIFIED', role }) })
    },
    onSuccess: async value => {
      setNotice('Özellik eşlemesi doğrulandı ve kategori kapsamında kaydedildi.')
      setExternalId(value.externalId)
      await client.invalidateQueries({ queryKey: ['attribute-mapping', localId, connectionId, categoryScope] })
    },
    onError: reason => setNotice(mappingSaveErrorMessage(reason, 'Özellik eşlemesi kaydedilemedi.')), onSettled: () => refreshMappingConflicts(client, connectionId)
  })
  useEffect(() => { if (mapping.data) setExternalId(mapping.data.externalId) }, [mapping.data])
  const activeConnections = activeMappingConnectionsForPlatform(connections.data?.items ?? [], selectedPlatformCode)
  const selectedPlatformLabel = mappingPlatformLabel(selectedPlatformCode)
  const localLeaves = categories.data?.items.filter(item => item.isActive && item.isLeaf) ?? []
  const activeAttributes = localAttributes.data?.items.filter(item => item.isActive) ?? []
  const remoteAttributes = references.data?.items.filter(item => item.isActive) ?? []
  function selectPlatform(platformCode: MappingPlatformCode) {
    setSelectedPlatformCode(platformCode)
    setConnectionId('')
    setCategoryId('')
    setLocalId('')
    setExternalId('')
    setNotice('')
  }
  return <section className="content f3 mapping-page">
    <div className="page-heading rv-page-head"><div><p className="eyebrow">Eşleştirme Ayarları</p><h1>Özellik Eşlemeleri</h1></div><Badge value="SAFE READ" /></div>
    {notice && <MessageToast message={notice} onClose={() => setNotice('')} />}
    <MappingViewTabs active="attribute" />
    <div className="mapping-category-platform-tabs mapping-platform-tabs rv-tabs rv-view-tabs" role="tablist" aria-label="Özellik eşleştirme pazaryeri">
      {supportedAttributeMappingPlatforms.map(platform => <button type="button" role="tab" aria-selected={selectedPlatformCode === platform.code} className={`${selectedPlatformCode === platform.code ? 'active is-active' : ''}${activeMappingConnectionsForPlatform(connections.data?.items ?? [], platform.code).length ? '' : ' is-unavailable'}`} title={activeMappingConnectionsForPlatform(connections.data?.items ?? [], platform.code).length ? `${platform.label} özelliği eşlemelerini göster` : `${platform.label} için aktif bağlantı yok`} key={platform.code} onClick={() => selectPlatform(platform.code)}><span className="rv-tab-label">{platformLogoSource(platform.code) && <img className="rv-view-tab-logo" src={platformLogoSource(platform.code)!} alt="" aria-hidden="true" />}{platform.label}</span></button>)}
    </div>
    <article className="panel mapping-step">
      <div className="editor-section-title"><span>1</span><div><h2>Kategori Kapsamını Seçin</h2><p>Özellik kimlikleri seçili ve doğrulanmış {selectedPlatformLabel} yaprak kategorisi kapsamında tutulur.</p></div></div>
      <div className="mapping-fields">
        <label>{selectedPlatformLabel} için Aktif Bağlantı<select aria-label={`${selectedPlatformLabel} için aktif bağlantı`} value={connectionId} onChange={event => { setConnectionId(event.target.value); setCategoryId(''); setLocalId(''); setExternalId(''); setNotice('') }}><option value="">Bağlantı Seçin</option>{activeConnections.map(item => <option value={item.id} key={item.id}>{item.displayName} · {item.externalStoreId}</option>)}</select></label>
        <label>Panel Yaprak Kategorisi<select aria-label="Özellik kapsamı panel kategorisi" value={categoryId} onChange={event => { setCategoryId(event.target.value); setLocalId(''); setExternalId(''); setNotice('') }} disabled={!connectionId}><option value="">Kategori Seçin</option>{localLeaves.map(item => <option value={item.id} key={item.id}>{item.path}</option>)}</select></label>
      </div>
      {!activeConnections.length ? <div className="unknown"><strong>{selectedPlatformLabel} bağlantısı gerekli</strong><p>Özellik referanslarını eşleyebilmek için önce aktif bir bağlantı kurun.</p><Link className="button-link" to="/integrations">Entegrasyonlara Git</Link></div> : categoryId && !categoryMapping.isLoading && !categoryMapping.data ? <div className="unknown"><strong>Kategori eşlemesi gerekli</strong><p>Özellikleri almadan önce panel kategorisini {selectedPlatformLabel} yaprak kategorisiyle eşleyin.</p><Link className="button-link" to="/mappings/categories">Kategori Eşlemelerine Git</Link></div> : categoryScope && references.isError ? <div className="unknown"><strong>Güncel özellik listesi yok</strong><p>Seçili kategori için yalnız salt-okunur özellik listesini eşitleyin.</p><button type="button" disabled={sync.isPending} onClick={() => sync.mutate()}>{sync.isPending ? 'Kuyruğa Alınıyor…' : 'Kategori Özelliklerini Eşitle'}</button></div> : null}
    </article>
    {references.data && <article className="panel mapping-step">
      <div className="editor-section-title"><span>2</span><div><h2>Özelliği Eşleyin</h2><p>Bu işlem yalnız yerel eşleme kaydı oluşturur; pazar yerine yazmaz.</p></div></div>
      <div className="mapping-fields">
        <label>Panel Özelliği<select aria-label="Panel özelliği" value={localId} onChange={event => { setLocalId(event.target.value); setExternalId(''); setNotice('') }}><option value="">Özellik Seçin</option>{activeAttributes.map(item => <option value={item.id} key={item.id}>{item.name} · {item.dataType}</option>)}</select></label>
        <label>{selectedPlatformLabel} Kategori Özelliği<select aria-label={`${selectedPlatformLabel} kategori özelliği`} value={externalId} onChange={event => setExternalId(event.target.value)} disabled={!localId || mapping.isLoading}><option value="">Özellik Seçin</option>{remoteAttributes.map(item => <option value={item.externalId} key={item.externalId}>{item.name}{item.isRequired ? ' · Zorunlu' : ''}{item.allowsCustomValue ? ' · Serbest Değer' : ''}</option>)}</select></label>
      </div>
      <div className="mapping-action"><span>{remoteAttributes.length.toLocaleString('tr-TR')} özellik · kategori {categoryScope}{mapping.data ? ` · mevcut eşleme v${mapping.data.version}` : ''}</span><button type="button" disabled={!localId || !externalId || save.isPending || mapping.isLoading || categoryRequirements.isLoading} onClick={() => save.mutate()}>{save.isPending ? 'Kaydediliyor…' : mapping.data ? 'Eşlemeyi Güncelle' : 'Eşlemeyi Doğrula ve Kaydet'}</button></div>
      {mapping.data && localId && <AttributeValueMappingEditor connectionId={connectionId} categoryScope={categoryScope} attribute={activeAttributes.find(item => item.id === localId)!} externalAttributeId={mapping.data.externalId} platformLabel={selectedPlatformLabel} customPanelColorValues={selectedPlatformCode === 'TRENDYOL' && usesCustomPanelColorValue(remoteAttributes.find(item => item.externalId === mapping.data!.externalId)?.name ?? '')} marketplaceAllowsFreeText={remoteAttributes.find(item => item.externalId === mapping.data!.externalId)?.allowsCustomValue === true} autoSyncMissingValues={selectedPlatformCode === 'HEPSIBURADA' && remoteAttributes.find(item => item.externalId === mapping.data!.externalId)?.allowsCustomValue === false} allowReusablePanelValues={selectedPlatformCode === 'HEPSIBURADA'} required={remoteAttributes.find(item => item.externalId === mapping.data!.externalId)?.isRequired === true} />}
    </article>}
  </section>
}
function WebColorValueMappingEditor({ connectionId, categoryScope, attribute, externalAttributeId }: { connectionId: string; categoryScope: string; attribute: LocalAttribute; externalAttributeId: string }) {
  const client = useQueryClient(); const [notice, setNotice] = useState(''); const [selections, setSelections] = useState<Record<string, string>>({}); const [selectedRemoteId, setSelectedRemoteId] = useState(''); const [saving, setSaving] = useState(false); const [showGroups, setShowGroups] = useState(false); const valueScope = `${categoryScope}/${externalAttributeId}`
  const references = useQuery({ queryKey: ['reference-attribute-values', connectionId, valueScope], queryFn: () => hubApi<ReferenceData>(`/reference-data/categories/${encodeURIComponent(categoryScope)}/attributes/${encodeURIComponent(externalAttributeId)}/values?connectionId=${encodeURIComponent(connectionId)}`), retry: false })
  const mappings = useQuery({ queryKey: ['attribute-value-mappings', connectionId, valueScope], queryFn: () => hubApi<CatalogMapping[]>(`/mappings/attribute-values?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(valueScope)}`), retry: false })
  const sync = useMutation({ mutationFn: () => hubApi(`/connections/${connectionId}/reference-sync-jobs?resourceType=ATTRIBUTE_VALUES&parentExternalId=${encodeURIComponent(valueScope)}`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: '{}' }), onSuccess: () => setNotice('Web Color değerleri salt-okunur eşitleme kuyruğuna alındı.'), onError: reason => setNotice(reason instanceof Error ? reason.message : 'Web Color değerleri eşitlenemedi.') })
  useEffect(() => {
    if (!notice) return
    const kind = notificationKindFromMessage(notice)
    appendNotification(notice, kind)
    document.documentElement.dataset.rvMappingFeedback = kind
    const timeout = window.setTimeout(() => setNotice(''), kind === 'info' ? 7000 : 5500)
    return () => window.clearTimeout(timeout)
  }, [notice])
  const localValues = (attribute.values?.filter(item => item.isActive) ?? []).slice().sort((left, right) => left.value.localeCompare(right.value, 'tr-TR', { sensitivity: 'base', numeric: true })); const remoteValues = (references.data?.items.filter(item => item.isActive) ?? []).slice().sort((left, right) => left.name.localeCompare(right.name, 'tr-TR', { sensitivity: 'base', numeric: true })); const mappingByLocal = new Map((mappings.data ?? []).map(item => [item.localId, item]))
  useEffect(() => { if (mappings.data) setSelections(Object.fromEntries(mappings.data.map(item => [item.localId, item.externalId]))) }, [mappings.data])
  useEffect(() => {
    if (!remoteValues.length) return
    setSelectedRemoteId(current => current && remoteValues.some(item => item.externalId === current) ? current : remoteValues.find(item => Object.values(selections).includes(item.externalId))?.externalId ?? remoteValues[0].externalId)
  }, [remoteValues, selections])
  const selectedLocalIds = new Set(localValues.filter(item => selections[item.id] === selectedRemoteId).map(item => item.id))
  const assignedGroups = remoteValues.map(remote => ({ remote, locals: localValues.filter(value => selections[value.id] === remote.externalId) })).filter(group => group.locals.length > 0)
  function toggleLocalValue(localId: string) {
    if (!selectedRemoteId) return
    setSelections(current => {
      const next = { ...current }
      if (next[localId] === selectedRemoteId) delete next[localId]
      else next[localId] = selectedRemoteId
      return next
    })
  }
  async function saveAll() {
    if (!references.data) return
    setSaving(true); setNotice('')
    const snapshotId = references.data.snapshotId
    try {
      const operations: Array<() => Promise<unknown>> = []
      for (const localValue of localValues) {
        const externalId = selections[localValue.id] ?? ''
        const existing = mappingByLocal.get(localValue.id)
        if (!externalId && existing) {
          operations.push(() => hubApi<boolean>(`/mappings/attribute-values/${localValue.id}?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(valueScope)}`, { method: 'DELETE', headers: { 'If-Match': `"v${existing.version}"` } }))
          continue
        }
        if (!externalId || existing?.externalId === externalId) continue
        operations.push(() => hubApi<CatalogMapping>(`/mappings/attribute-values/${localValue.id}`, { method: 'PUT', headers: existing ? { 'If-Match': `"v${existing.version}"` } : {}, body: JSON.stringify({ connectionId, snapshotId, scopeExternalId: valueScope, externalId, status: 'VERIFIED' }) }))
      }
      const result = await saveAllMappings(operations, operation => operation())
      if (result.failures.length) setNotice(`${result.saved} Web Color eşlemesi kaydedildi, ${result.failures.length} kaydedilemedi. ${mappingSaveErrorMessage(result.failures[0].error, 'Web Color eşlemeleri kaydedilemedi.')}`)
      else setNotice(result.saved ? `${result.saved} Web Color eşlemesi kaydedildi.` : 'Web Color eşlemelerinde değişiklik yok.')
    } finally {
      await client.invalidateQueries({ queryKey: ['attribute-value-mappings', connectionId, valueScope] }).catch(() => undefined)
      await client.invalidateQueries({ queryKey: ['publication-attribute-readiness'] }).catch(() => undefined)
      await refreshMappingConflicts(client, connectionId).catch(() => undefined)
      setSaving(false)
    }
  }
  if (!localValues.length) return <div className="unknown"><strong>Panel renk değeri bulunamadı</strong><p>Önce Panel Renk özelliğine aktif değerler ekleyin.</p></div>
  if (references.isError) return <div className="unknown"><strong>Güncel Web Color değerleri yok</strong><p>Web Color değer listesini salt-okunur olarak eşitleyin.</p>{notice && <p role="status">{notice}</p>}<button type="button" disabled={sync.isPending} onClick={() => sync.mutate()}>{sync.isPending ? 'Kuyruğa Alınıyor…' : 'Web Color Değerlerini Eşitle'}</button></div>
  if (references.isLoading || mappings.isLoading) return <Busy text="Web Color değerleri ve mevcut eşlemeler yükleniyor…" />
  if (mappings.isError) return <ErrorBox error={mappings.error} onRetry={() => void mappings.refetch()} />
  return <div className="web-color-value-editor"><div className="value-mapping-heading">{showGroups ? <strong className="cm-webcolor-title">Kayıtlı Eşlemeler</strong> : <label className="cm-webcolor-pick">Web Color Değeri<select aria-label="Web Color değeri" value={selectedRemoteId} onChange={event => setSelectedRemoteId(event.target.value)}><option value="">Web Color Seçin</option>{remoteValues.map(remote => <option key={remote.externalId} value={remote.externalId}>{remote.name}</option>)}</select></label>}<span className="cm-webcolor-actions"><button type="button" className="secondary" disabled={!assignedGroups.length} onClick={() => setShowGroups(value => !value)}>{showGroups ? 'Eşlemeye Dön' : `Kayıtlı Eşlemeleri Gör (${assignedGroups.length})`}</button><button type="button" disabled={saving} onClick={() => void saveAll()}>{saving ? 'Kaydediliyor…' : 'Tüm Eşlemeleri Kaydet'}</button></span></div>{notice && <MessageToast message={notice} onClose={() => setNotice('')} />}{!showGroups && <><div className="web-color-panel-value-picker"><div className="web-color-panel-value-picker-head"><strong>Karşılık Gelen Panel Renkleri</strong><span>{selectedLocalIds.size} seçildi</span></div><div className="web-color-panel-value-grid">{localValues.map(localValue => { const selected = selectedLocalIds.has(localValue.id); return <label key={localValue.id} className={`web-color-panel-value${selected ? ' selected' : ''}`}><input type="checkbox" checked={selected} disabled={!selectedRemoteId} onChange={() => toggleLocalValue(localValue.id)} /><span>{localValue.value}</span></label> })}</div></div></>}{showGroups && assignedGroups.length > 0 && <div className="web-color-assignment-groups"><div className="cm-webcolor-groups-head"><span>Web Color</span><span>Panel Renkleri</span></div><div>{assignedGroups.map(group => <p key={group.remote.externalId}><span>{group.remote.name}</span><small>{group.locals.map(value => value.value).join(' · ')}</small></p>)}</div></div>}</div>
}
// Shopify maps product types only and PTT AVM has no attribute service: neither has category fields to map.
function hasCategoryAttributes(platformCode: string) {
  return platformCode !== 'SHOPIFY' && platformCode !== 'PTTAVM'
}
const autoQueuedHepsiburadaValueSyncs = new Set<string>()
const autoQueuedCategoryAttributeSyncs = new Set<string>()
const autoQueuedBrandSyncs = new Set<string>()
function AttributeValueMappingEditor({ connectionId, categoryScope, attribute, externalAttributeId, platformLabel = 'Trendyol', customPanelColorValues = false, marketplaceAllowsFreeText = false, autoSyncMissingValues = false, allowReusablePanelValues = false, required = false }: { connectionId: string; categoryScope: string; attribute: LocalAttribute; externalAttributeId: string; platformLabel?: string; customPanelColorValues?: boolean; marketplaceAllowsFreeText?: boolean; autoSyncMissingValues?: boolean; allowReusablePanelValues?: boolean; required?: boolean }) {
  const client = useQueryClient(); const [notice, setNotice] = useState(''); const [selections, setSelections] = useState<Record<string, string[]>>({}); const [unmatchedOpen, setUnmatchedOpen] = useState(false); const [selectionsReady, setSelectionsReady] = useState(false); const [initializedValueScope, setInitializedValueScope] = useState(''); const [saving, setSaving] = useState(false); const [autoMapping, setAutoMapping] = useState(false); const [quickValueOpen, setQuickValueOpen] = useState(false); const [quickValueDraft, setQuickValueDraft] = useState(''); const [quickValueSaving, setQuickValueSaving] = useState(false); const [editingQuickValue, setEditingQuickValue] = useState<{ id: string; value: string } | null>(null); const [syncJobId, setSyncJobId] = useState<string | null>(null); const [shareWithOtherCategories, setShareWithOtherCategories] = useState(true); const [showUnusedValues, setShowUnusedValues] = useState(false); const handledSyncJobs = useRef(new Set<string>()); const valueScope = `${categoryScope}/${externalAttributeId}`
  const referenceKey = ['reference-attribute-values', connectionId, valueScope] as const
  const references = useQuery({ queryKey: referenceKey, queryFn: () => hubApi<ReferenceData>(`/reference-data/categories/${encodeURIComponent(categoryScope)}/attributes/${encodeURIComponent(externalAttributeId)}/values?connectionId=${encodeURIComponent(connectionId)}`), retry: false, retryOnMount: false, enabled: !customPanelColorValues && !marketplaceAllowsFreeText })
  const mappings = useQuery({ queryKey: ['attribute-value-mappings', connectionId, valueScope], queryFn: () => hubApi<CatalogMapping[]>(`/mappings/attribute-values?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(valueScope)}`), retry: false, enabled: !customPanelColorValues && !marketplaceAllowsFreeText })
  const [suggestionReview, setSuggestionReview] = useState<ValueMappingSuggestion[]>([])
  // Names the panel values carry on the other marketplaces: extra hints for the suggestions.
  const nameHints = useQuery({ queryKey: ['marketplace-name-hints', attribute.id, connectionId], queryFn: () => hubApi<{ attributeNames: string[]; valueNames: Record<string, string[]> }>(`/mappings/name-hints?attributeId=${encodeURIComponent(attribute.id)}&connectionId=${encodeURIComponent(connectionId)}`), retry: false, staleTime: 5 * 60_000, enabled: !customPanelColorValues && !marketplaceAllowsFreeText })
  const usedValues = useQuery({ queryKey: ['attribute-used-values', attribute.id], queryFn: () => hubApi<string[]>(`/catalog/attributes/${attribute.id}/used-values`), retry: false, staleTime: 60_000, enabled: !customPanelColorValues && !marketplaceAllowsFreeText })
  const sync = useMutation({ mutationFn: () => hubApi<{ jobId: string }>(`/connections/${connectionId}/reference-sync-jobs?resourceType=ATTRIBUTE_VALUES&parentExternalId=${encodeURIComponent(valueScope)}`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: '{}' }), onSuccess: result => { setSyncJobId(result.jobId); setNotice(`${platformLabel} değer listesi salt-okunur olarak eşitleniyor…`) }, onError: reason => setNotice(reason instanceof Error ? reason.message : 'Değer eşitleme başlatılamadı.') })
  const syncJob = useQuery({ queryKey: ['reference-value-sync-job', syncJobId], queryFn: () => hubApi<{ job: { status: string; lastErrorCode: string | null; lastErrorSummary: string | null } }>(`/jobs/${syncJobId}`, { cache: 'no-store' }), enabled: Boolean(syncJobId), refetchInterval: query => query.state.data && isReferenceSyncJobTerminal(query.state.data.job.status) ? false : 1000, refetchIntervalInBackground: true, retry: 1 })
  const syncBusy = sync.isPending || Boolean(syncJobId)
  const startValueSync = sync.mutate
  useEffect(() => {
    if (!autoSyncMissingValues || !references.isError || !(references.error instanceof ApiRequestError) || references.error.code !== 'REFERENCE_SNAPSHOT_UNAVAILABLE') return
    const autoSyncKey = `${connectionId}:${valueScope}`
    if (!shouldAutoSyncHepsiburadaValues({ allowsCustomValue: false, snapshotMissing: true, syncPending: syncBusy, alreadyRequested: autoQueuedHepsiburadaValueSyncs.has(autoSyncKey) })) return
    autoQueuedHepsiburadaValueSyncs.add(autoSyncKey)
    startValueSync()
  }, [autoSyncMissingValues, connectionId, references.isError, references.error, valueScope, syncBusy, startValueSync])
  useEffect(() => {
    const referenceQueryKey = ['reference-attribute-values', connectionId, valueScope] as const
    const jobId = syncJobId
    const job = syncJob.data?.job
    if (!jobId || !job || !isReferenceSyncJobTerminal(job.status) || handledSyncJobs.current.has(jobId)) return
    handledSyncJobs.current.add(jobId)
    setSyncJobId(null)
    if (job.status === 'SUCCEEDED') {
      void client.invalidateQueries({ queryKey: referenceQueryKey }).then(() => {
        const latest = client.getQueryData<ReferenceData>(referenceQueryKey)
        const latestQuery = client.getQueryState<ReferenceData>(referenceQueryKey)
        setNotice(latestQuery?.status === 'error'
          ? `${platformLabel} eşitleme işi tamamlandı ancak güncel değer snapshot’ı tekrar okunamadı.`
          : latest?.items.length
            ? `${platformLabel} değer listesi yenilendi. Yeni seçenek geldiyse eşleyip tüm eşlemeleri kaydedin.`
            : `${platformLabel} eşitlemesi tamamlandı; bu kategori özelliği için pazar yeri seçilebilir değer döndürmedi.`)
      })
      return
    }
    setNotice(`${platformLabel} değer eşitlemesi ${job.status.toLocaleLowerCase('tr-TR')} durumunda tamamlandı${job.lastErrorSummary ? `: ${job.lastErrorSummary}` : job.lastErrorCode ? ` (${job.lastErrorCode})` : '.'}`)
  }, [client, connectionId, platformLabel, valueScope, syncJob.data?.job, syncJobId])
  useEffect(() => {
    if (!notice) return
    const kind = notificationKindFromMessage(notice)
    appendNotification(notice, kind)
    document.documentElement.dataset.rvMappingFeedback = kind
    const timeout = window.setTimeout(() => setNotice(''), kind === 'info' ? 7000 : 5500)
    return () => window.clearTimeout(timeout)
  }, [notice])
  const localValues = (attribute.values?.filter(item => item.isActive) ?? []).slice().sort((left, right) => left.value.localeCompare(right.value, 'tr-TR', { sensitivity: 'base', numeric: true }))
  const allRemoteValues = references.data?.items ?? []
  const mappingsByExternal = new Map<string, CatalogMapping[]>()
  for (const item of mappings.data ?? []) mappingsByExternal.set(item.externalId, [...(mappingsByExternal.get(item.externalId) ?? []), item])
  const mappingFor = (externalId: string, localId: string) => mappingsByExternal.get(externalId)?.find(item => item.localId === localId)
  const sameIds = (left: readonly string[], right: readonly string[]) => left.length === right.length && left.every(id => right.includes(id))
  const remoteNameByExternal = new Map(allRemoteValues.map(item => [item.externalId, item.name]))
  const assignedPanelValues: Record<string, string> = {}
  for (const [externalId, localIds] of Object.entries(selections)) for (const localId of localIds) assignedPanelValues[localId] = remoteNameByExternal.get(externalId) ?? externalId
  const usedValueFilter = visiblePanelValues(localValues, usedValues.data, Object.values(selections).flat(), showUnusedValues)
  const unmatchedPanelValues = usedValueFilter.visible.filter(item => !assignedPanelValues[item.id])
  const mappedExternalIds = new Set((mappings.data ?? []).map(item => item.externalId))
  const remoteValues = allRemoteValues.filter(item => item.isActive || mappedExternalIds.has(item.externalId)).slice().sort((left, right) => left.name.localeCompare(right.name, 'tr-TR', { sensitivity: 'base', numeric: true }))
  const activeRemoteValues = allRemoteValues.filter(item => item.isActive)
  const directPlan = planDirectReferenceValues(localValues, activeRemoteValues)
  const missingPanelValues = planMissingReferencePanelValues(localValues, activeRemoteValues, selections)
  const unsavedExternalIds = new Set(remoteValues.filter(item => !sameIds(selections[item.externalId] ?? [], (mappingsByExternal.get(item.externalId) ?? []).map(mapping => mapping.localId))).map(item => item.externalId))
  const availableDirectMappings = directPlan.mappings.filter(item => !unsavedExternalIds.has(item.externalId))
  const directMappingWorkCount = availableDirectMappings.filter(item => attributeValueMappingNeedsSave(mappingFor(item.externalId, item.localId), item.localId, item.externalId, references.data?.snapshotId ?? '')).length
  const templateFilledCount = new Set((mappings.data ?? []).filter(item => item.source === 'TEMPLATE').map(item => item.localId)).size
  function updateAttributeCache(updated: LocalAttribute) {
    for (const queryKey of [['attributes', 'mapping-builder'], ['attributes', 'mapping']] as const) {
      client.setQueryData<Page<LocalAttribute>>(queryKey, current => current ? { ...current, items: current.items.map(item => item.id === updated.id ? updated : item) } : current)
    }
  }
  useEffect(() => {
    setSelections({})
    setSelectionsReady(false)
    setInitializedValueScope('')
    setSuggestionReview([])
  }, [valueScope])
  useEffect(() => {
    if (!mappings.data || initializedValueScope === valueScope) return
    const grouped = mappings.data.reduce<Record<string, string[]>>((current, item) => {
      current[item.externalId] = [...(current[item.externalId] ?? []), item.localId]
      return current
    }, {})
    setSelections(grouped)
    setSelectionsReady(true)
    setInitializedValueScope(valueScope)
  }, [mappings.data, valueScope, initializedValueScope])
  if (customPanelColorValues) {
    if (!localValues.length) return <div className="unknown"><strong>Panel renk değeri bulunamadı</strong><p>Normal Trendyol Renk alanı için panelde etkin renk seçeneği ekleyin.</p></div>
    return <div className="custom-panel-color-values"><div className="custom-panel-color-values-heading"><strong>Gönderilecek panel renkleri</strong><span>{localValues.length} renk · Trendyol’da değer eşlemesi gerekmiyor</span></div><div className="custom-panel-color-values-list">{localValues.map(localValue => <div className="custom-panel-color-value" key={localValue.id}><span>{localValue.value}</span><strong>{formatPanelColorValue(localValue.value)}</strong></div>)}</div></div>
  }
  if (marketplaceAllowsFreeText) return <div className="unknown"><strong>{platformLabel} alanı serbest metin kabul ediyor</strong><p>Bu özellik için sabit pazar yeri değer listesi bulunmadığından değer eşlemesi gerekmiyor.</p></div>
  async function saveAll() {
    if (!references.data || !selectionsReady) return
    setSaving(true); setNotice('')
    const snapshotId = references.data.snapshotId
    // A partial failure used to stop the loop and skip the reload, so the next attempt reused the old versions and
    // failed again (412). Now every change is attempted, the lists always reload (fresh versions, kept selections)
    // and the message says what was saved and what was not.
    try {
      const operations: Array<() => Promise<unknown>> = []
      for (const remoteValue of remoteValues) {
        const wanted = selections[remoteValue.externalId] ?? []
        for (const existing of mappingsByExternal.get(remoteValue.externalId) ?? []) {
          if (wanted.includes(existing.localId)) continue
          operations.push(() => hubApi<boolean>(`/mappings/attribute-values/${existing.localId}?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(valueScope)}&externalId=${encodeURIComponent(remoteValue.externalId)}`, { method: 'DELETE', headers: { 'If-Match': `"v${existing.version}"` } }))
        }
      }
      for (const planned of planReferencePanelMappings(selections)) {
        if (!remoteValues.some(item => item.externalId === planned.externalId)) continue
        const existing = mappingFor(planned.externalId, planned.localId)
        if (!attributeValueMappingNeedsSave(existing, planned.localId, planned.externalId, snapshotId)) continue
        operations.push(() => hubApi<CatalogMapping>(`/mappings/attribute-values/${planned.localId}`, { method: 'PUT', headers: existing ? { 'If-Match': `"v${existing.version}"` } : {}, body: JSON.stringify({ connectionId, snapshotId, scopeExternalId: valueScope, externalId: planned.externalId, status: 'VERIFIED', updateTemplate: shareWithOtherCategories }) }))
      }
      const result = await saveAllMappings(operations, operation => operation())
      if (result.failures.length) setNotice(`${result.saved} değer eşlemesi kaydedildi, ${result.failures.length} kaydedilemedi. ${mappingSaveErrorMessage(result.failures[0].error, 'Değer eşlemeleri kaydedilemedi.')} Seçimleriniz korundu; yeniden kaydedebilirsiniz.`)
      else setNotice(result.saved ? `${result.saved} değer eşlemesi kaydedildi.` : 'Değer eşlemelerinde değişiklik yok.')
    } finally {
      await client.invalidateQueries({ queryKey: ['attribute-value-mappings', connectionId, valueScope] }).catch(() => undefined)
      await client.invalidateQueries({ queryKey: ['publication-attribute-readiness'] }).catch(() => undefined)
      await refreshMappingConflicts(client, connectionId).catch(() => undefined)
      setSaving(false)
    }
  }
  async function addQuickValues() {
    const requested = quickValueDraft.split(',').map(value => value.trim()).filter(Boolean)
    const seen = new Set<string>()
    const values = requested.filter(value => {
      const normalized = normalizeReferenceValueLabel(value)
      if (seen.has(normalized)) return false
      seen.add(normalized)
      return !localValues.some(existing => normalizeReferenceValueLabel(existing.value) === normalized)
    })
    if (!values.length) {
      setNotice(requested.length ? 'Girilen değerler zaten bu özellikte bulunuyor.' : 'Eklemek için en az bir değer yazın.')
      return
    }
    setQuickValueSaving(true)
    try {
      const updated = await hubApi<LocalAttribute>(`/catalog/attributes/${attribute.id}/values`, {
        method: 'POST',
        headers: { 'Idempotency-Key': newIdempotencyKey() },
        body: JSON.stringify(values.map((value, index) => ({ value, sortOrder: localValues.length + index })))
      })
      updateAttributeCache(updated)
      await client.invalidateQueries({ queryKey: ['attributes'] })
      setQuickValueDraft('')
      setQuickValueOpen(false)
      setNotice(`${values.length} değer “${attribute.name}” özelliğine eklendi.`)
    } catch (reason) {
      setNotice(reason instanceof Error ? reason.message : 'Değer eklenemedi.')
    } finally { setQuickValueSaving(false) }
  }
  async function createMissingPanelValues() {
    if (!references.data || !selectionsReady || !missingPanelValues.length || quickValueSaving) return
    setQuickValueSaving(true)
    setNotice('')
    try {
      const updated = await hubApi<LocalAttribute>(`/catalog/attributes/${attribute.id}/values`, {
        method: 'POST',
        headers: { 'Idempotency-Key': newIdempotencyKey() },
        body: JSON.stringify(missingPanelValues.map((value, index) => ({ value, sortOrder: localValues.length + index })))
      })
      updateAttributeCache(updated)
      await client.invalidateQueries({ queryKey: ['attributes'] })
      setNotice(`${missingPanelValues.length} panel değeri oluşturuldu. Şimdi “Birebir eşleşenleri eşle” düğmesini kullanabilirsiniz.`)
    } catch (reason) {
      setNotice(reason instanceof Error ? reason.message : 'Eksik panel değerleri oluşturulamadı.')
    } finally { setQuickValueSaving(false) }
  }
  async function updateQuickValue(valueId: string, nextValue: string, sortOrder: number) {
    const value = nextValue.trim()
    if (!value) {
      setNotice('Değer boş olamaz.')
      return
    }
    setQuickValueSaving(true)
    try {
      const updated = await hubApi<LocalAttribute>(`/catalog/attributes/${attribute.id}/values/${valueId}`, {
        method: 'PUT',
        headers: { 'If-Match': `"v${attribute.version}"` },
        body: JSON.stringify({ value, sortOrder })
      })
      updateAttributeCache(updated)
      await client.invalidateQueries({ queryKey: ['attributes'] })
      setEditingQuickValue(null)
      setNotice('Değer güncellendi.')
    } catch (reason) {
      setNotice(reason instanceof Error ? reason.message : 'Değer güncellenemedi.')
    } finally { setQuickValueSaving(false) }
  }
  async function removeQuickValue(valueId: string) {
    setQuickValueSaving(true)
    try {
      const updated = await hubApi<LocalAttribute>(`/catalog/attributes/${attribute.id}/values/${valueId}`, { method: 'DELETE' })
      updateAttributeCache(updated)
      await client.invalidateQueries({ queryKey: ['attributes'] })
      setEditingQuickValue(null)
      setNotice('Değer kaldırıldı.')
    } catch (reason) {
      setNotice(reason instanceof Error ? reason.message : 'Değer kaldırılamadı.')
    } finally { setQuickValueSaving(false) }
  }
  async function mapDirectMatchingValues() {
    if (!references.data || !selectionsReady || (!directMappingWorkCount && !unsavedExternalIds.size) || autoMapping || syncBusy) return
    if (!directMappingWorkCount) {
      setNotice('Yeni birebir eşleşme bulunamadı. Kaydedilmemiş panel seçimleriniz korundu; kalıcı olması için “Tüm eşlemeleri kaydet” seçin.')
      return
    }
    setAutoMapping(true)
    setNotice('')
    let mappedCount = 0
    try {
      for (const planned of availableDirectMappings) {
        const existing = mappingFor(planned.externalId, planned.localId)
        if (!attributeValueMappingNeedsSave(existing, planned.localId, planned.externalId, references.data.snapshotId)) continue
        await hubApi<CatalogMapping>(`/mappings/attribute-values/${planned.localId}`, {
          method: 'PUT',
          headers: existing ? { 'If-Match': `"v${existing.version}"` } : {},
          body: JSON.stringify({ connectionId, snapshotId: references.data.snapshotId, scopeExternalId: valueScope, externalId: planned.externalId, status: 'VERIFIED' })
        })
        mappedCount++
      }
      setSelections(current => availableDirectMappings.reduce((next, planned) => updatePanelValueReferenceSelection(next, planned.externalId, [...(next[planned.externalId] ?? []), planned.localId], allowReusablePanelValues), current))
      await client.invalidateQueries({ queryKey: ['attribute-value-mappings', connectionId, valueScope] })
      await client.invalidateQueries({ queryKey: ['publication-attribute-readiness'] })
      const summary = `${mappedCount} panel değeri mevcut ${platformLabel} karşılığıyla eşlendi.`
      setNotice(directPlan.ambiguousCount ? `${summary} Adı birden fazla eşleşen ${directPlan.ambiguousCount} seçenek otomatik eşlenmedi.` : summary)
    } catch (reason) {
      await client.invalidateQueries({ queryKey: ['attribute-value-mappings', connectionId, valueScope] })
      await client.invalidateQueries({ queryKey: ['publication-attribute-readiness'] })
      setNotice(`Otomatik eşleme tamamlanamadı: ${mappingSaveErrorMessage(reason, 'beklenmeyen hata')}`)
    } finally {
      await refreshMappingConflicts(client, connectionId).catch(() => undefined)
      setAutoMapping(false)
    }
  }
  // Confidence-based suggestions for the panel values still unmatched: same names, folded letters
  // and synonyms are selected at once; weaker matches wait in a list for approval. Nothing is saved
  // until "Tüm eşlemeleri kaydet".
  function suggestMappings() {
    const result = suggestValueMappings(unmatchedPanelValues, activeRemoteValues, valueMatchScore, autoMatchScore, nameHints.data?.valueNames ?? {})
    if (result.automatic.length) setSelections(current => result.automatic.reduce((next, item) => togglePanelValueReferenceSelection(next, item.externalId, item.localId, allowReusablePanelValues), current))
    setSuggestionReview(result.review)
    setNotice(result.automatic.length || result.review.length
      ? `${result.automatic.length} eşleme yüksek güvenle seçildi${result.review.length ? `, ${result.review.length} öneri onayınızı bekliyor` : ''}. Kalıcı olması için “Tüm eşlemeleri kaydet” seçin.`
      : 'Eşleşmemiş değerler için güvenilir bir öneri bulunamadı.')
  }
  function approveSuggestions(items: readonly ValueMappingSuggestion[]) {
    setSelections(current => items.reduce((next, item) => assignedPanelValues[item.localId] ? next : togglePanelValueReferenceSelection(next, item.externalId, item.localId, allowReusablePanelValues), current))
    setSuggestionReview(current => current.filter(item => !items.includes(item)))
  }
  const closeQuickValueModal = () => { if (quickValueSaving) return; setQuickValueOpen(false); setEditingQuickValue(null) }
  const quickValueModal = quickValueOpen && createPortal(<div className="panel-attribute-modal value-mapping-quick-value-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) closeQuickValueModal() }}><DialogSurface as="section" onClose={() => { closeQuickValueModal() }} className="panel-attribute-modal-card panel-attribute-quick-modal-card" aria-labelledby="mapping-quick-value-title" onMouseDown={event => event.stopPropagation()}><header><div><strong id="mapping-quick-value-title">{attribute.name}</strong><small>Panel özelliği · bu başlığa yeni değer ekleyin</small></div><button type="button" aria-label="Değer ekleme penceresini kapat" onClick={closeQuickValueModal} disabled={quickValueSaving}><UiIcon name="close" /></button></header><form className="panel-attribute-add-value panel-attribute-quick-add-value" onSubmit={event => { event.preventDefault(); void addQuickValues() }}><input data-autofocus value={quickValueDraft} onChange={event => setQuickValueDraft(event.target.value)} placeholder="Yeni değerleri virgülle yazın" aria-label={`${attribute.name} için yeni değerler`} /><button type="submit" disabled={quickValueSaving || !quickValueDraft.trim()}>{quickValueSaving ? 'Ekleniyor…' : '+ Değer Ekle'}</button></form><div className="panel-attribute-value-editor">{localValues.length ? localValues.map(value => <div key={value.id}>{editingQuickValue?.id === value.id ? <input ref={focusOnMount} value={editingQuickValue.value} aria-label={`${value.value} değerini düzenle`} onChange={event => setEditingQuickValue({ id: value.id, value: event.target.value })} /> : <span>{value.value}</span>}<div>{editingQuickValue?.id === value.id ? <><button type="button" aria-label={`${value.value} değerini kaydet`} disabled={quickValueSaving} onClick={() => void updateQuickValue(value.id, editingQuickValue.value, value.sortOrder)}><UiIcon name="check" /></button><button type="button" aria-label="Değer düzenlemeyi iptal et" disabled={quickValueSaving} onClick={() => setEditingQuickValue(null)}><UiIcon name="close" /></button></> : <><button type="button" aria-label={`${value.value} değerini düzenle`} disabled={quickValueSaving} onClick={() => setEditingQuickValue({ id: value.id, value: value.value })}><UiIcon name="edit" /></button><button type="button" aria-label={`${value.value} değerini sil`} disabled={quickValueSaving} onClick={() => void removeQuickValue(value.id)}><UiIcon name="close" /></button></>}</div></div>) : <span className="panel-value-empty">Henüz değer eklenmedi.</span>}</div><footer><button type="button" className="secondary" onClick={closeQuickValueModal} disabled={quickValueSaving}>Kapat</button></footer></DialogSurface></div>, document.body)
  if (references.isError) return <div className="unknown"><strong>Güncel özellik değerleri listesi yok</strong><p>Seçili kategori ve özellik için pazar yerinin salt-okunur değer listesini eşitleyin.</p>{notice && <p role="status">{notice}</p>}{syncJob.isError && <p role="alert">Eşitleme işi durumu okunamadı; işlem takibinden iş kaydını kontrol edin.</p>}<div className="value-mapping-heading-actions"><button type="button" className="secondary" disabled={quickValueSaving} onClick={() => setQuickValueOpen(true)}>Hızlı Değer Oluştur</button><button type="button" disabled={syncBusy} onClick={() => sync.mutate()}>{syncBusy ? 'Eşitleniyor…' : `${platformLabel} Değerlerini Eşitle`}</button></div>{quickValueModal}</div>
  if (references.isLoading || mappings.isLoading) return <Busy text="Özellik değerleri ve mevcut eşlemeler yükleniyor…" />
  if (mappings.isError) return <ErrorBox error={mappings.error} onRetry={() => void mappings.refetch()} />
  return <div className="mapping-step nested value-mapping-editor">
    <div className="value-mapping-heading">
      <div><h3>Değer Eşleştirmeleri</h3><p>{platformLabel} değerlerini karşılık gelen panel değerleriyle eşleyin.{allowReusablePanelValues ? ' Aynı panel değeri birden fazla Hepsiburada seçeneği için kullanılabilir.' : ''}</p></div>
    </div>
    <div className="value-mapping-commandbar" role="toolbar" aria-label="Değer eşleme işlemleri">
      <div className="value-mapping-command-group" role="group" aria-label="Otomatik eşleme">
        <button type="button" className="secondary" title={`Eşleşmemiş panel değerleri için ${platformLabel} değeri önerir: aynı ad, Türkçe harf farkı ve eş anlamlılar (ör. Black → Siyah) doğrudan seçilir; zayıf benzerlikler onayınıza sunulur.`} disabled={!selectionsReady || autoMapping || saving || syncBusy || !unmatchedPanelValues.length || !activeRemoteValues.length} onClick={suggestMappings}><UiIcon name="sparkle" />Önerileri Getir</button>
        <button type="button" className="secondary" title={`Aynı adlı panel ve ${platformLabel} değerlerini eşler.${directPlan.ambiguousCount ? ` Belirsiz ${directPlan.ambiguousCount} eşleşme atlanır.` : ''}`} disabled={!selectionsReady || autoMapping || saving || syncBusy || quickValueSaving || (directMappingWorkCount === 0 && unsavedExternalIds.size === 0)} onClick={() => void mapDirectMatchingValues()}><UiIcon name="equal" />{autoMapping ? 'Eşleştiriliyor…' : 'Birebir Eşle'}</button>
      </div>
      <div className="value-mapping-command-group" role="group" aria-label="Panel değeri ekleme">
        <button type="button" className="secondary" title="Yeni bir panel değeri ekler." disabled={quickValueSaving || autoMapping || saving} onClick={() => setQuickValueOpen(true)}><UiIcon name="plus" />Hızlı Değer</button>
        <button type="button" className="secondary" title="Eşlemesi boş pazar yeri değerlerinin adlarıyla panel değerleri oluşturur. Aynı adlı tekrarları tek değer olarak ekler." disabled={!selectionsReady || !missingPanelValues.length || quickValueSaving || autoMapping || saving || syncBusy} onClick={() => void createMissingPanelValues()}><UiIcon name="layers" />{quickValueSaving ? 'Ekleniyor…' : 'Boş Değerleri Oluştur'}{!quickValueSaving && missingPanelValues.length > 0 && <b className="value-mapping-command-count">{missingPanelValues.length}</b>}</button>
      </div>
      <div className="value-mapping-command-group is-end" role="group" aria-label="Liste ve paylaşım">
        <button type="button" className="secondary" title={`Güncel ${platformLabel} değer listesini salt-okunur olarak yeniden okur.`} disabled={syncBusy || autoMapping || saving} onClick={() => sync.mutate()}><UiIcon name="refresh" />{syncBusy ? 'Yenileniyor…' : 'Listeyi Yenile'}</button>
        <button type="button" role="switch" aria-checked={shareWithOtherCategories} className={`secondary value-mapping-share-switch${shareWithOtherCategories ? ' is-on' : ''}`} title="Açıkken bu eşlemeler, aynı özelliği listeleyen diğer kategorilere de otomatik uygulanır. Kapalıyken yalnızca bu kategoride kalır." onClick={() => setShareWithOtherCategories(current => !current)}><i aria-hidden="true" /><span>Diğer Kategorilerde de Kullan</span></button>
      </div>
    </div>
    {notice && <MessageToast message={notice} onClose={() => setNotice('')} />}
    {!!localValues.length && !!remoteValues.length && (() => {
      const total = usedValueFilter.visible.length
      const matched = total - unmatchedPanelValues.length
      const shown = unmatchedOpen ? unmatchedPanelValues : unmatchedPanelValues.slice(0, 6)
      return <div className={`value-mapping-unmatched ${unmatchedPanelValues.length ? 'has-unmatched' : 'is-complete'}`} role="status">
        <div className="value-mapping-unmatched-head">
          <strong>{unmatchedPanelValues.length ? `${unmatchedPanelValues.length} panel değeri henüz eşleşmedi` : `Tüm panel değerleri bir ${platformLabel} değeriyle eşleşti`}</strong>
          <small>{matched} / {total} eşleşti{templateFilledCount > 0 ? ` · ${templateFilledCount} değer diğer kategorilerden geldi` : ''}</small>
        </div>
        {(usedValueFilter.hiddenCount > 0 || (showUnusedValues && (usedValues.data?.length ?? 0) > 0)) && <p className="value-mapping-used-filter">
          {showUnusedValues ? 'Tüm panel değerleri gösteriliyor.' : `Ürünlerde kullanılan ${usedValueFilter.visible.length} değer gösteriliyor.`}
          <button type="button" onClick={() => setShowUnusedValues(current => !current)}>{showUnusedValues ? 'Yalnızca Kullanılanları Göster' : `Kullanılmayan ${usedValueFilter.hiddenCount} Değeri de Göster`}</button>
        </p>}
        <i className="value-mapping-unmatched-bar" aria-hidden="true"><b style={{ width: `${Math.round((matched / Math.max(total, 1)) * 100)}%` }} /></i>
        {unmatchedPanelValues.length > 0 && <div className={`value-mapping-unmatched-chips${unmatchedOpen ? ' is-open' : ''}`}>
          {shown.map(item => <span key={item.id}>{item.value}</span>)}
          {unmatchedPanelValues.length > 6 && <button type="button" onClick={() => setUnmatchedOpen(current => !current)}>{unmatchedOpen ? 'Daha Az Göster' : `+${unmatchedPanelValues.length - 6} Daha Göster`}</button>}
        </div>}
      </div>
    })()}
    {suggestionReview.length > 0 && <div className="value-mapping-suggestions" role="region" aria-label="Onay bekleyen öneriler">
      <div className="value-mapping-suggestions-head">
        <strong>Onay Bekleyen Öneriler ({suggestionReview.length})</strong>
        <button type="button" onClick={() => approveSuggestions(suggestionReview)}>Tümünü Onayla</button>
        <button type="button" className="secondary" onClick={() => setSuggestionReview([])}>Listeyi Kapat</button>
      </div>
      <ul>
        {suggestionReview.map(item => <li key={`${item.localId}:${item.externalId}`}>
          <span>{item.localLabel}</span><b aria-hidden="true">→</b><strong>{item.remoteName}</strong>
          <small title="Benzerlik puanı">%{item.score} benzer</small>
          <button type="button" onClick={() => approveSuggestions([item])}>Onayla</button>
          <button type="button" className="secondary" onClick={() => setSuggestionReview(current => current.filter(entry => entry !== item))}>Atla</button>
        </li>)}
      </ul>
    </div>}
    <div className="value-mapping-rows">
      {!remoteValues.length && <div className="unknown"><strong>Bu özellik için seçilebilir değer bulunamadı</strong><p>{platformLabel} referans listesi başarıyla okundu ancak kategori özelliği sabit seçenek döndürmedi.</p></div>}
      {remoteValues.map(remoteValue => {
        const selectedPanelIds = selections[remoteValue.externalId] ?? []
        const fromTemplate = isTemplateFilledRow(mappingsByExternal.get(remoteValue.externalId) ?? [], selectedPanelIds)
        return <div key={remoteValue.externalId} className={valueMappingRowClassName(selectedPanelIds.length > 0, required)}>
          <span>{remoteValue.name}{fromTemplate && <em className="value-mapping-template-badge" title="Bu eşleme, aynı özelliği kullanan başka bir kategorideki kararınızdan otomatik geldi. Değiştirip kaydederseniz sizin kararınız olur.">Şablon</em>}</span>
          <b className="value-mapping-row-link" aria-hidden="true" title="Eşleştir"><UiIcon name="link" /></b>
          <PanelValueMultiSelect label={remoteValue.name} options={usedValueFilter.visible} selectedIds={selectedPanelIds} allowReuse={allowReusablePanelValues} assignedElsewhere={Object.fromEntries(Object.entries(assignedPanelValues).filter(([localId]) => !selectedPanelIds.includes(localId)))} disabled={!localValues.length} onToggle={localId => setSelections(current => togglePanelValueReferenceSelection(current, remoteValue.externalId, localId, allowReusablePanelValues))} />
        </div>
      })}
      {!!localValues.length && !remoteValues.length && <div className="unknown"><strong>Bu özellik için seçilebilir değer bulunamadı</strong><p>{platformLabel} referans listesi başarıyla okundu ancak kategori özelliği sabit seçenek döndürmedi.</p></div>}
    </div>
    <div className="value-mapping-savebar" role="region" aria-label="Kaydetme">
      <span>{unmatchedPanelValues.length ? `Eşleşmeyen ${unmatchedPanelValues.length} panel değeri` : 'Tüm panel değerleri eşleşti'}{unsavedExternalIds.size > 0 ? <strong>{unsavedExternalIds.size} kaydedilmemiş değişiklik</strong> : <em>Kaydedilmemiş değişiklik yok</em>}</span>
      <button type="button" disabled={!selectionsReady || saving || syncBusy || autoMapping || quickValueSaving} onClick={() => void saveAll()}><UiIcon name="save" />{saving ? 'Kaydediliyor…' : 'Tüm Eşlemeleri Kaydet'}</button>
    </div>
    {quickValueModal}
  </div>
}
// Only Trendyol and Pazarama keep a brand list to map against. The other marketplaces take the brand by name: the
// panel brand is sent as it is, so card 2 explains that instead of offering a mapping.
const brandListPlatformCodes: readonly MappingPlatformCode[] = ['TRENDYOL', 'PAZARAMA']
const brandAutoMatchLocative: Partial<Record<MappingPlatformCode, string>> = { HEPSIBURADA: "Hepsiburada'da", N11: "n11'de", SHOPIFY: "Shopify'da" }
type BrandMappingSaveInput = { connectionId: string; snapshotId: string; localId: string; externalId: string; version?: number }

export function BrandMappingPage() {
  const [confirm, confirmDialog] = useConfirmDialog()
  const client = useQueryClient(); const [connectionId, setConnectionId] = useState(''); const [localId, setLocalId] = useState(''); const [externalId, setExternalId] = useState(''); const [notice, setNotice] = useState(''); const [brandName, setBrandName] = useState(''); const [brandExternalSearch, setBrandExternalSearch] = useState(''); const [brandExternalPickerOpen, setBrandExternalPickerOpen] = useState(false); const [brandEditOpen, setBrandEditOpen] = useState(false); const [editingBrandMappingId, setEditingBrandMappingId] = useState(''); const [brandTransferBusy, setBrandTransferBusy] = useState(false); const [brandTransferBundle, setBrandTransferBundle] = useState<BrandMappingTransferBundle | null>(null); const [brandTransferOpen, setBrandTransferOpen] = useState(false); const [brandTransferSelection, setBrandTransferSelection] = useState({ brands: true, mappings: true })
  // The platform brand picker closed only from its own close button or a choice; now also on a press outside or Escape.
  const brandPickerRef = useRef<HTMLDivElement>(null)
  useDismissable(brandExternalPickerOpen, [brandPickerRef], () => setBrandExternalPickerOpen(false))
  useEffect(() => {
    if (!notice) return
    const kind = notificationKindFromMessage(notice)
    appendNotification(notice, kind)
    document.documentElement.dataset.rvMappingFeedback = kind
    const timeout = window.setTimeout(() => setNotice(''), kind === 'info' ? 7000 : 5500)
    return () => window.clearTimeout(timeout)
  }, [notice])
  const [brandPlatformCode, setBrandPlatformCode] = useState<MappingPlatformCode>('TRENDYOL')
  // Card 3 lists saved mappings of a brand-list platform; it keeps the last one card 2 chose when card 2 moves to a platform without a list.
  const [savedBrandPlatformChoice, setSavedBrandPlatformChoice] = useState<MappingPlatformCode>('TRENDYOL')
  const [lastListConnectionId, setLastListConnectionId] = useState('')
  const [editingExternalId, setEditingExternalId] = useState('')
  const connections = useQuery({ queryKey: ['connections', 'brand-mapping'], queryFn: () => loadAllPages<Connection>('/connections') })
  // Brand lists exist on Trendyol and Pazarama; the other marketplaces take the brand by its name.
  const brandTabPlatforms = mappingPlatformDefinitions.filter(platform => connections.data?.items.some(item => item.platformCode === platform.code && (item.status === 'ACTIVE' || item.status === 'VERIFIED')))
  const brandMappingSupported = brandListPlatformCodes.includes(brandPlatformCode)
  const savedTabPlatforms = brandTabPlatforms.filter(platform => brandListPlatformCodes.includes(platform.code))
  const savedBrandPlatformCode = savedTabPlatforms.some(platform => platform.code === savedBrandPlatformChoice) ? savedBrandPlatformChoice : savedTabPlatforms[0]?.code ?? savedBrandPlatformChoice
  const savedBrandPlatformLabel = mappingPlatformLabel(savedBrandPlatformCode)
  const brandPlatformLabel = mappingPlatformLabel(brandPlatformCode)
  const brandPlatformTabs = <div className="mapping-saved-tabs mapping-platform-tabs rv-tabs rv-view-tabs" role="tablist" aria-label="Marka eşleştirme platformları"> {brandTabPlatforms.map(platform => <button type="button" role="tab" key={platform.code} aria-selected={brandPlatformCode === platform.code} className={brandPlatformCode === platform.code ? 'active is-active' : ''} onClick={() => chooseBrandPlatform(platform.code)}><span className="rv-tab-label">{platformLogoSource(platform.code) && <img className="rv-view-tab-logo" src={platformLogoSource(platform.code)!} alt="" aria-hidden="true" />}{platform.label}</span></button>)} </div>
  const savedBrandPlatformTabs = <div className="mapping-saved-tabs mapping-platform-tabs rv-tabs rv-view-tabs" role="tablist" aria-label="Kayıtlı marka eşleştirme platformları"> {savedTabPlatforms.map(platform => <button type="button" role="tab" key={platform.code} aria-selected={savedBrandPlatformCode === platform.code} className={savedBrandPlatformCode === platform.code ? 'active is-active' : ''} onClick={() => chooseBrandPlatform(platform.code)}><span className="rv-tab-label">{platformLogoSource(platform.code) && <img className="rv-view-tab-logo" src={platformLogoSource(platform.code)!} alt="" aria-hidden="true" />}{platform.label}</span></button>)} </div>
  function chooseBrandPlatform(code: MappingPlatformCode) {
    setBrandPlatformCode(code); setConnectionId(''); setLocalId(''); setExternalId(''); setNotice('')
    if (brandListPlatformCodes.includes(code)) { setSavedBrandPlatformChoice(code); setEditingBrandMappingId('') }
  }
  const localBrands = useQuery({ queryKey: ['brands', 'mapping'], queryFn: () => loadAllPages<LocalBrand>('/catalog/brands') })
  const brandListHeight = useRememberedHeight<HTMLDivElement>('brand-library', localBrands.isLoading)
  const references = useQuery({ queryKey: ['reference-brands', connectionId], queryFn: () => hubApi<ReferenceData>(`/reference-data/brands?connectionId=${encodeURIComponent(connectionId)}`), enabled: brandMappingSupported && !!connectionId, retry: false })
  const mapping = useQuery({ queryKey: ['brand-mapping', localId, connectionId], queryFn: () => hubApi<CatalogMapping | null>(`/mappings/brands/${localId}?connectionId=${encodeURIComponent(connectionId)}`), enabled: brandMappingSupported && !!localId && !!connectionId, retry: false })
  const platformConnections = useMemo(() => connections.data?.items.filter(item => item.platformCode === brandPlatformCode && (item.status === 'ACTIVE' || item.status === 'VERIFIED')) ?? [], [connections.data, brandPlatformCode])
  const savedPlatformConnections = connections.data?.items.filter(item => item.platformCode === savedBrandPlatformCode && (item.status === 'ACTIVE' || item.status === 'VERIFIED')) ?? []
  // Card 3 reads the connection card 2 uses when both show the same platform; otherwise the last one picked there, or the first.
  const savedConnectionId = brandMappingSupported && brandPlatformCode === savedBrandPlatformCode ? connectionId : savedPlatformConnections.find(item => item.id === lastListConnectionId)?.id ?? savedPlatformConnections[0]?.id ?? ''
  // Same query keys as card 2, so when both cards show one connection the cache answers both.
  const savedReferences = useQuery({ queryKey: ['reference-brands', savedConnectionId], queryFn: () => hubApi<ReferenceData>(`/reference-data/brands?connectionId=${encodeURIComponent(savedConnectionId)}`), enabled: !!savedConnectionId, retry: false })
  const brandMappings = useQuery({ queryKey: ['brand-mappings', savedConnectionId], queryFn: () => hubApi<CatalogMapping[]>(`/mappings/brands?connectionId=${encodeURIComponent(savedConnectionId)}`), enabled: !!savedConnectionId, retry: false })
  const syncBrands = useMutation({
    mutationFn: () => {
      if (!connectionId) throw new Error(`Aktif ${brandPlatformLabel} bağlantısı bulunamadı.`)
      return hubApi(`/connections/${connectionId}/reference-sync-jobs?resourceType=BRANDS`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: '{}' })
    },
    onMutate: () => setNotice(''),
    // The refetch loop runs detached: awaiting it here kept the mutation pending (and the picker disabled) for the whole window.
    onSuccess: () => {
      setNotice('Marka listesi salt-okunur eşitleme kuyruğuna alındı.')
      const syncedConnectionId = connectionId
      const queryKey = ['reference-brands', syncedConnectionId]
      const fetchedBefore = client.getQueryData<ReferenceData>(queryKey)?.fetchedAt
      void (async () => {
        for (const delay of [500, 1000, 2000, 4000, 8000, 15000]) {
          await wait(delay)
          await client.invalidateQueries({ queryKey })
          const current = client.getQueryData<ReferenceData>(queryKey)
          if (current && current.fetchedAt !== fetchedBefore && !client.getQueryState(queryKey)?.error) break
        }
      })()
    },
    onError: reason => setNotice(reason instanceof Error ? reason.message : 'Marka listesi güncellenemedi.')
  })
  // A connection set up before brand lists were fetched on activation has none: the page reads it once by itself.
  const startBrandSync = syncBrands.mutate
  const brandSyncPending = syncBrands.isPending
  useEffect(() => {
    if (!brandMappingSupported || !connectionId || brandSyncPending || !references.isError || !(references.error instanceof ApiRequestError) || references.error.code !== "REFERENCE_SNAPSHOT_UNAVAILABLE" || autoQueuedBrandSyncs.has(connectionId)) return
    autoQueuedBrandSyncs.add(connectionId)
    startBrandSync()
  }, [brandMappingSupported, connectionId, brandSyncPending, references.isError, references.error, startBrandSync])
  const save = useMutation({
    mutationFn: (input: BrandMappingSaveInput) => hubApi<CatalogMapping>(`/mappings/brands/${input.localId}`, { method: 'PUT', headers: input.version !== undefined ? { 'If-Match': `"v${input.version}"` } : {}, body: JSON.stringify({ connectionId: input.connectionId, snapshotId: input.snapshotId, externalId: input.externalId, status: 'VERIFIED' }) }),
    onSuccess: async (value, input) => { setNotice('Marka eşlemesi doğrulandı ve kaydedildi.'); if (input.connectionId === connectionId && input.localId === localId) setExternalId(value.externalId); setBrandEditOpen(false); setEditingBrandMappingId(''); client.setQueryData(['brand-mapping', input.localId, input.connectionId], value); client.setQueryData<CatalogMapping[]>(['brand-mappings', input.connectionId], current => current ? [...current.filter(item => item.localId !== value.localId), value] : [value]); await Promise.all([client.invalidateQueries({ queryKey: ['brand-mapping', input.localId, input.connectionId] }), client.invalidateQueries({ queryKey: ['brand-mappings', input.connectionId] })]) },
    onError: async (reason, input) => { setNotice(mappingSaveErrorMessage(reason, 'Eşleme kaydedilemedi.')); if (isStaleMappingError(reason)) await Promise.all([client.invalidateQueries({ queryKey: ['brand-mapping', input.localId, input.connectionId] }), client.invalidateQueries({ queryKey: ['brand-mappings', input.connectionId] })]) },
    onSettled: (_, __, input) => refreshMappingConflicts(client, input.connectionId)
  })
  function saveSelectedBrandMapping() {
    if (!references.data || !localId || !externalId) { setNotice(`Bağlantı, panel markası ve ${brandPlatformLabel} markası zorunludur.`); return }
    save.mutate({ connectionId, snapshotId: references.data.snapshotId, localId, externalId, version: mapping.data?.version })
  }
  function saveEditedBrandMapping(item: CatalogMapping) {
    if (!savedReferences.data || !savedConnectionId || !editingExternalId) { setNotice(`Güncel ${savedBrandPlatformLabel} marka listesi olmadan eşleştirme kaydedilemez.`); return }
    save.mutate({ connectionId: savedConnectionId, snapshotId: savedReferences.data.snapshotId, localId: item.localId, externalId: editingExternalId, version: item.version })
  }
  const createBrand = useMutation({ mutationFn: () => { if (!brandName.trim()) throw new Error('Marka adı zorunludur.'); return hubApi<LocalBrand>('/catalog/brands', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ name: brandName.trim() }) }) }, onSuccess: async brand => { await client.invalidateQueries({ queryKey: ['brands', 'mapping'] }); setLocalId(brand.id); setExternalId(''); setBrandName(''); setNotice(`“${brand.name}” panel markası oluşturuldu ve seçildi.`) }, onError: reason => setNotice(reason instanceof Error ? reason.message : 'Marka oluşturulamadı.') })
  // An existing mapping fills the platform brand; with none, a platform brand picked before the panel brand stays.
  useEffect(() => { if (mapping.data) setExternalId(mapping.data.externalId) }, [mapping.data])
  useEffect(() => { if (!connectionId && platformConnections.length) setConnectionId(platformConnections[0].id) }, [connectionId, platformConnections])
  const activeLocalBrands = localBrands.data?.items.filter(item => item.isActive) ?? []
  const externalBrands = references.data?.items.filter(item => item.isActive) ?? []
  const selectedExternalBrand = externalBrands.find(item => item.externalId === externalId)
  const normalizedBrandExternalSearch = brandExternalSearch.trim().toLocaleLowerCase('tr-TR')
  const matchingExternalBrands = externalBrands.filter(item => !normalizedBrandExternalSearch || `${item.name} ${item.externalId}`.toLocaleLowerCase('tr-TR').includes(normalizedBrandExternalSearch))
  const visibleExternalBrands = [...(selectedExternalBrand ? [selectedExternalBrand] : []), ...matchingExternalBrands.filter(item => item.externalId !== selectedExternalBrand?.externalId)].slice(0, 20)
  const savedBrandMappings = brandMappings.data ?? []
  const savedExternalBrands = savedReferences.data?.items.filter(item => item.isActive) ?? []
  const localBrandById = new Map(activeLocalBrands.map(item => [item.id, item]))
  const externalBrandById = new Map(savedExternalBrands.map(item => [item.externalId, item]))
  const archiveBrand = useMutation({ mutationFn: (brand: LocalBrand) => hubApi<LocalBrand>(`/catalog/brands/${brand.id}`, { method: 'PATCH', headers: { 'If-Match': `"v${brand.version}"` }, body: JSON.stringify({ name: brand.name, isActive: false }) }), onSuccess: async (_, brand) => { if (localId === brand.id) { setLocalId(''); setExternalId('') }; await client.invalidateQueries({ queryKey: ['brands', 'mapping'] }); setNotice(`“${brand.name}” panel markalarından kaldırıldı.`) }, onError: reason => setNotice(reason instanceof Error ? reason.message : 'Marka kaldırılamadı.') })
  const removeMapping = useMutation({ mutationFn: async (item: CatalogMapping) => { const current = await hubApi<CatalogMapping | null>(`/mappings/brands/${item.localId}?connectionId=${encodeURIComponent(savedConnectionId)}`); if (!current || current.externalId !== item.externalId) throw new Error('Bu marka eşleştirmesi bu sırada değişti veya kaldırıldı. Liste yenilendi.'); return hubApi(`/mappings/brands/${item.localId}?connectionId=${encodeURIComponent(savedConnectionId)}`, { method: 'DELETE', headers: { 'If-Match': `"v${current.version}"` } }) }, onSuccess: (_, item) => { if (localId === item.localId && connectionId === savedConnectionId) setExternalId(''); setNotice('Marka eşleştirmesi kaldırıldı.') }, onError: reason => setNotice(mappingSaveErrorMessage(reason, 'Marka eşleştirmesi kaldırılamadı.')), onSettled: async (_, __, item) => { await Promise.all([client.invalidateQueries({ queryKey: ['brand-mapping', item.localId, savedConnectionId] }), client.invalidateQueries({ queryKey: ['brand-mappings', savedConnectionId] }), refreshMappingConflicts(client, savedConnectionId)]) } })
  async function exportBrandMappings() {
    if (!savedConnectionId) { setNotice(`Aktarım için önce ${savedBrandPlatformLabel} bağlantısı seçin.`); return }
    setBrandTransferBusy(true)
    try {
      const bundle: BrandMappingTransferBundle = { format: 'RAVENCIA_BRAND_MAPPING_BUNDLE', version: 1, exportedAt: new Date().toISOString(), brands: activeLocalBrands, mappings: savedBrandMappings }
      const url = URL.createObjectURL(new Blob([JSON.stringify(bundle, null, 2)], { type: 'application/json;charset=utf-8' }))
      const anchor = document.createElement('a')
      anchor.href = url
      anchor.download = 'ravencia-marka-eslestirme-yedegi.json'
      anchor.rel = 'noopener'
      document.body.appendChild(anchor)
      anchor.click()
      window.setTimeout(() => { anchor.remove(); URL.revokeObjectURL(url) }, 1000)
      setNotice(`${bundle.brands.length} marka ve ${bundle.mappings.length} marka eşleştirmesi dışa aktarıldı.`)
    } catch (reason) { setNotice(reason instanceof Error ? reason.message : 'Marka eşleştirme aktarımı oluşturulamadı.') } finally { setBrandTransferBusy(false) }
  }
  async function importBrandMappings() {
    if (!brandTransferBundle || !savedConnectionId) return
    if (!Object.values(brandTransferSelection).some(Boolean)) { setNotice('En az bir marka aktarım alanı seçin.'); return }
    if (brandTransferSelection.mappings && !savedReferences.data) { setNotice(`Marka eşlemelerini aktarmak için güncel ${savedBrandPlatformLabel} marka listesini eşitleyin.`); return }
    setBrandTransferBusy(true)
    try {
      const allLocalBrands = localBrands.data?.items ?? []
      const brandIdMap = new Map<string, string>()
      let created = 0
      let mapped = 0
      let skipped = 0
      for (const source of brandTransferBundle.brands) {
        const normalizedName = source.name.trim().toLocaleLowerCase('tr-TR')
        const existing = allLocalBrands.find(item => item.id === source.id || item.name.trim().toLocaleLowerCase('tr-TR') === normalizedName)
        let target = existing
        if (brandTransferSelection.brands) {
          if (target && !target.isActive) {
            target = await hubApi<LocalBrand>(`/catalog/brands/${target.id}`, { method: 'PATCH', headers: { 'If-Match': `"v${target.version}"` }, body: JSON.stringify({ name: target.name, isActive: true }) })
          } else if (!target) {
            target = await hubApi<LocalBrand>('/catalog/brands', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ name: source.name.trim() }) })
            created++
          }
        }
        if (target) brandIdMap.set(source.id, target.id)
      }
      if (brandTransferSelection.mappings) {
        const externalIds = new Set(savedExternalBrands.map(brand => brand.externalId))
        for (const item of brandTransferBundle.mappings) {
          const targetLocalId = brandIdMap.get(item.localId) ?? allLocalBrands.find(brand => brand.id === item.localId)?.id
          if (!targetLocalId || !externalIds.has(item.externalId)) { skipped++; continue }
          await hubApi<CatalogMapping>(`/mappings/brands/${targetLocalId}`, { method: 'PUT', body: JSON.stringify({ connectionId: savedConnectionId, snapshotId: savedReferences.data?.snapshotId, externalId: item.externalId, status: 'VERIFIED' }) })
          mapped++
        }
      }
      setBrandTransferOpen(false); setBrandTransferBundle(null); setNotice(`${created} marka ve ${mapped} marka eşleştirmesi içe aktarıldı.${skipped ? ` ${skipped} eşleştirme güncel referansta bulunamadığı için atlandı.` : ''}`); await Promise.all([client.invalidateQueries({ queryKey: ['brands', 'mapping'] }), client.invalidateQueries({ queryKey: ['brand-mapping'] }), client.invalidateQueries({ queryKey: ['brand-mappings', savedConnectionId] })])
    } catch (reason) { setNotice(reason instanceof Error ? reason.message : 'Marka eşleştirme aktarımı uygulanamadı.') } finally { setBrandTransferBusy(false) }
  }
  function readBrandMappingBundle(event: ChangeEvent<HTMLInputElement>) {
    const file = event.currentTarget.files?.[0]
    event.currentTarget.value = ''
    if (!file) return
    void file.text().then(text => {
      const parsed = JSON.parse(text) as Partial<BrandMappingTransferBundle>
      if (parsed.format !== 'RAVENCIA_BRAND_MAPPING_BUNDLE' || parsed.version !== 1 || !Array.isArray(parsed.brands) || !Array.isArray(parsed.mappings)) throw new Error('Geçerli bir Ravencia marka eşleştirme yedeği seçin.')
      setBrandTransferSelection({ brands: true, mappings: true })
      setBrandTransferBundle({ format: 'RAVENCIA_BRAND_MAPPING_BUNDLE', version: 1, exportedAt: parsed.exportedAt ?? new Date().toISOString(), brands: parsed.brands, mappings: parsed.mappings })
      setBrandTransferOpen(true)
    }).catch(reason => setNotice(reason instanceof Error ? reason.message : 'Marka eşleştirme yedeği okunamadı.'))
  }
  return <section className="content f3 mapping-page mapping-workspace stitch-reference-mapping">{confirmDialog}<header className="mapping-reference-heading"><div><h1>Eşleştirme Ayarları</h1></div><div className="mapping-reference-heading-actions" aria-label="Marka eşleştirme yedeği işlemleri"><div className="mapping-transfer-actions"><button type="button" className="secondary" title="Marka eşleştirme yedeğini JSON olarak dışa aktar" onClick={() => void exportBrandMappings()} disabled={brandTransferBusy || !savedConnectionId}><UiIcon name="download" size={18} />{brandTransferBusy ? 'Hazırlanıyor…' : 'Dışa Aktar'}</button><label className="mapping-transfer-upload" title="JSON marka eşleştirme yedeğini içe aktar"><UiIcon name="import" size={18} />İçe Aktar<input type="file" accept="application/json,.json" onChange={readBrandMappingBundle} disabled={brandTransferBusy} /></label></div></div></header><MappingViewTabs active="brand" />{notice && <MessageToast message={notice} onClose={() => setNotice('')} />}
    <article className="mapping-reference-card mapping-local-brand-builder">
      <div className="mapping-section-heading"><div><h2>1. Yeni Marka Ekle</h2><p>Panel markalarını ekleyin, arayın veya kaldırın.</p></div></div>
      <section className="brand-library-panel brand-library-panel-standalone"><div className="brand-library-heading"><div><strong>Panel Markaları</strong><small>Mevcut markayı seçin veya yeni bir marka oluşturun.</small></div><span>{activeLocalBrands.length} kayıt</span></div><form className="platform-category-create brand-library-create" onSubmit={event => { event.preventDefault(); createBrand.mutate() }}><label>Yeni Panel Markası<input aria-label="Yeni panel markası adı" maxLength={160} value={brandName} onChange={event => setBrandName(event.target.value)} placeholder="Örn. Ravencia" required /></label><button disabled={createBrand.isPending}>{createBrand.isPending ? 'Ekleniyor…' : 'Marka Ekle'}</button></form><div ref={brandListHeight.ref} style={brandListHeight.style} className="category-chip-list brand-library-list" aria-label="Eklenen panel markaları">{activeLocalBrands.length ? activeLocalBrands.map(brand => <span key={brand.id} className={brand.id === localId ? 'active' : ''}><button type="button" onClick={() => { setLocalId(brand.id); setExternalId(''); setBrandExternalSearch(''); setBrandExternalPickerOpen(false); setNotice('') }}>{brand.name}</button><button type="button" className="category-chip-remove" aria-label={`${brand.name} markasını kaldır`} onClick={() => archiveBrand.mutate(brand)} disabled={archiveBrand.isPending}><UiIcon name="close" /></button></span>) : localBrands.isLoading ? null : <small>Henüz panel markası eklenmedi.</small>}</div></section>
    </article>
    {!brandMappingSupported && <article className="mapping-reference-card brand-mapping-console"><header className="mapping-builder-head"><h2><UiIcon name="link" />2. Yeni Marka Eşleme</h2>{brandPlatformTabs}</header><div className="unknown">{brandPlatformCode === 'PTTAVM' ? <><strong>Markalar otomatik eşleşir</strong><p>PttAVM'de marka listesi yok; panel markalarınız otomatik eşleşir. Ayrıca eşleme yapmanız gerekmez.</p></> : brandAutoMatchLocative[brandPlatformCode] ? <><strong>Markalar otomatik eşleşir</strong><p>{brandAutoMatchLocative[brandPlatformCode]} marka listesi yok; panel markalarınız otomatik eşleşir ve ürünler panel markası adıyla gönderilir. Ayrıca eşleme yapmanız gerekmez.</p></> : <><strong>{brandPlatformLabel} için marka eşlemesi gerekmez</strong><p>{brandPlatformLabel} marka listesi sunmuyor; ürünün markası yayında adıyla gönderilir.</p></>}</div></article>}
    {brandMappingSupported && <>
    <article className="mapping-reference-card brand-mapping-console">
      <header className="mapping-builder-head"><h2><UiIcon name="link" />2. Yeni Marka Eşleme</h2>{brandPlatformTabs}</header>
      <div className="mapping-pair-grid brand-mapping-pair-grid"><section className="mapping-side local"><div className="mapping-side-head"><span>Panel Kataloğu</span><small>Ürünlerinizde kullandığınız yerel markayı seçin.</small></div><SearchableSelect label="Panel Markası" value={localId} options={activeLocalBrands.map(item => ({ value: item.id, label: item.name, description: 'Panel markası' }))} placeholder="Panel markalarında ara" disabled={!connectionId} triggerClassName="panel-category-picker-trigger" onChange={value => { if (localId) setExternalId(''); setLocalId(value); setBrandExternalSearch(''); setBrandExternalPickerOpen(false); setNotice('') }} /></section><div className="mapping-link-rail" aria-hidden="true"><UiIcon name="link" /><small>Eşleştir</small></div><section className="mapping-side remote"><div className="mapping-side-head"><div className="mapping-side-head-copy"><span>Hedef Pazar Yeri &amp; Marka</span><small>Bağlantıyı seçin, güncel marka referansından karşılığını belirleyin.</small></div></div><div className="mapping-side-field"><span>{brandPlatformLabel} Markası</span><div className="mapping-target-controls brand-mapping-target-controls">{platformConnections.length > 1 && <select aria-label="Hedef pazaryeri bağlantısı" value={connectionId} onChange={event => { setConnectionId(event.target.value); setLastListConnectionId(event.target.value); setEditingBrandMappingId(''); setLocalId(''); setExternalId(''); setBrandExternalSearch(''); setBrandExternalPickerOpen(false); setNotice('') }}><option value="">{brandPlatformLabel} Bağlantısı</option>{platformConnections.map(item => <option value={item.id} key={item.id}>{brandPlatformLabel}{platformConnections.length > 1 ? ` · ${item.displayName}` : ''}</option>)}</select>}<div className="platform-category-picker-wrap" ref={brandPickerRef}><button type="button" className="panel-category-picker-trigger" aria-expanded={brandExternalPickerOpen} onClick={() => setBrandExternalPickerOpen(value => !value)} disabled={!connectionId || !references.data}><span>{selectedExternalBrand?.name ?? (references.data ? 'Platform Markası Seçin...' : references.isFetching || syncBrands.isPending ? 'Markalar yükleniyor…' : 'Önce marka listesini senkronize edin')}</span><UiIcon name="chevronDown" /></button>{brandExternalPickerOpen && <div className="panel-category-picker platform-category-picker brand-reference-picker" role="dialog" aria-label="Platform markası seçimi"><div><input ref={focusOnMount} aria-label="Platform markalarında ara" value={brandExternalSearch} onChange={event => setBrandExternalSearch(event.target.value)} placeholder="Marka adı veya marka no ara..." /><button type="button" aria-label="Platform marka menüsünü kapat" onClick={() => setBrandExternalPickerOpen(false)}><UiIcon name="close" /></button></div><div role="listbox">{visibleExternalBrands.length ? visibleExternalBrands.map(item => <button type="button" role="option" aria-selected={item.externalId === externalId} key={item.externalId} onClick={() => { setExternalId(item.externalId); setBrandExternalPickerOpen(false) }}><span>{item.name}</span><small>Marka no: {item.externalId}</small></button>) : <span>{externalBrands.length ? 'Aramayla eşleşen marka bulunamadı.' : 'Marka listesi boş. Senkronize Et ile yeniden deneyin.'}</span>}</div></div>}</div></div></div></section></div>
      {connections.isError || localBrands.isError ? <ErrorBox error={connections.error ?? localBrands.error} /> : connectionId && (references.data || references.isError) ? <div className={`mapping-action brand-reference-action${references.isError && !references.data ? ' has-error' : ''}`}><span>{references.data ? <><strong>{externalBrands.length.toLocaleString('tr-TR')}</strong> marka · {formatDateTime(references.data.fetchedAt)}{mapping.data ? ` · Kayıt v${mapping.data.version}` : ''}</> : 'Marka listesi henüz alınmadı. Senkronize Et ile alın.'}</span><div className="brand-reference-buttons"><button type="button" className="secondary" disabled={!connectionId || syncBrands.isPending} onClick={() => syncBrands.mutate()}><UiIcon name={syncBrands.isPending ? 'loader' : 'refresh'} />{syncBrands.isPending ? 'Kuyruğa Alınıyor…' : 'Senkronize Et'}</button>{references.data && <button type="button" disabled={!localId || !externalId || save.isPending || mapping.isLoading} onClick={saveSelectedBrandMapping}>{save.isPending ? 'Kaydediliyor…' : mapping.data ? 'Eşleştirmeyi Güncelle' : 'Eşleştirmeyi Kaydet'}</button>}</div></div> : null}
    </article>
    </>}
    <article className="mapping-reference-card mapping-saved-card brand-mapping-saved-card"><header><h2>3. Kayıtlı Marka Eşleştirmeleri</h2>{savedBrandPlatformTabs}<div><span className="mapping-record-count">{savedBrandMappings.length.toLocaleString('tr-TR')} kayıt</span></div></header><div className="mapping-saved-table is-brand"><table><thead><tr><th>Panel Markası</th><th>Platform</th><th>{savedBrandPlatformLabel} Markası</th><th>Durum</th><th>İşlemler</th></tr></thead><tbody>{!savedConnectionId ? <tr><td className="mapping-empty-table-cell" colSpan={5}>Bu platformda kayıtlı marka eşleştirmesi yok.</td></tr> : brandMappings.isLoading ? <tr><td className="mapping-empty-table-cell" colSpan={5}>Kayıtlı marka eşleştirmeleri yükleniyor…</td></tr> : brandMappings.isError ? <tr><td className="mapping-empty-table-cell" colSpan={5}>Kayıtlı marka eşleştirmeleri alınamadı.</td></tr> : savedBrandMappings.length ? savedBrandMappings.map(item => <tr key={item.id} className={editingBrandMappingId === item.id ? 'is-editing' : ''}><td>{localBrandById.get(item.localId)?.name ?? item.localId}</td><td><span className="ms-platform">{platformLogoSource(savedBrandPlatformCode) && <img className={platformLogoClass(savedBrandPlatformCode)} src={platformLogoSource(savedBrandPlatformCode)!} alt="" aria-hidden="true" />}{savedBrandPlatformLabel}</span></td><td className="brand-mapping-inline-editor">{editingBrandMappingId === item.id ? <SearchableSelect label="" value={editingExternalId} options={savedExternalBrands.map(brand => ({ value: brand.externalId, label: brand.name, description: `${savedBrandPlatformLabel} marka no: ${brand.externalId}` }))} placeholder={`${savedBrandPlatformLabel} markası ara`} disabled={!savedReferences.data} onChange={setEditingExternalId} /> : externalBrandById.get(item.externalId)?.name ?? item.externalId}</td><td><span className={`mapping-table-status ${item.status === 'VERIFIED' || item.status === 'ACTIVE' ? 'active' : 'error'}`}><MappingStateGlyph state={item.status === 'VERIFIED' || item.status === 'ACTIVE' ? 'done' : 'partial'} />{mappingStatusLabel(item.status)}</span></td><td><div className="mapping-row-actions">{editingBrandMappingId === item.id ? <><button type="button" className="mapping-icon-button" aria-label="Marka eşleştirmesini kaydet" disabled={!editingExternalId || !savedReferences.data || save.isPending} onClick={() => saveEditedBrandMapping(item)}><UiIcon name="check" /></button><button type="button" className="mapping-icon-button" aria-label="Marka düzenlemeyi iptal et" onClick={() => setEditingBrandMappingId('')}><UiIcon name="close" /></button></> : <><button type="button" className="mapping-icon-button" aria-label="Marka eşleştirmesini düzenle" onClick={() => { setEditingExternalId(item.externalId); setEditingBrandMappingId(item.id); setBrandEditOpen(false); setNotice('') }}><UiIcon name="edit" /></button><button type="button" className="mapping-icon-button mapping-delete-button" aria-label="Eşleştirmeyi sil" disabled={removeMapping.isPending} onClick={() => void confirm({ title: 'Marka Eşleştirmesi Kaldırılsın mı?', message: 'Bu marka eşleştirmesi kaldırılacak.', confirmLabel: 'Kaldır', destructive: true }).then(accepted => { if (accepted) removeMapping.mutate(item) })}><MappingDeleteIcon /></button></>}</div></td></tr>) : <tr><td className="mapping-empty-table-cell" colSpan={5}>Henüz kaydedilmiş marka eşleştirmesi bulunmuyor.</td></tr>}</tbody></table></div><footer><span>Toplam {savedBrandMappings.length.toLocaleString('tr-TR')} eşleştirme</span></footer></article>
    {brandTransferOpen && brandTransferBundle && <div className="workspace-modal-backdrop mapping-transfer-backdrop" role="presentation" onMouseDown={() => { if (!brandTransferBusy) { setBrandTransferOpen(false); setBrandTransferBundle(null) } }}><DialogSurface as="section" onClose={() => { if (!brandTransferBusy) { setBrandTransferOpen(false); setBrandTransferBundle(null) } }} className="workspace-modal mapping-transfer-modal" aria-labelledby="brand-transfer-title" onMouseDown={event => event.stopPropagation()}><header><div><p className="eyebrow">Marka Yedeği</p><h2 id="brand-transfer-title">İçe Aktarma Alanlarını Seçin</h2><p>{brandTransferBundle.brands.length} marka · {brandTransferBundle.mappings.length} eşleştirme</p></div><button type="button" className="modal-close" onClick={() => { setBrandTransferOpen(false); setBrandTransferBundle(null) }} disabled={brandTransferBusy} aria-label="Marka içe aktarmayı kapat"><UiIcon name="close" /></button></header><div className="mapping-transfer-options"><label><input type="checkbox" checked={brandTransferSelection.brands} onChange={event => setBrandTransferSelection(current => ({ ...current, brands: event.target.checked }))} /><span><strong>Panel Markaları</strong><small>Eksik markaları ekler, pasif markaları yeniden etkinleştirir.</small></span></label><label><input type="checkbox" checked={brandTransferSelection.mappings} onChange={event => setBrandTransferSelection(current => ({ ...current, mappings: event.target.checked }))} /><span><strong>Marka Eşleştirmeleri</strong><small>Mevcut {savedBrandPlatformLabel} marka referanslarıyla eşleştirmeleri günceller.</small></span></label></div><p className="mapping-transfer-warning">Mevcut kayıtlar silinmez; aynı marka adları güncellenir, eksik olanlar eklenir. Güncel {savedBrandPlatformLabel} marka referansında bulunmayan eşleştirmeler atlanır.</p><footer><button type="button" className="secondary" onClick={() => { setBrandTransferOpen(false); setBrandTransferBundle(null) }} disabled={brandTransferBusy}>Vazgeç</button><button type="button" onClick={() => void importBrandMappings()} disabled={brandTransferBusy}>{brandTransferBusy ? 'İçe Aktarılıyor…' : 'Seçilenleri Güncelle'}</button></footer></DialogSurface></div>}
    {brandEditOpen && <div className="brand-mapping-modal-backdrop" role="presentation" onMouseDown={() => setBrandEditOpen(false)}><DialogSurface as="section" onClose={() => setBrandEditOpen(false)} className="brand-mapping-modal" aria-labelledby="brand-mapping-edit-title" onMouseDown={event => event.stopPropagation()}><header><div><small>Marka eşleme</small><h2 id="brand-mapping-edit-title">Marka Eşleştirmesini Düzenle</h2><p>Panel markası için {brandPlatformLabel} karşılığını güncelleyin.</p></div><button type="button" className="modal-close" aria-label="Marka eşleme penceresini kapat" onClick={() => setBrandEditOpen(false)}><UiIcon name="close" /></button></header><div className="brand-mapping-modal-body"><div className="brand-mapping-selected-local"><small>Panel markası</small><strong>{localBrandById.get(localId)?.name ?? localId}</strong></div><SearchableSelect label={`${brandPlatformLabel} Markası`} value={externalId} options={externalBrands.map(item => ({ value: item.externalId, label: item.name, description: `${brandPlatformLabel} marka no: ${item.externalId}` }))} placeholder={references.isLoading ? 'Markalar yükleniyor…' : 'Marka adı veya marka no ara'} disabled={!references.data} onChange={setExternalId} /></div><footer><button type="button" className="secondary" onClick={() => setBrandEditOpen(false)}>Vazgeç</button><button type="button" disabled={!localId || !externalId || save.isPending || mapping.isLoading} onClick={saveSelectedBrandMapping}>{save.isPending ? 'Kaydediliyor…' : 'Değişikliği Kaydet'}</button></footer></DialogSurface></div>}
  </section>
}
