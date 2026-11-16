import type { ApiAnswer } from '../../../../shared/api/api-answer'
import { useRememberedHeight } from '../../../../shared/remembered-height'
import { useEffect, useMemo, useState, type ChangeEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiRequestError, hubApi, loadAllPages } from '../../../../shared/api'
import { ErrorBox, TitleHint, UiIcon, useConfirmDialog, DialogSurface } from '../../../../shared/components'
import { isStaleMappingError, mappingSaveErrorMessage, refreshLocalBrands, refreshMappingConflicts } from '../mapping-refresh'
import { brandMappingImportMessage, importBrandMappingItems, type BrandImportFailure } from '../brand-mapping-import'
import { newIdempotencyKey } from '../../../../shared/idempotency'
import { MessageToast } from '../../../../shared/components/NoticeToast'
import { mappingStatusLabel } from '../../../../shared/status-labels'
import { platformLogoClass, platformLogoSource } from '../../../../shared/platform-logos'
import { MappingPlatformTab } from '../MappingPlatformTab'
import { appendNotification } from '../../../../shared/notifications'
import { mappingPlatformDefinitions, mappingPlatformLabel, type MappingPlatformCode } from '../../mapping-platforms'
import { formatDateTime } from '../../../../shared/format-date'
import { Connection, MappingStateGlyph, notificationKindFromMessage } from '../../shared'
import { brandPlatformConnectionId } from '../brand-mapping-state'
import { BrandReferencePicker, BrandReferenceSelect, brandReferenceKey, lookupBrandReferences, useBrandReferenceLookup, useBrandReferenceSummary, type BrandReferenceItem, type BrandReferenceSlice } from '../brand-reference'
import { PanelSelect } from '../../../../shared/PanelSelect'
import { EmptyState } from '../../../../shared/components/EmptyState'
import { connectionQueries, queryKeys } from '../../../../shared/api/query-keys'
import { useMappingNotificationConnection, MappingDeleteIcon, type CatalogMapping, SearchableSelect, MappingViewTabs, wait } from './mapping-shared'

type LocalBrand = ApiAnswer<'getCatalogBrands'>['items'][number]

type BrandMappingTransferBundle = { format: 'RAVENCIA_BRAND_MAPPING_BUNDLE'; version: 1; exportedAt: string; brands: LocalBrand[]; mappings: CatalogMapping[] }

const autoQueuedBrandSyncs = new Set<string>()

// Only Trendyol and Pazarama keep a brand list to map against. The other marketplaces take the brand by name: the
// panel brand is sent as it is, so card 2 explains that instead of offering a mapping.
const brandListPlatformCodes: readonly MappingPlatformCode[] = ['TRENDYOL', 'PAZARAMA']
const brandAutoMatchLocative: Partial<Record<MappingPlatformCode, string>> = { HEPSIBURADA: "Hepsiburada'da", N11: "N11'de", SHOPIFY: "Shopify'da" }
type BrandMappingSaveInput = { connectionId: string; snapshotId: string; localId: string; externalId: string; version?: number }

