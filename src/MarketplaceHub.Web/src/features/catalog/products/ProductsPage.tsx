// Ürünler (/products).
import { lazy, Suspense, useEffect, useMemo, useRef, useState, type FormEvent } from 'react'
import { variantOptionParts } from '../variant-option-label'
import { Link, useNavigate, useSearchParams } from 'react-router'
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi, loadAllPages, type CursorPage } from '../../../shared/api'
import { newIdempotencyKey, sendWithVersionedIdempotencyKey } from '../../../shared/idempotency'
import { useToastStack } from '../../../shared/components/toast-stack-context'
import { useConfirmDialog } from '../../../shared/components'
import { CountUp } from '../../../shared/components/CountUp'
import { useMediaQuery } from '../../../shared/hooks/useMediaQuery'
import { productStatusLabel } from '../../../shared/status-labels'
import { RrAnchor, RrBadge, RrColumnPop, RrError, RrFilterCard, RrHeadCell, RrIcon, RrLoading, RrLogo, RrModal, RrPageSize, RrRing, RrThumb, titleTr, type RingTone } from '../../marketplace/returns-ui'
import { appendNotification, notificationError, productNotificationSubject, type NotificationContext } from '../../../shared/notifications'
import { updateVariantGroupSelection, variantGroupSelectionState } from '../variant-bulk-edit'
import { productPlatformDisplayLabel, productPlatformDisplayState } from '../product-platform-status'
import { nextProductCatalogSort, parseProductCatalogSort, productCatalogSortOptions, productCatalogSortValue, type ProductCatalogSort } from '../product-catalog-sorting'
import { applyVariantStockSets, bulkEditSummary, isStaleStockError, parseStockInput, setVariantOnHand, stockErrorMessage, stockTarget, type VariantStockSet } from '../variant-stock-set'
import { activeProductSyncJobs as filterActiveProductSyncJobs, productSyncJobsFinished, productSyncJobsPath } from '../product-sync-tracking'
import { productImportConnection, singleProductLookupLabel } from '../product-import-platforms'
import { productImportOptionVisibility as getProductImportOptionVisibility, type ProductImportMethod, type ProductImportMode } from '../product-import-options'
import { productPlatformFilterGroups } from '../product-platform-filter'
import { quickEditOfferPrices, quickPriceTarget } from '../quick-price-edit'
import { bulkStatusPlan, bulkStatusResultMessage, runInChunks } from '../bulk-status-run'
import { ViewTabsWithMore, viewTabLabel } from '../../../shared/view-tabs'
import { formatMoney } from '../../../shared/format-money'
import { readUrlNumber, readUrlText, useChangeEffect, useListUrlSync } from '../../../shared/list-url-state'
import { ProductTransferDialog } from './ProductTransferDialog'
// Loaded with the editor's stylesheet only when a bulk publication opens, like a row's publish popup.
const BulkPublishDialog = lazy(() => import('./BulkPublishDialog').then(module => ({ default: module.BulkPublishDialog })))
import { AcceptedJob, ImageLightboxModal, MarketplaceConnection, Product, Variant, compareLabelsAlphabetically, compareOptionValues, compareVariantOptionSignatures, formatCatalogOptionLabel, isColorOptionName, isProductPublicationConnection, isVariantOptionName, optionValueSortRank, preferredColorOption, variantColorKey, variantOptionEntries } from '../shared'

