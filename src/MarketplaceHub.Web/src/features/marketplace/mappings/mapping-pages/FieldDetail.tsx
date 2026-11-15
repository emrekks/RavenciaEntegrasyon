import { memo, useCallback, useContext, useDeferredValue, useEffect, useMemo, useRef, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../../../shared/api'
import { ErrorBox } from '../../../../shared/components'
import { ValuePicker, pickerOptions, type PickerOption } from '../ValuePicker'
import { valueListQuery, valueMappingsQuery } from '../value-field-queries'
import { groupSavedSelections, rebaseSelections, selectionsDiffer } from '../value-drafts'
import { usesCustomPanelColorValue } from '../../color-value-format'
import { type AttributeDefault } from '../../AttributeDefaultsPanel'
import { offersProductFields, showsProductFields, parseProductFieldOption, productFieldLabel, productFieldOptionValue, productFieldSources } from '../../product-field-sources'
import { planDirectReferenceValues } from '../../attribute-value-mapping'
import { type MappingPlatformCode } from '../../mapping-platforms'
import { MappingStateGlyph } from '../../shared'
import { valueStillNeedsMapping } from '../../../catalog/value-mapping-rule'
import { PanelSelect } from '../../../../shared/PanelSelect'
import { EmptyState } from '../../../../shared/components/EmptyState'
import { queryKeys } from '../../../../shared/api/query-keys'
import { type LocalAttribute, type ReferenceItem, type ReferenceData, type CatalogMapping, isOptionAttribute, ValueDraftContext, valueCollator } from './mapping-shared'
import { AttributeValueMappingEditor } from './ValueMappingEditors'

export function ProductBrandField({ remote, required, platformLabel, footerHint, onNext }: { remote: ReferenceItem; required: boolean; platformLabel: string; footerHint: string; onNext?: () => void }) {
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
export function FieldHeading({ name, required }: { name: string; required: boolean; platformLabel?: string }) {
  return <>
    <div className="cm-detail-head"><h4>{name}</h4><span className={`cm-tag${required ? ' is-required' : ''}`}>{required ? 'Zorunlu' : 'İsteğe Bağlı'}</span></div>
  </>
}
export function FieldFooter({ hint, dirty = false, saving = false, onSave, onNext }: { hint: string; dirty?: boolean; saving?: boolean; onSave?: () => void; onNext?: () => void }) {
  // Kaydet saves the unsaved value choices of every field of the window (ValueDraftContext).
  const store = useContext(ValueDraftContext)
  const pending = store?.drafts.size ?? 0
  const unsaved = dirty || pending > 0
  const busy = saving || store?.saving === true
  const save = onSave ?? (pending ? () => void store?.saveAll() : undefined)
  return <div className={`cm-foot${unsaved ? ' is-dirty' : ''}`}>
    <span>{unsaved ? pending > 1 ? `${pending} alanda kaydedilmemiş değişiklik var` : 'Kaydedilmemiş değişiklikler var' : hint}</span>
    {/* The status legend sits in the footer row instead of a row of its own under the window. */}
    <span className="cm-legend" aria-label="Durum simgeleri"><span><MappingStateGlyph state="done" /> Tamam</span><span><MappingStateGlyph state="partial" /> Değerleri Eksik</span><span><MappingStateGlyph state="none" /> Eşlenmedi</span></span>
    <span className="cm-grow" />
    {onNext && <button type="button" className="secondary" onClick={onNext}>Sonraki Eksik</button>}
    <button type="button" disabled={!save || !unsaved || busy} onClick={save}>{busy ? 'Kaydediliyor…' : 'Kaydet'}</button>
  </div>
}
// One marketplace field: which panel attribute feeds it, then a table of the attribute's values.
export function FieldDetail({ platformCode, platformLabel, connectionId, categoryScope, snapshotId, localAttributes, categoryOptionIds, remote, mapping, fieldDefault, role: fixedRole, required, footerHint, onNext, onNotice, highlightValueIds }: { platformCode: MappingPlatformCode; platformLabel: string; connectionId: string; categoryScope: string; snapshotId: string; localAttributes: LocalAttribute[]; categoryOptionIds: Set<string>; remote: ReferenceItem; mapping: CatalogMapping | null; fieldDefault?: AttributeDefault; role?: 'ATTRIBUTE' | 'OPTION'; required: boolean; footerHint: string; onNext?: () => void; onNotice: (value: string) => void; highlightValueIds?: readonly string[] }) {
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
      await client.invalidateQueries({ queryKey: queryKeys.attributeMappings(connectionId, categoryScope) })
      await client.invalidateQueries({ queryKey: queryKeys.attributeDefaults(connectionId) })
      await client.invalidateQueries({ queryKey: queryKeys.publicationAttributeReadiness() })
      setBusy(false)
    }
  }
  return <>
    <FieldHeading name={remote.name} required={required} platformLabel={platformLabel} />
    <div className="cm-form">
      <div className="cm-field"><small>{platformLabel} Alanı</small><span className="cm-readonly">{remote.name}</span></div>
      <span className="cm-link" aria-hidden="true"><svg viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round"><path d="M6.6 9.4 9.4 6.6" /><path d="M7.4 4.6 8.6 3.4a2.6 2.6 0 0 1 3.7 3.7l-1.2 1.2" /><path d="M8.6 11.4 7.4 12.6a2.6 2.6 0 0 1-3.7-3.7l1.2-1.2" /></svg></span>
      <div className="cm-field"><small>Panel Özelliği</small><div className="cm-select-row">
        <PanelSelect ariaLabel={`${remote.name} için panel özelliği`} value={selectedValue} disabled={busy} onChange={next => void choose(next)} width={280} options={[
          { value: '', label: 'Seçin…' },
          ...(showsProductFields(platformCode) ? [{ value: '__group:product', label: <strong>Ürün Bilgileri</strong>, text: 'Ürün Bilgileri', disabled: true }, ...productFieldSources.map(item => ({ value: productFieldOptionValue(item.value), label: productFields ? item.label : `${item.label} (bu alan listeden seçilir)`, disabled: !productFields }))] : []),
          ...(panelOptions.length > 0 ? [{ value: '__group:options', label: <strong>Panel Seçenekleri</strong>, text: 'Panel Seçenekleri', disabled: true }, ...panelOptions.map(item => ({ value: item.id, label: item.name }))] : []),
          ...(panelAttributes.length > 0 ? [{ value: '__group:attributes', label: <strong>Panel Özellikleri</strong>, text: 'Panel Özellikleri', disabled: true }, ...panelAttributes.map(item => ({ value: item.id, label: item.name }))] : []),
        ]} />
      </div></div>
      <button type="button" className="secondary cm-quiet cm-unlink" disabled={(!mapping && !fieldSource) || busy} onClick={() => void choose('')}>Kaldır</button>
    </div>
    {fieldSource ? <><p className="cm-empty">Gönderimde her varyantın kendi {productFieldLabel(fieldSource)} bilgisi yazılır; boş olan varyantta gönderim durur ve hangi ürün olduğu söylenir. Bu seçim {platformLabel} bağlantısının bütün kategorilerinde geçerlidir.</p><FieldFooter hint={footerHint} onNext={onNext} /></>
      : !mapping || !selected ? <><p className="cm-empty">Bir panel özelliği seçtiğinizde değerleri burada eşlersiniz.</p><FieldFooter hint={footerHint} onNext={onNext} /></>
      : !values.length ? <><p className="cm-empty">“{selected.name}” özelliğinde henüz değer yok. Değerleri özellik kütüphanesinden ekleyin.</p><FieldFooter hint={footerHint} onNext={onNext} /></>
      : customPanelColorValues || remote.allowsCustomValue === true ? <><div className="cm-legacy-card"><AttributeValueMappingEditor connectionId={connectionId} categoryScope={categoryScope} attribute={selected} externalAttributeId={remote.externalId} platformLabel={platformLabel} customPanelColorValues={customPanelColorValues} marketplaceAllowsFreeText={remote.allowsCustomValue === true} required={required} /></div><FieldFooter hint={footerHint} onNext={onNext} /></>
      : <FieldValueTable key={`${categoryScope}/${remote.externalId}/${selected.id}`} platformCode={platformCode} platformLabel={platformLabel} connectionId={connectionId} categoryScope={categoryScope} attribute={selected} remote={remote} required={required} footerHint={footerHint} onNext={onNext} onNotice={onNotice} highlightValueIds={highlightValueIds} />}
  </>
}