export function BrandMappingPage() {
  const [confirm, confirmDialog] = useConfirmDialog()
  const client = useQueryClient(); const [connectionChoice, setConnectionChoice] = useState(''); const [localId, setLocalId] = useState(''); const [externalId, setExternalId] = useState(''); const [notice, setNotice] = useState(''); const [brandName, setBrandName] = useState(''); const [pickedBrand, setPickedBrand] = useState<BrandReferenceItem | null>(null); const [editingPickedBrand, setEditingPickedBrand] = useState<BrandReferenceItem | null>(null); const [brandEditOpen, setBrandEditOpen] = useState(false); const [editingBrandMappingId, setEditingBrandMappingId] = useState(''); const [brandTransferBusy, setBrandTransferBusy] = useState(false); const [brandTransferBundle, setBrandTransferBundle] = useState<BrandMappingTransferBundle | null>(null); const [brandTransferOpen, setBrandTransferOpen] = useState(false); const [brandTransferSelection, setBrandTransferSelection] = useState({ brands: true, mappings: true })
  const [brandPlatformCode, setBrandPlatformCode] = useState<MappingPlatformCode>('TRENDYOL')
  // Card 3 lists saved mappings of a brand-list platform; it keeps the last one card 2 chose when card 2 moves to a platform without a list.
  const [savedBrandPlatformChoice, setSavedBrandPlatformChoice] = useState<MappingPlatformCode>('TRENDYOL')
  const [lastListConnectionId, setLastListConnectionId] = useState('')
  const [editingExternalId, setEditingExternalId] = useState('')
  const connections = useQuery(connectionQueries.list<Connection>())
  // Brand lists exist on Trendyol and Pazarama; the other marketplaces take the brand by its name.
  const brandPlatformActive = (code: MappingPlatformCode) => connections.data?.items.some(item => item.platformCode === code && (item.status === 'ACTIVE' || item.status === 'VERIFIED')) ?? false
  const brandMappingSupported = brandListPlatformCodes.includes(brandPlatformCode)
  const savedTabPlatforms = mappingPlatformDefinitions.filter(platform => brandListPlatformCodes.includes(platform.code))
  const savedBrandPlatformCode = savedTabPlatforms.some(platform => platform.code === savedBrandPlatformChoice && brandPlatformActive(platform.code)) ? savedBrandPlatformChoice : savedTabPlatforms.find(platform => brandPlatformActive(platform.code))?.code ?? savedBrandPlatformChoice
  const savedBrandPlatformLabel = mappingPlatformLabel(savedBrandPlatformCode)
  const brandPlatformLabel = mappingPlatformLabel(brandPlatformCode)
  const platformConnections = useMemo(() => connections.data?.items.filter(item => item.platformCode === brandPlatformCode && (item.status === 'ACTIVE' || item.status === 'VERIFIED')) ?? [], [connections.data, brandPlatformCode])
  const connectionId = brandPlatformConnectionId(platformConnections, connectionChoice)
  const mappingConnection = useMappingNotificationConnection(connectionId)
  useEffect(() => {
    if (!notice) return
    const kind = notificationKindFromMessage(notice)
    appendNotification({ message: notice, kind, connectionId: mappingConnection.current || undefined, operation: 'Eşleştirme' })
    document.documentElement.dataset.rvMappingFeedback = kind
    const timeout = window.setTimeout(() => setNotice(''), kind === 'info' ? 7000 : 5500)
    return () => window.clearTimeout(timeout)
  }, [notice, mappingConnection])
  const brandPlatformTabs = <div className="mapping-saved-tabs mapping-platform-tabs rv-tabs rv-view-tabs" role="tablist" aria-label="Marka eşleştirme platformları">{mappingPlatformDefinitions.map(platform => <MappingPlatformTab key={platform.code} code={platform.code} label={platform.label} available={brandPlatformActive(platform.code)} selected={brandPlatformCode === platform.code} activeTitle={`${platform.label} markalarını göster`} onSelect={() => chooseBrandPlatform(platform.code)} />)}</div>

  const savedBrandPlatformTabs = <div className="mapping-saved-tabs mapping-platform-tabs rv-tabs rv-view-tabs" role="tablist" aria-label="Kayıtlı marka eşleştirme platformları">{savedTabPlatforms.map(platform => <MappingPlatformTab key={platform.code} code={platform.code} label={platform.label} available={brandPlatformActive(platform.code)} selected={savedBrandPlatformCode === platform.code} activeTitle={`${platform.label} markalarını göster`} onSelect={() => chooseBrandPlatform(platform.code)} />)}</div>

  function chooseBrandPlatform(code: MappingPlatformCode) {
    setBrandPlatformCode(code); setConnectionChoice(''); setLocalId(''); setExternalId(''); setNotice('')
    if (brandListPlatformCodes.includes(code)) { setSavedBrandPlatformChoice(code); setEditingBrandMappingId('') }
  }
  const localBrands = useQuery({ queryKey: queryKeys.brands('mapping'), queryFn: ({ signal }) => loadAllPages<LocalBrand>('/catalog/brands', { signal }) })
  const brandListHeight = useRememberedHeight<HTMLDivElement>('brand-library', localBrands.isLoading)
  // The brand list's size and age only; the pickers search it on the server and the tables look their ids up.
  const references = useBrandReferenceSummary(connectionId, brandMappingSupported)
  // The first read of a platform's list: the footer and the picker keep their shape and wording until it answers.
  const referencesPending = !!connectionId && references.isPending && !references.isError
  const mapping = useQuery({ queryKey: queryKeys.brandMapping(localId, connectionId), queryFn: ({ signal }) => hubApi<CatalogMapping | null>(`/mappings/brands/${localId}?connectionId=${encodeURIComponent(connectionId)}`, { signal }), enabled: brandMappingSupported && !!localId && !!connectionId, retry: false })
  const savedPlatformConnections = connections.data?.items.filter(item => item.platformCode === savedBrandPlatformCode && (item.status === 'ACTIVE' || item.status === 'VERIFIED')) ?? []
  // Card 3 reads the connection card 2 uses when both show the same platform; otherwise the last one picked there, or the first.
  const savedConnectionId = brandMappingSupported && brandPlatformCode === savedBrandPlatformCode ? connectionId : savedPlatformConnections.find(item => item.id === lastListConnectionId)?.id ?? savedPlatformConnections[0]?.id ?? ''
  // Same query keys as card 2, so when both cards show one connection the cache answers both.
  const savedReferences = useBrandReferenceSummary(savedConnectionId)
  const brandMappings = useQuery({ queryKey: queryKeys.brandMappings(savedConnectionId), queryFn: ({ signal }) => hubApi<CatalogMapping[]>(`/mappings/brands?connectionId=${encodeURIComponent(savedConnectionId)}`, { signal }), enabled: !!savedConnectionId, retry: false })
  // Marketplace brands the product import met that no panel brand is mapped to: the import never maps by name, so
  // their products came in without a brand until the user maps them here.
  const unmappedBrands = useQuery({ queryKey: queryKeys.brandUnmapped(connectionId), queryFn: ({ signal }) => hubApi<ApiAnswer<'getReferenceDataBrandsUnmapped'>>(`/reference-data/brands/unmapped?connectionId=${encodeURIComponent(connectionId)}`, { signal }), enabled: brandMappingSupported && !!connectionId, retry: false })
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
      const summaryKey = [...brandReferenceKey(syncedConnectionId), 'summary']
      const fetchedBefore = client.getQueryData<BrandReferenceSlice>(summaryKey)?.fetchedAt
      void (async () => {
        for (const delay of [500, 1000, 2000, 4000, 8000, 15000]) {
          await wait(delay)
          await client.invalidateQueries({ queryKey: summaryKey })
          const current = client.getQueryData<BrandReferenceSlice>(summaryKey)
          if (current && current.fetchedAt !== fetchedBefore && !client.getQueryState(summaryKey)?.error) break
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
    onSuccess: async (value, input) => { setNotice('Marka eşlemesi doğrulandı ve kaydedildi.'); if (input.connectionId === connectionId && input.localId === localId) setExternalId(value.externalId); setBrandEditOpen(false); setEditingBrandMappingId(''); client.setQueryData(queryKeys.brandMapping(input.localId, input.connectionId), value); client.setQueryData<CatalogMapping[]>(queryKeys.brandMappings(input.connectionId), current => current ? [...current.filter(item => item.localId !== value.localId), value] : [value]); await Promise.all([client.invalidateQueries({ queryKey: queryKeys.brandMapping(input.localId, input.connectionId) }), client.invalidateQueries({ queryKey: queryKeys.brandMappings(input.connectionId) }), client.invalidateQueries({ queryKey: queryKeys.brandUnmapped(input.connectionId) })]) },
    onError: async (reason, input) => { setNotice(mappingSaveErrorMessage(reason, 'Eşleme kaydedilemedi.')); if (isStaleMappingError(reason)) await Promise.all([client.invalidateQueries({ queryKey: queryKeys.brandMapping(input.localId, input.connectionId) }), client.invalidateQueries({ queryKey: queryKeys.brandMappings(input.connectionId) })]) },
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
  const createBrand = useMutation({ mutationFn: () => { if (!brandName.trim()) throw new Error('Marka adı zorunludur.'); return hubApi<ApiAnswer<'postCatalogBrands'>>('/catalog/brands', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ name: brandName.trim() }) }) }, onSuccess: async brand => { await refreshLocalBrands(client); setLocalId(brand.id); setExternalId(''); setBrandName(''); setNotice(`“${brand.name}” panel markası oluşturuldu ve seçildi.`) }, onError: reason => setNotice(reason instanceof Error ? reason.message : 'Marka oluşturulamadı.') })
  // An existing mapping fills the platform brand; with none, a platform brand picked before the panel brand stays.
  useEffect(() => { if (mapping.data) setExternalId(mapping.data.externalId) }, [mapping.data])
  const activeLocalBrands = localBrands.data?.items.filter(item => item.isActive) ?? []
  const selectedBrandIds = useMemo(() => externalId ? [externalId] : [], [externalId])
  const selectedBrandLookup = useBrandReferenceLookup(connectionId, references.data, selectedBrandIds)
  const selectedExternalName = (pickedBrand?.externalId === externalId ? pickedBrand.name : undefined) ?? selectedBrandLookup.byId.get(externalId)?.name ?? (selectedBrandLookup.isLoading ? 'Yükleniyor…' : undefined)
  const savedBrandMappings = useMemo(() => brandMappings.data ?? [], [brandMappings.data])
  const savedBrandIds = useMemo(() => savedBrandMappings.map(item => item.externalId), [savedBrandMappings])
  const savedBrandLookup = useBrandReferenceLookup(savedConnectionId, savedReferences.data, savedBrandIds)
  // The names wait for the platform's brand list too; the raw id shows only when a list or a lookup cannot answer.
  const savedNamesLoading = savedBrandLookup.isLoading || (!!savedConnectionId && savedReferences.isPending && !savedReferences.isError)
  const savedBrandName = (id: string) => savedBrandLookup.byId.get(id)?.name
  const editingExternalName = (editingPickedBrand?.externalId === editingExternalId ? editingPickedBrand.name : undefined) ?? savedBrandName(editingExternalId) ?? (savedNamesLoading ? 'Yükleniyor…' : undefined)
  const savedMappingsPending = !!savedConnectionId && brandMappings.isPending && !brandMappings.isError
  const savedMappingCount = savedMappingsPending ? <span className="brand-reference-skeleton is-count" aria-hidden="true" /> : savedBrandMappings.length.toLocaleString('tr-TR')
  const localBrandById = new Map(activeLocalBrands.map(item => [item.id, item]))
  const archiveBrand = useMutation({ mutationFn: (brand: LocalBrand) => hubApi<ApiAnswer<'patchCatalogBrandsById'>>(`/catalog/brands/${brand.id}`, { method: 'PATCH', headers: { 'If-Match': `"v${brand.version}"` }, body: JSON.stringify({ name: brand.name, isActive: false }) }), onSuccess: async (_, brand) => { if (localId === brand.id) { setLocalId(''); setExternalId('') }; await refreshLocalBrands(client); setNotice(`“${brand.name}” panel markalarından kaldırıldı.`) }, onError: reason => setNotice(reason instanceof Error ? reason.message : 'Marka kaldırılamadı.') })
  const removeMapping = useMutation({ mutationFn: async (item: CatalogMapping) => { const current = await hubApi<CatalogMapping | null>(`/mappings/brands/${item.localId}?connectionId=${encodeURIComponent(savedConnectionId)}`); if (!current || current.externalId !== item.externalId) throw new Error('Bu marka eşleştirmesi bu sırada değişti veya kaldırıldı. Liste yenilendi.'); return hubApi(`/mappings/brands/${item.localId}?connectionId=${encodeURIComponent(savedConnectionId)}`, { method: 'DELETE', headers: { 'If-Match': `"v${current.version}"` } }) }, onSuccess: (_, item) => { if (localId === item.localId && connectionId === savedConnectionId) setExternalId(''); setNotice('Marka eşleştirmesi kaldırıldı.') }, onError: reason => setNotice(mappingSaveErrorMessage(reason, 'Marka eşleştirmesi kaldırılamadı.')), onSettled: async (_, __, item) => { await Promise.all([client.invalidateQueries({ queryKey: queryKeys.brandMapping(item.localId, savedConnectionId) }), client.invalidateQueries({ queryKey: queryKeys.brandMappings(savedConnectionId) }), refreshMappingConflicts(client, savedConnectionId)]) } })
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
      // A brand or a mapping refused on the way no longer stops the rest (WEB-SAYFALAR-7): each is counted and named.
      const brandFailures: BrandImportFailure[] = []
      let result: Awaited<ReturnType<typeof importBrandMappingItems>> = { mapped: 0, skipped: 0, failures: [] }
      for (const source of brandTransferBundle.brands) {
        const normalizedName = source.name.trim().toLocaleLowerCase('tr-TR')
        const existing = allLocalBrands.find(item => item.id === source.id || item.name.trim().toLocaleLowerCase('tr-TR') === normalizedName)
        let target = existing
        if (brandTransferSelection.brands) {
          try {
            if (target && !target.isActive) {
              target = await hubApi<ApiAnswer<'patchCatalogBrandsById'>>(`/catalog/brands/${target.id}`, { method: 'PATCH', headers: { 'If-Match': `"v${target.version}"` }, body: JSON.stringify({ name: target.name, isActive: true }) })
            } else if (!target) {
              target = await hubApi<ApiAnswer<'postCatalogBrands'>>('/catalog/brands', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ name: source.name.trim() }) })
              created++
            }
          } catch (reason) {
            brandFailures.push({ name: source.name.trim(), message: reason instanceof Error ? reason.message : 'Kaydedilemedi.' })
            continue
          }
        }
        if (target) brandIdMap.set(source.id, target.id)
      }
      if (brandTransferSelection.mappings) {
        const externalIds = new Set((await lookupBrandReferences(savedConnectionId, brandTransferBundle.mappings.map(item => item.externalId))).map(brand => brand.externalId))
        const brandNames = new Map(brandTransferBundle.brands.map(brand => [brand.id, brand.name.trim()]))
        result = await importBrandMappingItems(brandTransferBundle.mappings, {
          target: item => { const targetLocalId = brandIdMap.get(item.localId) ?? allLocalBrands.find(brand => brand.id === item.localId)?.id; return targetLocalId && externalIds.has(item.externalId) ? targetLocalId : null },
          nameOf: item => brandNames.get(item.localId) ?? item.externalId,
          send: (item, targetLocalId) => hubApi<CatalogMapping>(`/mappings/brands/${targetLocalId}`, { method: 'PUT', body: JSON.stringify({ connectionId: savedConnectionId, snapshotId: savedReferences.data?.snapshotId, externalId: item.externalId, status: 'VERIFIED' }) })
        })
      }
      const message = brandMappingImportMessage({ created, brandFailures, ...result })
      // A clean import closes the window; one with refusals keeps it open beside its message.
      if (!brandFailures.length && !result.failures.length) { setBrandTransferOpen(false); setBrandTransferBundle(null) }
      setNotice(message)
    } catch (reason) { setNotice(reason instanceof Error ? reason.message : 'Marka eşleştirme aktarımı uygulanamadı.') } finally {
      // What was written before a stop is on the server: the lists are read again whatever happened.
      await Promise.all([refreshLocalBrands(client), client.invalidateQueries({ queryKey: queryKeys.brandMapping() }), client.invalidateQueries({ queryKey: queryKeys.brandMappings(savedConnectionId) })]).catch(() => undefined)
      setBrandTransferBusy(false)
    }
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
  // The brand count and age sit behind the "i" beside the target label, not in a line under it.
  const brandReferenceInfo = references.data ? `${references.data.itemCount.toLocaleString('tr-TR')} marka · ${formatDateTime(references.data.fetchedAt)}${mapping.data ? ` · Kayıt v${mapping.data.version}` : ''}` : referencesPending ? 'Marka listesi yükleniyor…' : 'Marka listesi henüz alınmadı. Senkronize Et ile alın.'
  return <section className="content f3 mapping-page mapping-workspace stitch-reference-mapping">{confirmDialog}<header className="mapping-reference-heading"><div><p className="eyebrow">Katalog</p><h1>Eşleştirmeler</h1></div><div className="mapping-reference-heading-actions" aria-label="Marka eşleştirme yedeği işlemleri"><div className="mapping-transfer-actions"><button type="button" className="secondary" title="Marka eşleştirme yedeğini JSON olarak dışa aktar" onClick={() => void exportBrandMappings()} disabled={brandTransferBusy || !savedConnectionId}><UiIcon name="download" size={18} />{brandTransferBusy ? 'Hazırlanıyor…' : 'Dışa Aktar'}</button><label className="mapping-transfer-upload" title="JSON marka eşleştirme yedeğini içe aktar"><UiIcon name="import" size={18} />İçe Aktar<input type="file" accept="application/json,.json" onChange={readBrandMappingBundle} disabled={brandTransferBusy} /></label></div></div></header><MappingViewTabs active="brand" />{notice && <MessageToast message={notice} onClose={() => setNotice('')} />}
    <article className="mapping-reference-card mapping-local-brand-builder">
      <div className="mapping-section-heading"><div><h2>1. Yeni Marka Ekle</h2></div></div>
      <section className="brand-library-panel brand-library-panel-standalone"><div className="brand-library-heading"><div><strong>Panel Markaları</strong></div><span>{activeLocalBrands.length} kayıt</span></div><form className="platform-category-create brand-library-create" onSubmit={event => { event.preventDefault(); createBrand.mutate() }}><label>Yeni Panel Markası<input aria-label="Yeni panel markası adı" maxLength={160} value={brandName} onChange={event => setBrandName(event.target.value)} placeholder="Örn. Ravencia" required /></label><button disabled={createBrand.isPending}>{createBrand.isPending ? 'Ekleniyor…' : 'Marka Ekle'}</button></form><div ref={brandListHeight.ref} style={brandListHeight.style} className="category-chip-list brand-library-list" aria-label="Eklenen panel markaları">{activeLocalBrands.length ? activeLocalBrands.map(brand => <span key={brand.id} className={brand.id === localId ? 'active' : ''}><button type="button" onClick={() => { setLocalId(brand.id); setExternalId(''); setNotice('') }}>{brand.name}</button><button type="button" className="category-chip-remove" aria-label={`${brand.name} markasını kaldır`} onClick={() => archiveBrand.mutate(brand)} disabled={archiveBrand.isPending}><UiIcon name="close" /></button></span>) : localBrands.isLoading ? null : <small>Henüz panel markası eklenmedi.</small>}</div></section>
    </article>
    {!brandMappingSupported && <article className="mapping-reference-card brand-mapping-console"><header className="mapping-builder-head"><h2><UiIcon name="link" />2. Yeni Marka Eşleme</h2>{brandPlatformTabs}</header><div className="unknown">{brandPlatformCode === 'PTTAVM' ? <><strong>Markalar otomatik eşleşir</strong><p>PttAVM'de marka listesi yok; panel markalarınız otomatik eşleşir. Ayrıca eşleme yapmanız gerekmez.</p></> : brandAutoMatchLocative[brandPlatformCode] ? <><strong>Markalar otomatik eşleşir</strong><p>{brandAutoMatchLocative[brandPlatformCode]} marka listesi yok; panel markalarınız otomatik eşleşir ve ürünler panel markası adıyla gönderilir. Ayrıca eşleme yapmanız gerekmez.</p></> : <><strong>{brandPlatformLabel} için marka eşlemesi gerekmez</strong><p>{brandPlatformLabel} marka listesi sunmuyor; ürünün markası yayında adıyla gönderilir.</p></>}</div></article>}
    {brandMappingSupported && <>
    <article className="mapping-reference-card brand-mapping-console">
      <header className="mapping-builder-head"><h2><UiIcon name="link" />2. Yeni Marka Eşleme</h2>{brandPlatformTabs}</header>
      <div className="mapping-pair-grid brand-mapping-pair-grid"><section className="mapping-side local"><div className="mapping-side-head"><span>Panel Kataloğu</span></div><SearchableSelect label="Panel Markası" value={localId} options={activeLocalBrands.map(item => ({ value: item.id, label: item.name, description: 'Panel markası' }))} placeholder="Panel markalarında ara" disabled={!connectionId} triggerClassName="panel-category-picker-trigger" onChange={value => { if (localId) setExternalId(''); setLocalId(value); setNotice('') }} /></section><div className="mapping-link-rail" aria-hidden="true"><UiIcon name="link" /><small>Eşleştir</small></div><section className="mapping-side remote"><div className="mapping-side-head"><div className="mapping-side-head-copy"><span>Hedef Pazar Yeri &amp; Marka</span></div></div><div className="mapping-side-field"><span className="mapping-label-line">{brandPlatformLabel} Markası<TitleHint label={`${brandPlatformLabel} Markası`}>{brandReferenceInfo}</TitleHint></span><div className="mapping-target-controls brand-mapping-target-controls">{platformConnections.length > 1 && <PanelSelect ariaLabel="Hedef pazaryeri bağlantısı" value={connectionId} onChange={next => { setConnectionChoice(next); setLastListConnectionId(next); setEditingBrandMappingId(''); setLocalId(''); setExternalId(''); setNotice('') }} options={[{ value: '', label: `${brandPlatformLabel} Bağlantısı` }, ...platformConnections.map(item => ({ value: item.id, label: `${brandPlatformLabel}${platformConnections.length > 1 ? ` · ${item.displayName}` : ''}` }))]} />}<BrandReferencePicker key={`${connectionId}:${localId}`} connectionId={connectionId} snapshot={references.data} value={externalId} selectedName={selectedExternalName} placeholder={references.data || referencesPending ? 'Platform Markası Seçin...' : references.isFetching || syncBrands.isPending ? 'Markalar yükleniyor…' : 'Önce marka listesini senkronize edin'} disabled={!connectionId || !references.data} onChange={(id, item) => { setExternalId(id); setPickedBrand(item) }} /></div></div></section></div>
      {connections.isError || localBrands.isError ? <ErrorBox error={connections.error ?? localBrands.error} /> : connectionId ? <div className={`mapping-action brand-reference-action${references.isError && !references.data ? ' has-error' : ''}`} aria-busy={referencesPending || undefined}><div className="brand-reference-buttons"><button type="button" className="secondary" disabled={!connectionId || syncBrands.isPending || referencesPending} onClick={() => syncBrands.mutate()}><UiIcon name={syncBrands.isPending ? 'loader' : 'refresh'} />{syncBrands.isPending ? 'Kuyruğa Alınıyor…' : 'Senkronize Et'}</button>{(references.data || referencesPending) && <button type="button" disabled={!references.data || !localId || !externalId || save.isPending || mapping.isLoading} onClick={saveSelectedBrandMapping}>{save.isPending ? 'Kaydediliyor…' : mapping.data ? 'Eşleştirmeyi Güncelle' : 'Eşleştirmeyi Kaydet'}</button>}</div></div> : null}
      {connectionId && !!unmappedBrands.data?.length && <section className="brand-library-panel brand-unmapped-panel" aria-label={`Eşlenmemiş ${brandPlatformLabel} markaları`}><div className="brand-library-heading"><div><strong>Eşlenmemiş {brandPlatformLabel} Markaları<TitleHint label={`Eşlenmemiş ${brandPlatformLabel} Markaları`}>Ürün aktarımı markaları kendisi eşler (eşleme yoksa aynı adlı panel markasına, o da yoksa yeni bir panel markasına). Burada aktarımın eşleyemediği markalar listelenir: eşlendiği panel markası kapalı, birden fazla panel markasına eşli ya da aynı adlı panel markası başka bir markaya eşli. Birini seçip panel markasını belirleyin ve kaydedin.</TitleHint></strong></div><span>{unmappedBrands.data.length} kayıt</span></div><div className="category-chip-list brand-unmapped-list">{unmappedBrands.data.map(item => <span key={item.externalId} className={item.externalId === externalId ? 'active' : ''}><button type="button" title={`Marka no ${item.externalId}`} onClick={() => { setExternalId(item.externalId); setPickedBrand({ externalId: item.externalId, name: item.name ?? item.externalId, isActive: true }); setNotice('') }}>{item.name ?? `Marka no ${item.externalId}`}</button></span>)}</div></section>}
    </article>
    </>}
    <article className="mapping-reference-card mapping-saved-card brand-mapping-saved-card"><header><h2>3. Kayıtlı Marka Eşleştirmeleri</h2>{savedBrandPlatformTabs}<div><span className="mapping-record-count">{savedMappingCount} kayıt</span></div></header><div className="mapping-saved-table is-brand"><table><thead><tr><th>Panel Markası</th><th>Platform</th><th>{savedBrandPlatformLabel} Markası</th><th>Durum</th><th>İşlemler</th></tr></thead><tbody>{!savedConnectionId ? <tr><td className="mapping-empty-table-cell" colSpan={5}><EmptyState icon="tag" title="Henüz Eşleştirme Yok">Bu platformda kayıtlı marka eşleştirmesi yok.</EmptyState></td></tr> : savedMappingsPending ? <tr className="brand-mapping-skeleton-row" aria-busy="true" aria-label="Kayıtlı marka eşleştirmeleri yükleniyor">{[0, 1, 2, 3, 4].map(cell => <td key={cell}><span className="brand-reference-skeleton is-cell" aria-hidden="true" /></td>)}</tr> : brandMappings.isError ? <tr><td className="mapping-empty-table-cell" colSpan={5}><EmptyState icon="alert" title="Eşleştirmeler Alınamadı">Kayıtlı marka eşleştirmeleri alınamadı.</EmptyState></td></tr> : savedBrandMappings.length ? savedBrandMappings.map(item => <tr key={item.id} className={editingBrandMappingId === item.id ? 'is-editing' : ''}><td>{localBrandById.get(item.localId)?.name ?? item.localId}</td><td><span className="ms-platform">{platformLogoSource(savedBrandPlatformCode) && <img className={platformLogoClass(savedBrandPlatformCode)} src={platformLogoSource(savedBrandPlatformCode)!} alt="" aria-hidden="true" />}{savedBrandPlatformLabel}</span></td><td className="brand-mapping-inline-editor">{editingBrandMappingId === item.id ? <BrandReferenceSelect label="" platformLabel={savedBrandPlatformLabel} connectionId={savedConnectionId} snapshot={savedReferences.data} value={editingExternalId} selectedName={editingExternalName} placeholder={`${savedBrandPlatformLabel} markası ara`} disabled={!savedReferences.data} onChange={(id, brand) => { setEditingExternalId(id); setEditingPickedBrand(brand) }} /> : savedBrandName(item.externalId) ?? (savedNamesLoading ? <span className="brand-reference-skeleton is-name" aria-label="Yükleniyor" /> : item.externalId)}</td><td><span className={`mapping-table-status ${item.status === 'VERIFIED' || item.status === 'ACTIVE' ? 'active' : 'error'}`}><MappingStateGlyph state={item.status === 'VERIFIED' || item.status === 'ACTIVE' ? 'done' : 'partial'} />{mappingStatusLabel(item.status)}</span></td><td><div className="mapping-row-actions">{editingBrandMappingId === item.id ? <><button type="button" className="mapping-icon-button" aria-label="Marka eşleştirmesini kaydet" disabled={!editingExternalId || !savedReferences.data || save.isPending} onClick={() => saveEditedBrandMapping(item)}><UiIcon name="check" /></button><button type="button" className="mapping-icon-button" aria-label="Marka düzenlemeyi iptal et" onClick={() => setEditingBrandMappingId('')}><UiIcon name="close" /></button></> : <><button type="button" className="mapping-icon-button" aria-label="Marka eşleştirmesini düzenle" onClick={() => { setEditingExternalId(item.externalId); setEditingBrandMappingId(item.id); setBrandEditOpen(false); setNotice('') }}><UiIcon name="edit" /></button><button type="button" className="mapping-icon-button mapping-delete-button" aria-label="Eşleştirmeyi sil" disabled={removeMapping.isPending} onClick={() => void confirm({ title: 'Marka Eşleştirmesi Kaldırılsın mı?', message: 'Bu marka eşleştirmesi kaldırılacak.', confirmLabel: 'Kaldır', destructive: true }).then(accepted => { if (accepted) removeMapping.mutate(item) })}><MappingDeleteIcon /></button></>}</div></td></tr>) : <tr><td className="mapping-empty-table-cell" colSpan={5}><EmptyState icon="tag" title="Henüz Eşleştirme Yok">Henüz kaydedilmiş marka eşleştirmesi bulunmuyor.</EmptyState></td></tr>}</tbody></table></div><footer><span>Toplam {savedMappingCount} eşleştirme</span></footer></article>
    {brandTransferOpen && brandTransferBundle && <div className="workspace-modal-backdrop mapping-transfer-backdrop" role="presentation" onMouseDown={() => { if (!brandTransferBusy) { setBrandTransferOpen(false); setBrandTransferBundle(null) } }}><DialogSurface as="section" onClose={() => { if (!brandTransferBusy) { setBrandTransferOpen(false); setBrandTransferBundle(null) } }} className="workspace-modal mapping-transfer-modal" aria-labelledby="brand-transfer-title" onMouseDown={event => event.stopPropagation()}><header><div><p className="eyebrow">Marka Yedeği</p><h2 id="brand-transfer-title">İçe Aktarma Alanlarını Seçin<TitleHint label="İçe Aktarma Alanlarını Seçin">Mevcut kayıtlar silinmez; aynı adlı markalar güncellenir, eksikler eklenir. Güncel {savedBrandPlatformLabel} marka listesinde olmayan eşleştirmeler atlanır.</TitleHint></h2><p>{brandTransferBundle.brands.length} marka · {brandTransferBundle.mappings.length} eşleştirme</p></div><button type="button" className="modal-close" onClick={() => { setBrandTransferOpen(false); setBrandTransferBundle(null) }} disabled={brandTransferBusy} aria-label="Marka içe aktarmayı kapat"><UiIcon name="close" /></button></header><div className="mapping-transfer-options"><label><input type="checkbox" checked={brandTransferSelection.brands} onChange={event => setBrandTransferSelection(current => ({ ...current, brands: event.target.checked }))} /><span><strong>Panel Markaları</strong><small>Eksik markaları ekler, pasif markaları yeniden etkinleştirir.</small></span></label><label><input type="checkbox" checked={brandTransferSelection.mappings} onChange={event => setBrandTransferSelection(current => ({ ...current, mappings: event.target.checked }))} /><span><strong>Marka Eşleştirmeleri</strong><small>Mevcut {savedBrandPlatformLabel} marka referanslarıyla eşleştirmeleri günceller.</small></span></label></div><footer><button type="button" className="secondary" onClick={() => { setBrandTransferOpen(false); setBrandTransferBundle(null) }} disabled={brandTransferBusy}>Vazgeç</button><button type="button" onClick={() => void importBrandMappings()} disabled={brandTransferBusy}>{brandTransferBusy ? 'İçe Aktarılıyor…' : 'Seçilenleri Güncelle'}</button></footer></DialogSurface></div>}
    {brandEditOpen && <div className="brand-mapping-modal-backdrop" role="presentation" onMouseDown={() => setBrandEditOpen(false)}><DialogSurface as="section" onClose={() => setBrandEditOpen(false)} className="brand-mapping-modal" aria-labelledby="brand-mapping-edit-title" onMouseDown={event => event.stopPropagation()}><header><div><small>Marka eşleme</small><h2 id="brand-mapping-edit-title">Marka Eşleştirmesini Düzenle</h2></div><button type="button" className="modal-close" aria-label="Marka eşleme penceresini kapat" onClick={() => setBrandEditOpen(false)}><UiIcon name="close" /></button></header><div className="brand-mapping-modal-body"><div className="brand-mapping-selected-local"><small>Panel markası</small><strong>{localBrandById.get(localId)?.name ?? localId}</strong></div><BrandReferenceSelect label={`${brandPlatformLabel} Markası`} platformLabel={brandPlatformLabel} connectionId={connectionId} snapshot={references.data} value={externalId} selectedName={selectedExternalName} placeholder={references.isLoading ? 'Markalar yükleniyor…' : 'Marka adı veya marka no ara'} disabled={!references.data} onChange={(id, brand) => { setExternalId(id); setPickedBrand(brand) }} /></div><footer><button type="button" className="secondary" onClick={() => setBrandEditOpen(false)}>Vazgeç</button><button type="button" disabled={!localId || !externalId || save.isPending || mapping.isLoading} onClick={saveSelectedBrandMapping}>{save.isPending ? 'Kaydediliyor…' : 'Değişikliği Kaydet'}</button></footer></DialogSurface></div>}
  </section>
}
