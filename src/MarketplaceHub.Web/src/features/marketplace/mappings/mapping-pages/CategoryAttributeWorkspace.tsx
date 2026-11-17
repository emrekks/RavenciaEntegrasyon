import type { ApiAnswer } from '../../../../shared/api/api-answer'
import { CollapsibleLibrary } from '../CollapsibleLibrary'
import { Fragment, useCallback, useEffect, useEffectEvent, useMemo, useRef, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiRequestError, hubApi, loadAllPages } from '../../../../shared/api'
import { Busy, ErrorBox, Modal, TitleHint, UiIcon, useConfirmDialog, DialogSurface } from '../../../../shared/components'
import { RrModal } from '../../../../shared/ui/rr-components'
import { mappingSaveErrorMessage, refreshMappingConflicts } from '../mapping-refresh'
import { mappedLocalIds, valueListQuery, valueMappingProgressQuery, valueMappingsQuery } from '../value-field-queries'
import { draftsKeptAfterSave, groupSavedSelections, rebaseSelections, runInBatches, selectionsDiffer, valueDraftOperations, type ValueDraft, type ValueDraftOperation } from '../value-drafts'
import { newIdempotencyKey } from '../../../../shared/idempotency'
import { dative } from '../../../../shared/job-outcome'
import { MessageToast } from '../../../../shared/components/NoticeToast'
import { useScrollLock } from '../../../../shared/hooks/useScrollLock'
import { sortByTurkishName } from '../../attribute-sorting'
import { usesCustomPanelColorValue } from '../../color-value-format'
import { AttributeDefaultsPanel, type AttributeDefault } from '../../AttributeDefaultsPanel'
import { offersProductFields, productFieldLabel } from '../../product-field-sources'
import { MappingConflictsPanel } from '../../MappingConflictsPanel'
import { canonicalKey } from '../../../../shared/value-matching'
import { attributeValueMappingNeedsSave, planDirectReferenceValues } from '../../attribute-value-mapping'
import { waitForReferenceSyncJobs } from '../../reference-value-sync'
import { mappingPlatformLabel, type MappingPlatformCode } from '../../mapping-platforms'
import { focusOnMount } from '../../../../shared/focus'
import { FieldState, MappingStateGlyph, Page } from '../../shared'
import { type SavedCategoryLabel } from '../saved-category-label'
import { nextMissingEntryKey, type MappingFocusItem } from '../mapping-focus'
import { PanelSelect } from '../../../../shared/PanelSelect'
import { EmptyState } from '../../../../shared/components/EmptyState'
import { queryKeys } from '../../../../shared/api/query-keys'
import { type LocalCategory, type LocalAttribute, type ReferenceItem, type ReferenceData, type CatalogMapping, wait, normalizedAttributeKey, isWebColorAttributeName, isPanelColorAttributeName, isVariantOptionAttributeName, isOptionAttribute, cleanMarketplaceCategoryPath, categoryMappingStatus, type ValueDraftStore, ValueDraftContext } from './mapping-shared'
import { ProductBrandField, FieldHeading, FieldFooter, FieldDetail } from './FieldDetail'
import { OptionMappingCard, WebColorMappingCard } from './ValueMappingEditors'

type CategoryRequirementView = { attributeId: string; isRequired: boolean; allowsCustomValue: boolean; displayOrder: number; role: 'ATTRIBUTE' | 'OPTION'; attribute: LocalAttribute }
type AttributeRequirementCommand = { attributeId: string; isRequired: boolean; allowsCustomValue: boolean; displayOrder: number; role: 'ATTRIBUTE' | 'OPTION' }

function displayAttributeName(value: string) { return value.replace(/\[(?:A-)?TDG\][\s_-]*/gi, '').replace(/^\(?(?:A-)?TDG\)?[\s_-]*/gi, '').replace(/_/g, ' ').replace(/\s+/g, ' ').trim() }

function isSizeAttributeName(value: string) {
  const key = normalizedAttributeKey(value).replace(/[^0-9A-Z]/g, '')
  return key === 'BEDEN' || key === 'SIZE' || key.endsWith('BEDEN') || key.endsWith('SIZE')
}
function isSizeAttribute(attribute: Pick<LocalAttribute, 'name' | 'code'>) { return isSizeAttributeName(attribute.name) || isSizeAttributeName(attribute.code) }

// The "Kategori Alanları ve Değerleri" window: the Eşleştirmeler page opens it from its category list, a product's publish
// card from a gap of its badge.
export function CategoryAttributeWorkspaceModal({ onClose, description = 'Pazaryeri alanlarını panel özellikleriyle eşleyin ve değer eşlemelerini tamamlayın.', children }: { onClose: () => void; description?: string; children: ReactNode }) {
  return <div className="workspace-modal-backdrop mapping-advanced-backdrop" role="presentation" onMouseDown={onClose}><DialogSurface as="section" onClose={onClose} className="workspace-modal mapping-advanced-modal" aria-labelledby="mapping-advanced-title" onMouseDown={event => event.stopPropagation()}><header><div><p className="eyebrow">Kategori Çalışma Alanı</p><h2 id="mapping-advanced-title">Kategori Alanları ve Değerleri{description && <TitleHint label="Kategori Alanları ve Değerleri">{description}</TitleHint>}</h2></div><button type="button" className="modal-close" onClick={onClose} aria-label="Kategori çalışma alanını kapat"><UiIcon name="close" /></button></header><div className="mapping-advanced-modal-body"><section className="mapping-popup-content">{children}</section></div></DialogSurface></div>
}
// The same window opened from a product's publish card: the product's category on one store, the field of the pressed gap
// chosen, the product's gaps marked (`focus`). It sits in the page's main area (the window's styles are scoped there).
// `onClose` runs when it closes; the card then reads its publish check again.
export function ProductCategoryFieldsDialog({ platformCode, connectionId, localCategoryId, focus, onClose }: { platformCode: MappingPlatformCode; connectionId: string; localCategoryId: string; focus: { items: MappingFocusItem[]; initialExternalAttributeId?: string; initialFieldName?: string; missing?: { title: string; detail: string } }; onClose: () => void }) {
  const [notice, setNotice] = useState('')
  useScrollLock(true, true)
  const localCategories = useQuery({ queryKey: queryKeys.categories('mapping'), queryFn: ({ signal }) => loadAllPages<LocalCategory>('/catalog/categories', { signal }) })
  const localAttributes = useQuery({ queryKey: queryKeys.attributes('mapping-builder'), queryFn: ({ signal }) => loadAllPages<LocalAttribute>('/catalog/attributes', { signal }) })
  const leaves = localCategories.data?.items.filter(item => item.isActive && item.isLeaf) ?? []
  const host = typeof document === 'undefined' ? null : document.querySelector('.app-shell main') ?? document.body
  if (!host) return null
  return createPortal(<>
    <CategoryAttributeWorkspaceModal onClose={onClose} description="">
      {localCategories.isLoading || localAttributes.isLoading ? <Busy text="Kategori ve özellikler yükleniyor…" />
        : localCategories.isError ? <ErrorBox error={localCategories.error} onRetry={() => void localCategories.refetch()} />
          : <CategoryAttributeWorkspace platformCode={platformCode} connectionId={connectionId} localCategoryId={localCategoryId} localCategories={leaves} localAttributes={localAttributes.data?.items.filter(item => item.isActive) ?? []} onNotice={setNotice} focus={focus} />}
    </CategoryAttributeWorkspaceModal>
    {notice && <MessageToast message={notice} onClose={() => setNotice('')} />}
  </>, host)
}
function slug(value: string) { return value.toLocaleLowerCase('tr-TR').replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '') || `attr-${Date.now()}` }
function attributeCodeForRole(title: string, role: 'ATTRIBUTE' | 'OPTION') { return `${role === 'OPTION' ? 'option-' : 'attribute-'}${slug(title)}` }

