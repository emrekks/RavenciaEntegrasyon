// Eşleştirmeler (/mappings/categories).
import type { ApiAnswer } from '../../../shared/api/api-answer'
import { CollapsibleLibrary } from './CollapsibleLibrary'
import { useRememberedHeight } from '../../../shared/remembered-height'
import './mapping-design-system.css'
import { useEffect, useLayoutEffect, useMemo, useRef, useState, type ChangeEvent } from 'react'
import { Link, useSearchParams } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi, loadAllPages } from '../../../shared/api'
import { Pagination, TitleHint, UiIcon, useConfirmDialog, DialogSurface } from '../../../shared/components'
import { RrModal } from '../../../shared/ui/rr-components'
import { isStaleMappingError, mappingSaveErrorMessage, refreshLocalCategories, refreshMappingConflicts } from './mapping-refresh'
import './ValuePicker' // keeps its stylesheet's place in this page's CSS order
import { newIdempotencyKey } from '../../../shared/idempotency'
import { categoryDeactivationBody } from './category-deactivation'
import { MessageToast } from '../../../shared/components/NoticeToast'
import { platformLogoClass, platformLogoSource } from '../../../shared/platform-logos'
import { MappingPlatformTab } from './MappingPlatformTab'
import { MobileActionMenu } from '../../../shared/MobileActionMenu'
import '../PanelValueMultiSelect' // keeps its stylesheet's place in this page's CSS order
import { useScrollLock } from '../../../shared/hooks/useScrollLock'
import { appendNotification } from '../../../shared/notifications'
import '../AttributeDefaultsPanel' // keeps its stylesheet's place in this page's CSS order
import '../MappingConflictsPanel' // keeps its stylesheet's place in this page's CSS order
import { activeMappingConnectionsForPlatform, mappingPlatformDefinitions, mappingPlatformLabel, type MappingPlatformCode } from '../mapping-platforms'
import { suggestCategoryMappings } from '../category-suggestions'
import '../../../shared/view-tabs' // keeps its stylesheet's place in this page's CSS order
import { formatDateTime } from '../../../shared/format-date'
import { focusOnMount } from '../../../shared/focus'
import { Connection, MappingStateGlyph, Page, notificationKindFromMessage } from '../shared'
import { useDismissable } from '../../../shared/hooks/useDismissable'
import './brand-reference' // keeps its stylesheet's place in this page's CSS order
import { savedCategoryLabel } from './saved-category-label'
// Styles of this route only (their rules are scoped to its own containers).
import '../value-mapping-modal.css'
import '../mapping-workspace.css'
import '../category-workbench.css'
import { PanelSelect } from '../../../shared/PanelSelect'
import { EmptyState } from '../../../shared/components/EmptyState'
import { exactMatchConfirmMessage, exactMatchResultMessage, typedProductTypeOption, type ShopifyExactMatchView } from './shopify-product-types'
import { connectionQueries, queryKeys } from '../../../shared/api/query-keys'
import { useMappingNotificationConnection, MappingDeleteIcon, type LocalCategory, type LocalAttribute, type ReferenceData, type CatalogMapping, MappingViewTabs, wait, isOptionAttribute, cleanMarketplaceCategoryPath, categoryMappingStatus } from './mapping-pages/mapping-shared'
import { CategoryAttributeWorkspaceModal, SavedCategoryCell, PanelAttributeLibraryBuilder, CategoryAttributeWorkspace } from './mapping-pages/CategoryAttributeWorkspace'
import { BrandMappingPage } from './mapping-pages/BrandMappingPage'
export { SearchableSelect } from './mapping-pages/mapping-shared'
export { ProductCategoryFieldsDialog } from './mapping-pages/CategoryAttributeWorkspace'
export { OptionMappingCard, WebColorMappingCard } from './mapping-pages/ValueMappingEditors'
export { BrandMappingPage } from './mapping-pages/BrandMappingPage'

type MappingTransferBundle = { format: 'RAVENCIA_MAPPING_BUNDLE'; version: 1; exportedAt: string; categories: LocalCategory[]; attributes: LocalAttribute[]; categoryMappings: CatalogMapping[]; attributeMappings: CatalogMapping[]; attributeValueMappings: CatalogMapping[] }

type MappingTransferScope = 'categories' | 'options' | 'attributes' | 'mappings'

