import type { ApiAnswer } from '../../../../shared/api/api-answer'
import { memo, useCallback, useContext, useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiRequestError, hubApi, hubPoll } from '../../../../shared/api'
import { Busy, ErrorBox, Modal, TitleHint, UiIcon, DialogSurface } from '../../../../shared/components'
import { mappingSaveErrorMessage, refreshMappingConflicts, saveAllMappings } from '../mapping-refresh'
import { valueListQuery, valueMappingsQuery } from '../value-field-queries'
import { webColorDraftSelections, webColorSelectionsFromDraft } from '../value-drafts'
import { newIdempotencyKey } from '../../../../shared/idempotency'
import { MessageToast } from '../../../../shared/components/NoticeToast'
import { PanelValueMultiSelect } from '../../PanelValueMultiSelect'
import { appendNotification } from '../../../../shared/notifications'
import { formatPanelColorValue, usesCustomPanelColorValue } from '../../color-value-format'
import { autoMatchScore, valueMatchScore } from '../../../../shared/value-matching'
import { attributeValueMappingNeedsSave, isTemplateFilledRow, suggestValueMappings, type ValueMappingSuggestion, visiblePanelValues, normalizeReferenceValueLabel, planDirectReferenceValues, planMissingReferencePanelValues, planReferencePanelMappings, togglePanelValueReferenceSelection, updatePanelValueReferenceSelection, valueMappingRowClassName } from '../../attribute-value-mapping'
import { isReferenceSyncJobTerminal, referenceSyncJobPollInterval, shouldAutoSyncHepsiburadaValues } from '../../reference-value-sync'
import { focusOnMount } from '../../../../shared/focus'
import { Page, notificationKindFromMessage } from '../../shared'
import { valueStillNeedsMapping } from '../../../catalog/value-mapping-rule'
import { PanelSelect } from '../../../../shared/PanelSelect'
import { queryKeys } from '../../../../shared/api/query-keys'
import { useMappingNotificationConnection, type LocalAttribute, type ReferenceItem, type ReferenceData, type CatalogMapping, isPanelColorAttributeName, ValueDraftContext, valueCollator } from './mapping-shared'

