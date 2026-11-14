// Ürünler (/products).
import { lazy, Suspense, useEffect, useMemo, useRef, useState } from 'react'
import { platformDisplayNames, platformDisplayOrder } from '../../../shared/platform-names'
import { Link, useNavigate, useSearchParams } from 'react-router'
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi, hubPoll, loadAllPages, type CursorPage } from '../../../shared/api'
import { newIdempotencyKey } from '../../../shared/idempotency'
import { useToastStack } from '../../../shared/components/toast-stack-context'
import { CardBackgroundIcon, StickyActionBar, TitleHint, useConfirmDialog } from '../../../shared/components'
import { CountUp } from '../../../shared/components/CountUp'
import { useMediaQuery } from '../../../shared/hooks/useMediaQuery'
import { productStatusLabel } from '../../../shared/status-labels'
import { RrAnchor, RrBadge, RrColumnPop, RrError, RrFilterCard, RrHeadCell, RrIcon, RrLoading, RrLogo, RrModal, RrPageSize, RrRing, RrThumb, titleTr, type RingTone } from '../../marketplace/returns-ui'
import { appendNotification, notificationError, productNotificationSubject, type NotificationContext } from '../../../shared/notifications'
import { productPlatformDisplayLabel, productPlatformDisplayState, withProductPlatformSaleState } from '../product-platform-status'
import { nextProductCatalogSort, parseProductCatalogSort, productCatalogSortOptions, productCatalogSortValue, type ProductCatalogSort } from '../product-catalog-sorting'
import { activeProductSyncJobs as filterActiveProductSyncJobs, productSyncJobsPath, settleTrackedProductSyncJobs, trackProductSyncJobs } from '../product-sync-tracking'
import { productImportConnection, singleProductLookupHint, singleProductLookupLabel } from '../product-import-platforms'
import { productImportChoiceSummary, productImportModeDescription, productImportNeedsChoice, productImportOptionVisibility as getProductImportOptionVisibility, productImportWritesContent, productImportWritesPriceStock, productImportOverwriteConfirmation, productImportStartedMessage, type ProductImportMethod, type ProductImportMode } from '../product-import-options'
import { productPlatformFilterGroups } from '../product-platform-filter'
import { QUICK_EDIT_SYNC_KEYS } from '../quick-price-edit'
import { bulkStatusPlan, bulkStatusResultMessage, runInChunks } from '../bulk-status-run'
import { requiredFieldsSkipNotice, type RequiredFieldsSkipped } from '../required-fields-skip'
import { ViewTabsWithMore, viewTabLabel } from '../../../shared/view-tabs'
import { formatMoney } from '../../../shared/format-money'
import { readUrlNumber, readUrlText, useChangeEffect, useListUrlSync } from '../../../shared/list-url-state'
import { ProductTransferDialog } from './ProductTransferDialog'
import { ModelCodeCopy } from './ModelCodeCopy'
import { productGroupImage } from './product-group-image'
import { productDeleteJobNote, productDeleteListing, productDeleteMoreText, productDeleteResultMessage, type ProductDeleteResponse } from './product-delete-text'
import { loadProductsByIds } from './quick-edit-products'
import { bulkPlatformUpdateMenuState } from './bulk-platform-update'
import { isProductUpdateConnection } from '../platform-update-targets'
import { fitTip, unfitTip } from '../../../shared/touch-tips'
import { scheduleDeferredPrefetch } from './deferred-prefetch'
import { PanelSelect } from '../../../shared/PanelSelect'
import { colourFirstOptions } from '../variant-option-matching'
import { ProductQuickEditModal, variantAxisParts, type QuickEditMode } from './ProductQuickEditModal'
// Loaded with the editor's stylesheet only when a bulk publication opens, like a row's publish popup.
const BulkPublishDialog = lazy(() => import('./BulkPublishDialog').then(module => ({ default: module.BulkPublishDialog })))
const BulkPlatformUpdateDialog = lazy(() => import('./BulkPlatformUpdateDialog').then(module => ({ default: module.BulkPlatformUpdateDialog })))
const BulkFieldDialog = lazy(() => import('./BulkFieldDialog').then(module => ({ default: module.BulkFieldDialog })))
import { cardCountText, toneAbove } from '../../../shared/metric-value'
import { AcceptedJob, ImageLightboxModal, LightboxImage, MarketplaceConnection, Product, Variant, compareOptionValues, formatCatalogOptionLabel, isColorOptionName, isProductPublicationConnection, isVariantOptionName, optionValueSortRank, preferredColorOption, variantOptionEntries } from '../shared'
import { EmptyState, ListEmptyState } from '../../../shared/components/EmptyState'

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
// Toplu Değer Ata: the selected records grouped by model (a record the list has not loaded is a model of its own; the
// API adds each model's other colour cards itself) and the variants the selection holds.
function selectionModelGroups(ids: readonly string[], cache: Record<string, Product>): { groups: string[][]; variantCount: number } {
  const known = ids.map(id => cache[id]).filter((product): product is Product => Boolean(product))
  const knownIds = new Set(known.map(product => product.id))
  const groups = [...productRowsAsCards(known).map(group => group.products.map(product => product.id)), ...ids.filter(id => !knownIds.has(id)).map(id => [id])]
  return { groups, variantCount: known.reduce((sum, product) => sum + product.variants.length, 0) }
}
async function fetchProductPage(limit: number, filters: ProductListFilters, after: string | null) {
  const params = new URLSearchParams({ limit: String(limit) })
  if (after) params.set('after', after)
  if (filters.search) params.set('search', filters.search)
  if (filters.status) params.set('status', filters.status)
  if (filters.platform) params.set('platform', filters.platform)
  if (filters.stock) params.set('stock', filters.stock)
  if (filters.sort) params.set('sort', filters.sort)
  return hubPoll<CursorPage<Product>>(`/products?${params.toString()}`)
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
  const options = colourFirstOptions(variantOptionEntries(variant)
    .sort((left, right) => left.name.localeCompare(right.name, 'tr', { sensitivity: 'base' }) || left.value.localeCompare(right.value, 'tr', { numeric: true, sensitivity: 'base' })))
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
// Two lists: Düşük Stok Renkleri (a colour with at least half its variants sold out, the products page's low-stock count)
// and Tükenen Varyantlar (any variant sold out while the product still has stock, read when its tab is opened).
export function LowStockDetailsModal({ products: lowProducts, loading: lowLoading, error: lowError, onClose, onImageClick }: { products: Product[]; loading: boolean; error: unknown; onClose: () => void; onImageClick: (url: string, title: string, modelCode?: string) => void }) {
  const [mode, setMode] = useState<'LOW' | 'GAP'>('LOW')
  const gapQuery = useQuery({ queryKey: ['products', 'sold-out-variant-details'], queryFn: () => loadAllPages<Product>('/products?stock=GAP', 200), enabled: mode === 'GAP', staleTime: 15_000 })
  const products = useMemo(() => mode === 'LOW' ? lowProducts : gapQuery.data?.items ?? [], [mode, lowProducts, gapQuery.data])
  const loading = mode === 'LOW' ? lowLoading : gapQuery.isLoading
  const error = mode === 'LOW' ? lowError : gapQuery.error
  const groups = useMemo(() => productRowsAsCards(products), [products])
  const [search, setSearch] = useState('')
  const [categoryFilter, setCategoryFilter] = useState('')
  // The list comes newest first (like the products page), so a model's rank is its newest card's place in it.
  const [sort, setSort] = useState<'NEWEST' | 'MODEL_ASC' | 'MODEL_DESC' | 'MISSING_DESC' | 'MISSING_ASC'>('NEWEST')
  const productRank = useMemo(() => new Map(products.map((product, index) => [product.id, index])), [products])
  const newestRank = (group: { products: Product[] }) => Math.min(...group.products.map(product => productRank.get(product.id) ?? Number.MAX_SAFE_INTEGER))
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
      if (sort === 'NEWEST') return newestRank(left) - newestRank(right)
      if (sort === 'MODEL_DESC') return lowStockModelCode(right).localeCompare(lowStockModelCode(left), 'tr', { numeric: true, sensitivity: 'base' })
      if (sort === 'MISSING_DESC') return lowStockMissingVariantCount(right) - lowStockMissingVariantCount(left) || lowStockModelCode(left).localeCompare(lowStockModelCode(right), 'tr', { numeric: true, sensitivity: 'base' })
      if (sort === 'MISSING_ASC') return lowStockMissingVariantCount(left) - lowStockMissingVariantCount(right) || lowStockModelCode(left).localeCompare(lowStockModelCode(right), 'tr', { numeric: true, sensitivity: 'base' })
      return lowStockModelCode(left).localeCompare(lowStockModelCode(right), 'tr', { numeric: true, sensitivity: 'base' })
    })
  // eslint-disable-next-line react-hooks/exhaustive-deps -- newestRank only reads productRank
  }, [categoryFilter, groups, search, sort, productRank])
  const lowVariantCount = filteredGroups.reduce((sum, group) => sum + lowStockMissingVariantCount(group), 0)
  const catalogRecordCount = filteredGroups.reduce((sum, group) => sum + group.products.length, 0)
  // Model cards start closed and sit two to a row; opening one opens its row neighbour too, so the row stays level.
  // One row of models and one colour are open at a time: opening another closes the one that was open.
  const [openFamilies, setOpenFamilies] = useState<Set<string>>(new Set())
  const [openColors, setOpenColors] = useState<Set<string>>(new Set())
  const twoColumns = useMediaQuery('(min-width: 761px)')
  function toggleFamilyRow(index: number) {
    const start = twoColumns ? index - index % 2 : index
    const ids = filteredGroups.slice(start, start + (twoColumns ? 2 : 1)).map(group => group.id)
    setOpenFamilies(current => current.has(filteredGroups[index].id) ? new Set() : new Set(ids))
  }
  // Pointing at (or focusing) a card lights up its whole row, so it is clear that both cards open together.
  const [hotRow, setHotRow] = useState<number | null>(null)
  const rowOf = (index: number) => twoColumns ? Math.floor(index / 2) : index

  const low = mode === 'LOW'
  return <RrModal crumb="Stok Takibi" label="Düşük Stok" title={<>Düşük Stok<TitleHint label="Düşük Stok">Model koduna göre gruplanmış, tükenen varyantı olan ürünler. <b>Düşük stok:</b> bir renkteki varyantların en az yarısı tükenmiş. <b>Tükenen varyant:</b> stoğu süren üründe en az bir beden ya da seçenek bitmiş.</TitleHint></>} size="lg" onClose={onClose} foot={<><span className="rr-note" /><button type="button" className="rr-btn" onClick={onClose}>Kapat</button></>}>
    <ViewTabsWithMore className="rr-low-tabs" more={[]} ariaLabel="Düşük stok" value={mode} onChange={value => setMode(value as typeof mode)} items={[{ value: 'LOW', label: 'Düşük Stok Renkleri' }, { value: 'GAP', label: 'Tükenen Varyantlar' }]} />
    {loading ? <RrLoading text={low ? 'Düşük stok ürünleri yükleniyor…' : 'Tükenen varyantlar yükleniyor…'} /> : error ? <RrError error={error} /> : !groups.length ? <EmptyState icon="stock" title={low ? 'Düşük Stok Ürün Yok' : 'Tükenen Varyant Yok'}>Tüm ürünlerin stoğu yeterli.</EmptyState> : <>
      <div className="rr-low-tools"><span style={{ position: 'relative' }}><RrIcon name="search" style={{ position: 'absolute', left: 11, top: 10, color: 'var(--rr-subtle)' }} /><input className="rr-in" style={{ paddingLeft: 34 }} value={search} onChange={event => setSearch(event.target.value)} placeholder="Model kodu, ürün veya renk ara" aria-label="Stok eksiklerinde ara" /></span><PanelSelect ariaLabel="Kategori" value={categoryFilter} onChange={setCategoryFilter} width={220} options={[{ value: '', label: 'Tüm Kategoriler' }, ...categoryOptions.map(category => ({ value: category, label: category }))]} /><PanelSelect ariaLabel="Sıralama" value={sort} onChange={setSort} width={260} options={[{ value: 'NEWEST', label: 'Eklenme Sırası (Yeniden Eskiye)' }, { value: 'MODEL_ASC', label: 'Model Kodu (A-Z)' }, { value: 'MODEL_DESC', label: 'Model Kodu (Z-A)' }, { value: 'MISSING_DESC', label: 'Eksik Varyant (Çoktan Aza)' }, { value: 'MISSING_ASC', label: 'Eksik Varyant (Azdan Çoğa)' }]} /></div>
      <p className="rr-lead">{filteredGroups.length} model · {catalogRecordCount} katalog kaydı · {lowVariantCount} eksik varyant</p>
      {!filteredGroups.length && <p className="rr-lead">Arama veya filtreye uyan ürün bulunamadı.</p>}
      <div className="rr-lowgrid">{filteredGroups.map((group, index) => { const image = productGroupImage(group) ?? lowStockProductImage(group.primary); const open = openFamilies.has(group.id); const missing = lowStockMissingVariantCount(group); const pairSide = twoColumns ? (index % 2 === 0 ? ' rr-pair-left' : ' rr-pair-right') : ''; return <article className={`rr-lowf${open ? ' rr-open' : ''}${hotRow === rowOf(index) ? ' rr-row-hot' : ''}${pairSide}`} key={group.id} onMouseEnter={() => setHotRow(rowOf(index))} onMouseLeave={() => setHotRow(null)}>
        <div className="rr-lowf-h"><RrThumb src={image} alt={group.primary.title} onOpen={image ? () => onImageClick(image, group.primary.title, group.products.map(item => item.modelCode).find(value => value?.trim()) ?? undefined) : undefined} /><button type="button" className="rr-lowf-t" aria-expanded={open} title={twoColumns ? (open ? 'Bu satırdaki iki modeli kapat' : 'Bu satırdaki iki modeli aç') : undefined} onFocus={() => setHotRow(rowOf(index))} onBlur={() => setHotRow(null)} onClick={() => toggleFamilyRow(index)}><span><small>Model Kodu</small><b className="rr-num">{lowStockModelCode(group)}</b><em title={group.primary.title}>{group.primary.title}</em></span><span className="rr-lowf-c" title={`${missing} eksik · ${group.products.length} renk`}><b className="rr-num">{missing}</b></span><RrIcon name="chev" /></button></div>
        {open && <section>{[...group.products].sort((left, right) => lowStockProductColor(left).localeCompare(lowStockProductColor(right), 'tr', { sensitivity: 'base' })).map(product => { const lowVariants = product.variants.filter(variant => variant.onHand <= 0).sort((left, right) => lowStockSizeLabel(left).localeCompare(lowStockSizeLabel(right), 'tr', { numeric: true, sensitivity: 'base' })); const colorOpen = openColors.has(product.id); return <div className={`rr-lowc${colorOpen ? ' rr-open' : ''}`} key={product.id}><button type="button" className="rr-lowc-h" aria-expanded={colorOpen} onClick={() => setOpenColors(current => current.has(product.id) ? new Set() : new Set([product.id]))}><b>{lowStockProductColor(product)}</b><span className="rr-lowc-n">{lowVariants.length} eksik varyant</span><RrIcon name="chev" /></button>{colorOpen && (lowVariants.length ? <div className="rr-lowvs">{lowVariants.map(variant => <div className="rr-lowv" key={variant.id}><span><b>{lowStockSizeLabel(variant)}</b><small>Stok Kodu: {variant.sku}{variant.barcode ? ` · Barkod: ${variant.barcode}` : ''}</small></span><span className={variant.onHand === 0 ? 'rr-zero' : 'rr-low'}>{variant.onHand === 0 ? 'Stoksuz' : <>Kalan <b className="rr-num">{variant.onHand}</b></>}</span></div>)}</div> : <p className="rr-lead">Bu renkte kritik stok varyantı bulunamadı.</p>)}</div> })}</section>}
      </article> })}</div>
    </>}
  </RrModal>
}
// The editor's publish step, loaded only when a row's "Platformlarda Yayınla" opens it in a popup.
const ProductPublishPopup = lazy(() => import('../product-editor/ProductEditorPages').then(module => ({ default: module.ProductPublishPopup })))