// The marketplace value a panel value is sent as, for every panel value of a selection (the first one, as `targetOf`).
function targetsByLocal(current: Record<string, string[]>) {
  const targets = new Map<string, string>()
  for (const [externalId, localIds] of Object.entries(current)) for (const localId of localIds) if (!targets.has(localId)) targets.set(localId, externalId)
  return targets
}
// One row of the value table. Memoised: a pick re-renders its own row, not the whole table (230 rows for Menşei).
const ValueRow = memo(function ValueRow({ localId, value, target, needed, options, label, onPick }: { localId: string; value: string; target: string; needed: boolean; options: readonly PickerOption[]; label: string; onPick: (localId: string, externalId: string) => void }) {
  return <div className={`cm-vrow${target ? '' : ' is-open'}${needed ? ' is-product-issue needs-mapping' : ''}`} role="row">
    <span className="cm-pv" role="cell">{value}{needed && <span className="cm-product-tag" title="Bu ürünün yayını için bu değerin eşlemesi gerekiyor">Eşleşmesi gereken</span>}</span>
    <span role="cell"><ValuePicker value={target} options={options} label={label} onChange={externalId => onPick(localId, externalId)} /></span>
    <span className="cm-vstate" role="cell"><MappingStateGlyph state={target ? 'done' : 'none'} /></span>
  </div>
})
// The value table's own shape while its lists are read: the same tools, header, rows and footer, so nothing moves when
// the values arrive (it was the full-height loading splash, then the table).
function ValueTableSkeleton({ platformLabel, footerHint, onNext }: { platformLabel: string; footerHint: string; onNext?: () => void }) {
  return <>
    <div className="cm-vtools" aria-hidden="true">
      <div className="cm-vtools-title"><h5>Değer Eşlemeleri</h5><span>… / …</span></div>
      <div className="cm-vtools-actions">
        <input className="cm-search" type="search" placeholder="Değer ara" disabled tabIndex={-1} />
        <button type="button" className="secondary cm-toggle" disabled tabIndex={-1}>Yalnızca Eksikler</button>
        <button type="button" className="secondary" disabled tabIndex={-1}>Birebir Eşle</button>
      </div>
    </div>
    <div className="cm-vtable cm-skeleton" role="status" aria-label="Değerler ve eşlemeler yükleniyor">
      <div className="cm-vrow is-head" aria-hidden="true"><span>Panel Değeri</span><span>{platformLabel} Değeri</span><span /></div>
      <div className="cm-vscroll" aria-hidden="true">{Array.from({ length: 8 }, (_, index) => <div className="cm-vrow" key={index}><span className="cm-skel-bar" /><span className="cm-skel-bar is-picker" /><span /></div>)}</div>
    </div>
    <FieldFooter hint={footerHint} onNext={onNext} />
  </>
}
// Panel values down the side, the marketplace value each one is sent as on the right.
// `highlightValueIds`: the panel values a product's publish check found without a value mapping. They are marked, come
// first, and the table opens on "Yalnızca Eksikler" (which keeps them in view even once chosen).
function FieldValueTable({ platformCode, platformLabel, connectionId, categoryScope, attribute, remote, required, footerHint, onNext, onNotice, highlightValueIds = [] }: { platformCode: MappingPlatformCode; platformLabel: string; connectionId: string; categoryScope: string; attribute: LocalAttribute; remote: ReferenceItem; required: boolean; footerHint: string; onNext?: () => void; onNotice: (value: string) => void; highlightValueIds?: readonly string[] }) {
  const store = useContext(ValueDraftContext)
  const valueScope = `${categoryScope}/${remote.externalId}`
  const references = useQuery(valueListQuery<ReferenceData>(connectionId, categoryScope, remote.externalId))
  const mappings = useQuery(valueMappingsQuery<CatalogMapping[]>(connectionId, valueScope))
  // A field whose saved pairs are in the cache (prefetched from the list) starts filled, without an empty first frame.
  const [selections, setSelections] = useState<Record<string, string[]> | null>(() => store?.drafts.get(valueScope)?.selections ?? (mappings.data ? groupSavedSelections(mappings.data) : null))
  // The saved pairs `selections` started from; when the server's pairs move on, the untouched values follow them.
  const [baseline, setBaseline] = useState<Record<string, string[]> | null>(() => { const draft = store?.drafts.get(valueScope); return draft ? groupSavedSelections(draft.saved) : mappings.data ? groupSavedSelections(mappings.data) : null })
  const [search, setSearch] = useState('')
  // Typing stays immediate; the rows follow when the browser has time.
  const deferredSearch = useDeferredValue(search)
  const highlighted = new Set(highlightValueIds)
  const [onlyMissing, setOnlyMissing] = useState(() => highlightValueIds.length > 0)
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
  // After Kaydet every field starts again from what the server holds, but one whose save was partly refused.
  if (seenGeneration !== generation) { setSeenGeneration(generation); if (!store?.kept.has(valueScope)) { setSelections(null); setBaseline(null) } }
  const saved = useMemo(() => groupSavedSelections(mappings.data ?? []), [mappings.data])
  useEffect(() => { if (mappings.data && selections === null) { const grouped = groupSavedSelections(mappings.data); setSelections(grouped); setBaseline(grouped) } }, [mappings.data, selections])
  // The saved pairs changed after the table was filled (Tüm Değerleri Eşle, another window).
  if (mappings.data && selections !== null && baseline !== null && selectionsDiffer(saved, baseline)) { setSelections(rebaseSelections(selections, baseline, saved)); setBaseline(saved) }
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
  const localValues = useMemo(() => attribute.values.filter(item => item.isActive).slice().sort((left, right) => valueCollator.compare(left.value, right.value)), [attribute.values])
  const dirty = selections !== null && selectionsDiffer(selections, saved)
  const putDraft = store?.put, dropDraft = store?.drop
  const snapshotId = references.data?.snapshotId ?? ''
  useEffect(() => {
    if (!selections || !mappings.data || !snapshotId) return
    if (dirty) putDraft?.({ valueScope, fieldName: remote.name, selections, saved: mappings.data, snapshotId, knownExternalIds: remoteValues.map(item => item.externalId) })
    else dropDraft?.(valueScope)
  }, [dirty, selections, mappings.data, snapshotId, remoteValues, valueScope, remote.name, putDraft, dropDraft])
  // The first value the product still needs is brought into view and its picker focused, once the table is in.
  const tableRef = useRef<HTMLDivElement>(null)
  const broughtIntoView = useRef(false)
  useEffect(() => {
    if (broughtIntoView.current || !highlightValueIds.length) return
    const first = tableRef.current?.querySelector<HTMLElement>('.cm-vrow.needs-mapping')
    if (!first) return
    broughtIntoView.current = true
    first.scrollIntoView?.({ block: 'nearest' })
    first.querySelector<HTMLElement>('button, select, input')?.focus({ preventScroll: true })
  })
  const setTarget = useCallback((localId: string, externalId: string) => {
    setSelections(current => {
      const base = current ?? {}
      const previous = targetsByLocal(base).get(localId) ?? ''
      const next: Record<string, string[]> = {}
      for (const [key, ids] of Object.entries(base)) {
        const kept = allowReuse && key !== previous ? ids : ids.filter(id => id !== localId)
        if (kept.length) next[key] = kept
      }
      if (externalId) next[externalId] = [...(next[externalId] ?? []).filter(id => id !== localId), localId]
      return next
    })
  }, [allowReuse])
  if (valuesUnavailable || references.isError) return <><div className="cm-legacy-card"><AttributeValueMappingEditor connectionId={connectionId} categoryScope={categoryScope} attribute={attribute} externalAttributeId={remote.externalId} platformLabel={platformLabel} autoSyncMissingValues={remote.allowsCustomValue !== true} allowReusablePanelValues={allowReuse} required={required} /></div><FieldFooter hint={footerHint} onNext={onNext} /></>
  if (mappings.isError) return <ErrorBox error={mappings.error} onRetry={() => void mappings.refetch()} />
  if (references.isLoading || mappings.isLoading || !selections || !references.data || !mappings.data) return <ValueTableSkeleton platformLabel={platformLabel} footerHint={footerHint} onNext={onNext} />
  const targets = targetsByLocal(selections)
  const doneCount = localValues.filter(item => targets.has(item.id)).length
  // The product's values still without a mapping, by the publish check's own rule (a pick not saved yet counts).
  const activeRemoteIds = new Set(references.data.items.filter(item => item.isActive).map(item => item.externalId))
  const stillNeeded = new Set(localValues.filter(item => highlighted.has(item.id)).filter(item => {
    const target = targets.get(item.id) ?? ''
    const saved = mappings.data!.find(mapping => mapping.localId === item.id && mapping.externalId === target) ?? mappings.data!.find(mapping => mapping.localId === item.id)
    return valueStillNeedsMapping(target, saved, references.data!.snapshotId, externalId => activeRemoteIds.has(externalId))
  }).map(item => item.id))
  const query = deferredSearch.trim().toLocaleLowerCase('tr-TR')
  const rows = localValues
    .filter(item => (!query || item.value.toLocaleLowerCase('tr-TR').includes(query)) && (!onlyMissing || !targets.has(item.id) || highlighted.has(item.id)))
    .sort((left, right) => Number(highlighted.has(right.id)) - Number(highlighted.has(left.id)))
  function matchSameNames() {
    const plan = planDirectReferenceValues(localValues, references.data!.items.filter(item => item.isActive))
    const open = plan.mappings.filter(item => !targets.has(item.localId))
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
    <div ref={tableRef} className="cm-vtable" role="table" aria-label={`${remote.name} değer eşlemeleri`}>
      <div className="cm-vrow is-head" role="row"><span role="columnheader">Panel Değeri</span><span role="columnheader">{platformLabel} Değeri</span><span aria-hidden="true" /></div>
      <div className="cm-vscroll">
        {rows.length ? rows.map(item => <ValueRow key={item.id} localId={item.id} value={item.value} target={targets.get(item.id) ?? ''} needed={stillNeeded.has(item.id)} options={options} label={`${item.value} için ${platformLabel} değeri`} onPick={setTarget} />)
          : <EmptyState icon="tag" title={onlyMissing ? 'Eşlenmemiş Değer Yok' : 'Değer Bulunamadı'}>{onlyMissing ? 'Eşlenmemiş değer kalmadı.' : 'Aramaya uyan değer yok.'}</EmptyState>}
      </div>
    </div>
    <FieldFooter hint={footerHint} dirty={dirty} onNext={onNext} />
  </>
}
