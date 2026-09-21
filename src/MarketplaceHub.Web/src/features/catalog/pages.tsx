import { useEffect, useMemo, useRef, useState, type FormEvent, type MouseEvent as ReactMouseEvent, type ReactNode } from 'react'
import { Link, useParams } from 'react-router'
import { createPortal } from 'react-dom'
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiRequestError, hubApi, loadAllPages, type CursorPage } from '../../shared/api'
import { Callout, Pagination, UiIcon, type UiIconName } from '../../shared/components'
import { sanitizeRichText } from '../../shared/security/sanitizeHtml'
import { productStatusLabel, productStatusTone, statusLabel } from '../../shared/status-labels'
import { platformLogoClass, platformLogoSource } from '../../shared/platform-logos'
import { PlatformSquareMark } from '../../shared/platform-square-mark'
import { appendNotification } from '../../shared/notifications'

type Versioned = { id: string; version: number }
type Category = Versioned & { name: string; path: string; depth: number; isLeaf: boolean; isActive: boolean }
type Brand = Versioned & { name: string; isActive: boolean }
type Attribute = Versioned & { code: string; name: string; dataType: string; values: Array<{ id: string; value: string }>; roles?: string[] | null }
type Variant = Versioned & {
  sku: string; barcode: string | null; modelCode: string | null; optionSignature: string; status: string
  weight: number | null; width: number | null; height: number | null; length: number | null; desi: number | null; costPrice: number | null
  onHand: number; available: number; inventoryVersion: number | null
  offerId: string | null; listPrice: number | null; salePrice: number | null; currency: string | null; offerStatus: string | null
  priceVersion: number | null; offerVersion: number | null; vatRate: number | null; vatInclusion: string | null; roundingMode: string | null; safetyStock: number | null
  mediaUrls?: string[]; options?: Record<string, string>; platformStatuses?: VariantPlatformStatus[]
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
  categoryPath?: string | null; variants: Variant[]; primaryImageUrl: string | null; totalStock: number; startingPrice: number | null; currency: string; modelCode: string | null; activePlatforms: string[] | null; familyMediaUrls?: string[]
  platformStatuses?: ProductPlatformStatus[]
  attributes?: Array<{ attributeId: string; valueId: string | null; textValue: string | null; numberValue: number | null; booleanValue: boolean | null; sortOrder: number }>
  options?: Array<{ id: string; label: string; values: Array<{ id: string; label: string }> }>
  mediaUrls?: string[]
}