function compareVariantItemsAlphabetically(left: { variant: { optionSignature?: string | null; sku: string } }, right: { variant: { optionSignature?: string | null; sku: string } }) {
  const leftLabel = (left.variant.optionSignature || left.variant.sku).trim()
  const rightLabel = (right.variant.optionSignature || right.variant.sku).trim()
  return compareVariantOptionSignatures(leftLabel, rightLabel)
    || compareLabelsAlphabetically(left.variant.sku, right.variant.sku)
}
type ProductListFilters = { search: string; status: string; platform: string; stock: string; sort: string }
type ProductSummary = { totalCount: number; activeCount: number; outOfStockCount: number; lowStockCount: number; platforms: string[]; totalUnits?: number }
type ProductSyncJob = { id: string; connectionId: string | null; jobType: string; status: string; progressCurrent: number; progressTotal: number | null; progressPercent: number | null; progressLabel: string | null; progressReceived: number; progressProcessed: number; progressSkipped: number; progressFailed: number; createdAt: string; completedAt: string | null }
const isProductImportConnection = productImportConnection
type ProductGroup = {
  id: string
  primary: Product
  products: Product[]
  variants: Array<{ product: Product; variant: Variant }>
}
function productFamilyKey(product: Product) {
  const modelCode = product.modelCode?.trim() || product.variants.map(variant => variant.modelCode?.trim()).find(Boolean)
  if (!modelCode) return `product:${product.id}`
  return `model:${modelCode.toLocaleUpperCase('tr-TR')}`
}
function productRowsAsCards(products: Product[]): ProductGroup[] {
  const groups = new Map<string, ProductGroup>()
  for (const product of products) {
    const id = productFamilyKey(product)
    const existing = groups.get(id)
    if (existing) {
      existing.products.push(product)
      existing.variants.push(...product.variants.map(variant => ({ product, variant })))
      continue
    }
    groups.set(id, {
      id,
      primary: product,
      products: [product],
      variants: product.variants.map(variant => ({ product, variant }))
    })
  }
  return [...groups.values()]
}
async function fetchProductPage(limit: number, filters: ProductListFilters, after: string | null) {
  const params = new URLSearchParams({ limit: String(limit) })
  if (after) params.set('after', after)
  if (filters.search) params.set('search', filters.search)
  if (filters.status) params.set('status', filters.status)
  if (filters.platform) params.set('platform', filters.platform)
  if (filters.stock) params.set('stock', filters.stock)
  if (filters.sort) params.set('sort', filters.sort)
  return hubApi<CursorPage<Product>>(`/products?${params.toString()}`)
}
type VariantDisplayValue = { label: string; quantity: number }
type VariantDisplayGroup = { label: string; values: VariantDisplayValue[]; valueSortLabel: string }
function productVariantDisplayGroups(variants: Variant[]) {
  const groups = new Map<string, VariantDisplayGroup>()
  const add = (key: string, label: string, value: string, quantity: number, valueSortLabel = label) => {
    const group = groups.get(key) ?? { label, values: [], valueSortLabel }
    const existing = group.values.find(item => item.label.localeCompare(value, 'tr', { sensitivity: 'accent' }) === 0)
    if (value) {
      if (existing) existing.quantity += quantity
      else group.values.push({ label: value, quantity })
    }
    groups.set(key, group)
  }
  for (const variant of variants) {
    const options = variantOptionEntries(variant)
    const color = preferredColorOption(options)
    const size = options.find(option => ['BEDEN', 'SIZE', 'SIZ', 'NUMARA', 'NUMBER'].includes(option.name.replace(/\s+/g, '').toLocaleUpperCase('tr-TR')))
    if (color) {
      add(`color:${color.value.toLocaleLowerCase('tr-TR')}`, color.value, size?.value ?? (options.filter(option => option !== color).map(option => `${option.name}: ${option.value}`).join(' · ') || variant.sku), variant.onHand, size?.name ?? 'Beden')
    } else if (size) {
      add('size', size.name, size.value, variant.onHand, size.name)
    } else if (options.length) {
      add(`option:${options[0].name.toLocaleLowerCase('tr-TR')}`, options[0].name, options.map(option => `${option.name}: ${option.value}`).join(' · '), variant.onHand, options[0].name)
    } else {
      add('variant', 'Varyantlar', variant.sku, variant.onHand)
    }
  }
  return [...groups.values()].map(group => ({
    ...group,
    values: [...group.values].sort((left, right) => {
      const leftRank = optionValueSortRank(group.valueSortLabel, left.label)
      const rightRank = optionValueSortRank(group.valueSortLabel, right.label)
      return leftRank.bucket - rightRank.bucket
        || leftRank.primary - rightRank.primary
        || leftRank.secondary - rightRank.secondary
        || leftRank.text.localeCompare(rightRank.text, 'tr', { numeric: true, sensitivity: 'base' })
    })
  })).sort((left, right) => left.label.localeCompare(right.label, 'tr', { sensitivity: 'base' }))
}
type QuickEditMode = 'stock' | 'price' | 'both'
function ProductQuickEditModal({ products, connections, mode = 'both', onChanged, onClose, onResult }: { products: Product[]; connections: MarketplaceConnection[]; mode?: QuickEditMode; onChanged: () => Promise<unknown>; onClose: () => void; onResult?: (title: string, message: string, kind: 'success' | 'error' | 'info', context?: NotificationContext) => void }) {
  const contextProduct = products[0]
  const contextTitle = contextProduct?.title || 'Toplu düzenleme'
  const contextModelCode = contextProduct ? contextProduct.modelCode || contextProduct.variants.find(variant => variant.modelCode)?.modelCode || '—' : null
  const variants = products.flatMap(product => product.variants.map(variant => ({ product, variant })))
  const sortedVariants = [...variants].sort(compareVariantItemsAlphabetically)
  const groups = sortedVariants.reduce<Record<string, typeof variants>>((result, item) => {
    const color = variantColorKey(item.variant) || 'Diğer'
    ;(result[color] ??= []).push(item)
    return result
  }, {})
  const optionValue = (signature: string, labels: string[], fallback: string) => {
    const labelPattern = labels.join('|')
    return signature.match(new RegExp(`(?:${labelPattern})\\s*[:=]\\s*([^|_]+)`, 'i'))?.[1]?.trim() || fallback
  }
  const colorOf = (item: typeof variants[number]) => variantColorKey(item.variant) || 'Diğer'
  const colorLabelOf = (item: typeof variants[number]) => preferredColorOption(variantOptionEntries(item.variant))?.value.trim() || 'Diğer'
  const sizeOf = (item: typeof variants[number]) => optionValue(item.variant.optionSignature || '', ['BEDEN', 'SIZE'], item.variant.optionSignature || 'Ana varyant')
  const colorLabels = sortedVariants.reduce<Record<string, string>>((result, item) => { const key = colorOf(item); result[key] ??= formatCatalogOptionLabel(colorLabelOf(item)); return result }, {})
  const colorOptions = Object.keys(groups).sort((left, right) => compareLabelsAlphabetically(colorLabels[left] || left, colorLabels[right] || right))
  const [selectionDraft, setSelectionDraft] = useState<string[]>([])
  const [selectedColors, setSelectedColors] = useState<string[]>([])
  const [selectedSizes, setSelectedSizes] = useState<string[]>([])
  const [openColorGroups, setOpenColorGroups] = useState<string[]>([])
  const [listPrice, setListPrice] = useState(''); const [salePrice, setSalePrice] = useState(''); const [stockAmount, setStockAmount] = useState('')
  const [stockAction, setStockAction] = useState<'SET' | 'ADD' | 'SUBTRACT'>('SET'); const [notice, setNotice] = useState(''); const [saving, setSaving] = useState(false)
  // The channel a variant without an offer gets its first price on: chosen by the person (preset only when there is
  // just one), never picked by the screen.
  const [priceConnectionId, setPriceConnectionId] = useState(() => connections.length === 1 ? connections[0].id : '')
  const needsPriceConnection = mode !== 'stock' && variants.some(item => !item.variant.offerId)
  // A first offer keeps its create key for this window's life, so a resend after a lost answer cannot make two.
  const offerCreateKeys = useRef(new Map<string, string>())
  const offerCreateKey = (variantId: string) => {
    const existing = offerCreateKeys.current.get(variantId)
    if (existing) return existing
    const created = newIdempotencyKey('channel-offer-create')
    offerCreateKeys.current.set(variantId, created)
    return created
  }
  // The stock each variant had when this dialog opened (or after this dialog saved it): the value a bulk change is
  // computed from and sent as the expected current stock. A refused (stale) change resets it to the refreshed list.
  const stockBaseline = useRef<Map<string, number>>(new Map())
  const resetStockBaseline = useRef(false)
  useEffect(() => {
    for (const item of variants) if (resetStockBaseline.current || !stockBaseline.current.has(item.variant.id)) stockBaseline.current.set(item.variant.id, item.variant.onHand)
    resetStockBaseline.current = false
  })
  const selectedSet = new Set(selectionDraft)
  const activeSelection = selectionDraft
  const activeSelectedSet = selectedSet
  const sizeOptions = [...new Set(sortedVariants.filter(item => !selectedColors.length || selectedColors.includes(colorOf(item))).map(sizeOf))].sort((left, right) => compareOptionValues('Beden', left, right))
  const toggleColor = (color: string) => {
    const next = selectedColors.includes(color) ? selectedColors.filter(item => item !== color) : [...selectedColors, color]
    setSelectedColors(next)
    applyFilterSelection(next, selectedSizes)
  }
  const toggleSize = (size: string) => {
    const next = selectedSizes.includes(size) ? selectedSizes.filter(item => item !== size) : [...selectedSizes, size]
    setSelectedSizes(next)
    applyFilterSelection(selectedColors, next)
  }
  const toggle = (id: string) => { setSelectionDraft(current => current.includes(id) ? current.filter(value => value !== id) : [...current, id]) }
  function applyFilterSelection(colors: string[], sizes: string[]) {
    const ids = !colors.length && !sizes.length ? [] : sortedVariants.filter(item => (!colors.length || colors.includes(colorOf(item))) && (!sizes.length || sizes.includes(sizeOf(item)))).map(item => item.variant.id)
    setSelectionDraft(ids)
  }
  async function apply(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); if (saving) return
    if (!selectionDraft.length) { const message = 'Önce en az bir varyant seçin.'; setNotice(message); onResult?.('Varyant Seçilmedi', message, 'error'); return }
    const targetSelection = selectionDraft
    const targetSelectionSet = new Set(targetSelection)
    const priceRequested = mode !== 'stock' && (listPrice !== '' || salePrice !== ''); const stockRequested = stockAmount !== ''
    if (!priceRequested && !stockRequested) { const message = 'Uygulanacak fiyat veya stok değerini girin.'; setNotice(message); onResult?.('Değer Girilmedi', message, 'error'); return }
    const list = listPrice === '' ? null : Number(listPrice); const sale = salePrice === '' ? null : Number(salePrice); const amount = stockAmount === '' ? null : Number(stockAmount)
    if ((list != null && (!Number.isFinite(list) || list < 0)) || (sale != null && (!Number.isFinite(sale) || sale <= 0)) || (list != null && sale != null && list < sale) || (amount != null && (!Number.isFinite(amount) || amount < 0))) { const message = 'Değerleri kontrol edin; satış fiyatı sıfırdan büyük olmalı, negatif fiyat/stok veya hatalı fiyat sıralaması kullanılamaz.'; setNotice(message); onResult?.('Geçersiz Değer', message, 'error'); return }
    setSaving(true); setNotice('Seçilen varyantlar güncelleniyor…')
    // Every selected variant is attempted; a failure is recorded and the rest still go out. Stock is sent as an
    // absolute value against the stock this dialog opened with, so a change made meanwhile is refused, not undone.
    const failures: Array<{ sku: string; message: string }> = []
    const stockUpdates: VariantStockSet[] = []
    // Variants whose first sale price was taken from the list price typed (no sale price given, none on the offer).
    const saleDefaulted: Array<{ sku: string; currency: string }> = []
    let anyStale = false
    try {
      for (const item of sortedVariants.filter(value => targetSelectionSet.has(value.variant.id))) {
        const variant = item.variant
        if (priceRequested) {
          try {
            const target = quickPriceTarget(variant, priceConnectionId, connections.length)
            if (target.kind === 'blocked') throw new Error(target.reason)
            const prices = quickEditOfferPrices(variant, list, sale)
            if (!prices.ok) throw new Error(prices.message)
            const body = { connectionId: target.kind === 'create' ? target.connectionId : '', variantId: variant.id, listPrice: prices.listPrice, salePrice: prices.salePrice, currency: variant.currency || 'TRY', vatRate: variant.vatRate ?? 10, vatInclusion: variant.vatInclusion || 'INCLUDED', roundingMode: variant.roundingMode || 'HALF_EVEN', safetyStock: variant.safetyStock ?? 0, status: variant.offerStatus || 'ACTIVE', reason: 'Toplu ürün fiyat düzenleme' }
            if (target.kind === 'update') await hubApi(`/channel-offers/${target.offerId}`, { method: 'PATCH', headers: { 'If-Match': `"v${target.offerVersion}"` }, body: JSON.stringify(body) })
            else await sendWithVersionedIdempotencyKey(offerCreateKey(variant.id), key => hubApi('/channel-offers', { method: 'POST', headers: { 'Idempotency-Key': key }, body: JSON.stringify(body) }))
            if (prices.saleDefaulted) saleDefaulted.push({ sku: variant.sku, currency: variant.currency || 'TRY' })
          } catch (error) {
            if (isStaleStockError(error)) anyStale = true
            failures.push({ sku: variant.sku, message: `fiyat: ${error instanceof Error ? error.message : 'kaydedilemedi'}` })
          }
        }
        if (stockRequested && amount != null) {
          const expected = stockBaseline.current.get(variant.id) ?? variant.onHand
          const target = stockTarget(stockAction, amount, expected)
          if (target < 0) failures.push({ sku: variant.sku, message: 'stok sıfırın altına inemez.' })
          else stockUpdates.push({ variantId: variant.id, sku: variant.sku, onHand: target, expectedOnHand: expected })
        }
      }
      const stockResult = await applyVariantStockSets(stockUpdates, update => setVariantOnHand(update, 'Toplu ürün stok düzenleme'))
      for (const saved of stockResult.saved) stockBaseline.current.set(saved.variantId, saved.onHand)
      for (const failed of stockResult.failed) { if (failed.stale) anyStale = true; failures.push({ sku: failed.sku, message: `stok: ${failed.message}` }) }
    } finally {
      await onChanged().catch(() => undefined)
      setSaving(false)
    }
    const summary = bulkEditSummary(targetSelection.length, failures)
    // The defaulted sale price is a decision the person did not type: it stays in the notifications to be checked.
    if (saleDefaulted.length && list != null) onResult?.('Satış Fiyatı Liste Fiyatına Eşitlendi', `Satış fiyatı girilmediği için ${saleDefaulted.length} varyantın satış fiyatı liste fiyatına (${formatMoney(list, saleDefaulted[0].currency)}) eşitlendi: ${saleDefaulted.slice(0, 10).map(item => item.sku).join(', ')}${saleDefaulted.length > 10 ? ` ve ${saleDefaulted.length - 10} varyant daha` : ''}. Satış fiyatını kontrol edin.`, 'info', { record: true })
    if (!failures.length) { onResult?.('Hızlı Düzenleme Uygulandı', summary, 'success'); onClose(); return }
    const staleHint = anyStale ? ' Değişen değerler yenilendi; kontrol edip yeniden uygulayın.' : ''
    setNotice(`Başarısız: ${summary}${staleHint}`); onResult?.('Hızlı Düzenleme Tamamlanamadı', summary, 'error')
    // The refreshed list is the user's new baseline once they have been told the stock changed.
    if (anyStale) resetStockBaseline.current = true
  }
  const toggleColorGroup = (color: string) => setOpenColorGroups(current => current.includes(color) ? current.filter(item => item !== color) : [...current, color])
  return <RrModal crumb={mode === 'stock' ? 'Hızlı Stok Güncelleme' : 'Toplu Düzenleme'} title={contextTitle} desc={contextModelCode ? `Model Kodu: ${contextModelCode}` : undefined} size="lg" locked={saving} onClose={onClose} foot={<><span className="rr-note">{activeSelection.length ? <><b>{activeSelection.length} varyant</b> seçildi</> : 'Varyant seçimi bekleniyor'}</span><button type="button" className="rr-btn" onClick={onClose} disabled={saving}>Vazgeç</button><button type="submit" form="product-quick-edit-form" className="rr-btn rr-primary" disabled={saving || !selectionDraft.length}>{saving ? 'Uygulanıyor…' : 'Seçilenlere Uygula'}</button></>}>
    <form id="product-quick-edit-form" style={{ display: 'contents' }} onSubmit={apply}>
      <section className="rr-step"><header><span className="rr-stepn">1</span><div className="rr-h3">Varyantları Seç</div><div className="rr-step-tools">
        <RrAnchor trigger={(open, toggleOpen) => <button type="button" className={`rr-trigger rr-sm${open ? ' rr-on' : ''}`} onClick={toggleOpen}><span>Renk: {selectedColors.length ? `${selectedColors.length} Seçili` : 'Tümü'}</span><RrIcon name="chev" /></button>}>{() => <div className="rr-pop rr-right" role="dialog" style={{ minWidth: 240 }}><label className="rr-opt"><input type="checkbox" className="rr-cb" checked={!selectedColors.length} onChange={() => { setSelectedColors([]); applyFilterSelection([], selectedSizes) }} /><span className="rr-grow">Tümü</span><small>{variants.filter(item => !selectedSizes.length || selectedSizes.includes(sizeOf(item))).length} varyant</small></label>{colorOptions.map(color => <label key={color} className="rr-opt"><input type="checkbox" className="rr-cb" checked={selectedColors.includes(color)} onChange={() => toggleColor(color)} /><span className="rr-grow">{colorLabels[color] || formatCatalogOptionLabel(color)}</span><small>{groups[color].length} varyant</small></label>)}</div>}</RrAnchor>
        <RrAnchor trigger={(open, toggleOpen) => <button type="button" className={`rr-trigger rr-sm${open ? ' rr-on' : ''}`} onClick={toggleOpen}><span>Beden: {selectedSizes.length ? `${selectedSizes.length} Seçili` : 'Tümü'}</span><RrIcon name="chev" /></button>}>{() => <div className="rr-pop rr-right" role="dialog" style={{ minWidth: 220 }}>{sizeOptions.map(size => <label key={size} className="rr-opt"><input type="checkbox" className="rr-cb" checked={selectedSizes.includes(size)} onChange={() => toggleSize(size)} /><span className="rr-grow">{size}</span><small>{variants.filter(item => (!selectedColors.length || selectedColors.includes(colorOf(item))) && sizeOf(item) === size).length} varyant</small></label>)}</div>}</RrAnchor>
      </div></header>
        {colorOptions.map(color => {
          const items = groups[color]
          const isOpen = openColorGroups.includes(color)
          const selectedCount = items.filter(item => selectedSet.has(item.variant.id)).length
          const groupSelection = variantGroupSelectionState(items.map(item => item.variant.id), selectedSet)
          const groupLabel = colorLabels[color] || formatCatalogOptionLabel(color)
          return <div className={`rr-vgroup${isOpen ? ' rr-open' : ''}`} key={color}><div className="rr-vg-h"><input type="checkbox" className="rr-cb" checked={groupSelection.checked} data-mixed={groupSelection.indeterminate ? '' : undefined} aria-label={`${groupLabel} rengindeki tüm varyantları seç`} onChange={event => setSelectionDraft(current => updateVariantGroupSelection(current, items.map(item => item.variant.id), event.currentTarget.checked))} /><button type="button" className="rr-vg-t" aria-expanded={isOpen} onClick={() => toggleColorGroup(color)}><b>{groupLabel}</b><small>{selectedCount ? `${selectedCount}/${items.length} seçili` : `${items.length} varyant`}</small><RrIcon name="chev" /></button></div>{isOpen && items.map(item => { const parts = variantAxisParts(item.variant.optionSignature); return <div className={`rr-vline${selectedSet.has(item.variant.id) ? '' : ' rr-off'}`} key={item.variant.id}><input type="checkbox" className="rr-cb" checked={selectedSet.has(item.variant.id)} onChange={() => toggle(item.variant.id)} aria-label={`${item.variant.optionSignature || item.variant.sku} varyantını seç`} /><span><b>{parts.length ? parts.map(part => part.value).join(' · ') : 'Varyant'}</b><small>Stok Kodu: {item.variant.sku}</small></span><QuickEditVariantControls variant={item.variant} priceConnectionId={priceConnectionId} connectionCount={connections.length} onChanged={onChanged} onStockSaved={(variantId, onHand) => stockBaseline.current.set(variantId, onHand)} showPrice={mode !== 'stock'} onSelect={() => setSelectionDraft(current => current.includes(item.variant.id) ? current : [...current, item.variant.id])} /></div> })}</div>
        })}
      </section>
      <section className="rr-step"><header><span className="rr-stepn">2</span><div className="rr-h3">{mode === 'stock' ? 'Stok Değerini Düzenle' : mode === 'price' ? 'Fiyat Değerini Düzenle' : 'Fiyat ve Stok Değerini Düzenle'}</div><small className="rr-lead">{activeSelection.length ? `${activeSelection.length} seçili varyanta uygulanacak` : 'Varyant seçimi bekleniyor'}</small></header>
        {activeSelection.length ? <div className="rr-chips">{variants.filter(item => activeSelectedSet.has(item.variant.id)).map(item => <span key={item.variant.id} className="rr-chip">{variantAxisParts(item.variant.optionSignature).map(part => part.value).join(' · ') || item.variant.sku}</span>)}</div> : <p className="rr-lead">Değeri şimdi girebilirsiniz; uygulamak için en az bir varyantı işaretleyin.</p>}
        {needsPriceConnection && connections.length > 0 && <label className="rr-fld"><small>Fiyat Kanalı</small><select className="rr-in" aria-label="Fiyat kanalı" value={priceConnectionId} onChange={event => setPriceConnectionId(event.target.value)}><option value="">Kanal Seçin</option>{connections.map(connection => <option key={connection.id} value={connection.id}>{connection.displayName}</option>)}</select><small className="rr-lead">Henüz fiyatı olmayan varyantların ilk fiyatı bu kanala kaydedilir.</small></label>}
        <div className="rr-dgrid">{mode !== 'stock' && <><label className="rr-fld"><small>Liste Fiyatı</small><input className="rr-in rr-num" type="number" min="0" step="0.01" value={listPrice} onChange={event => setListPrice(event.target.value)} placeholder="Değiştirme" /></label><label className="rr-fld"><small>Satış Fiyatı</small><input className="rr-in rr-num" type="number" min="0" step="0.01" value={salePrice} onChange={event => setSalePrice(event.target.value)} placeholder="Değiştirme" /></label></>}{mode !== 'price' && <><label className="rr-fld"><small>Stok İşlemi</small><select className="rr-in" value={stockAction} onChange={event => setStockAction(event.target.value as typeof stockAction)}><option value="SET">Bu Sayıya Eşitle</option><option value="ADD">Bu Kadar Ekle (+)</option><option value="SUBTRACT">Bu Kadar Çıkar (−)</option></select></label><label className="rr-fld"><small>Miktar</small><input className="rr-in rr-num" type="number" min="0" step="1" value={stockAmount} onChange={event => setStockAmount(event.target.value)} placeholder="Miktar" /></label></>}</div>
      </section>
      {notice && <div className={`rr-callout${notice.startsWith('Başarısız:') ? ' rr-danger' : ''}`} role={notice.startsWith('Başarısız:') ? 'alert' : 'status'}><RrIcon name={notice.startsWith('Başarısız:') ? 'alert' : 'info'} /><span>{notice}</span></div>}
    </form>
  </RrModal>
}
function QuickEditVariantControls({ variant, priceConnectionId, connectionCount, onChanged, onSelect, onStockSaved, showPrice = true }: { variant: Variant; priceConnectionId: string; connectionCount: number; onChanged: () => Promise<unknown>; onSelect: () => void; onStockSaved?: (variantId: string, onHand: number) => void; showPrice?: boolean }) {
  const [price, setPrice] = useState(variant.salePrice ?? variant.listPrice ?? '')
  // The field shows the loaded stock until the user types; fresh data replaces it only while it is untouched, and a
  // save sends the typed value as an absolute stock against the value shown when editing started.
  const [stock, setStock] = useState(String(variant.onHand))
  const [stockDirty, setStockDirty] = useState(false)
  const [stockBase, setStockBase] = useState(variant.onHand)
  const [stockError, setStockError] = useState('')
  useEffect(() => { if (!stockDirty) { setStock(String(variant.onHand)); setStockBase(variant.onHand) } }, [stockDirty, variant.onHand])
  const [savingPrice, setSavingPrice] = useState(false); const [savingStock, setSavingStock] = useState(false)
  const [priceError, setPriceError] = useState('')
  // The first offer's create key lives as long as the field (see the bulk apply above).
  const [offerCreateKey] = useState(() => newIdempotencyKey('channel-offer-create'))
  const priceTarget = quickPriceTarget(variant, priceConnectionId, connectionCount)
  async function savePrice() {
    const salePrice = Number(price); if (savingPrice || price === '' || salePrice === (variant.salePrice ?? variant.listPrice ?? 0)) return
    if (!Number.isFinite(salePrice) || salePrice <= 0) { setPriceError('Fiyat sıfırdan büyük olmalıdır.'); return }
    if (priceTarget.kind === 'blocked') { setPriceError(priceTarget.reason); return }
    const listPrice = Math.max(variant.listPrice ?? salePrice, salePrice)
    const body = { connectionId: priceTarget.kind === 'create' ? priceTarget.connectionId : '', variantId: variant.id, listPrice, salePrice, currency: variant.currency || 'TRY', vatRate: variant.vatRate ?? 10, vatInclusion: variant.vatInclusion || 'INCLUDED', roundingMode: variant.roundingMode || 'HALF_EVEN', safetyStock: variant.safetyStock ?? 0, status: variant.offerStatus || 'ACTIVE', reason: 'Hızlı varyant fiyat düzenleme' }
    setSavingPrice(true); setPriceError('')
    try {
      if (priceTarget.kind === 'update') await hubApi(`/channel-offers/${priceTarget.offerId}`, { method: 'PATCH', headers: { 'If-Match': `"v${priceTarget.offerVersion}"` }, body: JSON.stringify(body) })
      else await sendWithVersionedIdempotencyKey(offerCreateKey, key => hubApi('/channel-offers', { method: 'POST', headers: { 'Idempotency-Key': key }, body: JSON.stringify(body) }))
    } catch (error) {
      setPriceError(`Fiyat kaydedilmedi: ${error instanceof Error ? error.message : 'beklenmeyen hata.'}`)
      return
    } finally {
      setSavingPrice(false)
    }
    await onChanged().catch(() => undefined)
  }
  async function saveStock() {
    if (savingStock || !stockDirty) return
    const parsed = parseStockInput(stock)
    if (parsed.kind === 'empty') { setStockError('Stok boş bırakılamaz; değişiklik kaydedilmedi.'); setStockDirty(false); return }
    if (parsed.kind === 'invalid') { setStockError('Stok sıfır veya pozitif bir tam sayı olmalıdır.'); return }
    if (parsed.value === stockBase) { setStockError(''); setStockDirty(false); return }
    setSavingStock(true); setStockError('')
    try {
      await setVariantOnHand({ variantId: variant.id, sku: variant.sku, onHand: parsed.value, expectedOnHand: stockBase }, 'Hızlı varyant stok düzenleme')
      onStockSaved?.(variant.id, parsed.value)
      setStockBase(parsed.value)
      setStockDirty(false)
    } catch (error) {
      setStockError(stockErrorMessage(error))
      // A refused change keeps nothing local: the field shows the stock the server now holds.
      if (isStaleStockError(error)) setStockDirty(false)
    } finally {
      setSavingStock(false)
      await onChanged().catch(() => undefined)
    }
  }
  return <><label className="rr-mini"><small>Stok</small><input className="rr-in rr-num" aria-label={`${variant.sku} stok`} aria-invalid={stockError ? true : undefined} title={stockError || undefined} value={stock} onFocus={onSelect} onChange={event => { onSelect(); setStockError(''); if (!stockDirty) setStockBase(variant.onHand); setStockDirty(true); setStock(event.target.value) }} onBlur={() => void saveStock()} type="number" min="0" step="1" disabled={savingStock} />{stockError && <small className="rr-field-error" role="alert">{stockError}</small>}</label>{showPrice && <label className="rr-mini"><small>Fiyat</small><input className="rr-in rr-num" aria-label={`${variant.sku} fiyat`} aria-invalid={priceError ? true : undefined} title={priceError || (priceTarget.kind === 'blocked' ? priceTarget.reason : undefined)} value={price} onFocus={onSelect} onChange={event => { onSelect(); setPriceError(''); setPrice(event.target.value === '' ? '' : Number(event.target.value)) }} onBlur={() => void savePrice()} type="number" min="0" step="0.01" disabled={savingPrice || priceTarget.kind === 'blocked'} />{priceError ? <small className="rr-field-error" role="alert">{priceError}</small> : priceTarget.kind === 'blocked' && <small className="rr-field-hint">{priceTarget.reason}</small>}</label>}</>
}
function lowStockModelCode(group: ProductGroup) {
  return group.products.map(product => product.modelCode).find(value => value?.trim())
    ?? group.variants.map(item => item.variant.modelCode).find(value => value?.trim())
    ?? 'Model kodu yok'
}
function lowStockProductColor(product: Product) {
  for (const variant of product.variants) {
    const color = preferredColorOption(variantOptionEntries(variant))
    if (color?.value) return color.value
  }
  return 'Renk belirtilmemiş'
}
function productCategoryLabel(product: Product) {
  return product.categoryPath?.trim() || 'Kategori belirtilmemiş'
}
function lowStockVariantLabel(variant: Variant) {
  const options = variantOptionEntries(variant)
    .sort((left, right) => left.name.localeCompare(right.name, 'tr', { sensitivity: 'base' }) || left.value.localeCompare(right.value, 'tr', { numeric: true, sensitivity: 'base' }))
  return options.map(option => `${option.name}: ${option.value}`).join(' · ') || variant.optionSignature || variant.sku
}
function lowStockProductImage(product: Product, fallback: Product | null = null) {
  return product.primaryImageUrl
    ?? product.variants.flatMap(variant => variant.mediaUrls ?? [])[0]
    ?? fallback?.primaryImageUrl
    ?? fallback?.variants.flatMap(variant => variant.mediaUrls ?? [])[0]
    ?? null
}
// The variant's own options (size and the like) without the colour, which already heads the list; the long attribute
// list is only a fallback for variants that carry no options.
function lowStockSizeLabel(variant: Variant) {
  const parts = variantAxisParts(variant.optionSignature).filter(part => !isColorOptionName(part.name))
  if (parts.length) return parts.map(part => `${part.name}: ${part.value}`).join(' · ')
  // Imported variants may carry every product attribute; only the variant-defining ones (size) name the variant.
  const sizes = variantOptionEntries(variant).filter(option => isVariantOptionName(option.name) && !isColorOptionName(option.name))
  return sizes.length ? sizes.map(option => `${formatCatalogOptionLabel(option.name)}: ${option.value}`).join(' · ') : variant.sku
}
function lowStockMissingVariantCount(group: ProductGroup) {
  return group.variants.filter(item => item.variant.onHand <= 0).length
}
export function LowStockDetailsModal({ products, loading, error, onClose, onImageClick }: { products: Product[]; loading: boolean; error: unknown; onClose: () => void; onImageClick: (url: string, title: string) => void }) {
  const groups = useMemo(() => productRowsAsCards(products), [products])
  const [search, setSearch] = useState('')
  const [categoryFilter, setCategoryFilter] = useState('')
  const [sort, setSort] = useState<'MODEL_ASC' | 'MODEL_DESC' | 'MISSING_DESC' | 'MISSING_ASC'>('MODEL_ASC')
  const categoryOptions = useMemo(() => [...new Set(products.map(productCategoryLabel))].sort((left, right) => left.localeCompare(right, 'tr', { sensitivity: 'base' })), [products])
  const filteredGroups = useMemo(() => {
    const query = search.trim().toLocaleLowerCase('tr-TR')
    return groups.flatMap(group => {
      const modelCode = lowStockModelCode(group)
      const groupText = [modelCode, ...group.products.flatMap(product => [product.title, productCategoryLabel(product), lowStockProductColor(product), ...product.variants.map(lowStockVariantLabel)])]
        .join(' ')
        .toLocaleLowerCase('tr-TR')
      if (query && !groupText.includes(query)) return []
      if (categoryFilter && !group.products.some(product => productCategoryLabel(product) === categoryFilter)) return []
      const matchingProducts = query
        ? group.products.filter(product => [product.title, productCategoryLabel(product), lowStockProductColor(product), ...product.variants.map(lowStockVariantLabel)].join(' ').toLocaleLowerCase('tr-TR').includes(query))
        : []
      const categoryProducts = categoryFilter ? group.products.filter(product => productCategoryLabel(product) === categoryFilter) : group.products
      const visibleProducts = matchingProducts.length > 0 ? matchingProducts.filter(product => categoryProducts.includes(product)) : categoryProducts
      const visibleVariants = matchingProducts.length > 0
        ? group.variants.filter(item => matchingProducts.includes(item.product) && categoryProducts.includes(item.product))
        : group.variants.filter(item => categoryProducts.includes(item.product))
      return [{ ...group, products: visibleProducts, variants: visibleVariants }]
    }).filter(group => group.products.length > 0).sort((left, right) => {
      if (sort === 'MODEL_DESC') return lowStockModelCode(right).localeCompare(lowStockModelCode(left), 'tr', { numeric: true, sensitivity: 'base' })
      if (sort === 'MISSING_DESC') return lowStockMissingVariantCount(right) - lowStockMissingVariantCount(left) || lowStockModelCode(left).localeCompare(lowStockModelCode(right), 'tr', { numeric: true, sensitivity: 'base' })
      if (sort === 'MISSING_ASC') return lowStockMissingVariantCount(left) - lowStockMissingVariantCount(right) || lowStockModelCode(left).localeCompare(lowStockModelCode(right), 'tr', { numeric: true, sensitivity: 'base' })
      return lowStockModelCode(left).localeCompare(lowStockModelCode(right), 'tr', { numeric: true, sensitivity: 'base' })
    })
  }, [categoryFilter, groups, search, sort])
  const lowVariantCount = filteredGroups.reduce((sum, group) => sum + lowStockMissingVariantCount(group), 0)
  const catalogRecordCount = filteredGroups.reduce((sum, group) => sum + group.products.length, 0)
  // Model cards start closed and sit two to a row; opening one opens its row neighbour too, so the row stays level.
  const [openFamilies, setOpenFamilies] = useState<Set<string>>(new Set())
  const [openColors, setOpenColors] = useState<Set<string>>(new Set())
  const twoColumns = useMediaQuery('(min-width: 761px)')
  function toggleFamilyRow(index: number) {
    const start = twoColumns ? index - index % 2 : index
    const ids = filteredGroups.slice(start, start + (twoColumns ? 2 : 1)).map(group => group.id)
    setOpenFamilies(current => { const next = new Set(current); const opening = !current.has(filteredGroups[index].id); ids.forEach(id => opening ? next.add(id) : next.delete(id)); return next })
  }

  return <RrModal crumb="Stok Takibi" title="Düşük Stoklu Ürünler" desc="Model koduna göre gruplanmış ürünlerin eksik varyantları." size="lg" onClose={onClose} foot={<><span className="rr-note">Düşük stok: bir renkteki varyantların en az yarısı tükenmiş.</span><button type="button" className="rr-btn" onClick={onClose}>Kapat</button></>}>
    {loading ? <RrLoading text="Düşük stoklu ürünler yükleniyor…" /> : error ? <RrError error={error} /> : !groups.length ? <div className="rr-empty"><span className="rr-ei"><RrIcon name="box" /></span><b>Düşük Stoklu Ürün Yok</b><span className="rr-lead">Tüm ürünlerin stoğu yeterli.</span></div> : <>
      <div className="rr-low-tools"><span style={{ position: 'relative' }}><RrIcon name="search" style={{ position: 'absolute', left: 11, top: 10, color: 'var(--rr-subtle)' }} /><input className="rr-in" style={{ paddingLeft: 34 }} value={search} onChange={event => setSearch(event.target.value)} placeholder="Model kodu, ürün veya renk ara" aria-label="Düşük stoklu ürünlerde ara" /></span><select className="rr-in" value={categoryFilter} onChange={event => setCategoryFilter(event.target.value)} aria-label="Kategori"><option value="">Tüm Kategoriler</option>{categoryOptions.map(category => <option key={category} value={category}>{category}</option>)}</select><select className="rr-in" value={sort} onChange={event => setSort(event.target.value as typeof sort)} aria-label="Sıralama"><option value="MODEL_ASC">Model Kodu (A-Z)</option><option value="MODEL_DESC">Model Kodu (Z-A)</option><option value="MISSING_DESC">Eksik Varyant (Çoktan Aza)</option><option value="MISSING_ASC">Eksik Varyant (Azdan Çoğa)</option></select></div>
      <p className="rr-lead">{filteredGroups.length} model · {catalogRecordCount} katalog kaydı · {lowVariantCount} eksik varyant</p>
      {!filteredGroups.length && <p className="rr-lead">Arama veya filtreye uyan düşük stoklu ürün bulunamadı.</p>}
      <div className="rr-lowgrid">{filteredGroups.map((group, index) => { const image = lowStockProductImage(group.primary); const open = openFamilies.has(group.id); const missing = lowStockMissingVariantCount(group); return <article className={`rr-lowf${open ? ' rr-open' : ''}`} key={group.id}>
        <div className="rr-lowf-h"><RrThumb src={image} alt={group.primary.title} onOpen={image ? () => onImageClick(image, group.primary.title) : undefined} /><button type="button" className="rr-lowf-t" aria-expanded={open} onClick={() => toggleFamilyRow(index)}><span><small>Model Kodu</small><b className="rr-num">{lowStockModelCode(group)}</b><em title={group.primary.title}>{group.primary.title}</em></span><span className="rr-lowf-c"><b className="rr-num">{missing}</b><small>eksik · {group.products.length} renk</small></span><RrIcon name="chev" /></button></div>
        {open && <section>{[...group.products].sort((left, right) => lowStockProductColor(left).localeCompare(lowStockProductColor(right), 'tr', { sensitivity: 'base' })).map(product => { const lowVariants = product.variants.filter(variant => variant.onHand <= 0).sort((left, right) => lowStockSizeLabel(left).localeCompare(lowStockSizeLabel(right), 'tr', { numeric: true, sensitivity: 'base' })); const colorOpen = openColors.has(product.id); return <div className={`rr-lowc${colorOpen ? ' rr-open' : ''}`} key={product.id}><button type="button" className="rr-lowc-h" aria-expanded={colorOpen} onClick={() => setOpenColors(current => { const next = new Set(current); if (next.has(product.id)) next.delete(product.id); else next.add(product.id); return next })}><b>{lowStockProductColor(product)}</b><span className="rr-lowc-n">{lowVariants.length} eksik varyant</span><RrIcon name="chev" /></button>{colorOpen && (lowVariants.length ? <div className="rr-lowvs">{lowVariants.map(variant => <div className="rr-lowv" key={variant.id}><span><b>{lowStockSizeLabel(variant)}</b><small>Stok Kodu: {variant.sku}{variant.barcode ? ` · Barkod: ${variant.barcode}` : ''}</small></span><span className={variant.onHand === 0 ? 'rr-zero' : 'rr-low'}>{variant.onHand === 0 ? 'Stoksuz' : <>Kalan <b className="rr-num">{variant.onHand}</b></>}</span></div>)}</div> : <p className="rr-lead">Bu renkte kritik stok varyantı bulunamadı.</p>)}</div> })}</section>}
      </article> })}</div>
    </>}
  </RrModal>
}
// The editor's publish step, loaded only when a row's "Platformlarda Yayınla" opens it in a popup.
const ProductPublishPopup = lazy(() => import('../product-editor/ProductEditorPages').then(module => ({ default: module.ProductPublishPopup })))