function MappingValueModal({ open, title, description, onClose, children }: { open: boolean; title: string; description: string; onClose: () => void; children: ReactNode }) {
  const displayedDescription = description.startsWith('Panel değerlerini mevcut Trendyol seçeneklerine eşleyin.')
    ? 'Trendyol değerlerini karşılık gelen panel değerleriyle eşleyin. Trendyol’a yeni değer gönderilmez.'
    : description
  return <Modal open={open} title={title} hint={displayedDescription} onClose={onClose} className="mapping-values-modal">{children}</Modal>
}
export function OptionMappingCard({ connectionId, categoryScope, snapshotId, localAttribute, remoteAttributes, existingMapping, onNotice }: { connectionId: string; categoryScope: string; snapshotId: string; localAttribute: LocalAttribute; remoteAttributes: ReferenceItem[]; existingMapping: CatalogMapping | null; onNotice: (value: string) => void }) {
  const client = useQueryClient(); const [externalId, setExternalId] = useState(existingMapping?.externalId ?? ''); const [showValues, setShowValues] = useState(false)
  useEffect(() => { setExternalId(existingMapping?.externalId ?? ''); setShowValues(false) }, [existingMapping?.externalId])
  const selectedRemote = remoteAttributes.find(item => item.externalId === externalId)
  async function save() {
    try {
      if (!externalId) throw new Error('Trendyol seçenek karşılığını seçin.')
      await hubApi<CatalogMapping>(`/mappings/attributes/${localAttribute.id}`, { method: 'PUT', headers: existingMapping ? { 'If-Match': `"v${existingMapping.version}"` } : {}, body: JSON.stringify({ connectionId, snapshotId, scopeExternalId: categoryScope, externalId, status: 'VERIFIED', role: 'OPTION' }) })
      onNotice(`${localAttribute.name} seçenek eşlemesi kaydedildi.`); await client.invalidateQueries({ queryKey: queryKeys.attributeMappings(connectionId, categoryScope) })
    } catch (reason) { onNotice(reason instanceof Error ? reason.message : 'Seçenek eşlemesi kaydedilemedi.') }
  }
  async function remove() {
    if (!existingMapping) return
    try { await hubApi<boolean>(`/mappings/attributes/${localAttribute.id}?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(categoryScope)}&externalId=${encodeURIComponent(existingMapping.externalId)}`, { method: 'DELETE', headers: { 'If-Match': `"v${existingMapping.version}"` } }); setExternalId(''); onNotice(`${localAttribute.name} seçenek eşlemesi kaldırıldı.`); await client.invalidateQueries({ queryKey: queryKeys.attributeMappings(connectionId, categoryScope) }) } catch (reason) { onNotice(reason instanceof Error ? reason.message : 'Seçenek eşlemesi kaldırılamadı.') }
  }
  const mapped = !!existingMapping && existingMapping.externalId === externalId
  const isColorOption = isPanelColorAttributeName(localAttribute.name)
  const customPanelColorValues = usesCustomPanelColorValue(selectedRemote?.name ?? '')
  const cardTitle = isColorOption ? 'Renk' : `Panel: ${localAttribute.name}`
  const cardDescription = mapped
    ? (isColorOption ? 'Trendyol’dan çekilen düz renk · Kaynak: Panel Renk' : `Trendyol: ${selectedRemote?.name ?? existingMapping?.externalId}`)
    : (isColorOption ? 'Trendyol’dan çekilen düz renk alanı' : 'Varyant üreten panel seçeneği')
  return <section className={`attribute-mapping-card option-mapping-card ${mapped ? '' : 'unmapped'}`}><div className="attribute-mapping-card-head"><span className="attr-source">OPT</span><div><strong>{cardTitle}</strong><small>{cardDescription}</small></div></div><div className="mapping-fields compact"><label htmlFor={`option-map-${localAttribute.id}`}>Trendyol Seçenek/Özellik Karşılığı<PanelSelect id={`option-map-${localAttribute.id}`} ariaLabel={`${localAttribute.name} Trendyol seçenek karşılığı`} value={externalId} onChange={setExternalId} width={280} options={[{ value: '', label: 'Karşılık Seçin' }, ...remoteAttributes.map(item => ({ value: item.externalId, label: `${item.name}${item.isRequired ? ' · Zorunlu' : ''}` }))]} /></label>{mapped ? <button type="button" className="secondary" onClick={() => void remove()}>Eşlemeyi Kaldır</button> : <button type="button" onClick={() => void save()} disabled={!externalId}>Kaydet</button>}</div>{mapped && localAttribute.values.length > 0 && <><button type="button" className="value-mapping-toggle" onClick={() => setShowValues(value => !value)}>{showValues ? 'Seçenek Değerleri Açık' : customPanelColorValues ? `Gönderilecek Renkleri Gör (${localAttribute.values.length})` : `Seçenek Değerlerini Eşle (${localAttribute.values.length})`}</button><MappingValueModal open={showValues && Boolean(selectedRemote)} title={customPanelColorValues ? 'Trendyol’a Gönderilecek Panel Renkleri' : `${localAttribute.name} Değer Eşlemeleri`} description={customPanelColorValues ? 'Normal Renk alanı sabit değer listesi kullanmaz; panel renkleri baş harfleri büyük biçimde gönderilir.' : 'Panel değerlerini mevcut Trendyol seçeneklerine eşleyin. Yeni değer Trendyol’a gönderilmez.'} onClose={() => setShowValues(false)}>{selectedRemote && <AttributeValueMappingEditor connectionId={connectionId} categoryScope={categoryScope} attribute={localAttribute} externalAttributeId={selectedRemote.externalId} customPanelColorValues={customPanelColorValues} required={selectedRemote.isRequired === true} />}</MappingValueModal></>}</section>
}
export function WebColorMappingCard({ connectionId, categoryScope, snapshotId, localAttribute, localAttributes, categoryOptionIds, remoteAttribute, existingMapping, existingMappings, onNotice, needValueIds }: { connectionId: string; categoryScope: string; snapshotId: string; localAttribute: LocalAttribute; localAttributes: LocalAttribute[]; categoryOptionIds: Set<string>; remoteAttribute: ReferenceItem; existingMapping: CatalogMapping | null; existingMappings: CatalogMapping[]; onNotice: (value: string) => void; needValueIds?: readonly string[] }) {
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
      onNotice(`${selectedAttribute.name} → Trendyol Web Color eşlemesi kaydedildi.`); setShowValues(true); await client.invalidateQueries({ queryKey: queryKeys.attributeMappings(connectionId, categoryScope) })
    } catch (reason) { onNotice(reason instanceof Error ? reason.message : 'Web Color eşlemesi kaydedilemedi.') }
  }
  async function remove() {
    if (!currentMapping) return
    try { await hubApi<boolean>(`/mappings/attributes/${currentMapping.localId}?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(categoryScope)}&externalId=${encodeURIComponent(currentMapping.externalId)}`, { method: 'DELETE', headers: { 'If-Match': `"v${currentMapping.version}"` } }); setShowValues(false); onNotice('Panel Renk → Trendyol Web Color eşlemesi kaldırıldı.'); await client.invalidateQueries({ queryKey: queryKeys.attributeMappings(connectionId, categoryScope) }) } catch (reason) { onNotice(reason instanceof Error ? reason.message : 'Web Color eşlemesi kaldırılamadı.') }
  }
  return <>
    <div className="cm-form">
      <div className="cm-field"><small>Trendyol Alanı</small><span className="cm-readonly">Web Color</span></div>
      <span className="cm-link" aria-hidden="true"><svg viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round"><path d="M6.6 9.4 9.4 6.6" /><path d="M7.4 4.6 8.6 3.4a2.6 2.6 0 0 1 3.7 3.7l-1.2 1.2" /><path d="M8.6 11.4 7.4 12.6a2.6 2.6 0 0 1-3.7-3.7l1.2-1.2" /></svg></span>
      <div className="cm-field"><small>Panel Özelliği</small><span className="cm-readonly">{selectedAttribute.name}</span></div>
      {!mapped && <button type="button" className="cm-webcolor-link" onClick={() => void save()}>Bağla</button>}
    </div>
    {mapped ? (selectedAttribute.values.length > 0 ? <div className="cm-webcolor"><WebColorValueMappingEditor connectionId={connectionId} categoryScope={categoryScope} attribute={selectedAttribute} externalAttributeId={remoteAttribute.externalId} needValueIds={needValueIds} /></div> : <p className="cm-empty">Panel {selectedAttribute.name} özelliğinde henüz değer yok.</p>)
      : <p className="cm-empty">Web Color, panel {selectedAttribute.name} değerlerinden gönderilir. Değer eşlemek için önce bağlayın.</p>}
  </>
}
// One panel colour box of the Web Color grid; a tick re-renders its own box only.
const WebColorValueBox = memo(function WebColorValueBox({ localId, value, selected, needed, disabled, onToggle }: { localId: string; value: string; selected: boolean; needed: boolean; disabled: boolean; onToggle: (localId: string) => void }) {
  return <label className={`web-color-panel-value${selected ? ' selected' : ''}${needed ? ' needs-mapping' : ''}`} title={needed ? 'Bu ürünün yayını için Web Color değeri gerekiyor' : undefined}><input type="checkbox" checked={selected} disabled={disabled} onChange={() => onToggle(localId)} /><span>{value}</span></label>
})
// `needValueIds`: the panel colours a product's publish check found without a Web Color value. They come first, are
// marked until each has one (picked here or saved), counted at the top, and the first is brought into view.
function WebColorValueMappingEditor({ connectionId, categoryScope, attribute, externalAttributeId, needValueIds }: { connectionId: string; categoryScope: string; attribute: LocalAttribute; externalAttributeId: string; needValueIds?: readonly string[] }) {
  // The choices are the window's draft like every other field's (ValueDraftContext): the window's Kaydet saves them.
  const store = useContext(ValueDraftContext)
  const [notice, setNotice] = useState(''); const [selectedRemoteId, setSelectedRemoteId] = useState(''); const [showGroups, setShowGroups] = useState(false); const valueScope = `${categoryScope}/${externalAttributeId}`
  const [selections, setSelections] = useState<Record<string, string>>(() => webColorSelectionsFromDraft(store?.drafts.get(valueScope)?.selections))
  const draftedRef = useRef(store?.drafts.has(valueScope) === true)
  draftedRef.current = store?.drafts.has(valueScope) === true
  const references = useQuery(valueListQuery<ReferenceData>(connectionId, categoryScope, externalAttributeId))
  const mappings = useQuery(valueMappingsQuery<CatalogMapping[]>(connectionId, valueScope))
  const sync = useMutation({ mutationFn: () => hubApi(`/connections/${connectionId}/reference-sync-jobs?resourceType=ATTRIBUTE_VALUES&parentExternalId=${encodeURIComponent(valueScope)}`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: '{}' }), onSuccess: () => setNotice('Web Color değerleri salt-okunur eşitleme kuyruğuna alındı.'), onError: reason => setNotice(reason instanceof Error ? reason.message : 'Web Color değerleri eşitlenemedi.') })
  const mappingConnection = useMappingNotificationConnection(connectionId)
  useEffect(() => {
    if (!notice) return
    const kind = notificationKindFromMessage(notice)
    appendNotification({ message: notice, kind, connectionId: mappingConnection.current || undefined, operation: 'Eşleştirme' })
    document.documentElement.dataset.rvMappingFeedback = kind
    const timeout = window.setTimeout(() => setNotice(''), kind === 'info' ? 7000 : 5500)
    return () => window.clearTimeout(timeout)
  }, [notice, mappingConnection])
  // Sorted once per list, not on every tick of a colour box (one shared collator instead of localeCompare per pair).
  const localValues = useMemo(() => (attribute.values?.filter(item => item.isActive) ?? []).slice().sort((left, right) => valueCollator.compare(left.value, right.value)), [attribute.values]); const remoteValues = useMemo(() => (references.data?.items.filter(item => item.isActive) ?? []).slice().sort((left, right) => valueCollator.compare(left.name, right.name)), [references.data]); const mappingByLocal = new Map((mappings.data ?? []).map(item => [item.localId, item]))
  // Before paint, so the rows do not show unmapped for a moment once the mappings are in.
  // A draft kept by the window (the user moved to another field and back) stays; otherwise, and after Kaydet, the
  // saved pairs.
  const generation = store?.generation ?? 0
  useLayoutEffect(() => { if (mappings.data && !draftedRef.current) setSelections(Object.fromEntries(mappings.data.map(item => [item.localId, item.externalId]))) }, [mappings.data, generation])
  const put = store?.put; const drop = store?.drop
  useEffect(() => {
    if (!references.data || !mappings.data || !put || !drop) return
    const saved = Object.fromEntries(mappings.data.map(item => [item.localId, item.externalId]))
    const changed = [...new Set([...Object.keys(saved), ...Object.keys(selections)])].some(localId => (saved[localId] ?? '') !== (selections[localId] ?? ''))
    if (!changed) { drop(valueScope); return }
    put({ valueScope, fieldName: 'Web Color', selections: webColorDraftSelections(selections), saved: mappings.data.map(item => ({ localId: item.localId, externalId: item.externalId, version: item.version, snapshotId: item.snapshotId ?? '' })), snapshotId: references.data.snapshotId, knownExternalIds: references.data.items.map(item => item.externalId) })
  }, [selections, mappings.data, references.data, put, drop, valueScope])
  useLayoutEffect(() => {
    if (!remoteValues.length) return
    setSelectedRemoteId(current => current && remoteValues.some(item => item.externalId === current) ? current : remoteValues.find(item => Object.values(selections).includes(item.externalId))?.externalId ?? remoteValues[0].externalId)
  }, [remoteValues, selections])
  const asked = new Set(needValueIds ?? [])
  const activeRemoteIds = new Set(remoteValues.map(item => item.externalId))
  const stillNeeded = new Set(localValues.filter(item => asked.has(item.id) && valueStillNeedsMapping(selections[item.id] ?? '', mappingByLocal.get(item.id), references.data?.snapshotId ?? '', externalId => activeRemoteIds.has(externalId))).map(item => item.id))
  const askedValues = localValues.filter(item => asked.has(item.id))
  const orderedLocalValues = askedValues.length ? [...askedValues, ...localValues.filter(item => !asked.has(item.id))] : localValues
  const gridRef = useRef<HTMLDivElement>(null)
  const ready = Boolean(references.data && mappings.data)
  const broughtIntoView = useRef(false)
  useEffect(() => {
    if (!ready || broughtIntoView.current || !asked.size) return
    const first = gridRef.current?.querySelector<HTMLInputElement>('.web-color-panel-value.needs-mapping input')
    if (!first) return
    broughtIntoView.current = true
    first.closest('label')?.scrollIntoView?.({ block: 'nearest' })
    first.focus({ preventScroll: true })
  })
  const selectedLocalIds = new Set(localValues.filter(item => selections[item.id] === selectedRemoteId).map(item => item.id))
  const assignedGroups = remoteValues.map(remote => ({ remote, locals: localValues.filter(value => selections[value.id] === remote.externalId) })).filter(group => group.locals.length > 0)
  const toggleLocalValue = useCallback((localId: string) => {
    if (!selectedRemoteId) return
    setSelections(current => {
      const next = { ...current }
      if (next[localId] === selectedRemoteId) delete next[localId]
      else next[localId] = selectedRemoteId
      return next
    })
  }, [selectedRemoteId])
  if (!localValues.length) return <div className="unknown"><strong>Panel renk değeri bulunamadı</strong><p>Önce Panel Renk özelliğine aktif değerler ekleyin.</p></div>
  if (references.isError) return <div className="unknown"><strong>Güncel Web Color değerleri yok</strong><p>Web Color değer listesini salt-okunur olarak eşitleyin.</p>{notice && <p role="status">{notice}</p>}<button type="button" disabled={sync.isPending} onClick={() => sync.mutate()}>{sync.isPending ? 'Kuyruğa Alınıyor…' : 'Web Color Değerlerini Eşitle'}</button></div>
  if (references.isLoading || mappings.isLoading) return <div className="cm-skeleton cm-webcolor-skeleton" role="status" aria-label="Web Color değerleri ve mevcut eşlemeler yükleniyor">{Array.from({ length: 12 }, (_, index) => <span className="cm-skel-bar" key={index} />)}</div>
  if (mappings.isError) return <ErrorBox error={mappings.error} onRetry={() => void mappings.refetch()} />
  return <div className="web-color-value-editor"><div className="value-mapping-heading">{showGroups ? <strong className="cm-webcolor-title">Kayıtlı Eşlemeler</strong> : <label className="cm-webcolor-pick" htmlFor="mapping-web-color-value">Web Color Değeri<PanelSelect id="mapping-web-color-value" ariaLabel="Web Color değeri" value={selectedRemoteId} onChange={setSelectedRemoteId} options={[{ value: '', label: 'Web Color Seçin' }, ...remoteValues.map(remote => ({ value: remote.externalId, label: remote.name }))]} /></label>}<span className="cm-webcolor-actions"><button type="button" className="secondary" disabled={!assignedGroups.length} onClick={() => setShowGroups(value => !value)}>{showGroups ? 'Eşlemeye Dön' : `Kayıtlı Eşlemeleri Gör (${assignedGroups.length})`}</button></span></div>{notice && <MessageToast message={notice} onClose={() => setNotice('')} />}{!showGroups && <><div className="web-color-panel-value-picker"><div className="web-color-panel-value-picker-head"><strong>Karşılık Gelen Panel Renkleri</strong><span>{selectedLocalIds.size} seçildi</span></div><div ref={gridRef} className="web-color-panel-value-grid">{orderedLocalValues.map(localValue => <WebColorValueBox key={localValue.id} localId={localValue.id} value={localValue.value} selected={selectedLocalIds.has(localValue.id)} needed={stillNeeded.has(localValue.id)} disabled={!selectedRemoteId} onToggle={toggleLocalValue} />)}</div></div></>}{showGroups && assignedGroups.length > 0 && <div className="web-color-assignment-groups"><div className="cm-webcolor-groups-head"><span>Web Color</span><span>Panel Renkleri</span></div><div>{assignedGroups.map(group => <p key={group.remote.externalId}><span>{group.remote.name}</span><small>{group.locals.map(value => value.value).join(' · ')}</small></p>)}</div></div>}</div>
}