export function MappingPage() {
  const [searchParams] = useSearchParams()
  useEffect(() => {
    const main = document.querySelector<HTMLElement>('.app-shell.stitch-shell > main')
    if (main) main.scrollTop = 0
  }, [])
  return searchParams.get('view') === 'brands' ? <BrandMappingPage /> : <CategoryMappingWorkspace />
}
function CategoryMappingWorkspace() {
  const [confirm, confirmDialog] = useConfirmDialog()
  const client = useQueryClient()
  const [connectionId, setConnectionId] = useState(''); const [selectedPlatformCode, setSelectedPlatformCode] = useState<MappingPlatformCode>('TRENDYOL'); const [localId, setLocalId] = useState(''); const [externalId, setExternalId] = useState(''); const [notice, setNotice] = useState(''); const [categoryName, setCategoryName] = useState(''); const [categoryLibrarySearch, setCategoryLibrarySearch] = useState(''); const [categoryLibraryOpen, setCategoryLibraryOpen] = useState(false); const [categoryLibrarySort, setCategoryLibrarySort] = useState<'NAME_ASC' | 'NAME_DESC'>('NAME_ASC'); const [externalSearch, setExternalSearch] = useState(''); const [externalPickerOpen, setExternalPickerOpen] = useState(false); const [savedSearch, setSavedSearch] = useState(''); const [savedSort, setSavedSort] = useState<'NAME_ASC' | 'NAME_DESC'>('NAME_ASC'); const [savedPage, setSavedPage] = useState(1); const [advancedOpen, setAdvancedOpen] = useState(false); const [panelPickerOpen, setPanelPickerOpen] = useState(false); const [panelPickerSearch, setPanelPickerSearch] = useState(''); const [exportOpen, setExportOpen] = useState(false); const [exportSelection, setExportSelection] = useState<Record<MappingTransferScope, boolean>>({ categories: true, options: true, attributes: true, mappings: true }); const [transferOpen, setTransferOpen] = useState(false); const [transferBundle, setTransferBundle] = useState<MappingTransferBundle | null>(null); const [transferSelection, setTransferSelection] = useState<Record<MappingTransferScope, boolean>>({ categories: true, options: true, attributes: true, mappings: true }); const [transferBusy, setTransferBusy] = useState(false)
  const autoSelectedConnectionPlatform = useRef(false)
  useScrollLock(exportOpen || transferOpen || advancedOpen || panelPickerOpen, true)
  const mappingConnection = useMappingNotificationConnection(connectionId)
  useEffect(() => {
    if (!notice) return
    const kind = notificationKindFromMessage(notice)
    appendNotification({ message: notice, kind, connectionId: mappingConnection.current || undefined, operation: 'Eşleştirme' })
    document.documentElement.dataset.rvMappingFeedback = kind
    const timeout = window.setTimeout(() => setNotice(''), kind === 'info' ? 7000 : 5500)
    return () => window.clearTimeout(timeout)
  }, [notice, mappingConnection])
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
  const connections = useQuery(connectionQueries.list<Connection>())
  useEffect(() => {
    if (!connections.data || autoSelectedConnectionPlatform.current) return
    const availablePlatform = mappingPlatformDefinitions.find(platform => activeMappingConnectionsForPlatform(connections.data.items, platform.code).length > 0)
    if (!availablePlatform) return
    autoSelectedConnectionPlatform.current = true
    setSelectedPlatformCode(current => activeMappingConnectionsForPlatform(connections.data!.items, current).length ? current : availablePlatform.code)
  }, [connections.data])
  const [categoryAddedSignal, setCategoryAddedSignal] = useState(0)
  const localCategories = useQuery({ queryKey: queryKeys.categories('mapping'), queryFn: ({ signal }) => loadAllPages<LocalCategory>('/catalog/categories', { signal }) })
  const categoryListHeight = useRememberedHeight<HTMLDivElement>('category-library', localCategories.isLoading)
  const localAttributes = useQuery({ queryKey: queryKeys.attributes('mapping-builder'), queryFn: ({ signal }) => loadAllPages<LocalAttribute>('/catalog/attributes', { signal }) })
  const references = useQuery({ queryKey: queryKeys.referenceCategories(connectionId), queryFn: ({ signal }) => hubApi<ApiAnswer<'getReferenceDataCategories'>>(`/reference-data/categories?connectionId=${encodeURIComponent(connectionId)}`, { signal }), enabled: !!connectionId, retry: false })
  const mapping = useQuery({ queryKey: queryKeys.categoryMapping(localId, connectionId), queryFn: ({ signal }) => hubApi<CatalogMapping | null>(`/mappings/categories/${localId}?connectionId=${encodeURIComponent(connectionId)}`, { signal }), enabled: !!localId && !!connectionId, retry: false })
  const categoryMappings = useQuery({ queryKey: queryKeys.categoryMappings(connectionId), queryFn: ({ signal }) => hubApi<CatalogMapping[]>(`/mappings/categories?connectionId=${encodeURIComponent(connectionId)}`, { signal }), enabled: !!connectionId, retry: false })
  const syncCategories = useMutation({ mutationFn: () => {
    if (!connectionId) throw new Error(`Aktif ${mappingPlatformLabel(selectedPlatformCode)} bağlantısı bulunamadı.`)
    return hubApi(`/connections/${connectionId}/reference-sync-jobs?resourceType=CATEGORIES`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: '{}' })
  }, onSuccess: async () => {
    setNotice(`${mappingPlatformLabel(selectedPlatformCode)} kategori listesi salt-okunur eşitleme kuyruğuna alındı.`)
    for (const delay of [500, 1000, 2000, 4000]) {
      await wait(delay)
      await client.invalidateQueries({ queryKey: queryKeys.referenceCategories(connectionId) })
    }
  }, onError: reason => setNotice(reason instanceof Error ? reason.message : 'Kategori eşitleme başlatılamadı.') })
  const save = useMutation({ mutationFn: () => { if (!references.data || !localId || !externalId) throw new Error(`Panel kategorisi ve ${mappingPlatformDefinitions.find(item => item.code === selectedPlatformCode)?.label ?? 'platform'} kategorisi zorunludur.`); return hubApi<CatalogMapping>(`/mappings/categories/${localId}`, { method: 'PUT', headers: mapping.data ? { 'If-Match': `"v${mapping.data.version}"` } : {}, body: JSON.stringify({ connectionId, snapshotId: references.data.snapshotId, externalId, status: 'VERIFIED' }) }) }, onSuccess: async value => { setNotice('Kategori eşleştirmesi kaydedildi.'); setExternalId(value.externalId); client.setQueryData(queryKeys.categoryMapping(localId, connectionId), value); client.setQueryData(queryKeys.categoryMapping(localId, connectionId, 'embedded'), value); client.setQueryData<CatalogMapping[]>(queryKeys.categoryMappings(connectionId), current => current ? [...current.filter(item => item.localId !== value.localId), value] : [value]); await Promise.all([client.invalidateQueries({ queryKey: queryKeys.categoryMapping(localId, connectionId) }), client.invalidateQueries({ queryKey: queryKeys.categoryMapping(localId, connectionId, 'embedded') }), client.invalidateQueries({ queryKey: queryKeys.categoryMappings(connectionId) }), client.invalidateQueries({ queryKey: queryKeys.referenceCategories(connectionId) })]) }, onError: async reason => { setNotice(mappingSaveErrorMessage(reason, 'Eşleme kaydedilemedi.')); if (isStaleMappingError(reason)) await Promise.all([client.invalidateQueries({ queryKey: queryKeys.categoryMapping(localId, connectionId) }), client.invalidateQueries({ queryKey: queryKeys.categoryMappings(connectionId) })]) }, onSettled: () => refreshMappingConflicts(client, connectionId) })
  const createCategory = useMutation({ mutationFn: () => { if (!categoryName.trim()) throw new Error('Kategori adı zorunludur.'); return hubApi<ApiAnswer<'postCatalogCategories'>>('/catalog/categories', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ name: categoryName.trim(), parentId: null }) }) }, onSuccess: async category => { client.setQueryData<Page<LocalCategory>>(queryKeys.categories('mapping'), current => current ? { ...current, items: [...current.items.filter(item => item.id !== category.id), category] } : current); setLocalId(category.id); setExternalId(''); setCategoryName(''); setCategoryAddedSignal(value => value + 1); setCategoryAddedSignal(value => value + 1); setNotice(`“${category.name}” panel kategorisi oluşturuldu ve seçildi.`); await refreshLocalCategories(client) }, onError: reason => setNotice(reason instanceof Error ? reason.message : 'Kategori oluşturulamadı.') })
  useLayoutEffect(() => { setExternalId(mapping.data?.externalId ?? '') }, [mapping.data])
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
  const selectedPlatform = mappingPlatformDefinitions.find(platform => platform.code === selectedPlatformCode) ?? mappingPlatformDefinitions[0]
  // Shopify's product type is free text: there is nothing to verify, so its saved list has no Durum column.
  const showSavedStatus = selectedPlatform.code !== 'SHOPIFY'
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
  const archiveCategory = useMutation({ mutationFn: (category: LocalCategory) => hubApi<ApiAnswer<'patchCatalogCategoriesById'>>(`/catalog/categories/${category.id}`, { method: 'PATCH', headers: { 'If-Match': `"v${category.version}"` }, body: JSON.stringify(categoryDeactivationBody(category)) }), onSuccess: async (_, category) => { if (localId === category.id) { setLocalId(''); setExternalId('') }; await refreshLocalCategories(client); setNotice(`“${category.name}” panel kategorilerinden kaldırıldı.`) }, onError: reason => setNotice(reason instanceof Error ? reason.message : 'Kategori kaldırılamadı.') })
  // The delete uses the version the server holds now (not the one the list showed), and the lists and the conflict
  // panel are reloaded whatever the outcome.
  const removeMapping = useMutation({ mutationFn: async (item: CatalogMapping) => { const current = await hubApi<CatalogMapping | null>(`/mappings/categories/${item.localId}?connectionId=${encodeURIComponent(connectionId)}`); if (!current || current.externalId !== item.externalId) throw new Error('Bu eşleştirme bu sırada değişti veya kaldırıldı. Liste yenilendi.'); return hubApi<boolean>(`/mappings/categories/${item.localId}?connectionId=${encodeURIComponent(connectionId)}`, { method: 'DELETE', headers: { 'If-Match': `"v${current.version}"` } }) }, onSuccess: (_, item) => { if (localId === item.localId) { setExternalId(''); client.setQueryData(queryKeys.categoryMapping(localId, connectionId), null) }; setNotice('Kategori eşleştirmesi kaldırıldı.') }, onError: reason => setNotice(mappingSaveErrorMessage(reason, 'Eşleştirme kaldırılamadı.')), onSettled: async (_, __, item) => { await Promise.all([client.invalidateQueries({ queryKey: queryKeys.categoryMappings(connectionId) }), client.invalidateQueries({ queryKey: queryKeys.categoryMapping(item.localId, connectionId) }), refreshMappingConflicts(client, connectionId)]) } })
  // "Birebir Eşle" (Shopify only): counts first, asks, then maps every unmapped panel category to the store's product type
  // of the same name. It never changes a mapping and never creates a type.
  const [exactMatching, setExactMatching] = useState(false)
  async function matchSameNamedProductTypes() {
    if (!connectionId || exactMatching) return
    setExactMatching(true)
    try {
      const path = `/mappings/categories/exact-match?connectionId=${encodeURIComponent(connectionId)}`
      const preview = await hubApi<ShopifyExactMatchView>(`${path}&apply=false`, { method: 'POST', body: '{}' })
      const message = exactMatchConfirmMessage(preview)
      if (!message) { setNotice(exactMatchResultMessage(preview)); return }
      if (!(await confirm({ title: 'Birebir Eşlensin mi?', message, confirmLabel: 'Eşle' }))) return
      const result = await hubApi<ShopifyExactMatchView>(`${path}&apply=true`, { method: 'POST', body: '{}' })
      setNotice(exactMatchResultMessage(result))
      await Promise.all([client.invalidateQueries({ queryKey: queryKeys.categoryMappings(connectionId) }), client.invalidateQueries({ queryKey: queryKeys.categoryMapping() }), client.invalidateQueries({ queryKey: queryKeys.publicationAttributeReadiness() }), refreshMappingConflicts(client, connectionId)])
    } catch (reason) { setNotice(mappingSaveErrorMessage(reason, 'Birebir eşleme yapılamadı.')) } finally { setExactMatching(false) }
  }
  const selectedExternal = externalLeaves.find(item => item.externalId === externalId)
  const selectedLocal = localLeaves.find(item => item.id === localId)
  // Up to three likely marketplace categories for a panel category that has no mapping yet; a click only selects, Save still decides.
  const categorySuggestions = useMemo(() => selectedLocal && !mapping.data && !mapping.isLoading ? suggestCategoryMappings(selectedLocal, externalLeaves) : [], [selectedLocal, mapping.data, mapping.isLoading, externalLeaves])
  const normalizedExternalSearch = externalSearch.trim().toLocaleLowerCase('tr-TR')
  const matchingExternal = externalLeaves.filter(item => !normalizedExternalSearch || `${item.path} ${item.name} ${item.externalId}`.toLocaleLowerCase('tr-TR').includes(normalizedExternalSearch))
  const visibleExternal = [...(selectedExternal ? [selectedExternal] : []), ...matchingExternal.filter(item => item.externalId !== selectedExternal?.externalId)].slice(0, 20)
  // Shopify: the search text may name a product type the store does not hold yet (the first publication creates it).
  const newProductTypeOption = productTypeCategories ? typedProductTypeOption(externalSearch, externalLeaves) : null
  const savedMappings = categoryMappings.data ?? []
  const savedRows = savedMappings.map(item => ({ item, local: localLeaves.find(category => category.id === item.localId), external: references.data?.items.find(category => category.externalId === item.externalId) })).filter(row => !savedSearch.trim() || `${row.local?.path ?? row.item.localId} ${row.external?.path ?? row.item.externalId}`.toLocaleLowerCase('tr-TR').includes(savedSearch.trim().toLocaleLowerCase('tr-TR')))
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
          const target = existing ?? await hubApi<ApiAnswer<'postCatalogCategories'>>('/catalog/categories', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ name: source.name, parentId: parentPath ? targetCategories.find(item => item.path === parentPath)?.id ?? null : null }) })
          categoryIdMap.set(source.id, target.id); if (!targetCategories.some(item => item.id === target.id)) targetCategories.push(target)
        }
      }
      const importAttributes = transferBundle.attributes.filter(attribute => transferSelection.options && isOptionAttribute(attribute) || transferSelection.attributes && !isOptionAttribute(attribute))
      for (const source of importAttributes) {
        const existing = targetAttributes.find(item => item.id === source.id || item.code.toLocaleLowerCase('tr-TR') === source.code.toLocaleLowerCase('tr-TR') || item.name.toLocaleLowerCase('tr-TR') === source.name.toLocaleLowerCase('tr-TR'))
        const target = existing ?? await hubApi<ApiAnswer<'postCatalogAttributes'>>('/catalog/attributes', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ code: source.code, name: source.name, dataType: source.dataType, selectionMode: source.values.length ? 'SINGLE' : null, unit: null, values: source.values.map((value, index) => ({ value: value.value, sortOrder: index })) }) })
        attributeIdMap.set(source.id, target.id); if (!targetAttributes.some(item => item.id === target.id)) targetAttributes.push(target)
        const missingValues = source.values.filter(value => !target.values.some(existingValue => existingValue.value.toLocaleLowerCase('tr-TR') === value.value.toLocaleLowerCase('tr-TR')))
        if (missingValues.length) await hubApi<ApiAnswer<'postCatalogAttributesByIdValues'>>(`/catalog/attributes/${target.id}/values`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify(missingValues.map((value, index) => ({ value: value.value, sortOrder: target.values.length + index }))) })
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
      setTransferOpen(false); setTransferBundle(null); setNotice('Seçtiğiniz eşleştirme alanları güvenli şekilde içe aktarıldı.'); await Promise.all([refreshLocalCategories(client), client.invalidateQueries({ queryKey: queryKeys.attributes('mapping-builder') }), client.invalidateQueries({ queryKey: queryKeys.categoryMappings(connectionId) }), client.invalidateQueries({ queryKey: queryKeys.attributeMappings() })])
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
      <div className="mapping-section-heading"><div><h2><UiIcon name="addCategory" />1. Yeni Kategori Ekle</h2></div></div>
      <div className="mapping-local-manager-dialog mapping-local-manager-dialog-standalone">
        <form className="platform-category-create" onSubmit={event => { event.preventDefault(); createCategory.mutate() }}><label>Yeni Panel Kategorisi<input aria-label="Yeni panel kategorisi adı" maxLength={160} value={categoryName} onChange={event => setCategoryName(event.target.value)} placeholder="Örn. Anne Bluz" required /></label><button disabled={createCategory.isPending}>{createCategory.isPending ? 'Ekleniyor…' : 'Kategori Ekle'}</button></form>
        <CollapsibleLibrary title="Kategori Listesi" count={activeCategories.length} forceOpen={!!categoryLibrarySearch.trim()} openSignal={categoryAddedSignal}>
          <div className="category-library-tools"><div className={categoryLibraryOpen ? 'category-library-search-control is-open' : 'category-library-search-control'}><input aria-label="Eklenen panel kategorilerinde ara" value={categoryLibrarySearch} onFocus={() => setCategoryLibraryOpen(true)} onClick={() => setCategoryLibraryOpen(true)} onChange={event => setCategoryLibrarySearch(event.target.value)} placeholder="Kategori ara…" /><button type="button" className="category-library-search-toggle" aria-label={categoryLibraryOpen ? 'Kategorileri kapat' : 'Kategorileri aç'} aria-expanded={categoryLibraryOpen} onMouseDown={event => event.preventDefault()} onClick={() => setCategoryLibraryOpen(value => !value)}><span aria-hidden="true" /></button></div><PanelSelect className="category-library-sort" ariaLabel="Panel kategorilerini sırala" width={96} value={categoryLibrarySort} options={[{ value: 'NAME_ASC', label: 'A–Z' }, { value: 'NAME_DESC', label: 'Z–A' }]} onChange={setCategoryLibrarySort} /><span>{filteredCategoryLibrary.length.toLocaleString('tr-TR')} sonuç</span></div>
          {filteredCategoryLibrary.length === 0 && !localCategories.isLoading ? <EmptyState icon="mappings" title={activeCategories.length === 0 ? 'Henüz Kategori Yok' : 'Kategori Bulunamadı'}>{activeCategories.length === 0 ? 'Henüz panel kategorisi eklenmedi.' : 'Aramaya uygun kategori yok.'}</EmptyState> : <div className="category-library-list" ref={categoryListHeight.ref} style={categoryListHeight.style}>{filteredCategoryLibrary.slice(0, 100).map(category => <span key={category.id} className={category.id === localId ? 'active' : ''}><button type="button" onClick={() => { setLocalId(category.id); setExternalId('') }}>{category.name}</button><button type="button" className="category-library-remove" aria-label="Kategoriyi kaldır" onClick={() => archiveCategory.mutate(category)} disabled={archiveCategory.isPending}><UiIcon name="close" /></button></span>)}</div>}
        </CollapsibleLibrary>
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
        {mappingPlatformDefinitions.map(platform => <MappingPlatformTab key={platform.code} code={platform.code} label={platform.label} available={activeMappingConnections.some(item => item.platformCode.toUpperCase() === platform.code)} selected={selectedPlatformCode === platform.code} activeTitle={`${platform.label} kategorilerini göster`} onSelect={() => selectMappingPlatform(platform.code)} />)}
      </div>
      {!connectionId && !connections.isLoading && !selectedPlatformConnections.length && (autoSelectedConnectionPlatform.current || !activeMappingConnections.length) ? <div className="unknown mapping-connection-empty" role="status"><strong>{selectedPlatform.label} bağlantısı gerekli</strong><p>Kategori referanslarını alıp eşleme yapabilmek için önce aktif bir {selectedPlatform.label} bağlantısı kurun.</p><Link className="button-link" to="/integrations">Entegrasyonlara Git</Link></div> : <div className="mapping-reference-refresh-row">{selectedPlatformConnections.length > 1 && <label className="mapping-reference-connection-select" htmlFor="mapping-store-connection">Bağlantı<PanelSelect id="mapping-store-connection" ariaLabel="Mağaza bağlantısı" value={connectionId} onChange={selectMappingConnection} options={selectedPlatformConnections.map(item => ({ value: item.id, label: `${item.displayName} · ${item.environment === 'PRODUCTION' ? 'Canlı' : 'Stage'} · ${item.externalStoreId}` }))} /></label>}</div>}
    </>
  )
  // The reference list's size and age sit behind the "i" beside the target label, not in the footer.
  const categoryReferenceInfo = `${references.isLoading ? 'Kategori listesi yükleniyor…' : references.data ? `${externalLeaves.length.toLocaleString('tr-TR')} ${productTypeCategories ? 'ürün türü' : 'yaprak kategori'} · Güncellenme ${formatDateTime(references.data.fetchedAt)}` : references.isError ? 'Güncel kategori listesi bulunamadı' : 'Kategori listesi henüz alınmadı'}${mapping.data ? ` · Kayıt v${mapping.data.version}` : ''}`
  return <section className="content f3 mapping-page mapping-workspace stitch-reference-mapping">{confirmDialog}<header className="mapping-reference-heading"><div><p className="eyebrow">Katalog</p><h1>Eşleştirmeler</h1></div><div className="mapping-header-tools"><div className="mapping-reference-heading-actions" aria-label="Eşleştirme yedeği işlemleri"><div className="mapping-transfer-actions"><MobileActionMenu><button type="button" className="secondary" title="Dışa aktarılacak alanları seç" onClick={() => { setExportSelection({ categories: true, options: true, attributes: true, mappings: true }); setExportOpen(true) }} disabled={transferBusy || !connectionId}><UiIcon name="download" size={18} />{transferBusy ? 'Hazırlanıyor…' : 'Dışa Aktar'}</button><label className="mapping-transfer-upload" title="JSON eşleştirme yedeğini içe aktar"><UiIcon name="import" size={18} />İçe Aktar<input type="file" accept="application/json,.json" onChange={readMappingBundle} disabled={transferBusy} /></label></MobileActionMenu></div></div></div></header><MappingViewTabs active="category" />{notice && <MessageToast message={notice} onClose={() => setNotice('')} />}{localCategoryManagerSection}
     {/* eslint-disable-next-line jsx-a11y/click-events-have-key-events, jsx-a11y/no-noninteractive-element-interactions -- a click on the dimmed area closes the panel; the summary toggles it from the keyboard */}
     <article className="mapping-reference-card mapping-category-builder"><header className="mapping-builder-head"><h2><UiIcon name="link" />2. Yeni Kategori Eşleştirme</h2><div className="mapping-builder-head-actions">{productTypeCategories && <button type="button" className="secondary" title="Eşlemesi olmayan panel kategorilerini Shopify'daki aynı adlı ürün türüyle eşler; mevcut eşlemeleri değiştirmez, yeni tür açmaz." disabled={!connectionId || exactMatching || references.isError || references.isLoading} onClick={() => void matchSameNamedProductTypes()}><UiIcon name="equal" />{exactMatching ? 'Eşleştiriliyor…' : 'Birebir Eşle'}</button>}<button type="button" className="secondary" disabled={!connectionId || syncCategories.isPending} onClick={() => syncCategories.mutate()}><UiIcon name="refresh" />{syncCategories.isPending ? 'Kuyruğa Alınıyor…' : 'Senkronize Et'}</button></div></header>{mappingPlatformTabs}<div className="mapping-reference-grid"><div className="mapping-link-rail" aria-hidden="true"><UiIcon name="link" /><span>Eşleştir</span></div><section className="mapping-reference-local"><label>Panel Kategorisi<button type="button" className="panel-category-picker-trigger" aria-expanded={panelPickerOpen} onClick={() => setPanelPickerOpen(value => !value)} disabled={!connectionId}><span>{localLeaves.find(item => item.id === localId)?.path ?? 'Panel Kategorisi Seçin'}</span><UiIcon name="chevronDown" /></button></label>{panelPickerOpen && <div className="panel-category-picker" role="dialog" aria-label="Panel kategorisi seçimi"><div><input aria-label="Panel kategorilerinde ara" value={panelPickerSearch} onChange={event => setPanelPickerSearch(event.target.value)} placeholder="Panel kategorilerinde ara..." /></div><div role="listbox">{panelPickerItems.length ? panelPickerItems.map(item => <button type="button" role="option" aria-selected={item.id === localId} key={item.id} onClick={() => { setLocalId(item.id); setExternalId(''); setExternalSearch(''); setPanelPickerOpen(false); setNotice('') }}>{item.path}</button>) : <span>Aramaya uygun panel kategorisi bulunamadı.</span>}</div></div>}<details className="mapping-local-manager" onClick={event => { if (event.target === event.currentTarget) event.currentTarget.removeAttribute('open') }}><summary><UiIcon name="settings" /><span><strong>Lokal Kategorileri Yönet</strong></span><UiIcon name="chevronDown" /></summary><div className="mapping-local-manager-dialog"><header><div><strong>1. Yeni Kategori Ekle</strong></div><button type="button" aria-label="Kategori yönetim penceresini kapat" onClick={event => { event.preventDefault(); event.stopPropagation(); event.currentTarget.closest('details')?.removeAttribute('open') }}><UiIcon name="close" /></button></header><form className="platform-category-create" onSubmit={event => { event.preventDefault(); createCategory.mutate() }}><label>Yeni Panel Kategorisi<input aria-label="Yeni panel kategorisi adı" maxLength={160} value={categoryName} onChange={event => setCategoryName(event.target.value)} placeholder="Örn. Anne Bluz" required /></label><button disabled={createCategory.isPending}>{createCategory.isPending ? 'Ekleniyor…' : 'Kategori Ekle'}</button></form><div className="category-library-tools"><div className={categoryLibraryOpen ? 'category-library-search-control is-open' : 'category-library-search-control'}><input aria-label="Eklenen panel kategorilerinde ara" value={categoryLibrarySearch} onFocus={() => setCategoryLibraryOpen(true)} onClick={() => setCategoryLibraryOpen(true)} onChange={event => setCategoryLibrarySearch(event.target.value)} placeholder="Kategori ara…" /><button type="button" className="category-library-search-toggle" aria-label={categoryLibraryOpen ? 'Kategorileri kapat' : 'Kategorileri aç'} aria-expanded={categoryLibraryOpen} onMouseDown={event => event.preventDefault()} onClick={() => setCategoryLibraryOpen(value => !value)}><span aria-hidden="true" /></button></div><PanelSelect className="category-library-sort" ariaLabel="Panel kategorilerini sırala" width={96} value={categoryLibrarySort} options={[{ value: 'NAME_ASC', label: 'A–Z' }, { value: 'NAME_DESC', label: 'Z–A' }]} onChange={setCategoryLibrarySort} /><span>{filteredCategoryLibrary.length.toLocaleString('tr-TR')} sonuç</span></div><div className="category-chip-list">{filteredCategoryLibrary.slice(0, 100).map(category => <span key={category.id} className={category.id === localId ? 'active' : ''}><button type="button" onClick={() => { setLocalId(category.id); setExternalId('') }}>{category.name}</button><button type="button" className="category-chip-remove" aria-label="Kategoriyi kaldır" onClick={() => archiveCategory.mutate(category)} disabled={archiveCategory.isPending}><UiIcon name="close" /></button></span>)}</div></div></details></section><section className="mapping-reference-target"><label><span className="mapping-label-line">{selectedPlatform.label} {productTypeCategories ? "Ürün Türü" : "Kategorisi"}<TitleHint label={`${selectedPlatform.label} ${productTypeCategories ? "Ürün Türü" : "Kategorisi"}`}>{categoryReferenceInfo}</TitleHint></span></label><div className="mapping-target-controls"><div className="platform-category-picker-wrap"><button type="button" className="panel-category-picker-trigger" aria-expanded={externalPickerOpen} onClick={() => setExternalPickerOpen(value => !value)} disabled={!connectionId || !localId || references.isError || references.isLoading}><span title={selectedExternal ? cleanMarketplaceCategoryPath(selectedExternal.path) : undefined}>{selectedExternal ? cleanMarketplaceCategoryPath(selectedExternal.path) : productTypeCategories && externalId ? `${externalId} (yeni ürün türü)` : localId && (mapping.isLoading || references.isLoading) ? 'Yükleniyor…' : `${selectedPlatform.label} ${productTypeCategories ? 'Ürün Türü' : 'Kategori'} Seçin...`}</span><UiIcon name="chevronDown" /></button>{externalPickerOpen && <div className="panel-category-picker platform-category-picker" role="dialog" aria-label={`${selectedPlatform.label} ${externalNoun} seçimi`}><div><input ref={focusOnMount} aria-label={`${selectedPlatform.label} ${externalNounPlural} ara`} value={externalSearch} onChange={event => setExternalSearch(event.target.value)} placeholder={references.isLoading ? 'Kategoriler yükleniyor...' : `${selectedPlatform.label} ${externalNounPlural} ara...`} /></div><div role="listbox">{newProductTypeOption && <button type="button" role="option" aria-selected={newProductTypeOption === externalId} className="platform-category-new-type" onClick={() => { setExternalId(newProductTypeOption); setExternalPickerOpen(false) }} title="Mağazada bu adla tür yok; ilk yayında Shopify'da bu türle açılır."><span>“{newProductTypeOption}” yeni ürün türü olarak kullan</span></button>}{visibleExternal.length ? visibleExternal.map(item => { const path = cleanMarketplaceCategoryPath(item.path); const ambiguous = path === item.name && externalLeaves.filter(category => cleanMarketplaceCategoryPath(category.path) === path).length > 1; return <button type="button" role="option" aria-selected={item.externalId === externalId} key={item.externalId} onClick={() => { setExternalId(item.externalId); setExternalPickerOpen(false) }} title={path}><span>{path}</span>{ambiguous && <small>Kategori no: {item.externalId}</small>}</button> }) : newProductTypeOption ? null : <span>{references.isError ? 'Kategori listesi alınamadı. Salt-okunur eşitlemeyi yeniden deneyin.' : productTypeCategories ? externalLeaves.length ? 'Aramayla eşleşen ürün türü yok; yeni türün adını yazın.' : 'Mağazada henüz ürün türü yok; yeni türün adını yazın.' : 'Aramayla eşleşen kategori bulunamadı.'}</span>}</div></div>}</div></div></section></div>{categorySuggestions.length > 0 && <div className="category-suggestions" aria-label="Önerilen kategoriler"><span>Önerilen {selectedPlatform.label} kategorileri</span>{categorySuggestions.map(item => <button type="button" key={item.externalId} aria-pressed={item.externalId === externalId} className={item.externalId === externalId ? 'active' : ''} title={`Benzerlik ${item.score}/100`} onClick={() => { setExternalId(item.externalId); setExternalPickerOpen(false) }}>{cleanMarketplaceCategoryPath(item.path)}</button>)}</div>}<footer><button type="button" disabled={!localId || !externalId || save.isPending || mapping.isLoading} onClick={() => save.mutate()}><UiIcon name="externalLink" />{save.isPending ? 'Kaydediliyor…' : mapping.data ? 'Eşleştirmeyi Güncelle' : 'Eşleştirmeyi Kaydet'}</button></footer></article>
     <article className="mapping-reference-card mapping-option-summary panel-library-section"><div className="mapping-section-heading"><div><h2><UiIcon name="grid" />3. Ürün Seçenekleri</h2></div></div><PanelAttributeLibraryBuilder role="OPTION" attributes={localAttributes.data?.items.filter(item => item.isActive) ?? []} onNotice={setNotice} /></article>
 <article id="category-value-mapping" className="mapping-reference-card mapping-attribute-summary panel-library-section"><div className="mapping-section-heading"><div><h2><UiIcon name="slidersHorizontal" />4. Kategori Özellikleri ve Değerleri</h2></div></div><PanelAttributeLibraryBuilder role="ATTRIBUTE" attributes={localAttributes.data?.items.filter(item => item.isActive) ?? []} onNotice={setNotice} /></article>
    <article className="mapping-reference-card mapping-saved-card">
      <header>
        <h2><UiIcon name="equal" />5. Kayıtlı Kategori Eşleştirmeleri</h2>
        <div className="mapping-saved-header-tools">
          <div className="mapping-saved-tabs mapping-platform-tabs rv-tabs rv-view-tabs" role="tablist" aria-label="Kayıtlı eşleştirme platformları">
            {mappingPlatformDefinitions.filter(platform => hasCategoryAttributes(platform.code) || platform.code === selectedPlatformCode || activeMappingConnections.some(item => item.platformCode.toUpperCase() === platform.code)).map(platform => <MappingPlatformTab key={platform.code} code={platform.code} label={platform.label} available={activeMappingConnections.some(item => item.platformCode.toUpperCase() === platform.code)} selected={selectedPlatformCode === platform.code} activeTitle={`${platform.label} eşleştirmelerini göster`} onSelect={() => { setSelectedPlatformCode(platform.code); setSavedPage(1); setLocalId(''); setExternalId(''); setExternalSearch(''); setNotice('') }} />)}
          </div>
          <label className="rv-search-field mapping-saved-search"><UiIcon name="search" size={16} /><input type="search" aria-label="Kayıtlı eşleştirmelerde ara" value={savedSearch} onChange={event => { setSavedSearch(event.target.value); setSavedPage(1) }} placeholder="Eşleştirme ara..." /></label>
          <button type="button" className="mapping-icon-button mapping-sort-button" aria-label={savedSort === 'NAME_ASC' ? 'Kayıtlı eşleştirmeleri Z-A sırala' : 'Kayıtlı eşleştirmeleri A-Z sırala'} title={savedSort === 'NAME_ASC' ? 'Z-A sırala' : 'A-Z sırala'} onClick={() => { setSavedSort(current => current === 'NAME_ASC' ? 'NAME_DESC' : 'NAME_ASC'); setSavedPage(1) }}>{savedSort === 'NAME_ASC' ? 'A–Z' : 'Z–A'}</button>
        </div>
      </header>
      <div className="mapping-saved-table"><table><thead><tr><th>Panel Kategorisi</th><th>Platform</th><th>Platform Kategorisi</th>{showSavedStatus && <th>Durum</th>}<th>İşlemler</th></tr></thead><tbody>{sortedSavedRows.length ? visibleSavedRows.map(({ item, local, external }) => { const status = categoryMappingStatus(item); return <tr key={item.id}><td><SavedCategoryCell label={savedCategoryLabel(local?.path, item.localId, localCategories.isLoading)} /></td><td><span className="ms-platform"><img className={platformLogoClass(selectedPlatform.code)} src={platformLogoSource(selectedPlatform.code) ?? '/platforms/trendyol.webp'} alt="" aria-hidden="true" />{selectedPlatform.label}</span></td><td><SavedCategoryCell label={savedCategoryLabel(external ? cleanMarketplaceCategoryPath(external.path) : null, item.externalId, references.isLoading)} /></td>{showSavedStatus && <td><span className={`mapping-table-status ${status.className}`} title={status.title}><MappingStateGlyph state={status.className === 'active' ? 'done' : status.className === 'pending' ? 'none' : 'partial'} />{status.label}</span></td>}<td><div className="mapping-row-actions">{hasCategoryAttributes(selectedPlatformCode) && <button type="button" className="mapping-feature-button" aria-label="Kategori özelliklerini düzenle" onClick={() => { setLocalId(item.localId); setExternalId(item.externalId); setAdvancedOpen(true) }}>Kategori Özellikleri</button>}<button type="button" className="mapping-icon-button mapping-delete-button" aria-label="Eşleştirmeyi sil" disabled={removeMapping.isPending} onClick={() => void confirm({ title: 'Kategori Eşleştirmesi Kaldırılsın mı?', message: 'Bu kategori eşleştirmesi kaldırılacak.', confirmLabel: 'Kaldır', destructive: true }).then(accepted => { if (accepted) removeMapping.mutate(item) })}><MappingDeleteIcon /></button></div></td></tr> }) : <tr><td className="mapping-empty-table-cell" colSpan={showSavedStatus ? 5 : 4}>{categoryMappings.isLoading ? `${selectedPlatform.label} eşleştirmeleri yükleniyor…` : !connectionId ? `${selectedPlatform.label} için aktif bağlantı bulunamadı.` : <EmptyState icon="mappings" title="Henüz Eşleştirme Yok">Kayıtlı kategori eşleştirmesi bulunamadı.</EmptyState>}</td></tr>}</tbody></table></div>
      <footer><span>Toplam {sortedSavedRows.length.toLocaleString('tr-TR')} eşleştirme</span><Pagination className="pagination-controls" page={currentSavedPage} totalPages={savedPageCount} hasNext={currentSavedPage < savedPageCount} onPageChange={setSavedPage} onPrevious={() => setSavedPage(value => Math.max(1, value - 1))} onNext={() => setSavedPage(value => Math.min(savedPageCount, value + 1))} disabled={categoryMappings.isFetching} ariaLabel="Kayıtlı kategori eşleştirmeleri sayfaları" /></footer>
    </article>
{exportOpen && <div className="workspace-modal-backdrop mapping-transfer-backdrop" role="presentation" onMouseDown={() => { if (!transferBusy) setExportOpen(false) }}><DialogSurface as="section" onClose={() => { if (!transferBusy) setExportOpen(false) }} className="workspace-modal mapping-transfer-modal mapping-export-modal" aria-labelledby="mapping-export-title" onMouseDown={event => event.stopPropagation()}><header><div><h2 id="mapping-export-title">Dışa Aktarılacak Alanları Seçin<TitleHint label="Dışa Aktarılacak Alanları Seçin">Yedeğe dahil edilecek alanları seçin. Seçimleriniz tek bir JSON dosyasında toplanır.</TitleHint></h2></div><button type="button" className="modal-close" onClick={() => setExportOpen(false)} disabled={transferBusy} aria-label="Dışa aktarma penceresini kapat"><UiIcon name="close" /></button></header><div className="mapping-transfer-options mapping-export-options"><label className="mapping-transfer-all-option"><input type="checkbox" checked={Object.values(exportSelection).every(Boolean)} onChange={event => setExportSelection({ categories: event.target.checked, options: event.target.checked, attributes: event.target.checked, mappings: event.target.checked })} /><span><strong>Tümü</strong><small>Yukarıdaki tüm alanları tek seferde seçer.</small></span><em>{Object.values(exportSelection).filter(Boolean).length}/4 seçili</em></label><div className="mapping-export-scope-grid"><label><input type="checkbox" checked={exportSelection.categories} onChange={event => setExportSelection(current => ({ ...current, categories: event.target.checked }))} /><span><strong>Panel Kategorileri</strong><small>Aktif kategori kayıtlarını aktarır.</small></span></label><label><input type="checkbox" checked={exportSelection.options} onChange={event => setExportSelection(current => ({ ...current, options: event.target.checked }))} /><span><strong>Ürün Seçenekleri</strong><small>Renk ve beden değerlerini aktarır.</small></span></label><label><input type="checkbox" checked={exportSelection.attributes} onChange={event => setExportSelection(current => ({ ...current, attributes: event.target.checked }))} /><span><strong>Kategori Özellikleri</strong><small>Özellik ve kayıtlı değerlerini aktarır.</small></span></label><label><input type="checkbox" checked={exportSelection.mappings} onChange={event => setExportSelection(current => ({ ...current, mappings: event.target.checked }))} /><span><strong>Kategori Eşleştirmeleri</strong><small>Kategori, özellik ve değer eşleşmelerini aktarır.</small></span></label></div></div><footer><span className="mapping-export-footer-hint">{Object.values(exportSelection).filter(Boolean).length ? 'Seçilen alanlar dışa aktarılmaya hazır.' : 'En az bir alan seçin.'}</span><button type="button" className="secondary" onClick={() => setExportOpen(false)} disabled={transferBusy}>Vazgeç</button><button type="button" onClick={() => void exportSelectedMappings()} disabled={transferBusy || !Object.values(exportSelection).some(Boolean)}>{transferBusy ? 'Hazırlanıyor…' : 'Seçilenleri Dışa Aktar'}</button></footer></DialogSurface></div>}
{transferOpen && transferBundle && <RrModal title="İçe Aktarma Alanlarını Seçin" hint={<>Mevcut kayıtlar silinmez; aynı kod/ad ve değerler güncellenir, eksikler eklenir. Eşleşmeler seçili aktif {selectedPlatform.label} bağlantısının güncel verisiyle doğrulanır.</>} desc={`${transferBundle.categories.length} kategori · ${transferBundle.attributes.length} özellik · ${transferBundle.categoryMappings.length + transferBundle.attributeMappings.length + transferBundle.attributeValueMappings.length} eşleşme`} size="md" flush locked={transferBusy} modalClass="mapping-transfer-import" onClose={() => { setTransferOpen(false); setTransferBundle(null) }}
  foot={<><span className="rr-note" /><button type="button" className="rr-btn" onClick={() => { setTransferOpen(false); setTransferBundle(null) }} disabled={transferBusy}>Vazgeç</button><button type="button" className="rr-btn rr-primary" onClick={() => void importMappings()} disabled={transferBusy}>{transferBusy ? 'İçe Aktarılıyor…' : 'Seçilenleri Güncelle'}</button></>}><div className="mapping-transfer-options">{([['categories', 'Kategoriler', 'Panel kategorilerini eksik olanları ekleyerek güncelle'], ['options', 'Ürün Seçenekleri', 'option- ile başlayan seçenek başlıklarını güncelle'], ['attributes', 'Ürün Özellikleri', 'Ürün özelliklerini ve değerlerini güncelle'], ['mappings', 'Eşleşmeler', 'Kategori, özellik ve değer eşleşmelerini uygula']] as Array<[MappingTransferScope, string, string]>).map(([scope, label, description]) => <label key={scope}><input type="checkbox" checked={transferSelection[scope]} onChange={event => setTransferSelection(current => ({ ...current, [scope]: event.target.checked }))} /><span><strong>{label}</strong><small>{description}</small></span></label>)}</div></RrModal>}
      {advancedOpen && <CategoryAttributeWorkspaceModal onClose={() => setAdvancedOpen(false)}><CategoryAttributeWorkspace platformCode={selectedPlatformCode} connectionId={connectionId} localCategoryId={localId} localCategories={localLeaves} localAttributes={localAttributes.data?.items.filter(item => item.isActive) ?? []} onNotice={setNotice} /></CategoryAttributeWorkspaceModal>}
  </section>
}

// Shopify maps product types only and PttAVM has no attribute service: neither has category fields to map.
function hasCategoryAttributes(platformCode: string) {
  return platformCode !== 'SHOPIFY' && platformCode !== 'PTTAVM'
}
