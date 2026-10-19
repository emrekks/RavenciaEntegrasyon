import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type DragEvent, type FormEvent, type PointerEvent as ReactPointerEvent, type ReactNode } from 'react'
import { variantOptionParts } from './variant-option-label'
import { Link, useLocation, useNavigate, useParams } from 'react-router'
import { createPortal } from 'react-dom'
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiRequestError, hubApi, loadAllPages, type CursorPage } from '../../shared/api'
import { UiIcon, type UiIconName } from '../../shared/components'
import { NoticeToast } from '../../shared/components/NoticeToast'
import { sanitizeRichText } from '../../shared/security/sanitizeHtml'
import { productStatusLabel, statusLabel } from '../../shared/status-labels'
import { platformLogoClass, platformLogoSource } from '../../shared/platform-logos'
import './publication-status-tooltip.css'
import './product-import.css'
import './marketplace-pricing.css'
import './variant-header-menu.css'
import './publish-platform-issues.css'
import './products-responsive.css'
import './catalog-polish.css'
import './products-sade.css'
import { RrAnchor, RrBadge, RrColumnPop, RrError, RrFilterCard, RrHeadCell, RrIcon, RrImagePreview, RrLoading, RrLogo, RrModal, RrPageSize, RrRing, RrThumb, titleTr, type RingTone } from '../marketplace/returns-ui'
import { appendNotification } from '../../shared/notifications'
import { toggleProductAttributeValue } from './attribute-selection'
import { buildVariantAttributeAssignments, type VariantAttributeAssignment } from './variant-attribute-assignments'
import { filterAttributeOptionValues } from './attribute-value-search'
import { isStoredProductMediaUrl, mediaImageKey, mediaRefsEqual, mediaRefsSameSet, mediaUrlsInPreferredOrder, modelCodeForExistingVariant, publicProductMediaUrls, reorderItems, reorderMediaUrls, uniqueMediaUrls } from './product-media-editor'
import { applyVariantBulkEditValue, updateVariantGroupSelection, variantBulkEditIssue, variantGroupSelectionState, type VariantBulkEditField } from './variant-bulk-edit'
import { applyGeneratedVariantCodes, buildSequentialVariantCodes, buildVariantGenerationDefaults, resolveVariantSyncAttributeIds } from './variant-generation'
import { filterVariantsByOptions, selectVariantDraftsByKeys, type VariantOptionFilterSelections } from './variant-filtering'
import { displayVariantOptionEntriesFromSignature, formatColorOptionValue, joinVariantOptionLabels, matchingVariantOptionValues, mergeVariantOptionEntries, mergeVariantOptionValues, normalizeVariantOptionValue, parseVariantOptionSignature } from './variant-option-matching'
import { classifyPublicationAttributeIssues, fixableValueMappings, type PublicationAttributeIssues, type PublicationAttributeSelection, type PublicationMappingReference, type PublicationValueReferenceSet } from './publication-attribute-readiness'
import { MissingValueMappingDialog } from './MissingValueMappingDialog'
import { PublicationReadinessSourceError, publicationReadinessFailureDetail, readPublicationReadinessSource } from './publication-readiness-error'
import { productMediaUrlIssue } from './product-media-url'
import { barcodeClipboardIssue, parseBarcodeClipboardValues } from './product-barcode-paste'
import { productCopyIdentifierConflicts } from './product-copy-identifiers'
import { sameProductSnapshotIgnoringVersion } from './product-save-version'
import { isPublicationLive, isPublicationSelectionDisabled, isPublicationStatusJobRunning, publicationStatusLabel, publicationStatusNote, publicationStatusTone, shouldCheckPublicationAttributes } from './publication-status'
import { productPlatformDisplayLabel, productPlatformDisplayState } from './product-platform-status'
import { quickPlatformUpdateTargets } from './platform-update-targets'
import { nextProductCatalogSort, sortProductGroups, type ProductCatalogSort } from './product-catalog-sorting'
import { productPublicationTargets } from './product-publication-submit'
import { productPublicationChannelMode } from './product-publication-channels'
import { activeProductSyncJobs as filterActiveProductSyncJobs, productSyncJobsFinished, productSyncJobsPath } from './product-sync-tracking'
import { readVariantMediaAssignmentDraft, updateVariantMediaAssignmentDraft, variantMediaAssignmentKey, type VariantMediaAssignmentDrafts } from './variant-media-assignments'
import { OperationFeedbackToast, type OperationFeedback } from './operation-feedback-toast'
import { productImportConnection, singleProductLookupLabel } from './product-import-platforms'
import { productImportOptionVisibility as getProductImportOptionVisibility, type ProductImportMethod, type ProductImportMode } from './product-import-options'
import { productPlatformFilterGroups } from './product-platform-filter'
import { formatPanelColorValue } from '../marketplace/color-value-format'
import { ViewTabsWithMore, viewTabLabel } from '../../shared/view-tabs'

type Versioned = { id: string; version: number }
type Category = Versioned & { name: string; path: string; depth: number; isLeaf: boolean; isActive: boolean }
type Brand = Versioned & { name: string; isActive: boolean }
type Attribute = Versioned & { code: string; name: string; dataType: string; values: Array<{ id: string; value: string }>; roles?: string[] | null }
type ProductAttributeAssignment = VariantAttributeAssignment
type Variant = Versioned & {
  sku: string; barcode: string | null; modelCode: string | null; optionSignature: string; status: string
  weight: number | null; width: number | null; height: number | null; length: number | null; desi: number | null; costPrice: number | null
  defaultListPrice?: number | null; defaultSalePrice?: number | null
  onHand: number; available: number; inventoryVersion: number | null
  offerId: string | null; listPrice: number | null; salePrice: number | null; currency: string | null; offerStatus: string | null
  priceVersion: number | null; offerVersion: number | null; vatRate: number | null; vatInclusion: string | null; roundingMode: string | null; safetyStock: number | null
  mediaUrls?: string[]; options?: Record<string, string>; platformStatuses?: VariantPlatformStatus[]; attributes?: ProductAttributeAssignment[]
}
type ProductPlatformStatus = { platform: string; platformCode?: string; status: string; matchedVariantCount?: number; variantCount?: number; isChecking?: boolean }
type VariantPlatformStatus = {
  platform: string; platformCode: string; isLinked: boolean; status?: string
  connectionId?: string | null; offerId?: string | null; listPrice?: number | null; salePrice?: number | null
  currency?: string | null; vatRate?: number | null; vatInclusion?: string | null; roundingMode?: string | null
  safetyStock?: number | null; offerVersion?: number | null
}
type Product = Versioned & {
  title: string; description: string; brandId: string | null; categoryId: string | null; status: string; updatedAt: string
  defaultListPrice?: number | null; defaultSalePrice?: number | null
  categoryPath?: string | null; variants: Variant[]; primaryImageUrl: string | null; totalStock: number; startingPrice: number | null; currency: string; modelCode: string | null; activePlatforms: string[] | null; familyMediaUrls?: string[]
  familyMediaItems?: Array<{ url: string; mediaIds: string[]; sourceProductTitles: string[] }>; familyOrderedMediaUrls?: string[]; hasCustomMediaOrder?: boolean
  platformStatuses?: ProductPlatformStatus[]
  attributes?: ProductAttributeAssignment[]
  options?: Array<{ id: string; label: string; values: Array<{ id: string; label: string }> }>
  mediaUrls?: string[]
}

function orderMediaUrlsByVariants(variants: Variant[], productMediaUrls: string[], primaryImageUrl: string | null, hasCustomMediaOrder = false) {
  if (hasCustomMediaOrder) return [...productMediaUrls]
  const ordered: string[] = []
  const seen = new Set<string>()
  const seenColors = new Set<string>()
  const add = (url: string | null | undefined) => {
    const normalized = url?.trim()
    if (!normalized) return
    const key = normalized.toLocaleLowerCase('tr-TR')
    if (seen.has(key)) return
    seen.add(key)
    ordered.push(normalized)
  }

  const colorVariants = variants.some(variant => variantColorKey(variant) !== null)
  if (colorVariants) {
    for (const variant of variants) {
      const color = variantColorKey(variant)
      const group = color ?? `variant:${variant.id}`
      if (seenColors.has(group)) continue
      seenColors.add(group)
      for (const url of variant.mediaUrls ?? []) add(url)
    }
    if (!ordered.length) add(productMediaUrls[0])
  } else {
    for (const variant of variants) for (const url of variant.mediaUrls ?? []) add(url)
    for (const url of productMediaUrls) add(url)
  }
  add(primaryImageUrl)
  return ordered
}

function variantColorKey(variant: Variant) {
  const color = preferredColorOption(variantOptionEntries(variant))
  return color?.value.trim().toLocaleLowerCase('tr-TR') || null
}

function seedVariantMediaRefs(variants: Variant[]) {
  const mediaByColor = new Map<string, string[]>()
  for (const variant of variants) {
    const color = variantColorKey(variant)
    if (!color) continue
    const urls = mediaByColor.get(color) ?? []
    for (const url of variant.mediaUrls ?? []) if (url && !urls.some(item => item.localeCompare(url, undefined, { sensitivity: 'accent' }) === 0)) urls.push(url)
    mediaByColor.set(color, urls)
  }
  return variants.map(variant => {
    const color = variantColorKey(variant)
    const urls = color ? (mediaByColor.get(color) ?? variant.mediaUrls ?? []) : (variant.mediaUrls ?? [])
    return [...new Set(urls.filter(Boolean).map(url => `url|${url}`))]
  })
}

function compareLabelsAlphabetically(left: string, right: string) {
  return left.trim().localeCompare(right.trim(), 'tr-TR', { sensitivity: 'base', numeric: true })
}

function compareOptionValues(attributeName: string, left: string, right: string) {
  const leftRank = optionValueSortRank(attributeName, left)
  const rightRank = optionValueSortRank(attributeName, right)
  return leftRank.bucket - rightRank.bucket
    || leftRank.primary - rightRank.primary
    || leftRank.secondary - rightRank.secondary
    || leftRank.text.localeCompare(rightRank.text, 'tr-TR', { numeric: true, sensitivity: 'base' })
}

function compareVariantOptionSignatures(left: string, right: string) {
  const leftOptions = parseVariantOptionSignature(left)
  const rightOptions = parseVariantOptionSignature(right)
  const leftColor = leftOptions.find(option => isColorOptionName(option.name))?.value
  const rightColor = rightOptions.find(option => isColorOptionName(option.name))?.value
  const leftSize = leftOptions.find(option => ['BEDEN', 'SIZE', 'SIZ', 'NUMARA', 'NUMBER'].includes(normalizeVariantOptionName(option.name)))?.value
  const rightSize = rightOptions.find(option => ['BEDEN', 'SIZE', 'SIZ', 'NUMARA', 'NUMBER'].includes(normalizeVariantOptionName(option.name)))?.value

  if (leftColor && rightColor) {
    const colorOrder = compareLabelsAlphabetically(leftColor, rightColor)
    if (colorOrder) return colorOrder
  }
  if (leftSize && rightSize) {
    const sizeOrder = compareOptionValues('Beden', leftSize, rightSize)
    if (sizeOrder) return sizeOrder
  }
  return compareLabelsAlphabetically(left, right)
}

function sortVariantsAlphabetically<T extends { optionSignature?: string | null; sku: string }>(variants: T[]) {
  return [...variants].sort((left, right) => {
    const leftLabel = (left.optionSignature || left.sku).trim()
    const rightLabel = (right.optionSignature || right.sku).trim()
    return compareVariantOptionSignatures(leftLabel, rightLabel)
      || compareLabelsAlphabetically(left.sku, right.sku)
  })
}

function compareVariantItemsAlphabetically(left: { variant: { optionSignature?: string | null; sku: string } }, right: { variant: { optionSignature?: string | null; sku: string } }) {
  const leftLabel = (left.variant.optionSignature || left.variant.sku).trim()
  const rightLabel = (right.variant.optionSignature || right.variant.sku).trim()
  return compareVariantOptionSignatures(leftLabel, rightLabel)
    || compareLabelsAlphabetically(left.variant.sku, right.variant.sku)
}

type ProductListFilters = { search: string; status: string; platform: string; stock: string }
type ProductSummary = { totalCount: number; activeCount: number; outOfStockCount: number; lowStockCount: number; platforms: string[] }
type ImportSession = Versioned & { sourceType: string; status: string; totalRows: number; validRows: number; errorRows: number; reviewRows: number; sourceAssetId: string | null }
type Candidate = Versioned & { matchRule: string; safeSummary: string; productId: string | null; variantId: string | null }
type MarketplaceConnection = { id: string; platformCode: string; displayName: string; externalStoreId: string; status: string; externalWritesEnabled?: boolean }
type ChannelPricingDraft = { listPrice: string; salePrice: string }
type ProductUpdateRequest = { connectionIds: string[]; includeProductInformation: boolean }
type AcceptedJob = { jobId: string }
type PublicationStatus = { productId: string; connectionId: string; profileId: string | null; desiredStatus: string | null; actualStatus: string | null; lastRejectionCode: string | null; lastJobId: string | null; lastJobStatus: string | null; lines: Array<{ variantId: string; sku: string; barcode: string | null; desiredStatus: string; actualStatus: string; rejectionCode: string | null }> }
type PublicationJobDetail = { job: { id: string; status: string; progressCurrent: number; progressTotal: number | null; progressPercent: number | null; progressLabel: string | null; lastErrorCode: string | null; lastErrorSummary: string | null } }
type ProductSyncJob = { id: string; connectionId: string | null; jobType: string; status: string; progressCurrent: number; progressTotal: number | null; progressPercent: number | null; progressLabel: string | null; progressReceived: number; progressProcessed: number; progressSkipped: number; progressFailed: number; createdAt: string; completedAt: string | null }

const key = () => crypto.randomUUID()
const isProductImportConnection = productImportConnection
const isProductUpdateConnection = (item: MarketplaceConnection) => ['TRENDYOL', 'HEPSIBURADA', 'SHOPIFY'].includes(item.platformCode.trim().toUpperCase()) && ['ACTIVE', 'VERIFIED'].includes(item.status.trim().toUpperCase())
const productPublicationMode = (item: MarketplaceConnection) => productPublicationChannelMode(item.platformCode, item.status, item.externalWritesEnabled)
const isProductPublicationConnection = (item: MarketplaceConnection) => productPublicationMode(item) === 'PUBLISH'
const isProductPublicationChannel = (item: MarketplaceConnection) => productPublicationMode(item) !== null
const productUpdateCapabilities = (platformCode: string) => {
  const code = platformCode.trim().toUpperCase()
  if (code === 'TRENDYOL') return { writable: true, content: true, categoryAttributes: true, detail: 'Ürün bilgileri, fiyat ve stok güncellenebilir.' }
  if (code === 'HEPSIBURADA') return { writable: true, content: true, categoryAttributes: true, detail: 'Ürün bilgileri, fiyat ve stok Hepsiburada yazma kurallarıyla güncellenebilir.' }
  if (code === 'SHOPIFY') return { writable: false, content: false, categoryAttributes: false, detail: 'Shopify dış yazmaları şu anda desteklenmiyor; bağlantı salt okunur.' }
  return { writable: false, content: false, categoryAttributes: false, detail: 'Bu platform için ürün güncelleme desteği bulunmuyor.' }
}

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
  return hubApi<CursorPage<Product>>(`/products?${params.toString()}`)
}
function ErrorBox({ error, onDismiss }: { error: unknown; onDismiss?: () => void }) {
  const message = error ? (error instanceof Error ? error.message : 'İşlem tamamlanamadı.') : ''
  const [hiddenMessage, setHiddenMessage] = useState('')
  useEffect(() => {
    if (!message) return
    appendNotification(message, 'error')
    const timeout = window.setTimeout(() => setHiddenMessage(message), 8000)
    return () => window.clearTimeout(timeout)
  }, [message])
  if (!message || hiddenMessage === message) return null
  return <NoticeToast kind="error" message={message} durationMs={8000} onClose={() => { setHiddenMessage(message); onDismiss?.() }} />
}

function VariantHeaderActionMenu({ anchorRef, children }: { anchorRef: { current: HTMLDivElement | null }; children: ReactNode }) {
  const menuRef = useRef<HTMLDivElement>(null)
  const [position, setPosition] = useState<{ top: number; left: number; visibility: 'hidden' | 'visible' }>({ top: 0, left: 0, visibility: 'hidden' })
  useLayoutEffect(() => {
    const place = () => {
      const anchor = anchorRef.current?.querySelector('button')
      const menu = menuRef.current
      if (!anchor || !menu) return
      const bounds = anchor.getBoundingClientRect()
      const menuBounds = menu.getBoundingClientRect()
      const left = Math.max(12, Math.min(window.innerWidth - menuBounds.width - 12, bounds.right - menuBounds.width))
      const below = bounds.bottom + 8
      const top = below + menuBounds.height <= window.innerHeight - 12 ? below : Math.max(12, bounds.top - menuBounds.height - 8)
      setPosition({ top, left, visibility: 'visible' })
    }
    place()
    window.addEventListener('resize', place)
    window.addEventListener('scroll', place, true)
    return () => {
      window.removeEventListener('resize', place)
      window.removeEventListener('scroll', place, true)
    }
  }, [anchorRef])
  return createPortal(<div ref={menuRef} className="variant-header-action-menu variant-header-action-menu-portal" role="menu" data-variant-header-popover="true" style={position}>{children}</div>, document.body)
}

// Phones: two small buttons under a picture move it one place earlier or later in the order (shown on phones only).
function MediaMoveButtons({ index, count, disabled = false, onMove }: { index: number; count: number; disabled?: boolean; onMove: (target: number) => void }) {
  return <span className="media-move-buttons"><button type="button" aria-label="Görseli bir öne al" disabled={disabled || index === 0} onClick={event => { event.stopPropagation(); onMove(index - 1) }}><UiIcon name="chevronLeft" /></button><button type="button" aria-label="Görseli bir sonraya al" disabled={disabled || index >= count - 1} onClick={event => { event.stopPropagation(); onMove(index + 1) }}><UiIcon name="chevronRight" /></button></span>
}

function LocalImagePreview({ file, alt, caption: _caption, onRemove, onZoom, draggable = false, isDragOver = false, onDragStart, onDragOver, onDrop, onDragEnd, mediaIndex, mediaCount, onMove }: { file: File, alt: string, caption: string, mediaIndex?: number, mediaCount?: number, onMove?: (target: number) => void, onRemove?: () => void, onZoom?: (url: string) => void, draggable?: boolean, isDragOver?: boolean, onDragStart?: (event: DragEvent<HTMLElement>) => void, onDragOver?: (event: DragEvent<HTMLElement>) => void, onDrop?: (event: DragEvent<HTMLElement>) => void, onDragEnd?: () => void }) {
  const [url, setUrl] = useState('');
  useEffect(() => {
    const objectUrl = URL.createObjectURL(file);
    setUrl(objectUrl);
    return () => URL.revokeObjectURL(objectUrl);
  }, [file]);
  return <figure data-media-kind={mediaIndex === undefined ? undefined : 'file'} data-media-index={mediaIndex} className={`image-preview-card${draggable ? ' media-sortable' : ''}${isDragOver ? ' is-media-drag-over' : ''}`} draggable={draggable} onDragStart={onDragStart} onDragOver={onDragOver} onDrop={onDrop} onDragEnd={onDragEnd}><img src={url} alt={alt} className="clickable-thumb" draggable={false} onClick={() => onZoom?.(url)} title={draggable ? 'Sıralamak için sürükleyin; büyütmek için tıklayın' : 'Büyütmek için tıklayın'} />{onRemove && <button type="button" className="image-remove-btn" title="Görseli sil" onClick={e => { e.stopPropagation(); onRemove(); }}><UiIcon name="close" /></button>}{onMove && mediaIndex !== undefined && mediaCount !== undefined && mediaCount > 1 && <MediaMoveButtons index={mediaIndex} count={mediaCount} onMove={onMove} />}</figure>;
}
function ImageLightboxModal({ image, onClose }: { image: { url: string; title: string }; onClose: () => void }) {
  return <RrImagePreview title={image.title} sources={[image.url]} onClose={onClose} />
}

type ProductMediaOption = { value: string; label: string; url?: string; file?: File }
type ProductFamilyMediaItem = NonNullable<Product['familyMediaItems']>[number]
type MediaRemovalConfirmation = { kind: 'product'; url: string } | { kind: 'family'; item: ProductFamilyMediaItem }
type PendingProductMediaFile = { id: string; file: File }
type VariantMediaGroup = { id: string; name: string; values: Array<{ id: string; value: string }>; attributeId?: string }
type VariantMediaModalState = { mode: 'variant'; rowKey: string; draftRefs: string[] } | { mode: 'bulk'; draftsBySelection: VariantMediaAssignmentDrafts; groupId: string; valueId: string }
type VariantFilterSelections = Record<string, string[]>
type ParsedVariantOption = { name: string; value: string }

function normalizeVariantOptionName(name: string) {
  return name.replace(/[\s_-]+/g, '').toLocaleUpperCase('tr-TR')
}

function isWebColorOptionName(name: string) {
  return ['WEBCOLOR', 'WEBCOLOUR', 'WEBRENK'].includes(normalizeVariantOptionName(name))
}

function isColorOptionName(name: string) {
  return ['RENK', 'RENKLER', 'COLOR', 'COLORS', 'COLOUR', 'COLOURS', 'WEBCOLOR', 'WEBCOLOUR', 'WEBRENK'].includes(normalizeVariantOptionName(name))
}

function isSizeOptionName(name: string) {
  return ['BEDEN', 'BEDENLER', 'SIZE', 'SIZES', 'BOYUT', 'BOYUTLAR', 'NUMARA', 'NUMARALAR', 'SHOESIZE', 'AYAKKABINUMARASI'].includes(normalizeVariantOptionName(name))
}

function formatCatalogOptionLabel(value: string) {
  const legacyColorLabels: Record<string, string> = {
    GRI: 'Gri',
    HAKI: 'Haki',
    KAHVERENGI: 'Kahverengi',
    LACIVERT: 'Lacivert',
    SIYAH: 'Siyah',
    YESIL: 'Yeşil',
    MAVI: 'Mavi',
    KIRMIZI: 'Kırmızı'
  }
  const key = value.trim().toLocaleUpperCase('tr-TR')
  return legacyColorLabels[key] ?? formatPanelColorValue(value)
}

function preferredColorOption(options: ParsedVariantOption[]) {
  return options.find(option => isColorOptionName(option.name) && !isWebColorOptionName(option.name))
    ?? options.find(option => isColorOptionName(option.name))
}

function displayVariantOptionSignature(signature: string, savedOptions: Record<string, string> = {}) {
  const options = displayVariantOptionEntriesFromSignature(signature, savedOptions)
  if (!options.length) return signature.trim().toLocaleLowerCase('tr-TR') === 'tek ürün' ? signature : '—'
  return joinVariantOptionLabels(options.map(({ name, value }) => `${formatCatalogOptionLabel(name)}: ${isSizeOptionName(name) ? value.toLocaleUpperCase('tr-TR') : formatCatalogOptionLabel(value)}`))
}

function variantOptionEntries(variant: Pick<Variant, 'optionSignature' | 'options'>) {
  const options = Object.entries(variant.options ?? {}).map(([name, value]) => ({ name, value: cleanOptionValue(value) }))
  return mergeVariantOptionEntries(options, parseVariantOptionSignature(variant.optionSignature ?? ''))
}

function optionSignatureFromOptions(options: Record<string, string>) {
  return Object.entries(options)
    .filter(([, value]) => value.trim())
    .map(([name, value]) => `${name}:${cleanOptionValue(value)}`)
    .join('_')
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

function cleanOptionValue(value: string) {
  return value.replace(/^["“”]+|["“”]+$/g, '').trim()
}

const standardSizeOrder = new Map([
  ['XXXS', 0], ['3XS', 0], ['XXS', 1], ['2XS', 1], ['XS', 2], ['S', 3], ['M', 4], ['L', 5], ['XL', 6],
  ['2XL', 7], ['3XL', 8], ['4XL', 9], ['5XL', 10], ['6XL', 11], ['7XL', 12], ['8XL', 13], ['9XL', 14]
])

function optionValueSortRank(attributeName: string, rawValue: string) {
  const name = attributeName.replace(/[\s-]+/g, '').toLocaleUpperCase('tr-TR')
  const value = cleanOptionValue(rawValue).toLocaleUpperCase('tr-TR').replace(/\s+/g, ' ').trim()
  const compactValue = value.replace(/\s+/g, '')
  if (!['BEDEN', 'SIZE', 'SIZ', 'NUMARA', 'NUMBER'].includes(name)) return { bucket: 0, primary: 0, secondary: 0, text: value }

  const ageRange = value.match(/^(\d+(?:[.,]\d+)?)\s*[-–]\s*(\d+(?:[.,]\d+)?)\s*AY$/)
  if (ageRange) return { bucket: 0, primary: Number(ageRange[1].replace(',', '.')), secondary: Number(ageRange[2].replace(',', '.')), text: value }
  const numericValue = value.match(/^\d+(?:[.,]\d+)?$/)
  if (numericValue) return { bucket: 1, primary: Number(value.replace(',', '.')), secondary: 0, text: value }
  const standardRank = standardSizeOrder.get(compactValue)
  if (standardRank !== undefined) return { bucket: 2, primary: standardRank, secondary: 0, text: value }
  return { bucket: 3, primary: 0, secondary: 0, text: value }
}

function sortOptionValues<T extends { id: string; value: string }>(attributeName: string, values: T[]) {
  return values
    .map((value, index) => ({ value, index, rank: optionValueSortRank(attributeName, value.value) }))
    .sort((left, right) => left.rank.bucket - right.rank.bucket
      || left.rank.primary - right.rank.primary
      || left.rank.secondary - right.rank.secondary
      || left.rank.text.localeCompare(right.rank.text, 'tr', { numeric: true, sensitivity: 'base' })
      || left.index - right.index)
    .map(item => item.value)
}

function variantSignatureKey(signature: string) {
  return parseVariantOptionSignature(signature)
    .map(option => `${option.name.replace(/\s+/g, '').toLocaleLowerCase('tr-TR')}:${cleanOptionValue(option.value).toLocaleLowerCase('tr-TR')}`)
    .sort()
    .join('|')
}

function isVariantOptionName(name: string) {
  const normalized = normalizeVariantOptionName(name)
  return ['RENK', 'COLOR', 'COLOUR', 'WEBCOLOR', 'WEBCOLOUR', 'WEBRENK', 'BEDEN', 'SIZE', 'SIZ', 'NUMARA', 'NUMBER'].includes(normalized)
}

function isOptionAttribute(attribute: Pick<Attribute, 'code' | 'name' | 'roles'>) {
  const normalizedName = normalizeVariantOptionName(attribute.name)
  return attribute.code.trim().toLocaleLowerCase('tr-TR').startsWith('option-')
    || attribute.roles?.some(role => role.toLocaleUpperCase('tr-TR') === 'OPTION') === true
    || isVariantOptionName(normalizedName)
}

function VariantImageIcon() {
  return <svg className="variant-media-icon" viewBox="0 0 24 24" focusable="false" aria-hidden="true"><rect x="3" y="4" width="18" height="16" rx="2" /><circle cx="8.5" cy="9" r="1.5" /><path d="m4 17 5-5 3 3 2-2 6 6" /></svg>
}

function VariantDragHandleIcon() {
  return <svg className="variant-drag-icon" viewBox="0 0 16 20" focusable="false" aria-hidden="true"><circle cx="5" cy="4" r="1.4" /><circle cx="11" cy="4" r="1.4" /><circle cx="5" cy="10" r="1.4" /><circle cx="11" cy="10" r="1.4" /><circle cx="5" cy="16" r="1.4" /><circle cx="11" cy="16" r="1.4" /></svg>
}

function BarcodeFillIcon() {
  return <svg className="variant-header-action-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" focusable="false" aria-hidden="true"><path d="M11 14h10" /><path d="M16 4h2a2 2 0 0 1 2 2v1.344" /><path d="m17 18 4-4-4-4" /><path d="M8 4H6a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h12a2 2 0 0 0 1.793-1.113" /><rect x="8" y="2" width="8" height="4" rx="1" /></svg>
}

function BarcodePasteIcon() {
  return <BarcodeFillIcon />
}

function MediaOptionThumb({ option, selected, onClick }: { option: ProductMediaOption; selected: boolean; onClick: () => void }) {
  const [src, setSrc] = useState(option.url ?? '')
  const [failed, setFailed] = useState(false)
  useEffect(() => {
    if (!option.file) return
    const objectUrl = URL.createObjectURL(option.file)
    setSrc(objectUrl)
    setFailed(false)
    return () => URL.revokeObjectURL(objectUrl)
  }, [option.file])
  return <button type="button" className={`variant-media-option ${selected ? 'selected' : ''}`} aria-label={`${option.label}${selected ? ' seçimini kaldır' : ''}`} aria-pressed={selected} onClick={onClick}>
    <span className="variant-media-option-image">{src && !failed ? <img src={src} alt="" onError={() => setFailed(true)} /> : <VariantImageIcon />}</span>
    <span className="variant-media-option-label" title={option.label}>{option.label}</span>
    <i aria-hidden="true">{selected ? <UiIcon name="check" /> : null}</i>
  </button>
}

function VariantMediaPickerModal({
  mode,
  options,
  selectedRefs,
  groups,
  selectedGroupId,
  selectedValueId,
  matchedVariantCount,
  matchedGroupVariantCount,
  onRefsChange,
  onGroupChange,
  onValueChange,
  onApply,
  onClose
}: {
  mode: 'variant' | 'bulk'
  options: ProductMediaOption[]
  selectedRefs: string[]
  groups?: VariantMediaGroup[]
  selectedGroupId?: string
  selectedValueId?: string
  matchedVariantCount?: number
  matchedGroupVariantCount?: number
  onRefsChange: (values: string[]) => void
  onGroupChange?: (value: string) => void
  onValueChange?: (value: string) => void
  onApply: () => void
  onClose: () => void
}) {
  const selectedGroup = groups?.find(group => group.id === selectedGroupId)
  const selectedValue = selectedGroup?.values.find(value => value.id === selectedValueId)
  return <div className="workspace-modal-backdrop variant-media-picker-backdrop" role="presentation" onMouseDown={onClose}>
    <section className={`workspace-modal variant-media-picker-modal${mode === 'bulk' ? ' is-bulk' : ''}`} role="dialog" aria-modal="true" aria-labelledby="variant-media-picker-title" onMouseDown={event => event.stopPropagation()}>
      <header>
        <div><p className="eyebrow">VARYANT GÖRSELLERİ</p><h2 id="variant-media-picker-title">{mode === 'bulk' ? 'Seçeneklere görsel ata' : 'Varyant görsellerini seç'}</h2><p>{mode === 'bulk' ? 'Bir seçenek değerine görselleri atayın. Diğer değerlere geçince seçimleriniz korunur; eşlemeler tek seferde kaydedilir.' : 'Ürün görsellerinden bu varyanta ait birden fazla görsel seçin. Sıra, ürün panelindeki görsel sırasına göre kaydedilir.'}</p></div>
        <button type="button" className="modal-close" onClick={onClose} aria-label="Pencereyi kapat"><UiIcon name="close" /></button>
      </header>
      {mode === 'bulk' && groups?.length ? <><div className="variant-media-bulk-fields"><label>Seçenek grubu<select value={selectedGroupId} onChange={event => onGroupChange?.(event.target.value)}><option value="">Seçenek grubu seçin</option>{groups.map(group => <option key={group.id} value={group.id}>{group.name}</option>)}</select></label><label>Seçenek değeri<select value={selectedValueId} onChange={event => onValueChange?.(event.target.value)} disabled={!selectedGroup}><option value="">Değer seçin</option>{selectedGroup?.values.map(value => <option key={value.id} value={value.id}>{value.value}</option>)}</select></label></div>{selectedValue && <div className={`variant-media-bulk-match-summary ${matchedVariantCount ? '' : 'is-empty'}`} role="status"><strong>{selectedGroup?.name}: {selectedValue.value}</strong><span>{matchedVariantCount ? `${matchedVariantCount} varyant eşleşti` : 'Bu değerle eşleşen varyant bulunamadı'}</span></div>}</> : null}
      <div className="variant-media-picker-grid">
        {options.length ? options.map(option => <MediaOptionThumb key={option.value} option={option} selected={selectedRefs.includes(option.value)} onClick={() => {
           const next = selectedRefs.includes(option.value)
             ? selectedRefs.filter(value => value !== option.value)
             : [...selectedRefs, option.value]
           onRefsChange(options.filter(item => next.includes(item.value)).map(item => item.value))
         }} />) : <div className="empty small"><strong>Seçilebilir görsel yok</strong><p>Önce ürün görsellerine HTTPS linki veya dosya ekleyin.</p></div>}
      </div>
      <footer><button type="button" className="secondary" onClick={() => onRefsChange([])}>{mode === 'bulk' ? 'Bu değerin görsellerini kaldır' : 'Görselleri kaldır'}</button><button type="button" className="secondary" onClick={onClose}>Vazgeç</button><button type="button" onClick={onApply} disabled={!options.length || (mode === 'bulk' && (!selectedGroupId || !selectedValueId || !matchedGroupVariantCount))}>{mode === 'bulk' ? 'Tüm eşlemeleri kaydet' : 'Görselleri kaydet'}</button></footer>
    </section>
  </div>
}
const Tag = ({ children }: { children: ReactNode }) => <span className="tag">{children}</span>
const money = (value: number | null | undefined, currency = 'TRY') => value == null ? '—' : new Intl.NumberFormat('tr-TR', { style: 'currency', currency }).format(value)
function Page({ title, eyebrow, action, className, children }: { title: string; eyebrow: string; action?: ReactNode; className?: string; children: ReactNode }) { const productBack = className?.includes('product-add-page'); return <section className={`content stitch-page ${className ?? ''}`}><div className={`page-heading${productBack ? ' product-page-heading' : ''}`}><div className={productBack ? 'product-page-heading-copy' : undefined}>{productBack && <Link className="product-heading-back" to="/products" aria-label="Ürünler listesine dön"><UiIcon name="arrowLeft" /><span>Ürünler</span></Link>}<div className={productBack ? 'product-page-heading-title' : undefined}><p className="eyebrow">{eyebrow}</p><h1>{title}</h1></div></div>{action}</div>{children}</section> }

type QuickEditMode = 'stock' | 'price' | 'both'

function ProductQuickEditModal({ products, connections, mode = 'both', onChanged, onClose, onResult }: { products: Product[]; connections: MarketplaceConnection[]; mode?: QuickEditMode; onChanged: () => Promise<unknown>; onClose: () => void; onResult?: (message: string, kind: 'success' | 'error') => void }) {
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
    if (!selectionDraft.length) { const message = 'Önce en az bir varyant seçin.'; setNotice(message); onResult?.(message, 'error'); return }
    const targetSelection = selectionDraft
    const targetSelectionSet = new Set(targetSelection)
    const priceRequested = mode !== 'stock' && (listPrice !== '' || salePrice !== ''); const stockRequested = stockAmount !== ''
    if (!priceRequested && !stockRequested) { const message = 'Uygulanacak fiyat veya stok değerini girin.'; setNotice(message); onResult?.(message, 'error'); return }
    const list = listPrice === '' ? null : Number(listPrice); const sale = salePrice === '' ? null : Number(salePrice); const amount = stockAmount === '' ? null : Number(stockAmount)
    if ((list != null && (!Number.isFinite(list) || list < 0)) || (sale != null && (!Number.isFinite(sale) || sale < 0)) || (list != null && sale != null && list < sale) || (amount != null && (!Number.isFinite(amount) || amount < 0))) { const message = 'Değerleri kontrol edin; negatif fiyat/stok veya hatalı fiyat sıralaması kullanılamaz.'; setNotice(message); onResult?.(message, 'error'); return }
    setSaving(true); setNotice('Seçilen varyantlar güncelleniyor…')
    try {
      for (const item of sortedVariants.filter(value => targetSelectionSet.has(value.variant.id))) {
        const variant = item.variant
        if (priceRequested) {
          const connectionId = variant.offerId ? '' : connections[0]?.id ?? ''; const nextSale = sale ?? variant.salePrice ?? variant.listPrice ?? 0; const nextList = list ?? variant.listPrice ?? nextSale
          if (nextList < nextSale) throw new Error(`${variant.sku}: liste fiyatı satış fiyatından küçük olamaz.`)
          const body = { connectionId, variantId: variant.id, listPrice: nextList, salePrice: nextSale, currency: variant.currency || 'TRY', vatRate: variant.vatRate ?? 10, vatInclusion: variant.vatInclusion || 'INCLUDED', roundingMode: variant.roundingMode || 'HALF_EVEN', safetyStock: variant.safetyStock ?? 0, status: variant.offerStatus || 'ACTIVE', reason: 'Toplu ürün fiyat düzenleme' }
          if (variant.offerId) { if (variant.offerVersion == null) throw new Error(`${variant.sku}: fiyat sürümü eksik.`); await hubApi(`/channel-offers/${variant.offerId}`, { method: 'PATCH', headers: { 'If-Match': `"v${variant.offerVersion}"` }, body: JSON.stringify(body) }) }
          else { if (!connectionId) throw new Error('İlk fiyat için aktif platform bağlantısı bulunamadı.'); await hubApi('/channel-offers', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify(body) }) }
        }
        if (stockRequested && amount != null) {
          const target = stockAction === 'SET' ? amount : stockAction === 'ADD' ? variant.onHand + amount : variant.onHand - amount
          if (target < 0) throw new Error(`${variant.sku}: stok sıfırın altına inemez.`)
          const delta = target - variant.onHand
          if (delta !== 0) await hubApi(`/inventory/${variant.id}/adjustments`, { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ quantityDelta: delta, reason: 'Toplu ürün stok düzenleme', sourceEventId: key() }) })
        }
      }
      await onChanged(); onResult?.(`${targetSelection.length} varyant başarıyla güncellendi.`, 'success'); onClose()
    } catch (error) { const message = error instanceof Error ? error.message : 'Toplu düzenleme tamamlanamadı.'; setNotice(`Başarısız: ${message}`); onResult?.(message, 'error') } finally { setSaving(false) }
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
          return <div className={`rr-vgroup${isOpen ? ' rr-open' : ''}`} key={color}><div className="rr-vg-h"><input type="checkbox" className="rr-cb" checked={groupSelection.checked} data-mixed={groupSelection.indeterminate ? '' : undefined} aria-label={`${groupLabel} rengindeki tüm varyantları seç`} onChange={event => setSelectionDraft(current => updateVariantGroupSelection(current, items.map(item => item.variant.id), event.currentTarget.checked))} /><button type="button" className="rr-vg-t" aria-expanded={isOpen} onClick={() => toggleColorGroup(color)}><b>{groupLabel}</b><small>{selectedCount ? `${selectedCount}/${items.length} seçili` : `${items.length} varyant`}</small><RrIcon name="chev" /></button></div>{isOpen && items.map(item => { const parts = variantOptionParts(item.variant.optionSignature); return <div className={`rr-vline${selectedSet.has(item.variant.id) ? '' : ' rr-off'}`} key={item.variant.id}><input type="checkbox" className="rr-cb" checked={selectedSet.has(item.variant.id)} onChange={() => toggle(item.variant.id)} aria-label={`${item.variant.optionSignature || item.variant.sku} varyantını seç`} /><span><b>{parts.length ? parts.map(part => part.value).join(' · ') : 'Varyant'}</b><small>Stok Kodu: {item.variant.sku}</small></span><QuickEditVariantControls variant={item.variant} connections={connections} onChanged={onChanged} showPrice={mode !== 'stock'} onSelect={() => setSelectionDraft(current => current.includes(item.variant.id) ? current : [...current, item.variant.id])} /></div> })}</div>
        })}
      </section>
      <section className="rr-step"><header><span className="rr-stepn">2</span><div className="rr-h3">{mode === 'stock' ? 'Stok Değerini Düzenle' : mode === 'price' ? 'Fiyat Değerini Düzenle' : 'Fiyat ve Stok Değerini Düzenle'}</div><small className="rr-lead">{activeSelection.length ? `${activeSelection.length} seçili varyanta uygulanacak` : 'Varyant seçimi bekleniyor'}</small></header>
        {activeSelection.length ? <div className="rr-chips">{variants.filter(item => activeSelectedSet.has(item.variant.id)).map(item => <span key={item.variant.id} className="rr-chip">{variantOptionParts(item.variant.optionSignature).map(part => part.value).join(' · ') || item.variant.sku}</span>)}</div> : <p className="rr-lead">Değeri şimdi girebilirsiniz; uygulamak için en az bir varyantı işaretleyin.</p>}
        <div className="rr-dgrid">{mode !== 'stock' && <><label className="rr-fld"><small>Liste Fiyatı</small><input className="rr-in rr-num" type="number" min="0" step="0.01" value={listPrice} onChange={event => setListPrice(event.target.value)} placeholder="Değiştirme" /></label><label className="rr-fld"><small>Satış Fiyatı</small><input className="rr-in rr-num" type="number" min="0" step="0.01" value={salePrice} onChange={event => setSalePrice(event.target.value)} placeholder="Değiştirme" /></label></>}{mode !== 'price' && <><label className="rr-fld"><small>Stok İşlemi</small><select className="rr-in" value={stockAction} onChange={event => setStockAction(event.target.value as typeof stockAction)}><option value="SET">Bu Sayıya Eşitle</option><option value="ADD">Bu Kadar Ekle (+)</option><option value="SUBTRACT">Bu Kadar Çıkar (−)</option></select></label><label className="rr-fld"><small>Miktar</small><input className="rr-in rr-num" type="number" min="0" step="1" value={stockAmount} onChange={event => setStockAmount(event.target.value)} placeholder="Miktar" /></label></>}</div>
      </section>
      {notice && <div className={`rr-callout${notice.startsWith('Başarısız:') ? ' rr-danger' : ''}`} role={notice.startsWith('Başarısız:') ? 'alert' : 'status'}><RrIcon name={notice.startsWith('Başarısız:') ? 'alert' : 'info'} /><span>{notice}</span></div>}
    </form>
  </RrModal>
}

function QuickEditVariantControls({ variant, connections, onChanged, onSelect, showPrice = true }: { variant: Variant; connections: MarketplaceConnection[]; onChanged: () => Promise<unknown>; onSelect: () => void; showPrice?: boolean }) {
  const [price, setPrice] = useState(variant.salePrice ?? variant.listPrice ?? '')
  const [stock, setStock] = useState(variant.onHand)
  const [savingPrice, setSavingPrice] = useState(false); const [savingStock, setSavingStock] = useState(false)
  async function savePrice() {
    const salePrice = Number(price); if (savingPrice || price === '' || !Number.isFinite(salePrice) || salePrice < 0 || salePrice === (variant.salePrice ?? variant.listPrice ?? 0)) return
    const connectionId = variant.offerId ? '' : connections[0]?.id ?? ''; const listPrice = Math.max(variant.listPrice ?? salePrice, salePrice)
    if (!variant.offerId && !connectionId) return
    const body = { connectionId, variantId: variant.id, listPrice, salePrice, currency: variant.currency || 'TRY', vatRate: variant.vatRate ?? 10, vatInclusion: variant.vatInclusion || 'INCLUDED', roundingMode: variant.roundingMode || 'HALF_EVEN', safetyStock: variant.safetyStock ?? 0, status: variant.offerStatus || 'ACTIVE', reason: 'Hızlı varyant fiyat düzenleme' }
    setSavingPrice(true)
    try { if (variant.offerId) { if (variant.offerVersion == null) return; await hubApi(`/channel-offers/${variant.offerId}`, { method: 'PATCH', headers: { 'If-Match': `\"v${variant.offerVersion}\"` }, body: JSON.stringify(body) }) } else await hubApi('/channel-offers', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify(body) }); await onChanged() } finally { setSavingPrice(false) }
  }
  async function saveStock() {
    const target = Number(stock); const delta = target - variant.onHand; if (savingStock || !Number.isFinite(target) || target < 0 || delta === 0) return
    setSavingStock(true)
    try { await hubApi(`/inventory/${variant.id}/adjustments`, { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ quantityDelta: delta, reason: 'Hızlı varyant stok düzenleme', sourceEventId: key() }) }); await onChanged() } finally { setSavingStock(false) }
  }
  return <><label className="rr-mini"><small>Stok</small><input className="rr-in rr-num" aria-label={`${variant.sku} stok`} value={stock} onFocus={onSelect} onChange={event => { onSelect(); setStock(Number(event.target.value || 0)) }} onBlur={() => void saveStock()} type="number" min="0" step="1" disabled={savingStock} /></label>{showPrice && <label className="rr-mini"><small>Fiyat</small><input className="rr-in rr-num" aria-label={`${variant.sku} fiyat`} value={price} onFocus={onSelect} onChange={event => { onSelect(); setPrice(event.target.value === '' ? '' : Number(event.target.value)) }} onBlur={() => void savePrice()} type="number" min="0" step="0.01" disabled={savingPrice} /></label>}</>
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
  const parts = variantOptionParts(variant.optionSignature).filter(part => !isColorOptionName(part.name))
  if (parts.length) return parts.map(part => `${part.name}: ${part.value}`).join(' · ')
  // Imported variants may carry every product attribute; only the variant-defining ones (size) name the variant.
  const sizes = variantOptionEntries(variant).filter(option => isVariantOptionName(option.name) && !isColorOptionName(option.name))
  return sizes.length ? sizes.map(option => `${formatCatalogOptionLabel(option.name)}: ${option.value}`).join(' · ') : variant.sku
}

// On touch screens a window does not focus its first field by itself: that would open the keyboard before the person
// has chosen what to change.
const touchScreen = typeof window !== 'undefined' && window.matchMedia('(pointer: coarse)').matches

// The element that scrolls the page around a node: its nearest scrolling ancestor, or the document.
function scrollingParent(node: Element) {
  for (let current = node.parentElement; current; current = current.parentElement) {
    const overflow = getComputedStyle(current).overflowY
    if ((overflow === 'auto' || overflow === 'scroll') && current.scrollHeight > current.clientHeight) return current
  }
  return document.scrollingElement ?? document.documentElement
}

function useMediaQuery(query: string) {
  const [matches, setMatches] = useState(() => window.matchMedia(query).matches)
  useEffect(() => {
    const list = window.matchMedia(query)
    const update = () => setMatches(list.matches)
    list.addEventListener('change', update)
    window.addEventListener('resize', update)
    return () => { list.removeEventListener('change', update); window.removeEventListener('resize', update) }
  }, [query])
  return matches
}

function lowStockMissingVariantCount(group: ProductGroup) {
  return group.variants.filter(item => item.variant.onHand <= 0).length
}

function LowStockDetailsModal({ products, loading, error, onClose, onImageClick }: { products: Product[]; loading: boolean; error: unknown; onClose: () => void; onImageClick: (url: string, title: string) => void }) {
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
        {open && <section>{[...group.products].sort((left, right) => lowStockProductColor(left).localeCompare(lowStockProductColor(right), 'tr', { sensitivity: 'base' })).map(product => { const lowVariants = product.variants.filter(variant => variant.onHand <= 0).sort((left, right) => lowStockSizeLabel(left).localeCompare(lowStockSizeLabel(right), 'tr', { numeric: true, sensitivity: 'base' })); const colorOpen = openColors.has(product.id); return <div className={`rr-lowc${colorOpen ? ' rr-open' : ''}`} key={product.id}><button type="button" className="rr-lowc-h" aria-expanded={colorOpen} onClick={() => setOpenColors(current => { const next = new Set(current); if (next.has(product.id)) next.delete(product.id); else next.add(product.id); return next })}><b>{lowStockProductColor(product)}</b><span className="rr-lowc-n">{lowVariants.length} eksik varyant</span><RrIcon name="chev" /></button>{colorOpen && (lowVariants.length ? <div className="rr-lowvs">{lowVariants.map(variant => <div className="rr-lowv" key={variant.id}><span><b>{lowStockSizeLabel(variant)}</b><small>SKU: {variant.sku}{variant.barcode ? ` · Barkod: ${variant.barcode}` : ''}</small></span><span className={variant.onHand === 0 ? 'rr-zero' : 'rr-low'}>{variant.onHand === 0 ? 'Stoksuz' : <>Kalan <b className="rr-num">{variant.onHand}</b></>}</span></div>)}</div> : <p className="rr-lead">Bu renkte kritik stok varyantı bulunamadı.</p>)}</div> })}</section>}
      </article> })}</div>
    </>}
  </RrModal>
}

function ProductColorRows({ group, selected, onSelect, selectionMode: _selectionMode, onEnterSelection: _onEnterSelection, onQuickEdit, onImageClick, onDelete, onDuplicate, onSetStatus }: { group: ProductGroup; selected: boolean; onSelect: () => void; selectionMode: boolean; onEnterSelection: () => void; onQuickEdit: (mode: QuickEditMode) => void; onImageClick: (url: string, title: string) => void; onDelete: () => void; onDuplicate: () => void; onSetStatus: (status: 'ACTIVE' | 'ARCHIVED') => void }) {
  const navigate = useNavigate()
  const product = group.primary
  const platformStatuses = group.products.flatMap(item => item.platformStatuses ?? [])
  const platformStatusAggregates = new Map<string, { platform: string; platformCode: string; statuses: string[]; matchedVariantCount: number; variantCount: number; isChecking: boolean }>()
  for (const item of platformStatuses) {
    const platform = item.platform?.trim() || 'Platform'
    const platformCode = item.platformCode?.trim().toUpperCase() || (platform.toUpperCase().includes('SHOPIFY') ? 'SHOPIFY' : platform.toUpperCase().includes('HEPSIBURADA') ? 'HEPSIBURADA' : platform.toUpperCase().includes('TRENDYOL') ? 'TRENDYOL' : platform.toUpperCase().includes('N11') ? 'N11' : platform.toUpperCase().includes('PAZARAMA') ? 'PAZARAMA' : 'UNKNOWN')
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
  // connected there, yellow when only some are (or some failed), red when all failed and cycles while it updates.
  const platformMarks = (['TRENDYOL', 'HEPSIBURADA', 'N11', 'PAZARAMA', 'SHOPIFY'] as const).map(code => {
    const card = platformCards.find(item => item.platformCode === code)
    const name = code === 'N11' ? 'N11' : code.charAt(0) + code.slice(1).toLocaleLowerCase('tr-TR')
    const linkedTone = card && card.variantCount > 0 && card.matchedVariantCount < card.variantCount ? 'part' : 'on'
    const state = card ? ({ active: 'on', partial: 'part', linked: linkedTone, processing: 'busy', error: 'err', inactive: 'none' } as Record<string, string>)[card.state] ?? 'none' : 'none'
    return { code, state, label: card?.label ?? `${name} ürün eşleşmesi bulunamadı` }
  })
  const [ringTone, ringProgress] = ({ ACTIVE: ['ok', 1], DRAFT: ['prog', 0.3], ARCHIVED: ['off', 0], MIXED: ['bad', 0.66] } as Record<string, [RingTone, number]>)[status] ?? ['off', 0]
  const statusLabelText = status === 'MIXED' ? 'Hatalı' : titleTr(statusLabel)
  const menuItems: Array<['edit' | 'price' | 'dup' | 'on' | 'off' | 'trash', string, () => void, string?] | 'hr'> = [['edit', 'Ürünü Düzenle', () => navigate(`/products/${product.id}`)], ['price', 'Fiyat ve Stok Düzenle', () => onQuickEdit('both')], ['dup', 'Yeni Ürün Olarak Düzenle', onDuplicate], 'hr', status === 'ACTIVE' ? ['off', 'Satışa Kapat', () => onSetStatus('ARCHIVED')] : ['on', 'Satışa Aç', () => onSetStatus('ACTIVE')], ['trash', group.products.length > 1 ? 'Ürün Grubunu Sil' : 'Ürünü Sil', onDelete, 'rr-danger']]
  // Phones select a card only through its round checkbox; a hold or a tap elsewhere on the card no longer selects it.
  return <tr className={selected ? 'rr-sel' : undefined}>
    <td className="rr-wide"><div className="rr-prodcell"><input type="checkbox" className="rr-cb" aria-label={`${product.title} ürün grubunu seç`} checked={selected} onChange={onSelect} /><RrThumb src={product.primaryImageUrl} alt={product.title} onOpen={product.primaryImageUrl ? () => onImageClick(product.primaryImageUrl!, product.title) : undefined} /><div><b title={product.title}>{product.title}</b><small><span>Model Kodu:</span> <b className="rr-num">{modelCode}</b></small></div></div></td>
    <td className="rr-mid" data-l="Varyant"><span className={variantDisplayGroups.length > 0 ? 'rr-vhover' : 'rr-vhover rr-vhover-none'} tabIndex={variantDisplayGroups.length > 0 ? 0 : undefined}><span className="rr-varcell"><b>{group.products.length} seçenek</b><small>{group.variants.length} varyant</small></span>{variantDisplayGroups.length > 0 && <span className="rr-tip rr-vtip" role="tooltip"><table className="rr-vtab"><thead><tr><th />{variantSizes.map(size => <th key={size}>{size}</th>)}</tr></thead><tbody>{variantDisplayGroups.map(item => <tr key={item.label}><th title={item.label}>{item.label}</th>{variantSizes.map(size => { const value = item.values.find(entry => entry.label === size); return <td key={size} className={value == null ? 'rr-none' : value.quantity === 0 ? 'rr-empty' : value.quantity < 5 ? 'rr-low' : undefined}>{value == null ? '—' : value.quantity}</td> })}</tr>)}</tbody></table></span>}</span></td>
    <td className="rr-mid" data-l="Fiyat"><button type="button" className="rr-qbtn" title="Fiyatı hızlı düzenle" aria-label={`${product.title}: fiyatı düzenle`} onClick={() => onQuickEdit('price')}><b className="rr-num">{money(startingPrice ?? 0, product.currency)}</b>{listPrice != null && <small className="rr-num">{money(listPrice, product.currency)}</small>}</button></td>
    <td className="rr-mid" data-l="Stok"><button type="button" className={`rr-qbtn${totalStock === 0 ? ' rr-zero' : ''}`} title="Stoğu hızlı düzenle" aria-label={`${product.title}: stoğu düzenle`} onClick={() => onQuickEdit('stock')}><b className="rr-num">{totalStock}</b><small>{totalStock === 0 ? 'Stoksuz' : 'adet'}</small></button></td>
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
    <p className="rr-lead">Devam ederseniz bu kayıtların varyant, stok, fiyat ve platform eşleşmeleri silinir.</p>
  </RrModal>
}

export function ProductsPage() {
  const [trackedProductImportJobIds, setTrackedProductImportJobIds] = useState<string[]>([])
  const client = useQueryClient(); const navigate = useNavigate(); const [search, setSearch] = useState(''); const [searchFilter, setSearchFilter] = useState(''); const [status, setStatus] = useState(''); const [platform, setPlatform] = useState(''); const [stock, setStock] = useState(''); const [selectedProductIds, setSelectedProductIds] = useState<string[]>([]); const [selectedProductCache, setSelectedProductCache] = useState<Record<string, Product>>({}); const [allProductsSelected, setAllProductsSelected] = useState(false); const [selectingAllProducts, setSelectingAllProducts] = useState(false); const [quickEdit, setQuickEdit] = useState<{ productIds: string[]; mode: QuickEditMode } | null>(null); const [productToast, setProductToast] = useState<{ message: string; kind: 'success' | 'error' | 'info' } | null>(null); const [statusRequest, setStatusRequest] = useState<{ productIds: string[]; status: 'ACTIVE' | 'ARCHIVED' } | null>(null); const [jumpPage, setJumpPage] = useState(''); const [deleteRequest, setDeleteRequest] = useState<ProductDeleteRequest | null>(null); const [deletingProducts, setDeletingProducts] = useState(false); const [productImportOpen, setProductImportOpen] = useState(false); const [productImportMethod, setProductImportMethod] = useState<ProductImportMethod>('BULK'); const [productImportConnectionIds, setProductImportConnectionIds] = useState<string[]>([]); const [productImportMode, setProductImportMode] = useState<ProductImportMode>('FULL'); const [productImportLookup, setProductImportLookup] = useState(''); const [productImportIncludeArchived, setProductImportIncludeArchived] = useState(true); const [productImportUpdateExistingProducts, setProductImportUpdateExistingProducts] = useState(true); const [productImportUpdateExistingPricesAndStock, setProductImportUpdateExistingPricesAndStock] = useState(false); const [productImporting, setProductImporting] = useState(false); const [lightboxImage, setLightboxImage] = useState<{ url: string; title: string } | null>(null); const [lowStockOpen, setLowStockOpen] = useState(false); const [pageSize, setPageSize] = useState(20); const [pageNumber, setPageNumber] = useState(1); const [pageCursors, setPageCursors] = useState<Record<string, Record<number, string | null>>>({})
  const [pageJumping, setPageJumping] = useState(false)
  const [catalogSort, setCatalogSort] = useState<ProductCatalogSort>(null)
  const [productImportIncludePendingApproval, setProductImportIncludePendingApproval] = useState(false)
  const [expandedPlatformGroup, setExpandedPlatformGroup] = useState<string | null>(null)
  const productFilters = useMemo<ProductListFilters>(() => ({ search: searchFilter, status, platform, stock }), [searchFilter, status, platform, stock])
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
  const draftCountQuery = useQuery({ queryKey: ['products', 'count', 'DRAFT'], queryFn: () => fetchProductPage(1, { search: '', status: 'DRAFT', platform: '', stock: '' }, null), staleTime: 60_000 })
  const archivedCountQuery = useQuery({ queryKey: ['products', 'count', 'ARCHIVED'], queryFn: () => fetchProductPage(1, { search: '', status: 'ARCHIVED', platform: '', stock: '' }, null), staleTime: 60_000 })
  const lowStockQuery = useQuery({ queryKey: ['products', 'low-stock-details'], queryFn: () => loadAllPages<Product>('/products?stock=LOW', 200), enabled: lowStockOpen, staleTime: 15_000, refetchOnWindowFocus: true })
  const connectionsQuery = useQuery({ queryKey: ['connections', 'product-price'], queryFn: () => loadAllPages<MarketplaceConnection>('/connections') })
  const productSyncJobsQuery = useQuery({ queryKey: ['jobs', 'product-import'], queryFn: () => hubApi<ProductSyncJob[]>(productSyncJobsPath, { cache: 'no-store' }), enabled: productImportOpen, refetchInterval: query => productImportOpen && filterActiveProductSyncJobs(query.state.data ?? []).length ? 1500 : false, refetchIntervalInBackground: true, refetchOnWindowFocus: true, staleTime: 0 })
  const products = query.data?.items ?? []; const connections = (connectionsQuery.data?.items ?? []).filter(isProductImportConnection); const publicationConnections = (connectionsQuery.data?.items ?? []).filter(isProductPublicationConnection); const selectedImportPlatforms = productImportConnectionIds.map(connectionId => connections.find(connection => connection.id === connectionId)?.platformCode ?? '').filter(Boolean); const selectedImportHasShopify = selectedImportPlatforms.some(code => code.trim().toUpperCase() === 'SHOPIFY'); const selectedImportHasHepsiburada = selectedImportPlatforms.some(code => code.trim().toUpperCase() === 'HEPSIBURADA'); const selectedImportOnlyHepsiburada = selectedImportPlatforms.length > 0 && selectedImportPlatforms.every(code => code.trim().toUpperCase() === 'HEPSIBURADA'); const singleLookupLabel = singleProductLookupLabel(selectedImportPlatforms[0] ?? ''); const platforms = summaryQuery.data?.platforms ?? []
  const selectedImportHasTrendyol = selectedImportPlatforms.some(code => code.trim().toUpperCase() === 'TRENDYOL')
  const productImportSupportsPendingApproval = productImportMethod === 'BULK' && selectedImportHasTrendyol && (productImportMode === 'FULL' || productImportMode === 'NEW_ONLY' || productImportMode === 'MAPPING_ONLY')
  const productImportOptions = getProductImportOptionVisibility({ method: productImportMethod, mode: productImportMode, onlyHepsiburada: selectedImportOnlyHepsiburada, hasHepsiburada: selectedImportHasHepsiburada, supportsPendingApproval: productImportSupportsPendingApproval })
  useEffect(() => { if (selectedImportOnlyHepsiburada && productImportMode === 'EXISTING_ONLY') setProductImportMode('MAPPING_ONLY') }, [selectedImportOnlyHepsiburada, productImportMode])
  const selectedPlatformFilter = productPlatformFilterGroups.flatMap(group => group.options.map(option => ({ ...option, group: group.label }))).find(option => option.value === platform)
  const selectedPlatformLabel = selectedPlatformFilter ? `${selectedPlatformFilter.group} · ${selectedPlatformFilter.label}` : platform || 'Tüm Platformlar'
  const totalCount = query.data?.totalCount ?? products.length; const totalPages = Math.max(1, Math.ceil(totalCount / pageSize)); const currentPage = Math.min(pageNumber, totalPages); const pageProducts = currentPage === pageNumber ? products : []; const pageProductGroups = useMemo(() => sortProductGroups(productRowsAsCards(pageProducts), catalogSort), [pageProducts, catalogSort])
  const activeProductSyncJobs = useMemo(() => filterActiveProductSyncJobs(productSyncJobsQuery.data ?? []), [productSyncJobsQuery.data])
  const cancelProductSync = useMutation({ mutationFn: (jobId: string) => hubApi(`/jobs/${jobId}/cancel`, { method: 'POST', headers: { 'Idempotency-Key': `cancel-product-import:${jobId}` } }), onSuccess: () => { void productSyncJobsQuery.refetch(); void client.invalidateQueries({ queryKey: ['jobs'] }) } })
  const quickEditProducts = quickEdit
    ? quickEdit.productIds.map(id => selectedProductCache[id] ?? products.find(product => product.id === id)).filter((product): product is Product => Boolean(product))
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
  useEffect(() => { setPageNumber(1); setPageCursors({}); setSelectedProductIds([]); setSelectedProductCache({}); setAllProductsSelected(false) }, [productFilterKey, pageSize])
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
  function showProductToast(message: string, kind: 'success' | 'error' | 'info') { setProductToast({ message, kind }) }
  useEffect(() => {
    if (!productToast) return
    appendNotification(productToast.message, productToast.kind)
    const timeout = window.setTimeout(() => setProductToast(current => current === productToast ? null : current), 4000)
    return () => window.clearTimeout(timeout)
  }, [productToast])
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
    if (!productImportConnectionIds.length) { showProductToast('Aktarım için en az bir aktif pazar yeri bağlantısı seçin.', 'error'); return }
    if (productImportMethod === 'SINGLE' && productImportConnectionIds.length !== 1) { showProductToast('Tek ürün aktarımında yalnızca bir bağlantı seçilebilir.', 'error'); return }
    if (productImportMethod === 'SINGLE' && !productImportLookup.trim()) { showProductToast(`Tek ürün aktarımı için ${singleLookupLabel} girin.`, 'error'); return }
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
        return hubApi<AcceptedJob>(`/connections/${connectionId}/product-sync-jobs?${query.toString()}`, { method: 'POST', headers: { 'Idempotency-Key': key() }, body: '{}' })
      }))
      setTrackedProductImportJobIds(queuedProductSyncJobs.map(job => job.jobId))
      const modeLabel = productImportMethod === 'SINGLE' ? 'tek ürün' : full ? 'tam katalog' : productImportMode === 'NEW_ONLY' ? 'yeni ürün' : productImportMode === 'MAPPING_ONLY' ? 'ürün eşleme' : 'mevcut ürün güncelleme'
      const contentLabel = productImportMethod === 'BULK' && (full || existingOnly) ? selectedImportHasHepsiburada
        ? selectedImportOnlyHepsiburada ? ' · Hepsiburada ürün içeriği korunacak' : productImportUpdateExistingProducts ? ' · Hepsiburada içeriği korunacak, desteklenen platform içerikleri güncellenecek' : ' · mevcut ürün içerikleri korunacak'
        : productImportUpdateExistingProducts ? ' · mevcut ürün bilgileri güncellenecek' : ' · mevcut ürün bilgileri korunacak' : ''
       const priceStockLabel = productImportMethod === 'BULK' && existingOnly && !selectedImportOnlyHepsiburada ? productImportUpdateExistingPricesAndStock ? ' · mevcut fiyat ve stoklar güncellenecek' : ' · mevcut fiyat ve stoklar korunacak' : ''
       showProductToast(`${productImportConnectionIds.length} bağlantı için ${modeLabel} aktarımı kuyruğa alındı${productImportIncludeArchived ? selectedImportHasShopify ? ' · arşiv ve taslak ürünler dahil' : ' · arşiv ürünleri dahil' : ''}${includePendingApproval ? ' · onay bekleyen ürünler dahil' : ''}${contentLabel}${priceStockLabel}.`, 'info')
      void productSyncJobsQuery.refetch()
      void client.invalidateQueries({ queryKey: ['jobs'] })
    } catch (err) {
      showProductToast(err instanceof Error ? err.message : 'Platform ürünleri panele alınamadı.', 'error')
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
      showProductToast(`${productRowsAsCards(all.items).length} ürün kartı · ${ids.length} katalog kaydı tüm sayfalardan seçildi.`, 'success')
    } catch (err) {
      showProductToast(err instanceof Error ? err.message : 'Tüm sayfalardaki ürünler seçilemedi.', 'error')
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
      showProductToast(err instanceof Error ? err.message : 'İstenen ürün sayfası yüklenemedi.', 'error')
    } finally {
      setPageJumping(false)
    }
  }

  async function bulkSetProductStatus(newStatus: 'ACTIVE' | 'ARCHIVED', targetIds: string[]) {
    const targetCount = targetIds.length
    if (!targetCount) return
    try {
      for (let index = 0; index < targetIds.length; index += 500) {
        const productIds = targetIds.slice(index, index + 500)
        await hubApi('/products/bulk-status', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ productIds, status: newStatus }) })
      }
      showProductToast(`${targetCount} ürün durumu “${productStatusLabel(newStatus)}” olarak güncellendi.`, 'success')
      setSelectedProductIds([])
      setSelectedProductCache({})
      setAllProductsSelected(false)
      await refresh()
    } catch (err) {
      showProductToast(err instanceof Error ? err.message : 'Toplu durum güncelleme başarısız.', 'error')
    }
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
        await hubApi(`/products/${request.product.id}`, { method: 'DELETE', headers: { 'If-Match': `\"v${request.product.version}\"`, 'Idempotency-Key': key() } })
      } else {
        for (let index = 0; index < request.productIds.length; index += 500) {
          const productIds = request.productIds.slice(index, index + 500)
          await hubApi('/products/bulk-delete', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ productIds }) })
        }
      }
      const targetIds = new Set(request.productIds)
      setSelectedProductIds(current => current.filter(id => !targetIds.has(id)))
      setSelectedProductCache(current => { const next = { ...current }; targetIds.forEach(id => delete next[id]); return next })
      setAllProductsSelected(false)
      setDeleteRequest(null)
      showProductToast(request.productIds.length === 1 ? 'Ürün yerel katalogdan silindi.' : `${request.productIds.length} katalog kaydı silindi.`, 'success')
      await refresh()
    } catch (err) {
      showProductToast(err instanceof Error ? err.message : 'Ürün silme başarısız.', 'error')
    } finally {
      setDeletingProducts(false)
    }
  }

  function bulkDeleteProducts() {
    const ids = [...selectedProductIds]
    if (!ids.length) return
    setDeleteRequest({ productIds: ids, title: `${selectedProductCardCount} ürün kartı`, description: `${ids.length} katalog kaydı seçildi. Sipariş geçmişi korunur; yalnızca yerel katalog verileri silinir.` })
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
  const sortOptions: Array<[string, string]> = [['default', 'Varsayılan'], ['price-desc', 'Satış Fiyatına Göre Azalan'], ['price-asc', 'Satış Fiyatına Göre Artan'], ['stock-desc', 'Stok Miktarına Göre Azalan'], ['stock-asc', 'Stok Miktarına Göre Artan']]
  const sortValue = catalogSort ? `${catalogSort.field}-${catalogSort.direction}` : 'default'
  const setSortValue = (value: string) => { if (value === 'default') setCatalogSort(null); else { const [field, direction] = value.split('-') as ['price' | 'stock', 'asc' | 'desc']; setCatalogSort({ field, direction }) } }
  const platformPop = (close: () => void) => <div className="rr-pop" role="dialog" style={{ minWidth: 290 }}><div className="rr-ph"><span>Platform Durumu<small className="rr-ph-sub">Ürün eşleşmelerini platforma göre filtrele</small></span>{platform && <button type="button" className="rr-btn rr-link" onClick={() => { setPlatform(''); close() }}>Temizle</button>}</div><button type="button" className={`rr-opt${platform ? '' : ' rr-on'}`} onClick={() => { setPlatform(''); close() }}><span className="rr-radio" aria-hidden="true" /><span className="rr-grow">Tüm Platformlar<small className="rr-opt-sub">Platform ayrımı olmadan göster</small></span></button><div className="rr-sep" />{productPlatformFilterGroups.map(group => { const open = expandedPlatformGroup === group.label; const selectedOption = group.options.find(option => option.value === platform); return <div key={group.label} className={`rr-acc${open ? ' rr-open' : ''}`}><button type="button" className="rr-opt" aria-expanded={open} onClick={() => setExpandedPlatformGroup(value => value === group.label ? null : group.label)}><RrLogo code={group.label.toLocaleUpperCase('tr-TR').replace('İ', 'I')} /><span className="rr-grow">{group.label}<small className="rr-opt-sub">{selectedOption ? `${selectedOption.label} · Seçili` : 'Durum Seçin'}</small></span><RrIcon name="chev" /></button>{open && <div className="rr-acc-b">{group.options.map(option => { const on = platform === option.value; return <button key={option.value} type="button" className={`rr-opt${on ? ' rr-on' : ''}`} role="radio" aria-checked={on} onClick={() => { setPlatform(option.value); close() }}><span className="rr-radio" aria-hidden="true" /><span className="rr-grow">{option.label}<small className="rr-opt-sub">{option.label === 'Aktif' ? 'Tüm varyantlar eşleşti' : option.label === 'Kısmi' ? 'Bazı varyantlar eşleşti' : 'Eşleşme bulunamadı'}</small></span></button> })}</div>}</div> })}{platforms.filter(item => !productPlatformFilterGroups.some(group => group.label.localeCompare(item, 'tr-TR', { sensitivity: 'base' }) === 0)).map(item => <button key={item} type="button" className={`rr-opt${platform === item ? ' rr-on' : ''}`} onClick={() => { setPlatform(item); close() }}><span className="rr-radio" aria-hidden="true" /><span className="rr-grow">{item}<small className="rr-opt-sub">Platform eşleşmelerini göster</small></span></button>)}</div>
  const columnPop = (label: string, options: Array<{ value: string; label: string }>, current: string, pick: (value: string) => void, right?: boolean) => (close: () => void) => <RrColumnPop label={label} options={options} value={current || 'ALL'} right={right} onPick={value => { pick(value === 'ALL' ? '' : value); close() }} />
  const bulkItems: Array<['price' | 'on' | 'off' | 'trash', string, string, () => void, string?] | 'hr'> = [['price', 'Toplu Fiyat ve Stok Düzenle', 'Seçili ürünlerin varyantları', () => setQuickEdit({ productIds: selectedProductIds, mode: 'both' })], ['price', 'Toplu Fiyat Düzenle', 'Fiyatları tek seferde güncelle', () => setQuickEdit({ productIds: selectedProductIds, mode: 'price' })], ['price', 'Toplu Stok Düzenle', 'Stokları artır, azalt veya eşitle', () => setQuickEdit({ productIds: selectedProductIds, mode: 'stock' })], 'hr', ['on', 'Toplu Satışa Aç', 'Seçili ürünleri satışa aç', () => setStatusRequest({ productIds: [...selectedProductIds], status: 'ACTIVE' })], ['off', 'Toplu Satışa Kapat', 'Seçili ürünleri arşive al', () => setStatusRequest({ productIds: [...selectedProductIds], status: 'ARCHIVED' }), 'rr-danger'], ['trash', 'Seçili Ürünleri Sil', 'Yerel katalogdan kalıcı olarak kaldır', () => bulkDeleteProducts(), 'rr-danger']]
  const bulkMenu = (close: () => void) => <div className="rr-menu" role="menu" aria-label="Toplu işlemler" style={{ minWidth: 290 }}>{!selectedProductIds.length && <p className="rr-lead" style={{ padding: '6px 12px' }}>İşlem yapmak için önce en az bir ürün seçin.</p>}{bulkItems.map((entry, index) => entry === 'hr' ? <hr key={`hr-${index}`} /> : <button key={entry[1]} type="button" role="menuitem" className={['rr-two', entry[4]].filter(Boolean).join(' ')} disabled={!selectedProductIds.length} onClick={() => { close(); entry[3]() }}><RrIcon name={entry[0]} /><span><b>{entry[1]}</b><small>{entry[2]}</small></span></button>)}</div>
  const pageList = (() => { const first = Math.max(1, Math.min(currentPage - 1, totalPages - 3)); return Array.from({ length: Math.min(4, totalPages) }, (_, index) => first + index) })()
  const statusRequestGroups = statusRequest ? productRowsAsCards(statusRequest.productIds.map(id => selectedProductCache[id] ?? products.find(product => product.id === id)).filter((product): product is Product => Boolean(product))) : []
  return <section className="content f3 rr rr-products">
    <div className="rr-ph"><div><div className="rr-crumb">Katalog</div><h1>Ürünler</h1><p>Ürün kartlarınızı, varyantlarını, fiyat ve stoklarını yönetin; pazar yerlerindeki yayın durumunu izleyin.</p></div><div className="rr-acts"><button type="button" className="rr-btn" onClick={openProductImport}><RrIcon name="import" /><span className="rr-lbl-long">Platformdan Ürün Aktar</span><span className="rr-lbl-short">Ürün Aktar</span></button><Link className="rr-btn rr-primary" to="/products/new"><RrIcon name="plus" />Yeni Ürün Ekle</Link></div></div>
    <div className="rr-metrics"><article><small>Toplam Ürün</small><strong>{metric(summaryQuery.data?.totalCount)}</strong><span>katalogdaki ürün kartı</span></article><article><small>Aktif Ürün</small><strong>{metric(summaryQuery.data?.activeCount)}</strong><span>satışa açık</span></article><article className="rr-bad"><small>Stoksuz Ürün</small><strong>{metric(summaryQuery.data?.outOfStockCount)}</strong><span>tüm varyantları tükendi</span></article><button type="button" className="rr-metric-btn rr-warn" onClick={() => setLowStockOpen(true)} aria-haspopup="dialog"><small>Düşük Stoklu</small><strong>{metric(summaryQuery.data?.lowStockCount)}</strong><span>Ayrıntıları Gör <RrIcon name="arrowR" /></span></button></div>
    <div className="rr-fshell">
      <ViewTabsWithMore className="product-status-tabs" more={[]} ariaLabel="Ürün durumları" value={status || 'ALL'} onChange={value => setStatus(value === 'ALL' ? '' : value)} items={statusTabs.map(([value, label, icon]) => ({ value: value || 'ALL', label: viewTabLabel(icon, label), count: tabCount(value) }))} />
      <RrFilterCard active={[Boolean(platform), Boolean(stock), Boolean(catalogSort)].filter(Boolean).length}><div className="rr-filters rr-has-sort">
        <label className="rr-fld rr-search"><small>Ürün Ara</small><span style={{ position: 'relative' }}><RrIcon name="search" style={{ position: 'absolute', left: 11, top: 10, color: 'var(--rr-subtle)' }} /><input className="rr-in" style={{ paddingLeft: 34 }} aria-label="Ürün ara" placeholder="Ürün adı, SKU veya barkod ara" value={search} onChange={event => setSearch(event.target.value)} /></span></label>
        <div className="rr-fld"><small>Platform Durumu</small><RrAnchor trigger={(open, toggle) => <button type="button" className={`rr-trigger${open ? ' rr-on' : ''}`} onClick={() => { if (!open) setExpandedPlatformGroup(selectedPlatformFilter?.group ?? null); toggle() }}><span>{selectedPlatformLabel}</span><RrIcon name="chev" /></button>}>{platformPop}</RrAnchor></div>
        <div className="rr-fld"><small>Stok Durumu</small><RrAnchor trigger={(open, toggle) => <button type="button" className={`rr-trigger${open ? ' rr-on' : ''}`} onClick={toggle}><span>{stockOptions.find(option => option.value === (stock || 'ALL'))?.label}</span><RrIcon name="chev" /></button>}>{columnPop('Stok Durumu', stockOptions, stock, setStock)}</RrAnchor></div>
        <div className="rr-fld"><small>Sıralama</small><RrAnchor trigger={(open, toggle) => <button type="button" className={`rr-trigger${open ? ' rr-on' : ''}`} onClick={toggle}><span>{sortOptions.find(option => option[0] === sortValue)?.[1]}</span><RrIcon name="chev" /></button>}>{close => <div className="rr-pop rr-right" role="listbox" style={{ minWidth: 260 }}>{sortOptions.map(([value, label]) => { const on = value === sortValue; return <button key={value} type="button" className={`rr-opt${on ? ' rr-on' : ''}`} role="radio" aria-checked={on} onClick={() => { setSortValue(value); close() }}><span className="rr-radio" aria-hidden="true" /><span className="rr-grow">{label}</span></button> })}</div>}</RrAnchor></div>
        <div className="rr-fbtns"><button type="button" className="rr-btn" onClick={() => { setSearch(''); setStatus(''); setPlatform(''); setStock(''); setCatalogSort(null) }}>Temizle</button><button type="button" className="rr-btn rr-primary" onClick={() => setSearchFilter(search.trim())}>Filtrele</button></div>
      </div></RrFilterCard>
    </div>
    {selectionMode && <div className="rr-selbar" role="region" aria-label="Seçim modu"><button type="button" className="rr-btn rr-quiet" onClick={exitSelectionMode}>İptal</button><span className="rr-grow">{selectedProductCardCount} ürün kartı seçildi</span><button type="button" className="rr-btn" onClick={toggleAllVisible}>{allVisibleSelected ? 'Seçimi Kaldır' : 'Tümünü Seç'}</button></div>}
    {(query.error ?? summaryQuery.error ?? connectionsQuery.error) && <RrError error={query.error ?? summaryQuery.error ?? connectionsQuery.error} onRetry={() => { void query.refetch(); void summaryQuery.refetch() }} />}
    <div className="rr-panel"><div className="rr-panel-h"><div><h2>Ürün Kataloğu</h2><p>Toplam {totalCount.toLocaleString('tr-TR')} ürün</p></div><RrPageSize value={pageSize} sizes={[20, 50, 100]} onChange={setPageSize} /></div>
      {selectedProductIds.length > 0 && hasMoreProductsToSelect && <div className="rr-xsel" role="status"><span><b>{allProductsSelected ? 'Tüm ürün kartları seçildi.' : `${selectedProductCardCount.toLocaleString('tr-TR')} ürün kartı seçildi.`}</b> {allProductsSelected ? `${selectedProductIds.length.toLocaleString('tr-TR')} katalog kaydı toplu işlemlere dahil.` : 'Diğer sayfalardaki kartlar seçime dahil değil.'}</span>{allProductsSelected ? <button type="button" className="rr-btn rr-link" onClick={() => { setSelectedProductIds([]); setSelectedProductCache({}); setAllProductsSelected(false) }}>Seçimi Temizle</button> : <button type="button" className="rr-btn rr-link" onClick={() => void selectAllFilteredProducts()} disabled={selectingAllProducts}>{selectingAllProducts ? 'Tüm sayfalar seçiliyor…' : `Tüm ${totalCount.toLocaleString('tr-TR')} Kaydı Seç`}</button>}</div>}
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
        <tbody>{!pageProducts.length ? <tr><td className="rr-empty-row" colSpan={7}><div className="rr-empty"><span className="rr-ei"><RrIcon name="box" /></span><b>Ürün Bulunamadı</b><span className="rr-lead">Filtrelerle eşleşen ürün yok.</span><button type="button" className="rr-btn rr-quiet" style={{ marginTop: 4 }} onClick={() => { setSearch(''); setStatus(''); setPlatform(''); setStock('') }}>Filtreleri Temizle</button></div></td></tr> : pageProductGroups.map(group => <ProductColorRows key={group.id} selectionMode={selectionMode} onEnterSelection={() => setSelectionMode(true)} group={group} selected={group.products.every(product => selectedProductIds.includes(product.id))} onSelect={() => toggleProductGroup(group)} onQuickEdit={mode => setQuickEdit({ productIds: group.products.map(product => product.id), mode })} onImageClick={(url, title) => setLightboxImage({ url, title })} onDelete={() => requestDeleteProductGroup(group)} onDuplicate={() => navigate('/products/new', { state: { duplicateProductId: group.primary.id } })} onSetStatus={next => { setSelectedProductCache(current => ({ ...current, ...Object.fromEntries(group.products.map(product => [product.id, product])) })); setStatusRequest({ productIds: group.products.map(product => product.id), status: next }) }} />)}</tbody>
      </table>}</div>
      {totalCount > 0 && <div className="rr-pager"><span>Toplam <b style={{ color: 'var(--rr-ink)' }}>{totalCount.toLocaleString('tr-TR')}</b> adet</span><div className="rr-pages"><button type="button" aria-label="Önceki" disabled={currentPage <= 1 || query.isFetching || pageJumping} onClick={() => setPageNumber(value => Math.max(1, value - 1))}><RrIcon name="left" /></button>{pageList.map(value => <button key={value} type="button" className={value === currentPage ? 'rr-on' : undefined} aria-current={value === currentPage ? 'page' : undefined} disabled={query.isFetching || pageJumping} onClick={() => void goToPage(value)}>{value}</button>)}<button type="button" aria-label="Sonraki" disabled={!(currentPage < totalPages && Boolean(nextPageCursor)) || query.isFetching || pageJumping} onClick={goToNextPage}><RrIcon name="right" /></button></div><label className="rr-jump">Sayfaya Git<input className="rr-in" type="number" min={1} max={totalPages} placeholder={`1–${totalPages}`} aria-label="Sayfa numarası" value={jumpPage} onChange={event => setJumpPage(event.target.value)} onKeyDown={event => { if (event.key === 'Enter' && Number(jumpPage) >= 1) { void goToPage(Math.floor(Number(jumpPage))); setJumpPage('') } }} /><button type="button" className="rr-btn" onClick={() => { if (Number(jumpPage) >= 1) { void goToPage(Math.floor(Number(jumpPage))); setJumpPage('') } }}>Git</button></label></div>}
    </div>
    {selectedProductIds.length > 0 && <div className="rr-bulk rr-pbulk" role="group" aria-label="Toplu işlemler"><span className="rr-grow rr-bulk-count"><span>{allProductsSelected ? 'Tüm ürün kartları seçildi' : `${selectedProductCardCount.toLocaleString('tr-TR')} ürün kartı seçildi`}</span>{hasMoreProductsToSelect && !allProductsSelected && <button type="button" className="rr-btn rr-link rr-bulk-all" onClick={() => void selectAllFilteredProducts()} disabled={selectingAllProducts}>{selectingAllProducts ? 'Seçiliyor…' : `Tümünü Seç (${totalCount.toLocaleString('tr-TR')})`}</button>}</span><button type="button" className="rr-btn rr-quiet rr-bulk-clear" aria-label="Seçimi Temizle" title="Seçimi Temizle" onClick={() => { setSelectedProductIds([]); setSelectedProductCache({}); setAllProductsSelected(false) }}><RrIcon name="close" /><span>Seçimi Temizle</span></button><button type="button" className="rr-btn" onClick={() => setStatusRequest({ productIds: [...selectedProductIds], status: 'ACTIVE' })}><RrIcon name="on" />Satışa Aç</button><button type="button" className="rr-btn" onClick={() => setQuickEdit({ productIds: selectedProductIds, mode: 'both' })}><RrIcon name="price" /><span className="rr-lbl-long">Fiyat ve Stok Düzenle</span><span className="rr-lbl-short">Fiyat · Stok</span></button><RrAnchor trigger={(open, toggle) => <button type="button" className="rr-btn rr-primary" aria-haspopup="menu" aria-expanded={open} onClick={toggle}><span className="rr-lbl-long">Toplu İşlemler ({selectedProductCardCount.toLocaleString('tr-TR')})</span><span className="rr-lbl-short">Toplu ({selectedProductCardCount.toLocaleString('tr-TR')})</span><RrIcon name="chev" /></button>}>{close => <div className="rr-bulk-menu">{bulkMenu(close)}</div>}</RrAnchor></div>}
    {statusRequest && <RrModal crumb="Toplu İşlem" title={statusRequest.status === 'ACTIVE' ? 'Satışa Aç' : 'Satışa Kapat'} desc={`${statusRequestGroups.length || statusRequest.productIds.length} ürün kartı ${statusRequest.status === 'ACTIVE' ? 'satışa açılacak' : 'arşive alınacak'}.`} size="sm" onClose={() => setStatusRequest(null)} foot={<><span className="rr-note" /><button type="button" className="rr-btn" onClick={() => setStatusRequest(null)}>Vazgeç</button><button type="button" className={`rr-btn ${statusRequest.status === 'ACTIVE' ? 'rr-primary' : 'rr-danger'}`} onClick={() => { const request = statusRequest; setStatusRequest(null); void bulkSetProductStatus(request.status, request.productIds) }}>{statusRequest.status === 'ACTIVE' ? 'Satışa Aç' : 'Satışa Kapat'}</button></>}>
      <div className="rr-sum">{statusRequestGroups.slice(0, 6).map(group => <div key={group.id}><RrThumb src={group.primary.primaryImageUrl} alt={group.primary.title} /><span>{group.primary.title}<small>{group.primary.modelCode || '—'} · {group.variants.length} varyant</small></span><RrBadge label={group.products.every(product => product.status === 'ACTIVE') ? 'Satışa Açık' : titleTr(productStatusLabel(group.primary.status))} /></div>)}</div>
      {statusRequestGroups.length > 6 && <p className="rr-lead">ve {statusRequestGroups.length - 6} ürün kartı daha</p>}
      {statusRequest.status === 'ARCHIVED' && <div className="rr-callout rr-warn"><RrIcon name="alert" /><span>Pazar yerlerindeki ilanlar bu işlemle kapanmaz; kapatmak için ürün düzenleme ekranından yayını durdurun.</span></div>}
    </RrModal>}
    {quickEdit && <ProductQuickEditModal products={quickEditProducts} connections={publicationConnections} mode={quickEdit.mode} onChanged={refresh} onResult={showProductToast} onClose={() => setQuickEdit(null)} />}
    {deleteRequest && <ProductDeleteConfirmModal request={deleteRequest} deleting={deletingProducts} onClose={() => setDeleteRequest(null)} onConfirm={() => void confirmProductDelete()} />}
    {productImportOpen && <RrModal crumb="Katalog Aktarımı" title="Platformdan Ürün Aktarımı" desc="Bağlı pazar yerlerindeki ürünleri panel kataloğunuza aktarın. İşlem salt okunurdur; pazar yerlerine hiçbir veri gönderilmez." size="lg" locked={productImporting} onClose={() => setProductImportOpen(false)} foot={<><span className="rr-note">{productImportConnectionIds.length ? <><b>{productImportConnectionIds.length} bağlantı</b> seçili · {productImportMethod === 'SINGLE' ? 'Tek Ürün Aktarımı' : titleTr(productImportModeLabels[productImportMode] ?? '')}</> : 'Bağlantı seçilmedi'}</span><button type="button" className="rr-btn" onClick={() => setProductImportOpen(false)} disabled={productImporting}>Vazgeç</button><button type="button" className="rr-btn rr-primary" onClick={() => void importProductsFromPlatforms()} disabled={productImporting || !productImportConnectionIds.length}><RrIcon name="import" />{productImporting ? 'Kuyruğa alınıyor…' : 'Aktarımı Başlat'}</button></>}>
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
      {activeProductSyncJobs.length > 0 && <section className="rr-dsec" aria-live="polite"><header><div className="rr-h3">Devam Eden Aktarımlar</div><p>{activeProductSyncJobs.length} işlem</p></header>{activeProductSyncJobs.map(job => { const received = Math.max(0, job.progressReceived); const total = job.progressTotal != null && job.progressTotal >= received ? job.progressTotal : null; const percent = job.progressPercent != null ? Math.min(100, Math.max(0, job.progressPercent)) : total != null && total > 0 ? Math.min(100, Math.max(0, Math.floor(received * 100 / total))) : null; const label = job.progressLabel ?? (job.status === 'PENDING' ? 'Kuyrukta bekliyor' : 'Aktarım çalışıyor'); const mapping = job.progressLabel?.includes('Eşlenen') ?? false; return <div className="rr-job" key={job.id}><div className="rr-job-h"><b>{label}</b><span className="rr-num">{percent == null ? '—' : `%${percent}`}</span><button type="button" className="rr-btn rr-quiet" style={{ height: 28 }} disabled={cancelProductSync.isPending} onClick={() => cancelProductSync.mutate(job.id)}>Durdur</button></div><span className="rr-bar"><i style={{ width: `${percent ?? 8}%` }} /></span><div className="rr-job-c"><span>Alınan <b className="rr-num">{received.toLocaleString('tr-TR')}</b></span><span>{mapping ? 'Eşlenen' : 'İşlenen'} <b className="rr-num">{job.progressProcessed.toLocaleString('tr-TR')}</b></span><span>{mapping ? 'Eşleşmeyen' : 'Atlanan'} <b className="rr-num">{job.progressSkipped.toLocaleString('tr-TR')}</b></span><span>Hatalı <b className="rr-num rr-bad">{job.progressFailed.toLocaleString('tr-TR')}</b></span></div></div> })}</section>}
    </RrModal>}
    {productToast && <NoticeToast key={`${productToast.kind}:${productToast.message}`} kind={productToast.kind} message={productToast.message} onClose={() => setProductToast(null)} durationMs={4000} />}
    {lowStockOpen && <LowStockDetailsModal products={lowStockQuery.data?.items ?? []} loading={lowStockQuery.isLoading} error={lowStockQuery.error} onClose={() => setLowStockOpen(false)} onImageClick={(url, title) => setLightboxImage({ url, title })} />}
    {lightboxImage && <ImageLightboxModal image={lightboxImage} onClose={() => setLightboxImage(null)} />}
  </section>
}
const productImportModeLabels: Record<string, string> = { FULL: 'Tam katalog taraması', NEW_ONLY: 'Yeni ürünleri aktar', EXISTING_ONLY: 'Mevcut ürünleri güncelle', MAPPING_ONLY: 'Ürün eşleme' }
function shortStoreId(id: string) { return id.length > 14 ? `${id.slice(0, 8)}…${id.slice(-4)}` : id }
type CategoryRequirement = { attributeId: string; isRequired: boolean; allowsCustomValue: boolean; displayOrder: number; role: 'ATTRIBUTE' | 'OPTION'; isWebColor?: boolean; attribute: Attribute }
function isOptionRequirement(requirement: CategoryRequirement) {
  return requirement.role === 'OPTION' || isOptionAttribute(requirement.attribute)
}
type VariantDraft = {
  key: string
  optionSignature: string
  options: Record<string, string>
  attributeValueIds: Record<string, string>
  sku: string
  barcode: string
  stock: number
  salePrice: number
  listPrice: number
  costPrice: number
  mediaRefs: string[]
  persistedAttributes?: ProductAttributeAssignment[]
  platformStatuses?: VariantPlatformStatus[]
}
const variantBulkEditLabels: Record<VariantBulkEditField, string> = { stock: 'Stok', salePrice: 'Fiyat', costPrice: 'Maliyet', listPrice: 'Liste fiyatı' }
type ProductAttributePayload = ProductAttributeAssignment
function marketplacePlatformName(platform: VariantPlatformStatus) {
  const code = platform.platformCode.trim().toLocaleUpperCase('tr-TR')
  return code === 'SHOPIFY' ? 'Shopify' : code === 'HEPSIBURADA' ? 'Hepsiburada' : code === 'TRENDYOL' ? 'Trendyol' : code === 'N11' ? 'N11' : code === 'PAZARAMA' ? 'Pazarama' : platform.platform
}
function marketplacePriceLabel(value: number | null | undefined, currency: string | null | undefined) {
  return value == null ? 'Tanımlı değil' : `${value.toLocaleString('tr-TR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })} ${currency ?? ''}`.trim()
}
function LegacyVariantPlatformPricingModal({ row, platforms, selectedPlatform, draft, saving, onClose, onSelectPlatform, onDraftChange, onSave }: { row: VariantDraft; platforms: VariantPlatformStatus[]; selectedPlatform: VariantPlatformStatus; draft: ChannelPricingDraft; saving: boolean; onClose: () => void; onSelectPlatform: (platform: VariantPlatformStatus) => void; onDraftChange: (field: keyof ChannelPricingDraft, value: string) => void; onSave: () => void }) {
  return <div className="workspace-modal-backdrop variant-platform-pricing-backdrop" role="presentation" onMouseDown={() => !saving && onClose()}><section className="workspace-modal variant-platform-pricing-modal" role="dialog" aria-modal="true" aria-labelledby="variant-platform-pricing-title" onMouseDown={event => event.stopPropagation()}><header><div><p className="eyebrow">PLATFORM FİYATLARI</p><h2 id="variant-platform-pricing-title">Varyant kanal fiyatları</h2><p>{row.optionSignature} · {row.barcode || row.sku}</p></div><button type="button" className="modal-close" onClick={onClose} disabled={saving} aria-label="Fiyat penceresini kapat"><UiIcon name="close" /></button></header><div className="variant-platform-pricing-body"><div className="variant-platform-pricing-list">{platforms.map(platform => { const active = platform.connectionId === selectedPlatform.connectionId; return <article className={`variant-platform-pricing-card${active ? ' is-selected' : ''}`} key={`${platform.platformCode}:${platform.connectionId ?? platform.platform}`}><div className="variant-platform-pricing-card-head"><span className={`publish-platform-mark ${platform.platformCode.toLocaleLowerCase('tr-TR')}`}><img className={`publish-platform-logo ${platformLogoClass(platform.platformCode)}`} src={platformLogoSource(platform.platformCode) ?? '/platforms/trendyol.webp'} alt="" /></span><div><strong>{marketplacePlatformName(platform)}</strong><small>{platform.isLinked ? 'Bağlı' : 'Bağlı değil'}</small></div><span className={`variant-platform-pricing-state ${platform.isLinked ? 'is-linked' : 'is-unlinked'}`} /></div><dl><div><dt>Liste</dt><dd>{marketplacePriceLabel(platform.listPrice, platform.currency)}</dd></div><div><dt>Satış</dt><dd>{marketplacePriceLabel(platform.salePrice, platform.currency)}</dd></div></dl><button type="button" className="secondary" onClick={() => onSelectPlatform(platform)} disabled={saving}>{active ? 'Düzenleniyor' : 'Düzenle'}</button></article> })}</div><div className="variant-platform-pricing-editor"><div><strong>{marketplacePlatformName(selectedPlatform)} fiyatını düzenle</strong><small>Panel ana fiyatı değişmeden yalnızca bu kanal teklifini günceller.</small></div><div className="variant-platform-pricing-fields"><label>Liste fiyatı<input autoFocus={!touchScreen} type="number" min="0" step="0.01" value={draft.listPrice} onChange={event => onDraftChange('listPrice', event.target.value)} /></label><label>Satış fiyatı<input type="number" min="0" step="0.01" value={draft.salePrice} onChange={event => onDraftChange('salePrice', event.target.value)} /></label></div></div></div><footer><button type="button" className="secondary" onClick={onClose} disabled={saving}>Vazgeç</button><button type="button" onClick={onSave} disabled={saving || !selectedPlatform.connectionId}>{saving ? 'Kaydediliyor…' : 'Fiyatı kaydet'}</button></footer></section></div>
}
function LegacyVariantPlatformPricingModalWithVariantsFirst({ row, rows, platforms, selectedPlatform, draft, saving, onClose, onSelectRow, onSelectPlatform, onDraftChange, onSave }: { row: VariantDraft; rows: VariantDraft[]; platforms: VariantPlatformStatus[]; selectedPlatform: VariantPlatformStatus; draft: ChannelPricingDraft; saving: boolean; onClose: () => void; onSelectRow: (row: VariantDraft) => void; onSelectPlatform: (platform: VariantPlatformStatus) => void; onDraftChange: (field: keyof ChannelPricingDraft, value: string) => void; onSave: () => void }) {
  return <div className="workspace-modal-backdrop variant-platform-pricing-backdrop" role="presentation" onMouseDown={() => !saving && onClose()}><section className="workspace-modal variant-platform-pricing-modal" role="dialog" aria-modal="true" aria-labelledby="variant-platform-pricing-title" onMouseDown={event => event.stopPropagation()}><header><div><p className="eyebrow">PLATFORM FİYATLARI</p><h2 id="variant-platform-pricing-title">Varyant kanal fiyatları</h2><p>{row.optionSignature} · {row.barcode || row.sku}</p></div><button type="button" className="modal-close" onClick={onClose} disabled={saving} aria-label="Fiyat penceresini kapat"><UiIcon name="close" /></button></header><div className="variant-platform-pricing-body"><section className="variant-platform-pricing-variant-list" aria-label="Tüm varyantlar"><div className="variant-platform-pricing-variant-list-head"><strong>Tüm varyantlar</strong><span>{rows.length} varyant</span></div><div className="variant-platform-pricing-variant-items" role="listbox" aria-label="Ürün varyantları">{rows.map(item => <button type="button" role="option" aria-selected={item.key === row.key} className={`variant-platform-pricing-variant-item${item.key === row.key ? ' is-selected' : ''}`} key={item.key} onClick={() => onSelectRow(item)} disabled={saving}><span><strong>{item.optionSignature || item.sku}</strong><small>{item.barcode || item.sku}</small></span><i>{item.platformStatuses?.length ?? 0}</i></button>)}</div></section><div className="variant-platform-pricing-list">{platforms.map(platform => { const active = platform.connectionId === selectedPlatform.connectionId; return <article className={`variant-platform-pricing-card${active ? ' is-selected' : ''}`} key={`${platform.platformCode}:${platform.connectionId ?? platform.platform}`}><div className="variant-platform-pricing-card-head"><span className={`publish-platform-mark ${platform.platformCode.toLocaleLowerCase('tr-TR')}`}><img className={`publish-platform-logo ${platformLogoClass(platform.platformCode)}`} src={platformLogoSource(platform.platformCode) ?? '/platforms/trendyol.webp'} alt="" /></span><div><strong>{marketplacePlatformName(platform)}</strong><small>{platform.isLinked ? 'Bağlı' : 'Bağlı değil'}</small></div><span className={`variant-platform-pricing-state ${platform.isLinked ? 'is-linked' : 'is-unlinked'}`} /></div><dl><div><dt>Liste</dt><dd>{marketplacePriceLabel(platform.listPrice, platform.currency)}</dd></div><div><dt>Satış</dt><dd>{marketplacePriceLabel(platform.salePrice, platform.currency)}</dd></div></dl><button type="button" className="secondary" onClick={() => onSelectPlatform(platform)} disabled={saving}>{active ? 'Düzenleniyor' : 'Düzenle'}</button></article> })}</div><div className="variant-platform-pricing-editor"><div><strong>{marketplacePlatformName(selectedPlatform)} fiyatını düzenle</strong><small>Panel ana fiyatı değişmeden yalnızca bu kanal teklifini günceller.</small></div><div className="variant-platform-pricing-fields"><label>Liste fiyatı<input autoFocus={!touchScreen} type="number" min="0" step="0.01" value={draft.listPrice} onChange={event => onDraftChange('listPrice', event.target.value)} /></label><label>Satış fiyatı<input type="number" min="0" step="0.01" value={draft.salePrice} onChange={event => onDraftChange('salePrice', event.target.value)} /></label></div></div></div><footer><button type="button" className="secondary" onClick={onClose} disabled={saving}>Vazgeç</button><button type="button" onClick={onSave} disabled={saving || !selectedPlatform.connectionId}>{saving ? 'Kaydediliyor…' : 'Fiyatı kaydet'}</button></footer></section></div>
}
function VariantPlatformPricingModal({ row, rows, platforms, selectedPlatform, draft, saving, onClose, onSelectRow, onSelectPlatform, onDraftChange, onSave }: { row: VariantDraft; rows: VariantDraft[]; platforms: VariantPlatformStatus[]; selectedPlatform: VariantPlatformStatus; draft: ChannelPricingDraft; saving: boolean; onClose: () => void; onSelectRow: (row: VariantDraft) => void; onSelectPlatform: (platform: VariantPlatformStatus) => void; onDraftChange: (field: keyof ChannelPricingDraft, value: string) => void; onSave: () => void }) {
  return <div className="workspace-modal-backdrop variant-platform-pricing-backdrop" role="presentation" onMouseDown={() => !saving && onClose()}><section className="workspace-modal variant-platform-pricing-modal" role="dialog" aria-modal="true" aria-labelledby="variant-platform-pricing-title" onMouseDown={event => event.stopPropagation()}><header><div><p className="eyebrow">PLATFORM FİYATLARI</p><h2 id="variant-platform-pricing-title">Varyant kanal fiyatları</h2><p>{row.optionSignature} · {row.barcode || row.sku}</p></div><button type="button" className="modal-close" onClick={onClose} disabled={saving} aria-label="Fiyat penceresini kapat"><UiIcon name="close" /></button></header><div className="variant-platform-pricing-body"><div className="variant-platform-pricing-list">{platforms.map(platform => { const active = platform.connectionId === selectedPlatform.connectionId; return <article className={`variant-platform-pricing-card${active ? ' is-selected' : ''}`} key={`${platform.platformCode}:${platform.connectionId ?? platform.platform}`}><div className="variant-platform-pricing-card-head"><span className={`publish-platform-mark ${platform.platformCode.toLocaleLowerCase('tr-TR')}`}><img className={`publish-platform-logo ${platformLogoClass(platform.platformCode)}`} src={platformLogoSource(platform.platformCode) ?? '/platforms/trendyol.webp'} alt="" /></span><div><strong>{marketplacePlatformName(platform)}</strong><small>{platform.isLinked ? 'Bağlı' : 'Bağlı değil'}</small></div><span className={`variant-platform-pricing-state ${platform.isLinked ? 'is-linked' : 'is-unlinked'}`} /></div><dl><div><dt>Liste</dt><dd>{marketplacePriceLabel(platform.listPrice, platform.currency)}</dd></div><div><dt>Satış</dt><dd>{marketplacePriceLabel(platform.salePrice, platform.currency)}</dd></div></dl><button type="button" className="secondary" onClick={() => onSelectPlatform(platform)} disabled={saving}>{active ? 'Düzenleniyor' : 'Düzenle'}</button></article> })}</div><section className="variant-platform-pricing-variant-list" aria-label="Tüm varyantlar"><div className="variant-platform-pricing-variant-list-head"><strong>Tüm varyantlar</strong><span>{rows.length} varyant · {marketplacePlatformName(selectedPlatform)} fiyatları</span></div><div className="variant-platform-pricing-variant-items" role="listbox" aria-label="Ürün varyantları">{rows.map(item => { const variantPlatform = item.platformStatuses?.find(platform => platform.platformCode === selectedPlatform.platformCode) ?? item.platformStatuses?.[0]; const priceSummary = variantPlatform ? `Liste ${marketplacePriceLabel(variantPlatform.listPrice, variantPlatform.currency)} · Satış ${marketplacePriceLabel(variantPlatform.salePrice, variantPlatform.currency)}` : 'Fiyat tanımlı değil'; return <button type="button" role="option" aria-selected={item.key === row.key} className={`variant-platform-pricing-variant-item${item.key === row.key ? ' is-selected' : ''}`} key={item.key} onClick={() => onSelectRow(item)} disabled={saving} aria-label={`${item.optionSignature || item.sku} · ${priceSummary}`}><span><strong>{item.optionSignature || item.sku}</strong><small>{item.barcode || item.sku}</small><em>{priceSummary}</em></span><i>{item.platformStatuses?.length ?? 0}</i></button> })}</div></section><div className="variant-platform-pricing-editor"><div><strong>{marketplacePlatformName(selectedPlatform)} fiyatını düzenle</strong><small>Panel ana fiyatı değişmeden yalnızca bu kanal teklifini günceller.</small></div><div className="variant-platform-pricing-fields"><label>Liste fiyatı<input autoFocus={!touchScreen} type="number" min="0" step="0.01" value={draft.listPrice} onChange={event => onDraftChange('listPrice', event.target.value)} /></label><label>Satış fiyatı<input type="number" min="0" step="0.01" value={draft.salePrice} onChange={event => onDraftChange('salePrice', event.target.value)} /></label></div></div></div><footer><button type="button" className="secondary" onClick={onClose} disabled={saving}>Vazgeç</button><button type="button" onClick={onSave} disabled={saving || !selectedPlatform.connectionId}>{saving ? 'Kaydediliyor…' : 'Fiyatı kaydet'}</button></footer></section></div>
}
function BulkVariantPlatformPricingModal({ row, rows, platforms, savedDrafts, productName, modelCode, filterGroups, matchesFilterValue, saving, onClose, onSave }: { row: VariantDraft; rows: VariantDraft[]; platforms: VariantPlatformStatus[]; savedDrafts: Record<string, ChannelPricingDraft>; productName: string; modelCode: string; filterGroups: VariantMediaGroup[]; matchesFilterValue: (row: VariantDraft, group: VariantMediaGroup, value: { id: string; value: string }) => boolean; saving: boolean; onClose: () => void; onSave: (drafts: Record<string, ChannelPricingDraft>) => void }) {
  const sortedRows = sortVariantsAlphabetically(rows)
  const platformKey = (platform: VariantPlatformStatus) => `${platform.platformCode}:${platform.connectionId ?? platform.platform}`
  const initialMatrixDrafts = () => Object.fromEntries(sortedRows.flatMap(item => platforms.map(platform => {
    const status = item.platformStatuses?.find(value => value.connectionId === platform.connectionId)
    const saved = platform.connectionId ? savedDrafts[`${item.key}:${platform.connectionId}`] : undefined
    return [`${item.key}:${platformKey(platform)}`, { listPrice: String(saved?.listPrice ?? status?.listPrice ?? platform.listPrice ?? item.listPrice ?? ''), salePrice: String(saved?.salePrice ?? status?.salePrice ?? platform.salePrice ?? item.salePrice ?? '') }]
  }))) as Record<string, ChannelPricingDraft>
  const [matrixDrafts, setMatrixDrafts] = useState<Record<string, ChannelPricingDraft>>(initialMatrixDrafts)
  const [dirtyMatrixDraftKeys, setDirtyMatrixDraftKeys] = useState<string[]>([])
  const [matrixBulkEdit, setMatrixBulkEdit] = useState<{ platform: VariantPlatformStatus; field: keyof ChannelPricingDraft; label: string; value: string; error: string } | null>(null)
  const [filterSelections, setFilterSelections] = useState<VariantOptionFilterSelections>({})
  useEffect(() => { setMatrixDrafts(initialMatrixDrafts()); setDirtyMatrixDraftKeys([]) }, [row.key, rows, platforms, savedDrafts])
  const matchingRows = filterVariantsByOptions(sortedRows, filterGroups, filterSelections, matchesFilterValue)
  const matrixDraft = (item: VariantDraft, platform: VariantPlatformStatus) => matrixDrafts[`${item.key}:${platformKey(platform)}`] ?? { listPrice: '', salePrice: '' }
  const updateMatrixDraft = (item: VariantDraft, platform: VariantPlatformStatus, field: keyof ChannelPricingDraft, value: string) => {
    const draftKey = `${item.key}:${platformKey(platform)}`
    setDirtyMatrixDraftKeys(current => current.includes(draftKey) ? current : [...current, draftKey])
    setMatrixDrafts(current => ({ ...current, [draftKey]: { ...(current[draftKey] ?? { listPrice: '', salePrice: '' }), [field]: value } }))
  }
  const openMatrixBulkEdit = (platform: VariantPlatformStatus, field: keyof ChannelPricingDraft, label: string) => setMatrixBulkEdit({ platform, field, label, value: matrixDraft(matchingRows[0] ?? row, platform)[field], error: '' })
  const applyMatrixBulkEdit = () => {
    if (!matrixBulkEdit) return
    const value = Number(matrixBulkEdit.value)
    if (!matrixBulkEdit.value.trim() || !Number.isFinite(value) || value < 0) {
      setMatrixBulkEdit(current => current ? { ...current, error: 'Sıfır veya daha büyük geçerli bir fiyat girin.' } : current)
      return
    }
    const pairedField = matrixBulkEdit.field === 'listPrice' ? 'salePrice' : 'listPrice'
    if (!matchingRows.length) {
      setMatrixBulkEdit(current => current ? { ...current, error: 'Seçili renk ve bedenlerle eşleşen varyant bulunamadı.' } : current)
      return
    }
    const violatesPriceOrder = matchingRows.some(item => {
      const pairedValue = Number(matrixDraft(item, matrixBulkEdit.platform)[pairedField])
      return Number.isFinite(pairedValue) && (matrixBulkEdit.field === 'listPrice' ? value < pairedValue : value > pairedValue)
    })
    if (violatesPriceOrder) {
      setMatrixBulkEdit(current => current ? { ...current, error: matrixBulkEdit.field === 'listPrice' ? 'Liste fiyatı hiçbir varyantta satış fiyatından düşük olamaz.' : 'Satış fiyatı hiçbir varyantta liste fiyatından yüksek olamaz.' } : current)
      return
    }
    setMatrixDrafts(current => {
      const next = { ...current }
      for (const item of matchingRows) {
        const key = `${item.key}:${platformKey(matrixBulkEdit.platform)}`
        const currentDraft = next[key] ?? { listPrice: '', salePrice: '' }
        next[key] = { ...currentDraft, [matrixBulkEdit.field]: matrixBulkEdit.value }
      }
      return next
    })
    setDirtyMatrixDraftKeys(current => [...new Set([...current, ...matchingRows.map(item => `${item.key}:${platformKey(matrixBulkEdit.platform)}`)])])
    setMatrixBulkEdit(null)
  }
  const renderMatrixHeader = (platform: VariantPlatformStatus, label: string) => {
    const key = platformKey(platform)
    const field = label === 'Liste fiyatı' ? 'listPrice' : 'salePrice'
    return <div className="variant-platform-pricing-matrix-cell variant-platform-pricing-matrix-head" key={`${key}:${label}`}><strong>{marketplacePlatformName(platform).toLocaleUpperCase('tr-TR')}</strong><span className="variant-platform-pricing-matrix-head-label"><span>{label.toLocaleUpperCase('tr-TR')}</span><button type="button" className="variant-platform-pricing-bulk-trigger" aria-label={`${marketplacePlatformName(platform)} ${label} için filtreye uyan varyantlarda toplu düzenleme aç`} title={`${marketplacePlatformName(platform)} ${label.toLocaleLowerCase('tr-TR')} toplu değiştir`} aria-haspopup="dialog" aria-expanded={matrixBulkEdit?.platform === platform && matrixBulkEdit.field === field} onClick={() => openMatrixBulkEdit(platform, field, label)} disabled={saving || !platform.connectionId}><UiIcon name="edit" size={13} /></button></span></div>
  }
  return <>
    <div className="workspace-modal-backdrop variant-platform-pricing-backdrop" role="presentation" onMouseDown={() => !saving && onClose()}>
      <section className="workspace-modal variant-platform-pricing-modal" role="dialog" aria-modal="true" aria-label="Platform fiyatları" onMouseDown={event => event.stopPropagation()}>
        <header><div><p className="eyebrow">PLATFORM FİYATLARI</p><p className="variant-platform-pricing-product-context"><strong>{productName || 'Ürün adı belirtilmemiş'}</strong><span>Model kodu: {modelCode || '—'}</span></p></div><button type="button" className="modal-close" onClick={onClose} disabled={saving} aria-label="Fiyat penceresini kapat"><UiIcon name="close" /></button></header>
        <div className="variant-platform-pricing-body">
          <section className="variant-platform-pricing-matrix" aria-label="Platformlara göre varyant fiyat matrisi">
            <div className="variant-platform-pricing-bulk-heading"><strong>Platform fiyatlarını toplu düzenle</strong><span>{matchingRows.length} / {rows.length} varyant eşleşti · Panel ana fiyatı değişmez</span></div>
            {filterGroups.length > 0 && <div className="variant-platform-pricing-filter-grid" aria-label="Platform fiyatı renk ve beden filtreleri">{filterGroups.map(group => <VariantFilterDropdown key={group.id} group={group} selectedValueIds={filterSelections[group.id] ?? []} onToggle={valueId => setFilterSelections(current => ({ ...current, [group.id]: (current[group.id] ?? []).includes(valueId) ? current[group.id].filter(id => id !== valueId) : [...(current[group.id] ?? []), valueId] }))} onClear={() => setFilterSelections(current => ({ ...current, [group.id]: [] }))} />)}</div>}
            <div className="variant-platform-pricing-matrix-scroll">
              <div className="variant-platform-pricing-matrix-grid" style={{ gridTemplateColumns: `220px repeat(${platforms.length * 2}, 128px)` }}>
                <div className="variant-platform-pricing-matrix-cell variant-platform-pricing-matrix-corner"><strong>Varyant</strong><small>Model / barkod</small></div>
                {platforms.flatMap(platform => [renderMatrixHeader(platform, 'Liste fiyatı'), renderMatrixHeader(platform, 'Satış fiyatı')])}
                {sortedRows.flatMap(item => [
                  <div className="variant-platform-pricing-matrix-cell variant-platform-pricing-matrix-row-label" key={`${item.key}:label`}><strong>{item.optionSignature || item.sku}</strong><small>{item.barcode || item.sku}</small></div>,
                  ...platforms.flatMap(platform => {
                    const draft = matrixDraft(item, platform)
                    return [
                      <div className="variant-platform-pricing-matrix-cell" key={`${item.key}:${platformKey(platform)}:list`}><input aria-label={`${item.optionSignature || item.sku} ${marketplacePlatformName(platform)} liste fiyatı`} type="number" min="0" step="0.01" value={draft.listPrice} disabled={saving || !platform.connectionId} onChange={event => updateMatrixDraft(item, platform, 'listPrice', event.target.value)} /></div>,
                      <div className="variant-platform-pricing-matrix-cell" key={`${item.key}:${platformKey(platform)}:sale`}><input aria-label={`${item.optionSignature || item.sku} ${marketplacePlatformName(platform)} satış fiyatı`} type="number" min="0" step="0.01" value={draft.salePrice} disabled={saving || !platform.connectionId} onChange={event => updateMatrixDraft(item, platform, 'salePrice', event.target.value)} /></div>
                    ]
                  })
                ])}
              </div>
            </div>
          </section>
        </div>
        <footer><button type="button" onClick={() => onSave(selectVariantDraftsByKeys(matrixDrafts, dirtyMatrixDraftKeys))} disabled={saving || !dirtyMatrixDraftKeys.length || !platforms.some(platform => platform.connectionId)}>{saving ? 'Kaydediliyor…' : 'Kaydet'}</button><button type="button" className="secondary" onClick={onClose} disabled={saving}>Vazgeç</button></footer>
      </section>
    </div>
    {matrixBulkEdit && createPortal(
      <div className="workspace-modal-backdrop variant-platform-pricing-bulk-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) setMatrixBulkEdit(null) }}>
        <section className="workspace-modal variant-bulk-edit-modal" role="dialog" aria-modal="true" aria-labelledby="variant-platform-pricing-bulk-title" onMouseDown={event => event.stopPropagation()}>
          <header><div><p className="eyebrow">PLATFORM FİYATLARI</p><h2 id="variant-platform-pricing-bulk-title">{marketplacePlatformName(matrixBulkEdit.platform)} {matrixBulkEdit.label.toLocaleLowerCase('tr-TR')} toplu değiştir</h2><p>Değer seçili renk ve bedenlerle eşleşen {matchingRows.length} varyantın bu platformdaki {matrixBulkEdit.label.toLocaleLowerCase('tr-TR')} alanına uygulanır.</p></div><button type="button" className="modal-close" onClick={() => setMatrixBulkEdit(null)} aria-label="Toplu fiyat değişikliği penceresini kapat"><UiIcon name="close" /></button></header>
          <div className="variant-bulk-edit-body"><p className="variant-bulk-edit-target"><span>Uygulanacak varyant</span><strong>{matchingRows.length.toLocaleString('tr-TR')} satır</strong></p><p className="variant-bulk-edit-scope">Uygula, yalnızca seçilen renk ve bedenlerin bu platformdaki taslaklarını değiştirir. Kalıcı olması için ardından ana penceredeki Kaydet düğmesine basın.</p><label htmlFor="variant-platform-pricing-bulk-value">Yeni fiyat<input id="variant-platform-pricing-bulk-value" autoFocus={!touchScreen} type="number" min="0" step="0.01" value={matrixBulkEdit.value} aria-describedby={matrixBulkEdit.error ? 'variant-platform-pricing-bulk-error' : undefined} onChange={event => setMatrixBulkEdit(current => current ? { ...current, value: event.target.value, error: '' } : current)} /></label>{matrixBulkEdit.error && <p className="variant-bulk-edit-error" id="variant-platform-pricing-bulk-error" role="alert">{matrixBulkEdit.error}</p>}</div>
          <footer><button type="button" className="secondary" onClick={() => setMatrixBulkEdit(null)}>Vazgeç</button><button type="button" onClick={applyMatrixBulkEdit} disabled={!matchingRows.length}>Uygula</button></footer>
        </section>
      </div>,
      document.body
    )}
  </>
}
void LegacyVariantPlatformPricingModal
void LegacyVariantPlatformPricingModalWithVariantsFirst
void VariantPlatformPricingModal
// API, inventory and publication safeguards retain the actual 1000-line limit.
// The product workspace intentionally does not display an arbitrary UI quota.
const MAX_VARIANTS = 1000
const MAX_PRODUCT_MEDIA_BYTES = 6 * 1024 * 1024

function RichTextTool({ icon, label, onClick, disabled = false }: { icon: UiIconName; label: string; onClick: () => void; disabled?: boolean }) {
  return <button type="button" className="rich-text-tool" title={label} aria-label={label} onClick={onClick} disabled={disabled}><span className="rich-text-tool-icon" aria-hidden="true"><UiIcon name={icon} /></span><span className="rich-text-tool-label">{label}</span></button>
}

function RichTextEditor({ value, onChange }: { value: string; onChange: (value: string) => void }) {
  const visualEditor = useRef<HTMLDivElement>(null)
  const lastValue = useRef(value)
  const [htmlMode, setHtmlMode] = useState(false)
  const plainTextLength = value.replace(/<[^>]*>/g, '').trim().length

  useEffect(() => {
    if (!htmlMode && visualEditor.current && lastValue.current !== value) visualEditor.current.innerHTML = sanitizeRichText(value)
    lastValue.current = value
  }, [htmlMode, value])

  useEffect(() => {
    if (!htmlMode && visualEditor.current) visualEditor.current.innerHTML = sanitizeRichText(value)
  }, [htmlMode])

  function syncVisualValue() {
    const next = sanitizeRichText(visualEditor.current?.innerHTML ?? '')
    if (visualEditor.current && visualEditor.current.innerHTML !== next) visualEditor.current.innerHTML = next
    lastValue.current = next
    onChange(next === '<br>' ? '' : next)
  }

  function runCommand(command: string, argument?: string) {
    if (htmlMode || !visualEditor.current) return
    visualEditor.current.focus()
    document.execCommand(command, false, argument)
    syncVisualValue()
  }

  function insertLink() {
    const url = window.prompt('Bağlantı adresi', 'https://')
    if (url) runCommand('createLink', url)
  }

  function insertImage() {
    const url = window.prompt('Görsel adresi', 'https://')
    if (url) runCommand('insertImage', url)
  }

  function clearFormatting() {
    runCommand('removeFormat')
    runCommand('formatBlock', 'p')
  }

  return <div className="rich-text-editor rich-text-editor-pro">
    <div className="rich-text-editor-head">
      <div><strong>Ürün Açıklaması</strong><span className="rich-text-mode-note">{htmlMode ? 'HTML kodu düzenleniyor' : 'Görsel düzenleyici'}</span></div>
      <div className="rich-text-editor-meta"><span>{plainTextLength} karakter</span><div className="rich-text-mode-switch" role="group" aria-label="Açıklama görünümü"><button type="button" className={!htmlMode ? 'active' : ''} aria-pressed={!htmlMode} onClick={() => setHtmlMode(false)}><UiIcon name="sparkle" /> Görsel</button><button type="button" className={htmlMode ? 'active' : ''} aria-pressed={htmlMode} onClick={() => setHtmlMode(true)}><UiIcon name="code" /> HTML</button></div></div>
    </div>
    <div className="rich-text-toolbar" aria-label="Açıklama biçimlendirme araçları">
      <div className="rich-text-tool-group" aria-label="Metin biçimi"><RichTextTool icon="bold" label="Kalın" onClick={() => runCommand('bold')} disabled={htmlMode} /><RichTextTool icon="italic" label="İtalik" onClick={() => runCommand('italic')} disabled={htmlMode} /><RichTextTool icon="underline" label="Altı çizili" onClick={() => runCommand('underline')} disabled={htmlMode} /></div>
      <div className="rich-text-tool-group rich-text-alignment-group" aria-label="Paragraf hizası"><RichTextTool icon="alignLeft" label="Sola hizala" onClick={() => runCommand('justifyLeft')} disabled={htmlMode} /><RichTextTool icon="alignCenter" label="Ortala" onClick={() => runCommand('justifyCenter')} disabled={htmlMode} /><RichTextTool icon="alignRight" label="Sağa hizala" onClick={() => runCommand('justifyRight')} disabled={htmlMode} /></div>
      <div className="rich-text-tool-group" aria-label="İçerik ekle"><select aria-label="Yazı boyutu" defaultValue="" disabled={htmlMode} onChange={event => { const map: Record<string, string> = { '12': '2', '15': '3', '19': '5' }; runCommand('fontSize', map[event.target.value] ?? '3'); event.currentTarget.value = '' }}><option value="" disabled>Yazı boyutu</option><option value="12">Küçük</option><option value="15">Normal</option><option value="19">Büyük</option></select><RichTextTool icon="list" label="Madde listesi" onClick={() => runCommand('insertUnorderedList')} disabled={htmlMode} /><RichTextTool icon="paragraph" label="Paragraf" onClick={() => runCommand('formatBlock', 'p')} disabled={htmlMode} /><RichTextTool icon="textColor" label="Metin rengi" onClick={() => runCommand('foreColor', '#7652e8')} disabled={htmlMode} /><RichTextTool icon="link" label="Bağlantı ekle" onClick={insertLink} disabled={htmlMode} /><RichTextTool icon="image" label="Görsel ekle" onClick={insertImage} disabled={htmlMode} /></div>
      <div className="rich-text-tool-group" aria-label="Düzenleme"><RichTextTool icon="undo" label="Geri al" onClick={() => runCommand('undo')} disabled={htmlMode} /><RichTextTool icon="redo" label="Yinele" onClick={() => runCommand('redo')} disabled={htmlMode} /><RichTextTool icon="clearFormatting" label="Biçimi temizle" onClick={clearFormatting} disabled={htmlMode} /></div>
    </div>
    {htmlMode ? <textarea className="rich-text-html-editor" value={value} onChange={event => { lastValue.current = event.target.value; onChange(event.target.value) }} aria-label="Açıklama HTML kodu" placeholder="<p>Ürünün öne çıkan özelliklerini anlatın…</p>" spellCheck={false} /> : <div ref={visualEditor} className="rich-text-canvas" contentEditable role="textbox" aria-multiline="true" aria-label="Açıklama" data-placeholder="Ürünün öne çıkan özelliklerini anlatın…" suppressContentEditableWarning dangerouslySetInnerHTML={{ __html: sanitizeRichText(value) }} onInput={syncVisualValue} />}
  </div>
}

function buildVariantMatrix(requirements: CategoryRequirement[], variantAttributeIds: string[], selectedValueIds: Record<string, string[]>, baseSku: string, fallbackListPrice: number, fallbackSalePrice: number, fallbackCostPrice: number, initialStock: number, automaticBarcodes = false, modelCode = '') {
  const axes = variantAttributeIds.map(attributeId => {
    const requirement = requirements.find(item => item.attributeId === attributeId)
    const selected = new Set(selectedValueIds[attributeId] ?? [])
    return { requirement, values: requirement?.attribute.values.filter(value => selected.has(value.id)) ?? [] }
  }).filter(axis => axis.requirement && axis.values.length)
  if (!axes.length) return [] as VariantDraft[]
  const count = axes.reduce((total, axis) => total * axis.values.length, 1)
  if (count > MAX_VARIANTS) throw new Error(`En fazla ${MAX_VARIANTS} varyant oluşturabilirsiniz. Seçili kombinasyon sayısı: ${count}.`)
  const combinations = axes.reduce<Array<{ options: Record<string, string>; attributeValueIds: Record<string, string> }>>((carry, axis) => {
    if (!carry.length) return axis.values.map(value => ({ options: { [axis.requirement!.attribute.name]: value.value }, attributeValueIds: { [axis.requirement!.attributeId]: value.id } }))
    return carry.flatMap(entry => axis.values.map(value => ({ options: { ...entry.options, [axis.requirement!.attribute.name]: value.value }, attributeValueIds: { ...entry.attributeValueIds, [axis.requirement!.attributeId]: value.id } })))
  }, [])
  return combinations.map((entry, index) => ({
    key: crypto.randomUUID(),
    optionSignature: Object.entries(entry.options).map(([name, value]) => `${name}:${cleanOptionValue(value)}`).join('_'),
    options: entry.options,
    attributeValueIds: entry.attributeValueIds,
    ...buildVariantGenerationDefaults({ baseSku, modelCode, sequence: index + 1, automaticBarcodes, fallbackSalePrice, fallbackListPrice }),
    stock: initialStock,
    costPrice: fallbackCostPrice,
    mediaRefs: []
  }))
}

function productAttributePayload(requirement: CategoryRequirement, selectedIds: string[], typedValue: string, sortOrder: number): ProductAttributePayload[] {
  if (selectedIds.length) return selectedIds.map((valueId, index) => ({ attributeId: requirement.attributeId, valueId, textValue: null, numberValue: null, booleanValue: null, sortOrder: sortOrder * 100 + index }))
  const typed = typedValue.trim()
  if (!typed) return []
  if (requirement.attribute.dataType === 'NUMBER') {
    const value = Number(typed)
    if (!Number.isFinite(value)) throw new Error(`${requirement.attribute.name} sayısal olmalıdır.`)
    return [{ attributeId: requirement.attributeId, valueId: null, textValue: null, numberValue: value, booleanValue: null, sortOrder }]
  }
  if (requirement.attribute.dataType === 'BOOLEAN') {
    const normalized = typed.toLocaleLowerCase('tr-TR')
    if (!['true', 'false', 'evet', 'hayır', 'hayir', '1', '0'].includes(normalized)) throw new Error(`${requirement.attribute.name} için evet veya hayır seçin.`)
    return [{ attributeId: requirement.attributeId, valueId: null, textValue: null, numberValue: null, booleanValue: ['true', 'evet', '1'].includes(normalized), sortOrder }]
  }
  return [{ attributeId: requirement.attributeId, valueId: null, textValue: typed, numberValue: null, booleanValue: null, sortOrder }]
}

function CategoryAttributeValueDropdown({
  attributeName,
  required,
  values,
  selectedValues,
  onToggleValue
}: {
  attributeName: string
  required: boolean
  values: Array<{ id: string; value: string }>
  selectedValues: string[]
  onToggleValue: (valueId: string) => void
}) {
  const [open, setOpen] = useState(false)
  const [search, setSearch] = useState('')
  const [alignMenuRight, setAlignMenuRight] = useState(false)
  const rootRef = useRef<HTMLDivElement>(null)
  const isSingle = true // a category attribute takes one value
  const selected = values.filter(value => selectedValues.includes(value.id))
  const normalizedSearch = search.trim().toLocaleLowerCase('tr-TR')
  const filteredValues = values.filter(value => !normalizedSearch || value.value.toLocaleLowerCase('tr-TR').includes(normalizedSearch))
  const summary = selected.length === 0 ? 'Değer seçin' : isSingle ? selected[0].value : selected.length === 1 ? selected[0].value : `${selected[0].value} + ${selected.length - 1} değer`

  useEffect(() => {
    if (!open) return
    function closeOnOutsidePointer(event: PointerEvent) {
      if (!rootRef.current?.contains(event.target as Node)) setOpen(false)
    }
    document.addEventListener('pointerdown', closeOnOutsidePointer)
    return () => document.removeEventListener('pointerdown', closeOnOutsidePointer)
  }, [open])

  function clearSelection() {
    selected.forEach(value => onToggleValue(value.id))
  }

  function toggleDropdown() {
    if (!open) {
      const bounds = rootRef.current?.getBoundingClientRect()
      const menuWidth = Math.min(320, window.innerWidth - 32)
      setAlignMenuRight(Boolean(bounds && bounds.left + menuWidth > window.innerWidth - 16))
    }
    setOpen(current => !current)
  }

  return <div className="category-attribute-dropdown" ref={rootRef}>
    <button type="button" className={`category-attribute-select-trigger ${selected.length ? 'active' : 'is-empty'} ${required ? 'is-required' : 'is-optional'}`} aria-haspopup="listbox" aria-expanded={open} onClick={toggleDropdown}>
      <span>{summary}</span>
      <UiIcon name="chevronDown" />
    </button>
    {open && <div className={`category-attribute-dropdown-menu ${alignMenuRight ? 'is-edge-aligned' : ''}`} role="listbox" aria-label={`${attributeName} değerleri`}>
      <div className="category-attribute-dropdown-tools">
        <input autoFocus={!touchScreen} value={search} onChange={event => setSearch(event.target.value)} placeholder="Değer ara..." aria-label={`${attributeName} değerlerinde ara`} />
      </div>
      {selected.length > 0 && <button type="button" className="category-attribute-clear-selection" onClick={clearSelection}>Seçimi temizle</button>}
      <div className="category-attribute-dropdown-options">
        {filteredValues.length ? filteredValues.map(value => {
          const isSelected = selectedValues.includes(value.id)
          return <button type="button" role="option" aria-selected={isSelected} className={isSelected ? 'active' : ''} key={value.id} onClick={() => { onToggleValue(value.id); if (isSingle) setOpen(false) }}><span>{value.value}</span><i aria-hidden="true">{isSelected ? <UiIcon name="check" /> : null}</i></button>
        }) : <span className="category-attribute-dropdown-empty">Aramaya uygun değer yok.</span>}
      </div>
    </div>}
  </div>
}

function VariantFilterDropdown({
  group,
  selectedValueIds,
  onToggle,
  onClear
}: {
  group: VariantMediaGroup
  selectedValueIds: string[]
  onToggle: (valueId: string) => void
  onClear: () => void
}) {
  const [open, setOpen] = useState(false)
  const [search, setSearch] = useState('')
  const rootRef = useRef<HTMLDivElement>(null)
  const selected = group.values.filter(value => selectedValueIds.includes(value.id))
  const normalizedSearch = search.trim().toLocaleLowerCase('tr-TR')
  const filteredValues = group.values.filter(value => !normalizedSearch || value.value.toLocaleLowerCase('tr-TR').includes(normalizedSearch))
  const summary = selected.length === 0 ? 'Değer seçin' : selected.length === 1 ? selected[0].value : `${selected[0].value} + ${selected.length - 1} değer`

  useEffect(() => {
    if (!open) return
    function closeOnOutsidePointer(event: PointerEvent) {
      if (!rootRef.current?.contains(event.target as Node)) setOpen(false)
    }
    document.addEventListener('pointerdown', closeOnOutsidePointer)
    return () => document.removeEventListener('pointerdown', closeOnOutsidePointer)
  }, [open])

  return <div className="variant-filter-field" ref={rootRef}>
    <button type="button" className={`variant-filter-select-trigger ${selected.length ? 'active' : ''}`} aria-haspopup="listbox" aria-expanded={open} onClick={() => setOpen(current => !current)}>
      <span><small>{selected.length ? `${selected.length} değer seçildi` : group.name}</small><strong>{summary}</strong></span>
      <UiIcon name="chevronDown" />
    </button>
    {open && <div className="variant-filter-dropdown-menu" role="listbox" aria-label={`${group.name} filtre değerleri`}>
      <div className="variant-filter-dropdown-tools">
        <input autoFocus={!touchScreen} value={search} onChange={event => setSearch(event.target.value)} placeholder="Değer ara..." aria-label={`${group.name} değerlerinde ara`} />
      </div>
      {selected.length > 0 && <button type="button" className="variant-filter-clear-selection" onClick={onClear}>Seçimi temizle</button>}
      <div className="variant-filter-dropdown-options">
        {filteredValues.length ? filteredValues.map(value => {
          const isSelected = selectedValueIds.includes(value.id)
           return <button type="button" role="option" aria-selected={isSelected} className={isSelected ? 'active' : ''} key={value.id} onClick={() => onToggle(value.id)}><span>{value.value}</span><i aria-hidden="true">{isSelected ? <UiIcon name="check" /> : null}</i></button>
        }) : <span className="variant-filter-dropdown-empty">Aramaya uygun değer yok.</span>}
      </div>
    </div>}
  </div>
}

function CategoryAttributeMappingPanel({
  categoryId,
  categoryLabel,
  requirements,
  isLoading,
  isError,
  attributeSelections,
  attributeTextValues,
  onToggleValue,
  onTextChange,
  webColorRequirement,
  webColorAutoEnabled,
  manualWebColorValueId,
  onToggleWebColorAuto,
  onManualWebColorValueChange
}: {
  categoryId: string
  categoryLabel: string
  requirements: CategoryRequirement[]
  isLoading: boolean
  isError: boolean
  attributeSelections: Record<string, string[]>
  attributeTextValues: Record<string, string>
  onToggleValue: (attributeId: string, valueId: string) => void
  onTextChange: (attributeId: string, value: string) => void
  webColorRequirement?: CategoryRequirement
  webColorAutoEnabled: boolean
  manualWebColorValueId: string
  onToggleWebColorAuto: (enabled: boolean) => void
  onManualWebColorValueChange: (valueId: string) => void
}) {
  const attributes = requirements
    .filter(item => !isOptionRequirement(item) && item.attributeId !== webColorRequirement?.attributeId)
    .sort((left, right) => Number(right.isRequired) - Number(left.isRequired) || left.attribute.name.localeCompare(right.attribute.name, 'tr-TR', { sensitivity: 'base' }))

  return <section className="panel product-step-card product-category-mapping-card">
    <div className="editor-section-title">
      <span>3</span>
      <div>
        <h2>Kategori özellikleri</h2>
        <p>{categoryLabel ? `Seçili kategori: ${categoryLabel} · Bu kategoriye bağlanan özellik değerlerini atayın.` : 'Seçili kategoriye bağlanan özellik değerlerini atayın.'}</p>
      </div>
    </div>
    {!categoryId ? (
      <div className="unknown"><strong>Önce kategori seçin</strong><p>Kategori seçildiğinde eşlenmiş özellik başlıkları burada görünür.</p></div>
    ) : isLoading ? (
      <p>Kategori özellikleri yükleniyor…</p>
    ) : isError ? (
      <div className="unknown"><strong>Kategori özellikleri alınamadı</strong><p>Özellik başlıklarını Kategori Eşleştirme ekranında hazırlayın.</p></div>
    ) : attributes.length || webColorRequirement ? (
      <div className="category-attribute-mapping-list">
        {webColorRequirement && <article className="category-attribute-field category-attribute-field-web-color required has-selection">
          <div className="category-attribute-field-head">
            <div>
              <strong>Web Color <b className="required-marker" aria-hidden="true">*</b></strong>
              <small>Varyant renk aktarımı · Zorunlu</small>
            </div>
          </div>
          <div className={`attribute-builder-web-color-mode${webColorAutoEnabled ? ' is-auto' : ' is-manual'}`}>
            <label className="attribute-builder-web-color-toggle">
              <input type="checkbox" checked={webColorAutoEnabled} onChange={event => onToggleWebColorAuto(event.target.checked)} />
              <span><strong>Varyant renklerini otomatik aktar</strong><small>{webColorAutoEnabled ? 'Açık · Renk eşleşmelerinden dönüştürülmüş Web Color gönderilir.' : 'Kapalı · Web Color panel değeri aşağıdan seçilir.'}</small></span>
            </label>
            {webColorAutoEnabled ? null : (
              <label className="attribute-builder-web-color-manual">Trendyol katalog rengi<select aria-label="Manuel Trendyol katalog rengi" value={manualWebColorValueId} onChange={event => onManualWebColorValueChange(event.target.value)}><option value="">Trendyol katalog rengi seçin</option>{sortOptionValues('Renk', webColorRequirement.attribute.values).map(value => <option key={value.id} value={value.id}>{cleanOptionValue(value.value)}</option>)}</select></label>
            )}
          </div>
        </article>}
        {attributes.map(item => {
          const selectedValues = attributeSelections[item.attributeId] ?? []
          const typedValue = attributeTextValues[item.attributeId] ?? ''
          const hasValue = item.attribute.values.length > 0 ? selectedValues.length > 0 : typedValue.trim().length > 0
          const displayName = item.attribute.name
          return <article className={`category-attribute-field ${item.isRequired ? 'required' : ''} ${hasValue ? 'has-selection' : ''} ${item.isRequired && !hasValue ? 'is-missing' : ''}`} key={item.attributeId}>
            <div className="category-attribute-field-head">
              <div>
                <strong>{displayName}{item.isRequired && <b className="required-marker" aria-hidden="true"> *</b>}</strong>
              </div>
            </div>
            {item.attribute.values.length ? (
              <CategoryAttributeValueDropdown attributeName={displayName} required={item.isRequired} values={item.attribute.values} selectedValues={selectedValues} onToggleValue={valueId => onToggleValue(item.attributeId, valueId)} />
            ) : item.attribute.dataType === 'BOOLEAN' ? (
              <select aria-label={`${displayName} değeri`} value={attributeTextValues[item.attributeId] ?? ''} onChange={event => onTextChange(item.attributeId, event.target.value)}>
                <option value="">Değer seçin</option>
                <option value="evet">Evet</option>
                <option value="hayır">Hayır</option>
              </select>
            ) : (
              <input aria-label={`${displayName} değeri`} value={attributeTextValues[item.attributeId] ?? ''} onChange={event => onTextChange(item.attributeId, event.target.value)} type={item.attribute.dataType === 'NUMBER' ? 'number' : 'text'} placeholder={item.allowsCustomValue ? 'Değer girin' : 'Özellik değerini girin'} />
            )}
          </article>
        })}
      </div>
    ) : (
      <div className="empty small"><strong>Bu kategori için özellik başlığı yok</strong><p>Kategori Eşleştirme ekranından önce özellik başlıklarını oluşturun.</p></div>
    )}
  </section>
}

// Platform codes are stored upper-case; show the brand names the way they are written.
function publishPlatformName(code: string) {
  const names: Record<string, string> = { TRENDYOL: 'Trendyol', HEPSIBURADA: 'Hepsiburada', SHOPIFY: 'Shopify', N11: 'n11', PAZARAMA: 'Pazarama', PTTAVM: 'PttAVM', TRENDYOL_EFATURAM: 'E-Faturam' }
  const normalized = code.trim().toLocaleUpperCase('tr-TR')
  return names[normalized] ?? code.charAt(0).toLocaleUpperCase('tr-TR') + code.slice(1).toLocaleLowerCase('tr-TR')
}
// Opens the issue tooltip upwards when it would be clipped by the bottom of the viewport.
function placePublishIssueTooltip(event: { currentTarget: HTMLElement }) {
  const status = event.currentTarget
  const tooltip = status.nextElementSibling
  if (!(tooltip instanceof HTMLElement) || !tooltip.classList.contains('publish-issue-tooltip')) return
  tooltip.style.display = 'block'
  const height = tooltip.offsetHeight
  tooltip.style.removeProperty('display')
  const rect = status.getBoundingClientRect()
  const fitsBelow = window.innerHeight - rect.bottom >= height + 16
  tooltip.dataset.placement = !fitsBelow && rect.top >= height + 16 ? 'top' : 'bottom'
}

function PublishPlatformCard({ card, selected, productId, categoryId, productChecks, selectedAttributes, variantMediaWarning, onSelect, onDeselect }: {
  card: { code: string; name: string; initial: string; tone: string; connection: MarketplaceConnection; mode: 'PUBLISH' | 'READ_ONLY' }
  selected: boolean
  productId?: string
  categoryId: string
  productChecks: Array<{ title: string; detail: string; ok: boolean; shopifyOk?: boolean; shopifyDetail?: string }>
  selectedAttributes: PublicationAttributeSelection[]
  variantMediaWarning?: string
  onSelect: () => void
  onDeselect: () => void
}) {
  const client = useQueryClient()
  const statusKey = ['publication-status', productId, card.connection.id]
  const publication = useQuery({
    queryKey: statusKey,
    queryFn: () => hubApi<PublicationStatus>(`/products/${productId}/publication-status/${card.connection.id}`, { cache: 'no-store' }),
    enabled: !!productId && card.mode === 'PUBLISH',
    refetchInterval: query => isPublicationStatusJobRunning(query.state.data?.lastJobStatus) ? 3000 : false,
    refetchIntervalInBackground: true,
    refetchOnWindowFocus: true,
    staleTime: 0,
    retry: false
  })
  const refreshPublication = useMutation({
    mutationFn: async () => {
      if (!productId) throw new Error('Ürün kaydı bulunamadı.')
      return hubApi<AcceptedJob>(`/products/${productId}/publication-status/${card.connection.id}/refresh`, {
        method: 'POST',
        headers: { 'Idempotency-Key': key() },
        body: '{}'
      })
    },
    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: statusKey })
      appendNotification(`${card.name} yayın durumu güncelleniyor.`, 'success')
    },
    onError: reason => appendNotification(reason instanceof Error ? reason.message : 'Yayın durumu güncellenemedi.', 'error')
  })

  const publicationLabel = card.mode === 'READ_ONLY'
    ? 'Ürün yayını desteklenmiyor'
    : publication.isPending
    ? 'Yayın durumu yükleniyor…'
    : publication.isError
      ? 'Yayın durumu alınamadı'
      : publicationStatusLabel(publication.data?.actualStatus, publication.data?.lastJobStatus)
  const publicationTone = publicationStatusTone(publication.data?.actualStatus, publication.data?.lastJobStatus)
  const alreadyPublished = isPublicationLive(publication.data?.actualStatus)
  const publicationCheckPending = Boolean(card.mode === 'PUBLISH' && productId && publication.isPending)
  const publicationCheckUnavailable = Boolean(card.mode === 'PUBLISH' && productId && publication.isError && !publication.data)
  const shopifyWriteCapabilities = useQuery<Array<{ code: string; supportLevel: string; verifiedForConnection: boolean }>>({
    queryKey: ['capabilities', card.connection.id],
    queryFn: () => hubApi(`/connections/${card.connection.id}/capabilities`),
    enabled: card.mode === 'PUBLISH' && card.code === 'SHOPIFY',
    staleTime: 15_000,
    retry: false
  })
  const shopifyCapabilityCheckPending = card.mode === 'PUBLISH' && card.code === 'SHOPIFY' && shopifyWriteCapabilities.isPending
  const requiredShopifyCapabilities = ['PRODUCT_WRITE', 'INVENTORY_WRITE', 'PUBLICATION_READ', 'PUBLICATION_WRITE']
  const missingShopifyCapabilities = card.code === 'SHOPIFY' && shopifyWriteCapabilities.data
    ? requiredShopifyCapabilities.filter(code => !shopifyWriteCapabilities.data?.some(capability => capability.code === code && capability.supportLevel.toUpperCase() === 'SUPPORTED' && capability.verifiedForConnection))
    : []
  useEffect(() => {
    if (alreadyPublished && selected) onDeselect()
  }, [alreadyPublished, onDeselect, selected])
  const publicationNote = publicationStatusNote(publication.data?.lastRejectionCode)
  const missingChecks = card.mode === 'PUBLISH' ? productChecks
    .filter(check => !(card.code === 'SHOPIFY' ? check.shopifyOk ?? check.ok : check.ok))
    .map(check => ({ ...check, detail: card.code === 'SHOPIFY' ? check.shopifyDetail ?? check.detail : check.detail })) : []
  const mappingReadinessEnabled = card.mode === 'PUBLISH' && card.code !== 'SHOPIFY' && (!productId || !publication.isPending)
    && shouldCheckPublicationAttributes(categoryId, publication.data?.actualStatus, publication.data?.lastJobStatus)
  const mappingReadiness = useQuery({
    queryKey: ['publication-attribute-readiness', card.connection.id, categoryId, selectedAttributes],
    queryFn: async (): Promise<PublicationAttributeIssues> => {
      const connectionId = encodeURIComponent(card.connection.id)
      const categoryMapping = await readPublicationReadinessSource('Kategori eşlemesi', () => hubApi<{ externalId: string; snapshotId: string; status: string } | null>(`/mappings/categories/${categoryId}?connectionId=${connectionId}`))
      if (!categoryMapping || categoryMapping.status !== 'VERIFIED') {
        return { requiredIssues: [{ attribute: 'Kategori eşlemesi', detail: `Yayın öncesi güncel ${card.name} kategori eşlemesi gerekir.` }], optionalWarnings: [] }
      }

      const categoryReferences = await readPublicationReadinessSource(`${card.name} kategori listesi`, () => hubApi<{ snapshotId: string; items: Array<{ externalId: string; name: string; isActive: boolean; isLeaf: boolean }> }>(`/reference-data/categories?connectionId=${connectionId}`))
      const mappedCategory = categoryReferences.items.find(item => item.externalId === categoryMapping.externalId && item.isActive && item.isLeaf)
      if (categoryMapping.snapshotId !== categoryReferences.snapshotId || !mappedCategory) {
        return { requiredIssues: [{ attribute: 'Kategori eşlemesi', detail: `${card.name} kategori eşlemesi güncel değil; kategori eşlemesini yenileyin.` }], optionalWarnings: [] }
      }

      const categoryScope = categoryMapping.externalId
      const [attributeReferences, attributeMappings] = await Promise.all([
        readPublicationReadinessSource(`${card.name} kategori özellik listesi`, () => hubApi<{ snapshotId: string; items: Array<{ externalId: string; name: string; isActive: boolean; isRequired?: boolean | null; allowsCustomValue?: boolean | null }> }>(`/reference-data/categories/${encodeURIComponent(categoryScope)}/attributes?connectionId=${connectionId}`)),
        readPublicationReadinessSource('Yerel kategori özellik eşlemeleri', () => hubApi<PublicationMappingReference[]>(`/mappings/attributes?connectionId=${connectionId}&scopeExternalId=${encodeURIComponent(categoryScope)}`))
      ])
      const selectedIds = new Set(selectedAttributes.map(item => item.attributeId))
      const currentMappings = attributeMappings.filter(mapping => mapping.status === 'VERIFIED' && mapping.snapshotId === attributeReferences.snapshotId)
      const selectedExternalIds = [...new Set(currentMappings.filter(mapping => selectedIds.has(mapping.localId)).map(mapping => mapping.externalId))]
      const valueEntries = await Promise.all(selectedExternalIds.map(async externalAttributeId => {
        const valueScope = `${categoryScope}/${externalAttributeId}`
        const selectedAttribute = selectedAttributes.find(item => item.attributeId === currentMappings.find(mapping => mapping.externalId === externalAttributeId)?.localId)
        const attributeLabel = selectedAttribute?.name ?? externalAttributeId
        const mappings = await readPublicationReadinessSource(`“${attributeLabel}” yerel değer eşlemeleri`, () => hubApi<PublicationMappingReference[]>(`/mappings/attribute-values?connectionId=${connectionId}&scopeExternalId=${encodeURIComponent(valueScope)}`))
        let references: { snapshotId: string; items: Array<{ externalId: string; isActive: boolean }> }
        try {
          references = await readPublicationReadinessSource(`${card.name} “${attributeLabel}” özellik değer listesi`, () => hubApi<{ snapshotId: string; items: Array<{ externalId: string; isActive: boolean }> }>(`/reference-data/categories/${encodeURIComponent(categoryScope)}/attributes/${encodeURIComponent(externalAttributeId)}/values?connectionId=${connectionId}`))
        } catch (reason) {
          if (!(reason instanceof ApiRequestError && reason.code === 'REFERENCE_SNAPSHOT_UNAVAILABLE') && !(reason instanceof PublicationReadinessSourceError && reason.apiCode === 'REFERENCE_SNAPSHOT_UNAVAILABLE')) throw reason
          references = { snapshotId: '', items: [] }
        }
        return [externalAttributeId, { snapshotId: references.snapshotId, items: references.items, mappings }] as const
      }))
      const valueReferencesByAttribute: Record<string, PublicationValueReferenceSet> = Object.fromEntries(valueEntries)
      // Connection defaults (e.g. Menşei = TR) cover required attributes the product leaves empty.
      const attributeDefaults = await hubApi<Array<{ externalAttributeId: string }>>(`/connections/${connectionId}/attribute-defaults`).catch(() => [])
      // n11 fills its brand attribute (id 1) from the product brand, so it needs no mapping.
      const brandAttributeIds = card.connection.platformCode.trim().toUpperCase() === 'N11' ? ['1'] : []
      const result = classifyPublicationAttributeIssues({
        defaultedAttributeIds: [...attributeDefaults.map(item => item.externalAttributeId), ...brandAttributeIds],
        selectedAttributes,
        remoteAttributes: attributeReferences.items,
        attributeSnapshotId: attributeReferences.snapshotId,
        attributeMappings,
        platformName: card.name,
        valueReferencesByAttribute
      })
      return { ...result, categoryScope }
    },
    enabled: mappingReadinessEnabled,
    retry: false,
    staleTime: 15_000
  })
  const requiredMappingIssues = mappingReadinessEnabled ? mappingReadiness.data?.requiredIssues ?? [] : []
  const optionalMappingWarnings = mappingReadinessEnabled ? mappingReadiness.data?.optionalWarnings ?? [] : []
  const mappingCheckFailed = mappingReadinessEnabled && mappingReadiness.isError
  const mappingCheckPending = mappingReadinessEnabled && mappingReadiness.isPending
  const valueMappingFixes = mappingReadinessEnabled ? fixableValueMappings(mappingReadiness.data) : []
  const readinessCategoryScope = mappingReadinessEnabled ? mappingReadiness.data?.categoryScope : undefined
  const [valueFixOpen, setValueFixOpen] = useState(false)
  const [valueFixNotice, setValueFixNotice] = useState('')
  const blockedIssues = [
    ...missingChecks.map(check => ({ attribute: check.title, detail: check.detail })),
    ...(missingShopifyCapabilities.length ? [{ attribute: 'Shopify API izinleri', detail: `Ürün göndermeden önce Entegrasyonlar bölümünde Shopify erişimini yeniden yetkilendirin ve ${missingShopifyCapabilities.join(', ')} izinlerini doğrulayın.` }] : []),
    ...(card.code === 'SHOPIFY' && shopifyWriteCapabilities.isError ? [{ attribute: 'Shopify API izin kontrolü', detail: 'Shopify bağlantı izinleri okunamadı. Entegrasyonlar bölümünde bağlantıyı test edip izin kanıtlarını yenileyin.' }] : []),
    ...requiredMappingIssues,
    ...(mappingCheckFailed ? [{ attribute: `${card.name} özellik kontrolü`, detail: publicationReadinessFailureDetail(card.name, mappingReadiness.error) }] : [])
  ]
  const selectionBlocked = blockedIssues.length > 0 || mappingCheckPending || shopifyCapabilityCheckPending
  const issueListId = `publish-issues-${card.connection.id}`
  const issueDescriptionIds = [
    blockedIssues.length ? `${issueListId}-required` : '',
    mappingCheckPending ? `${issueListId}-pending` : '',
    shopifyCapabilityCheckPending ? `${issueListId}-shopify-pending` : '',
    optionalMappingWarnings.length ? `${issueListId}-optional` : ''
  ].filter(Boolean).join(' ') || undefined
  const hasRequiredPublicationBlock = blockedIssues.length > 0 && !alreadyPublished
  // A product that is not saved yet has no platform record: it shows what keeps it from publishing, or that it is not out yet.
  const displayedPublicationLabel = hasRequiredPublicationBlock ? 'Yayın için zorunlu eksik' : productId ? publicationLabel : 'Henüz yayınlanmadı'
  const displayedPublicationTone = hasRequiredPublicationBlock ? 'danger' : productId ? publicationTone : 'neutral'
  const readOnlyIsN11 = card.connection.platformCode.trim().toUpperCase() === 'N11'
  const readOnlyTitle = readOnlyIsN11 ? 'N11 ürün açma için Entegrasyonlar bölümünde N11 bağlantısının dış yazma anahtarını açın.' : 'Shopify ürün yayını için Entegrasyonlar bölümünde dış yazma anahtarını açın ve gerekli API izinlerini doğrulayın.'
  return <article className={`publish-platform-card${selected ? ' selected' : ''}${card.mode === 'READ_ONLY' ? ' is-read-only' : ''}`}>
    <button type="button" className="publish-platform-card-head" onClick={onSelect} aria-pressed={selected} aria-describedby={issueDescriptionIds} title={card.mode === 'READ_ONLY' ? readOnlyTitle : alreadyPublished ? 'Ürün bu platformda zaten yayında.' : undefined} disabled={card.mode === 'READ_ONLY' || isPublicationSelectionDisabled(publication.data?.actualStatus, selected, publicationCheckPending, publicationCheckUnavailable, selectionBlocked)}>
      <span className={`publish-platform-mark ${card.tone}`}><img className={`publish-platform-logo ${platformLogoClass(card.connection.platformCode)}`} src={platformLogoSource(card.connection.platformCode) ?? '/platforms/trendyol.webp'} alt="" aria-hidden="true" /></span>
      <span><strong>{card.name}</strong><small>{card.mode === 'READ_ONLY' ? 'Dış yazma kapalı · yayın kuyruğu devre dışı' : alreadyPublished ? 'Zaten yayında · yeniden seçilemez' : publicationCheckPending ? 'Yayın durumu kontrol ediliyor…' : publicationCheckUnavailable ? 'Yayın durumu doğrulanamadı' : shopifyCapabilityCheckPending ? 'Shopify yazma izinleri kontrol ediliyor…' : selected ? 'Yayın için seçildi' : mappingCheckPending ? 'Zorunlu alanlar kontrol ediliyor…' : selectionBlocked ? 'Zorunlu eksikler giderilmeden seçilemez' : 'Yayın için seçilmedi'}</small></span>
      <i className={`publish-platform-toggle ${selected ? 'on' : ''}`} aria-hidden="true"><b /></i>
    </button>
    <dl className="publish-platform-facts">
      <div><dt>Mağaza</dt><dd title={card.connection.externalStoreId ? `Mağaza no: ${card.connection.externalStoreId}` : undefined}>{card.connection.displayName || card.connection.externalStoreId || '—'}</dd></div>
      <div><dt>Platform</dt><dd>{publishPlatformName(card.connection.platformCode)}</dd></div>
      <div><dt>Bağlantı</dt><dd><span className="publish-platform-status active"><i aria-hidden="true" />Aktif bağlantı</span></dd></div>
      {(productId || card.mode !== 'READ_ONLY') && <div className="publish-platform-fact-publication" aria-live="polite"><dt>Yayın durumu</dt><dd><span className={`publish-platform-status status-${displayedPublicationTone}`} tabIndex={hasRequiredPublicationBlock ? 0 : undefined} aria-describedby={hasRequiredPublicationBlock ? `${issueListId}-required` : undefined} onMouseEnter={placePublishIssueTooltip} onFocus={placePublishIssueTooltip}><i aria-hidden="true" />{displayedPublicationLabel}</span>{hasRequiredPublicationBlock && <span className="publish-issue-tooltip" role="tooltip"><strong>{blockedIssues.length === 1 ? 'Yayın için 1 eksik var' : `Yayın için ${blockedIssues.length} eksik var`}</strong><ul>{blockedIssues.slice(0, 8).map(issue => <li key={`${issue.attribute}:${issue.detail}`}><b>{issue.attribute}</b> {issue.detail}</li>)}{blockedIssues.length > 8 && <li>Ve {blockedIssues.length - 8} eksik daha.</li>}</ul></span>}{publication.data?.profileId && publication.data.actualStatus && publication.data.actualStatus !== 'UNKNOWN' && <button type="button" className="publish-tracking-action" aria-label={`${card.name} yayın durumunu güncelle`} title="Platformdaki mevcut durumu yeniden sorgula" disabled={refreshPublication.isPending || isPublicationStatusJobRunning(publication.data.lastJobStatus)} onClick={() => refreshPublication.mutate()}><UiIcon name="refresh" /></button>}</dd>{publicationNote && <small>{publicationNote}</small>}</div>}
    </dl>
    {valueMappingFixes.length > 0 && readinessCategoryScope && <button type="button" className="publish-platform-fix-mappings" onClick={() => setValueFixOpen(true)}>Eksik değer eşlemelerini tamamla ({valueMappingFixes.length})</button>}
    {valueFixOpen && readinessCategoryScope && <MissingValueMappingDialog connectionId={card.connection.id} platformName={card.name} categoryScope={readinessCategoryScope} fixes={valueMappingFixes} onClose={() => setValueFixOpen(false)} onSaved={message => { setValueFixNotice(message); appendNotification(message, 'success') }} />}
    {valueFixNotice && <NoticeToast kind="success" message={valueFixNotice} onClose={() => setValueFixNotice('')} durationMs={5500} />}
    {card.mode === 'READ_ONLY' && <div className="publish-platform-warning" role="note"><UiIcon name="alert" /><div><strong>Shopify dış yazması kapalı</strong><p>Ürün yayınını açmak için Entegrasyonlar bölümünde Shopify dış yazma anahtarını etkinleştirin; bağlantı testinde write_products, write_inventory, read_publications ve write_publications izinleri doğrulanmalıdır.</p></div></div>}
    {shopifyCapabilityCheckPending && <span id={`${issueListId}-shopify-pending`} className="sr-only">Shopify izinleri kontrol ediliyor.</span>}
    {productId && publication.data?.lastJobId && <PublicationJobProgress jobId={publication.data.lastJobId} productId={productId} connectionId={card.connection.id} />}
    {blockedIssues.length > 0 && <span id={`${issueListId}-required`} className="sr-only">{blockedIssues.map(issue => `${issue.attribute}: ${issue.detail}`).join('. ')}</span>}
    {mappingCheckPending && <span id={`${issueListId}-pending`} className="sr-only">Yayın gereksinimleri kontrol ediliyor. Güncel zorunlu özellik eşlemeleri doğrulanıyor.</span>}
    {optionalMappingWarnings.length > 0 && <div id={`${issueListId}-optional`} className="publish-platform-warning" role="status" aria-label={`${card.name} isteğe bağlı özellik uyarıları`}><UiIcon name="alert" /><div><strong>{optionalMappingWarnings.length === 1 ? 'İsteğe bağlı özellik uyarısı' : `${optionalMappingWarnings.length} isteğe bağlı özellik uyarısı`}</strong><ul>{optionalMappingWarnings.map((issue, index) => <li key={`${issue.attribute}-${index}`}><b>{issue.attribute}:</b> {issue.detail}</li>)}</ul></div></div>}
    {variantMediaWarning && <div className="publish-platform-warning" role="status" aria-label={`${card.name} varyant görsel uyarısı`}><UiIcon name="alert" /><div><strong>Varyant görselleri seçilmedi</strong><p>{variantMediaWarning}</p></div></div>}
  </article>
}

function PublicationJobProgress({ jobId, productId, connectionId }: { jobId: string; productId: string; connectionId: string }) {
  const client = useQueryClient()
  const queryKey = ['publication-job', jobId] as const
  const job = useQuery({
    queryKey,
    queryFn: () => hubApi<PublicationJobDetail>(`/jobs/${jobId}`, { cache: 'no-store' }),
    refetchInterval: query => {
      const status = query.state.data?.job.status.trim().toUpperCase()
      return status && ['SUCCEEDED', 'DEAD', 'CANCELLED'].includes(status) ? false : 2500
    },
    refetchIntervalInBackground: true,
    refetchOnWindowFocus: true,
    staleTime: 0,
    retry: false
  })
  const cancel = useMutation({
    mutationFn: () => hubApi<PublicationJobDetail>(`/jobs/${jobId}/cancel`, {
      method: 'POST',
      headers: { 'Idempotency-Key': key() },
      body: '{}'
    }),
    onSuccess: async result => {
      client.setQueryData(queryKey, result)
      await Promise.all([
        client.invalidateQueries({ queryKey: ['publication-status', productId, connectionId] }),
        client.invalidateQueries({ queryKey })
      ])
    },
    onError: reason => appendNotification(reason instanceof Error ? reason.message : 'Yayın işlemi durdurulamadı.', 'error')
  })
  if (job.isPending) return <div className="publish-platform-job" role="status">Yayın işlemi bilgisi yükleniyor…</div>
  if (job.isError || !job.data?.job) return <div className="publish-platform-job" role="status">Yayın işlemi ilerlemesi alınamadı.</div>

  const detail = job.data.job
  const status = detail.status.trim().toUpperCase()
  const terminal = ['SUCCEEDED', 'DEAD', 'CANCELLED'].includes(status)
  const retryScheduled = ['RETRY_SCHEDULED', 'RETRYABLE_FAILURE'].includes(status)
  const percent = detail.progressPercent == null ? null : Math.max(0, Math.min(100, detail.progressPercent))
  const progressText = percent !== null
    ? `${percent}%${detail.progressTotal == null ? '' : ` · ${detail.progressCurrent.toLocaleString('tr-TR')} / ${detail.progressTotal.toLocaleString('tr-TR')}`}`
    : detail.progressTotal == null ? null : `${detail.progressCurrent.toLocaleString('tr-TR')} / ${detail.progressTotal.toLocaleString('tr-TR')}`
  return <div className={`publish-platform-job ${terminal ? `is-${status.toLowerCase()}` : 'is-active'}`} role="status" aria-live="polite">
    <div className="publish-platform-job-heading"><strong>{detail.progressLabel || 'Yayın işlemi'}</strong>{!retryScheduled && <span>{statusLabel(status)}</span>}</div>
    {!terminal && <div className={`publish-platform-job-progress${percent === null ? ' is-indeterminate' : ''}`} role="progressbar" aria-label="Yayın işlemi ilerlemesi" aria-valuemin={0} aria-valuemax={100} {...(percent === null ? {} : { 'aria-valuenow': percent })}><span style={percent === null ? undefined : { width: `${percent}%` }} /></div>}
    {(progressText || detail.lastErrorSummary) && <small>{detail.lastErrorSummary || progressText}</small>}
    {!terminal && <div className="publish-platform-job-actions"><small>Durdurma, platforma gönderilmiş bir isteği geri alamayabilir.</small><button type="button" className="secondary" disabled={cancel.isPending} onClick={() => cancel.mutate()}>{cancel.isPending ? 'Durduruluyor…' : 'İşlemi durdur'}</button></div>}
  </div>
}

export function NewProductPage({ editProductId }: { editProductId?: string } = {}) {
  const client = useQueryClient();
  const location = useLocation()
  const navigate = useNavigate()
  const routeState = location.state as { duplicateProductId?: unknown } | null
  const duplicateProductId = typeof routeState?.duplicateProductId === 'string' ? routeState.duplicateProductId : undefined
  const [error, setError] = useState<unknown>(); const [, setNotice] = useState(''); const [feedback, setFeedback] = useState<OperationFeedback | null>(null); const [submitting, setSubmitting] = useState(false); const [mediaAutoSaveTick, setMediaAutoSaveTick] = useState(0);const [calculateDesi, setCalculateDesi] = useState(false); const [desiCalculatorOpen, setDesiCalculatorOpen] = useState(false)
  const [form, setForm] = useState({ title: '', description: '', brandId: '', categoryId: '', baseSku: '', barcode: '', modelCode: '', weight: '', width: '', length: '', height: '', desi: '1', listPrice: '699.90', salePrice: '549.90', costPrice: '0', currency: 'TRY', vatRate: '10', vatIncluded: 'INCLUDED', initialStock: '0', safetyStock: '0', mediaUrls: '', status: 'ACTIVE' })
  const [attributeSelections, setAttributeSelections] = useState<Record<string, string[]>>({}); const [attributeTextValues, setAttributeTextValues] = useState<Record<string, string>>({}); const [variantAttributeIds, setVariantAttributeIds] = useState<string[]>([]); const [variantRows, setVariantRows] = useState<VariantDraft[]>([]); const [variantFilterSelections, setVariantFilterSelections] = useState<VariantFilterSelections>({}); const [variantFilterOpen, setVariantFilterOpen] = useState(false); const [draggedVariantKey, setDraggedVariantKey] = useState<string | null>(null); const [dragOverVariantKey, setDragOverVariantKey] = useState<string | null>(null); const [selectedChannelIds, setSelectedChannelIds] = useState<string[]>([]); const [channelPricing, setChannelPricing] = useState<Record<string, ChannelPricingDraft>>({}); const [variantChannelPricing, setVariantChannelPricing] = useState<Record<string, ChannelPricingDraft>>({})
  const [automaticBarcodeGeneration, setAutomaticBarcodeGeneration] = useState(false)
  const [bulkOptionSyncOpen, setBulkOptionSyncOpen] = useState(false)
  const [bulkOptionSyncSelection, setBulkOptionSyncSelection] = useState<string[]>([])
  const [attributeValueQueries, setAttributeValueQueries] = useState<Record<string, string>>({})
  const initializedEditProductKey = useRef<string | null>(null)
  const initializedEditOptionsKey = useRef<string | null>(null)
  const initializedEditWebColorKey = useRef<string | null>(null)
  const [wizardStep, setWizardStep] = useState<1 | 2>(1)
  const [platformUpdateDialogOpen, setPlatformUpdateDialogOpen] = useState(false)
  const [platformUpdateConnectionIds, setPlatformUpdateConnectionIds] = useState<string[]>([])
  const [platformUpdateProductInformation, setPlatformUpdateProductInformation] = useState(true)
  const pendingPlatformUpdate = useRef<ProductUpdateRequest | null>(null)
  const [lightboxImage, setLightboxImage] = useState<{ url: string; title: string } | null>(null)
  const [variantMediaModal, setVariantMediaModal] = useState<VariantMediaModalState | null>(null)
  const [barcodeSkuMenuOpen, setBarcodeSkuMenuOpen] = useState(false)
  const barcodeSkuActionRef = useRef<HTMLDivElement>(null)
  const [barcodePasteMenuOpen, setBarcodePasteMenuOpen] = useState(false)
  // Phones show the option column beside one group of fields at a time; the other columns stay in the grid, hidden.
  const [variantMobileGroup, setVariantMobileGroup] = useState<'codes' | 'stock' | 'cost' | 'actions'>('stock')
  const barcodePasteActionRef = useRef<HTMLDivElement>(null)
  const [pendingVariantCodeGeneration, setPendingVariantCodeGeneration] = useState<{ target: 'barcode' | 'sku'; codes: string[] } | null>(null)
  const variantRowsRef = useRef(variantRows)
  variantRowsRef.current = variantRows
  const [expandedOptionGroupIds, setExpandedOptionGroupIds] = useState<Record<string, boolean>>({})
  const [bulkStock, setBulkStock] = useState(''); const [bulkSalePrice, setBulkSalePrice] = useState(''); const [bulkCostPrice, setBulkCostPrice] = useState(''); const [bulkListPrice, setBulkListPrice] = useState('')
  const [variantBulkEdit, setVariantBulkEdit] = useState<{ field: VariantBulkEditField; value: string; error: string } | null>(null)
  const [variantBulkEditFilterSelections, setVariantBulkEditFilterSelections] = useState<VariantFilterSelections>({})
  const [mediaFiles, setMediaFiles] = useState<PendingProductMediaFile[]>([])
  const [deletingFamilyMediaKey, setDeletingFamilyMediaKey] = useState<string | null>(null)
  const [deletingProductMediaKey, setDeletingProductMediaKey] = useState<string | null>(null)
  const [mediaRemovalConfirmation, setMediaRemovalConfirmation] = useState<MediaRemovalConfirmation | null>(null)
  const [familyMediaOrder, setFamilyMediaOrder] = useState<string[]>([])
  const [familyMediaOrderDirty, setFamilyMediaOrderDirty] = useState(false)
  const [draggedMedia, setDraggedMedia] = useState<{ kind: 'file' | 'url'; index: number } | null>(null); const [dragOverMedia, setDragOverMedia] = useState<{ kind: 'file' | 'url'; index: number } | null>(null)
  const [pointerDraggedVariantKey, setPointerDraggedVariantKey] = useState<string | null>(null)
  // Touch screens have no drag and drop: holding a picture for a moment picks it up, sliding moves it and lifting the
  // finger drops it on the picture underneath. Moving before the hold completes scrolls the page as usual; while a
  // picture is held the page does not scroll (a non-passive touchmove stops it).
  // On touch screens a long press on a picture would start the browser's own drag (which scrolls the page towards the
  // edge it nears); the pictures are reordered by the press-and-hold handling below instead.
  const touchPointer = useMediaQuery('(pointer: coarse)')
  const mediaTouch = useRef<{ kind: 'file' | 'url'; index: number; target: number; x: number; y: number; lastY: number; timer: number; dragging: boolean; scroller: Element | null } | null>(null)
  const suppressMediaClick = useRef(false)
  const mediaStripCleanup = useRef<(() => void) | null>(null)
  const mediaStripRef = useCallback((node: HTMLDivElement | null) => {
    mediaStripCleanup.current?.()
    mediaStripCleanup.current = null
    if (!node) return
    const hold = (event: TouchEvent) => { if (mediaTouch.current?.dragging) event.preventDefault() }
    node.addEventListener('touchmove', hold, { passive: false })
    mediaStripCleanup.current = () => node.removeEventListener('touchmove', hold)
  }, [])
  const pointerDraggedVariantRef = useRef<string | null>(null)
  const pointerDragSourceRef = useRef<HTMLDivElement | null>(null)
  const pointerDragIdRef = useRef<number | null>(null)
  const [removedVariantIds, setRemovedVariantIds] = useState<string[]>([])
  const [variantPlatformPricing, setVariantPlatformPricing] = useState<{ rowKey: string; platform: VariantPlatformStatus } | null>(null)
  const [variantPlatformPricingDraft, setVariantPlatformPricingDraft] = useState<ChannelPricingDraft>({ listPrice: '', salePrice: '' })
  const [variantPlatformPricingSaving, setVariantPlatformPricingSaving] = useState(false)
  const initialEditMediaUrl = useRef('')
  const initialEditModelCode = useRef('')
  const initialEditVariantMediaRefs = useRef<Record<string, string[]>>({})
  useEffect(() => {
    if (!barcodeSkuMenuOpen && !barcodePasteMenuOpen) return
    function closeBarcodeMenus(event: PointerEvent) {
      const target = event.target as Node
      if (target instanceof Element && target.closest('[data-variant-header-popover="true"]')) return
      if (!barcodeSkuActionRef.current?.contains(target) && !barcodePasteActionRef.current?.contains(target)) {
        setBarcodeSkuMenuOpen(false)
        setBarcodePasteMenuOpen(false)
      }
    }
    function closeOnEscape(event: KeyboardEvent) {
      if (event.key === 'Escape') { setBarcodeSkuMenuOpen(false); setBarcodePasteMenuOpen(false) }
    }
    document.addEventListener('pointerdown', closeBarcodeMenus)
    document.addEventListener('keydown', closeOnEscape)
    return () => { document.removeEventListener('pointerdown', closeBarcodeMenus); document.removeEventListener('keydown', closeOnEscape) }
  }, [barcodeSkuMenuOpen, barcodePasteMenuOpen])
  useEffect(() => {
    if (!pendingVariantCodeGeneration) return
    function closeCodeGenerationOnEscape(event: KeyboardEvent) {
      if (event.key === 'Escape') setPendingVariantCodeGeneration(null)
    }
    document.addEventListener('keydown', closeCodeGenerationOnEscape)
    return () => document.removeEventListener('keydown', closeCodeGenerationOnEscape)
  }, [pendingVariantCodeGeneration])
  function showFeedback(message: string, kind: OperationFeedback['kind'], options: { persistent?: boolean } = {}) {
    setFeedback({ message, kind, ...options })
  }
  function handleMediaFiles(files: File[]) {
    const accepted: File[] = []
    const rejected: string[] = []
    for (const file of files) {
      const isImage = file.type === 'image/jpeg' || file.type === 'image/png'
      if (!isImage) { rejected.push(`${file.name}: yalnız JPEG veya PNG kabul edilir.`); continue }
      if (file.size <= 0 || file.size > MAX_PRODUCT_MEDIA_BYTES) { rejected.push(`${file.name}: dosya başına en fazla 6 MB olabilir.`); continue }
      accepted.push(file)
    }
    if (accepted.length) setMediaFiles(current => [...current, ...accepted.map(file => ({ id: crypto.randomUUID(), file }))])
    if (rejected.length) {
      const message = rejected.length === 1 ? rejected[0] : `${rejected.length} görsel eklenemedi. Dosya türü ve 6 MB sınırını kontrol edin.`
      setNotice(message); showFeedback(message, 'error')
    } else if (accepted.length) {
      const message = `${accepted.length} görsel seçildi.`; setNotice(message); showFeedback(message, 'info')
    }
  }
  const productToEdit = useQuery({ queryKey: ['product', editProductId], queryFn: () => hubApi<Product>(`/products/${editProductId}`), enabled: !!editProductId })
  const productToCopy = useQuery({ queryKey: ['product', duplicateProductId], queryFn: () => hubApi<Product>(`/products/${duplicateProductId}`), enabled: !editProductId && Boolean(duplicateProductId) })
  const seedProduct = editProductId ? productToEdit.data : productToCopy.data
  const categories = useQuery({ queryKey: ['categories', 'new-product'], queryFn: () => loadAllPages<Category>('/catalog/categories') })
  const brands = useQuery({ queryKey: ['brands', 'new-product'], queryFn: () => loadAllPages<Brand>('/catalog/brands') })
  const connections = useQuery({ queryKey: ['connections', 'new-product'], queryFn: () => loadAllPages<MarketplaceConnection>('/connections') })
  const requirements = useQuery({ queryKey: ['category-requirements', form.categoryId], queryFn: () => hubApi<CategoryRequirement[]>(`/catalog/categories/${form.categoryId}/attribute-requirements`), enabled: !!form.categoryId, retry: false })
  const leafCategories = (categories.data?.items ?? []).filter(item => item.isLeaf && item.isActive); const activeBrands = (brands.data?.items ?? []).filter(item => item.isActive)
  const fallbackListPrice = Number(form.listPrice || 0); const fallbackSalePrice = Number(form.salePrice || 0); const fallbackCostPrice = Number(form.costPrice || 0); const initialStock = Number(form.initialStock || 0)
  const variantStockTotal = variantRows.reduce((total, row) => total + Number(row.stock || 0), 0)
  const desi = useMemo(() => { const width = Number(form.width); const length = Number(form.length); const height = Number(form.height); return width > 0 && length > 0 && height > 0 ? width * length * height / 3000 : 0 }, [form.width, form.length, form.height])
  const mediaUrls = useMemo(() => form.mediaUrls.split(/\r?\n|[;|]/).map(item => item.trim()).filter(Boolean), [form.mediaUrls])

  const allRequirements = useMemo(() => (requirements.data ?? []).slice().sort((a, b) => a.displayOrder - b.displayOrder), [requirements.data])
  // A category requirement is usable in the product editor when its mapped
  // local attribute has active values or the marketplace accepts a custom
  // value. The latter keeps required text fields (for example a free-form
  // color field) available for entry from the product form.
  const mappedRequirements = useMemo(() => allRequirements.filter(item => item.attribute.values.length > 0 || item.allowsCustomValue), [allRequirements])
  const colorOptionRequirement = useMemo(() => mappedRequirements.find(item => !isWebColorOptionName(item.attribute.name) && isColorOptionName(item.attribute.name) && isOptionRequirement(item)), [mappedRequirements])
  // Web Color is backed by the local Renk option. Prefer the explicit
  // category mapping marker, but keep the editor usable when a category has
  // Renk configured before its Web Color mapping is saved.
  const webColorRequirement = useMemo(() => mappedRequirements.find(item => (item.isWebColor === true || isWebColorOptionName(item.attribute.name)) && item.attribute.values.length > 0) ?? colorOptionRequirement, [colorOptionRequirement, mappedRequirements])
  const webColorValues = webColorRequirement?.attribute.values ?? colorOptionRequirement?.attribute.values ?? []
  const optionRequirements = useMemo(() => mappedRequirements.filter(item => !isWebColorOptionName(item.attribute.name) && isOptionRequirement(item)).slice(0, 2), [mappedRequirements])
  const bulkSyncOptionRequirements = useMemo(() => optionRequirements.filter(item => isColorOptionName(item.attribute.name) || isSizeOptionName(item.attribute.name)), [optionRequirements])
  const [webColorAutoEnabled, setWebColorAutoEnabled] = useState(true)
  const [manualWebColorValueId, setManualWebColorValueId] = useState('')
  useEffect(() => {
    const optionIds = optionRequirements.map(item => item.attributeId)
    setVariantAttributeIds(current => current.filter(id => optionIds.includes(id)))
  }, [optionRequirements])
  const visibleOptionRequirements = optionRequirements

  useLayoutEffect(() => {
    const product = seedProduct
    if (!product) return
    const productKey = `${editProductId ? 'edit' : 'copy'}:${product.id}:${product.version}`
    if (initializedEditProductKey.current === productKey) return
    initializedEditProductKey.current = productKey
    const primary = product.variants[0]
    const initialModelCode = primary?.modelCode ?? product.modelCode ?? ''
    const sortedVariants = sortVariantsAlphabetically(product.variants)
    const savedMediaUrls = orderMediaUrlsByVariants(product.variants, product.mediaUrls ?? [], product.primaryImageUrl, product.hasCustomMediaOrder)
    const savedFamilyMediaUrls = product.familyMediaItems?.map(item => item.url) ?? product.familyMediaUrls ?? []
    const initialListPrice = product.defaultListPrice ?? primary?.listPrice ?? primary?.salePrice ?? 0
    const initialSalePrice = product.defaultSalePrice ?? primary?.salePrice ?? 0
    initialEditModelCode.current = initialModelCode.trim()
    setForm({ title: product.title, description: product.description ?? '', brandId: product.brandId ?? '', categoryId: product.categoryId ?? '', baseSku: primary?.sku ?? '', barcode: primary?.barcode ?? '', modelCode: initialModelCode, weight: String(primary?.weight ?? ''), width: String(primary?.width ?? ''), length: String(primary?.length ?? ''), height: String(primary?.height ?? ''), desi: String(primary?.desi ?? 1), listPrice: String(initialListPrice), salePrice: String(initialSalePrice), costPrice: String(primary?.costPrice ?? 0), currency: primary?.currency ?? 'TRY', vatRate: String(primary?.vatRate ?? 10), vatIncluded: primary?.vatInclusion ?? 'INCLUDED', initialStock: String(primary?.onHand ?? 0), safetyStock: String(primary?.safetyStock ?? 0), mediaUrls: savedMediaUrls.join('\n'), status: product.status || 'ACTIVE' })
    if (!editProductId && duplicateProductId) setChannelPricing(Object.fromEntries((primary?.platformStatuses ?? []).flatMap(platform => platform.connectionId ? [[platform.connectionId, { listPrice: String(platform.listPrice ?? initialListPrice), salePrice: String(platform.salePrice ?? initialSalePrice) }] as const] : [])))
    initialEditMediaUrl.current = savedMediaUrls.join('\n')
    setFamilyMediaOrder(uniqueMediaUrls(product.familyOrderedMediaUrls?.length
      ? product.familyOrderedMediaUrls
      : [...savedMediaUrls, ...savedFamilyMediaUrls]))
    setFamilyMediaOrderDirty(false)
    setMediaFiles([])
    const seededMediaRefs = seedVariantMediaRefs(product.variants)
    const mediaRefsByVariantId = new Map(product.variants.map((variant, index) => [variant.id, seededMediaRefs[index] ?? []]))
    initialEditVariantMediaRefs.current = Object.fromEntries(mediaRefsByVariantId)
    setVariantRows(sortedVariants.map(variant => {
      const options = Object.fromEntries(variantOptionEntries(variant).map(option => [option.name, option.value]))
      const persistedAttributes = variant.attributes ?? []
      const attributeValueIds = Object.fromEntries(persistedAttributes.flatMap(attribute => attribute.valueId ? [[attribute.attributeId, attribute.valueId] as const] : []))
      return { key: variant.id, optionSignature: optionSignatureFromOptions(options) || (variant.optionSignature && variant.optionSignature !== '-' ? variant.optionSignature : 'Tek Ürün'), options, attributeValueIds, persistedAttributes, sku: variant.sku, barcode: variant.barcode ?? '', stock: variant.onHand, salePrice: variant.defaultSalePrice ?? product.defaultSalePrice ?? variant.salePrice ?? 0, listPrice: variant.defaultListPrice ?? product.defaultListPrice ?? variant.listPrice ?? variant.salePrice ?? 0, costPrice: variant.costPrice ?? 0, mediaRefs: mediaRefsByVariantId.get(variant.id) ?? [], ...(editProductId ? { platformStatuses: variant.platformStatuses ?? [] } : {}) }
    }))
    const selected: Record<string, string[]> = {}; const typed: Record<string, string> = {}
    for (const attribute of product.attributes ?? []) { if (attribute.valueId) selected[attribute.attributeId] = [...(selected[attribute.attributeId] ?? []), attribute.valueId]; else if (attribute.textValue != null) typed[attribute.attributeId] = attribute.textValue; else if (attribute.numberValue != null) typed[attribute.attributeId] = String(attribute.numberValue); else if (attribute.booleanValue != null) typed[attribute.attributeId] = attribute.booleanValue ? 'evet' : 'hayır' }
    setAttributeSelections(selected); setAttributeTextValues(typed); setVariantAttributeIds([])
  }, [editProductId, seedProduct?.id, seedProduct?.version])

  useEffect(() => {
    const product = seedProduct
    if ((!editProductId && !duplicateProductId) || !product || !webColorRequirement || requirements.isLoading) return
    const productKey = `${editProductId ? 'edit' : 'copy'}:${product.id}:${product.version}`
    if (initializedEditWebColorKey.current === productKey) return
    initializedEditWebColorKey.current = productKey
    const savedManualColor = product.attributes?.find(item => item.attributeId === webColorRequirement.attributeId && item.valueId)
    setWebColorAutoEnabled(!savedManualColor)
    setManualWebColorValueId(savedManualColor?.valueId ?? '')
  }, [duplicateProductId, editProductId, requirements.isLoading, seedProduct, webColorRequirement])

  useEffect(() => {
    const product = seedProduct
    if ((!editProductId && !duplicateProductId) || !product || !allRequirements.length || requirements.isLoading) return
    const productKey = `${editProductId ? 'edit' : 'copy'}:${product.id}:${product.version}`
    if (initializedEditOptionsKey.current === productKey) return
    initializedEditOptionsKey.current = productKey
    const inferred: Record<string, string[]> = {}
    for (const requirement of optionRequirements) {
      const valuesInVariants = product.variants.flatMap(variant => variantOptionEntries(variant)
        .filter(option => normalizeVariantOptionName(option.name) === normalizeVariantOptionName(requirement.attribute.name))
        .map(option => option.value))
      const ids = matchingVariantOptionValues(requirement.attribute.values, valuesInVariants).map(value => value.id)
      if (ids.length) inferred[requirement.attributeId] = ids
    }
    setAttributeSelections(current => ({ ...current, ...inferred }))
    setVariantAttributeIds(Object.keys(inferred))
    setVariantRows(rows => rows.map(row => {
      const source = product.variants.find(variant => variant.id === row.key)
      if (!source) return row
      const options = Object.fromEntries(variantOptionEntries(source).map(option => [option.name, option.value]))
      const attributeValueIds = Object.fromEntries(optionRequirements.flatMap(requirement => {
        const option = variantOptionEntries(source).find(item => normalizeVariantOptionName(item.name) === normalizeVariantOptionName(requirement.attribute.name))
        const value = matchingVariantOptionValues(requirement.attribute.values, option ? [option.value] : [])[0]
        return value ? [[requirement.attributeId, value.id]] : []
      }))
      return { ...row, optionSignature: optionSignatureFromOptions(options) || (source.optionSignature && source.optionSignature !== '-' ? source.optionSignature : row.optionSignature), options, attributeValueIds }
    }))
  }, [allRequirements, duplicateProductId, editProductId, optionRequirements, requirements.isLoading, seedProduct])

  function updateField(name: keyof typeof form, value: string) { setForm(current => ({ ...current, [name]: value })) }
  function toggleAttributeValue(attributeId: string, valueId: string) {
    const requirement = mappedRequirements.find(item => item.attributeId === attributeId)
    const alreadySelected = (attributeSelections[attributeId] ?? []).includes(valueId)
    if (!alreadySelected && requirement && isOptionRequirement(requirement) && !variantAttributeIds.includes(attributeId) && variantAttributeIds.length >= 2) {
      const message = 'Bir ürün en fazla 2 seçenek grubuyla varyantlanabilir.'; setNotice(message); showFeedback(message, 'error'); return
    }
    setAttributeSelections(current => {
      const next = toggleProductAttributeValue(current, attributeId, valueId, Boolean(requirement && !isOptionRequirement(requirement)))
      const nextValues = next[attributeId] ?? []
      if (requirement && isOptionRequirement(requirement)) {
        setVariantAttributeIds(currentAxes => nextValues.length ? currentAxes.includes(attributeId) ? currentAxes : [...currentAxes, attributeId] : currentAxes.filter(id => id !== attributeId))
      }
      return next
    })
  }

  function toggleWebColorAuto(enabled: boolean) {
    setWebColorAutoEnabled(enabled)
    if (enabled) {
      setManualWebColorValueId('')
      return
    }
    const selected = webColorRequirement ? attributeSelections[webColorRequirement.attributeId] ?? [] : []
    setManualWebColorValueId(current => current || selected[0] || webColorValues[0]?.id || '')
  }
  function generateVariants(attributeIds = variantAttributeIds, preserveExistingDetails = false) {
    try {
      if (automaticBarcodeGeneration && !form.modelCode.trim()) {
        const message = 'Otomatik barkod oluşturmak için önce model kodunu girin.'; setNotice(message); showFeedback(message, 'error')
        return false
      }
      const generated = buildVariantMatrix(mappedRequirements, attributeIds, attributeSelections, form.baseSku || form.modelCode || form.title, fallbackListPrice, fallbackSalePrice, fallbackCostPrice, initialStock, automaticBarcodeGeneration, form.modelCode)
      if (!generated.length) {
        const message = 'Önce varyant olacak özellikleri ve bu özelliklerin değerlerini seçin.'; setNotice(message); showFeedback(message, 'error')
        return false
      }
      setVariantRows(current => {
        if (current.length === 1 && current[0].optionSignature.trim().toLocaleLowerCase('tr-TR') === 'tek ürün' && generated.length === 1) {
          const existing = current[0]
          const [next] = generated
          return [{ ...next, key: existing.key, ...(automaticBarcodeGeneration && !preserveExistingDetails ? {} : { barcode: existing.barcode || next.barcode, sku: existing.sku || next.sku, salePrice: existing.salePrice, listPrice: existing.listPrice }), stock: existing.stock, costPrice: existing.costPrice, mediaRefs: existing.mediaRefs }]
        }
        const existingMap = new Map(current.map(row => [variantSignatureKey(row.optionSignature), row]))
        const knownBarcodes = new Set(current.map(row => row.barcode.trim()).filter(Boolean))
        let nextBarcodeSequence = current.length + 1
        const merged = generated.map(gen => {
          const signatureKey = variantSignatureKey(gen.optionSignature)
          const match = existingMap.get(signatureKey)
          if (match) {
            existingMap.delete(signatureKey)
            return automaticBarcodeGeneration && !preserveExistingDetails ? { ...match, sku: gen.sku, barcode: gen.barcode, salePrice: gen.salePrice, listPrice: gen.listPrice } : match
          }
          if (automaticBarcodeGeneration && preserveExistingDetails) {
            let defaults = buildVariantGenerationDefaults({ baseSku: form.baseSku || form.modelCode || form.title, modelCode: form.modelCode, sequence: nextBarcodeSequence++, automaticBarcodes: true, fallbackSalePrice, fallbackListPrice })
            while (knownBarcodes.has(defaults.barcode)) defaults = buildVariantGenerationDefaults({ baseSku: form.baseSku || form.modelCode || form.title, modelCode: form.modelCode, sequence: nextBarcodeSequence++, automaticBarcodes: true, fallbackSalePrice, fallbackListPrice })
            knownBarcodes.add(defaults.barcode)
            return { ...gen, ...defaults }
          }
          return gen
        })
        const sortedRows = sortVariantsAlphabetically([...merged, ...Array.from(existingMap.values())])
        return automaticBarcodeGeneration && !preserveExistingDetails
          ? sortedRows.map((row, index) => ({ ...row, ...buildVariantGenerationDefaults({ baseSku: form.baseSku || form.modelCode || form.title, modelCode: form.modelCode, sequence: index + 1, automaticBarcodes: true, fallbackSalePrice, fallbackListPrice }) }))
          : sortedRows
      })
      const message = `${generated.length} varyant satırı hazırlandı.`; setNotice(message); showFeedback(message, 'success')
      return true
    } catch (reason) { const message = reason instanceof Error ? reason.message : 'Varyantlar oluşturulamadı.'; setNotice(message); showFeedback(message, 'error'); return false }
  }
  function syncOptionGroups() {
    const allIds = bulkSyncOptionRequirements.map(item => item.attributeId)
    const attributeIds = resolveVariantSyncAttributeIds(variantAttributeIds, bulkOptionSyncSelection, allIds)
    if (generateVariants(attributeIds, true)) setBulkOptionSyncOpen(false)
  }
  function clearVariants() {
    const persistedIds = new Set(productToEdit.data?.variants.map(variant => variant.id) ?? [])
    const clearedSavedIds = variantRows.filter(row => persistedIds.has(row.key)).map(row => row.key)
    setRemovedVariantIds(current => [...new Set([...current, ...clearedSavedIds])])
    setVariantRows([])
    const message = clearedSavedIds.length
      ? `Varyant satırları temizlendi. Kaydedince ${clearedSavedIds.length} kayıtlı varyant panelden silinir; pazaryerlerindeki ilanlara dokunulmaz.`
      : 'Oluşan varyant satırları temizlendi.'
    setNotice(message); showFeedback(message, 'success')
  }
  function updateVariantRow(keyValue: string, field: keyof VariantDraft, value: string) { setVariantRows(rows => rows.map(row => row.key !== keyValue ? row : { ...row, [field]: field === 'stock' || field === 'salePrice' || field === 'listPrice' || field === 'costPrice' ? Number(value || 0) : value })) }
  function updateVariantMedia(keyValue: string, values: string[]) { setVariantRows(rows => rows.map(row => row.key !== keyValue ? row : { ...row, mediaRefs: values })) }
  function reorderVariants(sourceKey: string, targetKey: string) {
    if (sourceKey === targetKey) return
    setVariantRows(rows => {
      const sourceIndex = rows.findIndex(row => row.key === sourceKey); const targetIndex = rows.findIndex(row => row.key === targetKey)
      if (sourceIndex < 0 || targetIndex < 0) return rows
      const next = [...rows]; const [moved] = next.splice(sourceIndex, 1)
      const targetIndexAfterRemoval = next.findIndex(row => row.key === targetKey)
      const insertionIndex = sourceIndex < targetIndex ? targetIndexAfterRemoval + 1 : targetIndexAfterRemoval
      next.splice(insertionIndex, 0, moved)
      return next
    })
  }
  useEffect(() => {
    if (!pointerDraggedVariantKey) return
    const rowKeyAtPoint = (clientX: number, clientY: number) => document.elementFromPoint(clientX, clientY)?.closest<HTMLElement>('[data-variant-row-key]')?.dataset.variantRowKey ?? null
    const clearPointerDrag = () => {
      const source = pointerDragSourceRef.current
      if (source && pointerDragIdRef.current !== null) {
        try { source.releasePointerCapture(pointerDragIdRef.current) } catch { /* pointer capture may already be released */ }
      }
      pointerDraggedVariantRef.current = null
      pointerDragSourceRef.current = null
      pointerDragIdRef.current = null
      setPointerDraggedVariantKey(null)
      setDraggedVariantKey(null)
      setDragOverVariantKey(null)
    }
    const handlePointerMove = (event: PointerEvent) => {
      event.preventDefault()
      const targetKey = rowKeyAtPoint(event.clientX, event.clientY)
      if (targetKey && targetKey !== pointerDraggedVariantKey) setDragOverVariantKey(targetKey)
    }
    const handlePointerUp = (event: PointerEvent) => {
      const sourceKey = pointerDraggedVariantRef.current
      const targetKey = rowKeyAtPoint(event.clientX, event.clientY)
      if (sourceKey && targetKey) reorderVariants(sourceKey, targetKey)
      clearPointerDrag()
    }
    document.addEventListener('pointermove', handlePointerMove, { passive: false })
    document.addEventListener('pointerup', handlePointerUp)
    document.addEventListener('pointercancel', clearPointerDrag)
    return () => {
      document.removeEventListener('pointermove', handlePointerMove)
      document.removeEventListener('pointerup', handlePointerUp)
      document.removeEventListener('pointercancel', clearPointerDrag)
    }
  }, [pointerDraggedVariantKey])
  function beginVariantPointerDrag(event: React.PointerEvent<HTMLDivElement>, keyValue: string) {
    if (event.pointerType === 'mouse' && event.button !== 0) return
    event.preventDefault()
    pointerDraggedVariantRef.current = keyValue
    pointerDragSourceRef.current = event.currentTarget
    pointerDragIdRef.current = event.pointerId
    try { event.currentTarget.setPointerCapture(event.pointerId) } catch { /* capture is not available in every browser */ }
    setPointerDraggedVariantKey(keyValue)
    setDraggedVariantKey(keyValue)
    setDragOverVariantKey(null)
  }
  function reorderMedia(sourceIndex: number, targetIndex: number) {
    const next = reorderMediaUrls(visibleMediaUrls, sourceIndex, targetIndex)
    setDraggedMedia(null); setDragOverMedia(null)
    if (next === visibleMediaUrls) return
    setFamilyMediaOrder(next)
    setFamilyMediaOrderDirty(Boolean(editProductId))
    const orderedProductMediaUrls = mediaUrlsInPreferredOrder(mediaUrls, next)
    if (!mediaRefsEqual(orderedProductMediaUrls, mediaUrls)) updateField('mediaUrls', orderedProductMediaUrls.join('\n'))
    showFeedback(editProductId ? 'Görsel sırası diğer renklerle birlikte Kaydet’te uygulanacak.' : 'Görsel sırası güncellendi. Kalıcı olması için kaydedin.', 'info')
  }
  function startMediaTouch(event: ReactPointerEvent<HTMLDivElement>) {
    if (event.pointerType === 'mouse' || submitting) return
    const target = event.target as Element
    const card = target.closest<HTMLElement>('[data-media-index]')
    if (!card || target.closest('button')) return
    const kind = card.dataset.mediaKind === 'file' ? 'file' : 'url'
    const index = Number(card.dataset.mediaIndex)
    const state = { kind, index, target: index, x: event.clientX, y: event.clientY, lastY: event.clientY, timer: 0, dragging: false, scroller: null } as NonNullable<typeof mediaTouch.current>
    state.timer = window.setTimeout(() => {
      state.dragging = true
      setDraggedMedia({ kind, index })
      setDragOverMedia({ kind, index })
      navigator.vibrate?.(12)
    }, 280)
    mediaTouch.current = state
  }
  function moveMediaTouch(event: ReactPointerEvent<HTMLDivElement>) {
    const state = mediaTouch.current
    if (!state) return
    if (!state.dragging) {
      // The pictures take no browser gestures on phones (they would cancel the hold), so a finger that moves before the
      // hold completes scrolls the page by hand.
      if (state.scroller || Math.hypot(event.clientX - state.x, event.clientY - state.y) > 8) {
        window.clearTimeout(state.timer)
        state.scroller ??= scrollingParent(event.currentTarget)
        state.scroller.scrollBy(0, state.lastY - event.clientY)
        state.lastY = event.clientY
      }
      return
    }
    const card = document.elementFromPoint(event.clientX, event.clientY)?.closest<HTMLElement>('[data-media-index]')
    if (!card || (card.dataset.mediaKind === 'file' ? 'file' : 'url') !== state.kind) return
    state.target = Number(card.dataset.mediaIndex)
    setDragOverMedia({ kind: state.kind, index: state.target })
  }
  function endMediaTouch() {
    const state = mediaTouch.current
    if (!state?.dragging) { cancelMediaTouch(); return }
    mediaTouch.current = null
    suppressMediaClick.current = true
    window.setTimeout(() => { suppressMediaClick.current = false }, 400)
    if (state.kind === 'url') reorderMedia(state.index, state.target)
    else reorderUploadedMedia(state.index, state.target)
    setDraggedMedia(null); setDragOverMedia(null)
  }
  function cancelMediaTouch() {
    const state = mediaTouch.current
    if (!state) return
    window.clearTimeout(state.timer)
    mediaTouch.current = null
    if (state.dragging) { setDraggedMedia(null); setDragOverMedia(null) }
  }
  function reorderUploadedMedia(sourceIndex: number, targetIndex: number) {
    const next = reorderItems(mediaFiles, sourceIndex, targetIndex)
    setDraggedMedia(null); setDragOverMedia(null)
    if (next === mediaFiles) return
    setMediaFiles(next)
    showFeedback('Yüklenen görsel sırası güncellendi. Kalıcı olması için ürünü kaydedin.', 'info')
  }
  function clearAllMedia() {
    setMediaFiles([])
    updateField('mediaUrls', '')
    setFamilyMediaOrder([])
    setVariantRows(rows => rows.map(row => ({ ...row, mediaRefs: [] })))
    setDraggedMedia(null)
    setDragOverMedia(null)
    showFeedback('Ürün ve varyant görselleri temizlendi. Kalıcı olması için kaydedin.', 'info')
  }
  async function removeProductMedia(url: string, confirmed = false) {
    const targetKey = mediaImageKey(url)
    const savedMediaUrls = initialEditMediaUrl.current.split(/\r?\n|[;|]/u).map(item => item.trim()).filter(Boolean)
    const isPersisted = Boolean(editProductId && productToEdit.data && savedMediaUrls.some(item => mediaImageKey(item) === targetKey))
    const nextDraftMediaUrls = mediaUrls.filter(item => mediaImageKey(item) !== targetKey)
    const removeVariantReference = (reference: string) => !reference.startsWith('url|') || mediaImageKey(reference.slice(4)) !== targetKey

    if (!isPersisted || !editProductId || !productToEdit.data) {
      updateField('mediaUrls', nextDraftMediaUrls.join('\n'))
      setVariantRows(rows => rows.map(row => ({ ...row, mediaRefs: row.mediaRefs.filter(removeVariantReference) })))
      showFeedback('Görsel taslaktan kaldırıldı. Kalıcı olması için Kaydet’e basın.', 'info')
      return
    }

    if (!confirmed) {
      setMediaRemovalConfirmation({ kind: 'product', url })
      return
    }
    setDeletingProductMediaKey(targetKey)
    try {
      const product = productToEdit.data
      const result = await hubApi<{ deletedCount: number; version: number; productVersions: Record<string, number> }>('/files/product-media-item', {
        method: 'DELETE',
        headers: { 'Idempotency-Key': key(), 'If-Match': `"v${product.version}"` },
        body: JSON.stringify({ productId: product.id, url })
      })
      initialEditMediaUrl.current = savedMediaUrls.filter(item => mediaImageKey(item) !== targetKey).join('\n')
      initialEditVariantMediaRefs.current = Object.fromEntries(Object.entries(initialEditVariantMediaRefs.current).map(([variantId, references]) => [variantId, references.filter(removeVariantReference)]))
      updateField('mediaUrls', nextDraftMediaUrls.join('\n'))
      setVariantRows(rows => rows.map(row => ({ ...row, mediaRefs: row.mediaRefs.filter(removeVariantReference) })))
      setFamilyMediaOrder(current => current.filter(item => mediaImageKey(item) !== targetKey))

      const cachedProduct = client.getQueryData<Product>(['product', product.id]) ?? product
      const cachedMediaUrls = (cachedProduct.mediaUrls ?? []).filter(item => mediaImageKey(item) !== targetKey)
      const productVersion = result.productVersions[product.id] ?? result.version
      initializedEditProductKey.current = `${product.id}:${productVersion}`
      client.setQueryData<Product>(['product', product.id], {
        ...cachedProduct,
        version: productVersion,
        mediaUrls: cachedMediaUrls,
        primaryImageUrl: cachedProduct.primaryImageUrl && mediaImageKey(cachedProduct.primaryImageUrl) === targetKey ? cachedMediaUrls[0] ?? null : cachedProduct.primaryImageUrl,
        familyMediaUrls: (cachedProduct.familyMediaUrls ?? []).filter(item => mediaImageKey(item) !== targetKey),
        familyMediaItems: (cachedProduct.familyMediaItems ?? []).filter(item => mediaImageKey(item.url) !== targetKey),
        familyOrderedMediaUrls: cachedProduct.familyOrderedMediaUrls?.filter(item => mediaImageKey(item) !== targetKey),
        variants: cachedProduct.variants.map(variant => ({ ...variant, mediaUrls: (variant.mediaUrls ?? []).filter(item => mediaImageKey(item) !== targetKey) }))
      })
      await productToEdit.refetch()
      await client.invalidateQueries({ queryKey: ['products'] })
      showFeedback(`${result.deletedCount} görsel bağlantısı kaldırıldı ve ürün yenilendi.`, 'success')
    } catch (reason) {
      showFeedback(reason instanceof Error ? reason.message : 'Görsel kaldırılamadı.', 'error')
    } finally {
      setDeletingProductMediaKey(null)
    }
  }
  async function removeFamilyMedia(item: ProductFamilyMediaItem, confirmed = false) {
    if (item.mediaIds.length === 0) {
      showFeedback('Görselin kaynak bağlantıları yüklenemedi. Sayfayı yenileyip tekrar deneyin.', 'error')
      return
    }
    if (!confirmed) {
      setMediaRemovalConfirmation({ kind: 'family', item })
      return
    }
    const pendingKey = item.mediaIds.join(',')
    setDeletingFamilyMediaKey(pendingKey)
    try {
      await hubApi<{ deletedCount: number }>('/files/product-media-items', {
        method: 'DELETE',
        headers: { 'Idempotency-Key': key() },
        body: JSON.stringify({ mediaIds: item.mediaIds })
      })
      setFamilyMediaOrder(current => current.filter(url => mediaImageKey(url) !== mediaImageKey(item.url)))
      await productToEdit.refetch()
      await client.invalidateQueries({ queryKey: ['products'] })
      showFeedback('Renk ailesi görseli kaldırıldı.', 'success')
    } catch (reason) {
      showFeedback(reason instanceof Error ? reason.message : 'Renk ailesi görseli kaldırılamadı.', 'error')
    } finally {
      setDeletingFamilyMediaKey(null)
    }
  }
  function confirmMediaRemoval() {
    const confirmation = mediaRemovalConfirmation
    setMediaRemovalConfirmation(null)
    if (!confirmation) return
    if (confirmation.kind === 'product') void removeProductMedia(confirmation.url, true)
    else void removeFamilyMedia(confirmation.item, true)
  }
  function updateChannel(id: string) {
    const selected = selectedChannelIds.includes(id)
    setSelectedChannelIds(current => selected ? current.filter(item => item !== id) : [...current, id])
    showFeedback(selected ? 'Yayın kanalı seçimden çıkarıldı.' : 'Yayın kanalı seçildi.', 'info')
  }
  function updateableProductConnectionIds() {
    return (connections.data?.items ?? []).filter(item => isProductPublicationConnection(item) && productUpdateCapabilities(item.platformCode).writable && item.status.trim().toUpperCase() === 'ACTIVE').map(item => item.id)
  }
  function openPlatformUpdateDialog() {
    const updateableIds = updateableProductConnectionIds()
    const currentSelection = selectedChannelIds.filter(id => updateableIds.includes(id))
    setPlatformUpdateConnectionIds(currentSelection.length ? currentSelection : updateableIds)
    setPlatformUpdateProductInformation(true)
    setPlatformUpdateDialogOpen(true)
  }
  function queuePlatformUpdate(connectionIds: string[], includeProductInformation: boolean) {
    const updateableIds = new Set(updateableProductConnectionIds())
    const safeConnectionIds = [...new Set(connectionIds)].filter(id => updateableIds.has(id))
    if (!safeConnectionIds.length) {
      showFeedback('Güncelleme için desteklenen ve aktif bir platform seçin.', 'error')
      return
    }
    const formElement = document.getElementById('product-creation-form')
    if (!(formElement instanceof HTMLFormElement)) {
      showFeedback('Ürün formu bulunamadı; güncelleme başlatılamadı.', 'error')
      return
    }
    pendingPlatformUpdate.current = { connectionIds: safeConnectionIds, includeProductInformation }
    setPlatformUpdateDialogOpen(false)
    formElement.requestSubmit()
  }
  function confirmPlatformUpdate() {
    queuePlatformUpdate(platformUpdateConnectionIds, platformUpdateProductInformation)
  }
  function startQuickPlatformUpdate() {
    const connectionIds = quickPlatformUpdateTargets(selectedChannelIds, updateableProductConnectionIds())
    if (!connectionIds.length) {
      openPlatformUpdateDialog()
      showFeedback('Hızlı güncelleme için önce güncellenecek platformu seçin.', 'info')
      return
    }
    queuePlatformUpdate(connectionIds, false)
  }
  function channelPriceDraft(connectionId: string): ChannelPricingDraft {
    return channelPricing[connectionId] ?? { listPrice: form.listPrice, salePrice: form.salePrice }
  }
  function toggleCustomChannelPrice(connectionId: string) {
    setChannelPricing(current => {
      if (connectionId in current) { const { [connectionId]: _removed, ...rest } = current; return rest }
      return { ...current, [connectionId]: { listPrice: form.listPrice, salePrice: form.salePrice } }
    })
  }
  function updateChannelPrice(connectionId: string, field: keyof ChannelPricingDraft, value: string) {
    setChannelPricing(current => ({ ...current, [connectionId]: { ...(current[connectionId] ?? { listPrice: form.listPrice, salePrice: form.salePrice }), [field]: value } }))
  }
  function platformDisplayName(platform: VariantPlatformStatus) {
    const code = platform.platformCode.trim().toLocaleUpperCase('tr-TR')
    return code === 'SHOPIFY' ? 'Shopify' : code === 'TRENDYOL' ? 'Trendyol' : code === 'HEPSIBURADA' ? 'Hepsiburada' : code === 'N11' ? 'N11' : code === 'PAZARAMA' ? 'Pazarama' : platform.platform
  }
  function openVariantPlatformPricing(row: VariantDraft, platform: VariantPlatformStatus) {
    setVariantPlatformPricing({ rowKey: row.key, platform })
    setVariantPlatformPricingDraft({
      listPrice: String(platform.listPrice ?? row.listPrice ?? form.listPrice),
      salePrice: String(platform.salePrice ?? row.salePrice ?? form.salePrice)
    })
    requestAnimationFrame(() => {
      document.querySelector(`[data-variant-row-key="${row.key}"]`)?.scrollIntoView({ behavior: 'smooth', block: 'center', inline: 'nearest' })
    })
  }
  async function saveVariantPlatformPricingBulk(targetPlatform?: VariantPlatformStatus, targetDraft?: ChannelPricingDraft) {
    if (!variantPlatformPricing) return
    const selectedPlatform = targetPlatform ?? variantPlatformPricing.platform
    if (!selectedPlatform.connectionId) {
      showFeedback('Bu platform bağlantısının fiyat bilgisi alınamadı.', 'error')
      return
    }
    const activeDraft = targetDraft ?? variantPlatformPricingDraft
    const listPrice = Number(activeDraft.listPrice)
    const salePrice = Number(activeDraft.salePrice)
    if (!Number.isFinite(listPrice) || !Number.isFinite(salePrice) || listPrice < 0 || salePrice < 0 || listPrice < salePrice) {
      showFeedback('Liste fiyatı satış fiyatından küçük olamaz.', 'error')
      return
    }
    setVariantPlatformPricingSaving(true)
    try {
      const savedOffers: Array<{ rowKey: string; platform: VariantPlatformStatus; saved: { id: string; version: number; listPrice: number; salePrice: number; currency: string } }> = []
      for (const row of variantRows) {
        const platform = row.platformStatuses?.find(item => item.connectionId === selectedPlatform.connectionId)
        if (!platform?.connectionId) continue
        const body = {
          listPrice,
          salePrice,
          currency: platform.currency ?? selectedPlatform.currency ?? form.currency ?? 'TRY',
          vatRate: platform.vatRate ?? selectedPlatform.vatRate ?? Number(form.vatRate || 0),
          vatInclusion: platform.vatInclusion ?? selectedPlatform.vatInclusion ?? form.vatIncluded,
          roundingMode: platform.roundingMode ?? selectedPlatform.roundingMode ?? 'HALF_EVEN',
          safetyStock: platform.safetyStock ?? selectedPlatform.safetyStock ?? Number(form.safetyStock || 0),
          status: 'ACTIVE',
          reason: `${platformDisplayName(platform)} toplu varyant fiyatı`
        }
        const saved = platform.offerId && platform.offerVersion != null
          ? await hubApi<{ id: string; version: number; listPrice: number; salePrice: number; currency: string }>(`/channel-offers/${platform.offerId}`, { method: 'PATCH', headers: { 'If-Match': `"v${platform.offerVersion}"` }, body: JSON.stringify(body) })
          : await hubApi<{ id: string; version: number; listPrice: number; salePrice: number; currency: string }>('/channel-offers', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ connectionId: platform.connectionId, variantId: row.key, ...body }) })
        savedOffers.push({ rowKey: row.key, platform, saved })
      }
      if (!savedOffers.length) {
        showFeedback('Bu platform için güncellenebilir varyant bulunamadı.', 'error')
        return
      }
      const savedByRow = new Map(savedOffers.map(item => [item.rowKey, item]))
      setVariantRows(rows => rows.map(row => {
        const saved = savedByRow.get(row.key)
        if (!saved) return row
        return { ...row, platformStatuses: row.platformStatuses?.map(platform => platform.connectionId === saved.platform.connectionId ? { ...platform, offerId: saved.saved.id, listPrice: saved.saved.listPrice, salePrice: saved.saved.salePrice, currency: saved.saved.currency, offerVersion: saved.saved.version } : platform) }
      }))
      setVariantPlatformPricing(null)
      const message = `${platformDisplayName(selectedPlatform)} için ${savedOffers.length} varyantın fiyatı toplu olarak kaydedildi.`
      setNotice(message); showFeedback(message, 'success')
      await client.invalidateQueries({ queryKey: ['products'] })
      if (editProductId) {
        await client.invalidateQueries({ queryKey: ['product', editProductId] })
        await productToEdit.refetch()
      }
    } catch (reason) {
      const message = reason instanceof Error ? reason.message : 'Pazaryeri varyant fiyatları kaydedilemedi.'
      showFeedback(message, 'error')
    } finally {
      setVariantPlatformPricingSaving(false)
    }
  }
  async function saveVariantPlatformPricingRow(targetRow: VariantDraft, targetPlatform: VariantPlatformStatus, targetDraft: ChannelPricingDraft) {
    const platform = targetRow.platformStatuses?.find(item => item.connectionId === targetPlatform.connectionId)
    if (!platform?.connectionId) {
      showFeedback('Bu platform bağlantısının fiyat bilgisi alınamadı.', 'error')
      return
    }
    const listPrice = Number(targetDraft.listPrice)
    const salePrice = Number(targetDraft.salePrice)
    if (!Number.isFinite(listPrice) || !Number.isFinite(salePrice) || listPrice < 0 || salePrice < 0 || listPrice < salePrice) {
      showFeedback('Liste fiyatı satış fiyatından küçük olamaz.', 'error')
      return
    }
    setVariantPlatformPricingSaving(true)
    try {
      const body = {
        listPrice,
        salePrice,
        currency: platform.currency ?? targetPlatform.currency ?? form.currency ?? 'TRY',
        vatRate: platform.vatRate ?? targetPlatform.vatRate ?? Number(form.vatRate || 0),
        vatInclusion: platform.vatInclusion ?? targetPlatform.vatInclusion ?? form.vatIncluded,
        roundingMode: platform.roundingMode ?? targetPlatform.roundingMode ?? 'HALF_EVEN',
        safetyStock: platform.safetyStock ?? targetPlatform.safetyStock ?? Number(form.safetyStock || 0),
        status: 'ACTIVE',
        reason: `${platformDisplayName(platform)} varyant fiyatı`
      }
      const saved = platform.offerId && platform.offerVersion != null
        ? await hubApi<{ id: string; version: number; listPrice: number; salePrice: number; currency: string }>(`/channel-offers/${platform.offerId}`, { method: 'PATCH', headers: { 'If-Match': `"v${platform.offerVersion}"` }, body: JSON.stringify(body) })
        : await hubApi<{ id: string; version: number; listPrice: number; salePrice: number; currency: string }>('/channel-offers', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ connectionId: platform.connectionId, variantId: targetRow.key, ...body }) })
      setVariantRows(rows => rows.map(row => row.key !== targetRow.key ? row : { ...row, platformStatuses: row.platformStatuses?.map(item => item.connectionId === platform.connectionId ? { ...item, offerId: saved.id, listPrice: saved.listPrice, salePrice: saved.salePrice, currency: saved.currency, offerVersion: saved.version } : item) }))
      showFeedback(`${platformDisplayName(platform)} ${targetRow.optionSignature || targetRow.sku} fiyatı kaydedildi.`, 'success')
      await client.invalidateQueries({ queryKey: ['products'] })
      if (editProductId) {
        await client.invalidateQueries({ queryKey: ['product', editProductId] })
        await productToEdit.refetch()
      }
    } catch (reason) {
      showFeedback(reason instanceof Error ? reason.message : 'Varyant kanal fiyatı kaydedilemedi.', 'error')
    } finally {
      setVariantPlatformPricingSaving(false)
    }
  }
  void saveVariantPlatformPricingBulk
  void saveVariantPlatformPricingRow
  async function saveVariantPlatformPricingMatrix(drafts: Record<string, ChannelPricingDraft>) {
    if (!variantPlatformPricing) return
    const sourceRow = variantRows.find(item => item.key === variantPlatformPricing.rowKey)
    const sourcePlatforms = sourceRow?.platformStatuses?.length ? sourceRow.platformStatuses : !editProductId ? variantPricingPlatforms : []
    if (!sourceRow || !sourcePlatforms.length) {
      showFeedback('Bu ürün için güncellenebilir platform fiyatı bulunamadı.', 'error')
      return
    }
    const platformKey = (platform: VariantPlatformStatus) => `${platform.platformCode}:${platform.connectionId ?? platform.platform}`
    const targets = variantRows.flatMap(targetRow => sourcePlatforms.flatMap(sourcePlatform => {
      const platform = targetRow.platformStatuses?.find(item => item.connectionId === sourcePlatform.connectionId) ?? (!editProductId ? sourcePlatform : undefined)
      const draft = drafts[`${targetRow.key}:${platformKey(sourcePlatform)}`]
      return platform?.connectionId && draft ? [{ row: targetRow, platform, draft }] : []
    }))
    if (!targets.length) {
      showFeedback('Bu platform için güncellenebilir varyant bulunamadı.', 'error')
      return
    }
    for (const target of targets) {
      const listPrice = Number(target.draft.listPrice)
      const salePrice = Number(target.draft.salePrice)
      if (!Number.isFinite(listPrice) || !Number.isFinite(salePrice) || listPrice < 0 || salePrice < 0 || listPrice < salePrice) {
        showFeedback('Liste fiyatı satış fiyatından küçük olamaz.', 'error')
        return
      }
    }
    if (!editProductId) {
      setVariantChannelPricing(current => ({
        ...current,
        ...Object.fromEntries(targets.map(target => [`${target.row.key}:${target.platform.connectionId}`, target.draft]))
      }))
      setVariantPlatformPricing(null)
      const message = `${targets.length} varyantın platform fiyatı ürün kaydedilince uygulanacak.`
      setNotice(message); showFeedback(message, 'success')
      return
    }
    setVariantPlatformPricingSaving(true)
    try {
      const savedOffers: Array<{ rowKey: string; connectionId: string; saved: { id: string; version: number; listPrice: number; salePrice: number; currency: string } }> = []
      for (const target of targets) {
        const listPrice = Number(target.draft.listPrice)
        const salePrice = Number(target.draft.salePrice)
        const body = {
          listPrice,
          salePrice,
          currency: target.platform.currency ?? form.currency ?? 'TRY',
          vatRate: target.platform.vatRate ?? Number(form.vatRate || 0),
          vatInclusion: target.platform.vatInclusion ?? form.vatIncluded,
          roundingMode: target.platform.roundingMode ?? 'HALF_EVEN',
          safetyStock: target.platform.safetyStock ?? Number(form.safetyStock || 0),
          status: 'ACTIVE',
          reason: `${platformDisplayName(target.platform)} toplu varyant fiyatı`
        }
        const saved = target.platform.offerId && target.platform.offerVersion != null
          ? await hubApi<{ id: string; version: number; listPrice: number; salePrice: number; currency: string }>(`/channel-offers/${target.platform.offerId}`, { method: 'PATCH', headers: { 'If-Match': `"v${target.platform.offerVersion}"` }, body: JSON.stringify(body) })
          : await hubApi<{ id: string; version: number; listPrice: number; salePrice: number; currency: string }>('/channel-offers', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ connectionId: target.platform.connectionId, variantId: target.row.key, ...body }) })
        savedOffers.push({ rowKey: target.row.key, connectionId: target.platform.connectionId!, saved })
      }
      const savedByKey = new Map(savedOffers.map(item => [`${item.rowKey}:${item.connectionId}`, item]))
      setVariantRows(currentRows => currentRows.map(targetRow => ({ ...targetRow, platformStatuses: targetRow.platformStatuses?.map(platform => { const saved = savedByKey.get(`${targetRow.key}:${platform.connectionId}`); return saved ? { ...platform, offerId: saved.saved.id, listPrice: saved.saved.listPrice, salePrice: saved.saved.salePrice, currency: saved.saved.currency, offerVersion: saved.saved.version } : platform }) })))
      setVariantPlatformPricing(null)
      const message = `${savedOffers.length} varyant platform fiyatı kaydedildi.`
      setNotice(message); showFeedback(message, 'success')
      await client.invalidateQueries({ queryKey: ['products'] })
      if (editProductId) {
        await client.invalidateQueries({ queryKey: ['product', editProductId] })
        await productToEdit.refetch()
      }
    } catch (reason) {
      showFeedback(reason instanceof Error ? reason.message : 'Pazaryeri varyant fiyatları kaydedilemedi.', 'error')
    } finally {
      setVariantPlatformPricingSaving(false)
    }
  }
  async function saveVariantPlatformPricing() {
    if (!variantPlatformPricing) return
    const row = variantRows.find(item => item.key === variantPlatformPricing.rowKey)
    const platform = variantPlatformPricing.platform
    if (!row || !platform.connectionId) {
      showFeedback('Bu platform bağlantısının fiyat bilgisi alınamadı.', 'error')
      return
    }
    const listPrice = Number(variantPlatformPricingDraft.listPrice)
    const salePrice = Number(variantPlatformPricingDraft.salePrice)
    if (!Number.isFinite(listPrice) || !Number.isFinite(salePrice) || listPrice < 0 || salePrice < 0 || listPrice < salePrice) {
      showFeedback('Liste fiyatı satış fiyatından küçük olamaz.', 'error')
      return
    }
    setVariantPlatformPricingSaving(true)
    try {
      const body = {
        listPrice,
        salePrice,
        currency: platform.currency ?? form.currency ?? 'TRY',
        vatRate: platform.vatRate ?? Number(form.vatRate || 0),
        vatInclusion: platform.vatInclusion ?? form.vatIncluded,
        roundingMode: platform.roundingMode ?? 'HALF_EVEN',
        safetyStock: platform.safetyStock ?? Number(form.safetyStock || 0),
        status: 'ACTIVE',
        reason: `${platformDisplayName(platform)} varyant fiyatı`
      }
      const saved = platform.offerId && platform.offerVersion != null
        ? await hubApi<{ id: string; version: number; listPrice: number; salePrice: number; currency: string }>(`/channel-offers/${platform.offerId}`, { method: 'PATCH', headers: { 'If-Match': `"v${platform.offerVersion}"` }, body: JSON.stringify(body) })
        : await hubApi<{ id: string; version: number; listPrice: number; salePrice: number; currency: string }>('/channel-offers', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ connectionId: platform.connectionId, variantId: row.key, ...body }) })
      const nextPlatform = { ...platform, offerId: saved.id, listPrice: saved.listPrice, salePrice: saved.salePrice, currency: saved.currency, offerVersion: saved.version }
      setVariantRows(rows => rows.map(item => item.key !== row.key ? item : { ...item, platformStatuses: item.platformStatuses?.map(status => status.connectionId === platform.connectionId ? nextPlatform : status) }))
      setVariantPlatformPricing(null)
      const message = `${platformDisplayName(platform)} için varyant fiyatı kaydedildi.`
      setNotice(message); showFeedback(message, 'success')
      await client.invalidateQueries({ queryKey: ['products'] })
      if (editProductId) {
        await client.invalidateQueries({ queryKey: ['product', editProductId] })
        await productToEdit.refetch()
      }
    } catch (reason) {
      const message = reason instanceof Error ? reason.message : 'Pazaryeri fiyatı kaydedilemedi.'
      showFeedback(message, 'error')
    } finally {
      setVariantPlatformPricingSaving(false)
    }
  }
  function applyBulk() {
    const stock = bulkStock === '' ? null : Number(bulkStock); const sale = bulkSalePrice === '' ? null : Number(bulkSalePrice); const cost = bulkCostPrice === '' ? null : Number(bulkCostPrice); const list = bulkListPrice === '' ? null : Number(bulkListPrice)
    const matchingKeys = new Set(variantRows.filter(row => rowMatchesVariantFilters(row)).map(row => row.key))
    if (hasVariantFilters && !matchingKeys.size) {
      const message = 'Seçtiğiniz filtrelerle eşleşen varyant bulunamadı.'; setNotice(message); showFeedback(message, 'error')
      return
    }
    setVariantRows(rows => rows.map(row => !matchingKeys.has(row.key) ? row : { ...row, stock: stock == null || !Number.isFinite(stock) ? row.stock : stock, salePrice: sale == null || !Number.isFinite(sale) ? row.salePrice : sale, costPrice: cost == null || !Number.isFinite(cost) ? row.costPrice : cost, listPrice: list == null || !Number.isFinite(list) ? row.listPrice : list }))
    const message = hasVariantFilters ? `Toplu stok, fiyat ve maliyet değerleri ${matchingKeys.size} seçili varyanta uygulandı.` : 'Toplu stok, fiyat ve maliyet değerleri tüm varyantlara uygulandı.'; setNotice(message); showFeedback(message, 'success')
  }

  function openVariantBulkEdit(field: VariantBulkEditField) {
    setVariantBulkEditFilterSelections({})
    setVariantBulkEdit({ field, value: '', error: '' })
  }

  function applyVariantBulkEdit() {
    if (!variantBulkEdit) return
    if (!variantBulkEdit.value.trim()) {
      setVariantBulkEdit(current => current ? { ...current, error: 'Uygulanacak değeri girin.' } : current)
      return
    }
    const targetRows = filterVariantsByOptions(variantRows, variantBulkEditFilterGroups, variantBulkEditFilterSelections, rowMatchesVariantMediaValue)
    if (!targetRows.length) {
      setVariantBulkEdit(current => current ? { ...current, error: 'Seçili filtreyle eşleşen varyant bulunamadı.' } : current)
      return
    }
    const value = Number(variantBulkEdit.value)
    const issue = variantBulkEditIssue(variantBulkEdit.field, value, targetRows)
    if (issue) {
      setVariantBulkEdit(current => current ? { ...current, error: issue } : current)
      return
    }
    const rowKeys = new Set(targetRows.map(row => row.key))
    setVariantRows(rows => applyVariantBulkEditValue(rows, rowKeys, variantBulkEdit.field, value))
    showFeedback(`${variantBulkEditLabels[variantBulkEdit.field]} değeri ${hasVariantBulkEditFilters ? `${targetRows.length} filtre eşleşen varyanta` : `tüm ${targetRows.length} varyanta`} uygulandı.`, 'success')
    setVariantBulkEdit(null)
  }

  function applyBarcodeToSku(mode: 'missing' | 'all') {
    const rowsWithBarcode = variantRows.filter(row => row.barcode.trim())
    if (!rowsWithBarcode.length) {
      const message = 'Stok kodu oluşturmak için önce en az bir barkod girin.'; setNotice(message); showFeedback(message, 'error'); setBarcodeSkuMenuOpen(false)
      return
    }
    const rowsToUpdate = rowsWithBarcode.filter(row => mode === 'all' || !row.sku.trim())
    if (!rowsToUpdate.length) {
      const message = 'Boş stok kodu bulunamadı; mevcut kodlar korunuyor.'; setNotice(message); showFeedback(message, 'info'); setBarcodeSkuMenuOpen(false)
      return
    }
    const targetKeys = new Set(rowsToUpdate.map(row => row.key))
    const finalSkuByKey = new Map(variantRows.map(row => [row.key, targetKeys.has(row.key) ? row.barcode.trim() : row.sku.trim()]))
    const skuOwners = new Map<string, string[]>()
    for (const [rowKey, sku] of finalSkuByKey) {
      if (!sku) continue
      const normalized = sku.toLocaleUpperCase('tr-TR')
      skuOwners.set(normalized, [...(skuOwners.get(normalized) ?? []), rowKey])
    }
    const conflictingKeys = new Set([...skuOwners.values()].filter(keys => keys.length > 1).flat())
    const safeRows = rowsToUpdate.filter(row => !conflictingKeys.has(row.key))
    if (!safeRows.length) {
      const message = 'Barkodlar uygulanamadı; stok kodlarında çakışma var. Önce tekrar eden barkodları düzeltin.'; setNotice(message); showFeedback(message, 'error'); setBarcodeSkuMenuOpen(false)
      return
    }
    const safeKeys = new Set(safeRows.map(row => row.key))
    setVariantRows(rows => rows.map(row => safeKeys.has(row.key) ? { ...row, sku: row.barcode.trim() } : row))
    const skippedCount = rowsToUpdate.length - safeRows.length
    const message = skippedCount
      ? `${safeRows.length} varyanta uygulandı; ${skippedCount} satır çakışma nedeniyle korunuyor.`
      : `${safeRows.length} varyantın stok kodu barkoddan güncellendi.`
    setNotice(message); showFeedback(message, skippedCount ? 'info' : 'success'); setBarcodeSkuMenuOpen(false)
  }

  function regenerateVariantCodes(target: 'barcode' | 'sku') {
    const codes = buildSequentialVariantCodes(form.modelCode, variantRows.length)
    if (!codes.length) {
      const message = !form.modelCode.trim() ? 'Otomatik kod üretmek için önce model kodunu girin.' : 'Kod üretimi için varyant sayısı 1-1000 arasında olmalıdır.'
      setNotice(message); showFeedback(message, 'error'); setBarcodePasteMenuOpen(false); setBarcodeSkuMenuOpen(false)
      return
    }
    setPendingVariantCodeGeneration({ target, codes })
    setBarcodePasteMenuOpen(false)
    setBarcodeSkuMenuOpen(false)
  }

  function confirmVariantCodeGeneration() {
    const request = pendingVariantCodeGeneration
    if (!request) return
    if (variantRowsRef.current.length !== request.codes.length) {
      const message = 'Varyant sayısı değiştiği için kodlar üretilmedi. Menüyü yeniden açıp tekrar deneyin.'
      setNotice(message); showFeedback(message, 'error'); setPendingVariantCodeGeneration(null)
      return
    }

    setVariantRows(rows => applyGeneratedVariantCodes(rows, request.target, request.codes))
    if (request.target === 'barcode') setForm(current => ({ ...current, barcode: request.codes[0] }))
    const message = `${request.codes.length} varyantın ${request.target === 'barcode' ? 'barkodu' : 'stok kodu'} model kodundan oluşturuldu; diğer kod alanı korundu.${request.target === 'barcode' ? ' Üst barkod alanı ilk varyantla eşitlendi.' : ''} Kalıcı olması için Kaydet’e basın.`
    setNotice(message); showFeedback(message, 'success'); setPendingVariantCodeGeneration(null)
  }

  async function pasteBarcodesFromClipboard(mode: 'missing' | 'all') {
    if (typeof navigator.clipboard?.readText !== 'function') {
      const message = 'Pano okuma bu tarayıcıda kullanılamıyor; barkodları satırlara elle yapıştırın.'
      setNotice(message); showFeedback(message, 'error'); setBarcodePasteMenuOpen(false)
      return
    }

    let clipboardText: string
    try {
      clipboardText = await navigator.clipboard.readText()
    } catch {
      const message = 'Panoya erişilemedi. Tarayıcı pano iznini verip tekrar deneyin.'
      setNotice(message); showFeedback(message, 'error'); setBarcodePasteMenuOpen(false)
      return
    }

    const rows = variantRowsRef.current
    const rowsToUpdate = mode === 'missing' ? rows.filter(row => !row.barcode.trim()) : rows
    if (!rowsToUpdate.length) {
      const message = mode === 'missing' ? 'Boş barkod satırı bulunamadı.' : 'Önce varyant satırlarını oluşturun.'
      setNotice(message); showFeedback(message, 'info'); setBarcodePasteMenuOpen(false)
      return
    }
    const values = parseBarcodeClipboardValues(clipboardText)
    const targetKeys = new Set(rowsToUpdate.map(row => row.key))
    const reservedValues = rows.filter(row => !targetKeys.has(row.key)).map(row => row.barcode.trim())
    const issue = barcodeClipboardIssue(values, rowsToUpdate.length, reservedValues)
    if (issue) {
      setNotice(issue); showFeedback(issue, 'error'); setBarcodePasteMenuOpen(false)
      return
    }

    const valuesByKey = new Map(rowsToUpdate.map((row, index) => [row.key, values[index].trim()]))
    setVariantRows(currentRows => currentRows.map(row => {
      const barcode = valuesByKey.get(row.key)
      return barcode === undefined ? row : { ...row, barcode }
    }))
    const message = `${rowsToUpdate.length} varyantın barkodu panodan yapıştırıldı.`
    setNotice(message); showFeedback(message, 'success'); setBarcodePasteMenuOpen(false)
  }

  function toggleVariantFilter(groupId: string, valueId: string) {
    setVariantFilterSelections(current => {
      const values = current[groupId] ?? []
      const nextValues = values.includes(valueId) ? values.filter(item => item !== valueId) : [...values, valueId]
      return { ...current, [groupId]: nextValues }
    })
  }

  function clearVariantFilters() { setVariantFilterSelections({}) }

  function openVariantMediaPicker(rowKey: string) {
    const row = variantRows.find(item => item.key === rowKey)
    setVariantMediaModal({ mode: 'variant', rowKey, draftRefs: row?.mediaRefs ?? [] })
  }
  function openBulkVariantMediaPicker() {
    const groups = bulkMediaGroups
    if (!groups.length) {
      const message = 'Önce seçenek grubu ve en az bir seçenek değeri seçin.'
      setNotice(message); showFeedback(message, 'error')
      return
    }
    const group = groups.find(item => normalizeVariantOptionName(item.name) === 'RENK') ?? groups[0]
    const draftsBySelection = Object.fromEntries(groups.flatMap(mediaGroup => mediaGroup.values.map(value => {
      const assignedRefs = new Set(variantRows
        .filter(row => rowMatchesVariantMediaValue(row, mediaGroup, value))
        .flatMap(row => row.mediaRefs))
      return [variantMediaAssignmentKey(mediaGroup.id, value.id), mediaChoices.filter(option => assignedRefs.has(option.value)).map(option => option.value)]
    })))
    setVariantMediaModal({ mode: 'bulk', draftsBySelection, groupId: group.id, valueId: group.values[0]?.id ?? '' })
  }
  function rowOptionValue(row: VariantDraft, group: Pick<VariantMediaGroup, 'name'>) {
    const groupName = normalizeVariantOptionName(group.name)
    const optionRank = (name: string) => {
      const normalized = normalizeVariantOptionName(name)
      if (normalized === groupName) return 3
      if (!isColorOptionName(name) || !isColorOptionName(group.name)) return -1
      return isWebColorOptionName(name) ? 1 : 2
    }
    const options = [
      ...Object.entries(row.options).map(([name, value]) => ({ name, value })),
      ...parseVariantOptionSignature(row.optionSignature)
    ]
    return options
      .filter(option => option.value.trim())
      .sort((left, right) => optionRank(right.name) - optionRank(left.name))
      .find(option => optionRank(option.name) >= 0)?.value ?? ''
  }
  function rowMatchesVariantMediaValue(row: VariantDraft, group: VariantMediaGroup, value: { id: string; value: string }) {
    return Boolean((group.attributeId && row.attributeValueIds[group.attributeId] === value.id) || normalizeVariantOptionValue(rowOptionValue(row, group)) === normalizeVariantOptionValue(value.value))
  }
  function rowMatchesVariantFilters(row: VariantDraft) {
    return filterVariantsByOptions([row], variantFilterGroups, variantFilterSelections, rowMatchesVariantMediaValue).length > 0
  }
  // Media assignments made in the modal are persisted right away on the edit page;
  // the effect runs after variantRows state has committed so the save sees the new refs.
  useEffect(() => {
    if (!mediaAutoSaveTick || !editProductId) return
    const form = document.getElementById('product-creation-form') as HTMLFormElement | null
    const saveButton = form?.querySelector<HTMLButtonElement>('button[data-submit-intent="save"]')
    if (form && saveButton && !saveButton.disabled) form.requestSubmit(saveButton)
  }, [mediaAutoSaveTick])

  function applyVariantMediaSelection() {
    if (!variantMediaModal) return
    if (variantMediaModal.mode === 'variant') {
      updateVariantMedia(variantMediaModal.rowKey, variantMediaModal.draftRefs)
      setVariantMediaModal(null)
      setMediaAutoSaveTick(tick => tick + 1)
      showFeedback(variantMediaModal.draftRefs.length ? `${variantMediaModal.draftRefs.length} varyant görseli seçildi.` : 'Varyant görselleri kaldırıldı.', 'success')
      return
    }
    const group = bulkMediaGroups.find(item => item.id === variantMediaModal.groupId)
    if (!group) return
    const assignments = group.values.map(value => ({
      value,
      refs: readVariantMediaAssignmentDraft(variantMediaModal.draftsBySelection, variantMediaAssignmentKey(group.id, value.id)),
      matchingRows: variantRows.filter(row => rowMatchesVariantMediaValue(row, group, value))
    })).filter(assignment => assignment.matchingRows.length > 0)
    const matchingRowCount = new Set(assignments.flatMap(assignment => assignment.matchingRows.map(row => row.key))).size
    if (!matchingRowCount) {
      const message = `${group.name} değerleriyle eşleşen varyant satırı bulunamadı.`
      setNotice(message); showFeedback(message, 'error')
      return
    }
    setVariantRows(rows => rows.map(row => {
      const assignment = assignments.find(item => item.matchingRows.some(matchingRow => matchingRow.key === row.key))
      return assignment ? { ...row, mediaRefs: assignment.refs } : row
    }))
    setVariantMediaModal(null); setMediaAutoSaveTick(tick => tick + 1)
    const message = `${group.name} görsel eşlemesi kaydedildi: ${assignments.length} değer, ${matchingRowCount} varyant satırı.`
    setNotice(message); showFeedback(message, 'success')
  }

  function rowsForSubmit(requireCompleteCatalog = true) {
    if (requireCompleteCatalog && variantAttributeIds.length && !variantRows.length) throw new Error('Varyant özellikleri seçili. Önce “Ürünleri ekle” ile varyantları oluşturun.')
    if (editProductId && !variantRows.length) {
      if (removedVariantIds.length) throw new Error('Tüm kayıtlı varyantlar temizlendi. Kaydetmeden önce “Ürünleri ekle” ile yeni varyant satırları oluşturun; ürünün en az bir varyantı kalmalıdır.')
      return []
    }
    if (variantRows.length) return variantRows
    const generatedDefaults = automaticBarcodeGeneration ? buildVariantGenerationDefaults({ baseSku: form.baseSku || form.modelCode || form.title, modelCode: form.modelCode, sequence: 1, automaticBarcodes: true, fallbackSalePrice, fallbackListPrice }) : null
    return [{ key: crypto.randomUUID(), optionSignature: 'Tek Ürün', options: {}, attributeValueIds: {}, sku: generatedDefaults?.sku ?? (form.baseSku || form.modelCode || form.title || 'URUN').trim().replace(/\s+/g, '-').toLocaleUpperCase('tr-TR'), barcode: generatedDefaults?.barcode ?? form.barcode, stock: initialStock, salePrice: generatedDefaults?.salePrice ?? fallbackSalePrice, listPrice: generatedDefaults?.listPrice ?? fallbackListPrice, costPrice: fallbackCostPrice, mediaRefs: [] }]
  }
  function validate(rows: VariantDraft[], requireCompleteCatalog = true, requirePublicationReadiness = true) {
    const issues: string[] = []; const requirementList = mappedRequirements
    if (requireCompleteCatalog && (variantAttributeIds.length > 2 || variantAttributeIds.some(id => { const requirement = requirementList.find(item => item.attributeId === id); return !requirement || !isOptionRequirement(requirement) }))) issues.push('Varyant için en fazla 2 Seçenek Eşitleme başlığı kullanılabilir.')
    if (requireCompleteCatalog && !webColorAutoEnabled && (!webColorRequirement || !manualWebColorValueId)) issues.push('Manuel Web Color aktarımı için gönderilecek panel renk değerini seçin.')
    if (!webColorAutoEnabled && webColorRequirement && manualWebColorValueId && !webColorRequirement.attribute.values.some(value => value.id === manualWebColorValueId)) issues.push('Manuel Web Color için seçilen değer geçerli değil.')
    if (requireCompleteCatalog && webColorAutoEnabled && webColorRequirement && (!colorOptionRequirement || !variantAttributeIds.includes(colorOptionRequirement.attributeId)) && !(attributeSelections[colorOptionRequirement?.attributeId ?? '']?.length)) issues.push('Web Color otomatik aktarımı için Renk seçeneğini seçin veya otomatik aktarımı kapatıp bir değer seçin.')
    if (!form.title.trim()) issues.push('Ürün adı zorunludur.')
    const defaultListPrice = Number(form.listPrice || 0); const defaultSalePrice = Number(form.salePrice || 0)
    if (!Number.isFinite(defaultListPrice) || !Number.isFinite(defaultSalePrice) || defaultListPrice < 0 || defaultSalePrice < 0 || defaultListPrice < defaultSalePrice) issues.push('Liste fiyatı satış fiyatından küçük olamaz ve fiyatlar negatif olamaz.')
    if (automaticBarcodeGeneration && !form.modelCode.trim()) issues.push('Otomatik barkod oluşturmak için model kodu zorunludur.')
    if (requireCompleteCatalog && !form.description.trim()) issues.push('Açıklama zorunludur.')
    if (requireCompleteCatalog) {
      for (const requirement of requirementList) {
        const selectedCount = attributeSelections[requirement.attributeId]?.length ?? 0
        if (!variantAttributeIds.includes(requirement.attributeId) && requirement.attribute.dataType === 'SINGLE_SELECT' && selectedCount > 1) issues.push(`${requirement.attribute.name} yalnız bir ürün değeri kabul eder.`)
        if (isOptionRequirement(requirement) || !requirement.isRequired) continue
        if (variantAttributeIds.includes(requirement.attributeId)) {
          if (rows.some(row => !row.attributeValueIds[requirement.attributeId])) issues.push(`${requirement.attribute.name} tüm varyantlarda seçilmelidir.`)
        } else if (!(attributeSelections[requirement.attributeId]?.length) && !(attributeTextValues[requirement.attributeId] ?? '').trim()) issues.push(`${requirement.attribute.name} zorunludur.`)
      }
    }
    if (rows.length > MAX_VARIANTS) issues.push(`En fazla ${MAX_VARIANTS} varyant oluşturulabilir.`)
    const publicationMediaRefs = [...mediaUrls, ...rows.flatMap(row => row.mediaRefs.filter(ref => ref.startsWith('url|')).map(ref => ref.slice(4)))]
    const allMediaUrls = publicProductMediaUrls(publicationMediaRefs)
    const invalidMediaUrl = allMediaUrls.map(productMediaUrlIssue).find((issue): issue is string => issue !== null)
    if (invalidMediaUrl) issues.push(`Görsel bağlantısı geçersiz: ${invalidMediaUrl} Ürün kaydedilmeden önce düzeltin veya kaldırın.`)
    const skus = rows.map(row => row.sku.trim().toLocaleUpperCase('tr-TR')); if (skus.some(value => !value)) issues.push('Tüm varyantlarda stok kodu zorunludur.'); if (new Set(skus).size !== skus.length) issues.push('Stok kodları benzersiz olmalıdır.')
    const signatures = rows.map(row => row.optionSignature); if (new Set(signatures).size !== signatures.length) issues.push('Aynı varyant kombinasyonu iki kez oluşturulamaz.')
    const barcodes = rows.map(row => row.barcode.trim()).filter(Boolean); if (new Set(barcodes.map(value => value.toLocaleUpperCase('tr-TR'))).size !== barcodes.length) issues.push('Barkodlar benzersiz olmalıdır.')
    if (duplicateProductId && productToCopy.data) {
      const conflicts = productCopyIdentifierConflicts(productToCopy.data.variants, rows)
      if (conflicts.barcodes) issues.push('Kaynak ürünün barkodları zaten kayıtlı. Yeni ürünü oluşturmak için çakışan barkodları değiştirin.')
      if (conflicts.skus) issues.push('Kaynak ürünün stok kodları zaten kayıtlı. Yeni ürünü oluşturmak için çakışan stok kodlarını değiştirin.')
    }
     if (rows.some(row => row.salePrice < 0 || row.listPrice < row.salePrice)) issues.push('Her varyantta liste fiyatı satış fiyatından küçük olamaz.')
     if (!form.desi.trim() || !Number.isFinite(Number(form.desi)) || Number(form.desi) <= 0) issues.push('Desi sıfırdan büyük olmalıdır.')
     if (requireCompleteCatalog && requirePublicationReadiness && selectedChannelIds.length) {
      if (!form.brandId) issues.push('Trendyol yayını için marka zorunludur.'); if (!form.modelCode.trim() || form.modelCode.trim().length > 40) issues.push('Trendyol yayını için en fazla 40 karakterlik model kodu zorunludur.'); if (form.title.trim().length > 100) issues.push('Trendyol ürün başlığı en fazla 100 karakter olabilir.')
      const publicMediaUrls = publicProductMediaUrls(publicationMediaRefs)
      const hasLocalPublicationMedia = mediaFiles.length > 0 || publicationMediaRefs.some(isStoredProductMediaUrl)
      if (!publicMediaUrls.length && !hasLocalPublicationMedia) issues.push('Trendyol yayını için en az bir ürün görseli ekleyin.')
      if (publicMediaUrls.some(url => !url.startsWith('https://'))) issues.push('Tüm görsel adresleri HTTPS olmalıdır.')
      if (rows.some(row => !row.barcode.trim() || !/^[a-zA-Z0-9._-]+$/.test(row.barcode.trim()))) issues.push('Trendyol yayını için her varyantta geçerli ve benzersiz barkod zorunludur.'); if (rows.some(row => row.salePrice <= 0)) issues.push('Trendyol yayını için satış fiyatı sıfırdan büyük olmalıdır.')
      for (const connectionId of selectedChannelIds) {
        const draft = channelPriceDraft(connectionId); const listPrice = Number(draft.listPrice); const salePrice = Number(draft.salePrice)
        if (!Number.isFinite(listPrice) || !Number.isFinite(salePrice) || salePrice <= 0 || listPrice < salePrice) {
          const name = (connections.data?.items ?? []).find(item => item.id === connectionId)?.displayName ?? 'Seçili kanal'
          issues.push(`${name} için liste fiyatı satış fiyatından küçük olamaz; satış fiyatı sıfırdan büyük olmalıdır.`)
        }
      }
    }
    if (issues.length) throw new Error(issues.join(' '))
  }

  function handleInvalid(event: FormEvent<HTMLFormElement>) {
    const target = event.target as HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement
    const message = target.validationMessage || 'Lütfen zorunlu alanları doldurun.'
    setError(new Error(message))
    setNotice(message)
    showFeedback(message, 'error')
  }

  function dismissProductError() {
    setError(undefined)
    setNotice('')
    setFeedback(current => current?.kind === 'error' ? null : current)
  }

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const platformUpdate = pendingPlatformUpdate.current
    pendingPlatformUpdate.current = null
    const submitter = (event.nativeEvent as SubmitEvent).submitter
    const submitData = new FormData(event.currentTarget)
    const saveAndStay = submitter?.getAttribute('data-submit-intent') === 'save'
      || (submitter?.getAttribute('name') === 'intent' && submitter?.getAttribute('value') === 'save')
      || submitData.get('intent') === 'save'
    const createOnly = !editProductId && (submitter?.getAttribute('data-submit-intent') === 'create'
      || (submitter?.getAttribute('name') === 'intent' && submitter?.getAttribute('value') === 'create'))
    const requireCompleteCatalog = !editProductId || !saveAndStay
    if (wizardStep !== 2 && !saveAndStay && !createOnly && !platformUpdate) {
      setWizardStep(2)
      return
    }
    setError(undefined); setNotice(''); showFeedback(editProductId ? 'Ürün değişiklikleri kaydediliyor…' : 'Ürün oluşturuluyor…', 'info', { persistent: true }); setSubmitting(true); let productCreated: Product | undefined; let familyMediaChanged = false
    try {
      let submitRequirements = requirements.data ?? []
      if (form.categoryId && !requirements.data) {
        const loadedRequirements = await requirements.refetch()
        if (loadedRequirements.isError || !loadedRequirements.data) throw new Error('Kategori özellikleri alınamadı. Güvenli kayıt için kategori eşleştirmesini kontrol edip tekrar deneyin.')
        submitRequirements = loadedRequirements.data
      }
      const optionRequirementsForSubmit = submitRequirements.filter(isOptionRequirement).map(item => ({ attributeId: item.attributeId, name: item.attribute.name, values: item.attribute.values }))
      if (requireCompleteCatalog && form.categoryId && requirements.isError && !requirements.data) throw new Error('Kategori özellikleri alınamadı. Önce kategori eşleştirmesini kontrol edin.')
      const requirementList = mappedRequirements; const rows = rowsForSubmit(requireCompleteCatalog); validate(rows, requireCompleteCatalog, !createOnly)
      const regularGlobalAttributes = requirementList
        .filter(item => !isOptionRequirement(item) && item.attributeId !== webColorRequirement?.attributeId && !variantAttributeIds.includes(item.attributeId))
        .flatMap((item, index) => productAttributePayload(item, attributeSelections[item.attributeId] ?? [], attributeTextValues[item.attributeId] ?? '', index))
      const colorGlobalAttributes = webColorRequirement && !webColorAutoEnabled && manualWebColorValueId
        ? [{ attributeId: webColorRequirement.attributeId, valueId: manualWebColorValueId, textValue: null, numberValue: null, booleanValue: null, sortOrder: regularGlobalAttributes.length }]
        : webColorRequirement && !variantAttributeIds.includes(webColorRequirement.attributeId)
          ? productAttributePayload(webColorRequirement, attributeSelections[webColorRequirement.attributeId] ?? [], attributeTextValues[webColorRequirement.attributeId] ?? '', regularGlobalAttributes.length)
          : []
      const globalAttributes = [...regularGlobalAttributes, ...colorGlobalAttributes]
      // Do not overwrite existing assignments while a newly selected category's
      // requirements are still loading (or failed). The edit form may be saved
      // without optional mapping data.
      const shouldPersistAttributes = !editProductId || Boolean(form.categoryId && requirements.isSuccess)
      const variantAttributesForPayload = (row: VariantDraft) => buildVariantAttributeAssignments({
        options: row.options,
        selectedValueIds: row.attributeValueIds,
        existing: row.persistedAttributes ?? [],
        optionRequirements: optionRequirementsForSubmit
      })
      const safeDescription = sanitizeRichText(form.description)
      const variantPayload = (row: VariantDraft, index: number) => ({ sku: row.sku, barcode: row.barcode || null, modelCode: form.modelCode.trim() || null, sortOrder: index, weight: calculateDesi ? Number(form.weight) || null : null, width: calculateDesi ? Number(form.width) || null : null, height: calculateDesi ? Number(form.height) || null : null, length: calculateDesi ? Number(form.length) || null : null, desi: calculateDesi ? desi || 1 : Number(form.desi) || 1, costPrice: row.costPrice, defaultListPrice: row.listPrice, defaultSalePrice: row.salePrice, options: row.options, attributes: variantAttributesForPayload(row) })
      const existingVariantIds = new Set(productToEdit.data?.variants.map(variant => variant.id) ?? [])
      const modelCodeForRow = (variantId: string) => modelCodeForExistingVariant(
        form.modelCode,
        initialEditModelCode.current,
        productToEdit.data?.variants.find(variant => variant.id === variantId)?.modelCode
      )
      let product: Product
      let productVersionRetryUsed = false
      if (productToEdit.data) {
        const editProduct = productToEdit.data
        const updatePayload = {
          title: form.title,
          status: form.status,
          description: safeDescription,
          brandId: form.brandId || null,
          categoryId: form.categoryId || null,
          defaultListPrice: Number(form.listPrice || 0),
          defaultSalePrice: Number(form.salePrice || 0),
          ...(shouldPersistAttributes ? { attributes: globalAttributes } : {}),
          variantIdsToDelete: removedVariantIds.filter(id => existingVariantIds.has(id) && !rows.some(row => row.key === id)),
          variantsToCreate: rows.filter(row => !existingVariantIds.has(row.key)).map(variantPayload),
          variantUpdates: rows.filter(row => existingVariantIds.has(row.key)).map(row => ({
            id: row.key,
            sku: row.sku,
            barcode: row.barcode || null,
            modelCode: modelCodeForRow(row.key),
            costPrice: row.costPrice,
            defaultListPrice: row.listPrice,
            defaultSalePrice: row.salePrice,
            sortOrder: rows.findIndex(candidate => candidate.key === row.key),
            options: row.options,
            attributes: variantAttributesForPayload(row)
          }))
        }
        const updateProductAtVersion = (version: number) => hubApi<Product>(`/products/${editProduct.id}`, {
          method: 'PATCH',
          headers: { 'If-Match': `"v${version}"` },
          body: JSON.stringify(updatePayload)
        })
        try {
          product = await updateProductAtVersion(editProduct.version)
        } catch (reason) {
          if (!(reason instanceof ApiRequestError) || reason.status !== 412) throw reason
          const latestProduct = await hubApi<Product>(`/products/${editProduct.id}`)
          if (!sameProductSnapshotIgnoringVersion(editProduct, latestProduct)) {
            throw new Error(`Ürün bu form açıkken başka bir işlemle değişti (güncel sürüm v${latestProduct.version}). Diğer değişiklikleri korumak için sayfayı yenileyip tekrar deneyin.`)
          }
          try {
            product = await updateProductAtVersion(latestProduct.version)
            productVersionRetryUsed = true
          } catch (retryReason) {
            if (retryReason instanceof ApiRequestError && retryReason.status === 412) {
              throw new Error('Ürün kaydedilirken yeniden değişti. Güncel veriyi korumak için sayfayı yenileyip tekrar deneyin.')
            }
            throw retryReason
          }
        }
      } else {
        product = await hubApi<Product>('/products', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ title: form.title, status: form.status, description: safeDescription, brandId: form.brandId || null, categoryId: form.categoryId || null, defaultListPrice: Number(form.listPrice || 0), defaultSalePrice: Number(form.salePrice || 0), attributes: globalAttributes, variants: rows.map(variantPayload) }) })
      }
      productCreated = product; const completed = ['ürün']; const warnings: string[] = []
      const initialProductMediaUrls = initialEditMediaUrl.current.split(/\r?\n|[;|]/u).map(url => url.trim()).filter(Boolean)
      const productMediaChanged = editProductId
        ? !mediaRefsEqual(mediaUrls, initialProductMediaUrls) && !(familyMediaOrderDirty && mediaRefsSameSet(mediaUrls, initialProductMediaUrls))
        : mediaUrls.length > 0
      if (productMediaChanged) await hubApi('/files/product-media-reconcile', { method: 'PUT', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ productId: product.id, variantId: null, items: mediaUrls.map((url, sortOrder) => ({ url, sortOrder })), altText: form.title }) })
      for (const [fileIndex, pendingMedia] of mediaFiles.entries()) { const data = new FormData(); data.set('file', pendingMedia.file); data.set('productId', product.id); data.set('mediaRole', mediaUrls.length + fileIndex === 0 ? 'PRIMARY' : 'GALLERY'); data.set('sortOrder', String(mediaUrls.length + fileIndex)); data.set('altText', form.title); await hubApi('/files/product-media', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: data }) }
      const rowsBySku = new Map(rows.map(row => [row.sku.trim().toLocaleUpperCase('tr-TR'), row]))
      for (const variant of product.variants) {
        const row = rowsBySku.get(variant.sku.trim().toLocaleUpperCase('tr-TR'))
        if (!row) continue
        const originalMediaRefs = initialEditVariantMediaRefs.current[variant.id]
        if (editProductId && originalMediaRefs && mediaRefsEqual(row.mediaRefs, originalMediaRefs)) continue
        await hubApi('/files/product-media-reconcile', { method: 'PUT', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ productId: product.id, variantId: variant.id, items: row.mediaRefs.flatMap((mediaRef, sortOrder) => mediaRef.startsWith('url|') ? [{ url: mediaRef.slice(4), sortOrder }] : []), altText: `${form.title} · ${row.optionSignature}` }) })
        for (const [mediaIndex, mediaRef] of row.mediaRefs.entries()) {
          if (mediaRef.startsWith('file|')) {
            const file = mediaFiles.find(item => item.id === mediaRef.slice(5))?.file
            if (file) { const data = new FormData(); data.set('file', file); data.set('productId', product.id); data.set('variantId', variant.id); data.set('mediaRole', mediaIndex === 0 ? 'PRIMARY' : 'GALLERY'); data.set('sortOrder', String(mediaIndex)); data.set('altText', `${form.title} · ${row.optionSignature}`); await hubApi('/files/product-media', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: data }) }
          }
        }
      }
      if (editProductId && familyMediaOrderDirty) {
        const result = await hubApi<{ changedProductCount: number }>('/files/product-media-family-reorder', {
          method: 'PUT',
          headers: { 'Idempotency-Key': key() },
          body: JSON.stringify({ productId: product.id, urlsInOrder: visibleMediaUrls })
        })
        familyMediaChanged = result.changedProductCount > 0
        setFamilyMediaOrderDirty(false)
      }
      if (mediaUrls.length || mediaFiles.length) completed.push('görseller')
      if (familyMediaChanged) completed.push('diğer renklerin görsel sırası')
      const targetConnectionIds = createOnly ? [] : platformUpdate?.connectionIds ?? selectedChannelIds
      for (const variant of product.variants) {
        const row = rowsBySku.get(variant.sku.trim().toLocaleUpperCase('tr-TR'))
        if (!row) continue
        const currentStock = productToEdit.data?.variants.find(item => item.id === variant.id)?.onHand ?? 0
        const stockDelta = row.stock - currentStock
        if (stockDelta !== 0) await hubApi(`/inventory/${variant.id}/adjustments`, { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ quantityDelta: stockDelta, reason: productToEdit.data ? 'Ürün düzenleme stoğu' : 'İlk ürün stoğu', sourceEventId: key() }) })
        for (const connectionId of targetConnectionIds) {
          const pricing = channelPriceDraft(connectionId)
          const platform = row.platformStatuses?.find(item => item.connectionId === connectionId)
          const variantPricing = variantChannelPricing[`${row.key}:${connectionId}`]
          const offerCommand = { connectionId, variantId: variant.id, listPrice: platformUpdate ? row.listPrice : Number(variantPricing?.listPrice ?? pricing.listPrice), salePrice: platformUpdate ? row.salePrice : Number(variantPricing?.salePrice ?? pricing.salePrice), currency: platform?.currency ?? form.currency ?? 'TRY', vatRate: Number(form.vatRate || platform?.vatRate || 0), vatInclusion: form.vatIncluded || platform?.vatInclusion || 'INCLUDED', roundingMode: platform?.roundingMode ?? 'HALF_EVEN', safetyStock: Number(form.safetyStock || platform?.safetyStock || 0), status: 'ACTIVE', reason: platformUpdate ? 'Ürün platform güncellemesi' : 'İlk ürün fiyatı' }
          if (platformUpdate && platform?.offerId) {
            if (platform.offerVersion == null) throw new Error(`${row.sku}: platform fiyat sürümü eksik; fiyat güncellenmedi.`)
            await hubApi(`/channel-offers/${platform.offerId}`, { method: 'PATCH', headers: { 'If-Match': `"v${platform.offerVersion}"` }, body: JSON.stringify(offerCommand) })
          } else {
            await hubApi('/channel-offers', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify(offerCommand) })
          }
        }
      }
      if (rows.some(row => row.stock > 0)) completed.push('stok'); if (targetConnectionIds.length) completed.push('kanal fiyatları')
      const publicationConnectionIds = productPublicationTargets(saveAndStay, targetConnectionIds)
      for (const connectionId of publicationConnectionIds) {
        try {
          if (platformUpdate) {
            if (platformUpdate.includeProductInformation) {
              const acceptedUpdate = await hubApi<AcceptedJob>(`/products/${product.id}/update-jobs`, { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ connectionId }) })
              completed.push(`ürün bilgisi güncelleme işi ${acceptedUpdate.jobId}`)
            }
            const acceptedPrices = await hubApi<AcceptedJob>(`/products/${product.id}/connections/${connectionId}/price-inventory-sync-jobs`, { method: 'POST', headers: { 'Idempotency-Key': key() }, body: '{}' })
            completed.push(`fiyat-stok güncelleme işi ${acceptedPrices.jobId}`)
            await client.invalidateQueries({ queryKey: ['publication-status', product.id, connectionId] })
            continue
          }
          let listingProfileVersion: number | undefined
          try {
            const listingProfile = await hubApi<{ version: number }>(`/products/${product.id}/listing-profiles/${connectionId}`)
            listingProfileVersion = listingProfile.version
          } catch (reason) {
            if (!(reason instanceof ApiRequestError) || reason.status !== 404) throw reason
          }
          await hubApi(`/products/${product.id}/listing-profiles/${connectionId}`, { method: 'PUT', headers: listingProfileVersion == null ? {} : { 'If-Match': `"v${listingProfileVersion}"` }, body: JSON.stringify({ titleOverride: null, descriptionOverride: null, externalCategoryId: null, externalBrandId: null, deliveryTimeDays: null, enabled: true }) })
          await client.invalidateQueries({ queryKey: ['listing-profile', product.id, connectionId] })
          const accepted = await hubApi<AcceptedJob>(`/products/${product.id}/publication-jobs`, { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ connectionId }) })
          await client.invalidateQueries({ queryKey: ['publication-status', product.id, connectionId] })
          completed.push(`yayın işi ${accepted.jobId}`)
        } catch (reason) { warnings.push(reason instanceof Error ? reason.message : 'Yayın işi oluşturulamadı.') }
      }
      const updateSummary = platformUpdate ? ` Seçilen ${platformUpdate.connectionIds.length} platform için ${platformUpdate.includeProductInformation ? 'ürün bilgisi ve fiyat-stok' : 'fiyat-stok'} güncelleme işi kuyruğa alındı.` : ''
      const versionRetrySummary = productVersionRetryUsed ? ' Daha güncel kayıt sürümüyle güvenli şekilde tekrar denendi.' : ''
      const message = `${editProductId ? 'Ürün güncellendi.' : `${completed.join(', ')} kaydedildi.`}${versionRetrySummary}${updateSummary}${warnings.length ? ` Yayın uyarısı: ${warnings.join(' ')}` : ''}`
      setRemovedVariantIds([])
      setNotice(createOnly ? 'Ürün oluşturuldu. Ürün sayfası açılıyor.' : message)
      await client.invalidateQueries({ queryKey: ['products'] })
      if (editProductId) {
        await client.invalidateQueries({ queryKey: ['product', editProductId] })
        await productToEdit.refetch()
      }
      if (!createOnly) showFeedback(message, warnings.length ? 'info' : 'success')
      if (createOnly && productCreated) {
        appendNotification('Ürün oluşturuldu. Ürün sayfası açılıyor.', 'success')
        navigate(`/products/${productCreated.id}`)
      }
    } catch (reason) {
      const message = reason instanceof Error ? reason.message : 'Kayıt tamamlanamadı.'
      const feedbackMessage = productCreated ? `Ürün kaydedildi ancak sonraki işlem tamamlanamadı: ${message}` : message
      setError(reason); setNotice(feedbackMessage); showFeedback(feedbackMessage, 'error')
      if (productCreated && !editProductId) {
        appendNotification(`${feedbackMessage} Ürün sayfası açılıyor; kaldığınız yerden düzenleyebilirsiniz.`, 'error')
        navigate(`/products/${productCreated.id}`)
      }
    } finally { setSubmitting(false) }
  }

  const [marketplacePricingOpen, setMarketplacePricingOpen] = useState(false)
  const publicationChannels = (connections.data?.items ?? []).filter(isProductPublicationChannel)
  const platformCards = publicationChannels.map(connection => ({
    code: connection.platformCode.trim().toLocaleLowerCase('tr-TR'),
    name: connection.displayName,
    initial: connection.displayName.trim().charAt(0).toLocaleUpperCase('tr-TR') || 'R',
    tone: connection.platformCode.trim().toLocaleLowerCase('tr-TR'),
    connection,
    mode: productPublicationMode(connection)!
  }))
  const variantPricingPlatforms = useMemo<VariantPlatformStatus[]>(() => (connections.data?.items ?? []).filter(isProductPublicationConnection).map(connection => {
    const pricing = channelPricing[connection.id]
    return {
      platform: connection.displayName,
      platformCode: connection.platformCode,
      isLinked: true,
      status: connection.status,
      connectionId: connection.id,
      listPrice: Number(pricing?.listPrice ?? form.listPrice),
      salePrice: Number(pricing?.salePrice ?? form.salePrice),
      currency: form.currency
    }
  }), [channelPricing, connections.data?.items, form.currency, form.listPrice, form.salePrice])
  const productUpdateTargets = (connections.data?.items ?? []).filter(isProductUpdateConnection).map(connection => ({
    connection,
    capabilities: productUpdateCapabilities(connection.platformCode)
  }))
  const assignedMediaUrls = [...new Set(variantRows.flatMap(row => row.mediaRefs.filter(ref => ref.startsWith('url|')).map(ref => ref.slice(4))))]
  const familyMediaUrls = productToEdit.data?.familyMediaUrls ?? []
  const familyMediaItems = productToEdit.data?.familyMediaItems ?? familyMediaUrls.map(url => ({ url, mediaIds: [], sourceProductTitles: [] }))
  const familyOnlyMediaItems = familyMediaItems.filter(item => !mediaUrls.some(current => mediaImageKey(current) === mediaImageKey(item.url)))
  const defaultFamilyOrder = productToEdit.data?.familyOrderedMediaUrls?.length
    ? productToEdit.data.familyOrderedMediaUrls
    : [...mediaUrls, ...familyMediaItems.map(item => item.url)]
  const visibleMediaUrls = mediaUrlsInPreferredOrder(uniqueMediaUrls(defaultFamilyOrder), familyMediaOrder.length ? familyMediaOrder : defaultFamilyOrder)
  const mediaChoices: ProductMediaOption[] = ([...new Set([...mediaUrls, ...familyMediaUrls, ...assignedMediaUrls])].map((url, index) => ({ value: `url|${url}`, label: `Görsel ${String(index + 1).padStart(2, '0')}`, url })) as ProductMediaOption[]).concat(mediaFiles.map(item => ({ value: `file|${item.id}`, label: `Yüklenen görsel · ${item.file.name}`, file: item.file })))
  const bulkMediaGroups = useMemo<VariantMediaGroup[]>(() => {
    const groups: VariantMediaGroup[] = []
    const groupsByName = new Map<string, VariantMediaGroup>()
    const hasPanelColorSource = (productToEdit.data?.options ?? []).some(option => isColorOptionName(option.label) && !isWebColorOptionName(option.label))
      || optionRequirements.some(item => isColorOptionName(item.attribute.name) && !isWebColorOptionName(item.attribute.name))
      || variantRows.some(row => variantOptionEntries(row).some(option => isColorOptionName(option.name) && !isWebColorOptionName(option.name)))
    const addGroup = (source: VariantMediaGroup) => {
      // Persisted options and stale attribute selections can still list values whose
      // variants were deleted; only offer values that some variant row still uses.
      const group = variantRows.length
        ? { ...source, values: source.values.filter(value => variantRows.some(row => rowMatchesVariantMediaValue(row, source, value))) }
        : source
      if (hasPanelColorSource && isWebColorOptionName(group.name)) return
      const canonicalName = isColorOptionName(group.name) ? 'Renk' : group.name.trim()
      const name = canonicalName.toLocaleLowerCase('tr-TR')
      if (!group.values.length) return
      const formatValues = (values: VariantMediaGroup['values']) => isColorOptionName(canonicalName)
        ? values.map(value => ({ ...value, value: formatColorOptionValue(value.value) }))
        : values
      const existing = groupsByName.get(name)
      if (existing) {
        existing.values = sortOptionValues(canonicalName, formatValues(mergeVariantOptionValues(existing.values, group.values)))
        existing.attributeId ??= group.attributeId
        return
      }
      const mergedGroup = { ...group, name: canonicalName, values: sortOptionValues(canonicalName, formatValues(group.values)) }
      groupsByName.set(name, mergedGroup)
      groups.push(mergedGroup)
    }
    // Existing products expose their persisted option groups separately from
    // category requirements. Prefer these IDs/values so imported variants such
    // as “Beden: Tek Ebat” can be targeted even when the current category no
    // longer exposes the original option requirement.
    // When every variant was cleared the persisted options are stale; the option
    // selections above on the page are the source of truth then.
    const persistedOptions = variantRows.length || !removedVariantIds.length ? productToEdit.data?.options ?? [] : []
    for (const option of persistedOptions) {
      addGroup({ id: `product-option:${option.id}`, name: option.label, values: option.values.map(value => ({ id: value.id, value: value.label })) })
    }
    for (const item of optionRequirements) {
      const selectedIds = attributeSelections[item.attributeId] ?? []
      const values = item.attribute.values
        .filter(value => selectedIds.includes(value.id) || variantRows.some(row => row.attributeValueIds[item.attributeId] === value.id || rowOptionValue(row, { name: item.attribute.name }).trim().toLocaleLowerCase('tr-TR') === value.value.trim().toLocaleLowerCase('tr-TR')))
        .map(value => ({ id: value.id, value: value.value }))
      addGroup({ id: `category-attribute:${item.attributeId}`, name: item.attribute.name, attributeId: item.attributeId, values })
    }
    const signatureValues = new Map<string, { name: string; values: Map<string, string> }>()
    for (const row of variantRows) {
      for (const option of variantOptionEntries(row)) {
        const groupKey = option.name.trim().toLocaleLowerCase('tr-TR')
        const group = signatureValues.get(groupKey) ?? { name: option.name.trim(), values: new Map<string, string>() }
        group.values.set(option.value.trim().toLocaleLowerCase('tr-TR'), option.value.trim())
        signatureValues.set(groupKey, group)
      }
    }
    for (const group of signatureValues.values()) {
      addGroup({ id: `variant-signature:${group.name.toLocaleLowerCase('tr-TR')}`, name: group.name, values: [...group.values].map(([id, value]) => ({ id, value })) })
    }
    return groups
  }, [attributeSelections, optionRequirements, productToEdit.data?.options, removedVariantIds.length, variantRows])
  const variantFilterGroups = useMemo(() => bulkMediaGroups.filter(group => (isColorOptionName(group.name) || isSizeOptionName(group.name)) && variantRows.some(row => rowOptionValue(row, group).trim())), [bulkMediaGroups, variantRows])
  const variantBulkEditFilterGroups = variantFilterGroups.filter(group => isColorOptionName(group.name) || isSizeOptionName(group.name))
  const variantBulkEditMatchingRows = filterVariantsByOptions(variantRows, variantBulkEditFilterGroups, variantBulkEditFilterSelections, rowMatchesVariantMediaValue)
  const hasVariantBulkEditFilters = Object.values(variantBulkEditFilterSelections).some(valueIds => valueIds.length > 0)
  const activeVariantFilterEntries = variantFilterGroups.map(group => ({ group, valueIds: variantFilterSelections[group.id] ?? [] })).filter(entry => entry.valueIds.length > 0)
  const hasVariantFilters = activeVariantFilterEntries.length > 0
  const matchingVariantCount = variantRows.filter(row => rowMatchesVariantFilters(row)).length
  const barcodeRowCount = variantRows.filter(row => row.barcode.trim()).length
  const emptySkuBarcodeRowCount = variantRows.filter(row => row.barcode.trim() && !row.sku.trim()).length
  const emptyBarcodeRowCount = variantRows.length - barcodeRowCount
  const generatedCodePreview = buildSequentialVariantCodes(form.modelCode, 1)[0] ?? 'MODEL-01'
  const selectedBulkMediaGroup = variantMediaModal?.mode === 'bulk' ? bulkMediaGroups.find(group => group.id === variantMediaModal.groupId) : undefined
  const selectedBulkMediaValueId = variantMediaModal?.mode === 'bulk' ? variantMediaModal.valueId : undefined
  const selectedBulkMediaValue = selectedBulkMediaGroup?.values.find(value => value.id === selectedBulkMediaValueId)
  const selectedBulkMediaMatchCount = selectedBulkMediaGroup && selectedBulkMediaValue ? variantRows.filter(row => rowMatchesVariantMediaValue(row, selectedBulkMediaGroup, selectedBulkMediaValue)).length : 0
  const selectedBulkMediaGroupMatchCount = selectedBulkMediaGroup ? new Set(variantRows.filter(row => selectedBulkMediaGroup.values.some(value => rowMatchesVariantMediaValue(row, selectedBulkMediaGroup, value))).map(row => row.key)).size : 0
  const selectedBulkMediaAssignmentKey = variantMediaModal?.mode === 'bulk' ? variantMediaAssignmentKey(variantMediaModal.groupId, variantMediaModal.valueId) : ''
  const selectedVariantMediaRefs = variantMediaModal?.mode === 'bulk'
    ? readVariantMediaAssignmentDraft(variantMediaModal.draftsBySelection, selectedBulkMediaAssignmentKey)
    : variantMediaModal?.draftRefs ?? []
  const hasBarcodeData = variantRows.length ? variantRows.every(row => row.barcode.trim()) : automaticBarcodeGeneration ? Boolean(form.modelCode.trim()) : Boolean(form.barcode.trim())
  const hasBasicProductData = Boolean(form.title.trim() && form.description.trim() && form.brandId && form.modelCode.trim() && hasBarcodeData)
  const mediaCount = mediaUrls.length + mediaFiles.length
  const hasProductMedia = mediaCount > 0
  const hasVariantData = variantAttributeIds.length === 0 || variantRows.length > 0
  const rowHasRequirementValue = (row: VariantDraft, requirement: CategoryRequirement) => Boolean(row.attributeValueIds[requirement.attributeId]) || Boolean(rowOptionValue(row, { name: requirement.attribute.name }).trim())
  const categoryValidationIssues = !form.categoryId
    ? ['Kategori seçilmedi.']
    : requirements.isLoading
      ? ['Kategori özellikleri kontrol ediliyor.']
      : requirements.isError
        ? ['Kategori özellikleri alınamadı.']
        : allRequirements.filter(requirement => !isOptionRequirement(requirement) && requirement.isRequired && !mappedRequirements.some(item => item.attributeId === requirement.attributeId)).map(requirement => `${requirement.attribute.name} kategori özelliği eşleştirilmemiş.`).concat(mappedRequirements.filter(requirement => !isOptionRequirement(requirement) && requirement.isRequired && requirement.attributeId !== webColorRequirement?.attributeId).flatMap(requirement => {
          if (variantAttributeIds.includes(requirement.attributeId)) return variantRows.length && variantRows.every(row => rowHasRequirementValue(row, requirement)) ? [] : [`${requirement.attribute.name} tüm varyantlarda seçilmelidir.`]
          return (attributeSelections[requirement.attributeId]?.length ?? 0) || Boolean((attributeTextValues[requirement.attributeId] ?? '').trim()) ? [] : [`${requirement.attribute.name} zorunludur.`]
        }))
  const optionValidationIssues = !form.categoryId || !requirements.isSuccess ? [] : allRequirements.filter(requirement => isOptionRequirement(requirement) && !isWebColorOptionName(requirement.attribute.name) && !mappedRequirements.some(item => item.attributeId === requirement.attributeId)).map(requirement => `${requirement.attribute.name} seçenek eşleştirmesi eksik.`).concat(optionRequirements.flatMap(requirement => {
    const selected = attributeSelections[requirement.attributeId]?.length ?? 0
    const represented = variantRows.some(row => rowHasRequirementValue(row, requirement))
    if (!selected && !represented) return [`${requirement.attribute.name} için seçenek değeri seçilmemiş.`]
    if (variantAttributeIds.includes(requirement.attributeId) && (!variantRows.length || variantRows.some(row => !rowHasRequirementValue(row, requirement)))) return [`${requirement.attribute.name} seçenekleri varyant satırlarına aktarılmamış.`]
    return []
  }))
  const webColorValidationIssues = !form.categoryId || !requirements.isSuccess ? [] : !webColorRequirement
    ? ['Web Color için kategori özelliği eşleştirilmemiş.']
    : webColorAutoEnabled
      ? (!colorOptionRequirement || (!variantAttributeIds.includes(colorOptionRequirement.attributeId) && !(attributeSelections[colorOptionRequirement.attributeId]?.length))) ? ['Web Color otomatik aktarımı için Renk seçeneğini eşitleyip değer seçin.'] : []
      : !manualWebColorValueId ? ['Manuel Web Color için katalog rengi seçin.'] : webColorRequirement.attribute.values.some(value => value.id === manualWebColorValueId) ? [] : ['Manuel Web Color değeri geçerli değil.']
  const catalogValidationIssues = [...categoryValidationIssues, ...optionValidationIssues, ...webColorValidationIssues]
  const catalogValidationDetail = catalogValidationIssues.length ? catalogValidationIssues.join(' ') : 'Kategori özellikleri, seçenekler ve Web Color yayınlamaya hazır.'
  const basicProductMissingParts = [
    !form.title.trim() && 'ürün adı',
    !form.description.trim() && 'açıklama',
    !form.brandId && 'marka',
    !form.modelCode.trim() && 'model kodu',
    !hasBarcodeData && 'barkod'
  ].filter((part): part is string => Boolean(part))
  const variantsWithoutMediaCount = variantRows.filter(row => !row.mediaRefs.length).length
  const productChecks = [
    { title: 'Temel Ürün Verileri', detail: hasBasicProductData ? 'İsim, açıklama, marka, model ve barkod bilgileri eksiksiz.' : `Eksik alanlar: ${basicProductMissingParts.join(', ')}.`, ok: hasBasicProductData, shopifyOk: Boolean(form.title.trim() && form.description.trim()), shopifyDetail: 'Shopify ürünü için ad ve açıklama gereklidir.' },
    { title: 'Görsel Kalitesi', detail: hasProductMedia ? `${mediaCount} adet ürün görseli eklendi.${mediaCount > 8 ? ' Trendyol için sıralamadaki ilk 8 görsel kullanılır.' : ''}` : 'En az bir yüksek çözünürlüklü görsel ekleyin.', ok: hasProductMedia, shopifyOk: hasProductMedia, shopifyDetail: 'Shopify yayını için en az bir geçerli HTTPS görseli gerekir.' },
    { title: 'Varyant Bilgileri', detail: hasVariantData ? 'Varyant yapısı yayınlanmaya hazır.' : 'Seçilen seçenekler için varyant satırlarını oluşturun.', ok: hasVariantData, shopifyOk: hasVariantData, shopifyDetail: 'Shopify yayını için en az bir SKU değerli varyant oluşturun.' },
    { title: 'Kategori ve Web Color', detail: catalogValidationDetail, ok: catalogValidationIssues.length === 0, shopifyOk: true, shopifyDetail: 'Shopify yayını için pazaryeri kategori eşlemesi veya Web Color gerekmez.' }
  ]
  const publicationAttributeSelections = useMemo<PublicationAttributeSelection[]>(() => allRequirements.map(requirement => {
    const valueIds = new Set(attributeSelections[requirement.attributeId] ?? [])
    let hasCustomValue = Boolean((attributeTextValues[requirement.attributeId] ?? '').trim())
    for (const row of variantRows) {
      const optionValue = rowOptionValue(row, { name: requirement.attribute.name }).trim()
      const matchedValue = optionValue
        ? requirement.attribute.values.find(value => normalizeVariantOptionValue(value.value) === normalizeVariantOptionValue(optionValue))
        : undefined
      const rowValueId = row.attributeValueIds[requirement.attributeId] ?? matchedValue?.id
      if (rowValueId) valueIds.add(rowValueId)
      else if (optionValue && isOptionRequirement(requirement)) hasCustomValue = true
    }
    const usesManualWebColor = requirement.attributeId === webColorRequirement?.attributeId && !webColorAutoEnabled
    if (usesManualWebColor) {
      valueIds.clear()
      if (manualWebColorValueId) valueIds.add(manualWebColorValueId)
      hasCustomValue = false
    }
    return {
      attributeId: requirement.attributeId,
      name: requirement.attribute.name,
      isRequired: requirement.isRequired,
      values: [...valueIds].map(id => ({ id, label: requirement.attribute.values.find(value => value.id === id)?.value ?? '' })),
      hasCustomValue
    }
  }), [allRequirements, attributeSelections, attributeTextValues, manualWebColorValueId, variantRows, webColorAutoEnabled, webColorRequirement])
  const canAddVariantCombinations = useMemo(() => {
    if (!variantAttributeIds.length || (automaticBarcodeGeneration && !form.modelCode.trim())) return false
    try {
      const generated = buildVariantMatrix(mappedRequirements, variantAttributeIds, attributeSelections, form.baseSku || form.modelCode || form.title, fallbackListPrice, fallbackSalePrice, fallbackCostPrice, initialStock, automaticBarcodeGeneration, form.modelCode)
      const existing = new Map(variantRows.map(row => [variantSignatureKey(row.optionSignature), row]))
      const missingCombinations = generated.some(row => !existing.has(variantSignatureKey(row.optionSignature)))
      const automaticDefaultsOutOfDate = automaticBarcodeGeneration && sortVariantsAlphabetically(variantRows).some((row, index) => {
        const expected = buildVariantGenerationDefaults({ baseSku: form.baseSku || form.modelCode || form.title, modelCode: form.modelCode, sequence: index + 1, automaticBarcodes: true, fallbackSalePrice, fallbackListPrice })
        return row.sku !== expected.sku || row.barcode !== expected.barcode || row.salePrice !== expected.salePrice || row.listPrice !== expected.listPrice
      })
      return missingCombinations || automaticDefaultsOutOfDate
    } catch { return false }
  }, [attributeSelections, automaticBarcodeGeneration, fallbackCostPrice, fallbackListPrice, fallbackSalePrice, form.baseSku, form.modelCode, form.title, initialStock, mappedRequirements, variantAttributeIds, variantRows])
  const selectedVariantPlatformRow = variantPlatformPricing ? variantRows.find(row => row.key === variantPlatformPricing.rowKey) : undefined
  const showVariantStockTotal = Boolean(editProductId || (duplicateProductId && variantRows.length))

  if (duplicateProductId && productToCopy.isLoading) return <Page title="Yeni Ürün Ekle" eyebrow="Katalog"><div className="panel" role="status">Kaynak ürünün bilgileri forma hazırlanıyor…</div></Page>
  if (duplicateProductId && productToCopy.isError) return <Page title="Yeni Ürün Ekle" eyebrow="Katalog"><ErrorBox error={productToCopy.error} /><Link className="button-link secondary" to="/products">Ürünlere dön</Link></Page>

  return <Page className={`product-add-page${editProductId ? ' product-edit-page' : ''}`} title={editProductId ? "Ürün Düzenle" : "Yeni Ürün Ekle"} eyebrow="Katalog">
    {variantPlatformPricing && selectedVariantPlatformRow && <BulkVariantPlatformPricingModal row={selectedVariantPlatformRow} rows={variantRows} platforms={selectedVariantPlatformRow.platformStatuses?.length ? selectedVariantPlatformRow.platformStatuses : variantPricingPlatforms} savedDrafts={variantChannelPricing} productName={form.title} modelCode={form.modelCode} filterGroups={variantFilterGroups.filter(group => isColorOptionName(group.name) || isSizeOptionName(group.name))} matchesFilterValue={rowMatchesVariantMediaValue} saving={variantPlatformPricingSaving} onClose={() => setVariantPlatformPricing(null)} onSave={drafts => void saveVariantPlatformPricingMatrix(drafts)} />}
    {/* @ts-ignore: legacy inline modal is disabled while the shared multi-platform modal is used above. */}
    {false && variantPlatformPricing && selectedVariantPlatformRow && <div className="workspace-modal-backdrop variant-platform-pricing-backdrop" role="presentation" onMouseDown={() => !variantPlatformPricingSaving && setVariantPlatformPricing(null)}><section className="workspace-modal variant-platform-pricing-modal" role="dialog" aria-modal="true" aria-labelledby="variant-platform-pricing-title" onMouseDown={event => event.stopPropagation()}><header><div><p className="eyebrow">VARYANT KANAL FİYATI</p><h2 id="variant-platform-pricing-title">{platformDisplayName(variantPlatformPricing.platform)} fiyatlandırması</h2><p>{selectedVariantPlatformRow.optionSignature} · {selectedVariantPlatformRow.barcode || selectedVariantPlatformRow.sku}</p></div><button type="button" className="modal-close" onClick={() => setVariantPlatformPricing(null)} disabled={variantPlatformPricingSaving} aria-label="Fiyat penceresini kapat"><UiIcon name="close" /></button></header><div className="variant-platform-pricing-body"><div className="variant-platform-pricing-channel"><span className={`publish-platform-mark ${variantPlatformPricing.platform.platformCode.toLocaleLowerCase('tr-TR')}`}><img className={`publish-platform-logo ${platformLogoClass(variantPlatformPricing.platform.platformCode)}`} src={platformLogoSource(variantPlatformPricing.platform.platformCode) ?? '/platforms/trendyol.webp'} alt="" /></span><div><strong>{platformDisplayName(variantPlatformPricing.platform)}</strong><small>{variantPlatformPricing.platform.isLinked ? 'Bu varyant platforma bağlı.' : 'Bu varyant için bağlantı henüz eşleşmemiş.'}</small></div></div><div className="variant-platform-pricing-fields"><label>Liste fiyatı<input autoFocus={!touchScreen} type="number" min="0" step="0.01" value={variantPlatformPricingDraft.listPrice} onChange={event => setVariantPlatformPricingDraft(current => ({ ...current, listPrice: event.target.value }))} /></label><label>Satış fiyatı<input type="number" min="0" step="0.01" value={variantPlatformPricingDraft.salePrice} onChange={event => setVariantPlatformPricingDraft(current => ({ ...current, salePrice: event.target.value }))} /></label></div><p className="variant-platform-pricing-help">Bu değer yalnızca seçtiğiniz varyantın {platformDisplayName(variantPlatformPricing.platform)} kanal teklifine kaydedilir; panel ana fiyatı değişmez.</p></div><footer><button type="button" className="secondary" onClick={() => setVariantPlatformPricing(null)} disabled={variantPlatformPricingSaving}>Vazgeç</button><button type="button" onClick={() => void saveVariantPlatformPricing()} disabled={variantPlatformPricingSaving || !variantPlatformPricing.platform.connectionId}>{variantPlatformPricingSaving ? 'Kaydediliyor…' : 'Fiyatı kaydet'}</button></footer></section></div>}
    <div className="product-add-wizardbar"><div className="product-add-stepper"><div className="product-add-progress" role="tablist" aria-label={editProductId ? 'Ürün düzenleme adımları' : 'Ürün ekleme adımları'}><button type="button" className={wizardStep === 1 ? 'active' : ''} role="tab" aria-selected={wizardStep === 1} onClick={() => setWizardStep(1)}><span>1</span><strong>Ürün bilgileri ve varyantlar</strong></button><i aria-hidden="true" /><button type="button" className={wizardStep === 2 ? 'active' : ''} role="tab" aria-selected={wizardStep === 2} onClick={() => setWizardStep(2)}><span>2</span><strong>Yayınlama</strong></button></div></div></div><form id="product-creation-form" className="product-creation-workspace product-add-workspace" data-wizard-step={wizardStep} onSubmit={submit} onInvalidCapture={handleInvalid} onChange={dismissProductError} noValidate>
    <div className="product-top-layout">
      <section className="panel product-step-card product-basics-card">
        <div className="editor-section-title"><span>1</span><div><h2>Temel ürün bilgileri</h2><p>Ürün kartının temel başlığı ve katalog bilgileri.</p></div></div>
        <div className="product-step-grid product-basics-grid">
          <label className="product-title-field">Ürün adı<input value={form.title} onChange={event => updateField('title', event.target.value)} required maxLength={320} /></label>
          <label>Satış durumu<select value={form.status} onChange={event => updateField('status', event.target.value)}><option value="ACTIVE">Satışa Açık</option><option value="ARCHIVED">Satışa Kapalı</option><option value="DRAFT">Taslak</option></select></label>
          <label className="product-brand-field">Marka<select value={form.brandId} onChange={event => updateField('brandId', event.target.value)}><option value="">Marka seçin</option>{activeBrands.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label>
          <label className="product-category-field">Panel kategorisi<select aria-label="Panel kategorisi" value={form.categoryId} onChange={event => { updateField('categoryId', event.target.value); setAttributeSelections({}); setAttributeTextValues({}); setVariantAttributeIds([]); setWebColorAutoEnabled(true); setManualWebColorValueId(''); if (!editProductId) setVariantRows([]) }}><option value="">Kategori seçin</option>{leafCategories.map(item => <option key={item.id} value={item.id}>{item.path}</option>)}</select></label>
          <label>Model kodu<input className="technical-field model-code-value" value={form.modelCode} onChange={event => updateField('modelCode', event.target.value)} /></label>
          <div className="product-identifiers-grid">
            <label>{automaticBarcodeGeneration ? 'Stok kodu (varyantlarda manuel)' : 'Stok Kodu'}<input className="technical-field sku-value" value={form.baseSku} onChange={event => updateField('baseSku', event.target.value)} placeholder={automaticBarcodeGeneration ? 'Varyant satırlarından girin' : 'RAV-BLUZ'} disabled={automaticBarcodeGeneration || variantRows.length > 1} /></label>
            <label>{automaticBarcodeGeneration ? 'Tek ürün barkodu (otomatik oluşturuluyor)' : 'Barkod'}<input className="technical-field barcode-value" value={form.barcode} onChange={event => updateField('barcode', event.target.value)} placeholder={automaticBarcodeGeneration ? 'Model kodu ve sıra numarasından oluşturulur' : 'Varyantsız üründe kullanılır'} disabled={automaticBarcodeGeneration || variantRows.length > 1} /></label>
            <label className="desi-input-field">Desi<span className="desi-inline-control"><input value={form.desi} onChange={event => { setCalculateDesi(false); updateField('desi', event.target.value) }} type="number" min="0.01" step="0.01" required /><button type="button" className="secondary" onClick={() => setDesiCalculatorOpen(true)}>Hesapla</button></span></label>
          </div>
          <div className="wide product-description-field"><span>Açıklama</span><RichTextEditor value={form.description} onChange={value => updateField('description', value)} /></div>
        </div>
      </section>
      <div className="product-top-sidebar">
        <section className="panel product-step-card product-pricing-card">
          <div className="editor-section-title"><span>2</span><div><h2>Fiyat, stok ve vergi</h2><p>Başlangıç değerleri varyantlara uygulanır.</p></div></div>
          <div className="product-step-grid">
            <label>Liste fiyatı<input value={form.listPrice} disabled={variantRows.length > 1} onChange={event => updateField('listPrice', event.target.value)} type="number" min="0" step="0.01" /></label>
            <label>Satış fiyatı<input value={form.salePrice} disabled={variantRows.length > 1} onChange={event => updateField('salePrice', event.target.value)} type="number" min="0" step="0.01" /></label>
            {variantRows.length <= 1 && <label>Maliyet<input value={form.costPrice} onChange={event => updateField('costPrice', event.target.value)} type="number" min="0" step="0.01" /></label>}
            <label>Para birimi<select value={form.currency} onChange={event => updateField('currency', event.target.value)}><option>TRY</option><option>USD</option><option>EUR</option></select></label>
            <label>KDV oranı<select value={form.vatRate} onChange={event => updateField('vatRate', event.target.value)}><option value="1">%1</option><option value="10">%10</option><option value="20">%20</option></select></label>
            <label>KDV dahil mi<select value={form.vatIncluded} onChange={event => updateField('vatIncluded', event.target.value)}><option value="INCLUDED">Evet</option><option value="EXCLUDED">Hayır</option></select></label>
            <label>{showVariantStockTotal || variantRows.length > 1 ? 'Varyantların toplam stoğu' : 'Stok'}<input value={showVariantStockTotal || variantRows.length > 1 ? String(variantStockTotal) : form.initialStock} onChange={event => updateField('initialStock', event.target.value)} type="number" min="0" step="1" readOnly={showVariantStockTotal || variantRows.length > 1} /></label>
            <label>Güvenlik stoğu<input value={form.safetyStock} onChange={event => updateField('safetyStock', event.target.value)} type="number" min="0" step="1" /></label>
          </div>
          {platformCards.length > 0 && <button type="button" className={`marketplace-pricing-open${marketplacePricingOpen ? ' is-open' : ''}`} aria-expanded={marketplacePricingOpen} aria-controls="marketplace-pricing-panel" onClick={() => setMarketplacePricingOpen(open => !open)}>
            <span className="marketplace-pricing-open-title"><strong>Pazaryerlerine Özel Fiyat</strong><UiIcon name="chevronDown" size={16} /></span>
            <small>İşaretlemediğiniz pazaryerleri bu ürünün liste ve satış fiyatını alır.</small>
            <span className="marketplace-pricing-open-count">{Object.keys(channelPricing).length ? `${Object.keys(channelPricing).length} kanalda özel fiyat` : 'Tüm kanallar aynı fiyatta'}</span>
          </button>}
          {marketplacePricingOpen && <div className="marketplace-pricing-inline" id="marketplace-pricing-panel"><div className="marketplace-pricing-bar-list">{platformCards.map(card => { const pricing = channelPriceDraft(card.connection.id); const selected = selectedChannelIds.includes(card.connection.id); const customPrice = card.connection.id in channelPricing; return <fieldset className={`marketplace-pricing-bar${selected ? ' selected' : ''}`} key={card.connection.id} aria-label={`${card.name} pazaryeri fiyatlandırması`}><div className="marketplace-pricing-bar-heading"><span className={`publish-platform-mark ${card.tone}`}><img className={`publish-platform-logo ${platformLogoClass(card.connection.platformCode)}`} src={platformLogoSource(card.connection.platformCode) ?? '/platforms/trendyol.webp'} alt="" aria-hidden="true" /></span><span><strong>{card.name}</strong><small>{customPrice ? 'Özel fiyat açık' : 'Ana fiyatla satılır'}</small></span><label className="marketplace-pricing-switch" title="Bu pazaryeri için özel fiyat"><input type="checkbox" role="switch" aria-label={`${card.name} için özel fiyat`} checked={customPrice} onChange={() => toggleCustomChannelPrice(card.connection.id)} /></label></div><div className="marketplace-pricing-fields"><label>Liste fiyatı<input value={pricing.listPrice} disabled={!customPrice} onChange={event => updateChannelPrice(card.connection.id, 'listPrice', event.target.value)} type="number" min="0" step="0.01" /></label><label>Satış fiyatı<input value={pricing.salePrice} disabled={!customPrice} onChange={event => updateChannelPrice(card.connection.id, 'salePrice', event.target.value)} type="number" min="0" step="0.01" /></label></div></fieldset>})}</div></div>}
        </section>
      </div>
    </div>
    {wizardStep === 1 && <CategoryAttributeMappingPanel
      categoryId={form.categoryId}
      categoryLabel={leafCategories.find(item => item.id === form.categoryId)?.path ?? ''}
      requirements={mappedRequirements}
      isLoading={requirements.isLoading}
      isError={requirements.isError}
      attributeSelections={attributeSelections}
      attributeTextValues={attributeTextValues}
      onToggleValue={toggleAttributeValue}
      onTextChange={(attributeId, value) => setAttributeTextValues(current => ({ ...current, [attributeId]: value }))}
      webColorRequirement={webColorRequirement}
      webColorAutoEnabled={webColorAutoEnabled}
      manualWebColorValueId={manualWebColorValueId}
      onToggleWebColorAuto={toggleWebColorAuto}
      onManualWebColorValueChange={setManualWebColorValueId}
    />}
    {bulkOptionSyncOpen && <div className="workspace-modal-backdrop bulk-option-sync-backdrop" role="presentation" onMouseDown={() => setBulkOptionSyncOpen(false)}><section className="workspace-modal bulk-option-sync-modal" role="dialog" aria-modal="true" aria-labelledby="bulk-option-sync-title" onMouseDown={event => event.stopPropagation()}><header><div><p className="eyebrow">ÜRÜN SEÇENEKLERİ</p><h2 id="bulk-option-sync-title">Toplu eşitleme</h2><p>Seçenek gruplarındaki işaretli değerleri varyant satırlarına ekleyin.</p></div><button type="button" className="modal-close" onClick={() => setBulkOptionSyncOpen(false)} aria-label="Pencereyi kapat"><UiIcon name="close" /></button></header><div className="bulk-option-sync-body"><fieldset><legend>Eşitlenecek gruplar</legend><div className="bulk-option-sync-options">{bulkSyncOptionRequirements.map(item => { const selectedCount = attributeSelections[item.attributeId]?.length ?? 0; const checked = bulkOptionSyncSelection.includes(item.attributeId); const label = isColorOptionName(item.attribute.name) ? 'Renkler' : 'Bedenler'; return <label className={`bulk-option-sync-option${checked ? ' is-selected' : ''}`} key={item.attributeId}><input type="checkbox" checked={checked} disabled={!selectedCount} onChange={() => setBulkOptionSyncSelection(current => checked ? current.filter(id => id !== item.attributeId) : [...current, item.attributeId])} /><span><strong>{label}</strong><small>{selectedCount} seçili değer · {item.attribute.name}</small></span></label> })}</div></fieldset><p className="bulk-option-sync-note">Hiçbir grup seçmezseniz listelenen tüm gruplar birlikte eşitlenir. Tek grup seçildiğinde diğer etkin seçenek ekseni korunur; mevcut varyant satırlarındaki stok, fiyat, barkod ve görseller değiştirilmez.</p></div><footer><button type="button" className="secondary" onClick={() => setBulkOptionSyncOpen(false)}>Vazgeç</button><button type="button" onClick={syncOptionGroups} disabled={!bulkSyncOptionRequirements.length}>{bulkOptionSyncSelection.length ? 'Seçilenleri eşitle' : 'Tümünü eşitle'}</button></footer></section></div>}
    {desiCalculatorOpen && <div className="workspace-modal-backdrop" role="presentation" onMouseDown={() => setDesiCalculatorOpen(false)}><section className="workspace-modal desi-calculator-modal" role="dialog" aria-modal="true" aria-labelledby="desi-calculator-title" onMouseDown={event => event.stopPropagation()}><header><div><h2 id="desi-calculator-title">Desi hesapla</h2><p>En × Boy × Yükseklik / 3000 formülü kullanılır.</p></div><button type="button" className="modal-close" onClick={() => setDesiCalculatorOpen(false)} aria-label="Pencereyi kapat"><UiIcon name="close" /></button></header><div className="desi-calculator-body"><div className="product-step-grid"><label>Ağırlık (kg)<input value={form.weight} onChange={event => updateField('weight', event.target.value)} type="number" min="0" step="0.01" /></label><label>En (cm)<input value={form.width} onChange={event => updateField('width', event.target.value)} type="number" min="0" step="0.1" /></label><label>Boy (cm)<input value={form.length} onChange={event => updateField('length', event.target.value)} type="number" min="0" step="0.1" /></label><label>Yükseklik (cm)<input value={form.height} onChange={event => updateField('height', event.target.value)} type="number" min="0" step="0.1" /></label></div><div className="calculated-field"><small>Hesaplanan desi</small><strong>{desi ? desi.toLocaleString('tr-TR', { maximumFractionDigits: 2 }) : 'Ölçüleri girin'}</strong></div></div><footer><button type="button" className="secondary" onClick={() => setDesiCalculatorOpen(false)}>İptal</button><button type="button" disabled={!desi} onClick={() => { updateField('desi', String(Number(desi.toFixed(2)))); setCalculateDesi(true); setDesiCalculatorOpen(false) }}>Uygula</button></footer></section></div>}

    <div className="product-layout-grid"><div className="product-main-stack">
      <section className="panel product-step-card product-media-card">
        <div className="editor-section-title">
          <span>4</span>
            <div><h2>Görseller</h2><p>JPEG/PNG dosyası yükleyebilirsiniz. Görselleri sürükleyerek sıralayın; aynı modelin diğer renk görselleri de burada görünür ve sıralama kayıtta renk ürünlerine uygulanır.</p></div>
            <div className="product-media-header-actions">
              {(mediaUrls.length > 0 || mediaFiles.length > 0 || variantRows.some(row => row.mediaRefs.length > 0)) && <button type="button" className="secondary product-media-clear-all-button" onClick={clearAllMedia}>Tümünü temizle</button>}
            </div>
        </div>
        <label className="upload-ghost-box product-media-upload">
          <input type="file" accept="image/jpeg,image/png" multiple onChange={event => { handleMediaFiles(Array.from(event.target.files ?? [])); event.currentTarget.value = '' }} />
          <strong>{mediaFiles.length ? `${mediaFiles.length} dosya seçildi` : 'Ürün görsellerini dosya olarak seç'}</strong>
          <small>Adet sınırı yok · JPEG veya PNG · dosya başına en fazla 6 MB</small>
        </label>
        {(mediaUrls.length > 0 || mediaFiles.length > 0 || familyOnlyMediaItems.length > 0) && <div ref={mediaStripRef} className="media-preview-strip" onPointerDown={startMediaTouch} onPointerMove={moveMediaTouch} onPointerUp={endMediaTouch} onPointerCancel={cancelMediaTouch} onContextMenu={event => { if (mediaTouch.current) event.preventDefault() }} onClickCapture={event => { if (suppressMediaClick.current) { event.preventDefault(); event.stopPropagation() } }}>
          {visibleMediaUrls.map((url, index) => {
            const ownedUrl = mediaUrls.find(current => mediaImageKey(current) === mediaImageKey(url))
            const ownIndex = ownedUrl === undefined ? -1 : mediaUrls.findIndex(current => mediaImageKey(current) === mediaImageKey(url))
            const familyItem = familyMediaItems.find(item => mediaImageKey(item.url) === mediaImageKey(url))
            const isProductMedia = ownIndex >= 0
            const deletingProductMedia = deletingProductMediaKey === mediaImageKey(url)
            const pendingKey = familyItem?.mediaIds.join(',') ?? ''
            const deleting = pendingKey !== '' && deletingFamilyMediaKey === pendingKey
            return <figure key={`family-${mediaImageKey(url)}`} data-media-kind="url" data-media-index={index} className={`image-preview-card media-sortable${isProductMedia ? '' : ' family-media-preview'} ${dragOverMedia?.kind === 'url' && dragOverMedia.index === index ? 'is-media-drag-over' : ''}`} draggable={!touchPointer && !submitting && !deletingProductMediaKey} onDragStart={event => { setDraggedMedia({ kind: 'url', index }); event.dataTransfer.effectAllowed = 'move'; event.dataTransfer.setData('text/plain', `url:${index}`) }} onDragOver={event => { if (draggedMedia?.kind !== 'url') return; event.preventDefault(); event.dataTransfer.dropEffect = 'move'; setDragOverMedia({ kind: 'url', index }) }} onDrop={event => { event.preventDefault(); const transferValue = event.dataTransfer.getData('text/plain'); const transferIndex = transferValue.startsWith('url:') ? Number(transferValue.slice(4)) : draggedMedia?.kind === 'url' ? draggedMedia.index : -1; if (draggedMedia?.kind === 'url' && Number.isInteger(transferIndex)) reorderMedia(transferIndex, index) }} onDragEnd={() => { setDraggedMedia(null); setDragOverMedia(null) }}>
              <img src={url} alt={`${form.title || 'Ürün'} ${index + 1}`} className="clickable-thumb" draggable={false} onClick={() => setLightboxImage({ url, title: isProductMedia ? form.title || 'Ürün Görseli' : `${form.title || 'Ürün'} · Renk ailesi` })} title="Büyütmek için tıklayın" />
              {isProductMedia
                ? <button type="button" className="image-remove-btn" disabled={deletingProductMedia || submitting} title="Görseli üründen hemen kaldır" aria-label="Görseli üründen hemen kaldır" onClick={event => { event.stopPropagation(); if (ownedUrl) void removeProductMedia(ownedUrl) }}><UiIcon name={deletingProductMedia ? 'loader' : 'close'} /></button>
                : <button type="button" className="image-remove-btn" disabled={deleting || !familyItem} title="Renk ailesi görselini kaynak kayıtlardan kaldır" aria-label="Renk ailesi görselini kaldır" onClick={event => { event.stopPropagation(); if (familyItem) void removeFamilyMedia(familyItem) }}><UiIcon name={deleting ? 'loader' : 'close'} /></button>}
              {visibleMediaUrls.length > 1 && <MediaMoveButtons index={index} count={visibleMediaUrls.length} disabled={submitting || Boolean(deletingProductMediaKey)} onMove={target => reorderMedia(index, target)} />}
              <figcaption>{isProductMedia ? `${ownIndex === 0 ? 'Ana görsel' : `${ownIndex + 1}. görsel`} · sürükle` : 'Renk varyantı görseli · sürükle'}</figcaption>
            </figure>
          })}
          {mediaFiles.map((item, index) => <LocalImagePreview key={item.id} mediaIndex={index} mediaCount={mediaFiles.length} onMove={target => reorderUploadedMedia(index, target)} file={item.file} alt={`${form.title || 'Ürün'} ${visibleMediaUrls.length + index + 1}`} caption={index === 0 && !visibleMediaUrls.length ? 'Ana görsel' : item.file.name} draggable={!touchPointer && !submitting} isDragOver={dragOverMedia?.kind === 'file' && dragOverMedia.index === index} onDragStart={event => { setDraggedMedia({ kind: 'file', index }); event.dataTransfer.effectAllowed = 'move'; event.dataTransfer.setData('text/plain', `file:${index}`) }} onDragOver={event => { if (draggedMedia?.kind !== 'file') return; event.preventDefault(); event.dataTransfer.dropEffect = 'move'; setDragOverMedia({ kind: 'file', index }) }} onDrop={event => { event.preventDefault(); const transferValue = event.dataTransfer.getData('text/plain'); const transferIndex = transferValue.startsWith('file:') ? Number(transferValue.slice(5)) : draggedMedia?.kind === 'file' ? draggedMedia.index : -1; if (draggedMedia?.kind === 'file' && Number.isInteger(transferIndex)) reorderUploadedMedia(transferIndex, index) }} onDragEnd={() => { setDraggedMedia(null); setDragOverMedia(null) }} onRemove={() => setMediaFiles(files => files.filter(file => file.id !== item.id))} onZoom={url => setLightboxImage({ url, title: form.title || 'Ürün Görseli' })} />)}
        </div>}
      </section>

      <section className="panel product-step-card product-options-card">
        <div className="editor-section-title product-options-header">
          <span>5</span>
          <div>
            <h2>Ürün seçenekleri</h2>
            <p>Seçenek grubu ve değerlerini burada seçin. Mevcut ürünlerde kayıtlı Renk ve Beden değerleri otomatik işaretlenir; yeni seçimler “Ürünleri ekle” ile varyant satırlarına eklenir.</p>
          </div>
        </div>
        {bulkSyncOptionRequirements.length > 0 && <div className="product-options-toolbar"><button type="button" className="secondary product-options-sync-button" onClick={() => { setBulkOptionSyncSelection([]); setBulkOptionSyncOpen(true) }} aria-haspopup="dialog">Toplu eşitleme</button></div>}
        {!form.categoryId ? (
          <div className="unknown"><strong>Önce kategori seçin</strong><p>Kategori seçildiğinde o kategoriye bağlanan özellikler burada görünür.</p></div>
        ) : requirements.isLoading ? (
          <p>Kategori özellikleri yükleniyor…</p>
        ) : requirements.isError ? (
          <div className="unknown"><strong>Kategori özellikleri alınamadı</strong><p>Önce kategori eşleme ekranında ilgili kategorinin özellik başlıklarını hazırlayın.</p></div>
        ) : (
          <div className="attribute-builder-list">
            {visibleOptionRequirements.map(item => {
              const expandable = item.attribute.values.length > 0
              const expanded = !expandable || (expandedOptionGroupIds[item.attributeId] ?? false)
              const valueQuery = attributeValueQueries[item.attributeId] ?? ''
              const filteredValues = filterAttributeOptionValues(sortOptionValues(item.attribute.name, item.attribute.values), valueQuery)
              return (
              <article className={`attribute-builder-card ${expanded ? 'is-open' : ''}`} key={item.attributeId}>
                <button type="button" className="attribute-builder-disclosure" aria-expanded={expanded} aria-controls={expanded ? `option-values-${item.attributeId}` : undefined} disabled={!expandable} onClick={() => setExpandedOptionGroupIds(current => ({ ...current, [item.attributeId]: !expanded }))}>
                  <span className="attribute-builder-disclosure-copy"><strong>{item.attribute.name}</strong><small>{item.attribute.values.length} değer · seçenek grubu</small></span>
                  <span className="attribute-builder-disclosure-meta"><small className={variantAttributeIds.includes(item.attributeId) ? 'attribute-builder-selected' : ''}>{(attributeSelections[item.attributeId]?.length ?? 0) > 0 ? `${attributeSelections[item.attributeId].length} değer seçildi` : 'Değer seçin'}</small>{expandable && <UiIcon name="chevronDown" />}</span>
                </button>
                {expanded && <div id={`option-values-${item.attributeId}`} className="attribute-builder-values">
                {item.attribute.values.length ? (
                  <>
                  <input
                    className="attribute-builder-value-search"
                    type="search"
                    aria-label={`${item.attribute.name} değerlerinde ara`}
                    placeholder={`${item.attribute.name} değerlerinde ara…`}
                    value={valueQuery}
                    onChange={event => setAttributeValueQueries(current => ({ ...current, [item.attributeId]: event.target.value }))}
                  />
                  <div className="option-chip-list">
                    {filteredValues.map(value => {
                      const isSelected = (attributeSelections[item.attributeId] ?? []).includes(value.id)
                      return (
                        <button
                          type="button"
                          key={value.id}
                          title={cleanOptionValue(value.value)}
                          aria-pressed={isSelected}
                          className={`option-chip ${isSelected ? 'active' : ''}`}
                          onClick={() => toggleAttributeValue(item.attributeId, value.id)}
                        >
                          {cleanOptionValue(value.value)}
                        </button>
                      )
                    })}
                  </div>
                  {filteredValues.length === 0 && <p className="attribute-builder-no-values">Eşleşen değer bulunamadı.</p>}
                  </>
                ) : item.attribute.dataType === 'BOOLEAN' ? (
                  <label>Değer<select value={attributeTextValues[item.attributeId] ?? ''} onChange={event => setAttributeTextValues(current => ({ ...current, [item.attributeId]: event.target.value }))}><option value="">Seçin</option><option value="evet">Evet</option><option value="hayır">Hayır</option></select></label>
                ) : (
                  <label>Değer<input value={attributeTextValues[item.attributeId] ?? ''} onChange={event => setAttributeTextValues(current => ({ ...current, [item.attributeId]: event.target.value }))} type={item.attribute.dataType === 'NUMBER' ? 'number' : 'text'} placeholder="Değer girin" /></label>
                )}
                </div>}
              </article>
              )
            })}
            {!visibleOptionRequirements.length && (
              <div className="empty small" style={{ gridColumn: '1 / -1' }}>
                <p>Bu ürün için kayıtlı seçenek grubu bulunamadı. Seçenek Eşitleme ekranından seçenek grubu oluşturabilirsiniz.</p>
              </div>
            )}
          </div>
        )}
        <div className="attribute-variant-action">
          <div>
             <strong>Varyantları oluştur</strong>
             <small>{variantAttributeIds.length ? `${variantAttributeIds.map(id => allRequirements.find(item => item.attributeId === id)?.attribute.name).filter(Boolean).join(' × ')} · ${variantAttributeIds.reduce((total, id) => total * Math.max(1, attributeSelections[id]?.length ?? 0), 1)} kombinasyon` : 'Önce seçenek grubunu ve değerlerini işaretleyin.'}</small>
             <label className="variant-auto-barcode-toggle"><input type="checkbox" checked={automaticBarcodeGeneration} onChange={event => setAutomaticBarcodeGeneration(event.target.checked)} /><span><strong>Barkodları otomatik oluştur</strong></span></label>
          </div>
          <div className="attribute-variant-actions">
            <button type="button" onClick={() => generateVariants()} disabled={!canAddVariantCombinations}>{canAddVariantCombinations ? 'Ürünleri ekle' : 'Seçenekler güncel'}</button>
          </div>
        </div>
      <div className="product-variant-groups-section">
        <div className="editor-section-title"><span>6</span><div><h2>Ürün seçenek grupları</h2><p>İşaretlediğiniz özellik değerlerinin tüm kombinasyonları varyant satırı olur.</p></div></div>
        {variantRows.length > 0 && <>
          <div className={`variant-filter-panel${variantFilterOpen ? ' is-open' : ''}`}>
            <div className="variant-filter-panel-head">
              <button type="button" className="variant-filter-panel-toggle" aria-expanded={variantFilterOpen} aria-label={variantFilterOpen ? 'Varyant filtrelerini kapat' : 'Varyant filtrelerini aç'} onClick={() => setVariantFilterOpen(current => !current)}>
                <span><strong>Varyantları filtrele</strong><span>Renk, beden gibi seçenekleri seçin; eşleşen satırlar aşağıda vurgulansın.</span></span>
                <span className="variant-filter-panel-toggle-action"><small>{variantFilterOpen ? 'Kapat' : 'Aç'}</small><UiIcon name="chevronDown" /></span>
              </button>
              {hasVariantFilters && <div className="variant-filter-panel-summary"><b>{matchingVariantCount} varyant seçildi</b><button type="button" className="variant-filter-clear" onClick={clearVariantFilters}>Filtreleri temizle</button></div>}
            </div>
            {variantFilterOpen && <div className="variant-filter-panel-body">
              {variantFilterGroups.length ? <div className="variant-filter-grid">{variantFilterGroups.map(group => <VariantFilterDropdown key={group.id} group={group} selectedValueIds={variantFilterSelections[group.id] ?? []} onToggle={valueId => toggleVariantFilter(group.id, valueId)} onClear={() => setVariantFilterSelections(current => ({ ...current, [group.id]: [] }))} />)}</div> : <p className="variant-filter-empty">Filtrelemek için seçenek grubu bulunamadı.</p>}
              <div className="variant-bulk-editor">
                <label><span>Stok</span><input aria-label="Tüm varyantların stoku" value={bulkStock} onChange={event => setBulkStock(event.target.value)} type="number" min="0" placeholder="Tüm stoklar" /></label>
                <label><span>Satış fiyatı</span><input aria-label="Tüm varyantların satış fiyatı" value={bulkSalePrice} onChange={event => setBulkSalePrice(event.target.value)} type="number" min="0" step="0.01" placeholder="Tüm satış fiyatları" /></label>
                <label><span>Maliyet</span><input aria-label="Tüm varyantların maliyeti" value={bulkCostPrice} onChange={event => setBulkCostPrice(event.target.value)} type="number" min="0" step="0.01" placeholder="Tüm maliyetler" /></label>
                <label><span>Liste fiyatı</span><input aria-label="Tüm varyantların liste fiyatı" value={bulkListPrice} onChange={event => setBulkListPrice(event.target.value)} type="number" min="0" step="0.01" placeholder="Tüm liste fiyatları" /></label>
                <div className="variant-bulk-action"><span>Toplu uygulama</span><button type="button" className="secondary" onClick={applyBulk} disabled={hasVariantFilters && matchingVariantCount === 0}>{hasVariantFilters ? `${matchingVariantCount} seçilene uygula` : 'Tümüne uygula'}</button></div>
              </div>
            </div>}
          </div>
            <div className="variant-table-toolbar"><span>Varyant görsellerini tek tek veya seçenek değerine göre toplu atayın.</span><div className="variant-table-toolbar-actions"><button type="button" className="secondary variant-clear-button" onClick={clearVariants}>Oluşan varyantları temizle</button><button type="button" className="secondary variant-media-bulk-button" onClick={openBulkVariantMediaPicker} title="Seçenek değerine görsel ata"><VariantImageIcon /> Seçeneklere görsel ata</button></div></div>
        </>}
            {variantRows.length > 0 && <div className="variant-mobile-groups" role="tablist" aria-label="Görünen varyant alanları">{([['codes', 'Kodlar'], ['stock', 'Stok / Fiyat'], ['cost', 'Maliyet'], ['actions', 'İşlemler']] as const).map(([group, label]) => <button key={group} type="button" role="tab" aria-selected={variantMobileGroup === group} className={variantMobileGroup === group ? 'is-on' : undefined} onClick={() => setVariantMobileGroup(group)}>{label}</button>)}</div>}
            <div className={`variant-table-editor is-mobile-${variantMobileGroup}`}>
              <div className="variant-table-head">
                <span>#</span><span>Seçenek</span>
                <span className="variant-table-header-with-action"><span>Barkod</span><div className="variant-header-action-shell" ref={barcodePasteActionRef}><button type="button" className="variant-header-action" onClick={() => { setBarcodeSkuMenuOpen(false); setBarcodePasteMenuOpen(current => !current) }} aria-label="Panodan barkod yapıştırma seçenekleri" aria-haspopup="menu" aria-expanded={barcodePasteMenuOpen} title="Panodan barkod yapıştırma seçenekleri"><BarcodePasteIcon /></button>{barcodePasteMenuOpen && <div className="variant-header-action-menu" role="menu"><button type="button" role="menuitem" disabled={!emptyBarcodeRowCount} onClick={() => void pasteBarcodesFromClipboard('missing')}><span><strong>Boş barkodlara yapıştır</strong><small>Sadece boş satırlar · {emptyBarcodeRowCount} hedef</small></span><UiIcon name="externalLink" /></button><button type="button" role="menuitem" disabled={!variantRows.length} onClick={() => void pasteBarcodesFromClipboard('all')}><span><strong>Tüm barkodları panodan değiştir</strong><small>Varyant sırası · {variantRows.length} hedef</small></span><i aria-hidden="true">!</i></button><p>Satır sayısı hedefle aynı olmalı; sekmeli tabloda ilk sütun kullanılır. Boş veya yinelenen barkodda işlem yapılmaz.</p></div>}</div></span>
                <span className="variant-table-header-with-action"><span>Stok kodu</span><div className="variant-header-action-shell" ref={barcodeSkuActionRef}><button type="button" className="variant-header-action" onClick={() => { setBarcodePasteMenuOpen(false); setBarcodeSkuMenuOpen(current => !current) }} aria-label="Barkoddan doldurma seçenekleri" aria-haspopup="menu" aria-expanded={barcodeSkuMenuOpen} title="Barkoddan stok kodu doldurma seçenekleri"><BarcodeFillIcon /></button>{barcodeSkuMenuOpen && <div className="variant-header-action-menu" role="menu"><button type="button" role="menuitem" disabled={!emptySkuBarcodeRowCount} onClick={() => applyBarcodeToSku('missing')}><span><strong>Eksik stok kodlarını doldur</strong><small>Sadece boş satırlar · {emptySkuBarcodeRowCount} aday</small></span><UiIcon name="externalLink" /></button><button type="button" role="menuitem" disabled={!barcodeRowCount} onClick={() => applyBarcodeToSku('all')}><span><strong>Barkodları stok koduna uygula</strong><small>Barkodu olan {barcodeRowCount} satırı güncelle</small></span><i aria-hidden="true">!</i></button><p>Çakışan barkodlar otomatik olarak atlanır; mevcut kodlar ilk seçenekte korunur.</p></div>}</div></span>
                {(['stock', 'salePrice', 'costPrice', 'listPrice'] as VariantBulkEditField[]).map(field => <span className="variant-table-header-with-action variant-bulk-header" key={field}><span>{variantBulkEditLabels[field]}</span><button type="button" className="variant-bulk-edit-trigger" aria-label={`${variantBulkEditLabels[field]} için toplu değişiklik aç`} aria-haspopup="dialog" aria-controls={variantBulkEdit?.field === field ? 'variant-bulk-edit-dialog' : undefined} aria-expanded={variantBulkEdit?.field === field} title={`${variantBulkEditLabels[field]} toplu değiştir`} onClick={() => openVariantBulkEdit(field)} disabled={!variantRows.length}><UiIcon name="edit" size={14} /></button></span>)}
                <span>İşlem</span>
              </div>
              {variantRows.length ? variantRows.map((row, index) => {
                const matchesFilter = rowMatchesVariantFilters(row)
                const rowPlatforms = row.platformStatuses?.length ? row.platformStatuses : !editProductId ? variantPricingPlatforms : []
                return <div data-variant-row-key={row.key} className={`variant-table-row ${hasVariantFilters && matchesFilter ? 'is-filter-match' : ''} ${hasVariantFilters && !matchesFilter ? 'is-filter-dimmed' : ''} ${draggedVariantKey === row.key ? 'is-dragging' : ''} ${dragOverVariantKey === row.key ? 'is-drag-target' : ''}`} key={row.key}>
                  <div className="variant-row-lead" title="Sıralamak için tutup sürükleyin" aria-label={`${row.optionSignature} varyantını sıralamak için sürükleyin`} onPointerDown={event => beginVariantPointerDrag(event, row.key)}><span className="variant-row-number">{index + 1}</span><span className="variant-drag-handle"><VariantDragHandleIcon /></span></div>
                  <input aria-label={`${index + 1}. varyant seçenekleri`} value={displayVariantOptionSignature(row.optionSignature, row.options)} readOnly />
                  <input aria-label={`${row.optionSignature} barkod`} className="technical-field barcode-value" value={row.barcode} onChange={event => updateVariantRow(row.key, 'barcode', event.target.value)} placeholder="EAN / barkod" />
                  <input aria-label={`${row.optionSignature} stok kodu`} className="technical-field sku-value" value={row.sku} onChange={event => updateVariantRow(row.key, 'sku', event.target.value)} placeholder="Varyant SKU" />
                  <input aria-label={`${row.optionSignature} stok`} value={row.stock} onChange={event => updateVariantRow(row.key, 'stock', event.target.value)} type="number" min="0" step="1" />
                  <input aria-label={`${row.optionSignature} satış fiyatı`} value={row.salePrice} onChange={event => updateVariantRow(row.key, 'salePrice', event.target.value)} type="number" min="0" step="0.01" />
                  <input aria-label={`${row.optionSignature} maliyeti`} value={row.costPrice} onChange={event => updateVariantRow(row.key, 'costPrice', event.target.value)} type="number" min="0" step="0.01" />
                  <input aria-label={`${row.optionSignature} liste fiyatı`} value={row.listPrice} onChange={event => updateVariantRow(row.key, 'listPrice', event.target.value)} type="number" min="0" step="0.01" />
                  <div className="variant-row-actions" aria-label={`${row.optionSignature} varyant işlemleri`}>
                    {rowPlatforms.length ? (() => { const linkedCount = rowPlatforms.filter(item => item.isLinked).length; const isPartial = linkedCount > 0 && linkedCount < rowPlatforms.length; const summaryClass = linkedCount === rowPlatforms.length ? 'is-linked' : isPartial ? 'is-partial' : 'is-unlinked'; return <button type="button" className={`variant-platform-status variant-platform-status-summary variant-row-action ${summaryClass}`} onClick={() => openVariantPlatformPricing(row, rowPlatforms[0])} title={`${rowPlatforms.length} platform fiyatını düzenle`} aria-label={`${row.optionSignature} platform fiyatlarını düzenle`}><UiIcon name="platforms" /></button> })() : <span className="variant-platform-empty">—</span>}
                    <button type="button" className={`variant-media-button variant-row-action ${row.mediaRefs.length ? 'has-media' : ''}`} onClick={() => openVariantMediaPicker(row.key)} aria-label={`${row.optionSignature} görsellerini seç`} title="Varyant görsellerini seç"><VariantImageIcon />{row.mediaRefs.length > 0 && <i aria-hidden="true">{row.mediaRefs.length}</i>}</button>
                    <button type="button" className="variant-row-action variant-row-action-delete" onClick={() => setVariantRows(rows => rows.filter(item => item.key !== row.key))} aria-label={`${row.optionSignature} varyantını sil`} title="Varyantı sil"><UiIcon name="trash" /></button>
                  </div>
                </div>
              }) : <div className="empty small"><strong>Henüz varyant yok</strong><p>Özellik değerlerini seçip “Ürünleri ekle” dediğinizde varyant satırları burada oluşur.</p></div>}
            </div>
      </div>
      </section>
    </div></div>

    <section className="product-publish-step" aria-label={editProductId ? 'Ürün yayınlama ve güncelleme' : 'Ürün yayınlama'}>
      <div className="product-publish-layout">
        <div className="product-publish-main">
          <div className="publish-platform-grid">
            {platformCards.length ? platformCards.map(card => <PublishPlatformCard
              card={card}
              key={card.connection.id}
              productId={editProductId}
              categoryId={form.categoryId}
              selected={selectedChannelIds.includes(card.connection.id)}
              productChecks={productChecks}
              selectedAttributes={publicationAttributeSelections}
              variantMediaWarning={variantsWithoutMediaCount > 0 ? `${variantRows.length} varyanttan ${variantsWithoutMediaCount} tanesine görsel seçilmedi. Varyant tablosundan veya “Seçeneklere görsel ata” ile görsel atayın.` : undefined}
              onSelect={() => updateChannel(card.connection.id)}
              onDeselect={() => setSelectedChannelIds(current => current.filter(id => id !== card.connection.id))}
            />) : <div className="publish-connections-empty"><strong>Aktif bağlantı bulunamadı</strong><p>Yayınlama için önce Platformlar sayfasından aktif bir bağlantı oluşturun.</p><Link to="/integrations">Platformları yönet <UiIcon name="arrowRight" /></Link></div>}
          </div>
          <div className="publish-selection-footer">
            <div><strong>{selectedChannelIds.length ? `${selectedChannelIds.length} platform seçildi` : 'Yayın platformu seçilmedi'}</strong><small>{selectedChannelIds.length ? 'Seçilen her mağazada ayrı yayın kuyruğu ve kontrol listesi bulunur.' : 'Yayınlanacak mağazaları kartların üstünden seçin.'}</small></div>
            {!selectedChannelIds.length && <span className="publish-selection-warning"><UiIcon name="alert" /> En az bir platform seçin</span>}
            <button type="submit" disabled={submitting}>{submitting ? (editProductId ? 'Yayınlanıyor…' : 'Ürün oluşturuluyor…') : (editProductId ? 'Yayınla' : <><UiIcon name="sparkle" /> Ürünü Oluştur</>)}</button>
          </div>
        </div>
      </div>
    </section>

    <section className="product-submit-sticky"><div><strong>{editProductId ? 'Ürün düzenlemeye hazır' : 'Ürün bilgileri hazır'}</strong><p>{variantRows.length || 1} satış satırı · {selectedChannelIds.length} seçili kanal</p></div><div className="product-submit-actions">{editProductId ? <button type="submit" name="intent" value="save" className="secondary" data-submit-intent="save" form="product-creation-form" disabled={submitting}>{submitting ? 'Kaydediliyor…' : 'Kaydet'}</button> : <button type="submit" name="intent" value="create" className="secondary" data-submit-intent="create" form="product-creation-form" disabled={submitting}>{submitting ? 'Oluşturuluyor…' : 'Ürünü oluştur'}</button>}{editProductId && <div className="platform-update-split" role="group" aria-label="Platform güncelleme"><button type="button" className="platform-update-quick" onClick={startQuickPlatformUpdate} disabled={submitting || connections.isLoading} title="Seçili aktif platformlarda yalnız fiyat ve stoku güncelle"><span>Hızlı güncelle</span><UiIcon name="refresh" /></button><button type="button" className="platform-update-settings" onClick={openPlatformUpdateDialog} disabled={submitting || connections.isLoading} aria-label="Platform güncelleme ayarları" title="Platform güncelleme ayarları" aria-haspopup="dialog"><UiIcon name="arrowUp" /></button></div>}<button type="button" onClick={() => setWizardStep(2)}><span className="submit-label-long">Yayınlamaya devam et</span><span className="submit-label-short">Yayınla</span> <UiIcon name="arrowRight" /></button></div></section>
    <ErrorBox error={error ?? categories.error ?? brands.error ?? connections.error} onDismiss={dismissProductError} />
    <OperationFeedbackToast feedback={feedback} onClose={() => { setFeedback(null); setNotice('') }} />
    {barcodePasteMenuOpen && <VariantHeaderActionMenu anchorRef={barcodePasteActionRef}>
      <section className="variant-header-action-menu-group" role="group" aria-label="Barkod üret">
        <p className="variant-header-action-menu-title">Model koduyla üret</p>
        <button type="button" role="menuitem" disabled={!variantRows.length || !form.modelCode.trim()} onClick={() => regenerateVariantCodes('barcode')}><span><strong>Model kodundan barkod üret</strong><small className="variant-code-generation-preview"><span>Barkod: {generatedCodePreview}</span><span>{variantRows.length.toLocaleString('tr-TR')} varyant</span></small></span><UiIcon name="refresh" /></button>
      </section>
      <section className="variant-header-action-menu-group" role="group" aria-label="Panodan barkod yapıştır">
        <p className="variant-header-action-menu-title">Panodan barkod yapıştır</p>
        <button type="button" role="menuitem" disabled={!emptyBarcodeRowCount} onClick={() => void pasteBarcodesFromClipboard('missing')}><span><strong>Boş barkodları doldur</strong><small>{emptyBarcodeRowCount.toLocaleString('tr-TR')} boş varyant</small></span><UiIcon name="arrowRight" /></button>
        <button type="button" role="menuitem" disabled={!variantRows.length} onClick={() => void pasteBarcodesFromClipboard('all')}><span><strong>Tüm barkodları değiştir</strong><small>{variantRows.length.toLocaleString('tr-TR')} varyant sırası</small></span><UiIcon name="alert" /></button>
      </section>
    </VariantHeaderActionMenu>}
    {barcodeSkuMenuOpen && <VariantHeaderActionMenu anchorRef={barcodeSkuActionRef}>
      <section className="variant-header-action-menu-group" role="group" aria-label="Model kodundan stok kodu üret">
        <p className="variant-header-action-menu-title">Model koduyla üret</p>
        <button type="button" role="menuitem" disabled={!variantRows.length || !form.modelCode.trim()} onClick={() => regenerateVariantCodes('sku')}><span><strong>Model kodundan stok kodu üret</strong><small className="variant-code-generation-preview"><span>Stok Kodu: {generatedCodePreview}</span><span>{variantRows.length.toLocaleString('tr-TR')} varyant</span></small></span><UiIcon name="refresh" /></button>
      </section>
      <section className="variant-header-action-menu-group" role="group" aria-label="Stok kodlarını barkodlardan düzenle">
        <p className="variant-header-action-menu-title">Stok kodlarını barkodlardan düzenle</p>
        <button type="button" role="menuitem" disabled={!emptySkuBarcodeRowCount} onClick={() => applyBarcodeToSku('missing')}><span><strong>Eksik stok kodlarını doldur</strong><small>Sadece boş satırlar · {emptySkuBarcodeRowCount.toLocaleString('tr-TR')} satır</small></span><UiIcon name="arrowRight" /></button>
        <button type="button" role="menuitem" disabled={!barcodeRowCount} onClick={() => applyBarcodeToSku('all')}><span><strong>Barkodları stok koduna uygula</strong><small>Stok kodu sütunu · {barcodeRowCount.toLocaleString('tr-TR')} barkodlu satır</small></span><UiIcon name="alert" /></button>
      </section>
    </VariantHeaderActionMenu>}
    {mediaRemovalConfirmation && createPortal(
      <div className="workspace-modal-backdrop media-removal-confirm-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) setMediaRemovalConfirmation(null) }}>
        <section className="workspace-modal media-removal-confirm-modal" role="alertdialog" aria-modal="true" aria-labelledby="media-removal-confirm-title" aria-describedby="media-removal-confirm-description" onMouseDown={event => event.stopPropagation()} onKeyDown={event => { if (event.key === 'Escape') setMediaRemovalConfirmation(null) }}>
          <header>
            <div>
              <p className="eyebrow">GÖRSEL YÖNETİMİ</p>
              <h2 id="media-removal-confirm-title">{mediaRemovalConfirmation.kind === 'product' ? 'Görseli hemen kaldır' : 'Renk ailesi görselini kaldır'}</h2>
            </div>
            <button type="button" className="modal-close" onClick={() => setMediaRemovalConfirmation(null)} aria-label="Görsel kaldırma onayını kapat"><UiIcon name="close" /></button>
          </header>
          <div className="media-removal-confirm-body">
            <p id="media-removal-confirm-description">{mediaRemovalConfirmation.kind === 'product'
              ? 'Bu görsel aynı model ailesindeki ürün ve varyant kayıtlarından hemen kaldırılacak. Pazaryerindeki görsel değiştirilmez.'
              : `Bu renk ailesi görseli ${(mediaRemovalConfirmation.item.sourceProductTitles.length ? mediaRemovalConfirmation.item.sourceProductTitles.join(', ') : 'ürün ailesindeki kaynak ürünler')} kayıtlarından kaldırılacak. İşlem yalnızca paneldeki görsel bağlantılarını etkiler; pazaryerindeki ürün veya görsel silinmez.`}</p>
          </div>
          <footer>
            <button type="button" className="secondary" autoFocus={!touchScreen} onClick={() => setMediaRemovalConfirmation(null)}>Vazgeç</button>
            <button type="button" className="destructive" onClick={confirmMediaRemoval}>Görseli kaldır</button>
          </footer>
        </section>
      </div>,
      document.body
    )}
    {pendingVariantCodeGeneration && createPortal(
      <div className="workspace-modal-backdrop variant-code-confirm-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) setPendingVariantCodeGeneration(null) }}>
        <section className="workspace-modal variant-code-confirm-modal" role="alertdialog" aria-modal="true" aria-labelledby="variant-code-confirm-title" aria-describedby="variant-code-confirm-description" onMouseDown={event => event.stopPropagation()}>
          <header>
            <div>
              <p className="eyebrow">VARYANT KODLARI</p>
              <h2 id="variant-code-confirm-title">{pendingVariantCodeGeneration.target === 'barcode' ? 'Barkodları model kodundan üret' : 'Stok kodlarını model kodundan üret'}</h2>
              <p id="variant-code-confirm-description">Mevcut {pendingVariantCodeGeneration.target === 'barcode' ? 'barkod' : 'stok kodu'} alanı {pendingVariantCodeGeneration.codes.length.toLocaleString('tr-TR')} varyant için değiştirilecek.</p>
            </div>
            <button type="button" className="modal-close" onClick={() => setPendingVariantCodeGeneration(null)} aria-label="Kod üretme onayını kapat"><UiIcon name="close" /></button>
          </header>
          <div className="variant-code-confirm-body">
            <p>Üretilecek kod aralığı</p>
            <code>{pendingVariantCodeGeneration.codes[0]}{pendingVariantCodeGeneration.codes.length > 1 ? ` – ${pendingVariantCodeGeneration.codes.at(-1)}` : ''}</code>
            <small>Onaylayınca kodlar, Kaydet’e basmadan bu formdaki varyantlara uygulanır. Diğer kod sütunu korunur; başka üründe kullanılan kodlar Kaydet sırasında reddedilebilir.</small>
          </div>
          <footer>
            <button type="button" className="secondary" autoFocus={!touchScreen} onClick={() => setPendingVariantCodeGeneration(null)}>Vazgeç</button>
            <button type="button" onClick={confirmVariantCodeGeneration}>{pendingVariantCodeGeneration.target === 'barcode' ? 'Barkodları üret' : 'Stok kodlarını üret'}</button>
          </footer>
        </section>
      </div>,
      document.body
    )}
    {platformUpdateDialogOpen && createPortal(
      <div className="workspace-modal-backdrop platform-update-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) setPlatformUpdateDialogOpen(false) }}>
        <section className="workspace-modal platform-update-modal" role="dialog" aria-modal="true" aria-labelledby="platform-update-title" onMouseDown={event => event.stopPropagation()}>
          <header><div><p className="eyebrow">ÜRÜN GÜNCELLEME</p><h2 id="platform-update-title">Platformlarda Güncelle</h2><p>Önce paneldeki ürün değişiklikleri kaydedilir, ardından seçtiğiniz mağazalara ayrı işler gönderilir.</p></div><button type="button" className="modal-close" onClick={() => setPlatformUpdateDialogOpen(false)} aria-label="Platform güncelleme penceresini kapat"><UiIcon name="close" /></button></header>
          <div className="platform-update-modal-body">
            <fieldset className="platform-update-targets"><legend>Güncellenecek platformlar</legend>
              {productUpdateTargets.length ? productUpdateTargets.map(({ connection, capabilities }) => {
                const selected = platformUpdateConnectionIds.includes(connection.id)
                const writable = capabilities.writable && connection.status.trim().toUpperCase() === 'ACTIVE'
                return <label className={`platform-update-target${selected ? ' is-selected' : ''}${!writable ? ' is-disabled' : ''}`} key={connection.id}>
                  <input type="checkbox" checked={selected} disabled={!writable} onChange={() => setPlatformUpdateConnectionIds(ids => selected ? ids.filter(id => id !== connection.id) : [...ids, connection.id])} />
                  <span className="platform-update-target-copy"><strong>{connection.displayName}</strong><small>{connection.platformCode} · Mağaza {connection.externalStoreId || '—'}</small></span>
                </label>
              }) : <p className="platform-update-empty">Aktif ürün yazma bağlantısı bulunamadı.</p>}
            </fieldset>
            <label className="platform-update-content-toggle"><input type="checkbox" checked={platformUpdateProductInformation} disabled={!platformUpdateConnectionIds.some(id => productUpdateTargets.some(target => target.connection.id === id && target.capabilities.content))} onChange={event => setPlatformUpdateProductInformation(event.target.checked)} /><span><strong>Ürün bilgilerini de güncelle</strong><small>Açıkken ad, açıklama, kategori özellikleri, desi/teslimat bilgileri ve görseller seçilen platformun desteklediği alanlarla gönderilir. Kapalıyken yalnız fiyat ve stok gönderilir.</small></span></label>
            <p className="platform-update-scope"><strong>{platformUpdateConnectionIds.filter(id => productUpdateTargets.some(target => target.connection.id === id && target.capabilities.writable && target.connection.status.trim().toUpperCase() === 'ACTIVE')).length}</strong> platform seçili · Değişiklikler önce kaydedilir, sonra kuyruğa alınır.</p>
            {platformUpdateProductInformation && <p className="platform-update-capability-note">Kategori özellikleri yalnız ilgili platform destekliyorsa gönderilir. Shopify şu anda salt okunur olduğundan seçilemez.</p>}
          </div>
          <footer><button type="button" className="secondary" onClick={() => setPlatformUpdateDialogOpen(false)}>Vazgeç</button><button type="button" onClick={confirmPlatformUpdate} disabled={submitting || !platformUpdateConnectionIds.some(id => productUpdateTargets.some(target => target.connection.id === id && target.capabilities.writable && target.connection.status.trim().toUpperCase() === 'ACTIVE'))}>{submitting ? 'Güncelleniyor…' : 'Güncelle'}</button></footer>
        </section>
      </div>,
      document.body
    )}
    {lightboxImage && <ImageLightboxModal image={lightboxImage} onClose={() => setLightboxImage(null)} />}
    {variantMediaModal && <VariantMediaPickerModal
      mode={variantMediaModal.mode}
      options={mediaChoices}
      selectedRefs={selectedVariantMediaRefs}
      groups={variantMediaModal.mode === 'bulk' ? bulkMediaGroups : undefined}
      selectedGroupId={variantMediaModal.mode === 'bulk' ? variantMediaModal.groupId : undefined}
      selectedValueId={variantMediaModal.mode === 'bulk' ? variantMediaModal.valueId : undefined}
      matchedVariantCount={variantMediaModal.mode === 'bulk' ? selectedBulkMediaMatchCount : undefined}
      matchedGroupVariantCount={variantMediaModal.mode === 'bulk' ? selectedBulkMediaGroupMatchCount : undefined}
      onRefsChange={values => setVariantMediaModal(current => {
        if (!current) return current
        if (current.mode === 'variant') return { ...current, draftRefs: values }
        const key = variantMediaAssignmentKey(current.groupId, current.valueId)
        return { ...current, draftsBySelection: updateVariantMediaAssignmentDraft(current.draftsBySelection, key, values) }
      })}
      onGroupChange={groupId => setVariantMediaModal(current => {
        if (!current || current.mode !== 'bulk') return current
        const group = bulkMediaGroups.find(item => item.id === groupId)
        return { ...current, groupId, valueId: group?.values[0]?.id ?? '' }
      })}
      onValueChange={valueId => setVariantMediaModal(current => current?.mode === 'bulk' ? { ...current, valueId } : current)}
      onApply={applyVariantMediaSelection}
      onClose={() => setVariantMediaModal(null)}
    />}
    {variantBulkEdit && <div className="workspace-modal-backdrop variant-bulk-edit-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) setVariantBulkEdit(null) }}>
      <section id="variant-bulk-edit-dialog" className="workspace-modal variant-bulk-edit-modal" role="dialog" aria-modal="true" aria-labelledby="variant-bulk-edit-title" onMouseDown={event => event.stopPropagation()}>
        <header><div><p className="eyebrow">VARYANTLAR</p><h2 id="variant-bulk-edit-title">Toplu {variantBulkEditLabels[variantBulkEdit.field]} değiştir</h2><p>Filtrelerle eşleşen varyant satırlarına uygulanır.</p></div><button type="button" className="modal-close" onClick={() => setVariantBulkEdit(null)} aria-label="Toplu değişiklik penceresini kapat"><UiIcon name="close" /></button></header>
        <div className="variant-bulk-edit-body">
          {variantBulkEditFilterGroups.length > 0 && <section className="variant-bulk-edit-filters" aria-label="Renk ve beden filtreleri"><strong>Renk ve beden seçin</strong><div>{variantBulkEditFilterGroups.map(group => <VariantFilterDropdown key={group.id} group={group} selectedValueIds={variantBulkEditFilterSelections[group.id] ?? []} onToggle={valueId => setVariantBulkEditFilterSelections(current => ({ ...current, [group.id]: (current[group.id] ?? []).includes(valueId) ? current[group.id].filter(id => id !== valueId) : [...(current[group.id] ?? []), valueId] }))} onClear={() => setVariantBulkEditFilterSelections(current => ({ ...current, [group.id]: [] }))} />)}</div></section>}
          <div className="variant-bulk-edit-target"><span>Uygulanacak varyant</span><strong>{variantBulkEditMatchingRows.length.toLocaleString('tr-TR')} / {variantRows.length.toLocaleString('tr-TR')} satır</strong></div>
          <label htmlFor="variant-bulk-edit-value">Yeni {variantBulkEditLabels[variantBulkEdit.field].toLocaleLowerCase('tr-TR')} değeri<input id="variant-bulk-edit-value" autoFocus={!touchScreen} type="number" min="0" step={variantBulkEdit.field === 'stock' ? '1' : '0.01'} value={variantBulkEdit.value} aria-describedby={variantBulkEdit.error ? 'variant-bulk-edit-help variant-bulk-edit-error' : 'variant-bulk-edit-help'} onChange={event => setVariantBulkEdit(current => current ? { ...current, value: event.target.value, error: '' } : current)} /></label>
          <small id="variant-bulk-edit-help">{variantBulkEdit.field === 'stock' ? 'Stok tam sayı ve sıfırdan büyük veya eşit olmalıdır.' : 'Tutar sıfır veya daha büyük olmalıdır.'}</small>
          {variantBulkEdit.error && <p id="variant-bulk-edit-error" className="variant-bulk-edit-error" role="alert">{variantBulkEdit.error}</p>}
        </div>
        <footer><button type="button" className="secondary" onClick={() => setVariantBulkEdit(null)}>Vazgeç</button><button type="button" onClick={applyVariantBulkEdit} disabled={!variantBulkEditMatchingRows.length}>Uygula</button></footer>
      </section>
    </div>}
  </form></Page>
}

export function ProductDetailPage() {
  const { id } = useParams()
  if (!id) return <p className="unknown">Ürün kimliği bulunamadı.</p>
  return <NewProductPage editProductId={id} />
}

export function CategoriesPage() {
  const client = useQueryClient(); const [error, setError] = useState<unknown>(); const query = useQuery({ queryKey: ['categories'], queryFn: () => loadAllPages<Category>('/catalog/categories') })
  async function submit(event: FormEvent<HTMLFormElement>) { event.preventDefault(); const data = new FormData(event.currentTarget); try { await hubApi('/catalog/categories', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ name: data.get('name'), parentId: data.get('parentId') || null }) }); event.currentTarget.reset(); await client.invalidateQueries({ queryKey: ['categories'] }) } catch (reason) { setError(reason) } }
  return <Page title="Kategoriler" eyebrow="Katalog"><form className="panel inline-form" onSubmit={submit}><label>Kategori adı<input name="name" required /></label><label>Üst kategori kimliği<input name="parentId" /></label><button>Ekle</button><ErrorBox error={error} /></form><div className="tree-list">{query.data?.items.map(item => <article key={item.id} style={{ marginLeft: Math.min(item.depth, 6) * 18 }}><div><strong>{item.name}</strong><small>{item.path}</small></div><Tag>{item.isLeaf ? 'LEAF' : 'PARENT'}</Tag></article>)}</div><ErrorBox error={query.error} /></Page>
}

export function BrandsPage() {
  const client = useQueryClient(); const [error, setError] = useState<unknown>(); const query = useQuery({ queryKey: ['brands'], queryFn: () => loadAllPages<Brand>('/catalog/brands') })
  async function submit(event: FormEvent<HTMLFormElement>) { event.preventDefault(); const data = new FormData(event.currentTarget); try { await hubApi('/catalog/brands', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ name: data.get('name') }) }); event.currentTarget.reset(); await client.invalidateQueries({ queryKey: ['brands'] }) } catch (reason) { setError(reason) } }
  return <Page title="Markalar" eyebrow="Katalog"><form className="panel inline-form" onSubmit={submit}><label>Marka adı<input name="name" required /></label><button>Ekle</button><ErrorBox error={error} /></form><div className="cards">{query.data?.items.map(item => <article className="panel" key={item.id}><strong>{item.name}</strong><Tag>{item.isActive ? 'ACTIVE' : 'DISABLED'}</Tag></article>)}</div><ErrorBox error={query.error} /></Page>
}

export function AttributesPage() {
  const client = useQueryClient(); const [error, setError] = useState<unknown>(); const query = useQuery({ queryKey: ['attributes'], queryFn: () => loadAllPages<Attribute>('/catalog/attributes') })
  async function submit(event: FormEvent<HTMLFormElement>) { event.preventDefault(); const data = new FormData(event.currentTarget); const values = String(data.get('values') || '').split(',').map(x => x.trim()).filter(Boolean).map((value, sortOrder) => ({ value, sortOrder })); try { await hubApi('/catalog/attributes', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ code: data.get('code'), name: data.get('name'), dataType: data.get('dataType'), selectionMode: null, unit: null, values }) }); event.currentTarget.reset(); await client.invalidateQueries({ queryKey: ['attributes'] }) } catch (reason) { setError(reason) } }
  return <Page title="Özellikler" eyebrow="Katalog"><form className="panel form-grid" onSubmit={submit}><label>Kod<input name="code" required /></label><label>Ad<input name="name" required /></label><label>Tip<select name="dataType"><option>TEXT</option><option>NUMBER</option><option>SINGLE_SELECT</option><option>MULTI_SELECT</option><option>BOOLEAN</option></select></label><label>Seçenekler (virgülle)<input name="values" /></label><ErrorBox error={error} /><button>Ekle</button></form><div className="cards">{query.data?.items.map(item => <article className="panel" key={item.id}><div><strong>{item.name}</strong><small>{item.code}</small></div><Tag>{item.dataType}</Tag></article>)}</div><ErrorBox error={query.error} /></Page>
}

export function ImportsPage() {
  const client = useQueryClient(); const [error, setError] = useState<unknown>(); const query = useQuery({ queryKey: ['imports'], queryFn: () => loadAllPages<ImportSession>('/imports') })
  async function create(sourceType: string) { try { await hubApi('/imports', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ sourceType, connectionId: null }) }); await client.invalidateQueries({ queryKey: ['imports'] }) } catch (reason) { setError(reason) } }
  return <Page title="İçe aktarımlar" eyebrow="Katalog" action={<div className="actions"><button type="button" onClick={() => create('CSV')}>CSV başlat</button><button type="button" onClick={() => create('XLSX')}>XLSX başlat</button></div>}><ErrorBox error={error ?? query.error} />{!query.data?.items.length ? <div className="empty">Henüz import oturumu yok.</div> : <div className="table-wrap"><table><thead><tr><th>Kaynak</th><th>Durum</th><th>Satır</th><th></th></tr></thead><tbody>{query.data.items.map(item => <tr key={item.id}><td>{item.sourceType}</td><td><Tag>{statusLabel(item.status)}</Tag></td><td>{item.validRows}/{item.totalRows}</td><td><Link to={`/imports/${item.id}`}>İncele</Link></td></tr>)}</tbody></table></div>}</Page>
}

export function ImportDetailPage() {
  const { id } = useParams(); const client = useQueryClient(); const [error, setError] = useState<unknown>(); const session = useQuery({ queryKey: ['import', id], queryFn: () => hubApi<ImportSession>(`/imports/${id}`), enabled: !!id, refetchInterval: 4000 }); const candidates = useQuery({ queryKey: ['candidates', id], queryFn: () => loadAllPages<Candidate>(`/imports/${id}/candidates`), enabled: session.data?.status === 'REVIEW_REQUIRED' })
  async function upload(event: FormEvent<HTMLFormElement>) { event.preventDefault(); try { await hubApi(`/imports/${id}/source-file`, { method: 'POST', headers: { 'Idempotency-Key': key() }, body: new FormData(event.currentTarget) }); await client.invalidateQueries({ queryKey: ['import', id] }) } catch (reason) { setError(reason) } }
  async function map(event: FormEvent<HTMLFormElement>) { event.preventDefault(); if (!session.data) return; const data = new FormData(event.currentTarget); const headers = String(data.get('headers')).split(',').map(x => x.trim()); const fields = String(data.get('fields')).split(',').map(x => x.trim()); try { await hubApi(`/imports/${id}/column-mapping`, { method: 'PUT', headers: { 'If-Match': `"v${session.data.version}"` }, body: JSON.stringify({ profileName: 'Manuel eşleme', variantGroupKey: null, mappings: headers.map((sourceColumn, sortOrder) => ({ sourceColumn, targetField: fields[sortOrder], sortOrder })) }) }); await client.invalidateQueries({ queryKey: ['import', id] }) } catch (reason) { setError(reason) } }
  async function job(kind: 'preview' | 'apply') { try { await hubApi(`/imports/${id}/${kind}-jobs`, { method: 'POST', headers: { 'Idempotency-Key': key() } }); await client.invalidateQueries({ queryKey: ['import', id] }) } catch (reason) { setError(reason) } }
  async function decide(candidate: Candidate, decision: 'CREATE' | 'LINK' | 'SKIP') { try { await hubApi(`/imports/${id}/decisions/${candidate.id}`, { method: 'PUT', headers: { 'If-Match': `"v${candidate.version}"` }, body: JSON.stringify({ decision, productId: decision === 'LINK' ? candidate.productId : null, variantId: decision === 'LINK' ? candidate.variantId : null }) }); await client.invalidateQueries({ queryKey: ['candidates', id] }); await client.invalidateQueries({ queryKey: ['import', id] }) } catch (reason) { setError(reason) } }
  if (!session.data) return <Page title="İçe aktarım" eyebrow="Katalog"><ErrorBox error={session.error} /><p>Yükleniyor…</p></Page>
  return <Page title={`İçe aktarım ${session.data.id.slice(0, 8)}`} eyebrow="Katalog"><div className="metrics">{[['Durum', session.data.status], ['Toplam', session.data.totalRows], ['Geçerli', session.data.validRows], ['Hatalı', session.data.errorRows]].map(([label, value]) => <article key={label}><small>{label}</small><strong>{label === 'Durum' ? statusLabel(String(value)) : value}</strong></article>)}</div><ErrorBox error={error} />{session.data.status === 'CREATED' && <div className="detail-grid"><form className="panel" onSubmit={upload}><h2>1. Dosya</h2><input name="file" type="file" accept=".csv,.xlsx" required /><button>Yükle</button></form><form className="panel" onSubmit={map}><h2>2. Kolon eşleme</h2><label>Başlıklar<input name="headers" placeholder="Ürün,SKU,Barkod" required /></label><label>Hedefler<input name="fields" placeholder="title,sku,barcode" required /></label><button>Kaydet</button></form></div>}<div className="actions spaced">{session.data.status === 'CREATED' && session.data.sourceAssetId && <button type="button" onClick={() => job('preview')}>Preview oluştur</button>}{session.data.status === 'READY_TO_APPLY' && <button type="button" onClick={() => job('apply')}>Kararları uygula</button>}{session.data.errorRows > 0 && <a className="button-link secondary" href={`/api/v1/imports/${id}/errors.csv`}>Hataları indir</a>}</div>{candidates.data?.items.map(candidate => <article className="candidate" key={candidate.id}><div><Tag>{candidate.matchRule}</Tag><code>{candidate.safeSummary}</code></div><div className="actions"><button type="button" onClick={() => decide(candidate, 'CREATE')}>Yeni</button>{candidate.productId && <button type="button" onClick={() => decide(candidate, 'LINK')}>Eşle</button>}<button type="button" className="secondary" onClick={() => decide(candidate, 'SKIP')}>Atla</button></div></article>)}</Page>
}