const autoQueuedHepsiburadaValueSyncs = new Set<string>()

export function AttributeValueMappingEditor({ connectionId, categoryScope, attribute, externalAttributeId, platformLabel = 'Trendyol', customPanelColorValues = false, marketplaceAllowsFreeText = false, autoSyncMissingValues = false, allowReusablePanelValues = false, required = false }: { connectionId: string; categoryScope: string; attribute: LocalAttribute; externalAttributeId: string; platformLabel?: string; customPanelColorValues?: boolean; marketplaceAllowsFreeText?: boolean; autoSyncMissingValues?: boolean; allowReusablePanelValues?: boolean; required?: boolean }) {
  const client = useQueryClient(); const [notice, setNotice] = useState(''); const [selections, setSelections] = useState<Record<string, string[]>>({}); const [unmatchedOpen, setUnmatchedOpen] = useState(false); const [selectionsReady, setSelectionsReady] = useState(false); const [initializedValueScope, setInitializedValueScope] = useState(''); const [saving, setSaving] = useState(false); const [autoMapping, setAutoMapping] = useState(false); const [quickValueOpen, setQuickValueOpen] = useState(false); const [quickValueDraft, setQuickValueDraft] = useState(''); const [quickValueSaving, setQuickValueSaving] = useState(false); const [editingQuickValue, setEditingQuickValue] = useState<{ id: string; value: string } | null>(null); const [syncJobId, setSyncJobId] = useState<string | null>(null); const [shareWithOtherCategories, setShareWithOtherCategories] = useState(true); const [showUnusedValues, setShowUnusedValues] = useState(false); const handledSyncJobs = useRef(new Set<string>()); const valueScope = `${categoryScope}/${externalAttributeId}`
  const referenceKey = queryKeys.referenceAttributeValues(connectionId, valueScope)
  const references = useQuery({ queryKey: referenceKey, queryFn: ({ signal }) => hubApi<ReferenceData>(`/reference-data/categories/${encodeURIComponent(categoryScope)}/attributes/${encodeURIComponent(externalAttributeId)}/values?connectionId=${encodeURIComponent(connectionId)}`, { signal }), retry: false, retryOnMount: false, enabled: !customPanelColorValues && !marketplaceAllowsFreeText })
  const mappings = useQuery({ queryKey: queryKeys.attributeValueMappings(connectionId, valueScope), queryFn: ({ signal }) => hubApi<CatalogMapping[]>(`/mappings/attribute-values?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(valueScope)}`, { signal }), retry: false, enabled: !customPanelColorValues && !marketplaceAllowsFreeText })
  const [suggestionReview, setSuggestionReview] = useState<ValueMappingSuggestion[]>([])
  // Names the panel values carry on the other marketplaces: extra hints for the suggestions.
  const nameHints = useQuery({ queryKey: queryKeys.marketplaceNameHints(attribute.id, connectionId), queryFn: ({ signal }) => hubApi<ApiAnswer<'getMappingsNameHints'>>(`/mappings/name-hints?attributeId=${encodeURIComponent(attribute.id)}&connectionId=${encodeURIComponent(connectionId)}`, { signal }), retry: false, staleTime: 5 * 60_000, enabled: !customPanelColorValues && !marketplaceAllowsFreeText })
  const usedValues = useQuery({ queryKey: queryKeys.attributeUsedValues(attribute.id), queryFn: ({ signal }) => hubApi<ApiAnswer<'getCatalogAttributesByIdUsedValues'>>(`/catalog/attributes/${attribute.id}/used-values`, { signal }), retry: false, staleTime: 60_000, enabled: !customPanelColorValues && !marketplaceAllowsFreeText })
  const sync = useMutation({ mutationFn: () => hubApi<ApiAnswer<'postConnectionsByConnectionIdReferenceSyncJobs'>>(`/connections/${connectionId}/reference-sync-jobs?resourceType=ATTRIBUTE_VALUES&parentExternalId=${encodeURIComponent(valueScope)}`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: '{}' }), onSuccess: result => { setSyncJobId(result.jobId); setNotice(`${platformLabel} değer listesi salt-okunur olarak eşitleniyor…`) }, onError: reason => setNotice(reason instanceof Error ? reason.message : 'Değer eşitleme başlatılamadı.') })
  const syncJob = useQuery({ queryKey: queryKeys.referenceValueSyncJob(syncJobId), queryFn: ({ signal }) => hubPoll<ApiAnswer<'getJobsById'>>(`/jobs/${syncJobId}`, { cache: 'no-store', signal }), enabled: Boolean(syncJobId), refetchInterval: query => referenceSyncJobPollInterval(query.state.data?.job.status, query.state.status === 'error'), refetchIntervalInBackground: true, retry: 1 })
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
    const referenceQueryKey = queryKeys.referenceAttributeValues(connectionId, valueScope)
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
    setNotice(`${platformLabel} değer eşitlemesi tamamlanamadı${job.lastErrorSummary ? `: ${job.lastErrorSummary}` : job.lastErrorCode ? `; nedeni kaydedilmedi (kod: ${job.lastErrorCode}).` : '; nedeni kaydedilmedi.'}`)
  }, [client, connectionId, platformLabel, valueScope, syncJob.data?.job, syncJobId])
  // The job could not be read (gone, refused, network): stop following it, unlock the button and say so.
  const syncJobReadFailed = syncJob.isError
  useEffect(() => {
    if (!syncJobId || !syncJobReadFailed) return
    setSyncJobId(null)
    setNotice(`${platformLabel} eşitleme işinin durumu okunamadı; iş kaydını İşlem Takibi’nden kontrol edin.`)
  }, [syncJobId, syncJobReadFailed, platformLabel])
  const mappingConnection = useMappingNotificationConnection(connectionId)
  useEffect(() => {
    if (!notice) return
    const kind = notificationKindFromMessage(notice)
    appendNotification({ message: notice, kind, connectionId: mappingConnection.current || undefined, operation: 'Eşleştirme' })
    document.documentElement.dataset.rvMappingFeedback = kind
    const timeout = window.setTimeout(() => setNotice(''), kind === 'info' ? 7000 : 5500)
    return () => window.clearTimeout(timeout)
  }, [notice, mappingConnection])
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
    for (const queryKey of [queryKeys.attributes('mapping-builder'), queryKeys.attributes('mapping')] as const) {
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
      await client.invalidateQueries({ queryKey: queryKeys.attributeValueMappings(connectionId, valueScope) }).catch(() => undefined)
      await client.invalidateQueries({ queryKey: queryKeys.publicationAttributeReadiness() }).catch(() => undefined)
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
      const updated = await hubApi<ApiAnswer<'postCatalogAttributesByIdValues'>>(`/catalog/attributes/${attribute.id}/values`, {
        method: 'POST',
        headers: { 'Idempotency-Key': newIdempotencyKey() },
        body: JSON.stringify(values.map((value, index) => ({ value, sortOrder: localValues.length + index })))
      })
      updateAttributeCache(updated)
      await client.invalidateQueries({ queryKey: queryKeys.attributes() })
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
      const updated = await hubApi<ApiAnswer<'postCatalogAttributesByIdValues'>>(`/catalog/attributes/${attribute.id}/values`, {
        method: 'POST',
        headers: { 'Idempotency-Key': newIdempotencyKey() },
        body: JSON.stringify(missingPanelValues.map((value, index) => ({ value, sortOrder: localValues.length + index })))
      })
      updateAttributeCache(updated)
      await client.invalidateQueries({ queryKey: queryKeys.attributes() })
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
      const updated = await hubApi<ApiAnswer<'putCatalogAttributesByIdValuesByValueId'>>(`/catalog/attributes/${attribute.id}/values/${valueId}`, {
        method: 'PUT',
        headers: { 'If-Match': `"v${attribute.version}"` },
        body: JSON.stringify({ value, sortOrder })
      })
      updateAttributeCache(updated)
      await client.invalidateQueries({ queryKey: queryKeys.attributes() })
      setEditingQuickValue(null)
      setNotice('Değer güncellendi.')
    } catch (reason) {
      setNotice(reason instanceof Error ? reason.message : 'Değer güncellenemedi.')
    } finally { setQuickValueSaving(false) }
  }
  async function removeQuickValue(valueId: string) {
    setQuickValueSaving(true)
    try {
      const updated = await hubApi<ApiAnswer<'deleteCatalogAttributesByIdValuesByValueId'>>(`/catalog/attributes/${attribute.id}/values/${valueId}`, { method: 'DELETE' })
      updateAttributeCache(updated)
      await client.invalidateQueries({ queryKey: queryKeys.attributes() })
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
      await client.invalidateQueries({ queryKey: queryKeys.attributeValueMappings(connectionId, valueScope) })
      await client.invalidateQueries({ queryKey: queryKeys.publicationAttributeReadiness() })
      const summary = `${mappedCount} panel değeri mevcut ${platformLabel} karşılığıyla eşlendi.`
      setNotice(directPlan.ambiguousCount ? `${summary} Adı birden fazla eşleşen ${directPlan.ambiguousCount} seçenek otomatik eşlenmedi.` : summary)
    } catch (reason) {
      await client.invalidateQueries({ queryKey: queryKeys.attributeValueMappings(connectionId, valueScope) })
      await client.invalidateQueries({ queryKey: queryKeys.publicationAttributeReadiness() })
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
  const quickValueModal = quickValueOpen && createPortal(<div className="panel-attribute-modal value-mapping-quick-value-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) closeQuickValueModal() }}><DialogSurface as="section" onClose={() => { closeQuickValueModal() }} className="panel-attribute-modal-card panel-attribute-quick-modal-card" aria-labelledby="mapping-quick-value-title" onMouseDown={event => event.stopPropagation()}><header><div><strong id="mapping-quick-value-title">{attribute.name}<TitleHint label={attribute.name}>Bu başlığa yeni değer ekleyin; birden fazla değeri virgülle ayırın.</TitleHint></strong><small>Panel özelliği</small></div><button type="button" aria-label="Değer ekleme penceresini kapat" onClick={closeQuickValueModal} disabled={quickValueSaving}><UiIcon name="close" /></button></header><form className="panel-attribute-add-value panel-attribute-quick-add-value" onSubmit={event => { event.preventDefault(); void addQuickValues() }}><input data-autofocus value={quickValueDraft} onChange={event => setQuickValueDraft(event.target.value)} placeholder="Yeni değerleri virgülle yazın" aria-label={`${attribute.name} için yeni değerler`} /><button type="submit" disabled={quickValueSaving || !quickValueDraft.trim()}>{quickValueSaving ? 'Ekleniyor…' : '+ Değer Ekle'}</button></form><div className="panel-attribute-value-editor">{localValues.length ? localValues.map(value => <div key={value.id}>{editingQuickValue?.id === value.id ? <input ref={focusOnMount} value={editingQuickValue.value} aria-label={`${value.value} değerini düzenle`} onChange={event => setEditingQuickValue({ id: value.id, value: event.target.value })} /> : <span>{value.value}</span>}<div>{editingQuickValue?.id === value.id ? <><button type="button" aria-label={`${value.value} değerini kaydet`} disabled={quickValueSaving} onClick={() => void updateQuickValue(value.id, editingQuickValue.value, value.sortOrder)}><UiIcon name="check" /></button><button type="button" aria-label="Değer düzenlemeyi iptal et" disabled={quickValueSaving} onClick={() => setEditingQuickValue(null)}><UiIcon name="close" /></button></> : <><button type="button" aria-label={`${value.value} değerini düzenle`} disabled={quickValueSaving} onClick={() => setEditingQuickValue({ id: value.id, value: value.value })}><UiIcon name="edit" /></button><button type="button" aria-label={`${value.value} değerini sil`} disabled={quickValueSaving} onClick={() => void removeQuickValue(value.id)}><UiIcon name="close" /></button></>}</div></div>) : <span className="panel-value-empty">Henüz değer eklenmedi.</span>}</div><footer><button type="button" className="secondary" onClick={closeQuickValueModal} disabled={quickValueSaving}>Kapat</button></footer></DialogSurface></div>, document.body)
  if (references.isError) return <div className="unknown"><strong>Güncel özellik değerleri listesi yok</strong><p>Seçili kategori ve özellik için pazar yerinin salt-okunur değer listesini eşitleyin.</p>{notice && <p role="status">{notice}</p>}<div className="value-mapping-heading-actions"><button type="button" className="secondary" disabled={quickValueSaving} onClick={() => setQuickValueOpen(true)}>Hızlı Değer Oluştur</button><button type="button" disabled={syncBusy} onClick={() => sync.mutate()}>{syncBusy ? 'Eşitleniyor…' : `${platformLabel} Değerlerini Eşitle`}</button></div>{quickValueModal}</div>
  if (references.isLoading || mappings.isLoading) return <Busy text="Özellik değerleri ve mevcut eşlemeler yükleniyor…" />
  if (mappings.isError) return <ErrorBox error={mappings.error} onRetry={() => void mappings.refetch()} />
  return <div className="mapping-step nested value-mapping-editor">
    <div className="value-mapping-heading">
      <div><h3>Değer Eşleştirmeleri{allowReusablePanelValues ? <TitleHint label="Değer Eşleştirmeleri">{platformLabel} değerlerini karşılık gelen panel değerleriyle eşleyin. Aynı panel değeri birden fazla Hepsiburada seçeneği için kullanılabilir.</TitleHint> : null}</h3></div>
    </div>
    <div className="value-mapping-commandbar" role="toolbar" aria-label="Değer eşleme işlemleri">
      <div className="value-mapping-command-group" role="group" aria-label="Otomatik eşleme">
        <button type="button" className="secondary" title={`Eşleşmemiş panel değerleri için ${platformLabel} değeri seçer: aynı ad, harf farkı ve eş anlamlılar (ör. Black → Siyah) doğrudan eşlenir, zayıf benzerlikler onayınıza sunulur.`} disabled={!selectionsReady || autoMapping || saving || syncBusy || !unmatchedPanelValues.length || !activeRemoteValues.length} onClick={suggestMappings}><UiIcon name="sparkle" />Önerileri Getir</button>
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
