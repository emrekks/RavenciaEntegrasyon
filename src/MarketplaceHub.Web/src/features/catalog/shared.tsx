// Code the catalog routes share (product list, product editor, catalog lists, imports): types, helpers and windows.
// Each route lives in its own module so it loads as its own chunk.
import { type ReactNode } from 'react'
import { Link } from 'react-router'
import { UiIcon } from '../../shared/components'
import './publication-status-tooltip.css'
import './product-import.css'
import './variant-header-menu.css'
import './publish-platform-issues.css'
import './products-responsive.css'
import './catalog-polish.css'
import './products-sade.css'
import { RrImagePreview } from '../marketplace/returns-ui'
import { type VariantAttributeAssignment } from './variant-attribute-assignments'
import { mergeVariantOptionEntries, parseVariantOptionSignature } from './variant-option-matching'
import { productPublicationChannelMode } from './product-publication-channels'
import { formatPanelColorValue } from '../marketplace/color-value-format'

export type Versioned = { id: string; version: number }
export type Category = Versioned & { name: string; path: string; depth: number; isLeaf: boolean; isActive: boolean }
export type Brand = Versioned & { name: string; isActive: boolean }
export type Attribute = Versioned & { code: string; name: string; dataType: string; values: Array<{ id: string; value: string }>; roles?: string[] | null }
export type ProductAttributeAssignment = VariantAttributeAssignment
export type Variant = Versioned & {
  sku: string; barcode: string | null; modelCode: string | null; optionSignature: string; status: string
  weight: number | null; width: number | null; height: number | null; length: number | null; desi: number | null; costPrice: number | null
  defaultListPrice?: number | null; defaultSalePrice?: number | null
  onHand: number; available: number; inventoryVersion: number | null
  productId?: string | null
  offerId: string | null; listPrice: number | null; salePrice: number | null; currency: string | null; offerStatus: string | null
  priceVersion: number | null; offerVersion: number | null; vatRate: number | null; vatInclusion: string | null; roundingMode: string | null; safetyStock: number | null
  mediaUrls?: string[]; options?: Record<string, string>; platformStatuses?: VariantPlatformStatus[]; attributes?: ProductAttributeAssignment[]
}
// missingVariantCount: variants the marketplace no longer knows (link NOT_FOUND or REJECTED), not in matchedVariantCount.
export type ProductPlatformStatus = { platform: string; platformCode?: string; status: string; matchedVariantCount?: number; variantCount?: number; isChecking?: boolean; missingVariantCount?: number }
export type VariantPlatformStatus = {
  platform: string; platformCode: string; isLinked: boolean; status?: string
  // Present at the marketplace (a variant link or a LIVE listing); isLinked also counts a variant only queued for
  // publication there.
  isMatched?: boolean
  // The marketplace no longer knows the variant (its link is NOT_FOUND or REJECTED): neither matched nor live there.
  isMissing?: boolean
  connectionId?: string | null; offerId?: string | null; listPrice?: number | null; salePrice?: number | null
  currency?: string | null; vatRate?: number | null; vatInclusion?: string | null; roundingMode?: string | null
  safetyStock?: number | null; offerVersion?: number | null
}
export type Product = Versioned & {
  title: string; description: string; brandId: string | null; categoryId: string | null; status: string; updatedAt: string
  defaultListPrice?: number | null; defaultSalePrice?: number | null; warrantyMonths?: number | null
  // The card's KDV Oranı and KDV Dahil mi ("INCLUDED"/"EXCLUDED"); null = not set on the card. A new marketplace offer
  // (bulk publish) takes its VAT from here.
  vatRate?: number | null; vatInclusion?: string | null
  // Trendyol Web Color chosen by hand (panel colour value id); null = automatic from each variant's Renk.
  trendyolWebColorValueId?: string | null
  familyProductIds?: string[] | null
  categoryPath?: string | null; variants: Variant[]; primaryImageUrl: string | null; totalStock: number; startingPrice: number | null; currency: string; modelCode: string | null; activePlatforms: string[] | null; familyMediaUrls?: string[]
  familyMediaItems?: Array<{ url: string; mediaIds: string[]; sourceProductTitles: string[] }>; familyOrderedMediaUrls?: string[]; hasCustomMediaOrder?: boolean
  platformStatuses?: ProductPlatformStatus[]
  attributes?: ProductAttributeAssignment[]
  options?: Array<{ id: string; label: string; values: Array<{ id: string; label: string }> }>
  mediaUrls?: string[]
}
export function variantColorKey(variant: Variant) {
  const color = preferredColorOption(variantOptionEntries(variant))
  return color?.value.trim().toLocaleLowerCase('tr-TR') || null
}
export function compareLabelsAlphabetically(left: string, right: string) {
  return left.trim().localeCompare(right.trim(), 'tr-TR', { sensitivity: 'base', numeric: true })
}
export function compareOptionValues(attributeName: string, left: string, right: string) {
  const leftRank = optionValueSortRank(attributeName, left)
  const rightRank = optionValueSortRank(attributeName, right)
  return leftRank.bucket - rightRank.bucket
    || leftRank.primary - rightRank.primary
    || leftRank.secondary - rightRank.secondary
    || leftRank.text.localeCompare(rightRank.text, 'tr-TR', { numeric: true, sensitivity: 'base' })
}
export function compareVariantOptionSignatures(left: string, right: string) {
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
export type MarketplaceConnection = { id: string; platformCode: string; displayName: string; externalStoreId: string; status: string; externalWritesEnabled?: boolean }
export type AcceptedJob = { jobId: string }
export const productPublicationMode = (item: MarketplaceConnection) => productPublicationChannelMode(item.platformCode, item.status, item.externalWritesEnabled)
export const isProductPublicationConnection = (item: MarketplaceConnection) => productPublicationMode(item) === 'PUBLISH'
export function ImageLightboxModal({ image, onClose }: { image: { url: string; title: string }; onClose: () => void }) {
  return <RrImagePreview title={image.title} sources={[image.url]} onClose={onClose} />
}
export type ParsedVariantOption = { name: string; value: string }
export function normalizeVariantOptionName(name: string) {
  return name.replace(/[\s_-]+/g, '').toLocaleUpperCase('tr-TR')
}
export function isWebColorOptionName(name: string) {
  return ['WEBCOLOR', 'WEBCOLOUR', 'WEBRENK'].includes(normalizeVariantOptionName(name))
}
export function isColorOptionName(name: string) {
  return ['RENK', 'RENKLER', 'COLOR', 'COLORS', 'COLOUR', 'COLOURS', 'WEBCOLOR', 'WEBCOLOUR', 'WEBRENK'].includes(normalizeVariantOptionName(name))
}
export function formatCatalogOptionLabel(value: string) {
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
export function preferredColorOption(options: ParsedVariantOption[]) {
  return options.find(option => isColorOptionName(option.name) && !isWebColorOptionName(option.name))
    ?? options.find(option => isColorOptionName(option.name))
}
export function variantOptionEntries(variant: Pick<Variant, 'optionSignature' | 'options'>) {
  const options = Object.entries(variant.options ?? {}).map(([name, value]) => ({ name, value: cleanOptionValue(value) }))
  return mergeVariantOptionEntries(options, parseVariantOptionSignature(variant.optionSignature ?? ''))
}
export function cleanOptionValue(value: string) {
  return value.replace(/^["“”]+|["“”]+$/g, '').trim()
}
export const standardSizeOrder = new Map([
  ['XXXS', 0], ['3XS', 0], ['XXS', 1], ['2XS', 1], ['XS', 2], ['S', 3], ['M', 4], ['L', 5], ['XL', 6],
  ['2XL', 7], ['3XL', 8], ['4XL', 9], ['5XL', 10], ['6XL', 11], ['7XL', 12], ['8XL', 13], ['9XL', 14]
])
export function optionValueSortRank(attributeName: string, rawValue: string) {
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
export function isVariantOptionName(name: string) {
  const normalized = normalizeVariantOptionName(name)
  return ['RENK', 'COLOR', 'COLOUR', 'WEBCOLOR', 'WEBCOLOUR', 'WEBRENK', 'BEDEN', 'SIZE', 'SIZ', 'NUMARA', 'NUMBER'].includes(normalized)
}
export const Tag = ({ children }: { children: ReactNode }) => <span className="tag">{children}</span>
export function Page({ title, eyebrow, meta, action, className, children }: { title: string; eyebrow: string; meta?: ReactNode; action?: ReactNode; className?: string; children: ReactNode }) { const productBack = className?.includes('product-add-page'); return <section className={`content stitch-page ${className ?? ''}`}><div className={`page-heading rv-page-head${productBack ? ' product-page-heading' : ''}`}><div className={productBack ? 'product-page-heading-copy' : undefined}>{productBack && <Link className="product-heading-back" to="/products" aria-label="Ürünler listesine dön"><UiIcon name="arrowLeft" /><span>Ürünler</span></Link>}<div className={productBack ? 'product-page-heading-title' : undefined}><p className="eyebrow">{eyebrow}</p><h1 title={title}>{title}</h1>{meta}</div></div>{action}</div>{children}</section> }