// A saved mapping's category cell: a quiet placeholder while the names load, a marked fallback when one is missing.
export function SavedCategoryCell({ label }: { label: SavedCategoryLabel }) {
  if (label.state === 'loading') return <span className="mapping-saved-loading" aria-busy="true">Yükleniyor…</span>
  if (label.state === 'fallback') return <span className="mapping-saved-fallback" title="Kategori adı bulunamadı; kayıtlı numarası gösteriliyor.">{label.text}</span>
  return <>{label.text}</>
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
    void Promise.all(categoriesNow.map(async category => [category.id, await hubApi<ApiAnswer<'getCatalogCategoriesByIdAttributeRequirements'>>(`/catalog/categories/${category.id}/attribute-requirements`)] as const))
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
        await client.invalidateQueries({ queryKey: queryKeys.categoryRequirements(category.id) })
        client.setQueryData<Page<LocalCategory>>(queryKeys.categories('mapping-builder-assignment'), currentCategories => currentCategories ? { ...currentCategories, items: currentCategories.items.map(item => item.id === category.id ? { ...item, version: item.version + 1 } : item) } : currentCategories)
        client.setQueryData<Page<LocalCategory>>(queryKeys.categories('mapping'), currentCategories => currentCategories ? { ...currentCategories, items: currentCategories.items.map(item => item.id === category.id ? { ...item, version: item.version + 1 } : item) } : currentCategories)
      }
      onNotice(changedCategories.length ? `${attribute.name} için ${selectedCategoryIds.length} kategori kapsamı kaydedildi.` : 'Kategori kapsamındaki değişiklik bulunmuyor.')
      onClose()
    } catch (reason) {
      const message = reason instanceof Error ? reason.message : 'Kategori kapsamı kaydedilemedi.'
      setError(message)
      onNotice(message)
    } finally { setSaving(false) }
  }

  return <DialogSurface as="div" onClose={onClose} className="panel-attribute-modal panel-attribute-category-modal" aria-labelledby="attribute-category-assignment-title"><div className="panel-attribute-modal-card panel-attribute-category-modal-card"><header><div><strong id="attribute-category-assignment-title">{attribute.name} Kategorileri<TitleHint label={`${attribute.name} Kategorileri`}>{role === 'OPTION' ? 'Bu seçeneğin' : 'Bu özelliğin'} görüneceği kategorileri seçin.</TitleHint></strong><small>{role === 'OPTION' ? 'Ürün seçeneği' : 'Ürün özelliği'}</small></div><button type="button" aria-label="Kategori kapsamı penceresini kapat" onClick={onClose} disabled={saving}><UiIcon name="close" /></button></header><div className="panel-attribute-category-search"><input data-autofocus aria-label="Kategorilerde ara" value={search} onChange={event => setSearch(event.target.value)} placeholder="Kategori yolu veya adı ara..." /><span>{selectedCategoryIds.length} seçili</span></div>{loading ? <div className="panel-attribute-category-list panel-attribute-category-list-loading" role="status" aria-live="polite" aria-busy="true" aria-label="Kategori kapsamları yükleniyor">{Array.from({ length: Math.max(12, activeCategories.length) }, (_, index) => <span className="panel-attribute-category-skeleton" key={index} aria-hidden="true" />)}</div> : error && !Object.keys(requirementsByCategory).length ? <div className="mapping-empty-row attribute-feedback is-error">{error}</div> : <div className="panel-attribute-category-list" role="listbox" aria-label="Özelliğin görüneceği kategoriler">{visibleCategories.length ? visibleCategories.map(category => <button type="button" role="option" aria-selected={isSelected(category.id)} className={isSelected(category.id) ? 'active' : ''} key={category.id} onClick={() => toggleCategory(category.id)}><span>{category.path}</span><i aria-hidden="true">{isSelected(category.id) ? <UiIcon name="check" /> : null}</i></button>) : <div className="mapping-empty-row">Aramaya uygun kategori bulunamadı.</div>}</div>}<footer><button type="button" className="secondary" onClick={onClose} disabled={saving}>Vazgeç</button><button type="button" onClick={() => void save()} disabled={loading || saving || !!error && !Object.keys(requirementsByCategory).length}>{saving ? 'Kaydediliyor…' : 'Kapsamı Kaydet'}</button></footer></div></DialogSurface>
}
export function PanelAttributeLibraryBuilder({ role, attributes, onNotice }: { role: 'ATTRIBUTE' | 'OPTION'; attributes: LocalAttribute[]; onNotice: (value: string) => void }) {
  const client = useQueryClient()
  const [title, setTitle] = useState(''); const [values, setValues] = useState<string[]>([]); const [valueDraft, setValueDraft] = useState(''); const [editingId, setEditingId] = useState(''); const [newValues, setNewValues] = useState(''); const [editingValue, setEditingValue] = useState<{ id: string; value: string } | null>(null); const [renamingTitle, setRenamingTitle] = useState<string | null>(null); const [categoryAssignmentId, setCategoryAssignmentId] = useState(''); const [feedback, setFeedback] = useState(''); const [feedbackTone, setFeedbackTone] = useState<'success' | 'error'>('success'); const [sortDirection, setSortDirection] = useState<1 | -1>(1)
  const [librarySearch, setLibrarySearch] = useState(''); const [addedSignal, setAddedSignal] = useState(0)
  const isOption = role === 'OPTION'
  const [confirm, confirmDialog] = useConfirmDialog()
  const matchingRecords = attributes.filter(attribute => !isWebColorAttributeName(attribute.name) && (isOption ? isOptionAttribute(attribute) : !isOptionAttribute(attribute)))
  const records = isOption ? matchingRecords : sortByTurkishName(matchingRecords)
  const visibleRecords = librarySearch.trim() ? records.filter(attribute => attribute.name.toLocaleLowerCase('tr-TR').includes(librarySearch.trim().toLocaleLowerCase('tr-TR'))) : records
  const editingAttribute = records.find(attribute => attribute.id === editingId) ?? null
  const categoryAssignmentAttribute = records.find(attribute => attribute.id === categoryAssignmentId) ?? null
  const categories = useQuery({ queryKey: queryKeys.categories('mapping-builder-assignment'), queryFn: ({ signal }) => loadAllPages<LocalCategory>('/catalog/categories', { signal }), enabled: !!categoryAssignmentAttribute })
  function showFeedback(message: string, tone: 'success' | 'error' = 'success') { setFeedback(message); setFeedbackTone(tone) }
  useScrollLock(Boolean(editingAttribute), true)
  useEffect(() => { setSortDirection(1) }, [editingId])
  function updateCache(attribute: LocalAttribute) {
    client.setQueryData<Page<LocalAttribute>>(queryKeys.attributes('mapping-builder'), current => {
      if (!current) return current
      const found = current.items.some(item => item.id === attribute.id)
      return { ...current, items: found ? current.items.map(item => item.id === attribute.id ? attribute : item) : [...current.items, attribute] }
    })
  }
  async function create() {
    try {
      if (!title.trim()) return showFeedback(isOption ? 'Seçenek başlığı girin.' : 'Özellik başlığı girin.', 'error')
      const parsedValues = values
      const attribute = await hubApi<ApiAnswer<'postCatalogAttributes'>>('/catalog/attributes', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ code: attributeCodeForRole(title, role), name: title.trim(), dataType: parsedValues.length ? 'SINGLE_SELECT' : 'TEXT', selectionMode: parsedValues.length ? 'SINGLE' : null, unit: null, values: parsedValues.map((value, index) => ({ value, sortOrder: index })) }) })
      updateCache(attribute); setAddedSignal(value => value + 1); setLibrarySearch(''); setEditingId(attribute.id); setTitle(''); setValues([]); setValueDraft(''); showFeedback(`${attribute.name} panele kaydedildi.`); onNotice(`${attribute.name} panele kaydedildi.`)
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
      const attribute = await hubApi<ApiAnswer<'postCatalogAttributesByIdValues'>>(`/catalog/attributes/${attributeId}/values`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify(parsedValues.map((value, index) => ({ value, sortOrder: index }))) })
      updateCache(attribute); setNewValues(''); showFeedback('Yeni değerler panele kaydedildi.'); onNotice('Yeni değerler panele kaydedildi.')
    } catch (reason) { showFeedback(reason instanceof Error ? reason.message : 'Değerler kaydedilemedi.', 'error') }
  }
  // The attribute's shown name; its code and every mapping that points at it stay. Options (Renk, Beden) are matched
  // to variant options by name, so only attributes get the pencil.
  async function renameAttribute(attribute: LocalAttribute, nextName: string) {
    const name = nextName.trim()
    if (!name || name === attribute.name) { setRenamingTitle(null); return }
    try {
      const saved = await hubApi<ApiAnswer<'patchCatalogAttributesById'>>(`/catalog/attributes/${attribute.id}`, { method: 'PATCH', headers: { 'If-Match': `"v${attribute.version}"` }, body: JSON.stringify({ name }) })
      updateCache(saved); setRenamingTitle(null); showFeedback(`Başlık “${saved.name}” olarak değiştirildi.`)
    } catch (reason) { showFeedback(reason instanceof Error ? reason.message : 'Başlık değiştirilemedi.', 'error') }
  }
  async function removeAttribute(attribute: LocalAttribute) {
    if (!(await confirm({ title: 'Başlık Kaldırılsın mı?', message: `“${attribute.name}” ve bağlı değerleri kaldırılacak.`, confirmLabel: 'Kaldır', destructive: true }))) return
    try {
      await hubApi<ApiAnswer<'deleteCatalogAttributesById'>>(`/catalog/attributes/${attribute.id}`, { method: 'DELETE', headers: { 'If-Match': `"v${attribute.version}"` } })
      client.setQueryData<Page<LocalAttribute>>(queryKeys.attributes('mapping-builder'), current => current ? { ...current, items: current.items.filter(item => item.id !== attribute.id) } : current)
      setEditingId(''); showFeedback(`${attribute.name} kaldırıldı.`)
    } catch (reason) { showFeedback(reason instanceof Error ? reason.message : 'Başlık kaldırılamadı.', 'error') }
  }
  async function removeValue(attributeId: string, valueId: string) {
    try { const attribute = await hubApi<ApiAnswer<'deleteCatalogAttributesByIdValuesByValueId'>>(`/catalog/attributes/${attributeId}/values/${valueId}`, { method: 'DELETE' }); updateCache(attribute); setEditingValue(null); showFeedback('Değer kaldırıldı.') } catch (reason) { showFeedback(reason instanceof Error ? reason.message : 'Değer kaldırılamadı.', 'error') }
  }
  async function updateValue(attributeId: string, originalId: string, nextValue: string, sortOrder: number) {
    const value = nextValue.trim()
    if (!value) return showFeedback('Değer boş olamaz.', 'error')
    try {
      const attribute = await hubApi<ApiAnswer<'putCatalogAttributesByIdValuesByValueId'>>(`/catalog/attributes/${attributeId}/values/${originalId}`, { method: 'PUT', headers: { 'If-Match': `"v${editingAttribute?.version ?? 0}"` }, body: JSON.stringify({ value, sortOrder }) })
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
      const updated = await hubApi<ApiAnswer<'putCatalogAttributesByIdValuesOrder'>>(`/catalog/attributes/${attribute.id}/values/order`, { method: 'PUT', headers: { 'If-Match': `"v${attribute.version}"` }, body: JSON.stringify({ valueIds: next.map(value => value.id) }) })
      updateCache(updated)
      return true
    } catch (reason) { showFeedback(reason instanceof Error ? reason.message : 'Değerler sıralanamadı.', 'error'); return false }
  }
  async function toggleSortValues(attribute: LocalAttribute) {
    const direction = sortDirection
    if (await sortValuesAlphabetically(attribute, direction)) setSortDirection(direction === 1 ? -1 : 1)
  }
  const editingValues = editingAttribute?.values.filter(value => value.isActive).slice().sort((left, right) => left.sortOrder - right.sortOrder || left.value.localeCompare(right.value, 'tr')) ?? []
  return <div className="panel-attribute-library">{confirmDialog}<div className="panel-attribute-create"><label>{isOption ? 'Yeni Seçenek Başlığı' : 'Yeni Özellik Başlığı'}<input value={title} onChange={event => setTitle(event.target.value)} placeholder={isOption ? 'Örn. Beden, Renk' : 'Örn. Materyal, Kol Boyu'} /></label><label>İlk Değerler <TokenValueInput values={values} draft={valueDraft} onDraftChange={setValueDraft} onAdd={addInitialValues} onRemove={value => setValues(current => current.filter(item => item !== value))} placeholder="Değer yazın, Enter veya Ekle" /></label><button type="button" onClick={() => void create()}><UiIcon name="save" />Panele Kaydet</button></div>{feedback && <p className={`attribute-feedback ${feedbackTone === 'success' ? 'is-success' : 'is-error'}`} role="status">{feedback}</p>}<CollapsibleLibrary title={isOption ? 'Seçenek Listesi' : 'Özellik Listesi'} count={records.length} forceOpen={!!librarySearch.trim()} openSignal={addedSignal} storageKey={isOption ? 'rv-option-library-open' : 'rv-attribute-library-open'}><div className="library-search-row"><input aria-label={isOption ? 'Seçeneklerde ara' : 'Özelliklerde ara'} value={librarySearch} onChange={event => setLibrarySearch(event.target.value)} placeholder={isOption ? 'Seçenek ara…' : 'Özellik ara…'} /><span>{visibleRecords.length.toLocaleString('tr-TR')} sonuç</span></div><div className="panel-attribute-records">{visibleRecords.length ? visibleRecords.map(attribute => <article className={attribute.id === editingId || attribute.id === categoryAssignmentId ? 'selected' : ''} key={attribute.id}><button type="button" className="panel-attribute-record-main" aria-label={`${attribute.name}: değerler, kategoriler ve silme`} onClick={() => setEditingId(attribute.id)}><strong>{attribute.name}</strong><small>{attribute.values.filter(value => value.isActive).length ? `${attribute.values.filter(value => value.isActive).length} değer kaydedildi` : 'Serbest değer'}</small></button></article>) : <div className="mapping-empty-row"><span>{records.length ? 'Aramaya uygun kayıt yok.' : isOption ? 'Henüz panel seçeneği oluşturulmadı.' : 'Henüz panel özelliği oluşturulmadı.'}</span></div>}</div></CollapsibleLibrary>{editingAttribute && <DialogSurface as="div" onClose={() => { setEditingId(''); setEditingValue(null) }} className="panel-attribute-modal" aria-label={`${editingAttribute.name} değer düzenleme`}><div className="panel-attribute-modal-card"><header><div>{renamingTitle !== null ? <form className="panel-attribute-title-edit" onSubmit={event => { event.preventDefault(); void renameAttribute(editingAttribute, renamingTitle) }}><input ref={focusOnMount} value={renamingTitle} maxLength={160} aria-label="Başlık adı" onChange={event => setRenamingTitle(event.target.value)} onKeyDown={event => { if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); setRenamingTitle(null) } }} /><button type="submit" aria-label="Başlık adını kaydet"><UiIcon name="check" /></button><button type="button" aria-label="Vazgeç" onClick={() => setRenamingTitle(null)}><UiIcon name="close" /></button></form> : <span className="panel-attribute-title"><strong>{editingAttribute.name}</strong>{!isOption && <button type="button" className="panel-attribute-title-pencil" aria-label={`${editingAttribute.name} başlığını yeniden adlandır`} title="Başlığı yeniden adlandır" onClick={() => setRenamingTitle(editingAttribute.name)}><UiIcon name="edit" /></button>}</span>}<small>{isOption ? 'Panel seçeneği' : 'Panel özelliği'}</small></div><div className="panel-attribute-modal-actions"><button type="button" className="secondary" onClick={() => { const id = editingAttribute.id; setEditingId(''); setEditingValue(null); setCategoryAssignmentId(id) }}>Kategoriler</button><button type="button" className="secondary is-danger" onClick={() => void removeAttribute(editingAttribute)}>Başlığı Sil</button></div><button type="button" aria-label="Düzenleme penceresini kapat" onClick={() => { setEditingId(''); setEditingValue(null) }}><UiIcon name="close" /></button></header><div className="panel-attribute-add-value"><input value={newValues} onChange={event => setNewValues(event.target.value)} placeholder="Yeni değerleri virgülle yazın" /><button type="button" disabled={!newValues.trim()} onClick={() => void addValues(editingAttribute.id, newValues)}>Değer Ekle</button><div className="panel-attribute-sort-actions" aria-label="Değerleri alfabetik sırala"><button type="button" title={`Değerleri ${sortDirection === 1 ? 'A–Z' : 'Z–A'} sırala`} aria-label={`Değerleri ${sortDirection === 1 ? 'A–Z' : 'Z–A'} sırala`} onClick={() => void toggleSortValues(editingAttribute)}>{sortDirection === 1 ? 'A–Z' : 'Z–A'}</button></div></div><div className="panel-attribute-value-editor">{editingValues.length ? editingValues.map(value => <div key={value.id}>{editingValue?.id === value.id ? <input ref={focusOnMount} value={editingValue.value} onChange={event => setEditingValue({ id: value.id, value: event.target.value })} /> : <span>{value.value}</span>}<div>{editingValue?.id === value.id ? <><button type="button" aria-label={`${value.value} değerini kaydet`} onClick={() => void updateValue(editingAttribute.id, value.id, editingValue.value, value.sortOrder)}><UiIcon name="check" /></button><button type="button" aria-label="Değer düzenlemeyi iptal et" onClick={() => setEditingValue(null)}><UiIcon name="close" /></button></> : <><button type="button" aria-label={`${value.value} değerini düzenle`} onClick={() => setEditingValue({ id: value.id, value: value.value })}><UiIcon name="edit" /></button><button type="button" aria-label={`${value.value} değerini sil`} onClick={() => void removeValue(editingAttribute.id, value.id)}><UiIcon name="close" /></button></>}</div></div>) : <span className="panel-value-empty">Henüz değer eklenmedi.</span>}</div></div></DialogSurface>}{categoryAssignmentAttribute && <AttributeCategoryAssignmentModal attribute={categoryAssignmentAttribute} role={role} categories={categories.data?.items ?? []} onClose={() => setCategoryAssignmentId('')} onNotice={message => { showFeedback(message); onNotice(message) }} />}</div>
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
// `focus` (from a product's publish card): the fields that product's publish check found missing, marked in the list and
// walked first by Sonraki Eksik, and the field the window opens on. It applies while the product's own category is shown.
export function CategoryAttributeWorkspace({ platformCode, connectionId, localCategoryId, localCategories, localAttributes, onNotice, focus }: { platformCode: MappingPlatformCode; connectionId: string; localCategoryId: string; localCategories: LocalCategory[]; localAttributes: LocalAttribute[]; onNotice: (value: string) => void; focus?: { items: MappingFocusItem[]; initialExternalAttributeId?: string; initialFieldName?: string; missing?: { title: string; detail: string } } }) {
  const client = useQueryClient()
  const platformLabel = mappingPlatformLabel(platformCode)
  const [attributeCategoryId, setAttributeCategoryId] = useState(localCategoryId)
  const categoryMappings = useQuery({ queryKey: queryKeys.categoryMappings(connectionId), queryFn: ({ signal }) => hubApi<CatalogMapping[]>(`/mappings/categories?connectionId=${encodeURIComponent(connectionId)}`, { signal }), enabled: !!connectionId, retry: false })
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
  const categoryRequirements = useQuery({ queryKey: queryKeys.categoryRequirements(attributeCategoryId, 'attribute-workspace'), queryFn: ({ signal }) => hubApi<ApiAnswer<'getCatalogCategoriesByIdAttributeRequirements'>>(`/catalog/categories/${attributeCategoryId}/attribute-requirements`, { signal }), enabled: !!attributeCategoryId })
  const categoryMapping = useQuery({ queryKey: queryKeys.categoryMapping(attributeCategoryId, connectionId, 'embedded'), queryFn: ({ signal }) => hubApi<CatalogMapping | null>(`/mappings/categories/${attributeCategoryId}?connectionId=${encodeURIComponent(connectionId)}`, { signal }), enabled: !!attributeCategoryId && !!connectionId, retry: false })
  const categoryScope = categoryMapping.data?.externalId ?? ''
  // Same cache entry as the category mapping card, so the target path rarely costs a request.
  const categoryReferences = useQuery({ queryKey: queryKeys.referenceCategories(connectionId), queryFn: ({ signal }) => hubApi<ApiAnswer<'getReferenceDataCategories'>>(`/reference-data/categories?connectionId=${encodeURIComponent(connectionId)}`, { signal }), enabled: !!connectionId && !!categoryScope, retry: false, staleTime: 5 * 60_000 })
  const targetCategoryPath = cleanMarketplaceCategoryPath(categoryReferences.data?.items.find(item => item.externalId === categoryScope)?.path ?? '') || (categoryScope ? `Kategori no: ${categoryScope}` : '')
  const targetCategoryMapping = matchedCategories.find(item => item.category.id === attributeCategoryId)?.mapping
  const targetCategoryStatus = targetCategoryMapping ? categoryMappingStatus(targetCategoryMapping) : null
  const references = useQuery({ queryKey: queryKeys.referenceAttributes(connectionId, categoryScope, 'embedded'), queryFn: ({ signal }) => hubApi<ReferenceData>(`/reference-data/categories/${encodeURIComponent(categoryScope)}/attributes?connectionId=${encodeURIComponent(connectionId)}`, { signal }), enabled: !!connectionId && !!categoryScope, retry: false })
  const sync = useMutation({ mutationFn: () => hubApi(`/connections/${connectionId}/reference-sync-jobs?resourceType=CATEGORY_ATTRIBUTES&parentExternalId=${encodeURIComponent(categoryScope)}`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: '{}' }), onSuccess: async () => { onNotice(`${platformLabel} kategori özellikleri salt-okunur eşitleme kuyruğuna alındı.`); for (const delay of [500, 1000, 2000, 4000, 8000, 15000]) { await new Promise(resolve => window.setTimeout(resolve, delay)); await client.invalidateQueries({ queryKey: queryKeys.referenceAttributes(connectionId, categoryScope, 'embedded') }) } }, onError: reason => onNotice(reason instanceof Error ? reason.message : 'Kategori özellikleri eşitlenemedi.') })
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
  const mappings = useQuery({ queryKey: queryKeys.attributeMappings(connectionId, categoryScope), queryFn: ({ signal }) => hubApi<CatalogMapping[]>(`/mappings/attributes?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(categoryScope)}`, { signal }), enabled: !!connectionId && !!categoryScope, retry: false })
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
  const [bulkBusy, setBulkBusy] = useState<'' | 'HEADINGS' | 'VALUES'>('')
  const [selectedEntryKey, setSelectedEntryKey] = useState('')
  // The field chosen while its values are still being read: the open field stays (dimmed) until they arrive, so the
  // detail never drops to a loading state and back (the jump the owner saw, 7 Nov 2026).
  const [pendingEntryKey, setPendingEntryKey] = useState('')
  const selectSequence = useRef(0)
  const valueDrafts = useValueDraftStore(connectionId, onNotice)
  const [extraPanel, setExtraPanel] = useState<'' | 'defaults' | 'conflicts'>('')
  // null until the owner folds or opens a group: İsteğe Bağlı then starts folded, unless the open field or a product gap is in it.
  const [collapsedGroupsChoice, setCollapsedGroups] = useState<string[] | null>(null)
  // Same cache entries as the two panels, so the header buttons can show how many items each holds.
  const attributeDefaults = useQuery({ queryKey: queryKeys.attributeDefaults(connectionId), queryFn: ({ signal }) => hubApi<ApiAnswer<'getConnectionsByConnectionIdAttributeDefaults'>>(`/connections/${connectionId}/attribute-defaults`, { signal }), retry: false, enabled: !!connectionId && attributeDefaultsPlatforms.has(platformCode) }).data
  const attributeDefaultsCount = attributeDefaults?.length ?? 0
  // Fields filled from the product itself (Kombin ID = Model Kodu), connection wide.
  const fieldDefaultByExternal = new Map((attributeDefaults ?? []).filter(item => item.sourceField).map(item => [item.externalAttributeId, item]))
  const mappingConflictData = useQuery({ queryKey: queryKeys.mappingConflicts(connectionId), queryFn: ({ signal }) => hubApi<ApiAnswer<'getMappingsConflicts'>>(`/mappings/conflicts?connectionId=${encodeURIComponent(connectionId)}`, { signal }), retry: false, staleTime: 30_000, enabled: !!connectionId }).data
  const mappingConflictCount = (mappingConflictData?.sameCategory?.length ?? 0) + (mappingConflictData?.crossCategory?.length ?? 0)
  // Value progress of every mapped field: one read for the whole category (it was one value list per field, 31 requests
  // and 350 KB for Elbise on 7 Nov 2026). A field whose own list is in the cache (opened, saved) counts from that list;
  // the per-field entries are only watched here, never fetched.
  const mappedValueFields = [...productRemoteItems.filter(item => productMappingByExternal.has(item.externalId)), ...(webColorRemote && existingWebColorMapping ? [webColorRemote] : [])]
  const valueProgress = useQuery({ ...valueMappingProgressQuery(connectionId, categoryScope), enabled: !!connectionId && !!categoryScope && mappedValueFields.length > 0 })
  const fieldValueMappings = useQueries({ queries: mappedValueFields.map(item => ({ ...valueMappingsQuery<CatalogMapping[]>(connectionId, `${categoryScope}/${item.externalId}`), enabled: false })) })
  const valueMappingsByExternal = new Map(mappedValueFields.map((item, index) => [item.externalId, mappedLocalIds(fieldValueMappings[index], valueProgress, item.externalId)]))
  // A field's value list and saved pairs, read before the field is shown (hover, focus, a click), so it opens with its
  // data instead of a loading state. Only fields whose values are mapped from a marketplace list need them.
  const prefetchFieldValues = (externalId: string | undefined, wait: boolean): Promise<unknown> | null => {
    if (!externalId || !connectionId || !categoryScope) return null
    const list = valueListQuery<ReferenceData>(connectionId, categoryScope, externalId)
    const saved = valueMappingsQuery<CatalogMapping[]>(connectionId, `${categoryScope}/${externalId}`)
    // Anything already in the cache (fresh or not) or already failed opens at once: the field shows it and refreshes itself.
    const missing = [list.queryKey, saved.queryKey].some(queryKey => { const state = client.getQueryState(queryKey); return !state || (state.data === undefined && state.status !== 'error') })
    const pending = Promise.all([client.prefetchQuery(list), client.prefetchQuery(saved)])
    return wait && missing ? pending : null
  }
  const attributeNameHints = useQuery({ queryKey: queryKeys.attributeNameHints(connectionId), queryFn: ({ signal }) => hubApi<ApiAnswer<'getMappingsAttributeNameHints'>>(`/mappings/attribute-name-hints?connectionId=${encodeURIComponent(connectionId)}`, { signal }), retry: false, staleTime: 5 * 60_000, enabled: !!connectionId })
  // Brings over the attribute and value decisions made for the connection's other categories.
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
      await client.invalidateQueries({ queryKey: queryKeys.attributeMappings(connectionId, categoryScope) })
      await client.invalidateQueries({ queryKey: queryKeys.publicationAttributeReadiness() })
      onNotice(matched || bridged
        ? `${matched ? `${matched} başlık aynı adlı panel özelliğiyle` : ''}${matched && bridged ? ', ' : ''}${bridged ? `${bridged} başlık eş anlamlısı ya da diğer pazaryerindeki eşlemesi üzerinden` : ''} eşlendi.${ambiguous ? ` Birden fazla adayı olan ${ambiguous} başlık atlandı.` : ''}${failed ? ` ${failed} başlık eşlenemedi.` : ''}`
        : failed ? `Başlık eşlemeleri kaydedilemedi (${failed}).` : `Yeni eşleşen başlık bulunamadı.${ambiguous ? ` Birden fazla adayı olan ${ambiguous} başlık atlandı.` : ''}`)
    } finally { setBulkBusy('') }
  }
  async function matchAllValuesByName() {
    if (!references.data || bulkBusy) return
    setBulkBusy('VALUES'); onNotice('')
    let pairs = 0; let refreshed = 0; let headings = 0; let skipped = 0; let unavailable = 0; let failed = 0; let ambiguous = 0
    // Panel values still without a marketplace value after the run, by heading: they have no same-named counterpart.
    const leftovers: Array<{ heading: string; values: string[] }> = []
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
          const job = await hubApi<ApiAnswer<'postConnectionsByConnectionIdReferenceSyncJobs'>>(`/connections/${connectionId}/reference-sync-jobs?resourceType=ATTRIBUTE_VALUES&parentExternalId=${encodeURIComponent(target.valueScope)}`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: '{}' })
          jobs.set(target.externalId, job.jobId)
        })
        await waitForReferenceSyncJobs([...jobs.values()], async jobId => (await hubApi<ApiAnswer<'getJobsById'>>(`/jobs/${jobId}`, { cache: 'no-store' })).job.status)
        await runInBatches(missing.filter(target => jobs.has(target.externalId)), 4, async target => {
          lists.set(target.externalId, await hubApi<ReferenceData>(valueListUrl(target.externalId)))
        })
        await client.invalidateQueries({ queryKey: queryKeys.referenceAttributeValues(connectionId) })
      }
      for (const { externalId, valueScope, localValues, remote } of targets) {
        const valueReferences = lists.get(externalId)
        // a free-text field without a list has nothing to match
        if (!valueReferences) { if (remote.allowsCustomValue !== true) unavailable++; continue }
        try {
          const existing = await hubApi<CatalogMapping[]>(`/mappings/attribute-values?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(valueScope)}`)
          const plan = planDirectReferenceValues(localValues, valueReferences.items.filter(item => item.isActive))
          ambiguous += plan.ambiguousCount
          const mappedLocalIds = new Set(existing.map(item => item.localId))
          let touched = false
          for (const pair of plan.mappings) {
            const current = existing.find(item => item.localId === pair.localId && item.externalId === pair.externalId)
            if (!attributeValueMappingNeedsSave(current, pair.localId, pair.externalId, valueReferences.snapshotId)) continue
            // a panel value goes to one marketplace value (Hepsiburada may reuse it), so never add a second target
            if (platformCode !== 'HEPSIBURADA' && existing.some(item => item.localId === pair.localId && item.externalId !== pair.externalId)) { skipped++; continue }
            try {
              await hubApi<CatalogMapping>(`/mappings/attribute-values/${pair.localId}`, { method: 'PUT', headers: current ? { 'If-Match': `"v${current.version}"` } : {}, body: JSON.stringify({ connectionId, snapshotId: valueReferences.snapshotId, scopeExternalId: valueScope, externalId: pair.externalId, status: 'VERIFIED' }) })
              // the same pair saved against an older list is moved to the current one, not matched anew
              if (current) refreshed++; else { pairs++; touched = true }
              mappedLocalIds.add(pair.localId)
            } catch { failed++ }
          }
          if (touched) headings++
          const open = localValues.filter(item => !mappedLocalIds.has(item.id)).map(item => item.value)
          if (open.length) leftovers.push({ heading: remote.name, values: open })
        } catch { unavailable++ }
      }
      await client.invalidateQueries({ queryKey: queryKeys.attributeValueMappings(connectionId) })
      await client.invalidateQueries({ queryKey: queryKeys.publicationAttributeReadiness() })
      await refreshMappingConflicts(client, connectionId).catch(() => undefined)
      const openCount = leftovers.reduce((sum, item) => sum + item.values.length, 0)
      // A summary, not the values: each heading with its count, the largest first, at most six; the values themselves
      // are marked in each field's table.
      const byCount = [...leftovers].sort((left, right) => right.values.length - left.values.length)
      const openList = byCount.slice(0, 6).map(item => `${item.heading} ${item.values.length}`).join(', ') + (byCount.length > 6 ? ` ve ${byCount.length - 6} başlık daha` : '')
      const openText = openCount ? `${leftovers.length} başlıkta ${openCount} değerin aynı adlı ${platformLabel} karşılığı yok (${openList}). Bunları alanlarının tablosundan elle eşleyin.` : ''
      const extra = `${refreshed ? ` ${refreshed} eşleme güncel ${platformLabel} listesine taşındı.` : ''}${ambiguous ? ` Aynı adı taşıyan birden fazla değer olduğu için ${ambiguous} değer atlandı.` : ''}${skipped ? ` ${skipped} değer başka bir karşılığa bağlı olduğu için atlandı.` : ''}${unavailable ? ` ${unavailable} başlığın değer listesi okunamadı; önce alanları yenileyin.` : ''}${failed ? ` ${failed} değer kaydedilemedi.` : ''}`
      onNotice(pairs
        ? `${headings} başlıkta ${pairs} değer aynı adlı karşılığıyla eşlendi ve kaydedildi.${openText ? ` ${openText}` : ''}${extra}`
        : `0 değer eşlendi (değişiklik yok): ${openText || (targets.length ? 'eşlenmiş başlıkların bütün değerleri zaten eşli.' : 'değer listesi olan eşlenmiş başlık yok.')}${extra}`)
    } catch (reason) {
      onNotice(reason instanceof Error ? reason.message : 'Değerler eşlenemedi.')
    } finally { setBulkBusy('') }
  }
  // On phones the field list is one sideways row; the chosen field (also when Sonraki Eksik picks it) is brought into
  // view. The hooks sit above the early returns; the effect reads the chosen chip from the list and scrolls only when it changed.
  const fieldNavRef = useRef<HTMLElement>(null)
  const scrolledFieldRef = useRef<Element | null>(null)
  // Opened from a gap: the gap's field is brought into view in the list and its first control focused (a value table
  // then moves focus on to the first value still to map).
  const focusedGapField = useRef(false)
  useEffect(() => {
    if (focusedGapField.current || !focus || !(focus.initialExternalAttributeId || focus.initialFieldName)) return
    const item = fieldNavRef.current?.querySelector<HTMLElement>('.cm-item.is-selected')
    if (!item || item.dataset.gapTarget !== 'true') return
    focusedGapField.current = true
    item.scrollIntoView?.({ block: 'nearest' })
    const detail = item.closest('.cm-layout')?.querySelector<HTMLElement>('.cm-detail')
    if (detail && !detail.contains(document.activeElement)) detail.querySelector<HTMLElement>('select:not(:disabled), .pm-trigger:not(:disabled), .vp-trigger, input:not(:disabled)')?.focus({ preventScroll: true })
  })
  useEffect(() => {
    const nav = fieldNavRef.current
    const item = nav?.querySelector<HTMLElement>('.cm-item.is-selected') ?? null
    if (item === scrolledFieldRef.current) return
    scrolledFieldRef.current = item
    if (!nav || !item || nav.scrollWidth <= nav.clientWidth) return
    const navBox = nav.getBoundingClientRect(), itemBox = item.getBoundingClientRect()
    nav.scrollLeft += itemBox.left - navBox.left - (navBox.width - itemBox.width) / 2
  })
  if (!connectionId) return <article className="panel mapping-step"><div className="editor-section-title"><span>4</span><div><h2>{platformLabel} Kategori ve Özellik Eşlemeleri<TitleHint label={`${platformLabel} Kategori ve Özellik Eşlemeleri`}>Önce 1. adımdaki kategori eşlemesini tamamlayın.</TitleHint></h2></div></div><div className="unknown"><strong>Bağlantı seçilmedi</strong><p>Önce aktif {platformLabel} bağlantısını seçin.</p></div></article>
  if (categoryMappings.isLoading) return <article className="panel mapping-step"><div className="editor-section-title"><span>4</span><div><h2>{platformLabel} Kategori ve Özellik Eşlemeleri</h2><p>1. adımdaki eşleşmiş kategoriler hazırlanıyor.</p></div></div><Busy text="Kategori eşlemeleri yükleniyor…" /></article>
  if (!matchedCategories.length) return <article className="panel mapping-step"><div className="editor-section-title"><span>4</span><div><h2>{platformLabel} Kategori ve Özellik Eşlemeleri<TitleHint label={`${platformLabel} Kategori ve Özellik Eşlemeleri`}>Yalnız 1. adımda eşleşen kategorilerin özellikleri burada açılır.</TitleHint></h2></div></div><div className="unknown"><strong>Kategori eşlemesi bekleniyor</strong><p>Önce 1. adımda en az bir panel kategorisini {platformLabel} kategorisiyle eşleştirin.</p></div></article>
  if (!categoryMapping.data) return <article className="panel mapping-step"><div className="editor-section-title"><span>4</span><div><h2>{platformLabel} Kategori ve Özellik Eşlemeleri<TitleHint label={`${platformLabel} Kategori ve Özellik Eşlemeleri`}>Ürün özellikleri ve seçenekleri kendi adımlarında tanımladıktan sonra değer karşılıklarını eşleyin.</TitleHint></h2></div></div><div className="unknown"><strong>Kategori eşlemesi bekleniyor</strong><p>Önce 1. adımda panel kategorisini {platformLabel} kategorisiyle eşleştirin.</p></div></article>

  const mappingEntries: MappingEntry[] = [
    ...(specialColorOption ? [(() => { const colorValues = (allPanelAttributes.find(attribute => attribute.id === existingWebColorMapping?.localId)?.values ?? []).filter(value => value.isActive); const colorDone = new Set((valueMappingsByExternal.get(specialColorOption.remote.externalId) ?? []).filter(localId => colorValues.some(value => value.id === localId))).size; const colorState: FieldState = !existingWebColorMapping ? 'none' : colorValues.length && colorDone < colorValues.length ? 'partial' : 'done'; return { key: 'web-color', externalId: specialColorOption.remote.externalId, valueExternalId: existingWebColorMapping && colorValues.length ? specialColorOption.remote.externalId : undefined, name: 'Web Color', required: true, state: colorState, meta: !existingWebColorMapping ? 'Eşlenmedi' : colorValues.length ? (colorDone < colorValues.length ? `${colorDone}/${colorValues.length} değer` : `${colorValues.length} değer`) : 'Eşlendi', node: <WebColorMappingCard connectionId={connectionId} categoryScope={categoryScope} snapshotId={references.data!.snapshotId} localAttribute={specialColorOption.attribute} localAttributes={productAttributes} categoryOptionIds={categoryOptionIds} remoteAttribute={specialColorOption.remote} existingMapping={existingWebColorMapping} existingMappings={allMappings} onNotice={onNotice} needValueIds={focus && attributeCategoryId === localCategoryId ? focus.items.find(item => item.externalAttributeId === specialColorOption.remote.externalId)?.localValueIds : undefined} /> } })()] : []),
    ...(optionRemoteItems.length > 0 ? optionAttributes.filter(item => !sizeOptionAttributeIds.has(item.id)).map(item => {
      const mappedOption = optionMappings.find(mapping => mapping.localId === item.id && optionRemoteItems.some(remote => remote.externalId === mapping.externalId))
      const mapped = Boolean(mappedOption)
      return { key: `option-${item.id}`, externalId: mappedOption?.externalId, name: item.name, required: false, state: (mapped ? 'done' : 'none') as FieldState, meta: mapped ? 'Seçenek' : 'Eşlenmedi', node: <OptionMappingCard connectionId={connectionId} categoryScope={categoryScope} snapshotId={references.data!.snapshotId} localAttribute={item} remoteAttributes={optionRemoteItems} existingMapping={optionMappings.find(mapping => mapping.localId === item.id && optionRemoteItems.some(remote => remote.externalId === mapping.externalId)) ?? null} onNotice={onNotice} /> }
    }) : []),
    ...productRemoteItems.map((item): MappingEntry => ({ ...attributeEntry(item), externalId: item.externalId }))
  ]
  function attributeEntry(item: ReferenceItem): MappingEntry {
      // N11's Marka (attribute 1) is always sent as the product's own brand; a panel attribute mapped to it is not sent.
      if (platformCode === 'N11' && item.externalId === n11BrandAttributeId) return { key: `attribute-${item.externalId}`, name: item.name, required: item.isRequired === true, state: 'done' as FieldState, meta: 'Ürün bilgisi: Marka', remote: item, mapping: null, productBrand: true }
      const current = productMappingByExternal.get(item.externalId) ?? null
      const fieldDefault = !current && offersProductFields(platformCode, item.allowsCustomValue) ? fieldDefaultByExternal.get(item.externalId) : undefined
      if (fieldDefault?.sourceField) return { key: `attribute-${item.externalId}`, name: item.name, required: item.isRequired === true, state: 'done' as FieldState, meta: `Ürün bilgisi: ${productFieldLabel(fieldDefault.sourceField)}`, remote: item, mapping: null, fieldDefault }
      const local = current ? productAttributes.find(attribute => attribute.id === current.localId) : undefined
      const activeValues = local?.values.filter(value => value.isActive) ?? []
      const needsValues = !!current && activeValues.length > 0 && item.allowsCustomValue !== true && !(platformCode === 'TRENDYOL' && usesCustomPanelColorValue(item.name))
      const saved = valueMappingsByExternal.get(item.externalId)
      const done = needsValues && saved ? new Set(saved.filter(localId => activeValues.some(value => value.id === localId))).size : null
      const state: FieldState = !current ? 'none' : needsValues && done !== null && done < activeValues.length ? 'partial' : 'done'
      const meta = !current ? 'Eşlenmedi' : !needsValues ? 'Eşlendi' : done !== null && done < activeValues.length ? `${done}/${activeValues.length} değer` : `${activeValues.length} değer`
      return { key: `attribute-${item.externalId}`, valueExternalId: needsValues ? item.externalId : undefined, name: item.name, required: item.isRequired === true, state, meta, ratio: done !== null && activeValues.length ? done / activeValues.length : undefined, remote: item, mapping: current }
  }
  const requiredEntries = mappingEntries.filter(entry => entry.required)
  const optionalEntries = mappingEntries.filter(entry => !entry.required)
  // The product's gaps, by the list entry of their marketplace field (required gaps first, then optional warnings).
  const focusActive = Boolean(focus) && attributeCategoryId === localCategoryId
  const productIssueByKey = new Map<string, MappingFocusItem>()
  if (focusActive) for (const item of focus!.items) {
    const entry = mappingEntries.find(candidate => candidate.externalId === item.externalAttributeId)
    if (entry && !productIssueByKey.has(entry.key)) productIssueByKey.set(entry.key, item)
  }
  const productIssueKeys = [...productIssueByKey.keys()]
  // The pressed gap's field: by its marketplace field id, else by its name (Web Color). A field the list does not have is
  // never swapped for another one silently: the window says which gap it was and why that field could not be opened.
  const initialFocusKey = !focusActive ? undefined
    : (focus?.initialExternalAttributeId ? mappingEntries.find(entry => entry.externalId === focus.initialExternalAttributeId)?.key : undefined)
      ?? (focus?.initialFieldName ? mappingEntries.find(entry => normalizedAttributeKey(entry.name) === normalizedAttributeKey(focus.initialFieldName!))?.key : undefined)
  const wantedField = focus?.initialFieldName ?? focus?.missing?.title
  const targetMissing = Boolean(focus && (focus.initialExternalAttributeId || focus.initialFieldName)) && !initialFocusKey && !selectedEntryKey
  const activeEntry = mappingEntries.find(entry => entry.key === selectedEntryKey) ?? mappingEntries.find(entry => entry.key === initialFocusKey) ?? mappingEntries.find(entry => entry.required && entry.state !== 'done') ?? mappingEntries[0]
  const activeIndex = activeEntry ? mappingEntries.indexOf(activeEntry) : -1
  const nextOpenEntry = [...mappingEntries.slice(activeIndex + 1), ...mappingEntries.slice(0, Math.max(0, activeIndex))].sort((left, right) => Number(right.required) - Number(left.required)).find(entry => entry.state !== 'done')
  const entryGroups = [{ title: 'Zorunlu', items: requiredEntries }, { title: 'İsteğe Bağlı', items: optionalEntries }]
    .filter(group => group.items.length > 0)
  const collapsedGroups = collapsedGroupsChoice ?? (optionalEntries.some(entry => entry.key === activeEntry?.key || productIssueByKey.has(entry.key)) ? [] : ['İsteğe Bağlı'])
  const requiredReady = requiredEntries.filter(entry => entry.state === 'done').length
  const openRequired = requiredEntries.length - requiredReady
  const footerHint = openRequired ? `${openRequired} zorunlu alan bekliyor` : 'Zorunlu alanların hepsi hazır'
  const nextKey = productIssueKeys.length ? nextMissingEntryKey(productIssueKeys, activeEntry?.key, nextOpenEntry?.key) : nextOpenEntry?.key
  // Opens a field: at once when its values are in the cache, otherwise once they have arrived (at most 1.5 s), the
  // open field dimmed meanwhile. A later choice wins over an earlier one still waiting.
  function selectEntry(key: string) {
    const sequence = ++selectSequence.current
    const entry = mappingEntries.find(candidate => candidate.key === key)
    const loading = key === activeEntry?.key ? null : prefetchFieldValues(entry?.valueExternalId, true)
    if (!loading) { setPendingEntryKey(''); setSelectedEntryKey(key); return }
    setPendingEntryKey(key)
    void Promise.race([loading, wait(1500)]).catch(() => undefined).finally(() => {
      if (selectSequence.current !== sequence) return
      setPendingEntryKey(''); setSelectedEntryKey(key)
    })
  }
  const prefetchEntry = (entry: MappingEntry) => { void prefetchFieldValues(entry.valueExternalId, false) }
  const pendingEntry = pendingEntryKey ? mappingEntries.find(entry => entry.key === pendingEntryKey) : undefined
  const shownKey = pendingEntry?.key ?? activeEntry?.key
  const goToNext = nextKey ? () => selectEntry(nextKey) : undefined
  const activeProductIssue = activeEntry ? productIssueByKey.get(activeEntry.key) : undefined
  const dataLoading = references.isLoading || mappings.isLoading || categoryRequirements.isLoading
  const dataFailed = references.isError || mappings.isError || categoryRequirements.isError
  return <article className="panel mapping-step cm-card">
    <header className="cm-page-head"><h2>Kategori Eşleştirmeleri</h2></header>
    <div className="cm-context">
      <label className="cm-field" htmlFor="mapping-attribute-category"><small>Panel Kategorisi</small><PanelSelect id="mapping-attribute-category" ariaLabel="Panel kategorisi" value={attributeCategoryId} onChange={setAttributeCategoryId} width={200} options={matchedCategories.map(({ category }) => ({ value: category.id, label: category.path }))} /></label>
      <span className="cm-link cm-context-link" aria-hidden="true"><svg viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round"><path d="M6.6 9.4 9.4 6.6" /><path d="M7.4 4.6 8.6 3.4a2.6 2.6 0 0 1 3.7 3.7l-1.2 1.2" /><path d="M8.6 11.4 7.4 12.6a2.6 2.6 0 0 1-3.7-3.7l1.2-1.2" /></svg></span>
      <div className="cm-field cm-target"><small>{platformLabel} Kategorisi</small><span className="cm-path"><strong title={targetCategoryPath}>{targetCategoryPath}</strong>{targetCategoryStatus && <span className={`mapping-table-status ${targetCategoryStatus.className}`} title={targetCategoryStatus.title}>{targetCategoryStatus.label}</span>}</span></div>
      {!dataLoading && !dataFailed && requiredEntries.length > 0 && <div className="cm-progress" aria-label={`Zorunlu alanlar: ${requiredReady} / ${requiredEntries.length}`}><div><span>Zorunlu Alanlar</span><b>{requiredReady} / {requiredEntries.length}</b></div><div className="cm-track"><i style={{ width: `${Math.round(requiredReady / requiredEntries.length * 100)}%` }} /></div></div>}
      {!dataLoading && !dataFailed && <div className="cm-actions" role="toolbar" aria-label="Kategori eşleme işlemleri">
      <button type="button" className="secondary" title="Adı aynı, eş anlamlı (ör. Materyal ≈ Kumaş Tipi) ya da diğer pazaryerinde eşlenmiş başlıkları toplu eşler; birden fazla aday varsa atlar." disabled={sync.isPending || bulkBusy !== ''} onClick={() => void matchHeadingsByName()}>{bulkBusy === 'HEADINGS' ? 'Başlıklar Eşleniyor…' : 'Başlıkları Eşle'}</button>
      <button type="button" className="secondary" title="Eşlenmiş tüm başlıklarda adı aynı olan değerleri toplu eşler" disabled={sync.isPending || bulkBusy !== ''} onClick={() => void matchAllValuesByName()}>{bulkBusy === 'VALUES' ? 'Değerler Eşleniyor…' : 'Tüm Değerleri Eşle'}</button>
      {attributeDefaultsPlatforms.has(platformCode) && categoryScope && <button type="button" className="secondary" title={`Ürünlerde boş kalan alanlar için ${dative(platformLabel)} gönderilecek sabit değerler`} onClick={() => setExtraPanel('defaults')}>Platform Varsayılanları <span className="cm-count">{attributeDefaultsCount}</span></button>}
      <button type="button" className={`secondary${mappingConflictCount ? ' cm-has-conflicts' : ''}`} title={`Aynı panel değeri birden fazla ${platformLabel} değerine bağlanmış eşlemeler`} onClick={() => setExtraPanel('conflicts')}>Gözden Geçirilecek <span className="cm-count">{mappingConflictCount}</span></button>
      {platformCode === 'TRENDYOL' && optionCount > 0 && <button type="button" className="secondary" title="Trendyol’daki ürünleri okuyarak seçenekleri günceller; Trendyol’a hiçbir şey göndermez." disabled={productSync.isPending} onClick={() => productSync.mutate()}>{productSync.isPending ? 'Ürünler Eşitleniyor…' : 'Seçenekleri Eşitle'}</button>}
      <button type="button" className="secondary" title={`Bu kategorinin ${platformLabel} alan listesini salt-okunur olarak yeniden okur.`} disabled={sync.isPending} onClick={() => sync.mutate()}><UiIcon name="refresh" />{sync.isPending ? 'Güncelleniyor…' : 'Pazaryerinden Güncelle'}</button>
      </div>}
    </div>
    {dataLoading ? <Busy text="Kategori özellikleri ve eşlemeler yükleniyor…" /> : dataFailed ? <div className="unknown"><strong>Güncel kategori özellik verisi alınamadı</strong><p>Seçili {platformLabel} kategorisinin özellik listesini yeniden eşitleyin.</p><button type="button" disabled={sync.isPending} onClick={() => sync.mutate()}>{sync.isPending ? 'Kuyruğa Alınıyor…' : 'Kategori Özelliklerini Eşitle'}</button></div> : <>
      <ValueDraftContext.Provider value={valueDrafts}>
      <div className="cm-layout">
        {/* Phones: the field list is one select above the field (the side list is hidden there). */}
        <label className="cm-nav-select" htmlFor="mapping-field-nav"><small>Alan</small><PanelSelect id="mapping-field-nav" ariaLabel="Eşlenecek alan" value={shownKey ?? ''} onChange={selectEntry} width={320} options={entryGroups.flatMap(group => [{ value: `__group:${group.title}`, label: <strong>{`${group.title} (${group.items.filter(entry => entry.state === 'done').length} / ${group.items.length})`}</strong>, text: group.title, disabled: true }, ...group.items.map(entry => ({ value: entry.key, label: `${entry.state === 'done' ? '✓' : entry.state === 'partial' ? '◐' : '○'} ${entry.name} · ${entry.meta}` }))])} /></label>
        <nav ref={fieldNavRef} className="cm-nav" aria-label="Kategori özellikleri">
          {entryGroups.map(group => <Fragment key={group.title}>
            <button type="button" className={`cm-section${collapsedGroups.includes(group.title) ? ' is-collapsed' : ''}`} aria-expanded={!collapsedGroups.includes(group.title)} onClick={() => setCollapsedGroups(() => collapsedGroups.includes(group.title) ? collapsedGroups.filter(title => title !== group.title) : [...collapsedGroups, group.title])}><svg className="cm-section-chevron" viewBox="0 0 16 16" aria-hidden="true"><path d="m4.5 6.5 3.5 3.5 3.5-3.5" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" /></svg><span>{group.title}</span><span>{group.items.filter(entry => entry.state === 'done').length} / {group.items.length}</span></button>
            {!collapsedGroups.includes(group.title) && group.items.map(entry => {
              const unsaved = Boolean(entry.remote && valueDrafts.drafts.has(`${categoryScope}/${entry.remote.externalId}`))
              const productIssue = productIssueByKey.get(entry.key)
              const issueText = productIssue ? `${productIssue.required ? 'Bu üründe zorunlu eksik' : 'Bu üründe isteğe bağlı uyarı'}: ${productIssue.details.join(' · ')}` : ''
              return <button type="button" key={entry.key} className={`cm-item${entry.key === shownKey ? ' is-selected' : ''}${unsaved ? ' is-unsaved' : ''}${productIssue ? ` has-product-issue is-product-${productIssue.required ? 'required' : 'optional'}` : ''}`} title={[unsaved ? 'Kaydedilmemiş değer seçimleri var' : '', issueText].filter(Boolean).join(' — ') || undefined} aria-current={entry.key === shownKey ? 'true' : undefined} data-gap-target={entry.key === initialFocusKey ? 'true' : undefined} onPointerEnter={() => prefetchEntry(entry)} onFocus={() => prefetchEntry(entry)} onClick={() => selectEntry(entry.key)}><MappingStateGlyph state={entry.state} required={entry.required} ratio={entry.ratio} /><span className="cm-name">{entry.name}{productIssue && <span className="cm-product-issue" aria-label={issueText} role="img" />}</span><span className="cm-meta">{entry.meta}</span></button>
            })}
          </Fragment>)}
        </nav>
        <section className={`cm-detail${pendingEntry ? ' is-pending' : ''}`} aria-busy={pendingEntry ? true : undefined} key={activeEntry?.key ?? 'empty'}>
          {targetMissing && <p className="cm-product-note is-required cm-target-missing" role="alert"><b>{focus?.missing?.title ?? wantedField}</b> {focus?.missing?.detail ? `${focus.missing.detail} ` : ''}“{wantedField}” alanı {focusActive ? `bu ${platformLabel} kategorisinin alanları arasında yok` : 'ürünün kategorisi seçili olmadığı için gösterilemiyor'}; bu yüzden açılamadı. Soldan başka bir alan seçebilirsiniz.</p>}
          {!activeEntry ? <p className="cm-empty">Bu kategoride eşlenecek {platformLabel} alanı yok.</p>
            : activeEntry.remote && activeEntry.productBrand ? <ProductBrandField remote={activeEntry.remote} required={activeEntry.required} platformLabel={platformLabel} footerHint={footerHint} onNext={goToNext} />
            : activeEntry.remote ? <FieldDetail platformCode={platformCode} platformLabel={platformLabel} connectionId={connectionId} categoryScope={categoryScope} snapshotId={references.data!.snapshotId} localAttributes={activeEntry.localAttributes ?? productAttributes} categoryOptionIds={categoryOptionIds} remote={activeEntry.remote} mapping={activeEntry.mapping ?? null} fieldDefault={activeEntry.fieldDefault} role={activeEntry.role} required={activeEntry.required} footerHint={footerHint} onNext={goToNext} onNotice={onNotice} highlightValueIds={activeProductIssue?.localValueIds} />
            : <>
              <FieldHeading name={activeEntry.name} required={activeEntry.required} platformLabel={platformLabel} />
              <div className="cm-legacy-card">{activeEntry.node}</div>
              <FieldFooter hint={footerHint} onNext={goToNext} />
            </>}
        </section>
      </div>
      </ValueDraftContext.Provider>
    </>}
    {extraPanel === 'defaults' && <RrModal title="Platform Varsayılanları" hint={`Ürünlerde boş kalan alanlar için ${dative(platformLabel)} gönderilecek sabit değerler.`} size="lg" modalClass="cm-defaults-modal" onClose={() => setExtraPanel('')}>
      {categoryScope && references.data && <AttributeDefaultsPanel connectionId={connectionId} categoryScope={categoryScope} platformLabel={platformLabel} remoteAttributes={platformCode === 'N11' ? references.data.items.filter(item => item.externalId !== n11BrandAttributeId) : references.data.items} onNotice={onNotice} expanded />}
    </RrModal>}
    <Modal open={extraPanel === 'conflicts'} title="Gözden Geçirilecek Eşlemeler" hint={`Aynı panel değeri birden fazla ${platformLabel} değerine bağlanmış eşlemeler.`} onClose={() => setExtraPanel('')} className="cm-extra-modal">
      {mappingConflictCount ? <MappingConflictsPanel connectionId={connectionId} platformLabel={platformLabel} onNotice={onNotice} expanded /> : <EmptyState icon="circleCheck" title="Eşleme Yok">Gözden geçirilecek eşleme yok.</EmptyState>}
    </Modal>
  </article>
}
type MappingEntry = { key: string; externalId?: string; valueExternalId?: string; name: string; required: boolean; state: FieldState; meta: string; ratio?: number; node?: ReactNode; remote?: ReferenceItem; mapping?: CatalogMapping | null; localAttributes?: LocalAttribute[]; role?: 'ATTRIBUTE' | 'OPTION'; fieldDefault?: AttributeDefault; productBrand?: boolean }
const n11BrandAttributeId = '1'
// The publications that send a connection's attribute defaults when a product leaves the field empty (ProductPublicationComposer,
// HepsiburadaProductPublicationComposer, N11ProductPublicationComposer, PazaramaProductPublicationComposer); n11 never takes
// its brand from a default, so its Marka field is not offered.
const attributeDefaultsPlatforms = new Set(['TRENDYOL', 'HEPSIBURADA', 'N11', 'PAZARAMA'])