function ProductColorRows({ group, priority, selected, onSelect, selectionMode: _selectionMode, onEnterSelection: _onEnterSelection, onQuickEdit, onImageClick, onDelete, onDuplicate, onSetStatus, onPublish }: { group: ProductGroup; priority?: boolean; selected: boolean; onSelect: () => void; selectionMode: boolean; onEnterSelection: () => void; onQuickEdit: (mode: QuickEditMode) => void; onImageClick: (url: string, title: string, modelCode?: string) => void; onDelete: () => void; onDuplicate: () => void; onSetStatus: (status: 'ACTIVE' | 'ARCHIVED') => void; onPublish: (productId: string) => void }) {
  const navigate = useNavigate()
  const product = group.primary
  const platformStatuses = group.products.flatMap(item => item.platformStatuses ?? [])
  const platformStatusAggregates = new Map<string, { platform: string; platformCode: string; statuses: string[]; matchedVariantCount: number; variantCount: number; isChecking: boolean; onSale: number; offSale: number; offSaleStockZero: number; reasons: Record<string, number> }>()
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
      current.onSale += item.onSaleVariantCount ?? 0
      current.offSale += item.offSaleVariantCount ?? 0
      current.offSaleStockZero += item.offSaleStockZeroVariantCount ?? 0
      for (const [reason, count] of Object.entries(item.offSaleReasonCounts ?? {})) current.reasons[reason] = (current.reasons[reason] ?? 0) + count
    } else {
      platformStatusAggregates.set(key, { platform, platformCode, statuses: [item.status], matchedVariantCount: item.matchedVariantCount ?? 0, variantCount: item.variantCount ?? 0, isChecking: Boolean(item.isChecking), onSale: item.onSaleVariantCount ?? 0, offSale: item.offSaleVariantCount ?? 0, offSaleStockZero: item.offSaleStockZeroVariantCount ?? 0, reasons: { ...item.offSaleReasonCounts } })
    }
  }
  const platformCards = [...platformStatusAggregates.entries()].map(([key, item]) => {
    const publicationState = productPlatformDisplayState(item.statuses, item.isChecking, item.matchedVariantCount, item.variantCount)
    const publicationLabel = productPlatformDisplayLabel(item.platform, item.statuses, item.matchedVariantCount, item.variantCount, publicationState)
    // The sale state goes over the publication state (a stock of 0 is never a closure; see withProductPlatformSaleState).
    const { state, label } = withProductPlatformSaleState(item.platform, publicationState, publicationLabel, { matched: item.matchedVariantCount, variantCount: item.variantCount, onSale: item.onSale, offSale: item.offSale, offSaleStockZero: item.offSaleStockZero, reasons: item.reasons })
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
  // Sizes run small to large (L, XL, 2XL, 3XL), not in the order the first colour happens to list them.
  const variantSizes = [...new Set(variantDisplayGroups.flatMap(item => item.values.map(value => value.label)))].sort((left, right) => compareOptionValues('Beden', left, right))
  const modelCode = group.products.map(item => item.modelCode).find(value => value?.trim()) ?? '—'
  const modelImage = productGroupImage(group)
  // Every marketplace is listed, those without a match greyed out. The dot is green when every variant is live or
  // connected there, yellow when only some are (or some failed, or some are closed to sale), red when all failed, the
  // marketplace no longer knows any of them (Bulunamadı) or a variant is blacklisted or locked there, orange when it is
  // listed there but archived or closed to sale (Satışa Kapalı), and cycles while it updates. A stock of 0 never closes.
  const platformMarks = platformDisplayOrder.map(code => {
    const card = platformCards.find(item => item.platformCode === code)
    const name = platformDisplayNames[code]
    const linkedTone = card && card.variantCount > 0 && card.matchedVariantCount < card.variantCount ? 'part' : 'on'
    const state = card ? ({ active: 'on', partial: 'link', linked: linkedTone, processing: 'busy', error: 'err', missing: 'err', inactive: 'none', closed: 'off', blocked: 'err' } as Record<string, string>)[card.state] ?? 'none' : 'none'
    return { code, state, label: card?.label ?? `${name}: Ürün Eşleşmesi Bulunamadı` }
  })
  const [ringTone, ringProgress] = ({ ACTIVE: ['ok', 1], DRAFT: ['prog', 0.3], ARCHIVED: ['off', 0], MIXED: ['bad', 0.66] } as Record<string, [RingTone, number]>)[status] ?? ['off', 0]
  const statusLabelText = status === 'MIXED' ? 'Hatalı' : titleTr(statusLabel)
  // A draft is completed in its editor (its Kaydet puts it on sale) and is never published as it is: no Platformlarda
  // Yayınla or Satışa Aç here.
  const draft = status === 'DRAFT'
  const menuItems: Array<['edit' | 'export' | 'price' | 'dup' | 'on' | 'off' | 'trash', string, () => void, string?] | 'hr'> = draft
    ? [['edit', 'Taslağı Tamamla', () => navigate(`/products/${product.id}`)], ['price', 'Fiyat ve Stok Düzenle', () => onQuickEdit('both')], ['dup', 'Yeni Ürün Olarak Düzenle', onDuplicate], 'hr', ['trash', group.products.length > 1 ? 'Ürün Grubunu Sil' : 'Ürünü Sil', onDelete, 'rr-danger']]
    : [['edit', 'Ürünü Düzenle', () => navigate(`/products/${product.id}`)], ['export', 'Platformlarda Yayınla', () => onPublish(product.id)], ['price', 'Fiyat ve Stok Düzenle', () => onQuickEdit('both')], ['dup', 'Yeni Ürün Olarak Düzenle', onDuplicate], 'hr', status === 'ACTIVE' ? ['off', 'Satışa Kapat', () => onSetStatus('ARCHIVED')] : ['on', 'Satışa Aç', () => onSetStatus('ACTIVE')], ['trash', group.products.length > 1 ? 'Ürün Grubunu Sil' : 'Ürünü Sil', onDelete, 'rr-danger']]
  // Phones select a card only through its round checkbox; a hold or a tap elsewhere on the card no longer selects it.
  return <tr className={selected ? 'rr-sel' : undefined}>
    <td className="rr-wide"><div className="rr-prodcell"><input type="checkbox" className="rr-cb" aria-label={`${product.title} ürün grubunu seç`} checked={selected} onChange={onSelect} /><RrThumb src={modelImage} alt={product.title} priority={priority} onOpen={modelImage ? () => onImageClick(modelImage, product.title, modelCode === '—' ? undefined : modelCode) : undefined} /><div><b title={product.title}>{product.title}</b><small><span>Model Kodu:</span> <b className="rr-num">{modelCode}</b>{modelCode !== '—' && <ModelCodeCopy value={modelCode} />}</small></div></div></td>
    <td className="rr-mid" data-l="Varyant"><span className={variantDisplayGroups.length > 0 ? 'rr-vhover' : 'rr-vhover rr-vhover-none'} tabIndex={0} onFocus={event => placeVariantTip(event.currentTarget)} onBlur={event => hideVariantTip(event.currentTarget)} role="button" aria-label={`${product.title}: varyantları aç`} onPointerDown={event => { event.currentTarget.dataset.wasOpen = document.activeElement === event.currentTarget ? '1' : '' }} onClick={event => { if (variantDisplayGroups.length > 0 && window.matchMedia('(max-width: 760px), (hover: none)').matches) { if (event.currentTarget.dataset.wasOpen) event.currentTarget.blur(); return } onQuickEdit('both') }} onKeyDown={event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); onQuickEdit('both') } }}><span className="rr-varcell" onMouseEnter={event => { if (event.currentTarget.parentElement) placeVariantTip(event.currentTarget.parentElement) }} onMouseLeave={event => { if (event.currentTarget.parentElement) hideVariantTip(event.currentTarget.parentElement) }}><b>{group.products.length} seçenek</b><small>{group.variants.length} varyant</small></span>{variantDisplayGroups.length > 0 && <span className="rr-tip rr-vtip" role="tooltip"><table className="rr-vtab"><thead><tr><th />{variantSizes.map(size => <th key={size}>{size}</th>)}</tr></thead><tbody>{variantDisplayGroups.map(item => <tr key={item.label}><th title={item.label}>{item.label}</th>{variantSizes.map(size => { const value = item.values.find(entry => entry.label === size); return <td key={size} className={value == null ? 'rr-none' : value.quantity === 0 ? 'rr-empty' : value.quantity < 5 ? 'rr-low' : undefined}>{value == null ? '—' : value.quantity}</td> })}</tr>)}</tbody></table></span>}</span></td>
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
  listing: ReturnType<typeof productDeleteListing>
}
function ProductDeleteConfirmModal({ request, deleting, onClose, onConfirm }: { request: ProductDeleteRequest; deleting: boolean; onClose: () => void; onConfirm: () => void }) {
  const isGroup = request.productIds.length > 1
  // The publication and price/stock jobs still waiting for these products: the delete cancels them.
  const preview = useQuery({
    queryKey: ['product-delete-preview', request.productIds],
    queryFn: async () => {
      let pendingJobCount = 0
      for (let index = 0; index < request.productIds.length; index += 500)
        pendingJobCount += (await hubApi<{ pendingJobCount: number }>('/products/delete-preview', { method: 'POST', body: JSON.stringify({ productIds: request.productIds.slice(index, index + 500) }) })).pendingJobCount
      return pendingJobCount
    },
    staleTime: 0
  })
  const jobNote = productDeleteJobNote(preview.data)
  return <RrModal crumb="Katalog Kaydını Sil" title={isGroup ? 'Ürün Grubunu Sil' : 'Ürünü Sil'} desc="Bu işlem yerel katalog kaydını kalıcı olarak kaldırır ve geri alınamaz. Sipariş geçmişi korunur; pazar yeri ilanı otomatik olarak silinmez." size="sm" locked={deleting} onClose={onClose} foot={<><span className="rr-note" /><button type="button" className="rr-btn" onClick={onClose} disabled={deleting}>Vazgeç</button><button type="button" className="rr-btn rr-solid" onClick={onConfirm} disabled={deleting}><RrIcon name="trash" />{deleting ? 'Siliniyor…' : isGroup ? `${request.productIds.length} Kaydı Sil` : 'Kalıcı Olarak Sil'}</button></>}>
    <div className="rr-callout rr-danger"><RrIcon name="alert" /><span><b>{request.title}</b><br />{request.description}</span></div>
    <ul className="rr-del-list" aria-label="Silinecek ürünler">
      {request.listing.shown.map(item => <li key={item.id}><span title={item.title}>{item.title}</span><small>{item.modelCode ? `Model Kodu: ${item.modelCode}` : 'Model kodu yok'}</small></li>)}
      {request.listing.moreCount > 0 && <li className="rr-del-more">{productDeleteMoreText(request.listing.moreCount)}</li>}
    </ul>
    <p className="rr-lead">Devam ederseniz bu kayıtların varyant, stok, fiyat ve platform eşleşmeleri ile stok geçmişi (sipariş ve iade stok hareketleri, rezervasyonlar) silinir; siparişler ve iadeler kalır.</p>
    {jobNote && <p className="rr-lead" role="note">{jobNote}</p>}
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

// The variant stock table is placed on the window, not inside the list's scroll box (which cut off every colour past the
// box's edge): under its row, or above it when the window has more room there, as tall as all its colours need and
// only scrolling when the window itself is too short for them.
// Phones and tablets (up to 1024 px) show the list as cards: there the table opens inside its card at the card's width
// (products-responsive.css, tablet-catalog.css), under its trigger, and fitTip only moves it above the trigger when the
// window has no room below, never past the window or under the sticky top bar. A fixed position written here on a
// wider screen is taken off, or it would pull the table out of the card.
// The tip leaves the top layer when the pointer leaves "N seçenek" (unless the keyboard focus keeps it open) or focus goes.
function closeVariantTip(tip: HTMLElement) {
  if (typeof tip.hidePopover === 'function' && tip.matches(':popover-open')) tip.hidePopover()
}
function hideVariantTip(anchor: HTMLElement) {
  const tip = anchor.querySelector<HTMLElement>('.rr-vtip')
  if (tip && !anchor.matches(':focus-visible')) closeVariantTip(tip)
}
function placeVariantTip(anchor: HTMLElement) {
  const tip = anchor.querySelector<HTMLElement>('.rr-vtip')
  if (!tip) return
  if (window.matchMedia('(max-width: 1024px)').matches) {
    closeVariantTip(tip)
    for (const property of ['position', 'transform', 'top', 'left', 'right', 'bottom', 'max-height', 'margin']) tip.style.removeProperty(property)
    anchor.removeAttribute('data-up')
    fitTip(anchor, tip)
    return
  }
  unfitTip(tip)
  // In the browser's top layer the tip sits above every sticky bar, card and sidebar and no ancestor can clip it.
  if (typeof tip.showPopover === 'function') {
    tip.setAttribute('popover', 'manual')
    if (!tip.matches(':popover-open')) tip.showPopover()
  }
  const edge = 8, offset = 6
  const own = anchor.getBoundingClientRect()
  Object.assign(tip.style, { position: 'fixed', transform: 'none', right: 'auto', bottom: 'auto', maxHeight: 'none', margin: '0' })
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
  const client = useQueryClient(); const navigate = useNavigate(); const [search, setSearch] = useState(initialList.filters.search); const [searchFilter, setSearchFilter] = useState(initialList.filters.search); const [status, setStatus] = useState(initialList.filters.status); const [platform, setPlatform] = useState(initialList.filters.platform); const [stock, setStock] = useState(initialList.filters.stock); const [selectedProductIds, setSelectedProductIds] = useState<string[]>([]); const [selectedProductCache, setSelectedProductCache] = useState<Record<string, Product>>({}); const [allProductsSelected, setAllProductsSelected] = useState(false); const [selectingAllProducts, setSelectingAllProducts] = useState(false); const [quickEdit, setQuickEdit] = useState<{ productIds: string[]; mode: QuickEditMode } | null>(null); const toastStack = useToastStack(); const [statusRequest, setStatusRequest] = useState<{ productIds: string[]; status: 'ACTIVE' | 'ARCHIVED'; skippedCount?: number; draftCount?: number } | null>(null); const [jumpPage, setJumpPage] = useState(''); const [deleteRequest, setDeleteRequest] = useState<ProductDeleteRequest | null>(null); const [deletingProducts, setDeletingProducts] = useState(false); const [productImportOpen, setProductImportOpen] = useState(false); const [productImportMethod, setProductImportMethod] = useState<ProductImportMethod>('BULK'); const [productImportConnectionIds, setProductImportConnectionIds] = useState<string[]>([]); const [productImportMode, setProductImportMode] = useState<ProductImportMode>('FULL'); const [productImportLookup, setProductImportLookup] = useState(''); const [productImportIncludeArchived, setProductImportIncludeArchived] = useState(true); const [productImportUpdateExistingProducts, setProductImportUpdateExistingProducts] = useState(false); const [productImportUpdatePrices, setProductImportUpdatePrices] = useState(false); const [productImportUpdateStock, setProductImportUpdateStock] = useState(false); const [productImporting, setProductImporting] = useState(false); const [lightboxImage, setLightboxImage] = useState<LightboxImage | null>(null); const [lowStockOpen, setLowStockOpen] = useState(() => searchParams.get('details') === 'low-stock'); const [transferOpen, setTransferOpen] = useState(false); const [bulkPublishOpen, setBulkPublishOpen] = useState(false); const [bulkUpdateOpen, setBulkUpdateOpen] = useState(false); const [bulkFieldOpen, setBulkFieldOpen] = useState(false);const [publishProductId, setPublishProductId] = useState<string | null>(null); const [pageSize, setPageSize] = useState(initialList.pageSize); const [pageNumber, setPageNumber] = useState(initialList.pageNumber); const [pageCursors, setPageCursors] = useState<Record<string, Record<number, string | null>>>(initialList.pageCursors)
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
    refetchIntervalInBackground: false
  })
  const summaryQuery = useQuery({ queryKey: ['products', 'summary'], queryFn: () => hubApi<ProductSummary>('/products/summary'), staleTime: 30_000, refetchOnWindowFocus: true })
  // The summary has no draft or archived counts; a one-item page carries the total.
  const draftCountQuery = useQuery({ queryKey: ['products', 'count', 'DRAFT'], queryFn: () => fetchProductPage(1, { search: '', status: 'DRAFT', platform: '', stock: '', sort: '' }, null), staleTime: 60_000 })
  const archivedCountQuery = useQuery({ queryKey: ['products', 'count', 'ARCHIVED'], queryFn: () => fetchProductPage(1, { search: '', status: 'ARCHIVED', platform: '', stock: '', sort: '' }, null), staleTime: 60_000 })
  const lowStockQuery = useQuery({ queryKey: ['products', 'low-stock-details'], queryFn: () => loadAllPages<Product>('/products?stock=LOW', 200), enabled: lowStockOpen, staleTime: 15_000, refetchOnWindowFocus: true })
  const connectionsQuery = useQuery({ queryKey: ['connections', 'product-price'], queryFn: () => loadAllPages<MarketplaceConnection>('/connections') })
  const productSyncJobsQuery = useQuery({ queryKey: ['jobs', 'product-import'], queryFn: () => hubPoll<ProductSyncJob[]>(productSyncJobsPath, { cache: 'no-store' }), enabled: productImportOpen || trackedProductImportJobIds.length > 0, refetchInterval: query => productImportOpen && filterActiveProductSyncJobs(query.state.data ?? []).length ? 1500 : trackedProductImportJobIds.length ? 3000 : false, refetchIntervalInBackground: true, refetchOnWindowFocus: true, staleTime: 0 })
  const products = useMemo(() => query.data?.items ?? [], [query.data]); const connections = (connectionsQuery.data?.items ?? []).filter(isProductImportConnection); const publicationConnections = (connectionsQuery.data?.items ?? []).filter(isProductPublicationConnection); const selectedImportPlatforms = productImportConnectionIds.map(connectionId => connections.find(connection => connection.id === connectionId)?.platformCode ?? '').filter(Boolean); const selectedImportHasShopify = selectedImportPlatforms.some(code => code.trim().toUpperCase() === 'SHOPIFY'); const singleLookupLabel = singleProductLookupLabel(selectedImportPlatforms[0] ?? ''); const platforms = summaryQuery.data?.platforms ?? []
  const selectedImportHasTrendyol = selectedImportPlatforms.some(code => code.trim().toUpperCase() === 'TRENDYOL')
  const productImportSupportsPendingApproval = productImportMethod === 'BULK' && selectedImportHasTrendyol && (productImportMode === 'FULL' || productImportMode === 'NEW_ONLY' || productImportMode === 'MAPPING_ONLY')
  const productImportOptions = getProductImportOptionVisibility({ method: productImportMethod, mode: productImportMode, platformCodes: selectedImportPlatforms, supportsPendingApproval: productImportSupportsPendingApproval })
  // A choice the selected platforms do not honour is hidden and counts as unticked (it is neither sent nor announced).
  const productImportChoices = { updateExisting: productImportOptions.showUpdateExisting && productImportUpdateExistingProducts, updatePrices: productImportOptions.showPriceStock && productImportUpdatePrices, updateStock: productImportOptions.showPriceStock && productImportUpdateStock }
  const productImportChoiceMissing = productImportNeedsChoice({ method: productImportMethod, mode: productImportMode, hasTrendyol: selectedImportPlatforms.some(code => code.trim().toUpperCase() === 'TRENDYOL'), showUpdateExisting: productImportOptions.showUpdateExisting, ...productImportChoices })
  useEffect(() => { if (!productImportOptions.showExistingMode && productImportMode === 'EXISTING_ONLY') setProductImportMode('MAPPING_ONLY') }, [productImportOptions.showExistingMode, productImportMode])
  const totalCount = query.data?.totalCount ?? products.length; const totalPages = Math.max(1, Math.ceil(totalCount / pageSize)); const currentPage = Math.min(pageNumber, totalPages); const pageProducts = useMemo(() => currentPage === pageNumber ? products : [], [currentPage, pageNumber, products]); const pageProductGroups = useMemo(() => productRowsAsCards(pageProducts), [pageProducts])
  const activeProductSyncJobs = useMemo(() => filterActiveProductSyncJobs(productSyncJobsQuery.data ?? []), [productSyncJobsQuery.data])
  const cancelProductSync = useMutation({ mutationFn: (jobId: string) => hubApi(`/jobs/${jobId}/cancel`, { method: 'POST', headers: { 'Idempotency-Key': `cancel-product-import:${jobId}` } }), onSuccess: () => { void productSyncJobsQuery.refetch(); void client.invalidateQueries({ queryKey: ['jobs'] }) } })
  const pageCopyOf = (id: string) => products.find(product => product.id === id) ?? selectedProductCache[id]
  // While the quick edit is open its products are read by id (loadProductsByIds): its saves refresh ['products'], which
  // covers this query, so every cell works on the latest versions. The page's copies show until the first answer.
  const quickEditIds = quickEdit?.productIds ?? []
  const quickEditQuery = useQuery({ queryKey: ['products', 'by-ids', quickEditIds], queryFn: () => loadProductsByIds(quickEditIds, id => hubApi<Product>(`/products/${id}`, { cache: 'no-store' }), pageCopyOf), enabled: quickEditIds.length > 0, staleTime: 0, gcTime: 0 })
  const quickEditProducts = quickEdit
    ? quickEditQuery.data ?? quickEdit.productIds.map(pageCopyOf).filter((product): product is Product => Boolean(product))
    : []
  const selectedProductCardCount = useMemo(() => productRowsAsCards(Object.values(selectedProductCache)).length, [selectedProductCache])
  const nextPageCursor = query.data?.nextCursor ?? null
  const productListRef = useRef<HTMLDivElement>(null)
  const productPagerRef = useRef<HTMLDivElement>(null)
  useEffect(() => {
    if (!productImportOpen || !activeProductSyncJobs.length) return
    const timer = window.setInterval(() => {
      void client.invalidateQueries({ queryKey: ['products', productFilters, pageSize, pageNumber, pageCursor] })
      void client.invalidateQueries({ queryKey: ['products', 'summary'] })
    }, 1500)
    return () => window.clearInterval(timer)
  }, [activeProductSyncJobs.length, client, pageCursor, pageNumber, pageSize, productFilters, productImportOpen])
  // An import's end refreshes the list whether or not its window is still open; each job is followed until it ends.
  useEffect(() => {
    const { finished, remaining } = settleTrackedProductSyncJobs(trackedProductImportJobIds, productSyncJobsQuery.data ?? [])
    if (!finished.length) return
    void client.invalidateQueries({ queryKey: ['products'] })
    setTrackedProductImportJobIds(remaining)
  }, [client, productSyncJobsQuery.data, trackedProductImportJobIds])
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
    // Read ahead only after this page's pictures (or near the list's end), so it does not take their bandwidth.
    return scheduleDeferredPrefetch(() => void client.prefetchQuery({ queryKey: ['products', productFilters, pageSize, nextPage, nextPageCursor], queryFn: () => fetchProductPage(pageSize, productFilters, nextPageCursor), staleTime: 30_000 }), { root: productListRef.current, nearEnd: productPagerRef.current })
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
  // A quick edit changes what the product editor shows too (panel price, channel prices, stock): both refresh.
  const refreshQuickEdit = () => Promise.all(QUICK_EDIT_SYNC_KEYS.map(queryKey => client.invalidateQueries({ queryKey })))
  // Each result lands below the toasts already shown and goes to the notification history.
  function showProductToast(title: string, message: string, kind: 'success' | 'error' | 'info' | 'warning', context?: NotificationContext, shape: { lines?: string[]; persistent?: boolean } = {}) {
    appendNotification({ ...context, title, message, kind })
    toastStack.show({ kind, title, message, lines: shape.lines, durationMs: shape.persistent ? undefined : shape.lines?.length ? 8000 : 4000 })
  }
  function openProductImport() {
    setProductImportMethod('BULK')
    setProductImportConnectionIds(connections.length === 1 ? [connections[0].id] : [])
    setProductImportMode('FULL')
    setProductImportLookup('')
    setProductImportIncludeArchived(true)
    setProductImportUpdateExistingProducts(false)
    setProductImportUpdatePrices(false); setProductImportUpdateStock(false)
    setProductImportOpen(true)
  }
  async function importProductsFromPlatforms() {
    if (productImporting) return
    if (!productImportConnectionIds.length) { showProductToast('Bağlantı Seçilmedi', 'Aktarım için en az bir aktif pazar yeri bağlantısı seçin.', 'error'); return }
    if (productImportMethod === 'SINGLE' && productImportConnectionIds.length !== 1) { showProductToast('Tek Bağlantı Seçin', 'Tek ürün aktarımında yalnızca bir bağlantı seçilebilir.', 'error'); return }
    if (productImportMethod === 'SINGLE' && !productImportLookup.trim()) { showProductToast('Ürün Bilgisi Gerekli', `Tek ürün aktarımı için ${singleLookupLabel} girin.`, 'error'); return }
    // Decision D: the panel is the stock master; taking the marketplace's prices and stock over the panel's is a
    // deliberate step, confirmed here.
    const overwriteConfirmation = productImportMethod === 'SINGLE' || productImportMode === 'EXISTING_ONLY' ? productImportOverwriteConfirmation(productImportChoices.updatePrices, productImportChoices.updateStock) : null
    if (overwriteConfirmation) {
      const accepted = await confirmImport({ ...overwriteConfirmation, confirmLabel: 'Pazaryeri Değerlerini Al', destructive: true })
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
        // Each connection gets only the choices its platform honours (product-import-options).
        const updateExistingProducts = productImportWritesContent(platformCode ?? '') && productImportChoices.updateExisting
        const priceStockAllowed = productImportWritesPriceStock(platformCode ?? '')
        const updatePrices = priceStockAllowed && productImportChoices.updatePrices
        const updateStock = priceStockAllowed && productImportChoices.updateStock
        const query = new URLSearchParams({ full: String(productImportMethod === 'BULK' && full), newOnly: String(productImportMethod === 'BULK' && newOnly), existingOnly: String(productImportMethod === 'BULK' && existingOnly), mappingOnly: String(productImportMethod === 'BULK' && mappingOnly), optionsOnly: String(productImportMethod === 'BULK' && optionsOnly), includeArchived: String(productImportIncludeArchived), includeDrafts: String(productImportMethod === 'BULK' && productImportIncludeArchived && platformCode === 'SHOPIFY'), includePendingApproval: String(includePendingApproval && platformCode === 'TRENDYOL'), updateExistingProducts: String(updateExistingProducts), updatePrices: String(updatePrices), updateStock: String(updateStock) })
        if (productImportMethod === 'SINGLE') query.set('lookup', productImportLookup.trim())
        return hubApi<AcceptedJob>(`/connections/${connectionId}/product-sync-jobs?${query.toString()}`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: '{}' })
      }))
      setTrackedProductImportJobIds(current => trackProductSyncJobs(current, queuedProductSyncJobs.map(job => job.jobId)))
      const scope = productImportMethod === 'SINGLE' ? 'single' as const : full ? 'full' as const : productImportMode === 'NEW_ONLY' ? 'new' as const : productImportMode === 'MAPPING_ONLY' ? 'mapping' as const : 'existing' as const
      showProductToast('Ürün Aktarımı Başladı', productImportStartedMessage({
        connectionCount: productImportConnectionIds.length,
        scope,
        includeArchived: productImportIncludeArchived,
        includeDrafts: productImportIncludeArchived && selectedImportHasShopify,
        includePendingApproval,
        contentChoice: productImportMethod === 'BULK' && (full || existingOnly) ? productImportChoices.updateExisting : null,
        priceStock: (productImportMethod === 'SINGLE' || existingOnly) && productImportOptions.showPriceStock ? { updatePrices: productImportChoices.updatePrices, updateStock: productImportChoices.updateStock } : null
      }), 'info')
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
      // A closed card missing a required field is skipped by the server and returned; the run goes on with the rest.
      const skipped: RequiredFieldsSkipped[] = []
      const result = await runInChunks(targetIds, 500, async productIds => {
        const response = await hubApi<{ updatedCount?: number; skipped?: RequiredFieldsSkipped[] } | null>('/products/bulk-status', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ productIds, status: newStatus }) })
        skipped.push(...(response?.skipped ?? []))
      }, (done, total) => setStatusProgress({ done, total }))
      const outcome = bulkStatusResultMessage(productStatusLabel(newStatus), result, skipped.length)
      const notice = requiredFieldsSkipNotice(skipped)
      showProductToast(outcome.title, notice ? `${outcome.message} ${notice.message}` : outcome.message, outcome.kind, notice ? { operation: 'Toplu Satışa Aç', details: notice.details, target: notice.target, record: true } : undefined)
      // A skipped card stays selected, so it can be opened again once it is complete.
      const skippedIds = new Set(skipped.map(card => card.productId))
      const doneIds = new Set(result.doneIds.filter(id => !skippedIds.has(id)))
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
    if (!statusRunning.current && plan.targetIds.length) setStatusRequest({ productIds: plan.targetIds, status: newStatus, skippedCount: plan.alreadyIds.length, draftCount: plan.draftIds.length })
  }

  const productForDelete = (id: string) => selectedProductCache[id] ?? loadedProductById.get(id)

  function requestDeleteProduct(product: Product) {
    setDeleteRequest({ productIds: [product.id], product, title: product.title, description: 'Tek bir katalog kaydı ve ona bağlı varyantlar silinecek.', listing: productDeleteListing([product.id], () => product) })
  }

  function requestDeleteProductGroup(group: ProductGroup) {
    if (group.products.length === 1) return requestDeleteProduct(group.primary)
    const ids = group.products.map(product => product.id)
    setDeleteRequest({ productIds: ids, title: group.primary.title, description: `${ids.length} katalog kaydı aynı ürün kartında gruplanmış durumda; grubun tamamı silinecek.`, listing: productDeleteListing(ids, id => group.products.find(product => product.id === id)) })
  }

  async function confirmProductDelete() {
    const request = deleteRequest
    if (!request || deletingProducts) return
    setDeletingProducts(true)
    // The records already deleted, chunk by chunk: they leave the selection even when a later chunk fails.
    const deletedIds = new Set<string>()
    const dropDeleted = () => {
      if (!deletedIds.size) return
      setSelectedProductIds(current => current.filter(id => !deletedIds.has(id)))
      setSelectedProductCache(current => { const next = { ...current }; deletedIds.forEach(id => delete next[id]); return next })
      setAllProductsSelected(false)
    }
    try {
      const responses: ProductDeleteResponse[] = []
      if (request.product && request.productIds.length === 1) {
        // The delete goes against the product's current version, not the one the row had when the dialog opened
        // (a sync or an edit since then refused it with 412 again on every try).
        const latest = await hubApi<Product>(`/products/${request.product.id}`, { cache: 'no-store' })
        responses.push(await hubApi<ProductDeleteResponse>(`/products/${request.product.id}`, { method: 'DELETE', headers: { 'If-Match': `"v${latest.version}"`, 'Idempotency-Key': newIdempotencyKey() } }))
        deletedIds.add(request.product.id)
      } else {
        for (let index = 0; index < request.productIds.length; index += 500) {
          const productIds = request.productIds.slice(index, index + 500)
          responses.push(await hubApi<ProductDeleteResponse>('/products/bulk-delete', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ productIds }) }))
          productIds.forEach(id => deletedIds.add(id))
        }
      }
      dropDeleted()
      setDeleteRequest(null)
      // A deleted product has no page any more: the notice names it without a link.
      showProductToast(request.productIds.length === 1 ? 'Ürün Silindi' : 'Ürünler Silindi', productDeleteResultMessage(responses, request.productIds.length), 'success', { operation: 'Katalogdan silme', subject: productNotificationSubject({ name: request.title, modelCode: request.product?.modelCode }) })
    } catch (err) {
      const failure = notificationError(err, 'Ürün silinemedi.')
      // A part-way bulk delete: the deleted chunks leave the selection and the dialog, the rest stay to try again.
      dropDeleted()
      const remaining = request.productIds.filter(id => !deletedIds.has(id))
      if (deletedIds.size && remaining.length) setDeleteRequest({ ...request, productIds: remaining, listing: productDeleteListing(remaining, productForDelete), description: `${deletedIds.size.toLocaleString('tr-TR')} kayıt silindi; kalan ${remaining.length.toLocaleString('tr-TR')} katalog kaydı silinmedi.` })
      const message = deletedIds.size ? `${deletedIds.size.toLocaleString('tr-TR')} kayıt silindi, ${remaining.length.toLocaleString('tr-TR')} kayıt silinemedi: ${failure.message}` : failure.message
      showProductToast(request.productIds.length === 1 ? 'Ürün Silinemedi' : 'Ürünler Silinemedi', message, 'error', { operation: 'Katalogdan silme', code: failure.code, subject: productNotificationSubject({ id: request.product?.id, name: request.title, modelCode: request.product?.modelCode }) })
    } finally {
      setDeletingProducts(false)
      await refresh().catch(() => undefined)
    }
  }

  function bulkDeleteProducts() {
    const ids = [...selectedProductIds]
    if (!ids.length) return
    setDeleteRequest({ productIds: ids, title: `${selectedProductCardCount} Ürün Kartı`, description: `${ids.length} katalog kaydı seçildi. Siparişler ve iadeler korunur; ürünlerin katalog ve stok kayıtları silinir.`, listing: productDeleteListing(ids, productForDelete) })
  }

  const [selectionMode, setSelectionMode] = useState(false)
  function exitSelectionMode() { setSelectionMode(false); setSelectedProductIds([]); setSelectedProductCache({}); setAllProductsSelected(false) }
  function toggleCatalogSort(field: 'price' | 'stock') {
    setCatalogSort(current => nextProductCatalogSort(current, field))
  }

  const metric = (value: number | undefined, query: { isLoading: boolean; isError: boolean } = summaryQuery) => cardCountText(value, { loading: query.isLoading, error: query.isError })
  const outOfStock = summaryQuery.data?.outOfStockCount ?? 0
  const lowStock = summaryQuery.data?.lowStockCount ?? 0
  const statusTabs: Array<[string, string, 'all' | 'done' | 'invoice' | 'paused']> = [['', 'Tüm Ürünler', 'all'], ['ACTIVE', 'Satışa Açık', 'done'], ['DRAFT', 'Taslak', 'invoice'], ['ARCHIVED', 'Satışa Kapalı', 'paused']]
  const tabCount = (value: string) => { const count = value === '' ? summaryQuery.data?.totalCount : value === 'ACTIVE' ? summaryQuery.data?.activeCount : value === 'DRAFT' ? draftCountQuery.data?.totalCount : archivedCountQuery.data?.totalCount; return count == null ? '…' : count.toLocaleString('tr-TR') }
  const stockOptions = [{ value: 'ALL', label: 'Tüm Stoklar' }, { value: 'OUT', label: 'Stoksuz' }, { value: 'LOW', label: 'Düşük Stok' }, { value: 'OK', label: 'Yeterli Stok' }]
  const statusOptions = statusTabs.map(([value, label]) => ({ value: value || 'ALL', label }))
  const sortOptions = productCatalogSortOptions
  const sortValue = productCatalogSortValue(catalogSort) || 'default'
  const setSortValue = (value: string) => setCatalogSort(parseProductCatalogSort(value))
  const platformPop = (close: () => void) => <div className="rr-pop" role="dialog" aria-label="Platform Durumu" style={{ minWidth: 290 }}><div className="rr-ph"><span>Platform Durumu</span>{platform && <button type="button" className="rr-btn rr-link" onClick={() => { setPlatform(''); close() }}>Temizle</button>}</div><button type="button" className={`rr-opt${platform ? '' : ' rr-on'}`} onClick={() => { setPlatform(''); close() }}><span className="rr-radio" aria-hidden="true" /><span className="rr-grow">Tüm Platformlar<small className="rr-opt-sub">Platform ayrımı olmadan göster</small></span></button><div className="rr-sep" />{productPlatformFilterGroups.map(group => { const open = expandedPlatformGroup === group.label; const selectedOption = group.options.find(option => option.value === platform); return <div key={group.label} className={`rr-acc${open ? ' rr-open' : ''}`}><button type="button" className="rr-opt" aria-expanded={open} onClick={() => setExpandedPlatformGroup(value => value === group.label ? null : group.label)}><RrLogo code={group.label.toLocaleUpperCase('tr-TR').replace('İ', 'I')} /><span className="rr-grow">{group.label}<small className="rr-opt-sub">{selectedOption ? `${selectedOption.label} · Seçili` : 'Durum Seçin'}</small></span><RrIcon name="chev" /></button>{open && <div className="rr-acc-b">{group.options.map(option => { const on = platform === option.value; return <button key={option.value} type="button" className={`rr-opt${on ? ' rr-on' : ''}`} role="radio" aria-checked={on} onClick={() => { setPlatform(option.value); close() }}><span className="rr-radio" aria-hidden="true" /><span className="rr-grow">{option.label}<small className="rr-opt-sub">{option.label === 'Aktif' ? 'Tüm varyantlar eşleşti' : option.label === 'Kısmi' ? 'Bazı varyantlar eşleşti' : 'Eşleşme bulunamadı'}</small></span></button> })}</div>}</div> })}{platforms.filter(item => !productPlatformFilterGroups.some(group => group.label.localeCompare(item, 'tr-TR', { sensitivity: 'base' }) === 0)).map(item => <button key={item} type="button" className={`rr-opt${platform === item ? ' rr-on' : ''}`} onClick={() => { setPlatform(item); close() }}><span className="rr-radio" aria-hidden="true" /><span className="rr-grow">{item}<small className="rr-opt-sub">Platform eşleşmelerini göster</small></span></button>)}</div>
  const columnPop = (label: string, options: Array<{ value: string; label: string }>, current: string, pick: (value: string) => void, right?: boolean) => (close: () => void) => <RrColumnPop label={label} options={options} value={current || 'ALL'} right={right} onPick={value => { pick(value === 'ALL' ? '' : value); close() }} />
  // "Platformlara Güncelle" stays off, saying why, while none of the selected products is on a store that takes an update.
  const bulkUpdateMenu = bulkPlatformUpdateMenuState(Object.values(selectedProductCache), (connectionsQuery.data?.items ?? []).filter(isProductUpdateConnection))
  const bulkItems: Array<['price' | 'on' | 'off' | 'trash' | 'export' | 'doc' | 'refresh', string, string, () => void, string?, boolean?] | 'hr'> = [['export', 'Platformlara Yayınla', 'Seçili ürünleri pazar yerlerine gönder', () => setBulkPublishOpen(true)], ['refresh', 'Platformlara Güncelle', bulkUpdateMenu.detail, () => setBulkUpdateOpen(true), undefined, bulkUpdateMenu.disabled], 'hr', ['price', 'Toplu Fiyat ve Stok Düzenle', 'Seçili ürünlerin varyantları', () => setQuickEdit({ productIds: selectedProductIds, mode: 'both' })], ['price', 'Toplu Fiyat Düzenle', 'Fiyatları tek seferde güncelle', () => setQuickEdit({ productIds: selectedProductIds, mode: 'price' })], ['price', 'Toplu Stok Düzenle', 'Stokları artır, azalt veya eşitle', () => setQuickEdit({ productIds: selectedProductIds, mode: 'stock' })], ['doc', 'Toplu Değer Ata', 'Seçili ürünlere tek bir alanın değerini ata', () => setBulkFieldOpen(true)], 'hr', ['on', 'Toplu Satışa Aç', bulkActivatePlan.targetIds.length ? 'Seçili ürünleri satışa aç' : 'Seçili ürünlerin hepsi zaten satışa açık', () => requestBulkStatus('ACTIVE'), undefined, !bulkActivatePlan.targetIds.length], ['off', 'Toplu Satışa Kapat', bulkArchivePlan.targetIds.length ? 'Seçili ürünleri arşive al' : 'Seçili ürünlerin hepsi zaten satışa kapalı', () => requestBulkStatus('ARCHIVED'), 'rr-danger', !bulkArchivePlan.targetIds.length], ['trash', 'Seçili Ürünleri Sil', 'Yerel katalogdan kalıcı olarak kaldır', () => bulkDeleteProducts(), 'rr-danger']]
  const bulkMenu = (close: () => void) => <div className="rr-menu" role="menu" aria-label="Toplu işlemler" style={{ minWidth: 320 }}>{!selectedProductIds.length && <p className="rr-lead" style={{ padding: '6px 12px' }}>İşlem yapmak için önce en az bir ürün seçin.</p>}{bulkItems.map((entry, index) => entry === 'hr' ? <hr key={`hr-${index}`} /> : <button key={entry[1]} type="button" role="menuitem" className={['rr-two', entry[4]].filter(Boolean).join(' ')} disabled={!selectedProductIds.length || Boolean(entry[5])} onClick={() => { close(); entry[3]() }}><RrIcon name={entry[0]} /><span><b>{entry[1]}</b><small>{entry[2]}</small></span></button>)}</div>
  const pageList = (() => { const first = Math.max(1, Math.min(currentPage - 1, totalPages - 3)); return Array.from({ length: Math.min(4, totalPages) }, (_, index) => first + index) })()
  const statusRequestGroups = statusRequest ? productRowsAsCards(statusRequest.productIds.map(id => selectedProductCache[id] ?? products.find(product => product.id === id)).filter((product): product is Product => Boolean(product))) : []
  return <section className="content f3 rr rr-products">{confirmImportDialog}
    <div className="rr-ph"><div><div className="rr-crumb">Katalog</div><h1>Ürünler</h1></div><div className="rr-acts"><button type="button" className="rr-btn" onClick={() => setTransferOpen(true)} aria-haspopup="dialog"><RrIcon name="export" /><span className="rr-lbl-long">İçe / Dışa Aktar</span><span className="rr-lbl-short">Excel</span></button><button type="button" className="rr-btn" onClick={openProductImport}><RrIcon name="import" /><span className="rr-lbl-long">Platformdan Ürün Aktar</span><span className="rr-lbl-short">Ürün Aktar</span></button><Link className="rr-btn rr-primary" to="/products/new"><RrIcon name="plus" /><span className="rr-lbl-long">Yeni Ürün Ekle</span><span className="rr-lbl-short">Yeni Ürün</span></Link></div></div>
    <div className="rr-metrics rr-metrics-6"><article title="Katalogdaki ürün kartı sayısı"><CardBackgroundIcon name="products" /><small>Toplam Ürün</small><strong><CountUp value={metric(summaryQuery.data?.totalCount)} /></strong></article><article title="Tüm varyantların stok toplamı"><CardBackgroundIcon name="layers" /><small>Stok Adeti</small><strong><CountUp value={metric(summaryQuery.data?.totalUnits)} /></strong></article><article title="Satışa açık ürünler"><CardBackgroundIcon name="circleCheck" /><small>Aktif Ürün</small><strong><CountUp value={metric(summaryQuery.data?.activeCount)} /></strong></article><article title="Satışa açılmamış ürünler"><CardBackgroundIcon name="packagePen" /><small>Taslak Ürün</small><strong><CountUp value={metric(draftCountQuery.data?.totalCount ?? undefined, draftCountQuery)} /></strong></article><article className={outOfStock > 0 ? 'rr-bad' : undefined} title="Tüm varyantları tükenen ürünler"><CardBackgroundIcon name="shirtX" tone={toneAbove(outOfStock, 'bad')} /><small>Stoksuz Ürün</small><strong><CountUp value={metric(summaryQuery.data?.outOfStockCount)} /></strong></article><button type="button" className={lowStock > 0 ? 'rr-metric-btn rr-warn' : 'rr-metric-btn'} onClick={() => setLowStockOpen(true)} aria-haspopup="dialog"><CardBackgroundIcon name="alertTriangle" tone={toneAbove(lowStock, 'warn')} /><small>Düşük Stok</small><strong><CountUp value={metric(summaryQuery.data?.lowStockCount)} /></strong></button></div>
    <div className="rr-fshell">
      <ViewTabsWithMore className="product-status-tabs" more={[]} ariaLabel="Ürün durumları" value={status || 'ALL'} onChange={value => setStatus(value === 'ALL' ? '' : value)} items={statusTabs.map(([value, label, icon]) => ({ value: value || 'ALL', label: viewTabLabel(icon, label), count: tabCount(value) }))} />
      <RrFilterCard active={[Boolean(platform), Boolean(stock), Boolean(catalogSort)].filter(Boolean).length}><div className="rr-filters rr-has-sort">
        <label className="rr-fld rr-search"><small>Ürün Ara</small><span style={{ position: 'relative' }}><RrIcon name="search" style={{ position: 'absolute', left: 11, top: 10, color: 'var(--rr-subtle)' }} /><input className="rr-in" style={{ paddingLeft: 34 }} aria-label="Ürün ara" placeholder="Ürün adı, stok kodu veya barkod ara" value={search} onChange={event => setSearch(event.target.value)} /></span></label>
        <div className="rr-fld"><small>Stok Durumu</small><RrAnchor trigger={(open, toggle) => <button type="button" className={`rr-trigger${open ? ' rr-on' : ''}`} onClick={toggle}><span>{stockOptions.find(option => option.value === (stock || 'ALL'))?.label}</span><RrIcon name="chev" /></button>}>{columnPop('Stok Durumu', stockOptions, stock, setStock)}</RrAnchor></div>
        <div className="rr-fld"><small>Sıralama</small><RrAnchor trigger={(open, toggle) => <button type="button" className={`rr-trigger${open ? ' rr-on' : ''}`} onClick={toggle}><span>{sortOptions.find(option => option[0] === sortValue)?.[1]}</span><RrIcon name="chev" /></button>}>{close => <div className="rr-pop rr-right" role="listbox" style={{ minWidth: 260 }}>{sortOptions.map(([value, label]) => { const on = value === sortValue; return <button key={value} type="button" className={`rr-opt${on ? ' rr-on' : ''}`} role="radio" aria-checked={on} onClick={() => { setSortValue(value); close() }}><span className="rr-radio" aria-hidden="true" /><span className="rr-grow">{label}</span></button> })}</div>}</RrAnchor></div>
        <div className="rr-fbtns"><button type="button" className="rr-btn rr-clear" disabled={!(search || status || platform || stock || catalogSort)} onClick={() => { setSearch(''); setStatus(''); setPlatform(''); setStock(''); setCatalogSort(null) }}>Temizle</button><button type="button" className="rr-btn rr-primary" onClick={() => setSearchFilter(search.trim())}>Filtrele</button></div>
      </div></RrFilterCard>
    </div>
    {selectionMode && <div className="rr-selbar" role="region" aria-label="Seçim modu"><button type="button" className="rr-btn rr-quiet" onClick={exitSelectionMode}>İptal</button><span className="rr-grow">{selectedProductCardCount} ürün kartı seçildi</span><button type="button" className="rr-btn" onClick={toggleAllVisible}>{allVisibleSelected ? 'Seçimi Kaldır' : 'Tümünü Seç'}</button></div>}
    {(query.error ?? summaryQuery.error ?? connectionsQuery.error) && <RrError error={query.error ?? summaryQuery.error ?? connectionsQuery.error} onRetry={() => { void query.refetch(); void summaryQuery.refetch() }} />}
    <div className="rr-panel"><div className="rr-panel-h"><div><h2>Ürün Kataloğu</h2><p>Toplam {totalCount.toLocaleString('tr-TR')} ürün</p></div><RrPageSize value={pageSize} sizes={[20, 50, 100]} onChange={setPageSize} /></div>
      {selectedProductIds.length > 0 && hasMoreProductsToSelect && <div className="rr-xsel" role="status"><span><b>{allProductsSelected ? 'Tüm ürün kartları seçildi.' : `${selectedProductCardCount.toLocaleString('tr-TR')} ürün kartı seçildi.`}</b> {allProductsSelected ? `${selectedProductIds.length.toLocaleString('tr-TR')} katalog kaydı toplu işlemlere dahil.` : 'Diğer sayfalardaki kartlar seçime dahil değil.'}</span>{allProductsSelected ? <button type="button" className="rr-btn rr-link" onClick={() => { setSelectedProductIds([]); setSelectedProductCache({}); setAllProductsSelected(false) }}>Seçimi Temizle</button> : <button type="button" className="rr-btn rr-link" onClick={() => void selectAllFilteredProducts()} disabled={selectingAllProducts}>{selectingAllProducts ? 'Tüm Sayfalar Seçiliyor…' : `Tüm ${totalCount.toLocaleString('tr-TR')} Kaydı Seç`}</button>}</div>}
      <div ref={productListRef} className={`rr-tblwrap${query.isPlaceholderData ? ' is-stale-data' : ''}`} aria-busy={query.isPlaceholderData || undefined}>{query.isLoading && !pageProducts.length ? <RrLoading text="Ürünler yükleniyor…" /> : <table className="rr-tbl rr-ptbl">
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
        <tbody>{!pageProducts.length ? <tr><td className="rr-empty-row" colSpan={7}><ListEmptyState icon="products" noun="Ürün" hasActiveFilters={Boolean(search || status || platform || stock)} filteredHint="Filtrelerle eşleşen ürün yok." emptyHint="Ürünler pazaryerlerinden içe aktarılır ya da elle eklenir." onClear={() => { setSearch(''); setStatus(''); setPlatform(''); setStock('') }} /></td></tr> : pageProductGroups.map((group, rowIndex) => <ProductColorRows priority={rowIndex < 6} key={group.id} selectionMode={selectionMode} onEnterSelection={() => setSelectionMode(true)} group={group} selected={group.products.every(product => selectedProductIds.includes(product.id))} onSelect={() => toggleProductGroup(group)} onQuickEdit={mode => setQuickEdit({ productIds: group.products.map(product => product.id), mode })} onImageClick={(url, title, modelCode) => setLightboxImage({ url, title, modelCode })} onDelete={() => requestDeleteProductGroup(group)} onDuplicate={() => navigate('/products/new', { state: { duplicateProductId: group.primary.id } })} onSetStatus={next => { const plan = bulkStatusPlan(group.products.map(product => product.id), id => group.products.find(product => product.id === id)?.status, next); if (!plan.targetIds.length) return; setSelectedProductCache(current => ({ ...current, ...Object.fromEntries(group.products.map(product => [product.id, product])) })); setStatusRequest({ productIds: plan.targetIds, status: next, skippedCount: plan.alreadyIds.length, draftCount: plan.draftIds.length }) }} onPublish={setPublishProductId} />)}</tbody>
      </table>}</div>
      {totalCount > 0 && <div ref={productPagerRef} className="rr-pager"><span>Toplam <b style={{ color: 'var(--rr-ink)' }}>{totalCount.toLocaleString('tr-TR')}</b> adet</span><div className="rr-pages"><button type="button" aria-label="Önceki" disabled={currentPage <= 1 || query.isFetching || pageJumping} onClick={() => setPageNumber(value => Math.max(1, value - 1))}><RrIcon name="left" /></button>{pageList.map(value => <button key={value} type="button" className={value === currentPage ? 'rr-on' : undefined} aria-current={value === currentPage ? 'page' : undefined} disabled={query.isFetching || pageJumping} onClick={() => void goToPage(value)}>{value}</button>)}<button type="button" aria-label="Sonraki" disabled={!(currentPage < totalPages && Boolean(nextPageCursor)) || query.isFetching || pageJumping} onClick={goToNextPage}><RrIcon name="right" /></button></div><label className="rr-jump"><span className="rr-jump-label">Sayfaya Git</span><input className="rr-in" type="number" min={1} max={totalPages} placeholder={`1–${totalPages}`} aria-label="Sayfa numarası" value={jumpPage} onChange={event => setJumpPage(event.target.value)} onKeyDown={event => { if (event.key === 'Enter' && Number(jumpPage) >= 1) { void goToPage(Math.floor(Number(jumpPage))); setJumpPage('') } }} /><button type="button" className="rr-btn" onClick={() => { if (Number(jumpPage) >= 1) { void goToPage(Math.floor(Number(jumpPage))); setJumpPage('') } }}>Git</button></label></div>}
    </div>
    <StickyActionBar open={selectedProductIds.length > 0} label="Toplu işlemler" className="rr-bulk rr-pbulk"><span className="rr-grow rr-bulk-count"><span>{allProductsSelected ? 'Tüm ürün kartları seçildi' : `${selectedProductCardCount.toLocaleString('tr-TR')} ürün kartı seçildi`}</span>{hasMoreProductsToSelect && !allProductsSelected && <button type="button" className="rr-btn rr-link rr-bulk-all" onClick={() => void selectAllFilteredProducts()} disabled={selectingAllProducts}>{selectingAllProducts ? 'Seçiliyor…' : `Tümünü Seç (${totalCount.toLocaleString('tr-TR')})`}</button>}</span><button type="button" className="rr-btn rr-quiet rr-bulk-clear" aria-label="Seçimi Temizle" title="Seçimi Temizle" onClick={() => { setSelectedProductIds([]); setSelectedProductCache({}); setAllProductsSelected(false) }}><RrIcon name="close" /><span>Seçimi Temizle</span></button>{bulkActivatePlan.targetIds.length > 0 && <button type="button" className="rr-btn" title={bulkActivatePlan.alreadyIds.length ? `${bulkActivatePlan.alreadyIds.length.toLocaleString('tr-TR')} kayıt zaten satışa açık; yalnızca kalan ${bulkActivatePlan.targetIds.length.toLocaleString('tr-TR')} kayıt açılır.` : undefined} onClick={() => requestBulkStatus('ACTIVE')}><RrIcon name="on" />Satışa Aç</button>}<button type="button" className="rr-btn" onClick={() => setQuickEdit({ productIds: selectedProductIds, mode: 'both' })}><RrIcon name="price" /><span className="rr-lbl-long">Fiyat ve Stok Düzenle</span><span className="rr-lbl-short">Fiyat · Stok</span></button><RrAnchor trigger={(open, toggle) => <button type="button" className="rr-btn rr-primary" aria-haspopup="menu" aria-expanded={open} onClick={toggle}><span className="rr-lbl-long">Toplu İşlemler ({selectedProductCardCount.toLocaleString('tr-TR')})</span><span className="rr-lbl-short">Toplu ({selectedProductCardCount.toLocaleString('tr-TR')})</span><RrIcon name="chev" /></button>}>{close => <div className="rr-bulk-menu">{bulkMenu(close)}</div>}</RrAnchor></StickyActionBar>
    {statusRequest && <RrModal crumb="Toplu İşlem" title={statusRequest.status === 'ACTIVE' ? 'Satışa Aç' : 'Satışa Kapat'} desc={`${statusRequestGroups.length || statusRequest.productIds.length} ürün kartı ${statusRequest.status === 'ACTIVE' ? 'satışa açılacak' : 'arşive alınacak'}.`} size="sm" locked={statusProgress != null} onClose={() => setStatusRequest(null)} foot={<><span className="rr-note" role="status">{statusProgress ? `${statusProgress.done.toLocaleString('tr-TR')} / ${statusProgress.total.toLocaleString('tr-TR')} ürün güncellendi` : ''}</span><button type="button" className="rr-btn" onClick={() => setStatusRequest(null)} disabled={statusProgress != null}>Vazgeç</button><button type="button" className={`rr-btn ${statusRequest.status === 'ACTIVE' ? 'rr-primary' : 'rr-danger'}`} disabled={statusProgress != null} onClick={() => void bulkSetProductStatus(statusRequest.status, statusRequest.productIds)}>{statusProgress ? 'Uygulanıyor…' : statusRequest.status === 'ACTIVE' ? 'Satışa Aç' : 'Satışa Kapat'}</button></>}>
      <div className="rr-sum">{statusRequestGroups.slice(0, 6).map(group => <div key={group.id}><RrThumb src={productGroupImage(group)} alt={group.primary.title} /><span>{group.primary.title}<small>{group.primary.modelCode || '—'} · {group.variants.length} varyant</small></span><RrBadge label={group.products.every(product => product.status === 'ACTIVE') ? 'Satışa Açık' : titleTr(productStatusLabel(group.primary.status))} /></div>)}</div>
      {statusRequestGroups.length > 6 && <p className="rr-lead">ve {statusRequestGroups.length - 6} ürün kartı daha</p>}
      {(statusRequest.skippedCount ?? 0) > 0 && <p className="rr-lead">{(statusRequest.skippedCount ?? 0).toLocaleString('tr-TR')} kayıt zaten {statusRequest.status === 'ACTIVE' ? 'satışa açık' : 'satışa kapalı'}; bu kayıtlar gönderilmez.</p>}
      {(statusRequest.draftCount ?? 0) > 0 && <p className="rr-lead">{(statusRequest.draftCount ?? 0).toLocaleString('tr-TR')} taslak kayıt değişmez; taslaklar ürün düzenleme ekranında tamamlanıp Kaydet ile satışa açılır.</p>}
      {statusRequest.status === 'ACTIVE' && <p className="rr-lead">Zorunlu alanı eksik kartlar atlanır ve bildirimlerde eksik alanlarıyla listelenir; diğerleri satışa açılır.</p>}
      {statusRequest.status === 'ARCHIVED' && <div className="rr-callout rr-warn"><RrIcon name="alert" /><span>Pazar yerlerindeki ilanlar bu işlemle kapanmaz; kapatmak için ürün düzenleme ekranından yayını durdurun.</span></div>}
    </RrModal>}
    {quickEdit && <ProductQuickEditModal products={quickEditProducts} connections={publicationConnections} mode={quickEdit.mode} onChanged={refreshQuickEdit} onResult={showProductToast} onClose={() => setQuickEdit(null)} />}
    {deleteRequest && <ProductDeleteConfirmModal request={deleteRequest} deleting={deletingProducts} onClose={() => setDeleteRequest(null)} onConfirm={() => void confirmProductDelete()} />}
    {productImportOpen && <RrModal crumb="Katalog Aktarımı" label="Platformdan Ürün Aktarımı" modalClass="rr-pimport-modal" title={<>Platformdan Ürün Aktarımı<TitleHint label="Platformdan Ürün Aktarımı">Bağlı pazar yerlerindeki ürünleri panel kataloğunuza aktarın. İşlem salt okunurdur; pazar yerlerine hiçbir veri gönderilmez.</TitleHint></>} size="lg" locked={productImporting} onClose={() => setProductImportOpen(false)} foot={<><span className="rr-note">{productImportConnectionIds.length ? <><b>{productImportConnectionIds.length} bağlantı</b> seçili · {productImportMethod === 'SINGLE' ? 'Tek Ürün Aktarımı' : titleTr(productImportModeLabels[productImportMode] ?? '')}</> : 'Bağlantı seçilmedi'}</span><button type="button" className="rr-btn" onClick={() => setProductImportOpen(false)} disabled={productImporting}>Vazgeç</button><button type="button" className="rr-btn rr-primary" onClick={() => void importProductsFromPlatforms()} disabled={productImporting || !productImportConnectionIds.length || productImportChoiceMissing} title={productImportChoiceMissing ? 'En az bir seçenek işaretleyin' : undefined}><RrIcon name="import" />{productImporting ? 'Kuyruğa Alınıyor…' : 'Aktarımı Başlat'}</button></>}>
      <div className="rr-h3">Pazar Yeri Bağlantıları</div>
      {connections.length ? <div className="rr-srcs rr-conns">{connections.map(connection => { const selected = productImportConnectionIds.includes(connection.id); return <label key={connection.id} className={`rr-src${selected ? ' rr-on' : ''}`} title={`${connection.platformCode} · ${connection.externalStoreId}`}><input type="checkbox" className="rr-cb" checked={selected} onChange={() => setProductImportConnectionIds(ids => selected ? ids.filter(id => id !== connection.id) : productImportMethod === 'SINGLE' ? [connection.id] : [...ids, connection.id])} /><RrLogo code={connection.platformCode} /><span><b><span>{connection.displayName}</span><span className="rr-src-ok" title="Aktif bağlantı" aria-label="Aktif bağlantı"><RrIcon name="check" size={13} /></span></b><small>Mağaza {shortStoreId(connection.externalStoreId)}</small></span></label> })}</div> : <div className="rr-callout rr-warn"><RrIcon name="alert" /><span>Aktarıma uygun, aktif veya doğrulanmış bir pazar yeri bağlantısı bulunamadı.</span></div>}
      <div className="rr-seg" role="tablist" aria-label="Ürün aktarım türü"><button type="button" role="tab" aria-selected={productImportMethod === 'BULK'} className={productImportMethod === 'BULK' ? 'rr-on' : undefined} onClick={() => setProductImportMethod('BULK')}>Toplu Aktarım</button><button type="button" role="tab" aria-selected={productImportMethod === 'SINGLE'} className={productImportMethod === 'SINGLE' ? 'rr-on' : undefined} onClick={() => { setProductImportMethod('SINGLE'); setProductImportConnectionIds(ids => ids.slice(0, 1)) }}>Tek Ürün Aktarımı</button></div>
      {productImportMethod === 'SINGLE' ? <label className="rr-fld"><small>{titleTr(singleLookupLabel)}</small><input className="rr-in" value={productImportLookup} onChange={event => setProductImportLookup(event.target.value)} placeholder={singleLookupLabel} /><em>{singleProductLookupHint(selectedImportPlatforms[0] ?? '')}</em></label> : <><div className="rr-h3">Aktarım Kapsamı</div><div className="rr-radios">{([['FULL', 'Tam Katalog Taraması'], ['NEW_ONLY', 'Yeni Ürünleri Aktar'], ...(productImportOptions.showExistingMode ? [['EXISTING_ONLY', 'Mevcut Ürünleri Güncelle']] : []), ['MAPPING_ONLY', 'Ürün Eşleme']] as Array<[typeof productImportMode, string]>).map(([value, label]) => <label key={value} className={`rr-rcard${productImportMode === value ? ' rr-on' : ''}`}><input type="radio" name="product-import-mode" value={value} checked={productImportMode === value} onChange={() => setProductImportMode(value)} hidden /><span className="rr-radio-dot" aria-hidden="true" /><span><b>{label}</b><small>{productImportModeDescription(value, selectedImportPlatforms)}</small></span></label>)}</div></>}
      {productImportOptions.showOptionsSection && <><div className="rr-h3">Gelişmiş Seçenekler</div><div className="rr-srcs">
        {productImportOptions.showUpdateExisting && <label className={`rr-src${productImportUpdateExistingProducts ? ' rr-on' : ''}`}><input type="checkbox" className="rr-cb" checked={productImportUpdateExistingProducts} onChange={event => setProductImportUpdateExistingProducts(event.target.checked)} /><span><b>Mevcut Ürün Bilgilerini Güncelle</b><small>{productImportUpdateExistingProducts ? 'Açık: tüm özellikler, ad, açıklama, kategori ve marka pazaryerindekiyle değiştirilir, gönderilmemiş panel değişiklikleri de; modelin yeni beden ve renkleri doğru renk kartına eklenir. Panelde yaptığınız görsel atamaları ve sıralaması korunur.' : 'Kapalı: ürün içeriği, özellikler ve görseller değişmez; yeni bedenler eklenmez, iş özetinde bildirilir.'}{productImportOptions.updateExistingScopeNote ? ` ${productImportOptions.updateExistingScopeNote}` : ''}</small></span></label>}
        {productImportOptions.showPriceStock && <label className={`rr-src${productImportUpdatePrices ? ' rr-on' : ''}`}><input type="checkbox" className="rr-cb" checked={productImportUpdatePrices} onChange={event => setProductImportUpdatePrices(event.target.checked)} /><span><b>Fiyatları Güncelle</b><small>{productImportUpdatePrices ? 'Açık: panel satış ve liste fiyatı pazaryerindeki fiyatla değiştirilir.' : 'Kapalı: panelde var olan fiyatlar korunur.'}{productImportOptions.priceStockScopeNote ? ` ${productImportOptions.priceStockScopeNote}` : ''}</small></span></label>}
        {productImportOptions.showPriceStock && <label className={`rr-src${productImportUpdateStock ? ' rr-on' : ''}`}><input type="checkbox" className="rr-cb" checked={productImportUpdateStock} onChange={event => setProductImportUpdateStock(event.target.checked)} /><span><b>Stokları Güncelle</b><small>{productImportUpdateStock ? 'Açık: panel stoğu pazaryerindeki stokla değiştirilir; değişim stok hareketlerine yazılır.' : 'Kapalı: panelde var olan stok korunur.'}{productImportOptions.priceStockScopeNote ? ` ${productImportOptions.priceStockScopeNote}` : ''}</small></span></label>}
        {productImportOptions.readOnlyNotes.map(note => <div key={note} className="rr-src"><span><b>İçerik Koruması</b><small>{note}</small></span></div>)}
        {productImportOptions.showArchived && <label className={`rr-src${productImportIncludeArchived ? ' rr-on' : ''}`}><input type="checkbox" className="rr-cb" checked={productImportIncludeArchived} onChange={event => setProductImportIncludeArchived(event.target.checked)} /><span><b>{selectedImportHasShopify ? 'Arşiv ve Taslak Ürünlerini Dahil Et' : 'Arşiv Ürünlerini Dahil Et'}</b><small>{productImportIncludeArchived ? 'Açık: aktif ve arşivlenmiş ürünler birlikte aktarılır.' : 'Kapalı: yalnızca aktif ürünler aktarılır.'}</small></span></label>}
        {productImportOptions.showPendingApproval && <label className={`rr-src${productImportIncludePendingApproval ? ' rr-on' : ''}`}><input type="checkbox" className="rr-cb" checked={productImportIncludePendingApproval} onChange={event => setProductImportIncludePendingApproval(event.target.checked)} /><span><b>Onay Bekleyen Ürünleri Dahil Et</b><small>{productImportIncludePendingApproval ? 'Açık: onay sürecindeki Trendyol ürünleri de işlenir.' : 'Kapalı: yalnızca onaylı Trendyol ürünleri işlenir.'}</small></span></label>}
      </div></>}
      {(productImportOptions.showUpdateExisting || productImportOptions.showPriceStock) && <div className={`rr-callout${productImportChoiceMissing ? ' rr-warn' : ''}`} role="status" data-testid="product-import-choice-summary"><RrIcon name={productImportChoiceMissing ? 'alert' : 'info'} /><span>{productImportChoiceSummary(productImportMethod, productImportChoices)}</span></div>}
      {activeProductSyncJobs.length > 0 && <section className="rr-dsec" aria-live="polite"><header><div className="rr-h3">Devam Eden Aktarımlar</div><p>{activeProductSyncJobs.length} işlem</p></header>{activeProductSyncJobs.map(job => { const received = Math.max(0, job.progressReceived); const total = job.progressTotal != null && job.progressTotal >= received ? job.progressTotal : null; const percent = job.progressPercent != null ? Math.min(100, Math.max(0, job.progressPercent)) : total != null && total > 0 ? Math.min(100, Math.max(0, Math.floor(received * 100 / total))) : null; const label = job.progressLabel ?? (job.status === 'PENDING' ? 'Kuyrukta bekliyor' : 'Aktarım çalışıyor'); const mapping = job.progressLabel?.includes('Eşlenen') ?? false; const jobConnection = (connectionsQuery.data?.items ?? []).find(item => item.id === job.connectionId); return <div className="rr-job" key={job.id}><div className="rr-job-h">{jobConnection && <RrLogo code={jobConnection.platformCode} />}<b>{jobConnection && <span className="rr-job-src">{jobConnection.displayName}</span>}{label}</b><span className="rr-num">{percent == null ? '—' : `%${percent}`}</span><button type="button" className="rr-btn rr-quiet" style={{ height: 28 }} disabled={cancelProductSync.isPending} onClick={() => cancelProductSync.mutate(job.id)}>Durdur</button></div><span className="rr-bar"><i style={{ width: `${percent ?? 8}%` }} /></span><div className="rr-job-c"><span>Alınan <b className="rr-num">{received.toLocaleString('tr-TR')}</b></span><span>{mapping ? 'Eşlenen' : 'İşlenen'} <b className="rr-num">{job.progressProcessed.toLocaleString('tr-TR')}</b></span><span>{mapping ? 'Eşleşmeyen' : 'Atlanan'} <b className="rr-num">{job.progressSkipped.toLocaleString('tr-TR')}</b></span><span>Hatalı <b className="rr-num rr-bad">{job.progressFailed.toLocaleString('tr-TR')}</b></span></div></div> })}</section>}
    </RrModal>}
    {transferOpen && <ProductTransferDialog onClose={() => setTransferOpen(false)} />}
    {publishProductId && <Suspense fallback={null}><ProductPublishPopup productId={publishProductId} onClose={() => { setPublishProductId(null); void client.invalidateQueries({ queryKey: ['products'] }) }} /></Suspense>}
    {/* One product card (a whole model family) opens the very same popup as its row's Platformlarda Yayınla. */}
    {bulkFieldOpen && selectedProductIds.length > 0 && <Suspense fallback={null}><BulkFieldDialog {...selectionModelGroups(selectedProductIds, selectedProductCache)} cardCount={selectedProductCardCount} onClose={() => setBulkFieldOpen(false)} onDone={async outcome => { showProductToast(outcome.title, outcome.message, outcome.kind, outcome.skipped ? { operation: 'Toplu Değer Ata', details: outcome.skipped.details, target: outcome.skipped.target, record: true } : { operation: 'Toplu Değer Ata' }); setBulkFieldOpen(false); await refresh().catch(() => undefined) }} /></Suspense>}
    {bulkPublishOpen && selectedProductCardCount === 1 &&selectedProductIds[0] && <Suspense fallback={null}><ProductPublishPopup productId={selectedProductIds[0]} onClose={() => { setBulkPublishOpen(false); void client.invalidateQueries({ queryKey: ['products'] }) }} /></Suspense>}
    {bulkUpdateOpen && <Suspense fallback={null}><BulkPlatformUpdateDialog productIds={selectedProductIds} cardCount={selectedProductCardCount} connections={connectionsQuery.data?.items ?? []} products={[...Object.values(selectedProductCache), ...products]} onClose={() => setBulkUpdateOpen(false)} onDone={notice => showProductToast(notice.title, notice.message, notice.kind, notice.details ? { operation: 'Toplu Güncelleme', details: notice.details, record: true } : { operation: 'Toplu Güncelleme' }, { lines: notice.lines, persistent: notice.persistent })} /></Suspense>}
    {bulkPublishOpen && selectedProductCardCount !== 1 && <Suspense fallback={null}><BulkPublishDialog productIds={selectedProductIds} cardCount={selectedProductCardCount} connections={publicationConnections} products={[...Object.values(selectedProductCache), ...products]} onClose={() => setBulkPublishOpen(false)} onDone={notice => showProductToast(notice.title, notice.message, notice.kind, notice.details ? { operation: 'Toplu Yayın', details: notice.details, record: true } : { operation: 'Toplu Yayın' }, { lines: notice.lines, persistent: notice.persistent })} /></Suspense>}
    {lowStockOpen && <LowStockDetailsModal products={lowStockQuery.data?.items ?? []} loading={lowStockQuery.isLoading} error={lowStockQuery.error} onClose={() => setLowStockOpen(false)} onImageClick={(url, title, modelCode) => setLightboxImage({ url, title, modelCode })} />}
    {lightboxImage && <ImageLightboxModal image={lightboxImage} onClose={() => setLightboxImage(null)} />}
  </section>
}
const productImportModeLabels: Record<string, string> = { FULL: 'Tam katalog taraması', NEW_ONLY: 'Yeni ürünleri aktar', EXISTING_ONLY: 'Mevcut ürünleri güncelle', MAPPING_ONLY: 'Ürün eşleme' }
function shortStoreId(id: string) { return id.length > 14 ? `${id.slice(0, 8)}…${id.slice(-4)}` : id }