function orderMediaUrlsByVariants(variants: Variant[], productMediaUrls: string[], primaryImageUrl: string | null) {
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
type ProductSummary = { totalCount: number; activeCount: number; outOfStockCount: number; lowStockCount: number; platforms: string[]; trendyolCatalogCount?: number | null }
const productPlatformFilterGroups = [
  { label: 'Trendyol', options: [{ value: 'TRENDYOL:ACTIVE', label: 'Aktif' }, { value: 'TRENDYOL:PARTIAL', label: 'Kısmi' }, { value: 'TRENDYOL:PASSIVE', label: 'Pasif' }] },
  { label: 'Shopify', options: [{ value: 'SHOPIFY:ACTIVE', label: 'Aktif' }, { value: 'SHOPIFY:PARTIAL', label: 'Kısmi' }, { value: 'SHOPIFY:PASSIVE', label: 'Pasif' }] }
]
type ImportSession = Versioned & { sourceType: string; status: string; totalRows: number; validRows: number; errorRows: number; reviewRows: number; sourceAssetId: string | null }
type Candidate = Versioned & { matchRule: string; safeSummary: string; productId: string | null; variantId: string | null }
type MarketplaceConnection = { id: string; platformCode: string; displayName: string; externalStoreId: string; status: string }
type ChannelPricingDraft = { listPrice: string; salePrice: string }
type AcceptedJob = { jobId: string }
type ProductSyncJob = { id: string; connectionId: string | null; jobType: string; status: string; progressCurrent: number; progressTotal: number | null; progressPercent: number | null; progressLabel: string | null; progressReceived: number; progressProcessed: number; progressSkipped: number; progressFailed: number; createdAt: string; completedAt: string | null }
type ProductImportMode = 'FULL' | 'NEW_ONLY' | 'EXISTING_ONLY' | 'MAPPING_ONLY'
type ProductImportMethod = 'BULK' | 'SINGLE'

const key = () => crypto.randomUUID()
const isProductImportConnection = (item: MarketplaceConnection) => ['TRENDYOL', 'SHOPIFY'].includes(item.platformCode.trim().toUpperCase()) && ['ACTIVE', 'VERIFIED'].includes(item.status.trim().toUpperCase())
const isProductPublicationConnection = (item: MarketplaceConnection) => item.platformCode.trim().toUpperCase() === 'TRENDYOL' && ['ACTIVE', 'VERIFIED'].includes(item.status.trim().toUpperCase())

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
const ErrorBox = ({ error }: { error: unknown }) => error ? <Callout tone="danger">{error instanceof Error ? error.message : 'İşlem tamamlanamadı.'}</Callout> : null
type OperationFeedback = { message: string; kind: 'success' | 'error' | 'info' }
function OperationFeedbackToast({ feedback, onClose }: { feedback: OperationFeedback | null; onClose: () => void }) {
  useEffect(() => {
    if (feedback) appendNotification(feedback.message, feedback.kind)
  }, [feedback])
  if (!feedback) return null
  const title = feedback.kind === 'success' ? 'İşlem başarılı' : feedback.kind === 'error' ? 'İşlem başarısız' : 'İşlem sürüyor'
  return <div className={`rv-toast rv-toast-${feedback.kind === 'error' ? 'danger' : feedback.kind} operation-feedback-toast ${feedback.kind}`} role={feedback.kind === 'error' ? 'alert' : 'status'} aria-live={feedback.kind === 'error' ? 'assertive' : 'polite'}><span className="rv-toast-icon" aria-hidden="true" /><div className="rv-toast-content"><strong>{title}</strong><p>{feedback.message}</p></div><button type="button" onClick={onClose} aria-label="Durum raporunu kapat"><UiIcon name="close" /></button></div>
}

function LocalImagePreview({ file, alt, caption: _caption, onRemove, onZoom }: { file: File, alt: string, caption: string, onRemove?: () => void, onZoom?: (url: string) => void }) {
  const [url, setUrl] = useState('');
  useEffect(() => {
    const objectUrl = URL.createObjectURL(file);
    setUrl(objectUrl);
    return () => URL.revokeObjectURL(objectUrl);
  }, [file]);
  return <figure className="image-preview-card"><img src={url} alt={alt} className="clickable-thumb" onClick={() => onZoom?.(url)} title="Büyütmek için tıklayın" />{onRemove && <button type="button" className="image-remove-btn" title="Görseli sil" onClick={e => { e.stopPropagation(); onRemove(); }}><UiIcon name="close" /></button>}</figure>;
}
function ImageLightboxModal({ image, onClose }: { image: { url: string; title: string }; onClose: () => void }) {
  return (
    <div className="workspace-modal-backdrop image-lightbox-backdrop" role="presentation" onMouseDown={onClose}>
      <section className="workspace-modal product-image-modal" role="dialog" aria-modal="true" aria-labelledby="catalog-image-modal-title" onMouseDown={e => e.stopPropagation()}>
        <header className="product-image-modal-header">
          <div>
            <h2 id="catalog-image-modal-title">{image.title}</h2>
          </div>
          <button type="button" className="modal-close" onClick={onClose} aria-label="Kapat"><UiIcon name="close" /></button>
        </header>
        <div className="product-image-modal-body">
          <div className="product-image-modal-frame">
            <img src={image.url} alt={`${image.title} büyük ürün görseli`} />
          </div>
        </div>
      </section>
    </div>
  )
}

type ProductMediaOption = { value: string; label: string; url?: string; file?: File }
type VariantMediaGroup = { id: string; name: string; values: Array<{ id: string; value: string }>; attributeId?: string }
type VariantFilterSelections = Record<string, string[]>
type ParsedVariantOption = { name: string; value: string }

function normalizeVariantOptionName(name: string) {
  return name.replace(/[\s_-]+/g, '').toLocaleUpperCase('tr-TR')
}

function isWebColorOptionName(name: string) {
  return ['WEBCOLOR', 'WEBCOLOUR', 'WEBRENK'].includes(normalizeVariantOptionName(name))
}

function isColorOptionName(name: string) {
  return ['RENK', 'COLOR', 'COLOUR', 'WEBCOLOR', 'WEBCOLOUR', 'WEBRENK'].includes(normalizeVariantOptionName(name))
}

function preferredColorOption(options: ParsedVariantOption[]) {
  return options.find(option => isColorOptionName(option.name) && !isWebColorOptionName(option.name))
    ?? options.find(option => isColorOptionName(option.name))
}

function parseVariantOptionSignature(signature: string): ParsedVariantOption[] {
  return signature.split(/\s*\|\s*|_(?=[^_:=]+\s*[:=])/).flatMap(part => {
    const separatorIndex = part.search(/\s*[:=]/)
    if (separatorIndex < 0) return []
    const name = part.slice(0, separatorIndex).trim()
    const value = cleanOptionValue(part.slice(separatorIndex).replace(/^\s*[:=]\s*/, ''))
    return name && value ? [{ name, value }] : []
  })
}

function variantOptionEntries(variant: Pick<Variant, 'optionSignature' | 'options'>) {
  const parsed = parseVariantOptionSignature(variant.optionSignature ?? '')
  if (parsed.length) return parsed
  return Object.entries(variant.options ?? {})
    .filter(([, value]) => value.trim())
    .map(([name, value]) => ({ name, value: cleanOptionValue(value) }))
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
        <div><p className="eyebrow">VARYANT GÖRSELLERİ</p><h2 id="variant-media-picker-title">{mode === 'bulk' ? 'Seçeneklere görsel ata' : 'Varyant görsellerini seç'}</h2><p>{mode === 'bulk' ? 'Seçenek grubu ve değerini seçin; aynı değere sahip tüm varyantlara seçilen görselleri uygulayın.' : 'Ürün görsellerinden bu varyanta ait birden fazla görsel seçin. Sıra, ürün panelindeki görsel sırasına göre kaydedilir.'}</p></div>
        <button type="button" className="modal-close" onClick={onClose} aria-label="Pencereyi kapat"><UiIcon name="close" /></button>
      </header>
      {mode === 'bulk' && groups?.length ? <><div className="variant-media-bulk-fields"><label>Seçenek grubu<select value={selectedGroupId} onChange={event => onGroupChange?.(event.target.value)}><option value="">Seçenek grubu seçin</option>{groups.map(group => <option key={group.id} value={group.id}>{group.name}</option>)}</select></label><label>Seçenek değeri<select value={selectedValueId} onChange={event => onValueChange?.(event.target.value)} disabled={!selectedGroup}><option value="">Değer seçin</option>{selectedGroup?.values.map(value => <option key={value.id} value={value.id}>{value.value}</option>)}</select></label></div>{selectedValue && <p className={`variant-media-bulk-match-summary ${matchedVariantCount ? '' : 'is-empty'}`}>{matchedVariantCount ? <><strong>{selectedGroup?.name}: {selectedValue.value}</strong> seçili — {matchedVariantCount} varyant satırına uygulanacak.</> : <>Bu değerle eşleşen varyant satırı bulunamadı.</>}</p>}</> : null}
      <div className="variant-media-picker-grid">
        {options.length ? options.map(option => <MediaOptionThumb key={option.value} option={option} selected={selectedRefs.includes(option.value)} onClick={() => {
           const next = selectedRefs.includes(option.value)
             ? selectedRefs.filter(value => value !== option.value)
             : [...selectedRefs, option.value]
           onRefsChange(options.filter(item => next.includes(item.value)).map(item => item.value))
         }} />) : <div className="empty small"><strong>Seçilebilir görsel yok</strong><p>Önce ürün görsellerine HTTPS linki veya dosya ekleyin.</p></div>}
      </div>
      <footer><button type="button" className="secondary" onClick={() => onRefsChange([])}>Görselleri kaldır</button><button type="button" className="secondary" onClick={onClose}>Vazgeç</button><button type="button" onClick={onApply} disabled={!options.length || (mode === 'bulk' && (!selectedGroupId || !selectedValueId || !matchedVariantCount))}>{mode === 'bulk' ? selectedRefs.length ? 'Seçeneklere uygula' : 'Görselleri kaldır' : 'Görselleri kaydet'}</button></footer>
    </section>
  </div>
}
const Tag = ({ children }: { children: ReactNode }) => <span className="tag">{children}</span>
const money = (value: number | null | undefined, currency = 'TRY') => value == null ? '—' : new Intl.NumberFormat('tr-TR', { style: 'currency', currency }).format(value)
function Page({ title, eyebrow, action, className, children }: { title: string; eyebrow: string; action?: ReactNode; className?: string; children: ReactNode }) { const productBack = className?.includes('product-add-page'); return <section className={`content stitch-page ${className ?? ''}`}><div className={`page-heading${productBack ? ' product-page-heading' : ''}`}><div className={productBack ? 'product-page-heading-copy' : undefined}>{productBack && <Link className="product-heading-back" to="/products" aria-label="Ürünler listesine dön"><UiIcon name="arrowLeft" /><span>KATALOG</span></Link>}<div className={productBack ? 'product-page-heading-title' : undefined}><p className="eyebrow">{eyebrow}</p><h1>{title}</h1></div></div>{action}</div>{children}</section> }

type QuickEditMode = 'stock' | 'price' | 'both'

function ProductQuickEditModal({ products, connections, mode = 'both', onChanged, onClose, onResult }: { products: Product[]; connections: MarketplaceConnection[]; mode?: QuickEditMode; onChanged: () => Promise<unknown>; onClose: () => void; onResult?: (message: string, kind: 'success' | 'error') => void }) {
  const title = mode === 'stock' ? 'Hızlı stok güncelleme' : 'Hızlı fiyat ve stok düzenleme'
  const eyebrow = mode === 'stock' ? 'HIZLI STOK GÜNCELLEME' : 'TOPLU DÜZENLEME'
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
  const colorLabels = sortedVariants.reduce<Record<string, string>>((result, item) => { const key = colorOf(item); result[key] ??= colorLabelOf(item); return result }, {})
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
  const renderVariantList = (items: typeof variants) => <div className="quick-edit-variants">{items.map(item => <div className="quick-edit-variant" key={item.variant.id}><input type="checkbox" checked={selectedSet.has(item.variant.id)} onChange={() => toggle(item.variant.id)} aria-label={`${item.variant.optionSignature || item.variant.sku} varyantını seç`} /><span><strong>{item.variant.optionSignature || 'Varyant'} · {item.variant.sku}</strong></span><QuickEditVariantControls variant={item.variant} connections={connections} onChanged={onChanged} onSelect={() => setSelectionDraft(current => current.includes(item.variant.id) ? current : [...current, item.variant.id])} /></div>)}</div>
  return <div className="workspace-modal-backdrop product-quick-edit-backdrop" role="presentation" onMouseDown={onClose}>
    <section className="workspace-modal product-quick-edit-modal" role="dialog" aria-modal="true" aria-label={title} onMouseDown={event => event.stopPropagation()}>
      <header>
        <div><p className="eyebrow">{eyebrow}</p><h2>{title}</h2><p className="quick-edit-product-context"><span className="quick-edit-product-title">{contextTitle}</span>{contextModelCode && <span className="quick-edit-product-model">Model kodu: {contextModelCode}</span>}</p></div>
        <button type="button" className="modal-close" onClick={onClose} aria-label="Pencereyi kapat"><UiIcon name="close" /></button>
      </header>
      <form onSubmit={apply}>
        <details className="quick-edit-step" open>
          <summary><span><b>1</b> Varyantları seç</span><UiIcon name="chevronDown" /></summary>
          <div className="quick-edit-filter-row">
            <details className="quick-edit-filter">
              <summary><span>Renk</span><small>{selectedColors.length ? `${selectedColors.length} renk seçildi` : 'Renk seçin'}</small><UiIcon name="chevronDown" /></summary>
              <div className="quick-edit-filter-options">{colorOptions.map(color => <label key={color}><input type="checkbox" checked={selectedColors.includes(color)} onChange={() => toggleColor(color)} /><span>{colorLabels[color] || color}</span><small>{groups[color].length} varyant</small></label>)}</div>
            </details>
            <details className="quick-edit-filter">
              <summary><span>Beden</span><small>{selectedSizes.length ? `${selectedSizes.length} beden seçildi` : 'Beden seçin'}</small><UiIcon name="chevronDown" /></summary>
              <div className="quick-edit-filter-options">{sizeOptions.map(size => <label key={size}><input type="checkbox" checked={selectedSizes.includes(size)} onChange={() => toggleSize(size)} /><span>{size}</span><small>{variants.filter(item => (!selectedColors.length || selectedColors.includes(colorOf(item))) && sizeOf(item) === size).length} varyant</small></label>)}</div>
            </details>
          </div>
          <div className="quick-edit-selection">
            <div className="quick-edit-color-list" aria-label="Renk varyant listesi">
              {colorOptions.map(color => {
                const items = groups[color]
                const isOpen = openColorGroups.includes(color)
                const selectedCount = items.filter(item => selectedSet.has(item.variant.id)).length
                return <section className={`quick-edit-color${isOpen ? ' is-open' : ''}`} key={color}>
                  <button type="button" className="quick-edit-color-toggle" aria-expanded={isOpen} onClick={() => toggleColorGroup(color)}><span><strong>{colorLabels[color] || color}</strong><small>{selectedCount ? `${selectedCount}/${items.length} seçili` : 'Renk varyantlarını göster'}</small></span><b>{items.length}</b><UiIcon name="chevronDown" /></button>
                  {isOpen && <div className="quick-edit-color-variants" role="region" aria-label={`${colorLabels[color] || color} varyantları`}>{renderVariantList(items)}</div>}
                </section>
              })}
            </div>
          </div>
        </details>
        <details className="quick-edit-step quick-edit-pricing-step" open>
          <summary><span><b>2</b> {mode === 'stock' ? 'Stok değerini düzenle' : 'Fiyat ve stok değerini düzenle'}</span><small>{activeSelection.length ? `${activeSelection.length} seçili varyanta uygulanacak` : 'Varyant seçimi bekleniyor'}</small><UiIcon name="chevronDown" /></summary>
          <div className="quick-edit-step-body">
            {activeSelection.length ? <div className="quick-edit-selected-list">{variants.filter(item => activeSelectedSet.has(item.variant.id)).map(item => <span key={item.variant.id}>{item.variant.optionSignature || item.variant.sku}</span>)}</div> : <p className="quick-edit-step-empty">Fiyat/stok değerini şimdi girebilirsiniz; uygulamak için en az bir varyantı işaretleyin.</p>}
            <div className="quick-edit-fields">
              {mode !== 'stock' && <fieldset><legend>Fiyat</legend><label>Liste fiyatı<input type="number" min="0" step="0.01" value={listPrice} onChange={event => setListPrice(event.target.value)} placeholder="Değiştirme" /></label><label>Satış fiyatı<input type="number" min="0" step="0.01" value={salePrice} onChange={event => setSalePrice(event.target.value)} placeholder="Değiştirme" /></label></fieldset>}
              <fieldset><legend>Stok</legend><label>İşlem<select value={stockAction} onChange={event => setStockAction(event.target.value as typeof stockAction)}><option value="SET">Bu sayıya eşitle</option><option value="ADD">Bu kadar ekle (+)</option><option value="SUBTRACT">Bu kadar çıkar (−)</option></select></label><label>Miktar<input type="number" min="0" step="1" value={stockAmount} onChange={event => setStockAmount(event.target.value)} placeholder="Miktar" /></label></fieldset>
            </div>
          </div>
        </details>
        {notice && <p className={notice.startsWith('Başarısız:') ? 'error' : 'notice'} role={notice.startsWith('Başarısız:') ? 'alert' : 'status'}>{notice}</p>}
        <footer className="quick-edit-footer"><span>{activeSelection.length ? `${activeSelection.length} varyant seçildi` : 'Varyant seçimi bekleniyor'}</span><button type="button" className="secondary" onClick={onClose}>Vazgeç</button><button type="submit" disabled={saving || !selectionDraft.length}>{saving ? 'Uygulanıyor…' : 'Seçilenlere uygula'}</button></footer>
      </form>
    </section>
  </div>
}

function QuickEditVariantControls({ variant, connections, onChanged, onSelect }: { variant: Variant; connections: MarketplaceConnection[]; onChanged: () => Promise<unknown>; onSelect: () => void }) {
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
  return <div className="quick-edit-variant-controls" onClick={event => event.stopPropagation()}><label><small>Stok</small><input aria-label={`${variant.sku} stok`} value={stock} onFocus={onSelect} onChange={event => { onSelect(); setStock(Number(event.target.value || 0)) }} onBlur={() => void saveStock()} type="number" min="0" step="1" disabled={savingStock} /></label><label><small>Fiyat</small><input aria-label={`${variant.sku} fiyat`} value={price} onFocus={onSelect} onChange={event => { onSelect(); setPrice(event.target.value === '' ? '' : Number(event.target.value)) }} onBlur={() => void savePrice()} type="number" min="0" step="0.01" disabled={savingPrice} /></label></div>
}

function ProductVariantHover({ count, catalogCount, groups }: { count: number; catalogCount: number; groups: VariantDisplayGroup[] }) {
  const triggerRef = useRef<HTMLDivElement>(null)
  const tooltipRef = useRef<HTMLDivElement>(null)
  const [open, setOpen] = useState(false)
  const [position, setPosition] = useState({ left: 16, top: 16 })
  const maxValueCount = Math.max(0, ...groups.map(group => group.values.length))
  const tooltipSize = maxValueCount <= 3 ? 'is-compact' : maxValueCount <= 6 ? 'is-medium' : 'is-wide'
  const tooltipWidth = maxValueCount <= 3 ? 280 : maxValueCount <= 6 ? 360 : 440

  function updatePosition() {
    const trigger = triggerRef.current?.getBoundingClientRect()
    const tooltip = tooltipRef.current?.getBoundingClientRect()
    if (!trigger) return
    const width = tooltip?.width ?? Math.min(tooltipWidth, Math.max(220, window.innerWidth - 32))
    const height = tooltip?.height ?? Math.max(48, groups.length * 30 + 16)
    const belowTop = trigger.bottom + 8
    const spaceBelow = window.innerHeight - belowTop - 12
    const top = spaceBelow >= height || trigger.top <= height + 20
      ? belowTop
      : Math.max(12, trigger.top - height - 8)
    const left = Math.min(Math.max(16, trigger.left), Math.max(16, window.innerWidth - width - 16))
    setPosition({ left, top })
  }

  function showTooltip() {
    setOpen(true)
    window.requestAnimationFrame(updatePosition)
  }

  useEffect(() => {
    if (!open) return
    const handleViewportChange = () => updatePosition()
    const closeOnEscape = (event: KeyboardEvent) => { if (event.key === 'Escape') setOpen(false) }
    window.addEventListener('resize', handleViewportChange)
    window.addEventListener('scroll', handleViewportChange, true)
    document.addEventListener('keydown', closeOnEscape)
    window.requestAnimationFrame(updatePosition)
    return () => {
      window.removeEventListener('resize', handleViewportChange)
      window.removeEventListener('scroll', handleViewportChange, true)
      document.removeEventListener('keydown', closeOnEscape)
    }
  }, [groups, open])

  return <>
    <div ref={triggerRef} className="product-list-variants product-variant-hover" tabIndex={0} aria-label={`${catalogCount} seçenek, ${count} varyant. Tüm varyantları görmek için üzerine gelin`} title="Tüm varyantları görmek için üzerine gelin" onMouseEnter={showTooltip} onMouseLeave={() => setOpen(false)} onFocus={showTooltip} onBlur={() => setOpen(false)}>
      <strong>{catalogCount} seçenek</strong><span>{count} varyant</span>
    </div>
    {open && createPortal(
      <div ref={tooltipRef} className={`product-variant-tooltip product-variant-tooltip-portal ${tooltipSize}`} role="tooltip" style={{ left: position.left, top: position.top }}>
        {groups.map(group => <div className="product-variant-tooltip-row" key={group.label}><strong>{group.label}:</strong><span className="product-variant-values">{group.values.map(value => <span className={`product-variant-value ${value.quantity === 0 ? 'is-empty' : value.quantity < 5 ? 'is-low' : 'is-healthy'}`} key={value.label}><span>{value.label}</span><b>{value.quantity}</b></span>)}</span></div>)}
      </div>,
      document.body
    )}
  </>
}

function ProductCatalogImage({ url, title, onClick }: { url: string | null; title: string; onClick: (event: ReactMouseEvent<HTMLImageElement>) => void }) {
  const [failed, setFailed] = useState(false)
  if (failed || !url) return <span className="product-list-placeholder" aria-label={`${title} için ürün görseli bulunamadı`}><UiIcon name="image" /></span>
  return <img src={url} alt={title} className="product-list-thumb clickable-thumb" loading="lazy" decoding="async" referrerPolicy="no-referrer" onError={() => setFailed(true)} onClick={onClick} title="Görseli büyütmek için tıklayın" />
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
  const familyListRef = useRef<HTMLDivElement | null>(null)
  const [openLowStockFamilyIds, setOpenLowStockFamilyIds] = useState<Set<string>>(new Set())
  function toggleLowStockFamilyRow(event: ReactMouseEvent<HTMLElement>, groupId: string) {
    event.preventDefault()
    const target = event.currentTarget.closest<HTMLElement>('.low-stock-family-card')
    const list = familyListRef.current
    if (!target || !list) return
    const rowTop = target.getBoundingClientRect().top
    const rowIds = Array.from(list.querySelectorAll<HTMLElement>(':scope > .low-stock-family-card'))
      .filter(card => Math.abs(card.getBoundingClientRect().top - rowTop) < 2)
      .map(card => card.dataset.familyId)
      .filter((id): id is string => Boolean(id))
    setOpenLowStockFamilyIds(current => {
      const next = new Set(current)
      const shouldOpen = !current.has(groupId)
      rowIds.forEach(id => shouldOpen ? next.add(id) : next.delete(id))
      return next
    })
  }

  return <div className="workspace-modal-backdrop low-stock-details-backdrop" role="presentation" onMouseDown={event => { if (event.currentTarget === event.target) onClose() }}>
    <section className="workspace-modal low-stock-details-modal" role="dialog" aria-modal="true" aria-labelledby="low-stock-details-title" onMouseDown={event => event.stopPropagation()}>
      <header>
        <div><p className="eyebrow">STOK TAKİBİ</p><h2 id="low-stock-details-title">Düşük stoklu ürünler</h2><p>Model koduna göre gruplanmış ürünleri, renklerini ve eksik veya kritik stoktaki varyantlarını inceleyin.</p></div>
        <button type="button" className="modal-close" onClick={onClose} aria-label="Düşük stok listesini kapat"><UiIcon name="close" /></button>
      </header>
      <div className="low-stock-details-body">
        {loading && <p className="low-stock-details-state">Düşük stoklu ürünler yükleniyor…</p>}
        {!loading && <ErrorBox error={error} />}
        {!loading && !error && !groups.length && <p className="low-stock-details-state">Düşük stoklu ürün bulunamadı.</p>}
        {!loading && !error && groups.length > 0 && <>
          <div className="low-stock-details-tools">
            <label><span>Arama</span><input value={search} onChange={event => setSearch(event.target.value)} placeholder="Model kodu, ürün veya renk ara…" /></label>
            <label><span>Kategori</span><select value={categoryFilter} onChange={event => setCategoryFilter(event.target.value)}><option value="">Tüm kategoriler</option>{categoryOptions.map(category => <option key={category} value={category}>{category}</option>)}</select></label>
            <label><span>Sıralama</span><select value={sort} onChange={event => setSort(event.target.value as typeof sort)}><option value="MODEL_ASC">Model kodu (A-Z)</option><option value="MODEL_DESC">Model kodu (Z-A)</option><option value="MISSING_DESC">Eksik varyant (çoktan aza)</option><option value="MISSING_ASC">Eksik varyant (azdan çoğa)</option></select></label>
          </div>
          <div className="low-stock-details-summary"><strong>{filteredGroups.length} model</strong><span>{catalogRecordCount} katalog kaydı · {lowVariantCount} eksik varyant</span></div>
          {!filteredGroups.length && <p className="low-stock-details-state">Arama veya filtreye uyan düşük stoklu ürün bulunamadı.</p>}
          <div className="low-stock-family-list" ref={familyListRef}>
            {filteredGroups.map(group => {
              const modelCode = lowStockModelCode(group)
              return <details className="low-stock-family-card" data-family-id={group.id} open={openLowStockFamilyIds.has(group.id)} key={group.id}>
                <summary className="low-stock-family-header" onClick={event => toggleLowStockFamilyRow(event, group.id)}>
                  <ProductCatalogImage url={lowStockProductImage(group.primary)} title={group.primary.title} onClick={event => { event.stopPropagation(); const url = lowStockProductImage(group.primary); if (url) onImageClick(url, group.primary.title) }} />
                  <div><small>Model kodu</small><strong>{modelCode}</strong><span>{group.primary.title}</span></div>
                  <span className="low-stock-family-count">{group.products.length} renk</span>
                  <UiIcon name="chevronDown" />
                </summary>
                <div className="low-stock-color-list">
                  {[...group.products].sort((left, right) => lowStockProductColor(left).localeCompare(lowStockProductColor(right), 'tr', { sensitivity: 'base' })).map(product => {
                    const lowVariants = product.variants.filter(variant => variant.onHand <= 0).sort((left, right) => lowStockVariantLabel(left).localeCompare(lowStockVariantLabel(right), 'tr', { numeric: true, sensitivity: 'base' }))
                    return <details className="low-stock-color-card" key={product.id}>
                      <summary className="low-stock-color-summary"><div><strong>{lowStockProductColor(product)}</strong><span>{product.title}</span></div><span>{lowVariants.length} eksik varyant</span><UiIcon name="chevronDown" /></summary>
                      <div className="low-stock-color-details">
                        <div className="low-stock-variant-heading"><strong>Eksik varyantlar</strong><span>{lowVariants.length} kayıt</span></div>
                        {lowVariants.length ? <div className="low-stock-variant-list">{lowVariants.map(variant => <div className="low-stock-variant-row" key={variant.id}>
                          <div><strong>{lowStockVariantLabel(variant)}</strong><small>SKU: {variant.sku}{variant.barcode ? ` · Barkod: ${variant.barcode}` : ''}</small></div>
                          <span className={`low-stock-value${variant.onHand === 0 ? ' is-empty' : ''}`}><b>{variant.onHand}</b><small>{variant.onHand === 0 ? 'Stoksuz' : 'Kalan'}</small></span>
                        </div>)}</div> : <p className="low-stock-details-muted">Bu renkte kritik stok varyantı bulunamadı.</p>}
                      </div>
                    </details>
                  })}
                </div>
              </details>
            })}
          </div>
        </>}
      </div>
      <footer><span className="low-stock-details-footer-note">Düşük stok: bir renkteki varyantların en az yarısı tükenmiş</span><button type="button" onClick={onClose}>Kapat</button></footer>
    </section>
  </div>
}

function ProductColorRows({ group, selected, onSelect, onQuickEdit, onImageClick, onDelete }: { group: ProductGroup; selected: boolean; onSelect: () => void; onQuickEdit: (mode: QuickEditMode) => void; onImageClick: (url: string, title: string) => void; onDelete: () => void }) {
  const product = group.primary
  const platformStatuses = group.products.flatMap(item => item.platformStatuses ?? [])
  const platformStatusAggregates = new Map<string, { platform: string; platformCode: string; statuses: string[]; matchedVariantCount: number; variantCount: number; isChecking: boolean }>()
  for (const item of platformStatuses) {
    const platform = item.platform?.trim() || 'Platform'
    const platformCode = item.platformCode?.trim().toUpperCase() || (platform.toUpperCase().includes('SHOPIFY') ? 'SHOPIFY' : platform.toUpperCase().includes('TRENDYOL') ? 'TRENDYOL' : 'UNKNOWN')
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
  const unmatchedVariantDetails = new Map<string, { barcodes: string[]; missingBarcodeCount: number }>()
  for (const { variant } of group.variants) {
    for (const item of variant.platformStatuses ?? []) {
      if (item.isLinked) continue
      const key = `${item.platformCode.trim().toUpperCase()}:${item.platform}`
      const current = unmatchedVariantDetails.get(key) ?? { barcodes: [], missingBarcodeCount: 0 }
      const barcode = variant.barcode?.trim()
      if (barcode && !current.barcodes.includes(barcode)) current.barcodes.push(barcode)
      else if (!barcode) current.missingBarcodeCount += 1
      unmatchedVariantDetails.set(key, current)
    }
  }
  const platformCheckingStatuses = ['QUEUED', 'UPDATE_QUEUED', 'BATCH_SUBMITTED', 'BATCH_IN_PROGRESS', 'UPDATE_SUBMITTED', 'UPDATE_IN_PROGRESS', 'APPROVAL_PENDING', 'APPROVAL_PARTIAL_PENDING', 'ARCHIVE_QUEUED', 'ARCHIVE_BATCH_SUBMITTED', 'ARCHIVE_RECONCILING', 'UNARCHIVE_QUEUED']
  const platformCards = [...platformStatusAggregates.entries()].map(([key, item]) => {
    const normalizedStatuses = item.statuses.map(status => status.trim().toLocaleUpperCase('tr-TR'))
    const platformChecking = item.isChecking || normalizedStatuses.some(status => platformCheckingStatuses.includes(status))
    const platformHasError = normalizedStatuses.some(status => ['REJECTED', 'PARTIAL_REJECTED', 'MANUAL_REVIEW', 'LOCKED', 'BLACKLISTED'].includes(status))
    const platformFullyMatched = item.variantCount > 0 && item.matchedVariantCount >= item.variantCount
    const platformPartiallyMatched = item.matchedVariantCount > 0 && item.matchedVariantCount < item.variantCount
    const state = platformChecking ? 'processing' : platformHasError ? 'error' : platformFullyMatched ? 'active' : platformPartiallyMatched ? 'partial' : 'inactive'
    const coverage = item.variantCount ? ` (${item.matchedVariantCount}/${item.variantCount})` : ''
    const unmatched = unmatchedVariantDetails.get(key)
    // Keep the hover summary compact. The complete list made the native
    // browser tooltip span the whole product row and cover the controls.
    const visibleBarcodes = unmatched?.barcodes.slice(0, 3) ?? []
    const remainingBarcodeCount = Math.max(0, (unmatched?.barcodes.length ?? 0) - visibleBarcodes.length)
    const unmatchedBarcodeLabel = unmatched && (visibleBarcodes.length || unmatched.missingBarcodeCount)
      ? ` · Eşleşmeyen barkodlar: ${visibleBarcodes.join(', ')}${remainingBarcodeCount ? `, +${remainingBarcodeCount} barkod` : ''}${unmatched.missingBarcodeCount ? ` · ${unmatched.missingBarcodeCount} varyantta barkod yok` : ''}`
      : ''
    const label = state === 'active'
      ? `Tüm varyantlar ${item.platform} ile eşleşti${coverage}`
      : state === 'partial'
        ? `Bazı varyantlar ${item.platform} ile eşleşti${coverage}`
        : state === 'processing'
          ? `${item.platform} ürün bağlantısı güncelleniyor`
          : state === 'error'
            ? `${item.platform} ürün bağlantısı başarısız veya incelemede`
            : `${item.platform} ürün eşleşmesi bulunamadı`
    return { key, platform: item.platform, platformCode: item.platformCode, state, label: `${label}${unmatchedBarcodeLabel}` }
  })
  const totalStock = group.products.reduce((sum, item) => sum + item.totalStock, 0)
  const prices = group.products.map(item => item.startingPrice).filter((price): price is number => price != null)
  const startingPrice = prices.length ? Math.min(...prices) : null
  const statuses = new Set(group.products.map(item => item.status))
  const status = statuses.size === 1 ? product.status : 'MIXED'
  const statusLabel = productStatusLabel(status)
  const statusTone = productStatusTone(status)
  const variantDisplayGroups = productVariantDisplayGroups(group.variants.map(item => item.variant))
  const modelCode = group.products.map(item => item.modelCode).find(value => value?.trim()) ?? '—'
  return <article className="product-catalog-item color-variant-item product-group-card">
      <div className="product-catalog-row">
        <input className="product-row-select" type="checkbox" aria-label={`${product.title} ürün grubunu seç`} checked={selected} onChange={onSelect} />
        <ProductCatalogImage url={product.primaryImageUrl} title={product.title} onClick={() => onImageClick(product.primaryImageUrl!, product.title)} />
        <div className="product-list-identity"><strong>{product.title}</strong><small>Model Kodu: <code className="technical-text model-code-value">{modelCode}</code></small></div>
        <ProductVariantHover count={group.variants.length} catalogCount={group.products.length} groups={variantDisplayGroups} />
        <button type="button" className="product-list-price clickable-cell" aria-label={`${product.title}: fiyatı düzenle`} onClick={() => onQuickEdit('both')}><strong>{money(startingPrice, product.currency)}</strong></button>
        <button type="button" className="product-list-stock clickable-cell" aria-label={`${product.title}: stoğu düzenle`} onClick={() => onQuickEdit('both')}><strong>{totalStock}</strong></button>
        <div className="product-list-platforms" aria-label="Platform durumları">{platformCards.length ? platformCards.map(card => <span className={`platform-state-icon ${card.state}`} key={card.key} title={card.label} aria-label={card.label}><PlatformSquareMark code={card.platformCode} name={card.label} /><i /></span>) : <span className="platform-state-icon inactive" title="Platform eşleşmesi bulunamadı" aria-label="Platform eşleşmesi bulunamadı"><PlatformSquareMark code="TRENDYOL" /><i /></span>}</div>
        <div className={`product-list-status pill ${statusTone}`.trim()}><span className="dot product-status-dot" aria-hidden="true" /><span className="product-status-label">{statusLabel}</span></div>
        <div className="product-list-actions"><Link className="product-edit-link" to={`/products/${product.id}`} aria-label={`${product.title} ürününü düzenle`} title={group.products.length > 1 ? 'Ürün grubundaki ilk kaydı düzenle' : 'Ürünü düzenle'}><UiIcon className="product-action-icon" name="edit" /></Link><button type="button" className="product-delete-button" onClick={event => { event.stopPropagation(); onDelete() }} aria-label={`${product.title} ürün grubunu sil`} title={group.products.length > 1 ? 'Ürün grubundaki tüm kayıtları sil' : 'Ürünü sil'}><UiIcon className="product-action-icon" name="trash" /></button></div>
      </div>
    </article>
}

type ProductDeleteRequest = {
  productIds: string[]
  product?: Product
  title: string
  description: string
}

function ProductDeleteConfirmModal({ request, deleting, onClose, onConfirm }: { request: ProductDeleteRequest; deleting: boolean; onClose: () => void; onConfirm: () => void }) {
  const isGroup = request.productIds.length > 1
  return <div className="workspace-modal-backdrop product-delete-backdrop" role="presentation" onMouseDown={event => { if (event.currentTarget === event.target && !deleting) onClose() }}>
    <section className="workspace-modal product-delete-modal" role="alertdialog" aria-modal="true" aria-labelledby="product-delete-title" aria-describedby="product-delete-description" onMouseDown={event => event.stopPropagation()}>
      <header>
        <div><p className="eyebrow">KATALOG KAYDINI SİL</p><h2 id="product-delete-title">{isGroup ? 'Ürün grubunu sil' : 'Ürünü sil'}</h2><p id="product-delete-description">Bu işlem yerel katalog kaydını kalıcı olarak kaldırır. Sipariş geçmişi korunur; marketplace ilanı otomatik olarak silinmez.</p></div>
        <button type="button" className="modal-close" onClick={onClose} disabled={deleting} aria-label="Silme penceresini kapat"><UiIcon name="close" /></button>
      </header>
      <div className="product-delete-body">
        <div className="product-delete-summary"><span aria-hidden="true">!</span><div><strong>{request.title}</strong><p>{request.description}</p></div></div>
        <p className="product-delete-warning">Devam ederseniz bu kayıtların varyant, stok, fiyat ve platform eşleşmeleri silinir.</p>
      </div>
      <footer><button type="button" className="secondary" onClick={onClose} disabled={deleting}>Vazgeç</button><button type="button" className="destructive" onClick={onConfirm} disabled={deleting}>{deleting ? 'Siliniyor…' : isGroup ? `${request.productIds.length} kaydı sil` : 'Kalıcı olarak sil'}</button></footer>
    </section>
  </div>
}

export function ProductsPage() {
  const client = useQueryClient(); const [search, setSearch] = useState(''); const [searchFilter, setSearchFilter] = useState(''); const [status, setStatus] = useState(''); const [platform, setPlatform] = useState(''); const [stock, setStock] = useState(''); const [selectedProductIds, setSelectedProductIds] = useState<string[]>([]); const [selectedProductCache, setSelectedProductCache] = useState<Record<string, Product>>({}); const [allProductsSelected, setAllProductsSelected] = useState(false); const [selectingAllProducts, setSelectingAllProducts] = useState(false); const [quickEdit, setQuickEdit] = useState<{ productIds: string[]; mode: QuickEditMode } | null>(null); const [productToast, setProductToast] = useState<{ message: string; kind: 'success' | 'error' | 'info' } | null>(null); const [bulkOpen, setBulkOpen] = useState(false); const [platformFilterOpen, setPlatformFilterOpen] = useState(false); const [deleteRequest, setDeleteRequest] = useState<ProductDeleteRequest | null>(null); const [deletingProducts, setDeletingProducts] = useState(false); const [productImportOpen, setProductImportOpen] = useState(false); const [productImportMethod, setProductImportMethod] = useState<ProductImportMethod>('BULK'); const [productImportConnectionIds, setProductImportConnectionIds] = useState<string[]>([]); const [productImportMode, setProductImportMode] = useState<ProductImportMode>('FULL'); const [productImportLookup, setProductImportLookup] = useState(''); const [productImportIncludeArchived, setProductImportIncludeArchived] = useState(true); const [productImportUpdateExistingProducts, setProductImportUpdateExistingProducts] = useState(true); const [productImporting, setProductImporting] = useState(false); const [lightboxImage, setLightboxImage] = useState<{ url: string; title: string } | null>(null); const [lowStockOpen, setLowStockOpen] = useState(false); const [pageSize, setPageSize] = useState(20); const [pageNumber, setPageNumber] = useState(1); const [pageCursors, setPageCursors] = useState<Record<string, Record<number, string | null>>>({})
  const [pageJumping, setPageJumping] = useState(false)
  const [expandedPlatformGroup, setExpandedPlatformGroup] = useState<string | null>(null)
  const bulkMenuRef = useRef<HTMLDivElement>(null)
  const platformFilterRef = useRef<HTMLDivElement>(null)
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
  const lowStockQuery = useQuery({ queryKey: ['products', 'low-stock-details'], queryFn: () => loadAllPages<Product>('/products?stock=LOW', 200), enabled: lowStockOpen, staleTime: 15_000, refetchOnWindowFocus: true })
  const connectionsQuery = useQuery({ queryKey: ['connections', 'product-price'], queryFn: () => loadAllPages<MarketplaceConnection>('/connections') })
  const productSyncJobsQuery = useQuery({ queryKey: ['jobs', 'product-import'], queryFn: () => hubApi<ProductSyncJob[]>('/jobs', { cache: 'no-store' }), enabled: productImportOpen, refetchInterval: productImportOpen ? 1000 : false, refetchIntervalInBackground: true, refetchOnWindowFocus: true, staleTime: 0 })
  const products = query.data?.items ?? []; const connections = (connectionsQuery.data?.items ?? []).filter(isProductImportConnection); const publicationConnections = (connectionsQuery.data?.items ?? []).filter(isProductPublicationConnection); const selectedImportHasShopify = productImportConnectionIds.some(connectionId => connections.some(connection => connection.id === connectionId && connection.platformCode.trim().toUpperCase() === 'SHOPIFY')); const selectedImportIdentityLabel = selectedImportHasShopify ? 'barkod veya stok kodu' : 'model kodu'; const platforms = summaryQuery.data?.platforms ?? []
  const selectedPlatformFilter = productPlatformFilterGroups.flatMap(group => group.options.map(option => ({ ...option, group: group.label }))).find(option => option.value === platform)
  const selectedPlatformLabel = selectedPlatformFilter ? `${selectedPlatformFilter.group} · ${selectedPlatformFilter.label}` : platform || 'Tüm platformlar'
  const totalCount = query.data?.totalCount ?? products.length; const totalPages = Math.max(1, Math.ceil(totalCount / pageSize)); const currentPage = Math.min(pageNumber, totalPages); const pageProducts = currentPage === pageNumber ? products : []; const pageProductGroups = useMemo(() => productRowsAsCards(pageProducts), [pageProducts])
  const activeProductSyncJobs = useMemo(() => (productSyncJobsQuery.data ?? []).filter(job => ['TRENDYOL_PRODUCT_SYNC', 'SHOPIFY_PRODUCT_SYNC'].includes(job.jobType) && !['SUCCEEDED', 'CANCELLED', 'BLOCKED', 'MANUAL_REVIEW', 'DEAD'].includes(job.status) && (!productImportConnectionIds.length || (job.connectionId && productImportConnectionIds.includes(job.connectionId)))), [productImportConnectionIds, productSyncJobsQuery.data])
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
  useEffect(() => { const timer = window.setTimeout(() => setSearchFilter(search.trim()), 250); return () => window.clearTimeout(timer) }, [search])
  useEffect(() => { setPageNumber(1); setPageCursors({}); setSelectedProductIds([]); setSelectedProductCache({}); setAllProductsSelected(false); setBulkOpen(false) }, [productFilterKey, pageSize])
  useEffect(() => { if (platformFilterOpen) setExpandedPlatformGroup(selectedPlatformFilter?.group ?? null) }, [platformFilterOpen, selectedPlatformFilter?.group])
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
  useEffect(() => {
    if (!bulkOpen && !platformFilterOpen) return
    const closeOnPointerDown = (event: MouseEvent) => {
      if (bulkOpen && !bulkMenuRef.current?.contains(event.target as Node)) setBulkOpen(false)
      if (platformFilterOpen && !platformFilterRef.current?.contains(event.target as Node)) setPlatformFilterOpen(false)
    }
    const closeOnEscape = (event: KeyboardEvent) => { if (event.key === 'Escape') { setBulkOpen(false); setPlatformFilterOpen(false) } }
    document.addEventListener('mousedown', closeOnPointerDown)
    document.addEventListener('keydown', closeOnEscape)
    return () => { document.removeEventListener('mousedown', closeOnPointerDown); document.removeEventListener('keydown', closeOnEscape) }
  }, [bulkOpen, platformFilterOpen])
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
    setProductImportOpen(true)
  }
  async function importProductsFromPlatforms() {
    if (productImporting) return
    if (!productImportConnectionIds.length) { showProductToast('Ürün çekmek için en az bir aktif Trendyol veya Shopify bağlantısı seçin.', 'error'); return }
    if (productImportMethod === 'SINGLE' && productImportConnectionIds.length !== 1) { showProductToast('Tekil çekimde yalnızca bir bağlantı seçin.', 'error'); return }
    if (productImportMethod === 'SINGLE' && !productImportLookup.trim()) { showProductToast(`Tekil çekim için ürün linki veya ${selectedImportIdentityLabel} girin.`, 'error'); return }
    setProductImporting(true)
    try {
      const full = productImportMode === 'FULL'
      const newOnly = productImportMode === 'NEW_ONLY'
      const existingOnly = productImportMode === 'EXISTING_ONLY'
      const mappingOnly = productImportMode === 'MAPPING_ONLY'
      const updateExistingProducts = productImportMethod === 'BULK' && !mappingOnly && (full || existingOnly) ? productImportUpdateExistingProducts : false
      const query = new URLSearchParams({ full: String(productImportMethod === 'BULK' && full), newOnly: String(productImportMethod === 'BULK' && newOnly), existingOnly: String(productImportMethod === 'BULK' && existingOnly), mappingOnly: String(productImportMethod === 'BULK' && mappingOnly), includeArchived: String(productImportIncludeArchived), includeDrafts: String(productImportMethod === 'BULK' && productImportIncludeArchived && selectedImportHasShopify), updateExistingProducts: String(updateExistingProducts) })
      if (productImportMethod === 'SINGLE') query.set('lookup', productImportLookup.trim())
      await Promise.all(productImportConnectionIds.map(connectionId => hubApi<AcceptedJob>(`/connections/${connectionId}/product-sync-jobs?${query.toString()}`, { method: 'POST', headers: { 'Idempotency-Key': key() }, body: '{}' })))
      const modeLabel = productImportMethod === 'SINGLE' ? 'tekil ürün' : full ? 'tam katalog' : productImportMode === 'NEW_ONLY' ? 'ekli olmayan ürün' : productImportMode === 'MAPPING_ONLY' ? 'ürün eşleme' : 'ekli ürün güncelleme'
      const contentLabel = productImportMethod === 'BULK' && (full || existingOnly) ? updateExistingProducts ? ' · mevcut ürün bilgileri güncellenecek' : ' · mevcut ürün bilgileri korunacak' : ''
      showProductToast(`${productImportConnectionIds.length} bağlantı için ${modeLabel} çekimi kuyruğa alındı${productImportIncludeArchived ? selectedImportHasShopify ? ' · arşiv ve taslak ürünler dahil' : ' · arşiv ürünleri dahil' : ''}${contentLabel}.`, 'info')
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

  async function bulkSetProductStatus(newStatus: 'ACTIVE' | 'ARCHIVED') {
    setBulkOpen(false)
    const targetCount = selectedProductIds.length
    if (!targetCount) return
    try {
      for (let index = 0; index < selectedProductIds.length; index += 500) {
        const productIds = selectedProductIds.slice(index, index + 500)
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
    setBulkOpen(false)
    const ids = [...selectedProductIds]
    if (!ids.length) return
    setDeleteRequest({ productIds: ids, title: `${selectedProductCardCount} ürün kartı`, description: `${ids.length} katalog kaydı seçildi. Sipariş geçmişi korunur; yalnızca yerel katalog verileri silinir.` })
  }

  return <Page className="products-page" title="Ürünler" eyebrow="Katalog" action={<div className="products-page-actions page-heading-actions"><button type="button" className="button-link product-import-trigger" onClick={openProductImport}><UiIcon name="download" /> Platformdan Ürün Çek</button><Link className="button-link product-create-trigger" to="/products/new"><UiIcon name="plus" /> Yeni Ürün Ekle</Link></div>}>
    <div className="product-metrics metrics"><article className="product-metric-total"><span className="product-metric-icon" aria-hidden="true"><UiIcon name="bag" /></span><small>Trendyol Ürünleri</small><strong>{summaryQuery.isLoading ? '—' : summaryQuery.data?.trendyolCatalogCount ?? '—'}</strong></article><article className="product-metric-active"><span className="product-metric-icon" aria-hidden="true"><UiIcon name="circleCheck" /></span><small>Paneldeki Aktif Ürün</small><strong>{summaryQuery.isLoading ? '—' : summaryQuery.data?.activeCount ?? 0}</strong></article><article className="product-metric-empty"><span className="product-metric-icon" aria-hidden="true"><UiIcon name="alert" /></span><small>Stoksuz Ürün</small><strong>{summaryQuery.isLoading ? '—' : summaryQuery.data?.outOfStockCount ?? 0}</strong></article><button type="button" className="product-metric-button product-metric-low" onClick={() => setLowStockOpen(true)} aria-haspopup="dialog"><span className="product-metric-icon" aria-hidden="true"><UiIcon name="warehouse" /></span><small>Düşük Stoklu</small><strong>{summaryQuery.isLoading ? '—' : summaryQuery.data?.lowStockCount ?? 0}</strong></button></div>
    <div className="product-toolbar">
      <div className="bulk-menu-shell" ref={bulkMenuRef}>
        <button type="button" className="bulk-action" aria-label={selectedProductIds.length > 0 ? `Toplu işlemler, ${selectedProductCardCount} kart seçili` : 'Toplu işlemler'} aria-expanded={bulkOpen} aria-haspopup="menu" aria-controls={bulkOpen ? 'products-bulk-action-menu' : undefined} onClick={() => setBulkOpen(v => !v)}>
          <span className="bulk-action-label">Toplu işlemler</span>
          {selectedProductIds.length > 0 && <span className="bulk-action-count">{selectedProductCardCount}</span>}
          <UiIcon name="chevronDown" />
        </button>
        {bulkOpen && (
          <div id="products-bulk-action-menu" className="bulk-action-menu" role="menu">
            {!selectedProductIds.length && <div className="bulk-action-empty" role="status">İşlem yapmak için önce en az bir ürün seçin.</div>}
            <button type="button" role="menuitem" disabled={!selectedProductIds.length} onClick={() => { setBulkOpen(false); setQuickEdit({ productIds: selectedProductIds, mode: 'both' }); }}>
              <b>01</b><span>Toplu Fiyat &amp; Stok Düzenle<small>Seçili ürünler</small></span>
            </button>
            <button type="button" role="menuitem" disabled={!selectedProductIds.length} onClick={() => { setBulkOpen(false); setQuickEdit({ productIds: selectedProductIds, mode: 'price' }); }}>
              <b>02</b><span>Toplu Fiyat Düzenle<small>Fiyatları tek seferde güncelle</small></span>
            </button>
            <button type="button" role="menuitem" disabled={!selectedProductIds.length} onClick={() => { setBulkOpen(false); setQuickEdit({ productIds: selectedProductIds, mode: 'stock' }); }}>
              <b>03</b><span>Toplu Stok Düzenle<small>Stokları artır / azalt / eşitle</small></span>
            </button>
            <button type="button" role="menuitem" disabled={!selectedProductIds.length} onClick={() => void bulkSetProductStatus('ACTIVE')}>
              <b>04</b><span>Toplu Satışa Aç<small>Seçili ürünleri satışta yap</small></span>
            </button>
            <button type="button" role="menuitem" className="destructive" disabled={!selectedProductIds.length} onClick={() => void bulkSetProductStatus('ARCHIVED')}>
              <b>05</b><span>Toplu Satışa Kapat<small>Seçili ürünleri arşive al</small></span>
            </button>
            <button type="button" role="menuitem" className="destructive" disabled={!selectedProductIds.length} onClick={() => void bulkDeleteProducts()}>
              <b>06</b><span>Seçili Ürünleri Sil<small>Yerel katalogdan kalıcı olarak kaldır</small></span>
            </button>
          </div>
        )}
      </div>
      <label className="order-search"><UiIcon name="search" /><input aria-label="Ürün ara" placeholder="SKU veya Barkod Ara..." value={search} onChange={event => setSearch(event.target.value)} /></label>
      <select aria-label="Ürün durumu" value={status} onChange={event => setStatus(event.target.value)}><option value="">Tüm Durumlar</option><option value="ACTIVE">{productStatusLabel('ACTIVE')}</option><option value="DRAFT">{productStatusLabel('DRAFT')}</option><option value="ARCHIVED">{productStatusLabel('ARCHIVED')}</option></select>
      <div className="product-platform-filter" ref={platformFilterRef}>
        <button type="button" className={`product-platform-filter-trigger${platformFilterOpen ? ' is-open' : ''}${platform ? ' has-selection' : ''}`} aria-label="Platform filtresi" aria-expanded={platformFilterOpen} aria-haspopup="menu" aria-controls={platformFilterOpen ? 'products-platform-filter-menu' : undefined} onClick={() => setPlatformFilterOpen(value => !value)}>
          <span className="product-platform-filter-trigger-copy"><small>Platform</small><strong>{selectedPlatformLabel}</strong></span><UiIcon name="chevronDown" />
        </button>
        {platformFilterOpen && <div id="products-platform-filter-menu" className="product-platform-filter-menu" role="menu" aria-label="Platform filtresi">
          <div className="product-platform-filter-menu-header"><div><strong>Platform durumu</strong><small>Ürün eşleşmelerini platforma göre filtrele</small></div>{platform && <button type="button" className="product-platform-filter-clear" onClick={() => { setPlatform(''); setPlatformFilterOpen(false) }}>Temizle</button>}</div>
          <button type="button" role="menuitemradio" aria-checked={!platform} className={`product-platform-filter-all${!platform ? ' active' : ''}`} onClick={() => { setPlatform(''); setPlatformFilterOpen(false) }}><span className="product-platform-filter-all-icon"><UiIcon name="grid" /></span><span><strong>Tüm platformlar</strong><small>Platform ayrımı olmadan göster</small></span>{!platform && <UiIcon name="check" />}</button>
          {productPlatformFilterGroups.map(group => { const groupOpen = expandedPlatformGroup === group.label; const selectedGroupOption = group.options.find(option => option.value === platform); return (<section className="product-platform-filter-group" key={group.label} aria-labelledby={`product-platform-filter-${group.label.toLowerCase()}`}>
            <button type="button" className={`product-platform-filter-group-toggle${groupOpen ? ' is-open' : ''}`} aria-expanded={groupOpen} aria-controls={`product-platform-filter-options-${group.label.toLowerCase()}`} onClick={() => setExpandedPlatformGroup(value => value === group.label ? null : group.label)}><span className={`product-platform-filter-logo ${group.label.toLowerCase() === 'shopify' ? 'shopify' : 'trendyol'}`}>{group.label.slice(0, 1)}</span><span className="product-platform-filter-group-toggle-copy"><strong id={`product-platform-filter-${group.label.toLowerCase()}`}>{group.label}</strong><small>{selectedGroupOption ? `${selectedGroupOption.label} · seçili` : 'Durum seçin'}</small></span><UiIcon name="chevronDown" /></button>
            {groupOpen && <div id={`product-platform-filter-options-${group.label.toLowerCase()}`} className="product-platform-filter-options">{group.options.map(option => <button type="button" role="menuitemradio" aria-checked={platform === option.value} className={`product-platform-filter-option state-${option.value.split(':')[1].toLowerCase()}${platform === option.value ? ' active' : ''}`} key={option.value} onClick={() => { setPlatform(option.value); setPlatformFilterOpen(false) }}><span className="product-platform-filter-state-dot" /><span><strong>{option.label}</strong><small>{option.label === 'Aktif' ? 'Tüm varyantlar eşleşti' : option.label === 'Kısmi' ? 'Bazı varyantlar eşleşti' : 'Eşleşme bulunamadı'}</small></span>{platform === option.value && <UiIcon name="check" />}</button>)}</div>}
          </section>)})}
          {platforms.filter(item => !productPlatformFilterGroups.some(group => group.label.localeCompare(item, 'tr-TR', { sensitivity: 'base' }) === 0)).length > 0 && <section className="product-platform-filter-group product-platform-filter-other"><div className="product-platform-filter-group-heading"><span className="product-platform-filter-logo other">+</span><strong>Diğer platformlar</strong></div>{platforms.filter(item => !productPlatformFilterGroups.some(group => group.label.localeCompare(item, 'tr-TR', { sensitivity: 'base' }) === 0)).map(item => <button type="button" role="menuitemradio" aria-checked={platform === item} className={`product-platform-filter-option${platform === item ? ' active' : ''}`} key={item} onClick={() => { setPlatform(item); setPlatformFilterOpen(false) }}><span className="product-platform-filter-state-dot" /><span><strong>{item}</strong><small>Platform eşleşmelerini göster</small></span>{platform === item && <UiIcon name="check" />}</button>)}</section>}
        </div>}
      </div>
      <select aria-label="Stok filtresi" value={stock} onChange={event => setStock(event.target.value)}><option value="">Stok Durumu</option><option value="OUT">Stoksuz</option><option value="LOW">Düşük stok</option><option value="OK">Yeterli stok</option></select>
    </div>
    {selectedProductIds.length > 0 && hasMoreProductsToSelect && <div className={`product-selection-banner${allProductsSelected ? ' is-all' : ''}`} role="status">
      <div className="product-selection-summary" aria-label={allProductsSelected ? `Tüm ${selectedProductCardCount.toLocaleString('tr-TR')} ürün kartı seçildi.` : `${selectedProductCardCount.toLocaleString('tr-TR')} ürün kartı seçildi.`}><span className="product-selection-count" aria-hidden="true">{selectedProductCardCount.toLocaleString('tr-TR')}</span><div><strong>{allProductsSelected ? 'Tüm ürün kartları seçildi.' : 'ürün kartı seçildi.'}</strong>{allProductsSelected && <span>{selectedProductIds.length.toLocaleString('tr-TR')} katalog kaydı toplu işlemlere dahil.</span>}</div></div>
      {allProductsSelected ? <button type="button" className="secondary" onClick={() => { setSelectedProductIds([]); setSelectedProductCache({}); setAllProductsSelected(false) }}>Seçimi temizle</button> : <button type="button" onClick={() => void selectAllFilteredProducts()} disabled={selectingAllProducts}>{selectingAllProducts ? 'Tüm sayfalar seçiliyor…' : `Tüm ${totalCount.toLocaleString('tr-TR')} kaydı seç`}</button>}
    </div>}
    <ErrorBox error={query.error ?? summaryQuery.error ?? connectionsQuery.error} />
    <section className="product-catalog-workspace">
      <header className="product-catalog-workspace-head"><div><h2>Ürün kataloğu</h2><p>{totalCount.toLocaleString('tr-TR')} ürün kartı</p></div>{totalCount > 0 && <label className="invoice-reference-page-size">Sayfa başına<select aria-label="Sayfa başına ürün kartı" value={pageSize} onChange={event => setPageSize(Number(event.target.value))}>{[20, 50, 100].map(value => <option key={value} value={value}>{value}</option>)}</select><span>kart</span></label>}</header>
    {query.isLoading && !pageProducts.length ? <p>Yükleniyor…</p> : !pageProducts.length ? <div className="empty">Filtrelerle eşleşen ürün yok.</div> : (
      <div className="product-catalog-scroll">
        <div className="product-catalog-table preferred-product-catalog">
          <div className="product-catalog-head">
            <label className="product-select-all"><input type="checkbox" checked={allVisibleSelected} onChange={toggleAllVisible} aria-label={`${pageProductGroups.length} ürün kartının tümünü seç`} title={`Yalnızca bu sayfadaki ${pageProductGroups.length} kartı seçer`} /><span>Ürün Detayı</span></label>
            <span>Varyant</span><span>Fiyat</span><span>Stok</span><span>Platform Durumu</span><span>Durum</span><span>İşlem</span>
          </div>
          {pageProductGroups.map(group => (
            <ProductColorRows key={group.id} group={group} selected={group.products.every(product => selectedProductIds.includes(product.id))} onSelect={() => toggleProductGroup(group)} onQuickEdit={mode => setQuickEdit({ productIds: group.products.map(product => product.id), mode })} onImageClick={(url, title) => setLightboxImage({ url, title })} onDelete={() => requestDeleteProductGroup(group)} />
          ))}
        </div>
      </div>
    )}
    {totalCount > 0 && <div className="order-pagination product-pagination"><span>Toplam {totalCount.toLocaleString('tr-TR')} adet</span><Pagination className="product-pagination-controls" page={currentPage} totalPages={totalPages} hasNext={currentPage < totalPages && Boolean(nextPageCursor)} onPageChange={goToPage} onPrevious={() => setPageNumber(value => Math.max(1, value - 1))} onNext={goToNextPage} disabled={query.isFetching || pageJumping} /></div>}
    </section>
   {quickEdit && <ProductQuickEditModal products={quickEditProducts} connections={publicationConnections} mode={quickEdit.mode} onChanged={refresh} onResult={showProductToast} onClose={() => setQuickEdit(null)} />}
    {deleteRequest && <ProductDeleteConfirmModal request={deleteRequest} deleting={deletingProducts} onClose={() => setDeleteRequest(null)} onConfirm={() => void confirmProductDelete()} />}
    {productImportOpen && <div className="workspace-modal-backdrop product-import-backdrop" role="presentation" onMouseDown={() => !productImporting && setProductImportOpen(false)}><section className="workspace-modal product-import-modal" role="dialog" aria-modal="true" aria-labelledby="product-import-title" onMouseDown={event => event.stopPropagation()}><header><div><p className="eyebrow">PLATFORM KATALOĞU</p><h2 id="product-import-title">Ürünleri platformdan çek</h2><p>Seçtiğiniz aktif marketplace bağlantılarındaki ürünleri yerel kataloğa salt-okunur olarak alın.</p></div><button type="button" className="modal-close" onClick={() => setProductImportOpen(false)} disabled={productImporting} aria-label="Pencereyi kapat"><UiIcon name="close" /></button></header><div className="product-import-body"><fieldset><legend>Platform bağlantıları</legend>{connections.length ? <div className="product-import-connections">{connections.map(connection => { const selected = productImportConnectionIds.includes(connection.id); return <label key={connection.id} className={`product-import-connection${selected ? ' selected' : ''}`}><input type="checkbox" checked={selected} onChange={() => setProductImportConnectionIds(ids => selected ? ids.filter(id => id !== connection.id) : productImportMethod === 'SINGLE' ? [connection.id] : [...ids, connection.id])} /><span><strong>{connection.displayName}</strong><small>{connection.platformCode} · {connection.externalStoreId} · {statusLabel(connection.status)}</small></span></label> })}</div> : <p className="product-import-empty">Ürün çekmeye uygun aktif veya doğrulanmış marketplace bağlantısı bulunamadı.</p>}</fieldset><div className="product-import-tabs" role="tablist" aria-label="Ürün aktarım türü"><button type="button" role="tab" aria-selected={productImportMethod === 'BULK'} className={'product-import-tab' + (productImportMethod === 'BULK' ? ' selected' : '')} title="Seçtiğiniz bağlantıların kataloğunu toplu olarak tarar." onClick={() => setProductImportMethod('BULK')}>Toplu ürün aktarımı</button><button type="button" role="tab" aria-selected={productImportMethod === 'SINGLE'} className={'product-import-tab' + (productImportMethod === 'SINGLE' ? ' selected' : '')} title={`Bir ürün linki veya ${selectedImportIdentityLabel} ile yalnızca tek ürün aktarır.`} onClick={() => { setProductImportMethod('SINGLE'); setProductImportConnectionIds(ids => ids.slice(0, 1)) }}>Tek ürün aktarımı</button></div>{productImportMethod === 'SINGLE' ? <fieldset><legend>Tek ürün aktarımı</legend><label className="product-import-mode product-import-lookup-field selected" title={`Bağlı mağazanın ürün linki veya ${selectedImportIdentityLabel} ile yalnızca istediğiniz ürünü aktarın.`}><span><strong>Ürün linki veya {selectedImportIdentityLabel}</strong><input className="product-import-lookup-input" value={productImportLookup} onChange={event => setProductImportLookup(event.target.value)} placeholder={selectedImportHasShopify ? 'Shopify: https://magaza.myshopify.com/products/urun veya barkod' : 'Trendyol: ürün linki veya model kodu'} /></span></label></fieldset> : <fieldset><legend>Ürün çekim seçenekleri</legend><div className="product-import-scan-grid"><label className={'product-import-mode' + (productImportMode === 'FULL' ? ' selected' : '')} title="Tüm ürünleri baştan okur ve mevcut eşleşmeleri günceller."><input type="radio" name="product-import-mode" value="FULL" checked={productImportMode === 'FULL'} onChange={() => setProductImportMode('FULL')} /><span><strong>Tam katalog taraması</strong><small>Tüm ürünleri okur ve yerel kayıtları günceller.</small></span></label><label className={'product-import-mode' + (productImportMode === 'NEW_ONLY' ? ' selected' : '')} title={`Panelde bulunmayan ${selectedImportIdentityLabel} değerlerini ve mevcut ürünlere eklenen yeni varyantları aktarır.`}><input type="radio" name="product-import-mode" value="NEW_ONLY" checked={productImportMode === 'NEW_ONLY'} onChange={() => setProductImportMode('NEW_ONLY')} /><span><strong>Yeni ürünleri aktar</strong><small>Panelde bulunmayan ürünleri ekler.</small></span></label><label className={'product-import-mode' + (productImportMode === 'EXISTING_ONLY' ? ' selected' : '')} title={`Panelde kayıtlı ${selectedImportIdentityLabel} değerlerini günceller; yeni değerleri eklemez.`}><input type="radio" name="product-import-mode" value="EXISTING_ONLY" checked={productImportMode === 'EXISTING_ONLY'} onChange={() => setProductImportMode('EXISTING_ONLY')} /><span><strong>Mevcut ürünleri güncelle</strong><small>Kayıtlı ürünlerin bilgilerini yeniler.</small></span></label><label className={'product-import-mode' + (productImportMode === 'MAPPING_ONLY' ? ' selected' : '')} title="Yalnızca mevcut panel ürünleriyle platform ürünleri arasında bağlantı kurar; ürün içeriğini, fiyatı ve stoğu değiştirmez."><input type="radio" name="product-import-mode" value="MAPPING_ONLY" checked={productImportMode === 'MAPPING_ONLY'} onChange={() => setProductImportMode('MAPPING_ONLY')} /><span><strong>Ürün eşleme</strong><small>Sadece mevcut ürünleri platform karşılıklarına bağlar.</small></span></label></div></fieldset>}{productImportMethod === 'BULK' && <fieldset><legend>Seçenekler</legend>{(productImportMode === 'FULL' || productImportMode === 'EXISTING_ONLY') && <label className={'product-import-mode' + (productImportUpdateExistingProducts ? ' selected' : '')} title="Açıkken platformdaki ürün bilgileri mevcut ürünlerin üzerine yazılır; kapalıyken yerel içerik korunur."><input type="checkbox" checked={productImportUpdateExistingProducts} onChange={event => setProductImportUpdateExistingProducts(event.target.checked)} /><span><strong>Mevcut ürün bilgilerini güncelle</strong><small>{productImportUpdateExistingProducts ? 'Açık: ad, açıklama, kategori, marka, varyant ve görseller güncellenir.' : 'Kapalı: mevcut ürün içeriği ve görseller korunur; stok/fiyat gözlemleri yine alınır.'}</small></span></label>}<label className={'product-import-mode' + (productImportIncludeArchived ? ' selected' : '')} title={selectedImportHasShopify ? 'Shopify aktif, arşivlenmiş ve taslak ürünleri birlikte kapsar.' : 'Aktif ve arşivlenmiş varyantların birlikte aktarılıp aktarılmayacağını belirler.'}><input type="checkbox" checked={productImportIncludeArchived} onChange={event => setProductImportIncludeArchived(event.target.checked)} /><span><strong>{selectedImportHasShopify ? 'Arşiv ve taslak ürünlerini dahil et' : 'Arşiv ürünlerini dahil et'}</strong><small>{productImportIncludeArchived ? selectedImportHasShopify ? 'Açık: aktif, arşivlenmiş ve taslak ürünler birlikte getirilir.' : 'Açık: aktif ve arşivlenmiş varyantlar birlikte getirilir.' : selectedImportHasShopify ? 'Kapalı: yalnızca yayınlanmış aktif ürünler alınır.' : 'Kapalı: yalnızca aktif varyantlar panele aktarılır.'}</small></span></label></fieldset>}{activeProductSyncJobs.length > 0 && <section className="product-import-progress" aria-live="polite"><div className="product-import-progress-heading"><strong>Devam eden aktarmalar</strong><small>{activeProductSyncJobs.length} işlem</small></div>{activeProductSyncJobs.map(job => { const received = Math.max(0, job.progressReceived); const total = job.progressTotal != null && job.progressTotal >= received ? job.progressTotal : null; const percent = job.progressPercent != null ? Math.min(100, Math.max(0, job.progressPercent)) : total != null && total > 0 ? Math.min(100, Math.max(0, Math.floor(received * 100 / total))) : null; const fallbackLabel = job.status === 'PENDING' ? 'Kuyrukta bekliyor' : 'Aktarım çalışıyor'; const progressLabel = job.progressLabel && job.progressTotal != null && job.progressReceived > job.progressTotal ? job.progressLabel.replace(/^[^·]+·\s*/, `${received.toLocaleString('tr-TR')} · `) : (job.progressLabel ?? fallbackLabel); const mappingProgress = job.progressLabel?.includes('Eşlenen') ?? false; return <article key={job.id}><div className="product-import-progress-top"><span>{progressLabel}</span><strong>{percent == null ? '—' : `%${percent}`}</strong></div><div className={`product-import-progress-track${percent == null ? ' indeterminate' : ''}`}><i style={percent == null ? undefined : { width: `${percent}%` }} /></div><div className="product-import-progress-bottom"><small className="product-import-progress-counters"><span>Alınan {received.toLocaleString('tr-TR')}</span><span>{mappingProgress ? 'Eşlenen' : 'İşlenen'} {job.progressProcessed.toLocaleString('tr-TR')}</span><span>{mappingProgress ? 'Eşleşmeyen' : 'Atlanan'} {job.progressSkipped.toLocaleString('tr-TR')}</span><span>Hatalı {job.progressFailed.toLocaleString('tr-TR')}</span></small><button type="button" className="secondary" disabled={cancelProductSync.isPending} onClick={() => cancelProductSync.mutate(job.id)}>Durdur</button></div></article> })}</section>}</div><footer><button type="button" className="secondary" onClick={() => setProductImportOpen(false)} disabled={productImporting}>Vazgeç</button><button type="button" onClick={() => void importProductsFromPlatforms()} disabled={productImporting || !productImportConnectionIds.length}>{productImporting ? 'Kuyruğa alınıyor…' : 'Ürünleri panele çek'}</button></footer></section></div>}
    {productToast && <div className={`rv-toast rv-toast-${productToast.kind === 'error' ? 'danger' : productToast.kind} product-operation-toast ${productToast.kind}`} role={productToast.kind === 'error' ? 'alert' : 'status'} aria-live={productToast.kind === 'error' ? 'assertive' : 'polite'}><span className="rv-toast-icon operation-feedback-icon" aria-hidden="true" /><div className="rv-toast-content"><strong>{productToast.kind === 'success' ? 'Güncellendi' : productToast.kind === 'info' ? 'İşlem sürüyor' : 'Başarısız'}</strong><p>{productToast.message}</p></div><button type="button" onClick={() => setProductToast(null)} aria-label="Durum bildirimini kapat"><UiIcon name="close" /></button></div>}
    {lowStockOpen && <LowStockDetailsModal products={lowStockQuery.data?.items ?? []} loading={lowStockQuery.isLoading} error={lowStockQuery.error} onClose={() => setLowStockOpen(false)} onImageClick={(url, title) => setLightboxImage({ url, title })} />}
    {lightboxImage && <ImageLightboxModal image={lightboxImage} onClose={() => setLightboxImage(null)} />}
  </Page>
}

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
  platformStatuses?: VariantPlatformStatus[]
}
type ProductAttributePayload = { attributeId: string; valueId: string | null; textValue: string | null; numberValue: number | null; booleanValue: boolean | null; sortOrder: number }
function marketplacePlatformName(platform: VariantPlatformStatus) {
  const code = platform.platformCode.trim().toLocaleUpperCase('tr-TR')
  return code === 'SHOPIFY' ? 'Shopify' : code === 'TRENDYOL' ? 'Trendyol' : platform.platform
}
function marketplacePriceLabel(value: number | null | undefined, currency: string | null | undefined) {
  return value == null ? 'Tanımlı değil' : `${value.toLocaleString('tr-TR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })} ${currency ?? ''}`.trim()
}
function LegacyVariantPlatformPricingModal({ row, platforms, selectedPlatform, draft, saving, onClose, onSelectPlatform, onDraftChange, onSave }: { row: VariantDraft; platforms: VariantPlatformStatus[]; selectedPlatform: VariantPlatformStatus; draft: ChannelPricingDraft; saving: boolean; onClose: () => void; onSelectPlatform: (platform: VariantPlatformStatus) => void; onDraftChange: (field: keyof ChannelPricingDraft, value: string) => void; onSave: () => void }) {
  return <div className="workspace-modal-backdrop variant-platform-pricing-backdrop" role="presentation" onMouseDown={() => !saving && onClose()}><section className="workspace-modal variant-platform-pricing-modal" role="dialog" aria-modal="true" aria-labelledby="variant-platform-pricing-title" onMouseDown={event => event.stopPropagation()}><header><div><p className="eyebrow">PLATFORM FİYATLARI</p><h2 id="variant-platform-pricing-title">Varyant kanal fiyatları</h2><p>{row.optionSignature} · {row.barcode || row.sku}</p></div><button type="button" className="modal-close" onClick={onClose} disabled={saving} aria-label="Fiyat penceresini kapat"><UiIcon name="close" /></button></header><div className="variant-platform-pricing-body"><div className="variant-platform-pricing-list">{platforms.map(platform => { const active = platform.connectionId === selectedPlatform.connectionId; return <article className={`variant-platform-pricing-card${active ? ' is-selected' : ''}`} key={`${platform.platformCode}:${platform.connectionId ?? platform.platform}`}><div className="variant-platform-pricing-card-head"><span className={`publish-platform-mark ${platform.platformCode.toLocaleLowerCase('tr-TR')}`}><img className={`publish-platform-logo ${platformLogoClass(platform.platformCode)}`} src={platformLogoSource(platform.platformCode) ?? '/platforms/trendyol.png'} alt="" /></span><div><strong>{marketplacePlatformName(platform)}</strong><small>{platform.isLinked ? 'Bağlı' : 'Bağlı değil'}</small></div><span className={`variant-platform-pricing-state ${platform.isLinked ? 'is-linked' : 'is-unlinked'}`} /></div><dl><div><dt>Liste</dt><dd>{marketplacePriceLabel(platform.listPrice, platform.currency)}</dd></div><div><dt>Satış</dt><dd>{marketplacePriceLabel(platform.salePrice, platform.currency)}</dd></div></dl><button type="button" className="secondary" onClick={() => onSelectPlatform(platform)} disabled={saving}>{active ? 'Düzenleniyor' : 'Düzenle'}</button></article> })}</div><div className="variant-platform-pricing-editor"><div><strong>{marketplacePlatformName(selectedPlatform)} fiyatını düzenle</strong><small>Panel ana fiyatı değişmeden yalnızca bu kanal teklifini günceller.</small></div><div className="variant-platform-pricing-fields"><label>Liste fiyatı<input autoFocus type="number" min="0" step="0.01" value={draft.listPrice} onChange={event => onDraftChange('listPrice', event.target.value)} /></label><label>Satış fiyatı<input type="number" min="0" step="0.01" value={draft.salePrice} onChange={event => onDraftChange('salePrice', event.target.value)} /></label></div></div></div><footer><button type="button" className="secondary" onClick={onClose} disabled={saving}>Vazgeç</button><button type="button" onClick={onSave} disabled={saving || !selectedPlatform.connectionId}>{saving ? 'Kaydediliyor…' : 'Fiyatı kaydet'}</button></footer></section></div>
}
function LegacyVariantPlatformPricingModalWithVariantsFirst({ row, rows, platforms, selectedPlatform, draft, saving, onClose, onSelectRow, onSelectPlatform, onDraftChange, onSave }: { row: VariantDraft; rows: VariantDraft[]; platforms: VariantPlatformStatus[]; selectedPlatform: VariantPlatformStatus; draft: ChannelPricingDraft; saving: boolean; onClose: () => void; onSelectRow: (row: VariantDraft) => void; onSelectPlatform: (platform: VariantPlatformStatus) => void; onDraftChange: (field: keyof ChannelPricingDraft, value: string) => void; onSave: () => void }) {
  return <div className="workspace-modal-backdrop variant-platform-pricing-backdrop" role="presentation" onMouseDown={() => !saving && onClose()}><section className="workspace-modal variant-platform-pricing-modal" role="dialog" aria-modal="true" aria-labelledby="variant-platform-pricing-title" onMouseDown={event => event.stopPropagation()}><header><div><p className="eyebrow">PLATFORM FİYATLARI</p><h2 id="variant-platform-pricing-title">Varyant kanal fiyatları</h2><p>{row.optionSignature} · {row.barcode || row.sku}</p></div><button type="button" className="modal-close" onClick={onClose} disabled={saving} aria-label="Fiyat penceresini kapat"><UiIcon name="close" /></button></header><div className="variant-platform-pricing-body"><section className="variant-platform-pricing-variant-list" aria-label="Tüm varyantlar"><div className="variant-platform-pricing-variant-list-head"><strong>Tüm varyantlar</strong><span>{rows.length} varyant</span></div><div className="variant-platform-pricing-variant-items" role="listbox" aria-label="Ürün varyantları">{rows.map(item => <button type="button" role="option" aria-selected={item.key === row.key} className={`variant-platform-pricing-variant-item${item.key === row.key ? ' is-selected' : ''}`} key={item.key} onClick={() => onSelectRow(item)} disabled={saving}><span><strong>{item.optionSignature || item.sku}</strong><small>{item.barcode || item.sku}</small></span><i>{item.platformStatuses?.length ?? 0}</i></button>)}</div></section><div className="variant-platform-pricing-list">{platforms.map(platform => { const active = platform.connectionId === selectedPlatform.connectionId; return <article className={`variant-platform-pricing-card${active ? ' is-selected' : ''}`} key={`${platform.platformCode}:${platform.connectionId ?? platform.platform}`}><div className="variant-platform-pricing-card-head"><span className={`publish-platform-mark ${platform.platformCode.toLocaleLowerCase('tr-TR')}`}><img className={`publish-platform-logo ${platformLogoClass(platform.platformCode)}`} src={platformLogoSource(platform.platformCode) ?? '/platforms/trendyol.png'} alt="" /></span><div><strong>{marketplacePlatformName(platform)}</strong><small>{platform.isLinked ? 'Bağlı' : 'Bağlı değil'}</small></div><span className={`variant-platform-pricing-state ${platform.isLinked ? 'is-linked' : 'is-unlinked'}`} /></div><dl><div><dt>Liste</dt><dd>{marketplacePriceLabel(platform.listPrice, platform.currency)}</dd></div><div><dt>Satış</dt><dd>{marketplacePriceLabel(platform.salePrice, platform.currency)}</dd></div></dl><button type="button" className="secondary" onClick={() => onSelectPlatform(platform)} disabled={saving}>{active ? 'Düzenleniyor' : 'Düzenle'}</button></article> })}</div><div className="variant-platform-pricing-editor"><div><strong>{marketplacePlatformName(selectedPlatform)} fiyatını düzenle</strong><small>Panel ana fiyatı değişmeden yalnızca bu kanal teklifini günceller.</small></div><div className="variant-platform-pricing-fields"><label>Liste fiyatı<input autoFocus type="number" min="0" step="0.01" value={draft.listPrice} onChange={event => onDraftChange('listPrice', event.target.value)} /></label><label>Satış fiyatı<input type="number" min="0" step="0.01" value={draft.salePrice} onChange={event => onDraftChange('salePrice', event.target.value)} /></label></div></div></div><footer><button type="button" className="secondary" onClick={onClose} disabled={saving}>Vazgeç</button><button type="button" onClick={onSave} disabled={saving || !selectedPlatform.connectionId}>{saving ? 'Kaydediliyor…' : 'Fiyatı kaydet'}</button></footer></section></div>
}
function VariantPlatformPricingModal({ row, rows, platforms, selectedPlatform, draft, saving, onClose, onSelectRow, onSelectPlatform, onDraftChange, onSave }: { row: VariantDraft; rows: VariantDraft[]; platforms: VariantPlatformStatus[]; selectedPlatform: VariantPlatformStatus; draft: ChannelPricingDraft; saving: boolean; onClose: () => void; onSelectRow: (row: VariantDraft) => void; onSelectPlatform: (platform: VariantPlatformStatus) => void; onDraftChange: (field: keyof ChannelPricingDraft, value: string) => void; onSave: () => void }) {
  return <div className="workspace-modal-backdrop variant-platform-pricing-backdrop" role="presentation" onMouseDown={() => !saving && onClose()}><section className="workspace-modal variant-platform-pricing-modal" role="dialog" aria-modal="true" aria-labelledby="variant-platform-pricing-title" onMouseDown={event => event.stopPropagation()}><header><div><p className="eyebrow">PLATFORM FİYATLARI</p><h2 id="variant-platform-pricing-title">Varyant kanal fiyatları</h2><p>{row.optionSignature} · {row.barcode || row.sku}</p></div><button type="button" className="modal-close" onClick={onClose} disabled={saving} aria-label="Fiyat penceresini kapat"><UiIcon name="close" /></button></header><div className="variant-platform-pricing-body"><div className="variant-platform-pricing-list">{platforms.map(platform => { const active = platform.connectionId === selectedPlatform.connectionId; return <article className={`variant-platform-pricing-card${active ? ' is-selected' : ''}`} key={`${platform.platformCode}:${platform.connectionId ?? platform.platform}`}><div className="variant-platform-pricing-card-head"><span className={`publish-platform-mark ${platform.platformCode.toLocaleLowerCase('tr-TR')}`}><img className={`publish-platform-logo ${platformLogoClass(platform.platformCode)}`} src={platformLogoSource(platform.platformCode) ?? '/platforms/trendyol.png'} alt="" /></span><div><strong>{marketplacePlatformName(platform)}</strong><small>{platform.isLinked ? 'Bağlı' : 'Bağlı değil'}</small></div><span className={`variant-platform-pricing-state ${platform.isLinked ? 'is-linked' : 'is-unlinked'}`} /></div><dl><div><dt>Liste</dt><dd>{marketplacePriceLabel(platform.listPrice, platform.currency)}</dd></div><div><dt>Satış</dt><dd>{marketplacePriceLabel(platform.salePrice, platform.currency)}</dd></div></dl><button type="button" className="secondary" onClick={() => onSelectPlatform(platform)} disabled={saving}>{active ? 'Düzenleniyor' : 'Düzenle'}</button></article> })}</div><section className="variant-platform-pricing-variant-list" aria-label="Tüm varyantlar"><div className="variant-platform-pricing-variant-list-head"><strong>Tüm varyantlar</strong><span>{rows.length} varyant · {marketplacePlatformName(selectedPlatform)} fiyatları</span></div><div className="variant-platform-pricing-variant-items" role="listbox" aria-label="Ürün varyantları">{rows.map(item => { const variantPlatform = item.platformStatuses?.find(platform => platform.platformCode === selectedPlatform.platformCode) ?? item.platformStatuses?.[0]; const priceSummary = variantPlatform ? `Liste ${marketplacePriceLabel(variantPlatform.listPrice, variantPlatform.currency)} · Satış ${marketplacePriceLabel(variantPlatform.salePrice, variantPlatform.currency)}` : 'Fiyat tanımlı değil'; return <button type="button" role="option" aria-selected={item.key === row.key} className={`variant-platform-pricing-variant-item${item.key === row.key ? ' is-selected' : ''}`} key={item.key} onClick={() => onSelectRow(item)} disabled={saving} aria-label={`${item.optionSignature || item.sku} · ${priceSummary}`}><span><strong>{item.optionSignature || item.sku}</strong><small>{item.barcode || item.sku}</small><em>{priceSummary}</em></span><i>{item.platformStatuses?.length ?? 0}</i></button> })}</div></section><div className="variant-platform-pricing-editor"><div><strong>{marketplacePlatformName(selectedPlatform)} fiyatını düzenle</strong><small>Panel ana fiyatı değişmeden yalnızca bu kanal teklifini günceller.</small></div><div className="variant-platform-pricing-fields"><label>Liste fiyatı<input autoFocus type="number" min="0" step="0.01" value={draft.listPrice} onChange={event => onDraftChange('listPrice', event.target.value)} /></label><label>Satış fiyatı<input type="number" min="0" step="0.01" value={draft.salePrice} onChange={event => onDraftChange('salePrice', event.target.value)} /></label></div></div></div><footer><button type="button" className="secondary" onClick={onClose} disabled={saving}>Vazgeç</button><button type="button" onClick={onSave} disabled={saving || !selectedPlatform.connectionId}>{saving ? 'Kaydediliyor…' : 'Fiyatı kaydet'}</button></footer></section></div>
}
function BulkVariantPlatformPricingModal({ row, rows, platforms, productName, modelCode, saving, onClose, onSave }: { row: VariantDraft; rows: VariantDraft[]; platforms: VariantPlatformStatus[]; productName: string; modelCode: string; saving: boolean; onClose: () => void; onSave: (drafts: Record<string, ChannelPricingDraft>) => void }) {
  const sortedRows = sortVariantsAlphabetically(rows)
  const platformKey = (platform: VariantPlatformStatus) => `${platform.platformCode}:${platform.connectionId ?? platform.platform}`
  const initialMatrixDrafts = () => Object.fromEntries(sortedRows.flatMap(item => platforms.map(platform => {
    const status = item.platformStatuses?.find(value => value.connectionId === platform.connectionId)
    return [`${item.key}:${platformKey(platform)}`, { listPrice: String(status?.listPrice ?? item.listPrice ?? ''), salePrice: String(status?.salePrice ?? item.salePrice ?? '') }]
  }))) as Record<string, ChannelPricingDraft>
  const [matrixDrafts, setMatrixDrafts] = useState<Record<string, ChannelPricingDraft>>(initialMatrixDrafts)
  useEffect(() => { setMatrixDrafts(initialMatrixDrafts()) }, [row.key, rows, platforms])
  const matrixDraft = (item: VariantDraft, platform: VariantPlatformStatus) => matrixDrafts[`${item.key}:${platformKey(platform)}`] ?? { listPrice: '', salePrice: '' }
  const updateMatrixDraft = (item: VariantDraft, platform: VariantPlatformStatus, field: keyof ChannelPricingDraft, value: string) => setMatrixDrafts(current => ({ ...current, [`${item.key}:${platformKey(platform)}`]: { ...matrixDraft(item, platform), [field]: value } }))
  const renderMatrixHeader = (platform: VariantPlatformStatus, label: string) => {
    const key = platformKey(platform)
    return <div className="variant-platform-pricing-matrix-cell variant-platform-pricing-matrix-head" key={`${key}:${label}`}><strong>{`${marketplacePlatformName(platform)} ${label}`.toLocaleUpperCase('tr-TR')}</strong></div>
  }
  return <div className="workspace-modal-backdrop variant-platform-pricing-backdrop" role="presentation" onMouseDown={() => !saving && onClose()}><section className="workspace-modal variant-platform-pricing-modal" role="dialog" aria-modal="true" aria-labelledby="variant-platform-pricing-title" onMouseDown={event => event.stopPropagation()}><header><div><p className="eyebrow">PLATFORM FİYATLARI</p><h2 id="variant-platform-pricing-title">Varyant kanal fiyatları</h2><p className="variant-platform-pricing-product-context"><strong>{productName || 'Ürün adı belirtilmemiş'}</strong><span>Model kodu: {modelCode || '—'}</span></p></div><button type="button" className="modal-close" onClick={onClose} disabled={saving} aria-label="Fiyat penceresini kapat"><UiIcon name="close" /></button></header><div className="variant-platform-pricing-body"><section className="variant-platform-pricing-matrix" aria-label="Platformlara göre varyant fiyat matrisi"><div className="variant-platform-pricing-bulk-heading"><strong>Platform fiyatlarını toplu düzenle</strong><span>{rows.length} varyanta uygulanır · Panel ana fiyatı değişmez</span></div><div className="variant-platform-pricing-matrix-scroll"><div className="variant-platform-pricing-matrix-grid" style={{ gridTemplateColumns: `150px repeat(${platforms.length * 2}, 128px)` }}><div className="variant-platform-pricing-matrix-cell variant-platform-pricing-matrix-corner"><strong>Varyant</strong><small>Model / barkod</small></div>{platforms.flatMap(platform => [renderMatrixHeader(platform, 'Liste fiyatı'), renderMatrixHeader(platform, 'Satış fiyatı')])}{sortedRows.flatMap(item => { return [<div className="variant-platform-pricing-matrix-cell variant-platform-pricing-matrix-row-label" key={`${item.key}:label`}><strong>{item.optionSignature || item.sku}</strong><small>{item.barcode || item.sku}</small></div>, ...platforms.flatMap(platform => { const draft = matrixDraft(item, platform); return [<div className="variant-platform-pricing-matrix-cell" key={`${item.key}:${platformKey(platform)}:list`}><input aria-label={`${item.optionSignature || item.sku} ${marketplacePlatformName(platform)} liste fiyatı`} type="number" min="0" step="0.01" value={draft.listPrice} disabled={saving || !platform.connectionId} onChange={event => updateMatrixDraft(item, platform, 'listPrice', event.target.value)} /></div>, <div className="variant-platform-pricing-matrix-cell" key={`${item.key}:${platformKey(platform)}:sale`}><input aria-label={`${item.optionSignature || item.sku} ${marketplacePlatformName(platform)} satış fiyatı`} type="number" min="0" step="0.01" value={draft.salePrice} disabled={saving || !platform.connectionId} onChange={event => updateMatrixDraft(item, platform, 'salePrice', event.target.value)} /></div>] })] })}</div></div></section></div><footer><button type="button" onClick={() => onSave(matrixDrafts)} disabled={saving || !platforms.some(platform => platform.connectionId)}>{saving ? 'Kaydediliyor…' : 'Kaydet'}</button><button type="button" className="secondary" onClick={onClose} disabled={saving}>Vazgeç</button></footer></section></div>
}
void LegacyVariantPlatformPricingModal
void LegacyVariantPlatformPricingModalWithVariantsFirst
void VariantPlatformPricingModal
// API, inventory and publication safeguards retain the actual 1000-line limit.
// The product workspace intentionally does not display an arbitrary UI quota.
const MAX_VARIANTS = 1000
const MAX_PRODUCT_ATTRIBUTES = 3
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

function buildVariantMatrix(requirements: CategoryRequirement[], variantAttributeIds: string[], selectedValueIds: Record<string, string[]>, baseSku: string, fallbackListPrice: number, fallbackSalePrice: number, fallbackCostPrice: number, initialStock: number) {
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
  const prefix = (baseSku || 'URUN').trim().replace(/\s+/g, '-').toLocaleUpperCase('tr-TR')
  return combinations.map((entry, index) => ({
    key: crypto.randomUUID(),
    optionSignature: Object.entries(entry.options).map(([name, value]) => `${name}:${cleanOptionValue(value)}`).join('_'),
    options: entry.options,
    attributeValueIds: entry.attributeValueIds,
    sku: `${prefix}-${index + 1}`,
    barcode: '',
    stock: initialStock,
    salePrice: fallbackSalePrice,
    listPrice: fallbackListPrice || fallbackSalePrice,
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
  dataType,
  values,
  selectedValues,
  onToggleValue
}: {
  attributeName: string
  dataType: string
  values: Array<{ id: string; value: string }>
  selectedValues: string[]
  onToggleValue: (valueId: string) => void
}) {
  const [open, setOpen] = useState(false)
  const [search, setSearch] = useState('')
  const rootRef = useRef<HTMLDivElement>(null)
  const isSingle = dataType === 'SINGLE_SELECT'
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

  return <div className="category-attribute-dropdown" ref={rootRef}>
    <button type="button" className={`category-attribute-select-trigger ${selected.length ? 'active' : 'is-empty'}`} aria-haspopup="listbox" aria-expanded={open} onClick={() => setOpen(current => !current)}>
      <span><small>{selected.length ? (isSingle ? 'Seçili değer' : `${selected.length} değer seçildi`) : 'Seçim yapın'}</small><strong>{summary}</strong></span>
      <UiIcon name="chevronDown" />
    </button>
    {open && <div className="category-attribute-dropdown-menu" role="listbox" aria-label={`${attributeName} değerleri`}>
      <div className="category-attribute-dropdown-tools">
        <input autoFocus value={search} onChange={event => setSearch(event.target.value)} placeholder="Değer ara..." aria-label={`${attributeName} değerlerinde ara`} />
        <span>{filteredValues.length}/{values.length}</span>
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
        <input autoFocus value={search} onChange={event => setSearch(event.target.value)} placeholder="Değer ara..." aria-label={`${group.name} değerlerinde ara`} />
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
  const dataTypeLabels: Record<string, string> = {
    SINGLE_SELECT: 'Tek seçim',
    MULTI_SELECT: 'Çoklu seçim',
    BOOLEAN: 'Evet / hayır',
    NUMBER: 'Sayısal değer',
    TEXT: 'Metin'
  }

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
                <small>{dataTypeLabels[item.attribute.dataType] ?? item.attribute.dataType}{item.isRequired ? ' · Zorunlu' : ' · İsteğe bağlı'}</small>
              </div>
            </div>
            {item.attribute.values.length ? (
              <CategoryAttributeValueDropdown attributeName={displayName} dataType={item.attribute.dataType} values={item.attribute.values} selectedValues={selectedValues} onToggleValue={valueId => onToggleValue(item.attributeId, valueId)} />
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

export function NewProductPage({ editProductId }: { editProductId?: string } = {}) {
  const client = useQueryClient();
  const [error, setError] = useState<unknown>(); const [created, setCreated] = useState<Product>(); const [, setNotice] = useState(''); const [feedback, setFeedback] = useState<OperationFeedback | null>(null); const [submitting, setSubmitting] = useState(false); const [calculateDesi, setCalculateDesi] = useState(false); const [desiCalculatorOpen, setDesiCalculatorOpen] = useState(false)
  const [form, setForm] = useState({ title: '', description: '', brandId: '', categoryId: '', baseSku: '', barcode: '', modelCode: '', weight: '', width: '', length: '', height: '', desi: '1', listPrice: '699.90', salePrice: '549.90', costPrice: '0', currency: 'TRY', vatRate: '10', vatIncluded: 'INCLUDED', initialStock: '0', safetyStock: '0', mediaUrls: '', status: 'ACTIVE' })
  const [attributeSelections, setAttributeSelections] = useState<Record<string, string[]>>({}); const [attributeTextValues, setAttributeTextValues] = useState<Record<string, string>>({}); const [variantAttributeIds, setVariantAttributeIds] = useState<string[]>([]); const [variantRows, setVariantRows] = useState<VariantDraft[]>([]); const [variantFilterSelections, setVariantFilterSelections] = useState<VariantFilterSelections>({}); const [variantFilterOpen, setVariantFilterOpen] = useState(false); const [draggedVariantKey, setDraggedVariantKey] = useState<string | null>(null); const [dragOverVariantKey, setDragOverVariantKey] = useState<string | null>(null); const [selectedChannelIds, setSelectedChannelIds] = useState<string[]>([]); const [channelPricing, setChannelPricing] = useState<Record<string, ChannelPricingDraft>>({})
  const initializedEditProductKey = useRef<string | null>(null)
  const initializedEditOptionsKey = useRef<string | null>(null)
  const initializedEditWebColorKey = useRef<string | null>(null)
  const [wizardStep, setWizardStep] = useState<1 | 2>(1)
  const [scheduledPublishOpen, setScheduledPublishOpen] = useState(false)
  const [lightboxImage, setLightboxImage] = useState<{ url: string; title: string } | null>(null)
  const [variantMediaModal, setVariantMediaModal] = useState<{ mode: 'variant' | 'bulk'; rowKey?: string; draftRefs: string[]; groupId: string; valueId: string } | null>(null)
  const [barcodeSkuMenuOpen, setBarcodeSkuMenuOpen] = useState(false)
  const barcodeSkuActionRef = useRef<HTMLDivElement>(null)
  const [expandedOptionGroupIds, setExpandedOptionGroupIds] = useState<Record<string, boolean>>({})
  const [bulkStock, setBulkStock] = useState(''); const [bulkSalePrice, setBulkSalePrice] = useState(''); const [bulkCostPrice, setBulkCostPrice] = useState(''); const [bulkListPrice, setBulkListPrice] = useState('')
  const [mediaFiles, setMediaFiles] = useState<File[]>([])
  const [draggedMediaUrl, setDraggedMediaUrl] = useState<string | null>(null); const [dragOverMediaUrl, setDragOverMediaUrl] = useState<string | null>(null)
  const [pointerDraggedVariantKey, setPointerDraggedVariantKey] = useState<string | null>(null)
  const pointerDraggedVariantRef = useRef<string | null>(null)
  const pointerDragSourceRef = useRef<HTMLDivElement | null>(null)
  const pointerDragIdRef = useRef<number | null>(null)
  const [mediaUrlSettingsOpen, setMediaUrlSettingsOpen] = useState(false)
  const [variantPlatformPricing, setVariantPlatformPricing] = useState<{ rowKey: string; platform: VariantPlatformStatus } | null>(null)
  const [variantPlatformPricingDraft, setVariantPlatformPricingDraft] = useState<ChannelPricingDraft>({ listPrice: '', salePrice: '' })
  const [variantPlatformPricingSaving, setVariantPlatformPricingSaving] = useState(false)
  const feedbackTimer = useRef<number | null>(null)
  const initialEditMediaUrl = useRef('')
  useEffect(() => {
    if (!barcodeSkuMenuOpen) return
    function closeBarcodeSkuMenu(event: PointerEvent) {
      if (!barcodeSkuActionRef.current?.contains(event.target as Node)) setBarcodeSkuMenuOpen(false)
    }
    function closeOnEscape(event: KeyboardEvent) {
      if (event.key === 'Escape') setBarcodeSkuMenuOpen(false)
    }
    document.addEventListener('pointerdown', closeBarcodeSkuMenu)
    document.addEventListener('keydown', closeOnEscape)
    return () => { document.removeEventListener('pointerdown', closeBarcodeSkuMenu); document.removeEventListener('keydown', closeOnEscape) }
  }, [barcodeSkuMenuOpen])
  function showFeedback(message: string, kind: OperationFeedback['kind']) {
    if (feedbackTimer.current !== null) window.clearTimeout(feedbackTimer.current)
    setFeedback({ message, kind })
    feedbackTimer.current = window.setTimeout(() => setFeedback(null), kind === 'info' ? 7000 : 5500)
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
    if (accepted.length) setMediaFiles(current => [...current, ...accepted])
    if (rejected.length) {
      const message = rejected.length === 1 ? rejected[0] : `${rejected.length} görsel eklenemedi. Dosya türü ve 6 MB sınırını kontrol edin.`
      setNotice(message); showFeedback(message, 'error')
    } else if (accepted.length) {
      const message = `${accepted.length} görsel seçildi.`; setNotice(message); showFeedback(message, 'info')
    }
  }
  useEffect(() => () => { if (feedbackTimer.current !== null) window.clearTimeout(feedbackTimer.current) }, [])
  const productToEdit = useQuery({ queryKey: ['product', editProductId], queryFn: () => hubApi<Product>(`/products/${editProductId}`), enabled: !!editProductId })
  const categories = useQuery({ queryKey: ['categories', 'new-product'], queryFn: () => loadAllPages<Category>('/catalog/categories') })
  const brands = useQuery({ queryKey: ['brands', 'new-product'], queryFn: () => loadAllPages<Brand>('/catalog/brands') })
  const connections = useQuery({ queryKey: ['connections', 'new-product'], queryFn: () => loadAllPages<MarketplaceConnection>('/connections') })
  const requirements = useQuery({ queryKey: ['category-requirements', form.categoryId], queryFn: () => hubApi<CategoryRequirement[]>(`/catalog/categories/${form.categoryId}/attribute-requirements`), enabled: !!form.categoryId, retry: false })
  const leafCategories = (categories.data?.items ?? []).filter(item => item.isLeaf && item.isActive); const activeBrands = (brands.data?.items ?? []).filter(item => item.isActive)
  const fallbackListPrice = Number(form.listPrice || 0); const fallbackSalePrice = Number(form.salePrice || 0); const fallbackCostPrice = Number(form.costPrice || 0); const initialStock = Number(form.initialStock || 0)
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
  const [webColorAutoEnabled, setWebColorAutoEnabled] = useState(true)
  const [manualWebColorValueId, setManualWebColorValueId] = useState('')
  useEffect(() => {
    const optionIds = optionRequirements.map(item => item.attributeId)
    setVariantAttributeIds(current => current.filter(id => optionIds.includes(id)))
  }, [optionRequirements])
  const visibleOptionRequirements = optionRequirements

  useEffect(() => {
    const product = productToEdit.data
    if (!product) return
    const productKey = `${product.id}:${product.version}`
    if (initializedEditProductKey.current === productKey) return
    initializedEditProductKey.current = productKey
    const primary = product.variants[0]
    const sortedVariants = sortVariantsAlphabetically(product.variants)
    const savedMediaUrls = orderMediaUrlsByVariants(product.variants, product.mediaUrls ?? [], product.primaryImageUrl)
    setForm({ title: product.title, description: product.description ?? '', brandId: product.brandId ?? '', categoryId: product.categoryId ?? '', baseSku: primary?.sku ?? '', barcode: primary?.barcode ?? '', modelCode: primary?.modelCode ?? product.modelCode ?? '', weight: String(primary?.weight ?? ''), width: String(primary?.width ?? ''), length: String(primary?.length ?? ''), height: String(primary?.height ?? ''), desi: String(primary?.desi ?? 1), listPrice: String(primary?.listPrice ?? primary?.salePrice ?? 0), salePrice: String(primary?.salePrice ?? 0), costPrice: String(primary?.costPrice ?? 0), currency: primary?.currency ?? 'TRY', vatRate: String(primary?.vatRate ?? 10), vatIncluded: primary?.vatInclusion ?? 'INCLUDED', initialStock: String(primary?.onHand ?? 0), safetyStock: String(primary?.safetyStock ?? 0), mediaUrls: savedMediaUrls.join('\n'), status: product.status || 'ACTIVE' })
    initialEditMediaUrl.current = savedMediaUrls.join('\n')
    setMediaFiles([])
    const seededMediaRefs = seedVariantMediaRefs(product.variants)
    const mediaRefsByVariantId = new Map(product.variants.map((variant, index) => [variant.id, seededMediaRefs[index] ?? []]))
    setVariantRows(sortedVariants.map(variant => {
      const options = Object.fromEntries(variantOptionEntries(variant).map(option => [option.name, option.value]))
      return { key: variant.id, optionSignature: variant.optionSignature && variant.optionSignature !== '-' ? variant.optionSignature : optionSignatureFromOptions(options) || 'Tek Ürün', options, attributeValueIds: {}, sku: variant.sku, barcode: variant.barcode ?? '', stock: variant.onHand, salePrice: variant.salePrice ?? 0, listPrice: variant.listPrice ?? variant.salePrice ?? 0, costPrice: variant.costPrice ?? 0, mediaRefs: mediaRefsByVariantId.get(variant.id) ?? [], platformStatuses: variant.platformStatuses ?? [] }
    }))
    const selected: Record<string, string[]> = {}; const typed: Record<string, string> = {}
    for (const attribute of product.attributes ?? []) { if (attribute.valueId) selected[attribute.attributeId] = [...(selected[attribute.attributeId] ?? []), attribute.valueId]; else if (attribute.textValue != null) typed[attribute.attributeId] = attribute.textValue; else if (attribute.numberValue != null) typed[attribute.attributeId] = String(attribute.numberValue); else if (attribute.booleanValue != null) typed[attribute.attributeId] = attribute.booleanValue ? 'evet' : 'hayır' }
    setAttributeSelections(selected); setAttributeTextValues(typed); setVariantAttributeIds([])
  }, [productToEdit.data?.id, productToEdit.data?.version])

  useEffect(() => {
    if (!editProductId || !productToEdit.data || !webColorRequirement || requirements.isLoading) return
    const productKey = `${productToEdit.data.id}:${productToEdit.data.version}`
    if (initializedEditWebColorKey.current === productKey) return
    initializedEditWebColorKey.current = productKey
    const savedManualColor = productToEdit.data.attributes?.find(item => item.attributeId === webColorRequirement.attributeId && item.valueId)
    setWebColorAutoEnabled(!savedManualColor)
    setManualWebColorValueId(savedManualColor?.valueId ?? '')
  }, [editProductId, productToEdit.data, requirements.isLoading, webColorRequirement])

  useEffect(() => {
    if (!editProductId || !productToEdit.data || !allRequirements.length || requirements.isLoading) return
    const productKey = `${productToEdit.data.id}:${productToEdit.data.version}`
    if (initializedEditOptionsKey.current === productKey) return
    initializedEditOptionsKey.current = productKey
    const inferred: Record<string, string[]> = {}
    for (const requirement of optionRequirements) {
      const valuesInVariants = new Set(productToEdit.data.variants.flatMap(variant => variantOptionEntries(variant)
        .filter(option => option.name.trim().toLocaleLowerCase('tr-TR') === requirement.attribute.name.trim().toLocaleLowerCase('tr-TR'))
        .map(option => option.value.trim().toLocaleLowerCase('tr-TR'))))
      const ids = requirement.attribute.values.filter(value => valuesInVariants.has(value.value.trim().toLocaleLowerCase('tr-TR'))).map(value => value.id)
      if (ids.length) inferred[requirement.attributeId] = ids
    }
    setAttributeSelections(current => ({ ...current, ...inferred }))
    setVariantAttributeIds(Object.keys(inferred))
    setVariantRows(rows => rows.map(row => {
      const source = productToEdit.data!.variants.find(variant => variant.id === row.key)
      if (!source) return row
      const options = Object.fromEntries(variantOptionEntries(source).map(option => [option.name, option.value]))
      const attributeValueIds = Object.fromEntries(optionRequirements.flatMap(requirement => {
        const option = variantOptionEntries(source).find(item => item.name.trim().toLocaleLowerCase('tr-TR') === requirement.attribute.name.trim().toLocaleLowerCase('tr-TR'))
        const value = requirement.attribute.values.find(item => item.value.trim().toLocaleLowerCase('tr-TR') === option?.value.trim().toLocaleLowerCase('tr-TR'))
        return value ? [[requirement.attributeId, value.id]] : []
      }))
      return { ...row, optionSignature: source.optionSignature && source.optionSignature !== '-' ? source.optionSignature : optionSignatureFromOptions(options) || row.optionSignature, options, attributeValueIds }
    }))
  }, [allRequirements, editProductId, optionRequirements, productToEdit.data, requirements.isLoading])

  function updateField(name: keyof typeof form, value: string) { setForm(current => ({ ...current, [name]: value })) }
  function toggleAttributeValue(attributeId: string, valueId: string) {
    const requirement = mappedRequirements.find(item => item.attributeId === attributeId)
    const alreadySelected = (attributeSelections[attributeId] ?? []).includes(valueId)
    if (!alreadySelected && requirement && isOptionRequirement(requirement) && !variantAttributeIds.includes(attributeId) && variantAttributeIds.length >= 2) {
      const message = 'Bir ürün en fazla 2 seçenek grubuyla varyantlanabilir.'; setNotice(message); showFeedback(message, 'error'); return
    }
    setAttributeSelections(current => {
      const values = current[attributeId] ?? []
      const nextValues = values.includes(valueId) ? values.filter(item => item !== valueId) : requirement && !isOptionRequirement(requirement) && requirement.attribute.dataType === 'SINGLE_SELECT' ? [valueId] : [...values, valueId]
      if (requirement && isOptionRequirement(requirement)) {
        setVariantAttributeIds(currentAxes => nextValues.length ? currentAxes.includes(attributeId) ? currentAxes : [...currentAxes, attributeId] : currentAxes.filter(id => id !== attributeId))
      }
      if (values.includes(valueId)) return { ...current, [attributeId]: nextValues }
      const selectedOptionalAttributeCount = mappedRequirements.filter(item => !isOptionRequirement(item) && !item.isRequired && (current[item.attributeId]?.length ?? 0) > 0).length
      if (requirement && !isOptionRequirement(requirement) && !requirement.isRequired && values.length === 0 && selectedOptionalAttributeCount >= MAX_PRODUCT_ATTRIBUTES) { const message = `Bir üründe en fazla ${MAX_PRODUCT_ATTRIBUTES} isteğe bağlı ürün özelliği kullanılabilir.`; setNotice(message); showFeedback(message, 'error'); return current }
      return { ...current, [attributeId]: nextValues }
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
  function generateVariants() {
    try {
      const generated = buildVariantMatrix(mappedRequirements, variantAttributeIds, attributeSelections, form.baseSku || form.modelCode || form.title, fallbackListPrice, fallbackSalePrice, fallbackCostPrice, initialStock)
      if (!generated.length) {
        const message = 'Önce varyant olacak özellikleri ve bu özelliklerin değerlerini seçin.'; setNotice(message); showFeedback(message, 'error')
        return
      }
      setVariantRows(current => {
        if (current.length === 1 && current[0].optionSignature.trim().toLocaleLowerCase('tr-TR') === 'tek ürün' && generated.length === 1) {
          const existing = current[0]
          const [next] = generated
          return [{ ...next, key: existing.key, barcode: existing.barcode || next.barcode, sku: existing.sku || next.sku, stock: existing.stock, salePrice: existing.salePrice, listPrice: existing.listPrice, costPrice: existing.costPrice, mediaRefs: existing.mediaRefs }]
        }
        const existingMap = new Map(current.map(row => [variantSignatureKey(row.optionSignature), row]))
        const merged = generated.map(gen => {
          const match = existingMap.get(variantSignatureKey(gen.optionSignature))
          if (match) {
            existingMap.delete(gen.optionSignature)
            return match
          }
          return gen
        })
        return sortVariantsAlphabetically([...merged, ...Array.from(existingMap.values())])
      })
      const message = `${generated.length} varyant satırı hazırlandı.`; setNotice(message); showFeedback(message, 'success')
    } catch (reason) { const message = reason instanceof Error ? reason.message : 'Varyantlar oluşturulamadı.'; setNotice(message); showFeedback(message, 'error') }
  }
  function clearVariants() { setVariantRows([]); const message = 'Oluşan varyant satırları temizlendi.'; setNotice(message); showFeedback(message, 'success') }
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
  function reorderMedia(sourceUrl: string, targetUrl: string) {
    if (!sourceUrl || !targetUrl || sourceUrl === targetUrl) return
    const current = [...mediaUrls, ...familyOnlyMediaUrls]
    const sourceIndex = current.indexOf(sourceUrl); const targetIndex = current.indexOf(targetUrl)
    if (sourceIndex < 0 || targetIndex < 0) return
    const next = [...current]; const [moved] = next.splice(sourceIndex, 1); next.splice(targetIndex, 0, moved)
    updateField('mediaUrls', next.join('\n'))
    setDraggedMediaUrl(null); setDragOverMediaUrl(null)
    showFeedback('Görsel sırası güncellendi. Kalıcı olması için kaydedin.', 'info')
  }
  function clearAllMedia() {
    setMediaFiles([])
    updateField('mediaUrls', '')
    setVariantRows(rows => rows.map(row => ({ ...row, mediaRefs: [] })))
    setDraggedMediaUrl(null)
    setDragOverMediaUrl(null)
    showFeedback('Ürün ve varyant görselleri temizlendi. Kalıcı olması için kaydedin.', 'info')
  }
  function updateChannel(id: string) {
    const selected = selectedChannelIds.includes(id)
    setSelectedChannelIds(current => selected ? current.filter(item => item !== id) : [...current, id])
    showFeedback(selected ? 'Yayın kanalı seçimden çıkarıldı.' : 'Yayın kanalı seçildi.', 'info')
  }
  function channelPriceDraft(connectionId: string): ChannelPricingDraft {
    return channelPricing[connectionId] ?? { listPrice: form.listPrice, salePrice: form.salePrice }
  }
  function updateChannelPrice(connectionId: string, field: keyof ChannelPricingDraft, value: string) {
    setChannelPricing(current => ({ ...current, [connectionId]: { ...(current[connectionId] ?? { listPrice: form.listPrice, salePrice: form.salePrice }), [field]: value } }))
  }
  function platformDisplayName(platform: VariantPlatformStatus) {
    const code = platform.platformCode.trim().toLocaleUpperCase('tr-TR')
    return code === 'SHOPIFY' ? 'Shopify' : code === 'TRENDYOL' ? 'Trendyol' : platform.platform
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
    const sourcePlatforms = sourceRow?.platformStatuses ?? []
    if (!sourceRow || !sourcePlatforms.length) {
      showFeedback('Bu ürün için güncellenebilir platform fiyatı bulunamadı.', 'error')
      return
    }
    const platformKey = (platform: VariantPlatformStatus) => `${platform.platformCode}:${platform.connectionId ?? platform.platform}`
    const targets = variantRows.flatMap(targetRow => sourcePlatforms.flatMap(sourcePlatform => {
      const platform = targetRow.platformStatuses?.find(item => item.connectionId === sourcePlatform.connectionId)
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
    setVariantMediaModal({ mode: 'variant', rowKey, draftRefs: row?.mediaRefs ?? [], groupId: '', valueId: '' })
  }
  function openBulkVariantMediaPicker() {
    const groups = bulkMediaGroups
    if (!groups.length) {
      const message = 'Önce seçenek grubu ve en az bir seçenek değeri seçin.'
      setNotice(message); showFeedback(message, 'error')
      return
    }
    const group = groups.find(item => normalizeVariantOptionName(item.name) === 'RENK') ?? groups[0]
    setVariantMediaModal({ mode: 'bulk', draftRefs: [], groupId: group.id, valueId: group.values[0]?.id ?? '' })
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
    return Boolean((group.attributeId && row.attributeValueIds[group.attributeId] === value.id) || rowOptionValue(row, group).trim().toLocaleLowerCase('tr-TR') === value.value.trim().toLocaleLowerCase('tr-TR'))
  }
  function rowMatchesVariantFilters(row: VariantDraft) {
    return variantFilterGroups.every(group => {
      const selectedValueIds = variantFilterSelections[group.id] ?? []
      if (!selectedValueIds.length) return true
      return group.values.filter(value => selectedValueIds.includes(value.id)).some(value => rowMatchesVariantMediaValue(row, group, value))
    })
  }
  function applyVariantMediaSelection() {
    if (!variantMediaModal) return
    if (variantMediaModal.mode === 'variant' && variantMediaModal.rowKey) {
      updateVariantMedia(variantMediaModal.rowKey, variantMediaModal.draftRefs)
      setVariantMediaModal(null)
      showFeedback(variantMediaModal.draftRefs.length ? `${variantMediaModal.draftRefs.length} varyant görseli seçildi.` : 'Varyant görselleri kaldırıldı.', 'success')
      return
    }
    const group = bulkMediaGroups.find(item => item.id === variantMediaModal.groupId)
    const value = group?.values.find(item => item.id === variantMediaModal.valueId)
    if (!group || !value) return
    const matchingRows = variantRows.filter(row => rowMatchesVariantMediaValue(row, group, value))
    if (!matchingRows.length) {
      const message = `${group.name}: ${value.value} seçeneğine bağlı varyant satırı bulunamadı.`
      setNotice(message); showFeedback(message, 'error')
      return
    }
    setVariantRows(rows => rows.map(row => rowMatchesVariantMediaValue(row, group, value) ? { ...row, mediaRefs: variantMediaModal.draftRefs } : row))
    setVariantMediaModal(null)
    const action = variantMediaModal.draftRefs.length ? `${variantMediaModal.draftRefs.length} görsel uygulandı` : 'görseller kaldırıldı'
    const message = `${group.name}: ${value.value} seçeneğindeki ${matchingRows.length} varyant satırında ${action}.`
    setNotice(message); showFeedback(message, 'success')
  }

  function rowsForSubmit(requireCompleteCatalog = true) {
    if (requireCompleteCatalog && variantAttributeIds.length && !variantRows.length) throw new Error('Varyant özellikleri seçili. Önce “Ürünleri ekle” ile varyantları oluşturun.')
    if (editProductId && !variantRows.length) return []
    return variantRows.length ? variantRows : [{ key: crypto.randomUUID(), optionSignature: 'Tek Ürün', options: {}, attributeValueIds: {}, sku: (form.baseSku || form.modelCode || form.title || 'URUN').trim().replace(/\s+/g, '-').toLocaleUpperCase('tr-TR'), barcode: form.barcode, stock: initialStock, salePrice: fallbackSalePrice, listPrice: fallbackListPrice, costPrice: fallbackCostPrice, mediaRefs: [] }]
  }
  function validate(rows: VariantDraft[], requireCompleteCatalog = true) {
    const issues: string[] = []; const requirementList = mappedRequirements
    if (requireCompleteCatalog && (variantAttributeIds.length > 2 || variantAttributeIds.some(id => { const requirement = requirementList.find(item => item.attributeId === id); return !requirement || !isOptionRequirement(requirement) }))) issues.push('Varyant için en fazla 2 Seçenek Eşitleme başlığı kullanılabilir.')
    const selectedOptionalProductAttributes = requirementList.filter(item => !isOptionRequirement(item) && !item.isRequired && ((attributeSelections[item.attributeId]?.length ?? 0) > 0 || Boolean((attributeTextValues[item.attributeId] ?? '').trim()))).length
    if (requireCompleteCatalog && selectedOptionalProductAttributes > MAX_PRODUCT_ATTRIBUTES) issues.push(`Bir üründe en fazla ${MAX_PRODUCT_ATTRIBUTES} isteğe bağlı ürün özelliği kullanılabilir.`)
    if (requireCompleteCatalog && !webColorAutoEnabled && (!webColorRequirement || !manualWebColorValueId)) issues.push('Manuel Web Color aktarımı için gönderilecek panel renk değerini seçin.')
    if (!webColorAutoEnabled && webColorRequirement && manualWebColorValueId && !webColorRequirement.attribute.values.some(value => value.id === manualWebColorValueId)) issues.push('Manuel Web Color için seçilen değer geçerli değil.')
    if (requireCompleteCatalog && webColorAutoEnabled && webColorRequirement && (!colorOptionRequirement || !variantAttributeIds.includes(colorOptionRequirement.attributeId)) && !(attributeSelections[colorOptionRequirement?.attributeId ?? '']?.length)) issues.push('Web Color otomatik aktarımı için Renk seçeneğini seçin veya otomatik aktarımı kapatıp bir değer seçin.')
    if (!form.title.trim()) issues.push('Ürün adı zorunludur.')
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
    const skus = rows.map(row => row.sku.trim().toLocaleUpperCase('tr-TR')); if (skus.some(value => !value)) issues.push('Tüm varyantlarda stok kodu zorunludur.'); if (new Set(skus).size !== skus.length) issues.push('Stok kodları benzersiz olmalıdır.')
    const signatures = rows.map(row => row.optionSignature); if (new Set(signatures).size !== signatures.length) issues.push('Aynı varyant kombinasyonu iki kez oluşturulamaz.')
    const barcodes = rows.map(row => row.barcode.trim()).filter(Boolean); if (new Set(barcodes.map(value => value.toLocaleUpperCase('tr-TR'))).size !== barcodes.length) issues.push('Barkodlar benzersiz olmalıdır.')
     if (rows.some(row => row.salePrice < 0 || row.listPrice < row.salePrice)) issues.push('Her varyantta liste fiyatı satış fiyatından küçük olamaz.')
     if (!form.desi.trim() || !Number.isFinite(Number(form.desi)) || Number(form.desi) <= 0) issues.push('Desi sıfırdan büyük olmalıdır.')
     if (requireCompleteCatalog && selectedChannelIds.length) {
      if (!form.brandId) issues.push('Trendyol yayını için marka zorunludur.'); if (!form.modelCode.trim() || form.modelCode.trim().length > 40) issues.push('Trendyol yayını için en fazla 40 karakterlik model kodu zorunludur.'); if (form.title.trim().length > 100) issues.push('Trendyol ürün başlığı en fazla 100 karakter olabilir.')
      if (!mediaUrls.length && !mediaFiles.length) issues.push('Trendyol yayını için en az bir HTTPS görsel adresi zorunludur.'); if (!mediaUrls.length && mediaFiles.length) issues.push('Yerel dosya katalogda önizleme içindir; Trendyol yayını için en az bir herkese açık HTTPS görsel adresi ekleyin.'); if (mediaUrls.length + mediaFiles.length > 8) issues.push('Trendyol yayını için en fazla 8 görsel kullanılabilir.'); if (mediaUrls.some(url => !url.startsWith('https://'))) issues.push('Tüm görsel adresleri HTTPS olmalıdır.')
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

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const submitter = (event.nativeEvent as SubmitEvent).submitter
    const submitData = new FormData(event.currentTarget)
    const saveAndStay = submitter?.getAttribute('data-submit-intent') === 'save'
      || (submitter?.getAttribute('name') === 'intent' && submitter?.getAttribute('value') === 'save')
      || submitData.get('intent') === 'save'
    const requireCompleteCatalog = !editProductId || !saveAndStay
    if (wizardStep !== 2 && !saveAndStay) {
      setWizardStep(2)
      return
    }
    setError(undefined); setNotice(''); showFeedback(editProductId ? 'Ürün değişiklikleri kaydediliyor…' : 'Ürün oluşturuluyor…', 'info'); setSubmitting(true); let productCreated: Product | undefined
    try {
      if (requireCompleteCatalog && form.categoryId && requirements.isLoading) throw new Error('Kategori özellikleri yükleniyor. Kaydetmeden önce kısa süre bekleyin.')
      if (requireCompleteCatalog && form.categoryId && requirements.isError) throw new Error('Kategori özellikleri alınamadı. Önce kategori eşleştirmesini kontrol edin.')
      const requirementList = mappedRequirements; const rows = rowsForSubmit(requireCompleteCatalog); validate(rows, requireCompleteCatalog)
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
      const safeDescription = sanitizeRichText(form.description)
      const variantPayload = (row: VariantDraft, index: number) => ({ sku: row.sku, barcode: row.barcode || null, modelCode: form.modelCode || null, sortOrder: index, weight: calculateDesi ? Number(form.weight) || null : null, width: calculateDesi ? Number(form.width) || null : null, height: calculateDesi ? Number(form.height) || null : null, length: calculateDesi ? Number(form.length) || null : null, desi: calculateDesi ? desi || 1 : Number(form.desi) || 1, costPrice: row.costPrice, options: row.options, attributes: Object.entries(row.attributeValueIds).map(([attributeId, valueId], attributeIndex) => ({ attributeId, valueId, textValue: null, numberValue: null, booleanValue: null, sortOrder: index * 100 + attributeIndex })) })
      const existingVariantIds = new Set(productToEdit.data?.variants.map(variant => variant.id) ?? [])
      const product = productToEdit.data
        ? await hubApi<Product>(`/products/${productToEdit.data.id}`, { method: 'PATCH', headers: { 'If-Match': `"v${productToEdit.data.version}"` }, body: JSON.stringify({ title: form.title, status: form.status, description: safeDescription, brandId: form.brandId || null, categoryId: form.categoryId || null, ...(shouldPersistAttributes ? { attributes: globalAttributes } : {}), variantsToCreate: rows.filter(row => !existingVariantIds.has(row.key)).map(variantPayload), variantUpdates: rows.filter(row => existingVariantIds.has(row.key)).map(row => ({ id: row.key, sku: row.sku, barcode: row.barcode || null, modelCode: form.modelCode || null, costPrice: row.costPrice, sortOrder: rows.findIndex(candidate => candidate.key === row.key), options: row.options, attributes: Object.entries(row.attributeValueIds).map(([attributeId, valueId], attributeIndex) => ({ attributeId, valueId, textValue: null, numberValue: null, booleanValue: null, sortOrder: rows.findIndex(candidate => candidate.key === row.key) * 100 + attributeIndex })) })) }) })
        : await hubApi<Product>('/products', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ title: form.title, status: form.status, description: safeDescription, brandId: form.brandId || null, categoryId: form.categoryId || null, attributes: globalAttributes, variants: rows.map(variantPayload) }) })
      productCreated = product; setCreated(product); const completed = ['ürün']; const warnings: string[] = []
      const mediaUrlsToPersist = editProductId && form.mediaUrls.trim() === initialEditMediaUrl.current.trim() ? [] : mediaUrls
      if (editProductId && form.mediaUrls.trim() !== initialEditMediaUrl.current.trim()) await hubApi(`/files/product-media?productId=${encodeURIComponent(product.id)}`, { method: 'DELETE', headers: { 'Idempotency-Key': key() } })
      for (const [index, url] of mediaUrlsToPersist.entries()) await hubApi('/files/product-media-url', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ productId: product.id, variantId: null, url, mediaRole: index === 0 ? 'PRIMARY' : 'GALLERY', sortOrder: index, altText: form.title }) })
      for (const [fileIndex, file] of mediaFiles.entries()) { const data = new FormData(); data.set('file', file); data.set('productId', product.id); data.set('mediaRole', mediaUrls.length + fileIndex === 0 ? 'PRIMARY' : 'GALLERY'); data.set('sortOrder', String(mediaUrls.length + fileIndex)); data.set('altText', form.title); await hubApi('/files/product-media', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: data }) }
      const rowsBySku = new Map(rows.map(row => [row.sku.trim().toLocaleUpperCase('tr-TR'), row]))
      for (const variant of product.variants) {
        const row = rowsBySku.get(variant.sku.trim().toLocaleUpperCase('tr-TR'))
        if (!row) continue
        await hubApi(`/files/product-media-variant?productId=${encodeURIComponent(product.id)}&variantId=${encodeURIComponent(variant.id)}`, { method: 'DELETE', headers: { 'Idempotency-Key': key() } })
        for (const [mediaIndex, mediaRef] of row.mediaRefs.entries()) {
          if (mediaRef.startsWith('url|')) {
            await hubApi('/files/product-media-url', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ productId: product.id, variantId: variant.id, url: mediaRef.slice(4), mediaRole: mediaIndex === 0 ? 'PRIMARY' : 'GALLERY', sortOrder: mediaIndex, altText: `${form.title} · ${row.optionSignature}` }) })
          } else if (mediaRef.startsWith('file|')) {
            const file = mediaFiles[Number(mediaRef.slice(5))]
            if (file) { const data = new FormData(); data.set('file', file); data.set('productId', product.id); data.set('variantId', variant.id); data.set('mediaRole', mediaIndex === 0 ? 'PRIMARY' : 'GALLERY'); data.set('sortOrder', String(mediaIndex)); data.set('altText', `${form.title} · ${row.optionSignature}`); await hubApi('/files/product-media', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: data }) }
          }
        }
      }
      if (mediaUrls.length || mediaFiles.length) completed.push('görseller')
      for (const variant of product.variants) {
        const row = rowsBySku.get(variant.sku.trim().toLocaleUpperCase('tr-TR'))
        if (!row) continue
        const currentStock = productToEdit.data?.variants.find(item => item.id === variant.id)?.onHand ?? 0
        const stockDelta = row.stock - currentStock
        if (stockDelta !== 0) await hubApi(`/inventory/${variant.id}/adjustments`, { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ quantityDelta: stockDelta, reason: productToEdit.data ? 'Ürün düzenleme stoğu' : 'İlk ürün stoğu', sourceEventId: key() }) })
        for (const connectionId of selectedChannelIds) {
          const pricing = channelPriceDraft(connectionId)
          await hubApi('/channel-offers', { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ connectionId, variantId: variant.id, listPrice: Number(pricing.listPrice), salePrice: Number(pricing.salePrice), currency: form.currency || 'TRY', vatRate: Number(form.vatRate || 0), vatInclusion: form.vatIncluded, roundingMode: 'HALF_EVEN', safetyStock: Number(form.safetyStock || 0), status: 'ACTIVE', reason: 'İlk ürün fiyatı' }) })
        }
      }
      if (rows.some(row => row.stock > 0)) completed.push('stok'); if (selectedChannelIds.length) completed.push('kanal fiyatları')
      for (const connectionId of selectedChannelIds) {
        try {
          let listingProfileVersion: number | undefined
          try {
            const listingProfile = await hubApi<{ version: number }>(`/products/${product.id}/listing-profiles/${connectionId}`)
            listingProfileVersion = listingProfile.version
          } catch (reason) {
            if (!(reason instanceof ApiRequestError) || reason.status !== 404) throw reason
          }
          await hubApi(`/products/${product.id}/listing-profiles/${connectionId}`, { method: 'PUT', headers: listingProfileVersion == null ? {} : { 'If-Match': `"v${listingProfileVersion}"` }, body: JSON.stringify({ titleOverride: null, descriptionOverride: null, externalCategoryId: null, externalBrandId: null, deliveryTimeDays: null, enabled: true }) })
          const accepted = await hubApi<AcceptedJob>(`/products/${product.id}/publication-jobs`, { method: 'POST', headers: { 'Idempotency-Key': key() }, body: JSON.stringify({ connectionId }) })
          completed.push(`yayın işi ${accepted.jobId}`)
        } catch (reason) { warnings.push(reason instanceof Error ? reason.message : 'Yayın işi oluşturulamadı.') }
      }
      const message = `${completed.join(', ')} kaydedildi.${warnings.length ? ` Yayın uyarısı: ${warnings.join(' ')}` : ''}`
      setNotice(message); showFeedback(message, warnings.length ? 'info' : 'success')
      await client.invalidateQueries({ queryKey: ['products'] })
      if (editProductId) {
        await client.invalidateQueries({ queryKey: ['product', editProductId] })
        await productToEdit.refetch()
      }
    } catch (reason) {
      const message = reason instanceof Error ? reason.message : 'Kayıt tamamlanamadı.'
      const feedbackMessage = productCreated ? `Ürün kaydedildi ancak sonraki işlem tamamlanamadı: ${message}` : message
      setError(reason); setNotice(feedbackMessage); showFeedback(feedbackMessage, 'error')
    } finally { setSubmitting(false) }
  }

  const publishConnections = (connections.data?.items ?? []).filter(isProductPublicationConnection)
  const platformCards = publishConnections.map(connection => ({
    code: connection.platformCode.trim().toLocaleLowerCase('tr-TR'),
    name: connection.displayName,
    initial: connection.displayName.trim().charAt(0).toLocaleUpperCase('tr-TR') || 'R',
    tone: connection.platformCode.trim().toLocaleLowerCase('tr-TR'),
    connection
  }))
  const selectedPublishConnections = publishConnections.filter(item => selectedChannelIds.includes(item.id))
  const assignedMediaUrls = [...new Set(variantRows.flatMap(row => row.mediaRefs.filter(ref => ref.startsWith('url|')).map(ref => ref.slice(4))))]
  const familyMediaUrls = productToEdit.data?.familyMediaUrls ?? []
  const familyOnlyMediaUrls = familyMediaUrls.filter(url => !mediaUrls.some(current => current.localeCompare(url, undefined, { sensitivity: 'accent' }) === 0))
  const mediaChoices: ProductMediaOption[] = ([...new Set([...mediaUrls, ...familyMediaUrls, ...assignedMediaUrls])].map((url, index) => ({ value: `url|${url}`, label: `${index + 1}. ${url}`, url })) as ProductMediaOption[]).concat(mediaFiles.map((file, index) => ({ value: `file|${index}`, label: `Dosya · ${file.name}`, file })))
  const bulkMediaGroups = useMemo<VariantMediaGroup[]>(() => {
    const groups: VariantMediaGroup[] = []
    const names = new Set<string>()
    const hasPanelColorSource = (productToEdit.data?.options ?? []).some(option => isColorOptionName(option.label) && !isWebColorOptionName(option.label))
      || optionRequirements.some(item => isColorOptionName(item.attribute.name) && !isWebColorOptionName(item.attribute.name))
      || variantRows.some(row => variantOptionEntries(row).some(option => isColorOptionName(option.name) && !isWebColorOptionName(option.name)))
    const addGroup = (group: VariantMediaGroup) => {
      if (hasPanelColorSource && isWebColorOptionName(group.name)) return
      const canonicalName = isColorOptionName(group.name) ? 'Renk' : group.name.trim()
      const name = canonicalName.toLocaleLowerCase('tr-TR')
      if (!group.values.length || names.has(name)) return
      names.add(name)
      groups.push({ ...group, name: canonicalName })
    }
    // Existing products expose their persisted option groups separately from
    // category requirements. Prefer these IDs/values so imported variants such
    // as “Beden: Tek Ebat” can be targeted even when the current category no
    // longer exposes the original option requirement.
    for (const option of productToEdit.data?.options ?? []) {
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
  }, [attributeSelections, optionRequirements, productToEdit.data?.options, variantRows])
  const variantFilterGroups = useMemo(() => bulkMediaGroups.filter(group => variantRows.some(row => rowOptionValue(row, group).trim())), [bulkMediaGroups, variantRows])
  const activeVariantFilterEntries = variantFilterGroups.map(group => ({ group, valueIds: variantFilterSelections[group.id] ?? [] })).filter(entry => entry.valueIds.length > 0)
  const hasVariantFilters = activeVariantFilterEntries.length > 0
  const matchingVariantCount = variantRows.filter(row => rowMatchesVariantFilters(row)).length
  const barcodeRowCount = variantRows.filter(row => row.barcode.trim()).length
  const emptySkuBarcodeRowCount = variantRows.filter(row => row.barcode.trim() && !row.sku.trim()).length
  const selectedBulkMediaGroup = variantMediaModal?.mode === 'bulk' ? bulkMediaGroups.find(group => group.id === variantMediaModal.groupId) : undefined
  const selectedBulkMediaValue = selectedBulkMediaGroup?.values.find(value => value.id === variantMediaModal?.valueId)
  const selectedBulkMediaMatchCount = selectedBulkMediaGroup && selectedBulkMediaValue ? variantRows.filter(row => rowMatchesVariantMediaValue(row, selectedBulkMediaGroup, selectedBulkMediaValue)).length : 0
  const hasBasicProductData = Boolean(form.title.trim() && form.description.trim() && form.brandId && form.modelCode.trim() && form.barcode.trim())
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
  const catalogValidationDetail = catalogValidationIssues.length ? `${catalogValidationIssues.slice(0, 2).join(' ')}${catalogValidationIssues.length > 2 ? ` +${catalogValidationIssues.length - 2} eksik` : ''}` : 'Kategori özellikleri, seçenekler ve Web Color yayınlamaya hazır.'
  const productChecks = [
    { title: 'Temel Ürün Verileri', detail: hasBasicProductData ? 'İsim, açıklama, marka ve barkod bilgileri eksiksiz.' : 'İsim, açıklama, marka, model veya barkod bilgisi eksik.', ok: hasBasicProductData },
    { title: 'Görsel Kalitesi', detail: hasProductMedia ? `${mediaCount} adet ürün görseli eklendi.` : 'En az bir yüksek çözünürlüklü görsel ekleyin.', ok: hasProductMedia },
    { title: 'Varyant Bilgileri', detail: hasVariantData ? 'Varyant yapısı yayınlanmaya hazır.' : 'Seçilen seçenekler için varyant satırlarını oluşturun.', ok: hasVariantData },
    { title: 'Kategori ve Web Color', detail: catalogValidationDetail, ok: catalogValidationIssues.length === 0 }
  ]
  const canAddVariantCombinations = useMemo(() => {
    if (!variantAttributeIds.length) return false
    try {
      const generated = buildVariantMatrix(mappedRequirements, variantAttributeIds, attributeSelections, form.baseSku || form.modelCode || form.title, fallbackListPrice, fallbackSalePrice, fallbackCostPrice, initialStock)
      const existing = new Set(variantRows.map(row => variantSignatureKey(row.optionSignature)))
      return generated.some(row => !existing.has(variantSignatureKey(row.optionSignature)))
    } catch { return false }
  }, [attributeSelections, fallbackCostPrice, fallbackListPrice, fallbackSalePrice, form.baseSku, form.modelCode, form.title, initialStock, mappedRequirements, variantAttributeIds, variantRows])
  const selectedVariantPlatformRow = variantPlatformPricing ? variantRows.find(row => row.key === variantPlatformPricing.rowKey) : undefined

  return <Page className={`product-add-page${editProductId ? ' product-edit-page' : ''}`} title={editProductId ? "Ürün Düzenle" : "Yeni Ürün Ekle"} eyebrow="Katalog">
    {variantPlatformPricing && selectedVariantPlatformRow && <BulkVariantPlatformPricingModal row={selectedVariantPlatformRow} rows={variantRows} platforms={selectedVariantPlatformRow.platformStatuses ?? []} productName={form.title} modelCode={form.modelCode} saving={variantPlatformPricingSaving} onClose={() => setVariantPlatformPricing(null)} onSave={drafts => void saveVariantPlatformPricingMatrix(drafts)} />}
    {/* @ts-ignore: legacy inline modal is disabled while the shared multi-platform modal is used above. */}
    {false && variantPlatformPricing && selectedVariantPlatformRow && <div className="workspace-modal-backdrop variant-platform-pricing-backdrop" role="presentation" onMouseDown={() => !variantPlatformPricingSaving && setVariantPlatformPricing(null)}><section className="workspace-modal variant-platform-pricing-modal" role="dialog" aria-modal="true" aria-labelledby="variant-platform-pricing-title" onMouseDown={event => event.stopPropagation()}><header><div><p className="eyebrow">VARYANT KANAL FİYATI</p><h2 id="variant-platform-pricing-title">{platformDisplayName(variantPlatformPricing.platform)} fiyatlandırması</h2><p>{selectedVariantPlatformRow.optionSignature} · {selectedVariantPlatformRow.barcode || selectedVariantPlatformRow.sku}</p></div><button type="button" className="modal-close" onClick={() => setVariantPlatformPricing(null)} disabled={variantPlatformPricingSaving} aria-label="Fiyat penceresini kapat"><UiIcon name="close" /></button></header><div className="variant-platform-pricing-body"><div className="variant-platform-pricing-channel"><span className={`publish-platform-mark ${variantPlatformPricing.platform.platformCode.toLocaleLowerCase('tr-TR')}`}><img className={`publish-platform-logo ${platformLogoClass(variantPlatformPricing.platform.platformCode)}`} src={platformLogoSource(variantPlatformPricing.platform.platformCode) ?? '/platforms/trendyol.png'} alt="" /></span><div><strong>{platformDisplayName(variantPlatformPricing.platform)}</strong><small>{variantPlatformPricing.platform.isLinked ? 'Bu varyant platforma bağlı.' : 'Bu varyant için bağlantı henüz eşleşmemiş.'}</small></div></div><div className="variant-platform-pricing-fields"><label>Liste fiyatı<input autoFocus type="number" min="0" step="0.01" value={variantPlatformPricingDraft.listPrice} onChange={event => setVariantPlatformPricingDraft(current => ({ ...current, listPrice: event.target.value }))} /></label><label>Satış fiyatı<input type="number" min="0" step="0.01" value={variantPlatformPricingDraft.salePrice} onChange={event => setVariantPlatformPricingDraft(current => ({ ...current, salePrice: event.target.value }))} /></label></div><p className="variant-platform-pricing-help">Bu değer yalnızca seçtiğiniz varyantın {platformDisplayName(variantPlatformPricing.platform)} kanal teklifine kaydedilir; panel ana fiyatı değişmez.</p></div><footer><button type="button" className="secondary" onClick={() => setVariantPlatformPricing(null)} disabled={variantPlatformPricingSaving}>Vazgeç</button><button type="button" onClick={() => void saveVariantPlatformPricing()} disabled={variantPlatformPricingSaving || !variantPlatformPricing.platform.connectionId}>{variantPlatformPricingSaving ? 'Kaydediliyor…' : 'Fiyatı kaydet'}</button></footer></section></div>}
    <p className="lede page-lede">Ürün bilgilerini ve varyantları hazırlayın; yayınlama adımında kanalları seçip gönderim kuyruğunu başlatın.</p><div className="product-add-wizardbar"><div className="product-add-stepper"><div className="product-add-progress" role="tablist" aria-label={editProductId ? 'Ürün düzenleme adımları' : 'Ürün ekleme adımları'}><button type="button" className={wizardStep === 1 ? 'active' : ''} role="tab" aria-selected={wizardStep === 1} onClick={() => setWizardStep(1)}><span>1</span><strong>Ürün bilgileri ve varyantlar</strong></button><i aria-hidden="true" /><button type="button" className={wizardStep === 2 ? 'active' : ''} role="tab" aria-selected={wizardStep === 2} onClick={() => setWizardStep(2)}><span>2</span><strong>Yayınlama</strong></button></div></div></div><form id="product-creation-form" className="product-creation-workspace product-add-workspace" data-wizard-step={wizardStep} onSubmit={submit} onInvalidCapture={handleInvalid} noValidate>
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
            <label>Stok Kodu<input className="technical-field sku-value" value={form.baseSku} onChange={event => updateField('baseSku', event.target.value)} placeholder="RAV-BLUZ" /></label>
            <label>Barkod<input className="technical-field barcode-value" value={form.barcode} onChange={event => updateField('barcode', event.target.value)} placeholder="Varyantsız üründe kullanılır" /></label>
            <label className="desi-input-field">Desi<span className="desi-inline-control"><input value={form.desi} onChange={event => { setCalculateDesi(false); updateField('desi', event.target.value) }} type="number" min="0.01" step="0.01" required /><button type="button" className="secondary" onClick={() => setDesiCalculatorOpen(true)}>Hesapla</button></span></label>
          </div>
          <label className="wide product-description-field">Açıklama<RichTextEditor value={form.description} onChange={value => updateField('description', value)} /></label>
        </div>
      </section>
      <div className="product-top-sidebar">
        <section className="panel product-step-card product-pricing-card">
          <div className="editor-section-title"><span>2</span><div><h2>Fiyat, stok ve vergi</h2><p>Başlangıç değerleri varyantlara uygulanır.</p></div></div>
          <div className="product-step-grid">
            <label>Liste fiyatı<input value={form.listPrice} onChange={event => updateField('listPrice', event.target.value)} type="number" min="0" step="0.01" /></label>
            <label>Satış fiyatı<input value={form.salePrice} onChange={event => updateField('salePrice', event.target.value)} type="number" min="0" step="0.01" /></label>
            <label>Maliyet<input value={form.costPrice} onChange={event => updateField('costPrice', event.target.value)} type="number" min="0" step="0.01" /></label>
            <label>Para birimi<select value={form.currency} onChange={event => updateField('currency', event.target.value)}><option>TRY</option><option>USD</option><option>EUR</option></select></label>
            <label>KDV oranı<select value={form.vatRate} onChange={event => updateField('vatRate', event.target.value)}><option value="1">%1</option><option value="10">%10</option><option value="20">%20</option></select></label>
            <label>KDV dahil mi<select value={form.vatIncluded} onChange={event => updateField('vatIncluded', event.target.value)}><option value="INCLUDED">Evet</option><option value="EXCLUDED">Hayır</option></select></label>
            <label>Stok<input value={form.initialStock} onChange={event => updateField('initialStock', event.target.value)} type="number" min="0" step="1" /></label>
            <label>Güvenlik stoğu<input value={form.safetyStock} onChange={event => updateField('safetyStock', event.target.value)} type="number" min="0" step="1" /></label>
          </div>
          {platformCards.length > 0 && <section className="marketplace-pricing-bars" aria-label="Pazaryerlerine özel fiyatlandırma">
            <div className="marketplace-pricing-bars-head"><div><strong>Pazaryerlerine özel fiyatlandırma</strong><small>Her kanal için ayrı liste ve satış fiyatı tanımlayın.</small></div><span>{selectedChannelIds.length} kanal seçili</span></div>
            <div className="marketplace-pricing-bar-list">
              {platformCards.map(card => {
                const pricing = channelPriceDraft(card.connection.id); const selected = selectedChannelIds.includes(card.connection.id)
                return <fieldset className={`marketplace-pricing-bar${selected ? ' selected' : ''}`} key={card.connection.id}>
                  <legend><span className={`publish-platform-mark ${card.tone}`}><img className={`publish-platform-logo ${platformLogoClass(card.connection.platformCode)}`} src={platformLogoSource(card.connection.platformCode) ?? '/platforms/trendyol.png'} alt="" aria-hidden="true" /></span><span><strong>{card.name}</strong><small>{selected ? 'Yayınlanacak kanal' : 'Yayın için seçilmedi'}</small></span></legend>
                  <label>Liste fiyatı<input value={pricing.listPrice} onChange={event => updateChannelPrice(card.connection.id, 'listPrice', event.target.value)} type="number" min="0" step="0.01" /></label>
                  <label>Satış fiyatı<input value={pricing.salePrice} onChange={event => updateChannelPrice(card.connection.id, 'salePrice', event.target.value)} type="number" min="0" step="0.01" /></label>
                  <button type="button" className={`secondary marketplace-pricing-toggle${selected ? ' selected' : ''}`} onClick={() => updateChannel(card.connection.id)}>{selected ? 'Kanaldan çıkar' : 'Yayın için seç'}</button>
                </fieldset>
              })}
            </div>
          </section>}
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
    {desiCalculatorOpen && <div className="workspace-modal-backdrop" role="presentation" onMouseDown={() => setDesiCalculatorOpen(false)}><section className="workspace-modal desi-calculator-modal" role="dialog" aria-modal="true" aria-labelledby="desi-calculator-title" onMouseDown={event => event.stopPropagation()}><header><div><h2 id="desi-calculator-title">Desi hesapla</h2><p>En × Boy × Yükseklik / 3000 formülü kullanılır.</p></div><button type="button" className="modal-close" onClick={() => setDesiCalculatorOpen(false)} aria-label="Pencereyi kapat"><UiIcon name="close" /></button></header><div className="desi-calculator-body"><div className="product-step-grid"><label>Ağırlık (kg)<input value={form.weight} onChange={event => updateField('weight', event.target.value)} type="number" min="0" step="0.01" /></label><label>En (cm)<input value={form.width} onChange={event => updateField('width', event.target.value)} type="number" min="0" step="0.1" /></label><label>Boy (cm)<input value={form.length} onChange={event => updateField('length', event.target.value)} type="number" min="0" step="0.1" /></label><label>Yükseklik (cm)<input value={form.height} onChange={event => updateField('height', event.target.value)} type="number" min="0" step="0.1" /></label></div><div className="calculated-field"><small>Hesaplanan desi</small><strong>{desi ? desi.toLocaleString('tr-TR', { maximumFractionDigits: 2 }) : 'Ölçüleri girin'}</strong></div></div><footer><button type="button" className="secondary" onClick={() => setDesiCalculatorOpen(false)}>İptal</button><button type="button" disabled={!desi} onClick={() => { updateField('desi', String(Number(desi.toFixed(2)))); setCalculateDesi(true); setDesiCalculatorOpen(false) }}>Uygula</button></footer></section></div>}

    {mediaUrlSettingsOpen && <div className="workspace-modal-backdrop" role="presentation" onMouseDown={() => setMediaUrlSettingsOpen(false)}><section className="workspace-modal product-media-url-modal" role="dialog" aria-modal="true" aria-labelledby="product-media-url-title" onMouseDown={event => event.stopPropagation()}><header><div><h2 id="product-media-url-title">Link ile görsel ekle</h2><p>Her satıra bir HTTPS adresi yazın. Eklenen görseller varyant seçimlerinde de kullanılabilir.</p></div><button type="button" className="modal-close" onClick={() => setMediaUrlSettingsOpen(false)} aria-label="Pencereyi kapat"><UiIcon name="close" /></button></header><label className="product-media-url-field">Görsel URL listesi<textarea id="product-media-urls" aria-describedby="media-url-help" value={form.mediaUrls} onChange={event => updateField('mediaUrls', event.target.value)} placeholder="Örn. https://site.com/gorsel-1.jpg&#10;https://site.com/gorsel-2.png" autoFocus /><small id="media-url-help" className="field-help">Adresleri ayrı satırda veya ; / | ayraçlarıyla yazabilirsiniz. İlk adres ürünün genel ana görselidir; varyant görseli seçimi aşağıdaki tabloda yapılır.</small></label><footer><span>{mediaUrls.length} adres kayıtlı</span><button type="button" onClick={() => setMediaUrlSettingsOpen(false)}>Tamam</button></footer></section></div>}
    <div className="product-layout-grid"><div className="product-main-stack">
      <section className="panel product-step-card product-media-card"><div className="editor-section-title"><span>4</span><div><h2>Görseller</h2><p>JPEG/PNG dosyası yükleyebilir veya internetten erişilebilen HTTPS adresleri ekleyebilirsiniz. Aynı modelin diğer renk görselleri de burada görünür.</p></div><div className="product-media-header-actions">{(mediaUrls.length > 0 || mediaFiles.length > 0 || variantRows.some(row => row.mediaRefs.length > 0)) && <button type="button" className="secondary product-media-clear-all-button" onClick={clearAllMedia}>Tümünü temizle</button>}<button type="button" className="product-media-link-button" onClick={() => setMediaUrlSettingsOpen(true)} aria-label="Link ile görsel ekle" title="Link ile görsel ekle"><UiIcon name="externalLink" />{mediaUrls.length > 0 && <b>{mediaUrls.length}</b>}</button></div></div><label className="upload-ghost-box product-media-upload"><input type="file" accept="image/jpeg,image/png" multiple onChange={event => { handleMediaFiles(Array.from(event.target.files ?? [])); event.currentTarget.value = '' }} /><strong>{mediaFiles.length ? `${mediaFiles.length} dosya seçildi` : 'Ürün görsellerini dosya olarak seç'}</strong><small>Adet sınırı yok · JPEG veya PNG · dosya başına en fazla 6 MB</small></label>{(mediaUrls.length > 0 || mediaFiles.length > 0 || familyOnlyMediaUrls.length > 0) && <div className="media-preview-strip">{mediaFiles.map((file, index) => <LocalImagePreview key={`${file.name}-${file.lastModified}-${index}`} file={file} alt={`${form.title || 'Ürün'} ${index + 1}`} caption={index === 0 && !mediaUrls.length ? 'Ana görsel' : file.name} onRemove={() => setMediaFiles(files => files.filter((_, i) => i !== index))} onZoom={url => setLightboxImage({ url, title: form.title || 'Ürün Görseli' })} />)}{mediaUrls.map((url, index) => <figure key={`${url}-${index}`} className={`image-preview-card media-sortable ${dragOverMediaUrl === url ? 'is-media-drag-over' : ''}`} draggable onDragStart={() => setDraggedMediaUrl(url)} onDragOver={event => { event.preventDefault(); setDragOverMediaUrl(url) }} onDrop={event => { event.preventDefault(); reorderMedia(draggedMediaUrl ?? '', url) }} onDragEnd={() => { setDraggedMediaUrl(null); setDragOverMediaUrl(null) }}><img src={url} alt={`${form.title || 'Ürün'} ${index + 1}`} className="clickable-thumb" onClick={() => setLightboxImage({ url, title: form.title || 'Ürün Görseli' })} title="Büyütmek için tıklayın" /><button type="button" className="image-remove-btn" title="Görseli kaldır" onClick={e => { e.stopPropagation(); const next = mediaUrls.filter((_, i) => i !== index).join('\n'); updateField('mediaUrls', next) }}><UiIcon name="close" /></button><figcaption>{index === 0 && !mediaFiles.length ? 'Ana görsel' : `${index + 1}. görsel`} · sürükle</figcaption></figure>)}{familyOnlyMediaUrls.map((url, index) => <figure key={`family-${url}`} className={`image-preview-card family-media-preview media-sortable ${dragOverMediaUrl === url ? 'is-media-drag-over' : ''}`} draggable onDragStart={() => setDraggedMediaUrl(url)} onDragOver={event => { event.preventDefault(); setDragOverMediaUrl(url) }} onDrop={event => { event.preventDefault(); reorderMedia(draggedMediaUrl ?? '', url) }} onDragEnd={() => { setDraggedMediaUrl(null); setDragOverMediaUrl(null) }}><img src={url} alt={`${form.title || 'Ürün'} renk ailesi görseli ${index + 1}`} className="clickable-thumb" onClick={() => setLightboxImage({ url, title: `${form.title || 'Ürün'} · Renk ailesi` })} title="Renk ailesi görselini büyüt" /><figcaption>Renk varyantı görseli · sürükle</figcaption></figure>)}</div>}
      </section>

      <section className="panel product-step-card product-options-card">
        <div className="editor-section-title">
          <span>5</span>
          <div>
            <h2>Ürün seçenekleri</h2>
            <p>Seçenek grubu ve değerlerini burada seçin. Mevcut ürünlerde kayıtlı Renk ve Beden değerleri otomatik işaretlenir; yeni seçimler “Ürünleri ekle” ile varyant satırlarına eklenir.</p>
          </div>
        </div>
        <div className="attribute-variant-action">
          <div>
            <strong>Varyantları oluştur</strong>
             <small>{variantAttributeIds.length ? `${variantAttributeIds.map(id => allRequirements.find(item => item.attributeId === id)?.attribute.name).filter(Boolean).join(' × ')} · ${variantAttributeIds.reduce((total, id) => total * Math.max(1, attributeSelections[id]?.length ?? 0), 1)} kombinasyon` : 'Önce seçenek grubunu ve değerlerini işaretleyin.'}</small>
          </div>
          <div className="attribute-variant-actions">
            <button type="button" onClick={generateVariants} disabled={!canAddVariantCombinations}>{canAddVariantCombinations ? 'Ürünleri ekle' : 'Seçenekler güncel'}</button>
          </div>
        </div>
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
              return (
              <article className={`attribute-builder-card ${expanded ? 'is-open' : ''}`} key={item.attributeId}>
                <button type="button" className="attribute-builder-disclosure" aria-expanded={expanded} aria-controls={expanded ? `option-values-${item.attributeId}` : undefined} disabled={!expandable} onClick={() => setExpandedOptionGroupIds(current => ({ ...current, [item.attributeId]: !expanded }))}>
                  <span className="attribute-builder-disclosure-copy"><strong>{item.attribute.name}</strong><small>{item.attribute.values.length} değer · seçenek grubu</small></span>
                  <span className="attribute-builder-disclosure-meta"><small className={variantAttributeIds.includes(item.attributeId) ? 'attribute-builder-selected' : ''}>{(attributeSelections[item.attributeId]?.length ?? 0) > 0 ? `${attributeSelections[item.attributeId].length} değer seçildi` : 'Değer seçin'}</small>{expandable && <UiIcon name="chevronDown" />}</span>
                </button>
                {expanded && <div id={`option-values-${item.attributeId}`} className="attribute-builder-values">
                {item.attribute.values.length ? (
                  <div className="option-chip-list">
                    {sortOptionValues(item.attribute.name, item.attribute.values).map(value => {
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
            <div className="variant-table-editor"><div className="variant-table-head"><span>#</span><span>Seçenek</span><span>Barkod</span><span className="variant-table-header-with-action"><span>Stok kodu</span><div className="variant-header-action-shell" ref={barcodeSkuActionRef}><button type="button" className="variant-header-action" onClick={() => setBarcodeSkuMenuOpen(current => !current)} aria-label="Barkoddan doldurma seçenekleri" aria-haspopup="menu" aria-expanded={barcodeSkuMenuOpen} title="Barkoddan stok kodu doldurma seçenekleri"><BarcodeFillIcon /></button>{barcodeSkuMenuOpen && <div className="variant-header-action-menu" role="menu"><button type="button" role="menuitem" disabled={!emptySkuBarcodeRowCount} onClick={() => applyBarcodeToSku('missing')}><span><strong>Eksik stok kodlarını doldur</strong><small>Sadece boş satırlar · {emptySkuBarcodeRowCount} aday</small></span><UiIcon name="externalLink" /></button><button type="button" role="menuitem" disabled={!barcodeRowCount} onClick={() => applyBarcodeToSku('all')}><span><strong>Barkodları stok koduna uygula</strong><small>Barkodu olan {barcodeRowCount} satırı güncelle</small></span><i aria-hidden="true">!</i></button><p>Çakışan barkodlar otomatik olarak atlanır; mevcut kodlar ilk seçenekte korunur.</p></div>}</div></span><span>Stok</span><span>Fiyat</span><span>Maliyet</span><span>Liste fiyatı</span><span>Platform</span><span>Görsel</span><span>İşlem</span></div>{variantRows.length ? variantRows.map((row, index) => { const matchesFilter = rowMatchesVariantFilters(row); return <div data-variant-row-key={row.key} className={`variant-table-row ${hasVariantFilters && matchesFilter ? 'is-filter-match' : ''} ${hasVariantFilters && !matchesFilter ? 'is-filter-dimmed' : ''} ${draggedVariantKey === row.key ? 'is-dragging' : ''} ${dragOverVariantKey === row.key ? 'is-drag-target' : ''}`} key={row.key}><div className="variant-row-lead" title="Sıralamak için tutup sürükleyin" aria-label={`${row.optionSignature} varyantını sıralamak için sürükleyin`} onPointerDown={event => beginVariantPointerDrag(event, row.key)}><span className="variant-row-number">{index + 1}</span><span className="variant-drag-handle"><VariantDragHandleIcon /></span></div><input aria-label={`${index + 1}. varyant seçenekleri`} value={row.optionSignature} readOnly /><input aria-label={`${row.optionSignature} barkod`} className="technical-field barcode-value" value={row.barcode} onChange={event => updateVariantRow(row.key, 'barcode', event.target.value)} placeholder="EAN / barkod" /><input aria-label={`${row.optionSignature} stok kodu`} className="technical-field sku-value" value={row.sku} onChange={event => updateVariantRow(row.key, 'sku', event.target.value)} placeholder="Varyant SKU" /><input aria-label={`${row.optionSignature} stok`} value={row.stock} onChange={event => updateVariantRow(row.key, 'stock', event.target.value)} type="number" min="0" step="1" /><input aria-label={`${row.optionSignature} satış fiyatı`} value={row.salePrice} onChange={event => updateVariantRow(row.key, 'salePrice', event.target.value)} type="number" min="0" step="0.01" /><input aria-label={`${row.optionSignature} maliyeti`} value={row.costPrice} onChange={event => updateVariantRow(row.key, 'costPrice', event.target.value)} type="number" min="0" step="0.01" /><input aria-label={`${row.optionSignature} liste fiyatı`} value={row.listPrice} onChange={event => updateVariantRow(row.key, 'listPrice', event.target.value)} type="number" min="0" step="0.01" /><div className="variant-platform-statuses" aria-label={`${row.optionSignature} platform bağlantıları`}>{row.platformStatuses?.length ? (() => { const linkedCount = row.platformStatuses.filter(item => item.isLinked).length; const isPartial = linkedCount > 0 && linkedCount < row.platformStatuses.length; const summaryClass = linkedCount === row.platformStatuses.length ? 'is-linked' : isPartial ? 'is-partial' : 'is-unlinked'; return <button type="button" className={`variant-platform-status variant-platform-status-summary ${summaryClass}`} onClick={() => openVariantPlatformPricing(row, row.platformStatuses![0])} title={`${row.platformStatuses.length} platform fiyatını görüntüle`} aria-label={`${row.optionSignature} platform fiyatlarını görüntüle`}><UiIcon name="platforms" /><i aria-hidden="true">{row.platformStatuses.length}</i></button> })() : <span className="variant-platform-empty">—</span>}</div><div className="variant-media-cell"><button type="button" className={`variant-media-button ${row.mediaRefs.length ? 'has-media' : ''}`} onClick={() => openVariantMediaPicker(row.key)} aria-label={`${row.optionSignature} görsellerini seç`} title="Varyant görsellerini seç"><VariantImageIcon />{row.mediaRefs.length > 0 && <i aria-hidden="true">{row.mediaRefs.length}</i>}</button></div><button type="button" className="secondary" onClick={() => setVariantRows(rows => rows.filter(item => item.key !== row.key))}>Sil</button></div> }) : <div className="empty small"><strong>Henüz varyant yok</strong><p>Özellik değerlerini seçip “Ürünleri ekle” dediğinizde varyant satırları burada oluşur.</p></div>}</div>
      </div>
      </section>
    </div></div>

    <section className="product-publish-step" aria-label={editProductId ? 'Ürün yayınlama ve güncelleme' : 'Ürün yayınlama'}><div className="product-publish-layout"><div className="product-publish-main"><div className="publish-platform-grid">{platformCards.length ? platformCards.map(card => { const selected = selectedChannelIds.includes(card.connection.id); return <article className={`publish-platform-card ${selected ? 'selected' : ''}`} key={card.connection.id}><button type="button" className="publish-platform-card-head" onClick={() => updateChannel(card.connection.id)} aria-pressed={selected}><span className={`publish-platform-mark ${card.tone}`}><img className={`publish-platform-logo ${platformLogoClass(card.connection.platformCode)}`} src={platformLogoSource(card.connection.platformCode) ?? '/platforms/trendyol.png'} alt="" aria-hidden="true" /></span><span><strong>{card.name}</strong><small>{selected ? 'Yayın için seçildi' : 'Bağlantı hazır'}</small></span><i className={`publish-platform-toggle ${selected ? 'on' : ''}`} aria-hidden="true"><b /></i></button><dl className="publish-platform-facts"><div><dt>Mağaza</dt><dd>{card.connection.externalStoreId || '—'}</dd></div><div><dt>Platform</dt><dd>{card.connection.platformCode}</dd></div><div><dt>Durum</dt><dd><span className="publish-platform-status active"><i aria-hidden="true" />Aktif bağlantı</span></dd></div></dl><div className={`publish-platform-readiness ${catalogValidationIssues.length ? 'has-issues' : 'is-ready'}`} aria-live="polite"><div className="publish-platform-readiness-head"><strong>{catalogValidationIssues.length ? 'Yayın öncesi eksikler' : 'Yayın kontrolü tamam'}</strong><span>{catalogValidationIssues.length ? <><UiIcon name="alert" /> {catalogValidationIssues.length} eksik</> : <><UiIcon name="check" /> Hazır</>}</span></div>{catalogValidationIssues.length > 0 && <ul>{catalogValidationIssues.slice(0, 3).map((issue, index) => <li key={`${issue}-${index}`}>{issue}</li>)}</ul>}</div></article> }) : <div className="publish-connections-empty"><strong>Aktif bağlantı bulunamadı</strong><p>Yayınlama için önce Platformlar sayfasından aktif bir bağlantı oluşturun.</p><Link to="/integrations">Platformları yönet <UiIcon name="arrowRight" /></Link></div>}</div><details className="scheduled-publish-panel" open={scheduledPublishOpen} onToggle={event => setScheduledPublishOpen((event.currentTarget as HTMLDetailsElement).open)}><summary><span><UiIcon name="calendar" /> Yayın kuyruğu</span><UiIcon name="chevronDown" /></summary><div className="scheduled-publish-info"><strong>{editProductId ? 'Güncelleme ve yayın kuyruğu hazır' : 'Otomatik sıraya alma aktif'}</strong><p>{editProductId ? 'Değişiklikler kaydedildikten sonra seçtiğiniz aktif platformlarda yayın veya güncelleme işi oluşturulur.' : 'Ürün oluşturulduktan sonra seçtiğiniz aktif platformlarda yayın kuyruğuna alınır.'}</p><small>Planlı tarih ve saat seçimi platform bağlantısı desteklediğinde etkinleşecektir.</small></div></details></div><aside className="publish-checklist-panel"><div className="publish-checklist-heading"><UiIcon name="grid" /><div><h2>Kontrol Listesi</h2><p>Yayınlamadan önce son kontroller</p></div></div><div className="publish-checklist-items">{productChecks.map(check => <article className={check.ok ? 'complete' : 'incomplete'} key={check.title}><span aria-hidden="true">{check.ok ? <UiIcon name="check" /> : <UiIcon name="alert" />}</span><div><strong>{check.title}</strong><p>{check.detail}</p></div></article>)}{selectedPublishConnections.length === 0 && <article className="publish-check-warning"><span aria-hidden="true">!</span><div><strong>Yayın platformu seçilmedi</strong><p>Ürünü yayınlamak istediğiniz aktif platformları seçin.</p></div></article>}</div><div className="publish-checklist-footer"><span>Yayınlanacak Platform</span><strong>{selectedPublishConnections.length}</strong><button type="submit" disabled={submitting}>{submitting ? (editProductId ? 'Kaydediliyor…' : 'Ürün oluşturuluyor…') : (editProductId ? 'Değişiklikleri kaydet' : <><UiIcon name="sparkle" /> Ürünü Oluştur</>)}</button><small>{editProductId ? 've seçili platformları güncelle' : 've seçili platformlarda yayınla'}</small></div></aside></div></section>

    <section className="product-submit-sticky"><div><strong>{editProductId ? 'Ürün düzenlemeye hazır' : 'Ürün bilgileri hazır'}</strong><p>{variantRows.length || 1} satış satırı · {selectedChannelIds.length} seçili kanal</p></div><div className="product-submit-actions">{editProductId && <button type="submit" name="intent" value="save" className="secondary" data-submit-intent="save" form="product-creation-form" disabled={submitting}>{submitting ? 'Kaydediliyor…' : 'Kaydet'}</button>}<button type="button" onClick={() => setWizardStep(2)}>Yayınlamaya devam et <UiIcon name="arrowRight" /></button></div></section>
    <ErrorBox error={error ?? categories.error ?? brands.error ?? connections.error} />{created && <p className="success">Oluşturuldu: <Link to={`/products/${created.id}`}>ürünü aç</Link></p>}
    <OperationFeedbackToast feedback={feedback} onClose={() => { setFeedback(null); setNotice('') }} />
    {lightboxImage && <ImageLightboxModal image={lightboxImage} onClose={() => setLightboxImage(null)} />}
    {variantMediaModal && <VariantMediaPickerModal mode={variantMediaModal.mode} options={mediaChoices} selectedRefs={variantMediaModal.draftRefs} groups={variantMediaModal.mode === 'bulk' ? bulkMediaGroups : undefined} selectedGroupId={variantMediaModal.groupId} selectedValueId={variantMediaModal.valueId} matchedVariantCount={variantMediaModal.mode === 'bulk' ? selectedBulkMediaMatchCount : undefined} onRefsChange={values => setVariantMediaModal(current => current ? { ...current, draftRefs: values } : current)} onGroupChange={groupId => setVariantMediaModal(current => { const group = bulkMediaGroups.find(item => item.id === groupId); return current ? { ...current, groupId, valueId: group?.values[0]?.id ?? '' } : current })} onValueChange={valueId => setVariantMediaModal(current => current ? { ...current, valueId } : current)} onApply={applyVariantMediaSelection} onClose={() => setVariantMediaModal(null)} />}
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