function ProductColorRows({ group, selected, onSelect, selectionMode: _selectionMode, onEnterSelection: _onEnterSelection, onQuickEdit, onImageClick, onDelete, onDuplicate, onSetStatus, onPublish }: { group: ProductGroup; selected: boolean; onSelect: () => void; selectionMode: boolean; onEnterSelection: () => void; onQuickEdit: (mode: QuickEditMode) => void; onImageClick: (url: string, title: string) => void; onDelete: () => void; onDuplicate: () => void; onSetStatus: (status: 'ACTIVE' | 'ARCHIVED') => void; onPublish: (productId: string) => void }) {
  const navigate = useNavigate()
  const product = group.primary
  const platformStatuses = group.products.flatMap(item => item.platformStatuses ?? [])
  const platformStatusAggregates = new Map<string, { platform: string; platformCode: string; statuses: string[]; matchedVariantCount: number; variantCount: number; isChecking: boolean }>()
  for (const item of platformStatuses) {
    const platform = item.platform?.trim() || 'Platform'
    const platformCode = item.platformCode?.trim().toUpperCase() || (platform.toUpperCase().includes('SHOPIFY') ? 'SHOPIFY' : platform.toUpperCase().includes('HEPSIBURADA') ? 'HEPSIBURADA' : platform.toUpperCase().includes('TRENDYOL') ? 'TRENDYOL' : platform.toUpperCase().includes('N11') ? 'N11' : platform.toUpperCase().includes('PAZARAMA') ? 'PAZARAMA' : platform.toUpperCase().replace(/\s/g, '').includes('PTTAVM') ? 'PTTAVM' : 'UNKNOWN')
    const key = `${platformCode}:${platform}`
    const current = platformStatusAggregates.get(key)
    if (current) {
      current.statuses.push(item.status)
      current.matchedVariantCount += item.matchedVariantCount ?? 0
      current.variantCount += item.variantCount ?? 0
      current.isChecking ||= Boolean(item.isChecking)
    } else {
      platformStatusAggregates.set(key, { platform, platformCode, statuses: [item.status], matchedVariantCount: item.matchedVariantCount ?? 0, variantCount: item.variantCount ?? 0, isChecking: Boolean(item.isChecking) })
    }
  }
  const platformCards = [...platformStatusAggregates.entries()].map(([key, item]) => {
    const state = productPlatformDisplayState(item.statuses, item.isChecking, item.matchedVariantCount)
    const label = productPlatformDisplayLabel(item.platform, item.statuses, item.matchedVariantCount, item.variantCount, state)
    return { key, platform: item.platform, platformCode: item.platformCode, state, label, matchedVariantCount: item.matchedVariantCount, variantCount: item.variantCount }
  })
  const totalStock = group.products.reduce((sum, item) => sum + item.totalStock, 0)
  const prices = group.products.map(item => item.startingPrice).filter((price): price is number => price != null)
  const startingPrice = prices.length ? Math.min(...prices) : null
  // The list price shows struck through when the cheapest variant sells below it.
  const cheapest = group.variants.map(item => item.variant).filter(variant => variant.salePrice != null && variant.salePrice === startingPrice)[0]
  const listPrice = cheapest?.listPrice != null && cheapest.listPrice > (cheapest.salePrice ?? 0) ? cheapest.listPrice : null
  const statuses = new Set(group.products.map(item => item.status))
  const status = statuses.size === 1 ? product.status : 'MIXED'
  const statusLabel = productStatusLabel(status)
  const variantDisplayGroups = productVariantDisplayGroups(group.variants.map(item => item.variant))
  // Sizes across all colours, in the order they first appear: the columns of the compact stock table.
  const variantSizes = [...new Set(variantDisplayGroups.flatMap(item => item.values.map(value => value.label)))]
  const modelCode = group.products.map(item => item.modelCode).find(value => value?.trim()) ?? '—'
  // Every marketplace is listed, those without a match greyed out. The dot is green when every variant is live or
  // connected there, yellow when only some are (or some failed), red when all failed or the marketplace no longer
  // knows any of them (Bulunamadı), and cycles while it updates.
  const platformMarks = (['TRENDYOL', 'HEPSIBURADA', 'N11', 'PAZARAMA', 'PTTAVM', 'SHOPIFY'] as const).map(code => {
    const card = platformCards.find(item => item.platformCode === code)
    const name = code === 'N11' ? 'N11' : code === 'PTTAVM' ? 'PTT AVM' : code.charAt(0) + code.slice(1).toLocaleLowerCase('tr-TR')
    const linkedTone = card && card.variantCount > 0 && card.matchedVariantCount < card.variantCount ? 'part' : 'on'
    const state = card ? ({ active: 'on', partial: 'part', linked: linkedTone, processing: 'busy', error: 'err', missing: 'err', inactive: 'none' } as Record<string, string>)[card.state] ?? 'none' : 'none'
    return { code, state, label: card?.label ?? `${name} ürün eşleşmesi bulunamadı` }
  })
  const [ringTone, ringProgress] = ({ ACTIVE: ['ok', 1], DRAFT: ['prog', 0.3], ARCHIVED: ['off', 0], MIXED: ['bad', 0.66] } as Record<string, [RingTone, number]>)[status] ?? ['off', 0]
  const statusLabelText = status === 'MIXED' ? 'Hatalı' : titleTr(statusLabel)
  const menuItems: Array<['edit' | 'export' | 'price' | 'dup' | 'on' | 'off' | 'trash', string, () => void, string?] | 'hr'> = [['edit', 'Ürünü Düzenle', () => navigate(`/products/${product.id}`)], ['export', 'Platformlarda Yayınla', () => onPublish(product.id)], ['price', 'Fiyat ve Stok Düzenle', () => onQuickEdit('both')], ['dup', 'Yeni Ürün Olarak Düzenle', onDuplicate], 'hr', status === 'ACTIVE' ? ['off', 'Satışa Kapat', () => onSetStatus('ARCHIVED')] : ['on', 'Satışa Aç', () => onSetStatus('ACTIVE')], ['trash', group.products.length > 1 ? 'Ürün Grubunu Sil' : 'Ürünü Sil', onDelete, 'rr-danger']]
  // Phones select a card only through its round checkbox; a hold or a tap elsewhere on the card no longer selects it.
  return <tr className={selected ? 'rr-sel' : undefined}>
    <td className="rr-wide"><div className="rr-prodcell"><input type="checkbox" className="rr-cb" aria-label={`${product.title} ürün grubunu seç`} checked={selected} onChange={onSelect} /><RrThumb src={product.primaryImageUrl} alt={product.title} onOpen={product.primaryImageUrl ? () => onImageClick(product.primaryImageUrl!, product.title) : undefined} /><div><b title={product.title}>{product.title}</b><small><span>Model Kodu:</span> <b className="rr-num">{modelCode}</b></small></div></div></td>
    <td className="rr-mid" data-l="Varyant"><span className={variantDisplayGroups.length > 0 ? 'rr-vhover' : 'rr-vhover rr-vhover-none'} tabIndex={variantDisplayGroups.length > 0 ? 0 : undefined} onMouseEnter={event => placeVariantTip(event.currentTarget)} onFocus={event => placeVariantTip(event.currentTarget)}><span className="rr-varcell"><b>{group.products.length} seçenek</b><small>{group.variants.length} varyant</small></span>{variantDisplayGroups.length > 0 && <span className="rr-tip rr-vtip" role="tooltip"><table className="rr-vtab"><thead><tr><th />{variantSizes.map(size => <th key={size}>{size}</th>)}</tr></thead><tbody>{variantDisplayGroups.map(item => <tr key={item.label}><th title={item.label}>{item.label}</th>{variantSizes.map(size => { const value = item.values.find(entry => entry.label === size); return <td key={size} className={value == null ? 'rr-none' : value.quantity === 0 ? 'rr-empty' : value.quantity < 5 ? 'rr-low' : undefined}>{value == null ? '—' : value.quantity}</td> })}</tr>)}</tbody></table></span>}</span></td>
    <td className="rr-mid" data-l="Fiyat"><button type="button" className="rr-qbtn" title="Fiyatı hızlı düzenle" aria-label={`${product.title}: fiyatı düzenle`} onClick={() => onQuickEdit('price')}><b className="rr-num">{formatMoney(startingPrice ?? 0, product.currency)}</b>{listPrice != null && <small className="rr-num">{formatMoney(listPrice, product.currency)}</small>}</button></td>
    <td className="rr-mid" data-l="Stok"><button type="button" className={`rr-qbtn${totalStock === 0 ? ' rr-zero' : ''}`} title="Stoğu hızlı düzenle" aria-label={`${product.title}: stoğu düzenle`} onClick={() => onQuickEdit('stock')}><b className="rr-num">{totalStock}</b><small>{totalStock === 0 ? 'Stoksuz' : 'adet'}</small></button></td>
    {/* eslint-disable-next-line jsx-a11y/no-noninteractive-tabindex -- tooltip trigger: focusable so keyboards can read the platform state */}
    <td className="rr-mid" data-l="Platform Durumu"><div className="rr-pmarks">{platformMarks.map(mark => <span key={mark.code} className={`rr-pm rr-${mark.state}`} tabIndex={0} data-tip={mark.label} aria-label={mark.label}><RrLogo code={mark.code} /><i /></span>)}</div></td>
    <td className="rr-mid" data-l="Durum"><RrRing tone={ringTone} progress={ringProgress} label={statusLabelText} /></td>
    <td className="rr-mid" data-l="İşlemler"><div className="rr-actcell"><RrAnchor trigger={(open, toggle) => <button type="button" className={`rr-trigger${open ? ' rr-on' : ''}`} aria-haspopup="menu" aria-expanded={open} onClick={toggle}><span>İşlemler</span><RrIcon name="chev" /></button>}>{close => <div className="rr-menu" role="menu" aria-label={`${product.title} işlemleri`}>{menuItems.map((entry, index) => entry === 'hr' ? <hr key={`hr-${index}`} /> : <button key={entry[1]} type="button" role="menuitem" className={entry[3] || undefined} onClick={() => { close(); entry[2]() }}><RrIcon name={entry[0]} />{entry[1]}</button>)}</div>}</RrAnchor><Link className="rr-btn rr-link" to={`/products/${product.id}`}>Düzenle <RrIcon name="open" /></Link></div></td>
  </tr>
}
type ProductDeleteRequest = {
  productIds: string[]
  product?: Product
  title: string
  description: string
}
function ProductDeleteConfirmModal({ request, deleting, onClose, onConfirm }: { request: ProductDeleteRequest; deleting: boolean; onClose: () => void; onConfirm: () => void }) {
  const isGroup = request.productIds.length > 1
  return <RrModal crumb="Katalog Kaydını Sil" title={isGroup ? 'Ürün Grubunu Sil' : 'Ürünü Sil'} desc="Bu işlem yerel katalog kaydını kalıcı olarak kaldırır. Sipariş geçmişi korunur; pazar yeri ilanı otomatik olarak silinmez." size="sm" locked={deleting} onClose={onClose} foot={<><span className="rr-note" /><button type="button" className="rr-btn" onClick={onClose} disabled={deleting}>Vazgeç</button><button type="button" className="rr-btn rr-solid" onClick={onConfirm} disabled={deleting}><RrIcon name="trash" />{deleting ? 'Siliniyor…' : isGroup ? `${request.productIds.length} Kaydı Sil` : 'Kalıcı Olarak Sil'}</button></>}>
    <div className="rr-callout rr-danger"><RrIcon name="alert" /><span><b>{request.title}</b><br />{request.description}</span></div>
    <p className="rr-lead">Devam ederseniz bu kayıtların varyant, stok, fiyat ve platform eşleşmeleri ile stok geçmişi (sipariş ve iade stok hareketleri, rezervasyonlar) silinir; siparişler ve iadeler kalır.</p>
  </RrModal>
}
const productPageSizes = [20, 50, 100]
// The list state a products address carries (W16); the page's cursor goes with it, so page 3 opens as page 3.
function productListFromUrl(params: URLSearchParams) {
  const filters: ProductListFilters = { search: readUrlText(params, 'q'), status: readUrlText(params, 'status'), platform: readUrlText(params, 'platform'), stock: readUrlText(params, 'stock'), sort: productCatalogSortValue(parseProductCatalogSort(readUrlText(params, 'sort'))) }
  const pageSize = readUrlNumber(params, 'size', 20, productPageSizes)
  const after = readUrlText(params, 'after')
  const pageNumber = after ? readUrlNumber(params, 'page', 1) : 1
  return { filters, pageSize, pageNumber, pageCursors: pageNumber > 1 ? { [JSON.stringify(filters)]: { [pageNumber]: after } } : {} }
}
// The options that tell one variant from another (colour, size, number). Imported variants carry every product
// attribute in their signature ("Renk: Yeşil | Beden: XL | Boy: Regular | Cinsiyet: …"); only these name the variant.
function variantAxisParts(signature: string | null | undefined) {
  const parts = variantOptionParts(signature)
  const axes = parts.filter(part => isVariantOptionName(part.name))
  return axes.length ? axes : parts.slice(0, 2)
}