function useValueDraftStore(connectionId: string, onNotice: (value: string) => void): ValueDraftStore {
  const client = useQueryClient()
  const [drafts, setDrafts] = useState<ReadonlyMap<string, ValueDraft>>(() => new Map())
  const [generation, setGeneration] = useState(0)
  const [kept, setKept] = useState<ReadonlySet<string>>(() => new Set())
  const [saving, setSaving] = useState(false)
  const put = useCallback((draft: ValueDraft) => setDrafts(current => new Map(current).set(draft.valueScope, draft)), [])
  const drop = useCallback((valueScope: string) => setDrafts(current => { if (!current.has(valueScope)) return current; const next = new Map(current); next.delete(valueScope); return next }), [])
  const saveAll = useCallback(async () => {
    if (saving || !drafts.size) return
    setSaving(true)
    const pending = [...drafts.values()]
    // A save that stops unexpectedly keeps every draft; a finished one keeps only the fields with a refused request.
    let keptDrafts = new Map(pending.map(draft => [draft.valueScope, draft]))
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
      keptDrafts = draftsKeptAfterSave(pending, failures.map(failure => failure.item))
      const fields = pending.map(draft => draft.fieldName).join(', ')
      onNotice(failures.length
        ? `${fields}: ${saved} değer eşlemesi kaydedildi, ${failures.length} kaydedilemedi. ${mappingSaveErrorMessage(failures[0].error, 'Değer eşlemeleri kaydedilemedi.')} Kaydedilemeyen seçimler seçili duruyor; Kaydet ile yeniden deneyebilirsiniz.`
        : saved ? `${pending.length > 1 ? `${pending.length} alanda` : `${fields}:`} ${saved} değer eşlemesi kaydedildi.` : 'Değer eşlemelerinde değişiklik yok.')
    } finally {
      // The lists are always reloaded, so every field shows what the server now holds.
      await Promise.all(pending.map(draft => client.invalidateQueries({ queryKey: queryKeys.attributeValueMappings(connectionId, draft.valueScope) }).catch(() => undefined)))
      await client.invalidateQueries({ queryKey: queryKeys.publicationAttributeReadiness() }).catch(() => undefined)
      await refreshMappingConflicts(client, connectionId).catch(() => undefined)
      // A field with a refused request keeps its draft, moved onto the reloaded pairs (what was saved now matches the
      // server, the versions are current): what was not saved stays chosen for the next Kaydet.
      const rebased = new Map<string, ValueDraft>()
      for (const draft of keptDrafts.values()) {
        const fresh = await client.fetchQuery(valueMappingsQuery<CatalogMapping[]>(connectionId, draft.valueScope)).catch(() => null)
        const next = fresh ? { ...draft, saved: fresh, selections: rebaseSelections(draft.selections, groupSavedSelections(draft.saved), groupSavedSelections(fresh)) } : draft
        if (!fresh || selectionsDiffer(next.selections, groupSavedSelections(fresh))) rebased.set(draft.valueScope, next)
      }
      setDrafts(rebased)
      setKept(new Set(rebased.keys()))
      setGeneration(value => value + 1)
      setSaving(false)
    }
  }, [client, connectionId, drafts, onNotice, saving])
  return useMemo(() => ({ drafts, generation, kept, saving, put, drop, saveAll }), [drafts, generation, kept, saving, put, drop, saveAll])
}

const autoQueuedCategoryAttributeSyncs = new Set<string>()