// The variant stock table is placed on the window, not inside the list's scroll box (which cut off every colour past the
// box's edge): under its row, or above it when the window has more room there, as tall as all its colours need and
// only scrolling when the window itself is too short for them.
function placeVariantTip(anchor: HTMLElement) {
  const tip = anchor.querySelector<HTMLElement>('.rr-vtip')
  if (!tip) return
  const edge = 8, offset = 6
  const own = anchor.getBoundingClientRect()
  Object.assign(tip.style, { position: 'fixed', transform: 'none', right: 'auto', bottom: 'auto', maxHeight: 'none' })
  const needed = tip.scrollHeight || 260
  const below = window.innerHeight - own.bottom - offset - edge
  const above = own.top - offset - edge
  const up = below < needed && above > below
  const height = Math.max(96, Math.min(needed, up ? above : below))
  anchor.toggleAttribute('data-up', up)
  tip.style.maxHeight = `${height}px`
  tip.style.top = `${up ? own.top - offset - height : own.bottom + offset}px`
  tip.style.left = `${Math.max(edge, Math.min(own.left, window.innerWidth - tip.offsetWidth - edge))}px`
}

export function ProductsPage() {
  const [searchParams] = useSearchParams()
  const [initialList] = useState(() => productListFromUrl(searchParams))
  const [trackedProductImportJobIds, setTrackedProductImportJobIds] = useState<string[]>([])
  const [confirmImport, confirmImportDialog] = useConfirmDialog()
  const client = useQueryClient(); const navigate = useNavigate(); const [search, setSearch] = useState(initialList.filters.search); const [searchFilter, setSearchFilter] = useState(initialList.filters.search); const [status, setStatus] = useState(initialList.filters.status); const [platform, setPlatform] = useState(initialList.filters.platform); const [stock, setStock] = useState(initialList.filters.stock); const [selectedProductIds, setSelectedProductIds] = useState<string[]>([]); const [selectedProductCache, setSelectedProductCache] = useState<Record<string, Product>>({}); const [allProductsSelected, setAllProductsSelected] = useState(false); const [selectingAllProducts, setSelectingAllProducts] = useState(false); const [quickEdit, setQuickEdit] = useState<{ productIds: string[]; mode: QuickEditMode } | null>(null); const toastStack = useToastStack(); const [statusRequest, setStatusRequest] = useState<{ productIds: string[]; status: 'ACTIVE' | 'ARCHIVED'; skippedCount?: number } | null>(null); const [jumpPage, setJumpPage] = useState(''); const [deleteRequest, setDeleteRequest] = useState<ProductDeleteRequest | null>(null); const [deletingProducts, setDeletingProducts] = useState(false); const [productImportOpen, setProductImportOpen] = useState(false); const [productImportMethod, setProductImportMethod] = useState<ProductImportMethod>('BULK'); const [productImportConnectionIds, setProductImportConnectionIds] = useState<string[]>([]); const [productImportMode, setProductImportMode] = useState<ProductImportMode>('FULL'); const [productImportLookup, setProductImportLookup] = useState(''); const [productImportIncludeArchived, setProductImportIncludeArchived] = useState(true); const [productImportUpdateExistingProducts, setProductImportUpdateExistingProducts] = useState(true); const [productImportUpdateExistingPricesAndStock, setProductImportUpdateExistingPricesAndStock] = useState(false); const [productImporting, setProductImporting] = useState(false); const [lightboxImage, setLightboxImage] = useState<{ url: string; title: string } | null>(null); const [lowStockOpen, setLowStockOpen] = useState(() => searchParams.get('details') === 'low-stock'); const [transferOpen, setTransferOpen] = useState(false); const [bulkPublishOpen, setBulkPublishOpen] = useState(false); const [publishProductId, setPublishProductId] = useState<string | null>(null); const [pageSize, setPageSize] = useState(initialList.pageSize); const [pageNumber, setPageNumber] = useState(initialList.pageNumber); const [pageCursors, setPageCursors] = useState<Record<string, Record<number, string | null>>>(initialList.pageCursors)
  const [pageJumping, setPageJumping] = useState(false)
  const [catalogSort, setCatalogSort] = useState<ProductCatalogSort>(() => parseProductCatalogSort(initialList.filters.sort))
  const [productImportIncludePendingApproval, setProductImportIncludePendingApproval] = useState(false)
  const [expandedPlatformGroup, setExpandedPlatformGroup] = useState<string | null>(null)
  const productFilters = useMemo<ProductListFilters>(() => ({ search: searchFilter, status, platform, stock, sort: productCatalogSortValue(catalogSort) }), [searchFilter, status, platform, stock, catalogSort])
  const productFilterKey = JSON.stringify(productFilters)
  const pageCursor = pageCursors[productFilterKey]?.[pageNumber] ?? null
  const query = useQuery({
    queryKey: ['products', productFilters, pageSize, pageNumber, pageCursor],
    queryFn: () => fetchProductPage(pageSize, productFilters, pageCursor),
    placeholderData: keepPreviousData,
    staleTime: 30_000,
    refetchOnWindowFocus: true,
    refetchInterval: 60_000,
    refetchIntervalInBackground: true
  })
  const summaryQuery = useQuery({ queryKey: ['products', 'summary'], queryFn: () => hubApi<ProductSummary>('/products/summary'), staleTime: 30_000, refetchOnWindowFocus: true })
  // The summary has no draft or archived counts; a one-item page carries the total.
  const draftCountQuery = useQuery({ queryKey: ['products', 'count', 'DRAFT'], queryFn: () => fetchProductPage(1, { search: '', status: 'DRAFT', platform: '', stock: '', sort: '' }, null), staleTime: 60_000 })
  const archivedCountQuery = useQuery({ queryKey: ['products', 'count', 'ARCHIVED'], queryFn: () => fetchProductPage(1, { search: '', status: 'ARCHIVED', platform: '', stock: '', sort: '' }, null), staleTime: 60_000 })
  const lowStockQuery = useQuery({ queryKey: ['products', 'low-stock-details'], queryFn: () => loadAllPages<Product>('/products?stock=LOW', 200), enabled: lowStockOpen, staleTime: 15_000, refetchOnWindowFocus: true })
  const connectionsQuery = useQuery({ queryKey: ['connections', 'product-price'], queryFn: () => loadAllPages<MarketplaceConnection>('/connections') })
  const productSyncJobsQuery = useQuery({ queryKey: ['jobs', 'product-import'], queryFn: () => hubApi<ProductSyncJob[]>(productSyncJobsPath, { cache: 'no-store' }), enabled: productImportOpen, refetchInterval: query => productImportOpen && filterActiveProductSyncJobs(query.state.data ?? []).length ? 1500 : false, refetchIntervalInBackground: true, refetchOnWindowFocus: true, staleTime: 0 })
  const products = useMemo(() => query.data?.items ?? [], [query.data]); const connections = (connectionsQuery.data?.items ?? []).filter(isProductImportConnection); const publicationConnections = (connectionsQuery.data?.items ?? []).filter(isProductPublicationConnection); const selectedImportPlatforms = productImportConnectionIds.map(connectionId => connections.find(connection => connection.id === connectionId)?.platformCode ?? '').filter(Boolean); const selectedImportHasShopify = selectedImportPlatforms.some(code => code.trim().toUpperCase() === 'SHOPIFY'); const selectedImportHasHepsiburada = selectedImportPlatforms.some(code => code.trim().toUpperCase() === 'HEPSIBURADA'); const selectedImportOnlyHepsiburada = selectedImportPlatforms.length > 0 && selectedImportPlatforms.every(code => code.trim().toUpperCase() === 'HEPSIBURADA'); const singleLookupLabel = singleProductLookupLabel(selectedImportPlatforms[0] ?? ''); const platforms = summaryQuery.data?.platforms ?? []
  const selectedImportHasTrendyol = selectedImportPlatforms.some(code => code.trim().toUpperCase() === 'TRENDYOL')
  const productImportSupportsPendingApproval = productImportMethod === 'BULK' && selectedImportHasTrendyol && (productImportMode === 'FULL' || productImportMode === 'NEW_ONLY' || productImportMode === 'MAPPING_ONLY')
  const productImportOptions = getProductImportOptionVisibility({ method: productImportMethod, mode: productImportMode, onlyHepsiburada: selectedImportOnlyHepsiburada, hasHepsiburada: selectedImportHasHepsiburada, supportsPendingApproval: productImportSupportsPendingApproval })
  useEffect(() => { if (selectedImportOnlyHepsiburada && productImportMode === 'EXISTING_ONLY') setProductImportMode('MAPPING_ONLY') }, [selectedImportOnlyHepsiburada, productImportMode])
  const selectedPlatformFilter = productPlatformFilterGroups.flatMap(group => group.options.map(option => ({ ...option, group: group.label }))).find(option => option.value === platform)
  const selectedPlatformLabel = selectedPlatformFilter ? `${selectedPlatformFilter.group} · ${selectedPlatformFilter.label}` : platform || 'Tüm Platformlar'
  const totalCount = query.data?.totalCount ?? products.length; const totalPages = Math.max(1, Math.ceil(totalCount / pageSize)); const currentPage = Math.min(pageNumber, totalPages); const pageProducts = useMemo(() => currentPage === pageNumber ? products : [], [currentPage, pageNumber, products]); const pageProductGroups = useMemo(() => productRowsAsCards(pageProducts), [pageProducts])
  const activeProductSyncJobs = useMemo(() => filterActiveProductSyncJobs(productSyncJobsQuery.data ?? []), [productSyncJobsQuery.data])
  const cancelProductSync = useMutation({ mutationFn: (jobId: string) => hubApi(`/jobs/${jobId}/cancel`, { method: 'POST', headers: { 'Idempotency-Key': `cancel-product-import:${jobId}` } }), onSuccess: () => { void productSyncJobsQuery.refetch(); void client.invalidateQueries({ queryKey: ['jobs'] }) } })
  const quickEditProducts = quickEdit
    ? quickEdit.productIds.map(id => products.find(product => product.id === id) ?? selectedProductCache[id]).filter((product): product is Product => Boolean(product))
    : []
  const selectedProductCardCount = useMemo(() => productRowsAsCards(Object.values(selectedProductCache)).length, [selectedProductCache])
  const nextPageCursor = query.data?.nextCursor ?? null
  useEffect(() => {
    if (!productImportOpen || !activeProductSyncJobs.length) return
    const timer = window.setInterval(() => {
      void client.invalidateQueries({ queryKey: ['products', productFilters, pageSize, pageNumber, pageCursor] })
      void client.invalidateQueries({ queryKey: ['products', 'summary'] })
    }, 1500)
    return () => window.clearInterval(timer)
  }, [activeProductSyncJobs.length, client, pageCursor, pageNumber, pageSize, productFilters, productImportOpen])
  useEffect(() => {
    if (!productImportOpen || !productSyncJobsFinished(trackedProductImportJobIds, productSyncJobsQuery.data ?? [])) return
    void client.invalidateQueries({ queryKey: ['products'] })
    setTrackedProductImportJobIds([])
  }, [client, productImportOpen, productSyncJobsQuery.data, trackedProductImportJobIds])
  useEffect(() => { const timer = window.setTimeout(() => setSearchFilter(search.trim()), 250); return () => window.clearTimeout(timer) }, [search])
  useChangeEffect(`${productFilterKey}|${pageSize}`, () => { setPageNumber(1); setPageCursors({}); setSelectedProductIds([]); setSelectedProductCache({}); setAllProductsSelected(false) })
  useListUrlSync({ q: searchFilter, status, platform, stock, sort: productFilters.sort, size: pageSize, page: pageNumber, after: pageNumber > 1 ? pageCursor : null, details: lowStockOpen ? 'low-stock' : null }, { size: 20, page: 1 })
  useEffect(() => {
    if (query.data?.totalCount == null) return
    const nextTotalPages = Math.max(1, Math.ceil(query.data.totalCount / pageSize))
    setPageNumber(value => value > nextTotalPages ? nextTotalPages : value)
  }, [pageSize, query.data?.totalCount])
  useEffect(() => {
    if (query.isPlaceholderData || !nextPageCursor || !query.data?.hasMore) return
    const nextPage = currentPage + 1
    setPageCursors(current => current[productFilterKey]?.[nextPage] === nextPageCursor ? current : { ...current, [productFilterKey]: { ...(current[productFilterKey] ?? {}), [nextPage]: nextPageCursor } })
    void client.prefetchQuery({ queryKey: ['products', productFilters, pageSize, nextPage, nextPageCursor], queryFn: () => fetchProductPage(pageSize, productFilters, nextPageCursor), staleTime: 30_000 })
  }, [client, currentPage, nextPageCursor, pageSize, productFilterKey, productFilters, query.data?.hasMore, query.isPlaceholderData])
  useEffect(() => {
    if (!products.length || !selectedProductIds.length) return
    setSelectedProductCache(current => {
      let changed = false; const next = { ...current }
      for (const product of products) if (selectedProductIds.includes(product.id) && next[product.id] !== product) { next[product.id] = product; changed = true }
      return changed ? next : current
    })
  }, [products, selectedProductIds])
  useEffect(() => {
    if (!quickEdit?.productIds.length || !products.length) return
    const missingProducts = products.filter(product => quickEdit.productIds.includes(product.id) && !selectedProductCache[product.id])
    if (!missingProducts.length) return
    setSelectedProductCache(current => ({ ...current, ...Object.fromEntries(missingProducts.map(product => [product.id, product])) }))
  }, [products, quickEdit, selectedProductCache])
  const allVisibleSelected = pageProductGroups.length > 0 && pageProductGroups.every(group => group.products.every(product => selectedProductIds.includes(product.id)))
  const hasMoreProductsToSelect = totalCount > pageProductGroups.length
  const refresh = () => client.invalidateQueries({ queryKey: ['products'] })
  // Each result lands below the toasts already shown and goes to the notification history.
  function showProductToast(title: string, message: string, kind: 'success' | 'error' | 'info', context?: NotificationContext) {
    appendNotification({ ...context, title, message, kind })
    toastStack.show({ kind, title, message, durationMs: 4000 })
  }
  function openProductImport() {
    setProductImportMethod('BULK')
    setProductImportConnectionIds(connections.length === 1 ? [connections[0].id] : [])
    setProductImportMode('FULL')
    setProductImportLookup('')
    setProductImportIncludeArchived(true)
    setProductImportUpdateExistingProducts(true)
    setProductImportUpdateExistingPricesAndStock(false)
    setProductImportOpen(true)
  }
  async function importProductsFromPlatforms() {
    if (productImporting) return
    if (!productImportConnectionIds.length) { showProductToast('Bağlantı Seçilmedi', 'Aktarım için en az bir aktif pazar yeri bağlantısı seçin.', 'error'); return }
    if (productImportMethod === 'SINGLE' && productImportConnectionIds.length !== 1) { showProductToast('Tek Bağlantı Seçin', 'Tek ürün aktarımında yalnızca bir bağlantı seçilebilir.', 'error'); return }
    if (productImportMethod === 'SINGLE' && !productImportLookup.trim()) { showProductToast('Ürün Bilgisi Gerekli', `Tek ürün aktarımı için ${singleLookupLabel} girin.`, 'error'); return }
    // Decision D: the panel is the stock master; taking the marketplace's prices and stock over the panel's is a
    // deliberate step, confirmed here.
    if (productImportMethod === 'BULK' && productImportMode === 'EXISTING_ONLY' && productImportUpdateExistingPricesAndStock && !selectedImportOnlyHepsiburada) {
      const accepted = await confirmImport({ title: 'Panel Fiyat ve Stokları Değiştirilsin mi?', message: 'Mevcut ürünlerin panel fiyatı ve stoğu pazaryerindeki değerlerle değiştirilecek. Panel ana stoktur; bunu yalnız pazaryerindeki değerlerin doğru olduğundan eminseniz yapın. Değişen stoklar, otomatik gönderimi açık olan diğer pazaryerlerine de gönderilir.', confirmLabel: 'Pazaryeri Değerlerini Al', destructive: true })
      if (!accepted) return
    }
    setProductImporting(true)
    try {
      const full = productImportMode === 'FULL'
      const newOnly = productImportMode === 'NEW_ONLY'
      const existingOnly = productImportMode === 'EXISTING_ONLY'
      const mappingOnly = productImportMode === 'MAPPING_ONLY'
      const optionsOnly = false
      const includePendingApproval = productImportSupportsPendingApproval && productImportIncludePendingApproval
      const queuedProductSyncJobs = await Promise.all(productImportConnectionIds.map(connectionId => {
        const connection = connections.find(item => item.id === connectionId)
        const platformCode = connection?.platformCode.trim().toUpperCase()
        const isHepsiburada = platformCode === 'HEPSIBURADA'
        const updateExistingProducts = productImportMethod === 'BULK' && !mappingOnly && (full || existingOnly) && !isHepsiburada ? productImportUpdateExistingProducts : false
        const updateExistingPricesAndStock = productImportMethod === 'BULK' && existingOnly && !isHepsiburada && productImportUpdateExistingPricesAndStock
        const query = new URLSearchParams({ full: String(productImportMethod === 'BULK' && full), newOnly: String(productImportMethod === 'BULK' && newOnly), existingOnly: String(productImportMethod === 'BULK' && existingOnly), mappingOnly: String(productImportMethod === 'BULK' && mappingOnly), optionsOnly: String(productImportMethod === 'BULK' && optionsOnly), includeArchived: String(productImportIncludeArchived), includeDrafts: String(productImportMethod === 'BULK' && productImportIncludeArchived && platformCode === 'SHOPIFY'), includePendingApproval: String(includePendingApproval && platformCode === 'TRENDYOL'), updateExistingProducts: String(updateExistingProducts), updateExistingPricesAndStock: String(updateExistingPricesAndStock) })
        if (productImportMethod === 'SINGLE') query.set('lookup', productImportLookup.trim())
        return hubApi<AcceptedJob>(`/connections/${connectionId}/product-sync-jobs?${query.toString()}`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: '{}' })
      }))
      setTrackedProductImportJobIds(queuedProductSyncJobs.map(job => job.jobId))
      const modeLabel = productImportMethod === 'SINGLE' ? 'tek ürün' : full ? 'tam katalog' : productImportMode === 'NEW_ONLY' ? 'yeni ürün' : productImportMode === 'MAPPING_ONLY' ? 'ürün eşleme' : 'mevcut ürün güncelleme'
      const contentLabel = productImportMethod === 'BULK' && (full || existingOnly) ? selectedImportHasHepsiburada
        ? selectedImportOnlyHepsiburada ? ' · Hepsiburada ürün içeriği korunacak' : productImportUpdateExistingProducts ? ' · Hepsiburada içeriği korunacak, desteklenen platform içerikleri güncellenecek' : ' · mevcut ürün içerikleri korunacak'
        : productImportUpdateExistingProducts ? ' · mevcut ürün bilgileri güncellenecek' : ' · mevcut ürün bilgileri korunacak' : ''
       const priceStockLabel = productImportMethod === 'BULK' && existingOnly && !selectedImportOnlyHepsiburada ? productImportUpdateExistingPricesAndStock ? ' · mevcut fiyat ve stoklar güncellenecek' : ' · mevcut fiyat ve stoklar korunacak' : ''
       showProductToast('Ürün Aktarımı Kuyruğa Alındı', `${productImportConnectionIds.length} bağlantı için ${modeLabel} aktarımı kuyruğa alındı${productImportIncludeArchived ? selectedImportHasShopify ? ' · arşiv ve taslak ürünler dahil' : ' · arşiv ürünleri dahil' : ''}${includePendingApproval ? ' · onay bekleyen ürünler dahil' : ''}${contentLabel}${priceStockLabel}.`, 'info')
      void productSyncJobsQuery.refetch()
      void client.invalidateQueries({ queryKey: ['jobs'] })
    } catch (err) {
      showProductToast('Ürün Aktarımı Başlatılamadı', err instanceof Error ? err.message : 'Platform ürünleri panele alınamadı.', 'error')
    } finally {
      setProductImporting(false)
    }
  }
  function toggleProductGroup(group: ProductGroup) {
    const groupIds = group.products.map(product => product.id)
    const allSelected = groupIds.every(id => selectedProductIds.includes(id))
    setAllProductsSelected(false)
    setSelectedProductIds(ids => allSelected ? ids.filter(id => !groupIds.includes(id)) : [...new Set([...ids, ...groupIds])])
    setSelectedProductCache(current => {
      const next = { ...current }
      if (allSelected) groupIds.forEach(id => delete next[id])
      else group.products.forEach(product => { next[product.id] = product })
      return next
    })
  }
  function toggleAllVisible() {
    const pageIds = new Set(pageProducts.map(product => product.id))
    if (allProductsSelected) {
      setSelectedProductIds([])
      setSelectedProductCache({})
      setAllProductsSelected(false)
      return
    }
    if (allVisibleSelected) {
      setSelectedProductIds(ids => ids.filter(id => !pageIds.has(id)))
      setSelectedProductCache(current => { const next = { ...current }; pageIds.forEach(id => delete next[id]); return next })
      setAllProductsSelected(false)
    } else {
      setSelectedProductIds(ids => [...new Set([...ids, ...pageProducts.map(product => product.id)])])
      setSelectedProductCache(current => ({ ...current, ...Object.fromEntries(pageProducts.map(product => [product.id, product])) }))
    }
  }
  async function selectAllFilteredProducts() {
    if (selectingAllProducts || !hasMoreProductsToSelect) return
    setSelectingAllProducts(true)
    try {
      const params = new URLSearchParams()
      if (productFilters.search) params.set('search', productFilters.search)
      if (productFilters.status) params.set('status', productFilters.status)
      if (productFilters.platform) params.set('platform', productFilters.platform)
      if (productFilters.stock) params.set('stock', productFilters.stock)
      const all = await loadAllPages<Product>(`/products?${params.toString()}`, 200)
      const ids = all.items.map(product => product.id)
      setSelectedProductIds(ids)
      setSelectedProductCache(Object.fromEntries(all.items.map(product => [product.id, product])))
      setAllProductsSelected(true)
      showProductToast('Tüm Ürünler Seçildi', `${productRowsAsCards(all.items).length} ürün kartı · ${ids.length} katalog kaydı tüm sayfalardan seçildi.`, 'success')
    } catch (err) {
      showProductToast('Ürünler Seçilemedi', err instanceof Error ? err.message : 'Tüm sayfalardaki ürünler seçilemedi.', 'error')
    } finally {
      setSelectingAllProducts(false)
    }
  }
  function goToNextPage() {
    if (!nextPageCursor || currentPage >= totalPages) return
    const nextPage = currentPage + 1
    setPageCursors(current => current[productFilterKey]?.[nextPage] === nextPageCursor ? current : { ...current, [productFilterKey]: { ...(current[productFilterKey] ?? {}), [nextPage]: nextPageCursor } })
    setPageNumber(nextPage)
  }

  async function goToPage(nextPage: number) {
    const target = Math.min(Math.max(1, nextPage), totalPages)
    if (pageJumping || target === currentPage) return
    if (target === 1 || pageCursors[productFilterKey]?.[target] !== undefined) { setPageNumber(target); return }
    setPageJumping(true)
    try {
      let cursor = pageCursors[productFilterKey]?.[2] ?? null
      for (let page = 2; page <= target; page += 1) {
        const knownCursor = pageCursors[productFilterKey]?.[page]
        if (knownCursor !== undefined) { cursor = knownCursor; continue }
        const result = await fetchProductPage(pageSize, productFilters, cursor)
        if (!result.nextCursor) return
        cursor = result.nextCursor
        setPageCursors(current => ({ ...current, [productFilterKey]: { ...(current[productFilterKey] ?? {}), [page]: result.nextCursor } }))
      }
      setPageNumber(target)
    } catch (err) {
      showProductToast('Ürün Sayfası Yüklenemedi', err instanceof Error ? err.message : 'İstenen ürün sayfası yüklenemedi.', 'error')
    } finally {
      setPageJumping(false)
    }
  }

  // One bulk status run at a time: the window stays open and locked while it runs and shows how far it got; a failed
  // chunk leaves the products it did not reach selected, and the list is refreshed whatever happened.
  const statusRunning = useRef(false)
  const [statusProgress, setStatusProgress] = useState<{ done: number; total: number } | null>(null)
  async function bulkSetProductStatus(newStatus: 'ACTIVE' | 'ARCHIVED', targetIds: string[]) {
    if (!targetIds.length || statusRunning.current) return
    statusRunning.current = true
    setStatusProgress({ done: 0, total: targetIds.length })
    try {
      const result = await runInChunks(targetIds, 500, productIds => hubApi('/products/bulk-status', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ productIds, status: newStatus }) }), (done, total) => setStatusProgress({ done, total }))
      const outcome = bulkStatusResultMessage(productStatusLabel(newStatus), result)
      showProductToast(outcome.title, outcome.message, outcome.kind)
      const doneIds = new Set(result.doneIds)
      setSelectedProductIds(current => current.filter(id => !doneIds.has(id)))
      setSelectedProductCache(current => { const next = { ...current }; doneIds.forEach(id => delete next[id]); return next })
      if (doneIds.size) setAllProductsSelected(false)
    } finally {
      statusRunning.current = false
      setStatusProgress(null)
      setStatusRequest(null)
      await refresh().catch(() => undefined)
    }
  }
  // Records already in the target status are not sent again: an all-active selection is not offered Satışa Aç, and a
  // mixed one opens only the rest (owner, 30 Oct 2026).
  const loadedProductById = new Map(products.map(product => [product.id, product]))
  const productStatusOf = (id: string) => (selectedProductCache[id] ?? loadedProductById.get(id))?.status
  const bulkActivatePlan = bulkStatusPlan(selectedProductIds, productStatusOf, 'ACTIVE')
  const bulkArchivePlan = bulkStatusPlan(selectedProductIds, productStatusOf, 'ARCHIVED')
  function requestBulkStatus(newStatus: 'ACTIVE' | 'ARCHIVED') {
    const plan = newStatus === 'ACTIVE' ? bulkActivatePlan : bulkArchivePlan
    if (!statusRunning.current && plan.targetIds.length) setStatusRequest({ productIds: plan.targetIds, status: newStatus, skippedCount: plan.alreadyIds.length })
  }

  function requestDeleteProduct(product: Product) {
    setDeleteRequest({ productIds: [product.id], product, title: product.title, description: 'Tek bir katalog kaydı ve ona bağlı varyantlar silinecek.' })
  }

  function requestDeleteProductGroup(group: ProductGroup) {
    if (group.products.length === 1) return requestDeleteProduct(group.primary)
    const ids = group.products.map(product => product.id)
    setDeleteRequest({ productIds: ids, title: group.primary.title, description: `${ids.length} katalog kaydı aynı ürün kartında gruplanmış durumda; grubun tamamı silinecek.` })
  }

  async function confirmProductDelete() {
    const request = deleteRequest
    if (!request || deletingProducts) return
    setDeletingProducts(true)
    try {
      if (request.product && request.productIds.length === 1) {
        await hubApi(`/products/${request.product.id}`, { method: 'DELETE', headers: { 'If-Match': `"v${request.product.version}"`, 'Idempotency-Key': newIdempotencyKey() } })
      } else {
        for (let index = 0; index < request.productIds.length; index += 500) {
          const productIds = request.productIds.slice(index, index + 500)
          await hubApi('/products/bulk-delete', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ productIds }) })
        }
      }
      const targetIds = new Set(request.productIds)
      setSelectedProductIds(current => current.filter(id => !targetIds.has(id)))
      setSelectedProductCache(current => { const next = { ...current }; targetIds.forEach(id => delete next[id]); return next })
      setAllProductsSelected(false)
      setDeleteRequest(null)
      // A deleted product has no page any more: the notice names it without a link.
      showProductToast(request.productIds.length === 1 ? 'Ürün Silindi' : 'Ürünler Silindi', request.productIds.length === 1 ? 'Ürün ve varyantları yerel katalogdan silindi.' : `${request.productIds.length} katalog kaydı yerel katalogdan silindi.`, 'success', { operation: 'Katalogdan silme', subject: productNotificationSubject({ name: request.title, modelCode: request.product?.modelCode }) })
      await refresh()
    } catch (err) {
      const failure = notificationError(err, 'Ürün silinemedi.')
      showProductToast(request.productIds.length === 1 ? 'Ürün Silinemedi' : 'Ürünler Silinemedi', failure.message, 'error', { operation: 'Katalogdan silme', code: failure.code, subject: productNotificationSubject({ id: request.product?.id, name: request.title, modelCode: request.product?.modelCode }) })
    } finally {
      setDeletingProducts(false)
    }
  }

  function bulkDeleteProducts() {
    const ids = [...selectedProductIds]
    if (!ids.length) return
    setDeleteRequest({ productIds: ids, title: `${selectedProductCardCount} Ürün Kartı`, description: `${ids.length} katalog kaydı seçildi. Siparişler ve iadeler korunur; ürünlerin katalog ve stok kayıtları silinir.` })
  }

  const [selectionMode, setSelectionMode] = useState(false)
  function exitSelectionMode() { setSelectionMode(false); setSelectedProductIds([]); setSelectedProductCache({}); setAllProductsSelected(false) }
  function toggleCatalogSort(field: 'price' | 'stock') {
    setCatalogSort(current => nextProductCatalogSort(current, field))
  }

  const metric = (value: number | undefined) => summaryQuery.isLoading ? '…' : (value ?? 0).toLocaleString('tr-TR')
  const statusTabs: Array<[string, string, 'all' | 'done' | 'invoice' | 'paused']> = [['', 'Tüm Ürünler', 'all'], ['ACTIVE', 'Satışa Açık', 'done'], ['DRAFT', 'Taslak', 'invoice'], ['ARCHIVED', 'Satışa Kapalı', 'paused']]
  const tabCount = (value: string) => { const count = value === '' ? summaryQuery.data?.totalCount : value === 'ACTIVE' ? summaryQuery.data?.activeCount : value === 'DRAFT' ? draftCountQuery.data?.totalCount : archivedCountQuery.data?.totalCount; return count == null ? '…' : count.toLocaleString('tr-TR') }
  const stockOptions = [{ value: 'ALL', label: 'Tüm Stoklar' }, { value: 'OUT', label: 'Stoksuz' }, { value: 'LOW', label: 'Düşük Stok' }, { value: 'OK', label: 'Yeterli Stok' }]
  const statusOptions = statusTabs.map(([value, label]) => ({ value: value || 'ALL', label }))
  const sortOptions = productCatalogSortOptions
  const sortValue = productCatalogSortValue(catalogSort) || 'default'
  const setSortValue = (value: string) => setCatalogSort(parseProductCatalogSort(value))
  const platformPop = (close: () => void) => <div className="rr-pop" role="dialog" style={{ minWidth: 290 }}><div className="rr-ph"><span>Platform Durumu<small className="rr-ph-sub">Ürün eşleşmelerini platforma göre filtrele</small></span>{platform && <button type="button" className="rr-btn rr-link" onClick={() => { setPlatform(''); close() }}>Temizle</button>}</div><button type="button" className={`rr-opt${platform ? '' : ' rr-on'}`} onClick={() => { setPlatform(''); close() }}><span className="rr-radio" aria-hidden="true" /><span className="rr-grow">Tüm Platformlar<small className="rr-opt-sub">Platform ayrımı olmadan göster</small></span></button><div className="rr-sep" />{productPlatformFilterGroups.map(group => { const open = expandedPlatformGroup === group.label; const selectedOption = group.options.find(option => option.value === platform); return <div key={group.label} className={`rr-acc${open ? ' rr-open' : ''}`}><button type="button" className="rr-opt" aria-expanded={open} onClick={() => setExpandedPlatformGroup(value => value === group.label ? null : group.label)}><RrLogo code={group.label.toLocaleUpperCase('tr-TR').replace('İ', 'I')} /><span className="rr-grow">{group.label}<small className="rr-opt-sub">{selectedOption ? `${selectedOption.label} · Seçili` : 'Durum Seçin'}</small></span><RrIcon name="chev" /></button>{open && <div className="rr-acc-b">{group.options.map(option => { const on = platform === option.value; return <button key={option.value} type="button" className={`rr-opt${on ? ' rr-on' : ''}`} role="radio" aria-checked={on} onClick={() => { setPlatform(option.value); close() }}><span className="rr-radio" aria-hidden="true" /><span className="rr-grow">{option.label}<small className="rr-opt-sub">{option.label === 'Aktif' ? 'Tüm varyantlar eşleşti' : option.label === 'Kısmi' ? 'Bazı varyantlar eşleşti' : 'Eşleşme bulunamadı'}</small></span></button> })}</div>}</div> })}{platforms.filter(item => !productPlatformFilterGroups.some(group => group.label.localeCompare(item, 'tr-TR', { sensitivity: 'base' }) === 0)).map(item => <button key={item} type="button" className={`rr-opt${platform === item ? ' rr-on' : ''}`} onClick={() => { setPlatform(item); close() }}><span className="rr-radio" aria-hidden="true" /><span className="rr-grow">{item}<small className="rr-opt-sub">Platform eşleşmelerini göster</small></span></button>)}</div>
  const columnPop = (label: string, options: Array<{ value: string; label: string }>, current: string, pick: (value: string) => void, right?: boolean) => (close: () => void) => <RrColumnPop label={label} options={options} value={current || 'ALL'} right={right} onPick={value => { pick(value === 'ALL' ? '' : value); close() }} />
  const bulkItems: Array<['price' | 'on' | 'off' | 'trash' | 'export', string, string, () => void, string?, boolean?] | 'hr'> = [['export', 'Platformlara Yayınla', 'Seçili ürünleri pazar yerlerine gönder', () => setBulkPublishOpen(true)], 'hr', ['price', 'Toplu Fiyat ve Stok Düzenle', 'Seçili ürünlerin varyantları', () => setQuickEdit({ productIds: selectedProductIds, mode: 'both' })], ['price', 'Toplu Fiyat Düzenle', 'Fiyatları tek seferde güncelle', () => setQuickEdit({ productIds: selectedProductIds, mode: 'price' })], ['price', 'Toplu Stok Düzenle', 'Stokları artır, azalt veya eşitle', () => setQuickEdit({ productIds: selectedProductIds, mode: 'stock' })], 'hr', ['on', 'Toplu Satışa Aç', bulkActivatePlan.targetIds.length ? 'Seçili ürünleri satışa aç' : 'Seçili ürünlerin hepsi zaten satışa açık', () => requestBulkStatus('ACTIVE'), undefined, !bulkActivatePlan.targetIds.length], ['off', 'Toplu Satışa Kapat', bulkArchivePlan.targetIds.length ? 'Seçili ürünleri arşive al' : 'Seçili ürünlerin hepsi zaten satışa kapalı', () => requestBulkStatus('ARCHIVED'), 'rr-danger', !bulkArchivePlan.targetIds.length], ['trash', 'Seçili Ürünleri Sil', 'Yerel katalogdan kalıcı olarak kaldır', () => bulkDeleteProducts(), 'rr-danger']]
  const bulkMenu = (close: () => void) => <div className="rr-menu" role="menu" aria-label="Toplu işlemler" style={{ minWidth: 290 }}>{!selectedProductIds.length && <p className="rr-lead" style={{ padding: '6px 12px' }}>İşlem yapmak için önce en az bir ürün seçin.</p>}{bulkItems.map((entry, index) => entry === 'hr' ? <hr key={`hr-${index}`} /> : <button key={entry[1]} type="button" role="menuitem" className={['rr-two', entry[4]].filter(Boolean).join(' ')} disabled={!selectedProductIds.length || Boolean(entry[5])} onClick={() => { close(); entry[3]() }}><RrIcon name={entry[0]} /><span><b>{entry[1]}</b><small>{entry[2]}</small></span></button>)}</div>
  const pageList = (() => { const first = Math.max(1, Math.min(currentPage - 1, totalPages - 3)); return Array.from({ length: Math.min(4, totalPages) }, (_, index) => first + index) })()
  const statusRequestGroups = statusRequest ? productRowsAsCards(statusRequest.productIds.map(id => selectedProductCache[id] ?? products.find(product => product.id === id)).filter((product): product is Product => Boolean(product))) : []
  return <section className="content f3 rr rr-products">{confirmImportDialog}
    <div className="rr-ph"><div><div className="rr-crumb">Katalog</div><h1>Ürünler</h1><p>Ürün kartlarınızı, varyantlarını, fiyat ve stoklarını yönetin; pazar yerlerindeki yayın durumunu izleyin.</p></div><div className="rr-acts"><button type="button" className="rr-btn" onClick={() => setTransferOpen(true)} aria-haspopup="dialog"><RrIcon name="export" /><span className="rr-lbl-long">İçe / Dışa Aktar</span><span className="rr-lbl-short">Excel</span></button><button type="button" className="rr-btn" onClick={openProductImport}><RrIcon name="import" /><span className="rr-lbl-long">Platformdan Ürün Aktar</span><span className="rr-lbl-short">Ürün Aktar</span></button><Link className="rr-btn rr-primary" to="/products/new"><RrIcon name="plus" />Yeni Ürün Ekle</Link></div></div>
    <div className="rr-metrics rr-metrics-5"><article><small>Toplam Ürün</small><strong><CountUp value={metric(summaryQuery.data?.totalCount)} /></strong><span>katalogdaki ürün kartı</span></article><article><small>Aktif Ürün</small><strong><CountUp value={metric(summaryQuery.data?.activeCount)} /></strong><span>satışa açık</span></article><article><small>Stok Adeti</small><strong><CountUp value={metric(summaryQuery.data?.totalUnits)} /></strong><span>tüm varyantların toplamı</span></article><article className="rr-bad"><small>Stoksuz Ürün</small><strong><CountUp value={metric(summaryQuery.data?.outOfStockCount)} /></strong><span>tüm varyantları tükendi</span></article><button type="button" className="rr-metric-btn rr-warn" onClick={() => setLowStockOpen(true)} aria-haspopup="dialog"><small>Düşük Stoklu</small><strong><CountUp value={metric(summaryQuery.data?.lowStockCount)} /></strong><span>Ayrıntıları Gör <RrIcon name="arrowR" /></span></button></div>
    <div className="rr-fshell">
      <ViewTabsWithMore className="product-status-tabs" more={[]} ariaLabel="Ürün durumları" value={status || 'ALL'} onChange={value => setStatus(value === 'ALL' ? '' : value)} items={statusTabs.map(([value, label, icon]) => ({ value: value || 'ALL', label: viewTabLabel(icon, label), count: tabCount(value) }))} />
      <RrFilterCard active={[Boolean(platform), Boolean(stock), Boolean(catalogSort)].filter(Boolean).length}><div className="rr-filters rr-has-sort">
        <label className="rr-fld rr-search"><small>Ürün Ara</small><span style={{ position: 'relative' }}><RrIcon name="search" style={{ position: 'absolute', left: 11, top: 10, color: 'var(--rr-subtle)' }} /><input className="rr-in" style={{ paddingLeft: 34 }} aria-label="Ürün ara" placeholder="Ürün adı, stok kodu veya barkod ara" value={search} onChange={event => setSearch(event.target.value)} /></span></label>
        <div className="rr-fld"><small>Platform Durumu</small><RrAnchor trigger={(open, toggle) => <button type="button" className={`rr-trigger${open ? ' rr-on' : ''}`} onClick={() => { if (!open) setExpandedPlatformGroup(selectedPlatformFilter?.group ?? null); toggle() }}><span>{selectedPlatformLabel}</span><RrIcon name="chev" /></button>}>{platformPop}</RrAnchor></div>
        <div className="rr-fld"><small>Stok Durumu</small><RrAnchor trigger={(open, toggle) => <button type="button" className={`rr-trigger${open ? ' rr-on' : ''}`} onClick={toggle}><span>{stockOptions.find(option => option.value === (stock || 'ALL'))?.label}</span><RrIcon name="chev" /></button>}>{columnPop('Stok Durumu', stockOptions, stock, setStock)}</RrAnchor></div>
        <div className="rr-fld"><small>Sıralama</small><RrAnchor trigger={(open, toggle) => <button type="button" className={`rr-trigger${open ? ' rr-on' : ''}`} onClick={toggle}><span>{sortOptions.find(option => option[0] === sortValue)?.[1]}</span><RrIcon name="chev" /></button>}>{close => <div className="rr-pop rr-right" role="listbox" style={{ minWidth: 260 }}>{sortOptions.map(([value, label]) => { const on = value === sortValue; return <button key={value} type="button" className={`rr-opt${on ? ' rr-on' : ''}`} role="radio" aria-checked={on} onClick={() => { setSortValue(value); close() }}><span className="rr-radio" aria-hidden="true" /><span className="rr-grow">{label}</span></button> })}</div>}</RrAnchor></div>
        <div className="rr-fbtns"><button type="button" className="rr-btn" onClick={() => { setSearch(''); setStatus(''); setPlatform(''); setStock(''); setCatalogSort(null) }}>Temizle</button><button type="button" className="rr-btn rr-primary" onClick={() => setSearchFilter(search.trim())}>Filtrele</button></div>
      </div></RrFilterCard>
    </div>
    {selectionMode && <div className="rr-selbar" role="region" aria-label="Seçim modu"><button type="button" className="rr-btn rr-quiet" onClick={exitSelectionMode}>İptal</button><span className="rr-grow">{selectedProductCardCount} ürün kartı seçildi</span><button type="button" className="rr-btn" onClick={toggleAllVisible}>{allVisibleSelected ? 'Seçimi Kaldır' : 'Tümünü Seç'}</button></div>}
    {(query.error ?? summaryQuery.error ?? connectionsQuery.error) && <RrError error={query.error ?? summaryQuery.error ?? connectionsQuery.error} onRetry={() => { void query.refetch(); void summaryQuery.refetch() }} />}
    <div className="rr-panel"><div className="rr-panel-h"><div><h2>Ürün Kataloğu</h2><p>Toplam {totalCount.toLocaleString('tr-TR')} ürün</p></div><RrPageSize value={pageSize} sizes={[20, 50, 100]} onChange={setPageSize} /></div>
      {selectedProductIds.length > 0 && hasMoreProductsToSelect && <div className="rr-xsel" role="status"><span><b>{allProductsSelected ? 'Tüm ürün kartları seçildi.' : `${selectedProductCardCount.toLocaleString('tr-TR')} ürün kartı seçildi.`}</b> {allProductsSelected ? `${selectedProductIds.length.toLocaleString('tr-TR')} katalog kaydı toplu işlemlere dahil.` : 'Diğer sayfalardaki kartlar seçime dahil değil.'}</span>{allProductsSelected ? <button type="button" className="rr-btn rr-link" onClick={() => { setSelectedProductIds([]); setSelectedProductCache({}); setAllProductsSelected(false) }}>Seçimi Temizle</button> : <button type="button" className="rr-btn rr-link" onClick={() => void selectAllFilteredProducts()} disabled={selectingAllProducts}>{selectingAllProducts ? 'Tüm Sayfalar Seçiliyor…' : `Tüm ${totalCount.toLocaleString('tr-TR')} Kaydı Seç`}</button>}</div>}
      <div className="rr-tblwrap">{query.isLoading && !pageProducts.length ? <RrLoading text="Ürünler yükleniyor…" /> : <table className="rr-tbl rr-ptbl">
        <colgroup><col className="rr-c-pd" /><col className="rr-c-var" /><col className="rr-c-pr" /><col className="rr-c-st" /><col className="rr-c-pl" /><col className="rr-c-status" /><col className="rr-c-act" /></colgroup>
        <thead><tr>
          <RrHeadCell label="Ürün Detayı" extra={<input type="checkbox" className="rr-cb" checked={allVisibleSelected} onChange={toggleAllVisible} aria-label={`${pageProductGroups.length} ürün kartının tümünü seç`} style={{ marginRight: 8 }} />} />
          <RrHeadCell label="Varyant" />
          <RrHeadCell label="Fiyat" sort={catalogSort?.field === 'price' ? catalogSort.direction : null} onSort={() => toggleCatalogSort('price')} />
          <RrHeadCell label="Stok" sort={catalogSort?.field === 'stock' ? catalogSort.direction : null} onSort={() => toggleCatalogSort('stock')} active={Boolean(stock)} pop={columnPop('Stok Durumu', stockOptions, stock, setStock)} />
          <RrHeadCell label="Platform Durumu" active={Boolean(platform)} pop={platformPop} />
          <RrHeadCell label="Durum" active={Boolean(status)} pop={columnPop('Ürün Durumu', statusOptions, status, setStatus, true)} />
          <RrHeadCell label="İşlemler" />
        </tr></thead>
        <tbody>{!pageProducts.length ? <tr><td className="rr-empty-row" colSpan={7}><div className="rr-empty"><span className="rr-ei"><RrIcon name="box" /></span><b>Ürün Bulunamadı</b><span className="rr-lead">Filtrelerle eşleşen ürün yok.</span><button type="button" className="rr-btn rr-quiet" style={{ marginTop: 4 }} onClick={() => { setSearch(''); setStatus(''); setPlatform(''); setStock('') }}>Temizle</button></div></td></tr> : pageProductGroups.map(group => <ProductColorRows key={group.id} selectionMode={selectionMode} onEnterSelection={() => setSelectionMode(true)} group={group} selected={group.products.every(product => selectedProductIds.includes(product.id))} onSelect={() => toggleProductGroup(group)} onQuickEdit={mode => setQuickEdit({ productIds: group.products.map(product => product.id), mode })} onImageClick={(url, title) => setLightboxImage({ url, title })} onDelete={() => requestDeleteProductGroup(group)} onDuplicate={() => navigate('/products/new', { state: { duplicateProductId: group.primary.id } })} onSetStatus={next => { const plan = bulkStatusPlan(group.products.map(product => product.id), id => group.products.find(product => product.id === id)?.status, next); if (!plan.targetIds.length) return; setSelectedProductCache(current => ({ ...current, ...Object.fromEntries(group.products.map(product => [product.id, product])) })); setStatusRequest({ productIds: plan.targetIds, status: next, skippedCount: plan.alreadyIds.length }) }} onPublish={setPublishProductId} />)}</tbody>
      </table>}</div>
      {totalCount > 0 && <div className="rr-pager"><span>Toplam <b style={{ color: 'var(--rr-ink)' }}>{totalCount.toLocaleString('tr-TR')}</b> adet</span><div className="rr-pages"><button type="button" aria-label="Önceki" disabled={currentPage <= 1 || query.isFetching || pageJumping} onClick={() => setPageNumber(value => Math.max(1, value - 1))}><RrIcon name="left" /></button>{pageList.map(value => <button key={value} type="button" className={value === currentPage ? 'rr-on' : undefined} aria-current={value === currentPage ? 'page' : undefined} disabled={query.isFetching || pageJumping} onClick={() => void goToPage(value)}>{value}</button>)}<button type="button" aria-label="Sonraki" disabled={!(currentPage < totalPages && Boolean(nextPageCursor)) || query.isFetching || pageJumping} onClick={goToNextPage}><RrIcon name="right" /></button></div><label className="rr-jump">Sayfaya Git<input className="rr-in" type="number" min={1} max={totalPages} placeholder={`1–${totalPages}`} aria-label="Sayfa numarası" value={jumpPage} onChange={event => setJumpPage(event.target.value)} onKeyDown={event => { if (event.key === 'Enter' && Number(jumpPage) >= 1) { void goToPage(Math.floor(Number(jumpPage))); setJumpPage('') } }} /><button type="button" className="rr-btn" onClick={() => { if (Number(jumpPage) >= 1) { void goToPage(Math.floor(Number(jumpPage))); setJumpPage('') } }}>Git</button></label></div>}
    </div>
    {selectedProductIds.length > 0 && <div className="rr-bulk rr-pbulk" role="group" aria-label="Toplu işlemler"><span className="rr-grow rr-bulk-count"><span>{allProductsSelected ? 'Tüm ürün kartları seçildi' : `${selectedProductCardCount.toLocaleString('tr-TR')} ürün kartı seçildi`}</span>{hasMoreProductsToSelect && !allProductsSelected && <button type="button" className="rr-btn rr-link rr-bulk-all" onClick={() => void selectAllFilteredProducts()} disabled={selectingAllProducts}>{selectingAllProducts ? 'Seçiliyor…' : `Tümünü Seç (${totalCount.toLocaleString('tr-TR')})`}</button>}</span><button type="button" className="rr-btn rr-quiet rr-bulk-clear" aria-label="Seçimi Temizle" title="Seçimi Temizle" onClick={() => { setSelectedProductIds([]); setSelectedProductCache({}); setAllProductsSelected(false) }}><RrIcon name="close" /><span>Seçimi Temizle</span></button>{bulkActivatePlan.targetIds.length > 0 && <button type="button" className="rr-btn" title={bulkActivatePlan.alreadyIds.length ? `${bulkActivatePlan.alreadyIds.length.toLocaleString('tr-TR')} kayıt zaten satışa açık; yalnızca kalan ${bulkActivatePlan.targetIds.length.toLocaleString('tr-TR')} kayıt açılır.` : undefined} onClick={() => requestBulkStatus('ACTIVE')}><RrIcon name="on" />Satışa Aç</button>}<button type="button" className="rr-btn" onClick={() => setQuickEdit({ productIds: selectedProductIds, mode: 'both' })}><RrIcon name="price" /><span className="rr-lbl-long">Fiyat ve Stok Düzenle</span><span className="rr-lbl-short">Fiyat · Stok</span></button><RrAnchor trigger={(open, toggle) => <button type="button" className="rr-btn rr-primary" aria-haspopup="menu" aria-expanded={open} onClick={toggle}><span className="rr-lbl-long">Toplu İşlemler ({selectedProductCardCount.toLocaleString('tr-TR')})</span><span className="rr-lbl-short">Toplu ({selectedProductCardCount.toLocaleString('tr-TR')})</span><RrIcon name="chev" /></button>}>{close => <div className="rr-bulk-menu">{bulkMenu(close)}</div>}</RrAnchor></div>}
    {statusRequest && <RrModal crumb="Toplu İşlem" title={statusRequest.status === 'ACTIVE' ? 'Satışa Aç' : 'Satışa Kapat'} desc={`${statusRequestGroups.length || statusRequest.productIds.length} ürün kartı ${statusRequest.status === 'ACTIVE' ? 'satışa açılacak' : 'arşive alınacak'}.`} size="sm" locked={statusProgress != null} onClose={() => setStatusRequest(null)} foot={<><span className="rr-note" role="status">{statusProgress ? `${statusProgress.done.toLocaleString('tr-TR')} / ${statusProgress.total.toLocaleString('tr-TR')} ürün güncellendi` : ''}</span><button type="button" className="rr-btn" onClick={() => setStatusRequest(null)} disabled={statusProgress != null}>Vazgeç</button><button type="button" className={`rr-btn ${statusRequest.status === 'ACTIVE' ? 'rr-primary' : 'rr-danger'}`} disabled={statusProgress != null} onClick={() => void bulkSetProductStatus(statusRequest.status, statusRequest.productIds)}>{statusProgress ? 'Uygulanıyor…' : statusRequest.status === 'ACTIVE' ? 'Satışa Aç' : 'Satışa Kapat'}</button></>}>
      <div className="rr-sum">{statusRequestGroups.slice(0, 6).map(group => <div key={group.id}><RrThumb src={group.primary.primaryImageUrl} alt={group.primary.title} /><span>{group.primary.title}<small>{group.primary.modelCode || '—'} · {group.variants.length} varyant</small></span><RrBadge label={group.products.every(product => product.status === 'ACTIVE') ? 'Satışa Açık' : titleTr(productStatusLabel(group.primary.status))} /></div>)}</div>
      {statusRequestGroups.length > 6 && <p className="rr-lead">ve {statusRequestGroups.length - 6} ürün kartı daha</p>}
      {(statusRequest.skippedCount ?? 0) > 0 && <p className="rr-lead">{(statusRequest.skippedCount ?? 0).toLocaleString('tr-TR')} kayıt zaten {statusRequest.status === 'ACTIVE' ? 'satışa açık' : 'satışa kapalı'}; bu kayıtlar gönderilmez.</p>}
      {statusRequest.status === 'ARCHIVED' && <div className="rr-callout rr-warn"><RrIcon name="alert" /><span>Pazar yerlerindeki ilanlar bu işlemle kapanmaz; kapatmak için ürün düzenleme ekranından yayını durdurun.</span></div>}
    </RrModal>}
    {quickEdit && <ProductQuickEditModal products={quickEditProducts} connections={publicationConnections} mode={quickEdit.mode} onChanged={refresh} onResult={showProductToast} onClose={() => setQuickEdit(null)} />}
    {deleteRequest && <ProductDeleteConfirmModal request={deleteRequest} deleting={deletingProducts} onClose={() => setDeleteRequest(null)} onConfirm={() => void confirmProductDelete()} />}
    {productImportOpen && <RrModal crumb="Katalog Aktarımı" title="Platformdan Ürün Aktarımı" desc="Bağlı pazar yerlerindeki ürünleri panel kataloğunuza aktarın. İşlem salt okunurdur; pazar yerlerine hiçbir veri gönderilmez." size="lg" locked={productImporting} onClose={() => setProductImportOpen(false)} foot={<><span className="rr-note">{productImportConnectionIds.length ? <><b>{productImportConnectionIds.length} bağlantı</b> seçili · {productImportMethod === 'SINGLE' ? 'Tek Ürün Aktarımı' : titleTr(productImportModeLabels[productImportMode] ?? '')}</> : 'Bağlantı seçilmedi'}</span><button type="button" className="rr-btn" onClick={() => setProductImportOpen(false)} disabled={productImporting}>Vazgeç</button><button type="button" className="rr-btn rr-primary" onClick={() => void importProductsFromPlatforms()} disabled={productImporting || !productImportConnectionIds.length}><RrIcon name="import" />{productImporting ? 'Kuyruğa Alınıyor…' : 'Aktarımı Başlat'}</button></>}>
      <div className="rr-h3">Pazar Yeri Bağlantıları</div>
      {connections.length ? <div className="rr-srcs rr-conns">{connections.map(connection => { const selected = productImportConnectionIds.includes(connection.id); return <label key={connection.id} className={`rr-src${selected ? ' rr-on' : ''}`} title={`${connection.platformCode} · ${connection.externalStoreId}`}><input type="checkbox" className="rr-cb" checked={selected} onChange={() => setProductImportConnectionIds(ids => selected ? ids.filter(id => id !== connection.id) : productImportMethod === 'SINGLE' ? [connection.id] : [...ids, connection.id])} /><RrLogo code={connection.platformCode} /><span><b><span>{connection.displayName}</span><span className="rr-src-ok" title="Aktif bağlantı" aria-label="Aktif bağlantı"><RrIcon name="check" size={13} /></span></b><small>Mağaza {shortStoreId(connection.externalStoreId)}</small></span></label> })}</div> : <div className="rr-callout rr-warn"><RrIcon name="alert" /><span>Aktarıma uygun, aktif veya doğrulanmış bir pazar yeri bağlantısı bulunamadı.</span></div>}
      <div className="rr-seg" role="tablist" aria-label="Ürün aktarım türü"><button type="button" role="tab" aria-selected={productImportMethod === 'BULK'} className={productImportMethod === 'BULK' ? 'rr-on' : undefined} onClick={() => setProductImportMethod('BULK')}>Toplu Aktarım</button><button type="button" role="tab" aria-selected={productImportMethod === 'SINGLE'} className={productImportMethod === 'SINGLE' ? 'rr-on' : undefined} onClick={() => { setProductImportMethod('SINGLE'); setProductImportConnectionIds(ids => ids.slice(0, 1)) }}>Tek Ürün Aktarımı</button></div>
      {productImportMethod === 'SINGLE' ? <label className="rr-fld"><small>{titleTr(singleLookupLabel)}</small><input className="rr-in" value={productImportLookup} onChange={event => setProductImportLookup(event.target.value)} placeholder={selectedImportHasShopify ? 'Shopify: ürün linki veya barkod / stok kodu' : selectedImportHasHepsiburada ? 'Hepsiburada: ürün ID’si' : 'Trendyol: ürün linki veya model kodu'} /><em>Hepsiburada için ürün ID’si, Shopify için ürün linki veya barkod / stok kodu girin.</em></label> : <><div className="rr-h3">Aktarım Kapsamı</div><div className="rr-radios">{([['FULL', 'Tam Katalog Taraması', selectedImportHasHepsiburada ? 'Tüm Hepsiburada ürünlerini okur; panelde var olan ürün içeriği değişmez.' : 'Pazar yerindeki tüm ürünleri okur ve panel kayıtlarını günceller.'], ['NEW_ONLY', 'Yeni Ürünleri Aktar', 'Yalnızca panelde henüz bulunmayan ürünleri ekler.'], ...(selectedImportOnlyHepsiburada ? [] : [['EXISTING_ONLY', 'Mevcut Ürünleri Güncelle', 'Panelde kayıtlı ürünlerin bilgilerini pazar yerindeki haline göre yeniler.']]), ['MAPPING_ONLY', 'Ürün Eşleme', 'Panel ürünlerini pazar yerindeki karşılıklarıyla eşler; içerik, fiyat ve stok değişmez.']] as Array<[typeof productImportMode, string, string]>).map(([value, label, description]) => <label key={value} className={`rr-rcard${productImportMode === value ? ' rr-on' : ''}`}><input type="radio" name="product-import-mode" value={value} checked={productImportMode === value} onChange={() => setProductImportMode(value)} hidden /><span className="rr-radio-dot" aria-hidden="true" /><span><b>{label}</b><small>{description}</small></span></label>)}</div></>}
      {productImportOptions.showOptionsSection && <><div className="rr-h3">Gelişmiş Seçenekler</div><div className="rr-srcs">
        {productImportOptions.showUpdateExisting && <label className={`rr-src${productImportUpdateExistingProducts ? ' rr-on' : ''}`}><input type="checkbox" className="rr-cb" checked={productImportUpdateExistingProducts} onChange={event => setProductImportUpdateExistingProducts(event.target.checked)} /><span><b>Mevcut Ürün Bilgilerini Güncelle</b><small>{productImportUpdateExistingProducts ? 'Açık: ad, açıklama, kategori, marka, varyant ve görseller güncellenir.' : 'Kapalı: panelde var olan ürün içeriği ve görseller korunur.'}</small></span></label>}
        {productImportOptions.showUpdateExisting && <label className={`rr-src${productImportUpdateExistingPricesAndStock ? ' rr-on' : ''}`}><input type="checkbox" className="rr-cb" checked={productImportUpdateExistingPricesAndStock} onChange={event => setProductImportUpdateExistingPricesAndStock(event.target.checked)} /><span><b>Fiyat ve Stokları Güncelle</b><small>{productImportUpdateExistingPricesAndStock ? 'Açık: fiyat ve stok değerleri pazar yerinden panele alınır.' : 'Kapalı: panelde var olan fiyat ve stok değerleri korunur.'}</small></span></label>}
        {productImportOptions.showHepsiburadaReadOnlyNote && <div className="rr-src"><span><b>Hepsiburada İçerik Koruması</b><small>Hepsiburada ürünleri yalnızca okunur; panelde var olan ürün içeriğinin üzerine yazılmaz.</small></span></div>}
        {productImportOptions.showArchived && <label className={`rr-src${productImportIncludeArchived ? ' rr-on' : ''}`}><input type="checkbox" className="rr-cb" checked={productImportIncludeArchived} onChange={event => setProductImportIncludeArchived(event.target.checked)} /><span><b>{selectedImportHasShopify ? 'Arşiv ve Taslak Ürünlerini Dahil Et' : 'Arşiv Ürünlerini Dahil Et'}</b><small>{productImportIncludeArchived ? 'Açık: aktif ve arşivlenmiş ürünler birlikte aktarılır.' : 'Kapalı: yalnızca aktif ürünler aktarılır.'}</small></span></label>}
        {productImportOptions.showPendingApproval && <label className={`rr-src${productImportIncludePendingApproval ? ' rr-on' : ''}`}><input type="checkbox" className="rr-cb" checked={productImportIncludePendingApproval} onChange={event => setProductImportIncludePendingApproval(event.target.checked)} /><span><b>Onay Bekleyen Ürünleri Dahil Et</b><small>{productImportIncludePendingApproval ? 'Açık: onay sürecindeki Trendyol ürünleri de işlenir.' : 'Kapalı: yalnızca onaylı Trendyol ürünleri işlenir.'}</small></span></label>}
      </div></>}
      {activeProductSyncJobs.length > 0 && <section className="rr-dsec" aria-live="polite"><header><div className="rr-h3">Devam Eden Aktarımlar</div><p>{activeProductSyncJobs.length} işlem</p></header>{activeProductSyncJobs.map(job => { const received = Math.max(0, job.progressReceived); const total = job.progressTotal != null && job.progressTotal >= received ? job.progressTotal : null; const percent = job.progressPercent != null ? Math.min(100, Math.max(0, job.progressPercent)) : total != null && total > 0 ? Math.min(100, Math.max(0, Math.floor(received * 100 / total))) : null; const label = job.progressLabel ?? (job.status === 'PENDING' ? 'Kuyrukta bekliyor' : 'Aktarım çalışıyor'); const mapping = job.progressLabel?.includes('Eşlenen') ?? false; const jobConnection = (connectionsQuery.data?.items ?? []).find(item => item.id === job.connectionId); return <div className="rr-job" key={job.id}><div className="rr-job-h">{jobConnection && <RrLogo code={jobConnection.platformCode} />}<b>{jobConnection && <span className="rr-job-src">{jobConnection.displayName}</span>}{label}</b><span className="rr-num">{percent == null ? '—' : `%${percent}`}</span><button type="button" className="rr-btn rr-quiet" style={{ height: 28 }} disabled={cancelProductSync.isPending} onClick={() => cancelProductSync.mutate(job.id)}>Durdur</button></div><span className="rr-bar"><i style={{ width: `${percent ?? 8}%` }} /></span><div className="rr-job-c"><span>Alınan <b className="rr-num">{received.toLocaleString('tr-TR')}</b></span><span>{mapping ? 'Eşlenen' : 'İşlenen'} <b className="rr-num">{job.progressProcessed.toLocaleString('tr-TR')}</b></span><span>{mapping ? 'Eşleşmeyen' : 'Atlanan'} <b className="rr-num">{job.progressSkipped.toLocaleString('tr-TR')}</b></span><span>Hatalı <b className="rr-num rr-bad">{job.progressFailed.toLocaleString('tr-TR')}</b></span></div></div> })}</section>}
    </RrModal>}
    {transferOpen && <ProductTransferDialog onClose={() => setTransferOpen(false)} />}
    {publishProductId && <Suspense fallback={null}><ProductPublishPopup productId={publishProductId} onClose={() => { setPublishProductId(null); void client.invalidateQueries({ queryKey: ['products'] }) }} /></Suspense>}
    {/* One product card (a whole model family) opens the very same popup as its row's Platformlarda Yayınla. */}
    {bulkPublishOpen && selectedProductCardCount === 1 && selectedProductIds[0] && <Suspense fallback={null}><ProductPublishPopup productId={selectedProductIds[0]} onClose={() => { setBulkPublishOpen(false); void client.invalidateQueries({ queryKey: ['products'] }) }} /></Suspense>}
    {bulkPublishOpen && selectedProductCardCount !== 1 && <Suspense fallback={null}><BulkPublishDialog productIds={selectedProductIds} cardCount={selectedProductCardCount} connections={publicationConnections} products={[...Object.values(selectedProductCache), ...products]} onClose={() => setBulkPublishOpen(false)} onDone={(message, kind) => showProductToast(kind === 'success' ? 'Toplu Yayın Kuyruğa Alındı' : 'Toplu Yayın Kısmen Gönderildi', message, kind)} /></Suspense>}
    {lowStockOpen && <LowStockDetailsModal products={lowStockQuery.data?.items ?? []} loading={lowStockQuery.isLoading} error={lowStockQuery.error} onClose={() => setLowStockOpen(false)} onImageClick={(url, title) => setLightboxImage({ url, title })} />}
    {lightboxImage && <ImageLightboxModal image={lightboxImage} onClose={() => setLightboxImage(null)} />}
  </section>
}
const productImportModeLabels: Record<string, string> = { FULL: 'Tam katalog taraması', NEW_ONLY: 'Yeni ürünleri aktar', EXISTING_ONLY: 'Mevcut ürünleri güncelle', MAPPING_ONLY: 'Ürün eşleme' }
function shortStoreId(id: string) { return id.length > 14 ? `${id.slice(0, 8)}…${id.slice(-4)}` : id }
