// Ürün ekleme ve düzenleme (/products/new, /products/:id).
import { useCallback, useEffect, useEffectEvent, useLayoutEffect, useMemo, useRef, useState, type FormEvent, type PointerEvent as ReactPointerEvent, type ReactNode } from 'react'
import './product-editor-design-system.css'
import { Link, useLocation, useNavigate, useParams } from 'react-router'
import { createPortal } from 'react-dom'
import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiRequestError, hubApi, loadAllPages } from '../../../shared/api'
import { UiIcon, usePromptDialog, type UiIconName, DialogSurface, UnsavedChangesGuard } from '../../../shared/components'
import { newIdempotencyKey, sendWithVersionedIdempotencyKey } from '../../../shared/idempotency'
import { httpsUrlProblem } from '../../../shared/safe-url'
import { ErrorToast, NoticeToast } from '../../../shared/components/NoticeToast'
import { useMediaQuery } from '../../../shared/hooks/useMediaQuery'
import { sanitizeRichText } from '../../../shared/security/sanitizeHtml'
import { revealFieldError } from './field-error-reveal'
import { platformLogoClass, platformLogoSource } from '../../../shared/platform-logos'
import { appendNotification, notificationError, productNotificationSubject } from '../../../shared/notifications'
import { toggleProductAttributeValue } from '../attribute-selection'
import { buildVariantAttributeAssignments } from '../variant-attribute-assignments'
import { filterAttributeOptionValues } from '../attribute-value-search'
import { isStoredProductMediaUrl, mediaImageKey, mediaRefsEqual, mediaRefsSameSet, mediaUrlsInPreferredOrder, modelCodeForExistingVariant, publicProductMediaUrls, reorderItems, reorderMediaUrls, uniqueMediaUrls } from '../product-media-editor'
import { applyVariantBulkEditValue, variantBulkEditIssue, type VariantBulkEditField } from '../variant-bulk-edit'
import { applyGeneratedVariantCodes, buildSequentialVariantCodes, buildVariantGenerationDefaults, resolveVariantSyncAttributeIds } from '../variant-generation'
import { filterVariantsByOptions, selectVariantDraftsByKeys, type VariantOptionFilterSelections } from '../variant-filtering'
import { displayVariantOptionEntriesFromSignature, formatColorOptionValue, joinVariantOptionLabels, matchingVariantOptionValues, mergeVariantOptionValues, normalizeVariantOptionValue, parseVariantOptionSignature } from '../variant-option-matching'
import { classifyPublicationAttributeIssues, fixableValueMappings, type PublicationAttributeIssues, type PublicationAttributeSelection, type PublicationMappingReference, type PublicationValueReferenceSet } from '../publication-attribute-readiness'
import { MissingValueMappingDialog } from '../MissingValueMappingDialog'
import { LocalImagePreview, MediaMoveButtons, MediaZoomButton } from './media-tiles'
import { mapWithConcurrency, mediaSaveProgressMessage, storedProductMediaUrl, variantMediaReconcilePlan } from './product-media-save'
import { MarketplaceCodesDialog } from './MarketplaceCodesDialog'
import { PublicationJobDialog } from './PublicationJobDialog'
import { PlatformUpdateDialog } from './PlatformUpdateDialog'
import { isProductDeliveryDays, productDeliveryDays } from './product-delivery-days'
import { platformUpdatePlan, priceStockPlatformUpdateFields, readPlatformUpdatePreferences, writePlatformUpdatePreferences, type PlatformUpdateField, type PlatformUpdatePreferences } from './platform-update-fields'
import { publishCardVariantLinks, publishCardVariantPresence } from './publish-card-variants'
import { VariantOptionEditDialog, type VariantOptionEditChange } from './VariantOptionEditDialog'
import { VariantMediaBulkSelect } from './VariantMediaBulkSelect'
import { describePlatformCodeIssues, platformCodeIssues, platformCodeNames } from '../platform-code-rules'
import { PublicationReadinessSourceError, publicationReadinessFailureDetail, readPublicationReadinessSource } from '../publication-readiness-error'
import { productMediaUrlIssue } from '../product-media-url'
import { barcodeClipboardIssue, parseBarcodeClipboardValues } from '../product-barcode-paste'
import { productCopyIdentifierConflicts } from '../product-copy-identifiers'
import { copyRemovedMediaKeys, historyStateWithCopyRemovedMedia, withCopyRemovedMedia, withoutCopyRemovedMedia, withoutCopyRemovedMediaRefs } from '../product-copy-media-removals'
import { sameProductSnapshotIgnoringVersion } from '../product-save-version'
import { panelPrices } from '../quick-price-edit'
import { editorDataStamp, editorFieldErrors, editorSeedKey, editorSnapshot, editorStockUpdates, editorVariantCostUpdate, editorVariantDesiUpdate, editorVat, editorVatRateIssue, TURKISH_VAT_RATES, type EditorFieldErrors } from '../product-editor-state'
import { applyVariantStockSets, setVariantOnHand } from '../variant-stock-set'
import { isPttAvmChecking, isPttAvmFamilyRetryable, isPublicationLive, isPublicationSelectionDisabled, isPublicationStatusJobRunning, isSkippedByPublish, publicationStatusLabel, publicationStatusNote, publicationStatusTone, pttAvmRetryLabel, shouldCheckPublicationAttributes } from '../publication-status'
import { quickPlatformUpdateTargets } from '../platform-update-targets'
import { publicationJobPollInterval } from '../publication-job-poll'
import { isRefreshJobRunning, publicationJobInline } from '../publication-job-inline'
import { statusLabel } from '../../../shared/status-labels'
import { familyCardsInPanelOrder } from '../family-card-order'
import { productPublicationTargets, productSaveFeedback,publicationCancelMessage, publicationRefreshMessage, type MarketplacePublishOutcome, type MarketplaceUpdateOutcome } from '../product-publication-submit'
import { readVariantMediaAssignmentDraft, updateVariantMediaAssignmentDraft, variantMediaAssignmentKey, variantMediaReconcileRequests, type VariantMediaAssignmentDrafts } from '../variant-media-assignments'
import { useOperationFeedbackToasts, type OperationFeedback } from '../operation-feedback-toast'
import { focusOnMount } from '../../../shared/focus'
import { channelPricingInputs, copiedChannelPricing, emptyChannelPricingDraft, type ChannelPricingDraft } from './marketplace-channel-pricing'
import { AcceptedJob, Attribute, Brand, Category, ImageLightboxModal, MarketplaceConnection, Page, Product, ProductAttributeAssignment, Variant, VariantPlatformStatus, cleanOptionValue, compareLabelsAlphabetically, compareVariantOptionSignatures, formatCatalogOptionLabel, isColorOptionName, isProductPublicationConnection, isVariantOptionName, isWebColorOptionName, normalizeVariantOptionName, optionValueSortRank, productPublicationMode, variantColorKey, variantOptionEntries } from '../shared'
import { insideSelector, useDismissable } from '../../../shared/hooks/useDismissable'

// The save's slot in the toast column: "Ürün Kaydediliyor" turns into its result in place instead of stacking.
const productSaveToastKey = 'product-editor-save'
// Channel prices are separate rows per variant and store; this many are written at once.
const channelOfferWriteConcurrency = 6

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
function sortVariantsAlphabetically<T extends { optionSignature?: string | null; sku: string }>(variants: T[]) {
  return [...variants].sort((left, right) => {
    const leftLabel = (left.optionSignature || left.sku).trim()
    const rightLabel = (right.optionSignature || right.sku).trim()
    return compareVariantOptionSignatures(leftLabel, rightLabel)
      || compareLabelsAlphabetically(left.sku, right.sku)
  })
}
type ProductUpdateRequest = { connectionIds: string[]; fields: PlatformUpdateField[] }
type PublicationStatus = { productId: string; connectionId: string; profileId: string | null; desiredStatus: string | null; actualStatus: string | null; lastRejectionCode: string | null; lastJobId: string | null; lastJobStatus: string | null; lines: Array<{ variantId: string; sku: string; barcode: string | null; desiredStatus: string; actualStatus: string; rejectionCode: string | null }> }
type PublicationJobDetail = { job: { id: string; jobType?: string; status: string; progressCurrent: number; progressTotal: number | null; progressPercent: number | null; progressLabel: string | null; lastErrorCode: string | null; lastErrorSummary: string | null } }
const isProductUpdateConnection = (item: MarketplaceConnection) => ['TRENDYOL', 'HEPSIBURADA', 'SHOPIFY', 'PAZARAMA', 'N11', 'PTTAVM'].includes(item.platformCode.trim().toUpperCase()) && ['ACTIVE', 'VERIFIED'].includes(item.status.trim().toUpperCase())
const isProductPublicationChannel = (item: MarketplaceConnection) => productPublicationMode(item) !== null
const productUpdateCapabilities = (platformCode: string) => {
  const code = platformCode.trim().toUpperCase()
  if (code === 'TRENDYOL') return { writable: true, content: true, categoryAttributes: true, detail: 'Ürün bilgileri, fiyat ve stok güncellenebilir.' }
  if (code === 'HEPSIBURADA') return { writable: true, content: true, categoryAttributes: true, detail: 'Ürün bilgileri, fiyat ve stok Hepsiburada yazma kurallarıyla güncellenebilir.' }
  if (code === 'SHOPIFY') return { writable: true, content: true, categoryAttributes: false, detail: 'Ürün kartı tek istekte güncellenir: ürün adı, açıklama, görseller, renk ve beden, fiyat ve stok birlikte gider.' }
  if (code === 'PAZARAMA') return { writable: true, content: false, categoryAttributes: false, detail: 'KDV oranı, fiyat ve stok güncellenebilir; ürün içeriği yalnız Pazarama panelinden değişir.' }
  if (code === 'N11') return { writable: true, content: true, categoryAttributes: true, detail: 'Açıklama, kategori özellikleri, KDV oranı, teslimat süresi, fiyat ve stok güncellenebilir; ürün adı, görseller ve marka N11 API ile değişmez.' }
  if (code === 'PTTAVM') return { writable: true, content: true, categoryAttributes: false, detail: 'Seçilen ürün bilgileri tek istekte gider; PTT AVM’deki tüm varyantlar olduğu gibi eklenir. Fiyat ve stok birlikte gider.' }
  return { writable: false, content: false, categoryAttributes: false, detail: 'Bu platform için ürün güncelleme desteği bulunmuyor.' }
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
type ProductMediaOption = { value: string; label: string; url?: string; file?: File }
type ProductFamilyMediaItem = NonNullable<Product['familyMediaItems']>[number]
type MediaRemovalConfirmation = { kind: 'product'; url: string } | { kind: 'family'; item: ProductFamilyMediaItem }
type PendingProductMediaFile = { id: string; file: File }
type VariantMediaGroup = { id: string; name: string; values: Array<{ id: string; value: string }>; attributeId?: string }
type VariantMediaModalState = { mode: 'variant'; rowKey: string; draftRefs: string[] } | { mode: 'bulk'; draftsBySelection: VariantMediaAssignmentDrafts; groupId: string; valueId: string }
type VariantFilterSelections = Record<string, string[]>
function isSizeOptionName(name: string) {
  return ['BEDEN', 'BEDENLER', 'SIZE', 'SIZES', 'BOYUT', 'BOYUTLAR', 'NUMARA', 'NUMARALAR', 'SHOESIZE', 'AYAKKABINUMARASI'].includes(normalizeVariantOptionName(name))
}
function displayVariantOptionSignature(signature: string, savedOptions: Record<string, string> = {}) {
  const options = displayVariantOptionEntriesFromSignature(signature, savedOptions)
  if (!options.length) return signature.trim().toLocaleLowerCase('tr-TR') === 'tek ürün' ? signature : '—'
  return joinVariantOptionLabels(options.map(({ name, value }) => `${formatCatalogOptionLabel(name)}: ${isSizeOptionName(name) ? value.toLocaleUpperCase('tr-TR') : formatCatalogOptionLabel(value)}`))
}
function optionSignatureFromOptions(options: Record<string, string>) {
  return Object.entries(options)
    .filter(([, value]) => value.trim())
    .map(([name, value]) => `${name}:${cleanOptionValue(value)}`)
    .join('_')
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
  assignedValueIds,
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
  assignedValueIds?: ReadonlySet<string>
  onRefsChange: (values: string[]) => void
  onGroupChange?: (value: string) => void
  onValueChange?: (value: string) => void
  onApply: () => void
  onClose: () => void
}) {
  const selectedGroup = groups?.find(group => group.id === selectedGroupId)
  const selectedValue = selectedGroup?.values.find(value => value.id === selectedValueId)
  return <div className="workspace-modal-backdrop variant-media-picker-backdrop" role="presentation" onMouseDown={onClose}>
    <DialogSurface as="section" onClose={onClose} className={`workspace-modal variant-media-picker-modal${mode === 'bulk' ? ' is-bulk' : ''}`} aria-labelledby="variant-media-picker-title" onMouseDown={event => event.stopPropagation()}>
      <header>
        <div><p className="eyebrow">Varyant Görselleri</p><h2 id="variant-media-picker-title">{mode === 'bulk' ? 'Seçeneklere Görsel Ata' : 'Varyant Görsellerini Seç'}</h2><p>{mode === 'bulk' ? 'Bir seçenek değerine görselleri atayın. Diğer değerlere geçince seçimleriniz korunur; eşlemeler tek seferde kaydedilir.' : 'Ürün görsellerinden bu varyanta ait birden fazla görsel seçin. Sıra, ürün panelindeki görsel sırasına göre kaydedilir.'}</p></div>
        <button type="button" className="modal-close" onClick={onClose} aria-label="Pencereyi kapat"><UiIcon name="close" /></button>
      </header>
      {mode === 'bulk' && groups?.length ? <><div className="variant-media-bulk-fields"><VariantMediaBulkSelect label="Seçenek Grubu" placeholder="Seçenek Grubu Seçin" value={selectedGroupId ?? ''} options={groups.map(group => ({ id: group.id, label: group.name }))} onChange={value => onGroupChange?.(value)} /><VariantMediaBulkSelect label="Seçenek Değeri" placeholder="Değer Seçin" value={selectedValueId ?? ''} options={selectedGroup?.values.map(value => ({ id: value.id, label: value.value, assigned: assignedValueIds?.has(value.id) })) ?? []} disabled={!selectedGroup} onChange={value => onValueChange?.(value)} /></div>{selectedValue && <div className={`variant-media-bulk-match-summary ${matchedVariantCount ? '' : 'is-empty'}`} role="status"><strong>{selectedGroup?.name}: {selectedValue.value}</strong><span>{matchedVariantCount ? `${matchedVariantCount} varyant eşleşti` : 'Bu değerle eşleşen varyant bulunamadı'}</span></div>}</> : null}
      <div className="variant-media-picker-grid">
        {options.length ? options.map(option => <MediaOptionThumb key={option.value} option={option} selected={selectedRefs.includes(option.value)} onClick={() => {
           const next = selectedRefs.includes(option.value)
             ? selectedRefs.filter(value => value !== option.value)
             : [...selectedRefs, option.value]
           onRefsChange(options.filter(item => next.includes(item.value)).map(item => item.value))
         }} />) : <div className="empty small"><strong>Seçilebilir görsel yok</strong><p>Önce ürün görsellerine HTTPS linki veya dosya ekleyin.</p></div>}
      </div>
      <footer><button type="button" className="secondary" onClick={() => onRefsChange([])}>{mode === 'bulk' ? 'Bu Değerin Görsellerini Kaldır' : 'Görselleri Kaldır'}</button><button type="button" className="secondary" onClick={onClose}>Vazgeç</button><button type="button" onClick={onApply} disabled={!options.length || (mode === 'bulk' && (!selectedGroupId || !selectedValueId || !matchedGroupVariantCount))}>{mode === 'bulk' ? 'Tüm Eşlemeleri Kaydet' : 'Görselleri Kaydet'}</button></footer>
    </DialogSurface>
  </div>
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
  // Empty (null) until a cost is entered: an empty cost is "not known", never 0.
  costPrice: number | null
  // The cost this row was loaded with (edit page only): the cost is saved only when the row differs from it, so an
  // untouched row never writes over a cost set elsewhere meanwhile (ürün içe aktarma).
  loadedCostPrice?: number | null
  mediaRefs: string[]
  // The on-hand stock this row was loaded with (edit page only): stock is saved only when the row differs from it,
  // as an absolute value against it.
  loadedStock?: number
  persistedAttributes?: ProductAttributeAssignment[]
  platformStatuses?: VariantPlatformStatus[]
}
// A message the server attached to one field of a refused save (422 fieldErrors), shown under that field.
function EditorFieldError({ message }: { message?: string }) {
  return message ? <small className="product-field-error" role="alert">{message}</small> : null
}
const variantBulkEditLabels: Record<VariantBulkEditField, string> = { stock: 'Stok', salePrice: 'Fiyat', costPrice: 'Maliyet', listPrice: 'Liste Fiyatı' }
type ProductAttributePayload = ProductAttributeAssignment
function marketplacePlatformName(platform: VariantPlatformStatus) {
  const code = platform.platformCode.trim().toLocaleUpperCase('tr-TR')
  return code === 'SHOPIFY' ? 'Shopify' : code === 'HEPSIBURADA' ? 'Hepsiburada' : code === 'TRENDYOL' ? 'Trendyol' : code === 'N11' ? 'N11' : code === 'PAZARAMA' ? 'Pazarama' : code === 'PTTAVM' ? 'PTT AVM' : platform.platform
}
const pricingPlatformOrder = ['TRENDYOL', 'HEPSIBURADA', 'N11', 'PAZARAMA', 'PTTAVM', 'SHOPIFY']
const pricingPlatformRank = (platformCode: string) => { const index = pricingPlatformOrder.indexOf(platformCode.trim().toUpperCase()); return index < 0 ? pricingPlatformOrder.length : index }
function BulkVariantPlatformPricingModal({ row, rows, platforms: unorderedPlatforms, savedDrafts, productName, modelCode, filterGroups, matchesFilterValue, saving, onClose, onSave }: { row: VariantDraft; rows: VariantDraft[]; platforms: VariantPlatformStatus[]; savedDrafts: Record<string, ChannelPricingDraft>; productName: string; modelCode: string; filterGroups: VariantMediaGroup[]; matchesFilterValue: (row: VariantDraft, group: VariantMediaGroup, value: { id: string; value: string }) => boolean; saving: boolean; onClose: () => void; onSave: (drafts: Record<string, ChannelPricingDraft>) => void }) {
  // The columns follow the marketplaces in the panel's order (Trendyol, Hepsiburada, n11, Pazarama, PTT AVM, Shopify).
  const platforms = [...unorderedPlatforms].sort((left, right) => pricingPlatformRank(left.platformCode) - pricingPlatformRank(right.platformCode))
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
  const resetMatrixDrafts = useEffectEvent(() => { setMatrixDrafts(initialMatrixDrafts()); setDirtyMatrixDraftKeys([]) })
  useEffect(() => resetMatrixDrafts(), [row.key, rows, platforms, savedDrafts])
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
    if (!matchingRows.length) {
      setMatrixBulkEdit(current => current ? { ...current, error: 'Seçili renk ve bedenlerle eşleşen varyant bulunamadı.' } : current)
      return
    }
    // List and sale price are compared when the matrix is saved, so either one can be filled first.
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
    const field = label === 'Liste Fiyatı' ? 'listPrice' : 'salePrice'
    return <div className="variant-platform-pricing-matrix-cell variant-platform-pricing-matrix-head" key={`${key}:${label}`}><strong>{marketplacePlatformName(platform).toLocaleUpperCase('tr-TR')}</strong><span className="variant-platform-pricing-matrix-head-label"><span>{label.toLocaleUpperCase('tr-TR')}</span><button type="button" className="variant-platform-pricing-bulk-trigger" aria-label={`${marketplacePlatformName(platform)} ${label} için filtreye uyan varyantlarda toplu düzenleme aç`} title={`${marketplacePlatformName(platform)} ${label.toLocaleLowerCase('tr-TR')} toplu değiştir`} aria-haspopup="dialog" aria-expanded={matrixBulkEdit?.platform === platform && matrixBulkEdit.field === field} onClick={() => openMatrixBulkEdit(platform, field, label)} disabled={saving || !platform.connectionId}><UiIcon name="edit" size={13} /></button></span></div>
  }
  return <>
    <div className="workspace-modal-backdrop variant-platform-pricing-backdrop" role="presentation" onMouseDown={() => !saving && onClose()}>
      <DialogSurface as="section" onClose={() => !saving && onClose()} className="workspace-modal variant-platform-pricing-modal" aria-label="Platform fiyatları" onMouseDown={event => event.stopPropagation()}>
        <header><div><p className="eyebrow">Platform Fiyatları</p><p className="variant-platform-pricing-product-context"><strong>{productName || 'Ürün adı belirtilmemiş'}</strong><span>Model kodu: {modelCode || '—'}</span></p></div><button type="button" className="modal-close" onClick={onClose} disabled={saving} aria-label="Fiyat penceresini kapat"><UiIcon name="close" /></button></header>
        <div className="variant-platform-pricing-body">
          <section className="variant-platform-pricing-matrix" aria-label="Platformlara göre varyant fiyat matrisi">
            <div className="variant-platform-pricing-bulk-heading"><strong>Platform Fiyatlarını Toplu Düzenle</strong><span>{matchingRows.length} / {rows.length} varyant eşleşti · Panel ana fiyatı değişmez</span></div>
            {filterGroups.length > 0 && <div className="variant-platform-pricing-filter-grid" aria-label="Platform fiyatı renk ve beden filtreleri">{filterGroups.map(group => <VariantFilterDropdown key={group.id} group={group} selectedValueIds={filterSelections[group.id] ?? []} onToggle={valueId => setFilterSelections(current => ({ ...current, [group.id]: (current[group.id] ?? []).includes(valueId) ? current[group.id].filter(id => id !== valueId) : [...(current[group.id] ?? []), valueId] }))} onClear={() => setFilterSelections(current => ({ ...current, [group.id]: [] }))} />)}</div>}
            <div className="variant-platform-pricing-matrix-scroll">
              <div className="variant-platform-pricing-matrix-grid" style={{ gridTemplateColumns: `220px repeat(${platforms.length * 2}, 128px)` }}>
                <div className="variant-platform-pricing-matrix-cell variant-platform-pricing-matrix-corner"><strong>Varyant</strong><small>Model / barkod</small></div>
                {platforms.flatMap(platform => [renderMatrixHeader(platform, 'Liste Fiyatı'), renderMatrixHeader(platform, 'Satış Fiyatı')])}
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
      </DialogSurface>
    </div>
    {matrixBulkEdit && createPortal(
      <div className="workspace-modal-backdrop variant-platform-pricing-bulk-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) setMatrixBulkEdit(null) }}>
        <DialogSurface as="section" onClose={() => { setMatrixBulkEdit(null) }} className="workspace-modal variant-bulk-edit-modal" aria-labelledby="variant-platform-pricing-bulk-title" onMouseDown={event => event.stopPropagation()}>
          <header><div><p className="eyebrow">Platform Fiyatları</p><h2 id="variant-platform-pricing-bulk-title">{marketplacePlatformName(matrixBulkEdit.platform)} {matrixBulkEdit.label} Toplu Değiştir</h2><p>Değer seçili renk ve bedenlerle eşleşen {matchingRows.length} varyantın bu platformdaki {matrixBulkEdit.label.toLocaleLowerCase('tr-TR')} alanına uygulanır.</p></div><button type="button" className="modal-close" onClick={() => setMatrixBulkEdit(null)} aria-label="Toplu fiyat değişikliği penceresini kapat"><UiIcon name="close" /></button></header>
          <div className="variant-bulk-edit-body"><p className="variant-bulk-edit-target"><span>Uygulanacak Varyant</span><strong>{matchingRows.length.toLocaleString('tr-TR')} satır</strong></p><p className="variant-bulk-edit-scope">Uygula, yalnızca seçilen renk ve bedenlerin bu platformdaki taslaklarını değiştirir. Kalıcı olması için ardından ana penceredeki Kaydet düğmesine basın.</p><label htmlFor="variant-platform-pricing-bulk-value">Yeni Fiyat<input id="variant-platform-pricing-bulk-value" data-autofocus={touchScreen ? undefined : true} type="number" min="0" step="0.01" value={matrixBulkEdit.value} aria-describedby={matrixBulkEdit.error ? 'variant-platform-pricing-bulk-error' : undefined} onChange={event => setMatrixBulkEdit(current => current ? { ...current, value: event.target.value, error: '' } : current)} /></label>{matrixBulkEdit.error && <p className="variant-bulk-edit-error" id="variant-platform-pricing-bulk-error" role="alert">{matrixBulkEdit.error}</p>}</div>
          <footer><button type="button" className="secondary" onClick={() => setMatrixBulkEdit(null)}>Vazgeç</button><button type="button" onClick={applyMatrixBulkEdit} disabled={!matchingRows.length}>Uygula</button></footer>
        </DialogSurface>
      </div>,
      document.querySelector('main') ?? document.body
    )}
  </>
}
// API, inventory and publication safeguards retain the actual 1000-line limit.
// The product workspace intentionally does not display an arbitrary UI quota.
const MAX_VARIANTS = 1000
const MAX_PRODUCT_MEDIA_BYTES = 6 * 1024 * 1024
function RichTextTool({ icon, label, onClick, disabled = false }: { icon: UiIconName; label: string; onClick: () => void; disabled?: boolean }) {
  return <button type="button" className="rich-text-tool" title={label} aria-label={label} onClick={onClick} disabled={disabled}><span className="rich-text-tool-icon" aria-hidden="true"><UiIcon name={icon} /></span><span className="rich-text-tool-label">{label}</span></button>
}
function RichTextEditor({ value, onChange }: { value: string; onChange: (value: string) => void }) {
  const [promptAddress, addressDialog] = usePromptDialog()
  const visualEditor = useRef<HTMLDivElement>(null)
  const lastValue = useRef(value)
  // The canvas gets its HTML once; later changes are written by the effects below. A new HTML string on every
  // keystroke made React rewrite the canvas and threw the caret back to the start (Enter on the last line).
  const [initialVisualHtml] = useState(() => sanitizeRichText(value))
  const [htmlMode, setHtmlMode] = useState(false)
  const plainTextLength = value.replace(/<[^>]*>/g, '').trim().length

  useEffect(() => {
    if (!htmlMode && visualEditor.current && lastValue.current !== value) visualEditor.current.innerHTML = sanitizeRichText(value)
    lastValue.current = value
  }, [htmlMode, value])

  // Back from HTML mode the visual editor shows the current text again.
  const showVisualValue = useEffectEvent(() => { if (!htmlMode && visualEditor.current) visualEditor.current.innerHTML = sanitizeRichText(value) })
  useEffect(() => showVisualValue(), [htmlMode])

  // Typing only reports the cleaned value; the canvas is rewritten only after a toolbar command, never mid-typing.
  function syncVisualValue(rewrite = false) {
    const next = sanitizeRichText(visualEditor.current?.innerHTML ?? '')
    if (rewrite && visualEditor.current && visualEditor.current.innerHTML !== next) visualEditor.current.innerHTML = next
    lastValue.current = next
    onChange(next === '<br>' ? '' : next)
  }

  function runCommand(command: string, argument?: string) {
    if (htmlMode || !visualEditor.current) return
    visualEditor.current.focus()
    document.execCommand(command, false, argument)
    syncVisualValue(true)
  }

  // The address window takes focus away from the editor: the selection is kept and put back before the command runs.
  function editorSelection() {
    const selection = window.getSelection()
    return selection && selection.rangeCount && visualEditor.current?.contains(selection.anchorNode) ? selection.getRangeAt(0).cloneRange() : null
  }
  async function insertAddress(kind: 'link' | 'image') {
    if (htmlMode || !visualEditor.current) return
    const range = editorSelection()
    const url = await promptAddress(kind === 'link'
      ? { title: 'Bağlantı Ekle', label: 'Bağlantı Adresi', initialValue: 'https://', inputType: 'url', confirmLabel: 'Ekle', validate: httpsUrlProblem }
      : { title: 'Görsel Ekle', label: 'Görsel Adresi', initialValue: 'https://', inputType: 'url', confirmLabel: 'Ekle', validate: httpsUrlProblem })
    if (!url || !visualEditor.current) return
    visualEditor.current.focus()
    if (range) { const selection = window.getSelection(); selection?.removeAllRanges(); selection?.addRange(range) }
    runCommand(kind === 'link' ? 'createLink' : 'insertImage', url)
  }
  const insertLink = () => void insertAddress('link')
  const insertImage = () => void insertAddress('image')

  function clearFormatting() {
    runCommand('removeFormat')
    runCommand('formatBlock', 'p')
  }

  return <div className="rich-text-editor rich-text-editor-pro">{addressDialog}
    <div className="rich-text-editor-head">
      <div><strong>Ürün Açıklaması</strong><span className="rich-text-mode-note">{htmlMode ? 'HTML kodu düzenleniyor' : 'Görsel düzenleyici'}</span></div>
      <div className="rich-text-editor-meta"><span>{plainTextLength} karakter</span><div className="rich-text-mode-switch" role="group" aria-label="Açıklama görünümü"><button type="button" className={!htmlMode ? 'active' : ''} aria-pressed={!htmlMode} onClick={() => setHtmlMode(false)}><UiIcon name="sparkle" /> Görsel</button><button type="button" className={htmlMode ? 'active' : ''} aria-pressed={htmlMode} onClick={() => setHtmlMode(true)}><UiIcon name="code" /> HTML</button></div></div>
    </div>
    <div className="rich-text-toolbar" aria-label="Açıklama biçimlendirme araçları">
      <div className="rich-text-tool-group" aria-label="Metin biçimi"><RichTextTool icon="bold" label="Kalın" onClick={() => runCommand('bold')} disabled={htmlMode} /><RichTextTool icon="italic" label="İtalik" onClick={() => runCommand('italic')} disabled={htmlMode} /><RichTextTool icon="underline" label="Altı Çizili" onClick={() => runCommand('underline')} disabled={htmlMode} /></div>
      <div className="rich-text-tool-group rich-text-alignment-group" aria-label="Paragraf hizası"><RichTextTool icon="alignLeft" label="Sola Hizala" onClick={() => runCommand('justifyLeft')} disabled={htmlMode} /><RichTextTool icon="alignCenter" label="Ortala" onClick={() => runCommand('justifyCenter')} disabled={htmlMode} /><RichTextTool icon="alignRight" label="Sağa Hizala" onClick={() => runCommand('justifyRight')} disabled={htmlMode} /></div>
      <div className="rich-text-tool-group" aria-label="İçerik ekle"><select aria-label="Yazı Boyutu" defaultValue="" disabled={htmlMode} onChange={event => { const map: Record<string, string> = { '12': '2', '15': '3', '19': '5' }; runCommand('fontSize', map[event.target.value] ?? '3'); event.currentTarget.value = '' }}><option value="" disabled>Yazı Boyutu</option><option value="12">Küçük</option><option value="15">Normal</option><option value="19">Büyük</option></select><RichTextTool icon="list" label="Madde Listesi" onClick={() => runCommand('insertUnorderedList')} disabled={htmlMode} /><RichTextTool icon="paragraph" label="Paragraf" onClick={() => runCommand('formatBlock', 'p')} disabled={htmlMode} /><RichTextTool icon="textColor" label="Metin Rengi" onClick={() => runCommand('foreColor', '#7652e8')} disabled={htmlMode} /><RichTextTool icon="link" label="Bağlantı Ekle" onClick={insertLink} disabled={htmlMode} /><RichTextTool icon="image" label="Görsel Ekle" onClick={insertImage} disabled={htmlMode} /></div>
      <div className="rich-text-tool-group" aria-label="Düzenleme"><RichTextTool icon="undo" label="Geri Al" onClick={() => runCommand('undo')} disabled={htmlMode} /><RichTextTool icon="redo" label="Yinele" onClick={() => runCommand('redo')} disabled={htmlMode} /><RichTextTool icon="clearFormatting" label="Biçimi Temizle" onClick={clearFormatting} disabled={htmlMode} /></div>
    </div>
    {htmlMode ? <textarea className="rich-text-html-editor" value={value} onChange={event => { lastValue.current = event.target.value; onChange(event.target.value) }} aria-label="Açıklama HTML kodu" placeholder="<p>Ürünün öne çıkan özelliklerini anlatın…</p>" spellCheck={false} /> : <div ref={visualEditor} className="rich-text-canvas" contentEditable role="textbox" aria-multiline="true" aria-label="Açıklama" data-placeholder="Ürünün öne çıkan özelliklerini anlatın…" suppressContentEditableWarning dangerouslySetInnerHTML={{ __html: initialVisualHtml }} onFocus={() => document.execCommand("defaultParagraphSeparator", false, "p")} onInput={() => syncVisualValue()} />}
  </div>
}
function buildVariantMatrix(requirements: CategoryRequirement[], variantAttributeIds: string[], selectedValueIds: Record<string, string[]>, baseSku: string, fallbackListPrice: number, fallbackSalePrice: number, fallbackCostPrice: number | null, initialStock: number, automaticBarcodes = false, modelCode = '') {
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

  useDismissable(open, [rootRef], () => setOpen(false))

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
        <input ref={touchScreen ? undefined : focusOnMount} value={search} onChange={event => setSearch(event.target.value)} placeholder="Değer ara..." aria-label={`${attributeName} değerlerinde ara`} />
      </div>
      {selected.length > 0 && <button type="button" className="category-attribute-clear-selection" onClick={clearSelection}>Seçimi Temizle</button>}
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

  useDismissable(open, [rootRef], () => setOpen(false))

  return <div className="variant-filter-field" ref={rootRef}>
    <button type="button" className={`variant-filter-select-trigger ${selected.length ? 'active' : ''}`} aria-haspopup="listbox" aria-expanded={open} onClick={() => setOpen(current => !current)}>
      <span><small>{selected.length ? `${selected.length} değer seçildi` : group.name}</small><strong>{summary}</strong></span>
      <UiIcon name="chevronDown" />
    </button>
    {open && <div className="variant-filter-dropdown-menu" role="listbox" aria-label={`${group.name} filtre değerleri`}>
      <div className="variant-filter-dropdown-tools">
        <input ref={touchScreen ? undefined : focusOnMount} value={search} onChange={event => setSearch(event.target.value)} placeholder="Değer ara..." aria-label={`${group.name} değerlerinde ara`} />
      </div>
      {selected.length > 0 && <button type="button" className="variant-filter-clear-selection" onClick={onClear}>Seçimi Temizle</button>}
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
        <h2>Kategori Özellikleri</h2>
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
              <small>Yalnız Trendyol · Zorunlu</small>
            </div>
          </div>
          <div className={`attribute-builder-web-color-mode${webColorAutoEnabled ? ' is-auto' : ' is-manual'}`}>
            <label className="attribute-builder-web-color-toggle">
              <input type="checkbox" checked={webColorAutoEnabled} onChange={event => onToggleWebColorAuto(event.target.checked)} />
              <span><strong>Varyant renklerini otomatik aktar</strong><small>{webColorAutoEnabled ? 'Açık · Her varyant kendi rengiyle Trendyol Web Color olarak gider.' : 'Kapalı · Seçilen renk tüm varyantlara Trendyol Web Color olarak gider; diğer pazaryerleri etkilenmez.'}</small></span>
            </label>
            {webColorAutoEnabled ? null : (
              <label className="attribute-builder-web-color-manual">Trendyol Katalog Rengi<select aria-label="Manuel Trendyol katalog rengi" value={manualWebColorValueId} onChange={event => onManualWebColorValueChange(event.target.value)}><option value="">Trendyol Katalog Rengi Seçin</option>{sortOptionValues('Renk', webColorRequirement.attribute.values).map(value => <option key={value.id} value={value.id}>{cleanOptionValue(value.value)}</option>)}</select></label>
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
                <option value="">Değer Seçin</option>
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
// A card with no publication record of its own (matched by an import, or never sent) is refreshed by looking the
// product up on the marketplace: Trendyol and n11 by model code, Pazarama and PTT AVM by barcode, Shopify by barcode or
// stock code. Hepsiburada looks a single product up by its Hepsiburada product id, which the editor does not hold.
export function cardLookupKey(platformCode: string, keys: { modelCode: string; barcode: string; sku: string }) {
  const code = platformCode.trim().toUpperCase()
  const value = code === 'TRENDYOL' || code === 'N11' ? keys.modelCode
    : code === 'PAZARAMA' || code === 'PTTAVM' ? keys.barcode
      : code === 'SHOPIFY' ? keys.barcode || keys.sku
        : ''
  return value.trim() || null
}

// Live is green, being sent or awaiting the marketplace is yellow, refused is red; a card missing required fields is
// still unpublished (its pill names the gap).
export function publishCardState({ tone, jobRunning, requiredBlock, retryable }: { tone: string; jobRunning: boolean; requiredBlock: boolean; retryable: boolean }): 'unpublished' | 'error' | 'working' | 'live' {
  if (requiredBlock) return 'unpublished'
  if (jobRunning) return 'working'
  if (retryable || tone === 'danger') return 'error'
  if (tone === 'success') return 'live'
  if (tone === 'info' || tone === 'warning') return 'working'
  return 'unpublished'
}

function PublishPlatformCard({ card, selected, productId, familyProductIds, variantLinks, lookupKeys, categoryId, productChecks, selectedAttributes, variantMediaWarning, onSelect, onDeselect, onFeedback: emitFeedback }: {
  familyProductIds?: string[] | null
  lookupKeys?: { modelCode: string; barcode: string; sku: string }
  // How many of the family's variants are present on this marketplace (matched or live), and how many it no longer knows.
  variantLinks?: { linked: number; total: number; missing?: number }
  card: { code: string; name: string; initial: string; tone: string; connection: MarketplaceConnection; mode: 'PUBLISH' | 'READ_ONLY' }
  selected: boolean
  productId?: string
  categoryId: string
  productChecks: Array<{ title: string; detail: string; ok: boolean; shopifyOk?: boolean; shopifyDetail?: string }>
  selectedAttributes: PublicationAttributeSelection[]
  variantMediaWarning?: string
  onSelect: () => void
  onDeselect: () => void
  /** The page's toast: the refresh and stop controls say on screen what they started, naming the marketplace. */
  onFeedback: (feedback: OperationFeedback) => void
}) {
  const client = useQueryClient()
  const platformName = publishPlatformName(card.connection.platformCode)
  // Every notice of this card names the marketplace and the store it is about.
  const onFeedback = (feedback: OperationFeedback) => emitFeedback({ ...feedback, context: { platformCode: card.connection.platformCode, connectionId: card.connection.id, storeName: card.connection.displayName, ...feedback.context } })
  const statusKey = ['publication-status', productId, card.connection.id]
  // The other colour cards of the family (same model code): the card stays selectable while any of them is not live
  // here, and Yayınla then sends only those (live or in-progress cards are skipped).
  const otherFamilyIds = (familyProductIds ?? []).filter(id => id !== productId)
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
  // The job the card's refresh started (a status check or a product lookup): the icon turns while it runs, a stop
  // button sits beside it, and when it ends the card's, its family's and the variants' states are read again.
  const [refreshJobId, setRefreshJobId] = useState<string | null>(null)
  const refreshPublication = useMutation({
    mutationFn: async () => {
      if (!productId) throw new Error('Ürün kaydı bulunamadı.')
      return hubApi<AcceptedJob>(`/products/${productId}/publication-status/${card.connection.id}/refresh`, {
        method: 'POST',
        headers: { 'Idempotency-Key': newIdempotencyKey() },
        body: '{}'
      })
    },
    onSuccess: async accepted => {
      setRefreshJobId(accepted?.jobId ?? null)
      // The refresh checks every colour card of the family: their statuses are read again too, not only this card's
      // (live 2026-10-28: all eight MZ054YNC cards LIVE on the server, the card showed "1/8 renk" from cached siblings).
      await Promise.all([statusKey, ...otherFamilyIds.map(id => ['publication-status', id, card.connection.id])]
        .map(queryKey => client.invalidateQueries({ queryKey })))
      onFeedback({ title: 'Durum Sorgulanıyor', message: publicationRefreshMessage(platformName, card.connection.platformCode, (familyProductIds ?? []).filter(id => id !== productId).length + 1), kind: 'info' })
    },
    onError: reason => {
      // Several cards refused in one go (a family, repeated clicks) collapse into one notice listing the products.
      const failure = notificationError(reason, 'Pazaryeri yanıt vermedi.')
      onFeedback({ title: 'Yayın Durumu Sorgulanamadı', message: `${platformName} yayın durumu sorgulanamadı: ${failure.message}`, kind: 'error', persistent: true, context: { operation: 'Yayın durumu sorgulama', code: failure.code, groupKey: `publication-refresh-failed:${card.connection.id}:${failure.code ?? failure.message}`, groupMessage: `{count} ürün için ${platformName} yayın durumu sorgulanamadı.` } })
    }
  })

  const lookupKey = lookupKeys ? cardLookupKey(card.connection.platformCode, lookupKeys) : null
  const lookupProduct = useMutation({
    mutationFn: () => hubApi<AcceptedJob>(`/connections/${card.connection.id}/product-sync-jobs?${new URLSearchParams({ lookup: lookupKey ?? '', includeArchived: 'true' }).toString()}`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: '{}' }),
    onSuccess: async accepted => {
      setRefreshJobId(accepted?.jobId ?? null)
      await client.invalidateQueries({ queryKey: statusKey })
      onFeedback({ title: 'Ürün Pazaryerinde Aranıyor', message: `${platformName} mağazasında ${lookupKey} aranıyor; bulunursa varyantlar eşleştirilir ve durum güncellenir. Ürün bilgileri değiştirilmez.`, kind: 'info' })
    },
    onError: reason => {
      const failure = notificationError(reason, 'Pazaryeri yanıt vermedi.')
      onFeedback({ title: 'Ürün Aranamadı', message: `${platformName} mağazasında ürün aranamadı: ${failure.message}`, kind: 'error', persistent: true, context: { operation: 'Ürün arama', code: failure.code } })
    }
  })

  const publicationLabel = card.mode === 'READ_ONLY'
    ? 'Ürün Yayını Desteklenmiyor'
    : publication.isPending
    ? 'Yayın Durumu Yükleniyor…'
    : publication.isError
      ? 'Yayın Durumu Alınamadı'
      : publicationStatusLabel(publication.data?.actualStatus, publication.data?.lastJobStatus, card.connection.platformCode)
  const publicationTone = publicationStatusTone(publication.data?.actualStatus, publication.data?.lastJobStatus)
  const familyStatuses = useQueries({
    queries: otherFamilyIds.map(id => ({
      queryKey: ['publication-status', id, card.connection.id],
      queryFn: () => hubApi<PublicationStatus>(`/products/${id}/publication-status/${card.connection.id}`, { cache: 'no-store' }),
      enabled: card.mode === 'PUBLISH',
      // A sibling's own send or status check is followed until it ends, like this card's.
      refetchInterval: (query: { state: { data?: PublicationStatus } }) => isPublicationStatusJobRunning(query.state.data?.lastJobStatus) ? 3000 : false,
      refetchIntervalInBackground: true,
      retry: false,
      staleTime: 0
    }))
  })
  const refreshJob = useCardRefreshJob(refreshJobId, async job => {
    await Promise.all([statusKey, ...otherFamilyIds.map(id => ['publication-status', id, card.connection.id]), ['product', productId]]
      .map(queryKey => client.invalidateQueries({ queryKey })))
    const status = job?.status.trim().toUpperCase()
    if (status && status !== 'SUCCEEDED' && status !== 'CANCELLED')
      onFeedback({ title: 'Sorgu Tamamlanamadı', message: `${platformName}: ${job?.lastErrorSummary?.trim() || statusLabel(status)}`, kind: 'error', persistent: true, context: { operation: 'Yayın durumu sorgulama', code: job?.lastErrorCode ?? undefined } })
  }, {
    onCancelled: () => onFeedback({ title: 'Sorgu Durduruldu', message: `${platformName} sorgusu durduruldu.`, kind: 'info' }),
    onCancelFailed: message => onFeedback({ title: 'Sorgu Durdurulamadı', message: `${platformName}: sorgu durdurulamadı — ${message}`, kind: 'error', persistent: true })
  })
  // A family refresh queues a status check for every colour card: the icon keeps turning until the last one ends,
  // not only this card's own check.
  const familyChecksRunning = Boolean(refreshJobId) && [publication, ...familyStatuses].some(status => isPublicationStatusJobRunning(status.data?.lastJobStatus))
  const refreshBusy = refreshJob.running || familyChecksRunning || refreshPublication.isPending || lookupProduct.isPending
  const ownLive = isPublicationLive(publication.data?.actualStatus)
  const familyLiveCount = (ownLive ? 1 : 0) + familyStatuses.filter(status => isPublicationLive(status.data?.actualStatus)).length
  const familyTotal = otherFamilyIds.length + 1
  // On PTT AVM one request carries the whole family: a colour card the family's send carried has no record of its own
  // until the status refresh takes it into that send, so it refreshes (not looks up) once a sibling is out there.
  const pttAvmFamilySent = card.mode === 'PUBLISH' && card.connection.platformCode.trim().toUpperCase() === 'PTTAVM'
    && familyStatuses.some(status => Boolean(status.data?.profileId) && Boolean(status.data?.actualStatus) && status.data?.actualStatus !== 'UNKNOWN')
  // Matched variants are the truth when they exist: every variant matched = live and not selectable; some = partly
  // live and selectable (Yayınla then sends only the cards none of whose variants is matched here).
  // A variant the marketplace no longer knows (link NOT_FOUND or REJECTED) is not live there: the card is partly live, or
  // "Bulunamadı" when none is left, and stays selectable so Yayınla sends it again.
  const linkedTotal = variantLinks?.total ?? 0
  const linkedCount = variantLinks?.linked ?? 0
  const missingCount = variantLinks?.missing ?? 0
  const { allLinked, someLinked, noneFound } = publishCardVariantPresence(variantLinks)
  const familyPartial = !noneFound && (someLinked || (!allLinked && ownLive && otherFamilyIds.length > 0 && familyLiveCount < familyTotal && familyStatuses.every(status => !status.isPending)))
  const alreadyPublished = missingCount === 0 && (allLinked || (ownLive && !familyPartial))
  const selectionStatus = noneFound || familyPartial ? null : allLinked ? 'LIVE' : publication.data?.actualStatus
  const partialLabel = someLinked ? `Kısmen yayında · ${linkedCount}/${linkedTotal}` : `Kısmen yayında · ${familyLiveCount}/${familyTotal} renk`
  // PTT AVM keeps a product with variants as the whole model family under its model code: every colour card goes in
  // one request. The variant count is this card's when the family is this card alone.
  const isPttAvmFamily = card.mode === 'PUBLISH' && card.connection.platformCode.trim().toUpperCase() === 'PTTAVM' && (familyTotal > 1 || linkedTotal > 1)
  // A PTT AVM family card waiting for an approval that never comes (or stopped) says "Tekrar Deneyin" and stays
  // selectable: Yayınla sends the family again.
  const pttAvmRetry = isPttAvmFamily && Boolean(productId) && isPttAvmFamilyRetryable(card.connection.platformCode, publication.data?.actualStatus, publication.data?.lastJobStatus)
  const publicationCheckPending = Boolean(card.mode === 'PUBLISH' && productId && publication.isPending)
  const publicationCheckUnavailable = Boolean(card.mode === 'PUBLISH' && productId && publication.isError && !publication.data)
  // card.code is lower case (it also names the logo tone); the platform code decides the Shopify-only checks.
  const isShopifyCard = card.connection.platformCode.trim().toUpperCase() === 'SHOPIFY'
  const shopifyWriteCapabilities = useQuery<Array<{ code: string; supportLevel: string; verifiedForConnection: boolean }>>({
    queryKey: ['capabilities', card.connection.id],
    queryFn: () => hubApi(`/connections/${card.connection.id}/capabilities`),
    enabled: card.mode === 'PUBLISH' && isShopifyCard,
    staleTime: 15_000,
    retry: false
  })
  const shopifyCapabilityCheckPending = card.mode === 'PUBLISH' && isShopifyCard && shopifyWriteCapabilities.isPending
  const requiredShopifyCapabilities = ['PRODUCT_WRITE', 'INVENTORY_WRITE', 'PUBLICATION_READ', 'PUBLICATION_WRITE']
  const missingShopifyCapabilities = isShopifyCard && shopifyWriteCapabilities.data
    ? requiredShopifyCapabilities.filter(code => !shopifyWriteCapabilities.data?.some(capability => capability.code === code && capability.supportLevel.toUpperCase() === 'SUPPORTED' && capability.verifiedForConnection))
    : []
  useEffect(() => {
    if (alreadyPublished && selected) onDeselect()
  }, [alreadyPublished, onDeselect, selected])
  const publicationNote = publicationStatusNote(publication.data?.lastRejectionCode)
  const { view: jobView, cancel: cancelJob } = usePublicationJobInline(publication.data?.lastJobId, productId, card.connection.id, {
    onCancelled: status => onFeedback({ title: status?.trim().toUpperCase() === 'CANCELLED' ? 'Yayın İşi Durduruldu' : 'Durdurma İstendi', message: publicationCancelMessage(platformName, status), kind: 'info' }),
    onCancelFailed: message => onFeedback({ title: 'Yayın İşi Durdurulamadı', message: `${platformName}: yayın işi durdurulamadı — ${message}`, kind: 'error', persistent: true })
  })
  const jobRunning = jobView.state === 'running'
  const missingChecks = card.mode === 'PUBLISH' ? productChecks
    .filter(check => !(isShopifyCard ? check.shopifyOk ?? check.ok : check.ok))
    .map(check => ({ ...check, detail: isShopifyCard ? check.shopifyDetail ?? check.detail : check.detail })) : []
  const mappingReadinessEnabled = card.mode === 'PUBLISH' && !isShopifyCard && (!productId || !publication.isPending)
    && (pttAvmRetry ? Boolean(categoryId) : shouldCheckPublicationAttributes(categoryId, selectionStatus, publication.data?.lastJobStatus))
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
      if (card.connection.platformCode.trim().toUpperCase() === 'PTTAVM') return { requiredIssues: [], optionalWarnings: [], categoryScope: categoryMapping.externalId }

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
        // n11 sends an unmapped value of a free-text field (Boy: Regular) as its own text (N11ProductPublicationComposer).
        sendsUnmappedValuesAsFreeText: card.connection.platformCode.trim().toUpperCase() === 'N11',
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
  // The status opens the API job behind it; a card whose required fields are missing keeps its issue tooltip instead.
  const [jobDetailOpen, setJobDetailOpen] = useState(false)
  const [valueFixNotice, setValueFixNotice] = useState('')
  const blockedIssues = [
    ...missingChecks.map(check => ({ attribute: check.title, detail: check.detail })),
    ...(missingShopifyCapabilities.length ? [{ attribute: 'Shopify API izinleri', detail: `Ürün göndermeden önce Entegrasyonlar bölümünde Shopify erişimini yeniden yetkilendirin ve ${missingShopifyCapabilities.join(', ')} izinlerini doğrulayın.` }] : []),
    ...(isShopifyCard && shopifyWriteCapabilities.isError ? [{ attribute: 'Shopify API izin kontrolü', detail: 'Shopify bağlantı izinleri okunamadı. Entegrasyonlar bölümünde bağlantıyı test edip izin kanıtlarını yenileyin.' }] : []),
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
  const lastJobId = !hasRequiredPublicationBlock && productId ? publication.data?.lastJobId ?? null : null
  // A product that is not saved yet has no platform record: it shows what keeps it from publishing, or that it is not out yet.
  const displayedPublicationLabel = hasRequiredPublicationBlock ? 'Yayın için zorunlu eksik' : noneFound ? 'Bulunamadı' : familyPartial ? partialLabel : allLinked ? 'Yayında' : pttAvmRetry && !isPttAvmChecking(card.connection.platformCode, publication.data?.actualStatus) ? pttAvmRetryLabel : productId ? publicationLabel : 'Henüz yayınlanmadı'
  const missingVariantsTitle = noneFound ? 'Pazaryerinde Bulunamadı' : missingCount > 0 ? `${missingCount} varyant pazaryerinde bulunamadı` : undefined
  const displayedPublicationTone = hasRequiredPublicationBlock ? 'danger' : noneFound ? 'danger' : familyPartial ? 'info' : allLinked ? 'success' : pttAvmRetry && !isPttAvmChecking(card.connection.platformCode, publication.data?.actualStatus) ? 'warning' : productId ? publicationTone : 'neutral'
  const readOnlyIsN11 = card.connection.platformCode.trim().toUpperCase() === 'N11'
  const readOnlyTitle = readOnlyIsN11 ? 'N11 ürün açma için Entegrasyonlar bölümünde N11 bağlantısının dış yazma anahtarını açın.' : 'Shopify ürün yayını için Entegrasyonlar bölümünde dış yazma anahtarını açın ve gerekli API izinlerini doğrulayın.'
  // The card's frame and place in the grid: not yet published first, then refused, in progress, and live last.
  const cardState = publishCardState({ tone: displayedPublicationTone, jobRunning, requiredBlock: hasRequiredPublicationBlock, retryable: Boolean(pttAvmRetry) })
  return <article className={`publish-platform-card is-state-${cardState}${selected ? ' selected' : ''}${card.mode === 'READ_ONLY' ? ' is-read-only' : ''}`}>
    <button type="button" className="publish-platform-card-head" onClick={onSelect} aria-pressed={selected} aria-describedby={issueDescriptionIds} title={card.mode === 'READ_ONLY' ? readOnlyTitle : alreadyPublished ? 'Ürün bu platformda zaten yayında.' : undefined} disabled={card.mode === 'READ_ONLY' || isPublicationSelectionDisabled(selectionStatus, selected, publicationCheckPending, publicationCheckUnavailable, selectionBlocked)}>
      <span className={`publish-platform-mark ${card.tone}`}><img className={`publish-platform-logo ${platformLogoClass(card.connection.platformCode)}`} src={platformLogoSource(card.connection.platformCode) ?? '/platforms/trendyol.webp'} alt="" aria-hidden="true" /></span>
      <span><strong>{card.name}</strong><small>{card.mode === 'READ_ONLY' ? 'Dış yazma kapalı · yayın kuyruğu devre dışı' : alreadyPublished ? 'Zaten yayında · yeniden seçilemez' : publicationCheckPending ? 'Yayın durumu kontrol ediliyor…' : publicationCheckUnavailable ? 'Yayın durumu doğrulanamadı' : shopifyCapabilityCheckPending ? 'Shopify yazma izinleri kontrol ediliyor…' : selected ? 'Yayın için seçildi' : mappingCheckPending ? 'Zorunlu alanlar kontrol ediliyor…' : selectionBlocked ? 'Zorunlu eksikler giderilmeden seçilemez' : pttAvmRetry ? "Tekrar denemek için seçip Yayınla'ya basın" : 'Yayın için seçilmedi'}</small></span>
      <i className={`publish-platform-toggle ${selected ? 'on' : ''}`} aria-hidden="true"><b /></i>
    </button>
    <dl className="publish-platform-facts">
      <div><dt>Mağaza</dt><dd title={card.connection.externalStoreId ? `Mağaza no: ${card.connection.externalStoreId}` : undefined}>{card.connection.displayName || card.connection.externalStoreId || '—'}</dd></div>
      <div><dt>Platform</dt><dd>{publishPlatformName(card.connection.platformCode)}</dd></div>
      <div><dt>Bağlantı</dt><dd><span className="publish-platform-status active"><i aria-hidden="true" />Aktif Bağlantı</span></dd></div>
      {/* eslint-disable-next-line jsx-a11y/no-noninteractive-tabindex -- tooltip trigger: focusable so keyboards can open the publication issues */}
      {(productId || card.mode !== 'READ_ONLY') && <div className="publish-platform-fact-publication" aria-live="polite"><dt>Yayın Durumu</dt><dd>{lastJobId ? <button type="button" className={`publish-platform-status publish-platform-status-button status-${displayedPublicationTone}${jobRunning ? ' is-working' : ''}`} title={`${jobRunning ? `${jobView.message} · ` : missingVariantsTitle ? `${missingVariantsTitle} · ` : ''}İş ayrıntısını aç`} aria-haspopup="dialog" onClick={() => setJobDetailOpen(true)}><i aria-hidden="true" />{displayedPublicationLabel}</button> : <span className={`publish-platform-status status-${displayedPublicationTone}${jobRunning ? ' is-working' : ''}`} title={hasRequiredPublicationBlock ? undefined : jobRunning ? jobView.message : missingVariantsTitle} tabIndex={hasRequiredPublicationBlock ? 0 : undefined} aria-describedby={hasRequiredPublicationBlock ? `${issueListId}-required` : undefined} onMouseEnter={placePublishIssueTooltip} onFocus={placePublishIssueTooltip}><i aria-hidden="true" />{displayedPublicationLabel}</span>}{jobRunning && <span className="sr-only">{jobView.message}</span>}{hasRequiredPublicationBlock && <span className="publish-issue-tooltip" role="tooltip"><strong>{blockedIssues.length === 1 ? 'Yayın için 1 eksik var' : `Yayın için ${blockedIssues.length} eksik var`}</strong><ul>{blockedIssues.slice(0, 8).map(issue => <li key={`${issue.attribute}:${issue.detail}`}><b>{issue.attribute}</b> {issue.detail}</li>)}{blockedIssues.length > 8 && <li>Ve {blockedIssues.length - 8} eksik daha.</li>}</ul></span>}{(publication.data?.profileId && publication.data.actualStatus && publication.data.actualStatus !== 'UNKNOWN') || pttAvmFamilySent
        ? <button type="button" className={`publish-tracking-action${refreshBusy ? ' is-spinning' : ''}`} aria-label={refreshBusy ? `${card.name} yayın durumu sorgulanıyor` : `${card.name} yayın durumunu güncelle`} aria-busy={refreshBusy} title={refreshBusy ? 'Platformdaki durum sorgulanıyor…' : 'Platformdaki mevcut durumu yeniden sorgula'} disabled={refreshBusy || isPublicationStatusJobRunning(publication.data?.lastJobStatus)} onClick={() => refreshPublication.mutate()}><UiIcon name="refresh" /></button>
        : productId && card.mode !== 'READ_ONLY' && <button type="button" className={`publish-tracking-action${refreshBusy ? ' is-spinning' : ''}`} aria-label={refreshBusy ? `${card.name} mağazasında ürün aranıyor` : `${card.name} mağazasında ürünü ara`} aria-busy={refreshBusy} title={refreshBusy ? `${card.name} mağazasında ${lookupKey} aranıyor…` : lookupKey ? `${card.name} mağazasında ${lookupKey} ara ve varyantları eşleştir` : `${card.name} tek ürünü kendi ürün numarasıyla arar; bu ürün için o numara bilinmiyor. Ürün Aktar'dan arayın.`} disabled={!lookupKey || refreshBusy || publication.isPending} onClick={() => lookupProduct.mutate()}><UiIcon name="refresh" /></button>}{refreshJob.running && !(jobRunning && publication.data?.lastJobId === refreshJobId) && <button type="button" className="publish-tracking-action publish-tracking-stop" aria-label={refreshJob.cancel.isPending ? `${card.name} sorgusu durduruluyor` : `${card.name} sorgusunu durdur`} title="Sorguyu Durdur" disabled={refreshJob.cancel.isPending} onClick={() => refreshJob.cancel.mutate()}><i aria-hidden="true" /></button>}{jobRunning &&<button type="button" className="publish-tracking-action publish-tracking-stop" aria-label={cancelJob.isPending ? `${card.name} yayın işlemi durduruluyor` : `${card.name} yayın işlemini durdur`} title="Durdur — platforma gönderilmiş bir isteği geri alamayabilir." disabled={cancelJob.isPending} onClick={() => cancelJob.mutate()}><i aria-hidden="true" /></button>}</dd>{publicationNote && <small>{publicationNote}</small>}{jobView.state === 'settled' && <small className={`publish-platform-job-note is-${jobView.tone}`} title={jobView.message}>{jobView.message}</small>}</div>}
    </dl>
    {valueMappingFixes.length > 0 && readinessCategoryScope && <button type="button" className="publish-platform-fix-mappings" onClick={() => setValueFixOpen(true)}><UiIcon name="link" />Eksik Değer Eşlemelerini Tamamla<span className="publish-platform-fix-count" aria-label={`${valueMappingFixes.length} değer`}>{valueMappingFixes.length}</span></button>}
    {valueFixOpen && readinessCategoryScope && <MissingValueMappingDialog connectionId={card.connection.id} platformName={card.name} categoryScope={readinessCategoryScope} fixes={valueMappingFixes} onClose={() => setValueFixOpen(false)} onSaved={message => { setValueFixNotice(message); appendNotification({ title: 'Değer Eşlemeleri Kaydedildi', message, kind: 'success', operation: 'Özellik değeri eşleştirme', platformCode: card.connection.platformCode, connectionId: card.connection.id, storeName: card.connection.displayName, target: { href: '/mappings/attributes', label: 'Eşleştirmelere Git' } }) }} />}
    {jobDetailOpen && lastJobId && <PublicationJobDialog jobId={lastJobId} platformCode={card.connection.platformCode} platformName={card.name} storeName={card.connection.displayName} onClose={() => setJobDetailOpen(false)} />}
    {valueFixNotice && <NoticeToast kind="success" title="Değer Eşlemeleri Kaydedildi" message={valueFixNotice} onClose={() => setValueFixNotice('')} durationMs={5500} />}
    {card.mode === 'READ_ONLY' && <div className="publish-platform-warning" role="note"><UiIcon name="alert" /><div><strong>Shopify Dış Yazması Kapalı</strong><p>Ürün yayınını açmak için Entegrasyonlar bölümünde Shopify dış yazma anahtarını etkinleştirin; bağlantı testinde write_products, write_inventory, read_publications ve write_publications izinleri doğrulanmalıdır.</p></div></div>}
    {shopifyCapabilityCheckPending && <span id={`${issueListId}-shopify-pending`} className="sr-only">Shopify izinleri kontrol ediliyor.</span>}
    {blockedIssues.length > 0 && <span id={`${issueListId}-required`} className="sr-only">{blockedIssues.map(issue => `${issue.attribute}: ${issue.detail}`).join('. ')}</span>}
    {mappingCheckPending && <span id={`${issueListId}-pending`} className="sr-only">Yayın gereksinimleri kontrol ediliyor. Güncel zorunlu özellik eşlemeleri doğrulanıyor.</span>}
    {optionalMappingWarnings.length > 0 && <div id={`${issueListId}-optional`} className="publish-platform-warning" role="status" aria-label={`${card.name} isteğe bağlı özellik uyarıları`}><UiIcon name="alert" /><div><strong>{optionalMappingWarnings.length === 1 ? 'İsteğe bağlı özellik uyarısı' : `${optionalMappingWarnings.length} isteğe bağlı özellik uyarısı`}</strong><ul>{optionalMappingWarnings.map((issue, index) => <li key={`${issue.attribute}-${index}`}><b>{issue.attribute}:</b> {issue.detail}</li>)}</ul></div></div>}
    {variantMediaWarning && <div className="publish-platform-warning" role="status" aria-label={`${card.name} varyant görsel uyarısı`}><UiIcon name="alert" /><div><strong>Varyant görselleri seçilmedi</strong><p>{variantMediaWarning}</p></div></div>}
  </article>
}
// The card's last publication job, read while it runs; the card shows it inline so its height never changes.
function usePublicationJobInline(jobId: string | null | undefined, productId: string | undefined, connectionId: string, notify: { onCancelled: (status: string | null | undefined) => void; onCancelFailed: (message: string) => void }) {
  const client = useQueryClient()
  const queryKey = ['publication-job', jobId ?? ''] as const
  const enabled = Boolean(productId && jobId)
  const job = useQuery({
    queryKey,
    queryFn: () => hubApi<PublicationJobDetail>(`/jobs/${jobId}`, { cache: 'no-store' }),
    enabled,
    refetchInterval: query => publicationJobPollInterval(query.state.data?.job.status, query.state.status === 'error'),
    refetchIntervalInBackground: true,
    refetchOnWindowFocus: true,
    staleTime: 0,
    retry: false
  })
  const cancel = useMutation({
    mutationFn: () => hubApi<PublicationJobDetail>(`/jobs/${jobId}/cancel`, {
      method: 'POST',
      headers: { 'Idempotency-Key': newIdempotencyKey() },
      body: '{}'
    }),
    onSuccess: async result => {
      client.setQueryData(queryKey, result)
      notify.onCancelled(result?.job?.status)
      await Promise.all([
        client.invalidateQueries({ queryKey: ['publication-status', productId, connectionId] }),
        client.invalidateQueries({ queryKey })
      ])
    },
    onError: reason => notify.onCancelFailed(reason instanceof Error ? reason.message : 'bilinmeyen hata.')
  })
  return { view: enabled ? publicationJobInline(job.data?.job) : { state: 'hidden' as const }, cancel }
}
// The job a card's refresh started, read until it ends; onSettled runs once when it does (or when it can no longer be read).
function useCardRefreshJob(jobId: string | null, onSettled: (job: PublicationJobDetail['job'] | undefined) => void | Promise<void>, notify: { onCancelled: () => void; onCancelFailed: (message: string) => void }) {
  const client = useQueryClient()
  const queryKey = ['publication-job', jobId ?? ''] as const
  const job = useQuery({
    queryKey,
    queryFn: () => hubApi<PublicationJobDetail>(`/jobs/${jobId}`, { cache: 'no-store' }),
    enabled: Boolean(jobId),
    refetchInterval: query => publicationJobPollInterval(query.state.data?.job.status, query.state.status === 'error'),
    refetchIntervalInBackground: true,
    staleTime: 0,
    retry: false
  })
  const running = Boolean(jobId) && !job.isError && (job.isPending || isRefreshJobRunning(job.data?.job))
  const settledJobId = useRef<string | null>(null)
  const settle = useEffectEvent(() => { void onSettled(job.data?.job) })
  useEffect(() => {
    if (!jobId || running || settledJobId.current === jobId) return
    settledJobId.current = jobId
    settle()
  }, [jobId, running])
  const cancel = useMutation({
    mutationFn: () => hubApi<PublicationJobDetail>(`/jobs/${jobId}/cancel`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: '{}' }),
    onSuccess: result => {
      client.setQueryData(queryKey, result)
      notify.onCancelled()
    },
    onError: reason => notify.onCancelFailed(reason instanceof Error ? reason.message : 'bilinmeyen hata.')
  })
  return { running, cancel }
}
// Variant option helpers: pure functions of a draft row, shared by the variant media and filter memos.
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
// The product list's "Platformlarda Yayınla" opens the editor's publish step in a popup: the same cards, checks and
// Yayınla, without the page heading, the step bar or the leave-page guard (the popup closes instead of navigating).
function EditorShell({ publishOnly, className, title, eyebrow = 'Katalog', meta, children }: { publishOnly: boolean; className: string; title: string; eyebrow?: string; meta?: ReactNode; children: ReactNode }) {
  return publishOnly ? <div className={`${className} product-publish-popup`}>{children}</div> : <Page className={className} title={title} eyebrow={eyebrow} meta={meta}>{children}</Page>
}

export function NewProductPage({ editProductId, publishOnly = false }: { editProductId?: string; publishOnly?: boolean } = {}) {
  const client = useQueryClient();
  const location = useLocation()
  const navigate = useNavigate()
  const routeState = location.state as { duplicateProductId?: unknown; wizardStep?: unknown } | null
  const duplicateProductId = typeof routeState?.duplicateProductId === 'string' ? routeState.duplicateProductId : undefined
  // Images removed from a copy stay removed (reload, reseed from the source); the copy's own list, never the source's.
  const copyRemovedMedia = useRef<string[]>(editProductId ? [] : copyRemovedMediaKeys(routeState))
  const [error, setError] = useState<unknown>(); const [, setNotice] = useState(''); const operationToasts = useOperationFeedbackToasts()
  // Every notice of the editor names the product it is about (and the marketplace, when a card adds it).
  const setFeedback = (feedback: OperationFeedback) => operationToasts.show({ ...feedback, context: { subject: productNotificationSubject({ id: editProductId, name: form.title, modelCode: form.modelCode }), ...feedback.context } }); const [submitting, setSubmitting] = useState(false); const [mediaAutoSaveTick, setMediaAutoSaveTick] = useState(0);const [calculateDesi, setCalculateDesi] = useState(false); const [desiCalculatorOpen, setDesiCalculatorOpen] = useState(false)
  const [form, setForm] = useState({ title: '', description: '', brandId: '', categoryId: '', baseSku: '', barcode: '', modelCode: '', weight: '', width: '', length: '', height: '', desi: '1', warrantyMonths: '0', deliveryDays: '1', listPrice: '0', salePrice: '0', costPrice: '0', currency: 'TRY', vatRate: '10', vatIncluded: 'INCLUDED', initialStock: '0', safetyStock: '0', mediaUrls: '', status: 'ACTIVE' })
  const [attributeSelections, setAttributeSelections] = useState<Record<string, string[]>>({}); const [attributeTextValues, setAttributeTextValues] = useState<Record<string, string>>({}); const [variantAttributeIds, setVariantAttributeIds] = useState<string[]>([]); const [variantRows, setVariantRows] = useState<VariantDraft[]>([]); const [variantFilterSelections, setVariantFilterSelections] = useState<VariantFilterSelections>({}); const [variantFilterOpen, setVariantFilterOpen] = useState(false); const [draggedVariantKey, setDraggedVariantKey] = useState<string | null>(null); const [dragOverVariantKey, setDragOverVariantKey] = useState<string | null>(null); const [selectedChannelIds, setSelectedChannelIds] = useState<string[]>([]); const [channelPricing, setChannelPricing] = useState<Record<string, ChannelPricingDraft>>({}); const [variantChannelPricing, setVariantChannelPricing] = useState<Record<string, ChannelPricingDraft>>({})
  const [automaticBarcodeGeneration, setAutomaticBarcodeGeneration] = useState(false)
  const [bulkOptionSyncOpen, setBulkOptionSyncOpen] = useState(false)
  const [marketplaceCodesOpen, setMarketplaceCodesOpen] = useState(false)
  // The variant whose marketplace codes the row's link button opened, or null for the whole table.
  const [marketplaceCodesVariant, setMarketplaceCodesVariant] = useState<string | null>(null)
  const [bulkOptionSyncSelection, setBulkOptionSyncSelection] = useState<string[]>([])
  const [attributeValueQueries, setAttributeValueQueries] = useState<Record<string, string>>({})
  const initializedEditProductKey = useRef<string | null>(null)
  const initializedEditOptionsKey = useRef<string | null>(null)
  const initializedEditWebColorKey = useRef<string | null>(null)
  const [wizardStep, setWizardStep] = useState<1 | 2>(() => publishOnly || routeState?.wizardStep === 2 ? 2 : 1)
  const [platformUpdateDialogOpen, setPlatformUpdateDialogOpen] = useState(false)
  const [platformUpdateConnectionIds, setPlatformUpdateConnectionIds] = useState<string[]>([])
  // The fields picked in "Platformlarda Güncelle" stay as they were left, for every product (this browser).
  const [platformUpdatePreferences, setPlatformUpdatePreferences] = useState<PlatformUpdatePreferences>(() => readPlatformUpdatePreferences())
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
  // Unsaved-edit tracking (W6): the snapshot taken right after the form was filled; a differing current snapshot means
  // the form holds edits a reseed would destroy.
  const [snapshotRequest, setSnapshotRequest] = useState(0)
  const [seedSnapshot, setSeedSnapshot] = useState<string | null>(null)
  const [staleProductVersion, setStaleProductVersion] = useState<number | null>(null)
  const allowReseedUntil = useRef(0)
  const [fieldErrors, setFieldErrors] = useState<EditorFieldErrors>({})
  // A save refused with a field message scrolls to that field, frames it red and shakes it.
  const lastFieldReveal = useRef(0)
  useEffect(() => {
    if (!Object.values(fieldErrors).some(Boolean)) return
    const frame = requestAnimationFrame(() => { lastFieldReveal.current = Date.now(); revealFieldError(document.getElementById('product-creation-form')) })
    return () => cancelAnimationFrame(frame)
  }, [fieldErrors])
  const currentEditorSnapshot = useMemo(() => editorSnapshot({ form, variantRows, attributeSelections, attributeTextValues, familyMediaOrder, pendingMediaFileCount: mediaFiles.length, removedVariantIds }), [attributeSelections, attributeTextValues, familyMediaOrder, form, mediaFiles.length, removedVariantIds, variantRows])
  // A new product compares with the empty form it opened with; a copy and an edit with the form right after seeding.
  const editorDirty = seedSnapshot !== null && seedSnapshot !== currentEditorSnapshot
  const captureEditorSnapshot = useEffectEvent(() => setSeedSnapshot(currentEditorSnapshot))
  useEffect(() => { if (snapshotRequest) captureEditorSnapshot() }, [snapshotRequest])
  useEffect(() => { if (!editProductId && !duplicateProductId) setSnapshotRequest(value => value + 1) }, [duplicateProductId, editProductId])
  // One create key for the form's whole life: a resend after a lost answer (network error) is replayed by the server
  // instead of creating the product twice; once the server answered with an error the next attempt gets a new key.
  const [productCreateKey] = useState(() => newIdempotencyKey('product-create'))
  // The editor's own navigation after a save (to the new product's page) is not asked about.
  const leavingAfterSave = useRef(false)
  useEffect(() => {
    if (!editorDirty) return
    const warn = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = '' }
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [editorDirty])
  const [variantPlatformPricing, setVariantPlatformPricing] = useState<{ rowKey: string; platform: VariantPlatformStatus } | null>(null)
  const [variantPlatformPricingDraft, setVariantPlatformPricingDraft] = useState<ChannelPricingDraft>({ listPrice: '', salePrice: '' })
  const [variantPlatformPricingSaving, setVariantPlatformPricingSaving] = useState(false)
  const initialEditMediaUrl = useRef('')
  const initialEditModelCode = useRef('')
  const initialEditDesi = useRef(1)
  const initialEditVariantMediaRefs = useRef<Record<string, string[]>>({})
  // The two column menus draw their list in a portal marked data-variant-header-popover.
  useDismissable(barcodeSkuMenuOpen, [barcodeSkuActionRef], () => setBarcodeSkuMenuOpen(false), { isInside: insideSelector('[data-variant-header-popover="true"]') })
  useDismissable(barcodePasteMenuOpen, [barcodePasteActionRef], () => setBarcodePasteMenuOpen(false), { isInside: insideSelector('[data-variant-header-popover="true"]') })
  useEffect(() => {
    if (!pendingVariantCodeGeneration) return
    function closeCodeGenerationOnEscape(event: KeyboardEvent) {
      if (event.key === 'Escape') setPendingVariantCodeGeneration(null)
    }
    document.addEventListener('keydown', closeCodeGenerationOnEscape)
    return () => document.removeEventListener('keydown', closeCodeGenerationOnEscape)
  }, [pendingVariantCodeGeneration])
  function showFeedback(title: string, message: string, kind: OperationFeedback['kind'], options: { persistent?: boolean; key?: string } = {}) {
    setFeedback({ title, message, kind, ...options })
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
      setNotice(message); showFeedback('Görsel Eklenemedi', message, 'error')
    } else if (accepted.length) {
      const message = `${accepted.length} görsel seçildi.`; setNotice(message); showFeedback('Görsel Seçildi', message, 'info')
    }
  }
  // Always fresh on open and on returning to the tab: the products list's quick edit (or another tab) may have changed
  // its prices, channel prices or stock. Unsaved edits are kept; the stale banner offers the newer data instead.
  const productToEdit = useQuery({ queryKey: ['product', editProductId], queryFn: () => hubApi<Product>(`/products/${editProductId}`), enabled: !!editProductId, staleTime: 0, refetchOnWindowFocus: true })
  const productToCopy = useQuery({ queryKey: ['product', duplicateProductId], queryFn: () => hubApi<Product>(`/products/${duplicateProductId}`), enabled: !editProductId && Boolean(duplicateProductId) })
  const seedProduct = editProductId ? productToEdit.data : productToCopy.data
  const categories = useQuery({ queryKey: ['categories', 'new-product'], queryFn: () => loadAllPages<Category>('/catalog/categories') })
  const brands = useQuery({ queryKey: ['brands', 'new-product'], queryFn: () => loadAllPages<Brand>('/catalog/brands') })
  const connections = useQuery({ queryKey: ['connections', 'new-product'], queryFn: () => loadAllPages<MarketplaceConnection>('/connections') })
  const requirements = useQuery({ queryKey: ['category-requirements', form.categoryId], queryFn: () => hubApi<CategoryRequirement[]>(`/catalog/categories/${form.categoryId}/attribute-requirements`), enabled: !!form.categoryId, retry: false })
  const leafCategories = (categories.data?.items ?? []).filter(item => item.isLeaf && item.isActive); const activeBrands = (brands.data?.items ?? []).filter(item => item.isActive)
  const fallbackListPrice = Number(form.listPrice || 0); const fallbackSalePrice = Number(form.salePrice || 0); const fallbackCostPrice = form.costPrice.trim() === '' ? null : Number(form.costPrice); const initialStock = Number(form.initialStock || 0)
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
  const [optionEditRowKey, setOptionEditRowKey] = useState<string | null>(null)
  useEffect(() => {
    const optionIds = optionRequirements.map(item => item.attributeId)
    setVariantAttributeIds(current => current.filter(id => optionIds.includes(id)))
  }, [optionRequirements])
  const visibleOptionRequirements = optionRequirements

  const seedForm = useEffectEvent(() => {
    const product = seedProduct
    if (!product) return
    const mode = editProductId ? 'edit' : 'copy'
    const productKey = editorSeedKey(mode, product.id, product.version, editorDataStamp(product.variants))
    if (initializedEditProductKey.current === productKey) return
    // A newer version (another tab, an order, the importer) must not wipe edits that are not saved yet: the form is
    // kept and a banner offers to reload it. The editor's own saves allow the next reseed.
    const alreadySeededThisProduct = initializedEditProductKey.current?.startsWith(`${mode}:${product.id}:`) ?? false
    if (editProductId && alreadySeededThisProduct && editorDirty && Date.now() > allowReseedUntil.current) { setStaleProductVersion(product.version); return }
    allowReseedUntil.current = 0
    setStaleProductVersion(null)
    initializedEditProductKey.current = productKey
    const primary = product.variants[0]
    const initialModelCode = primary?.modelCode ?? product.modelCode ?? ''
    const sortedVariants = sortVariantsAlphabetically(product.variants)
    const removedMediaKeys = mode === 'copy' ? copyRemovedMedia.current : []
    const savedMediaUrls = withoutCopyRemovedMedia(orderMediaUrlsByVariants(product.variants, product.mediaUrls ?? [], product.primaryImageUrl, product.hasCustomMediaOrder), removedMediaKeys)
    const savedFamilyMediaUrls = withoutCopyRemovedMedia(product.familyMediaItems?.map(item => item.url) ?? product.familyMediaUrls ?? [], removedMediaKeys)
    const initialListPrice = product.defaultListPrice ?? primary?.listPrice ?? primary?.salePrice ?? 0
    const initialSalePrice = product.defaultSalePrice ?? primary?.salePrice ?? 0
    initialEditModelCode.current = initialModelCode.trim()
    initialEditDesi.current = Number(primary?.desi ?? 1)
    setForm({ title: product.title, description: product.description ?? '', brandId: product.brandId ?? '', categoryId: product.categoryId ?? '', baseSku: mode === 'copy' ? '' : primary?.sku ?? '', barcode: mode === 'copy' ? '' : primary?.barcode ?? '', modelCode: initialModelCode, weight: String(primary?.weight ?? ''), width: String(primary?.width ?? ''), length: String(primary?.length ?? ''), height: String(primary?.height ?? ''), desi: String(primary?.desi ?? 1), warrantyMonths: String(product.warrantyMonths ?? 0), deliveryDays: String(product.deliveryDays ?? 1), listPrice: String(initialListPrice), salePrice: String(initialSalePrice), costPrice: primary?.costPrice == null ? '' : String(primary.costPrice), currency: primary?.currency ?? 'TRY', ...editorVat(product, primary), initialStock: String(primary?.onHand ?? 0), safetyStock: String(primary?.safetyStock ?? 0), mediaUrls: savedMediaUrls.join('\n'), status: product.status || 'ACTIVE' })
    if (!editProductId && duplicateProductId) setChannelPricing(copiedChannelPricing(primary?.platformStatuses ?? [], initialListPrice, initialSalePrice))
    initialEditMediaUrl.current = savedMediaUrls.join('\n')
    setFamilyMediaOrder(withoutCopyRemovedMedia(uniqueMediaUrls(product.familyOrderedMediaUrls?.length
      ? product.familyOrderedMediaUrls
      : [...savedMediaUrls, ...savedFamilyMediaUrls]), removedMediaKeys))
    setFamilyMediaOrderDirty(false)
    setMediaFiles([])
    const seededMediaRefs = seedVariantMediaRefs(product.variants).map(references => withoutCopyRemovedMediaRefs(references, removedMediaKeys))
    const mediaRefsByVariantId = new Map(product.variants.map((variant, index) => [variant.id, seededMediaRefs[index] ?? []]))
    initialEditVariantMediaRefs.current = Object.fromEntries(mediaRefsByVariantId)
    setVariantRows(sortedVariants.map(variant => {
      const options = Object.fromEntries(variantOptionEntries(variant).map(option => [option.name, option.value]))
      const persistedAttributes = variant.attributes ?? []
      const attributeValueIds = Object.fromEntries(persistedAttributes.flatMap(attribute => attribute.valueId ? [[attribute.attributeId, attribute.valueId] as const] : []))
      return { key: variant.id, optionSignature: optionSignatureFromOptions(options) || (variant.optionSignature && variant.optionSignature !== '-' ? variant.optionSignature : 'Tek Ürün'), options, attributeValueIds, persistedAttributes, sku: mode === 'copy' ? '' : variant.sku, barcode: mode === 'copy' ? '' : variant.barcode ?? '', stock: variant.onHand, ...(editProductId ? { loadedStock: variant.onHand } : {}), salePrice: panelPrices(variant, product).salePrice ?? 0, listPrice: panelPrices(variant, product).listPrice ?? 0, costPrice: variant.costPrice ?? null, ...(editProductId ? { loadedCostPrice: variant.costPrice ?? null } : {}), mediaRefs: mediaRefsByVariantId.get(variant.id) ?? [], ...(editProductId ? { platformStatuses: variant.platformStatuses ?? [] } : {}) }
    }))
    const selected: Record<string, string[]> = {}; const typed: Record<string, string> = {}
    for (const attribute of product.attributes ?? []) { if (attribute.valueId) selected[attribute.attributeId] = [...(selected[attribute.attributeId] ?? []), attribute.valueId]; else if (attribute.textValue != null) typed[attribute.attributeId] = attribute.textValue; else if (attribute.numberValue != null) typed[attribute.attributeId] = String(attribute.numberValue); else if (attribute.booleanValue != null) typed[attribute.attributeId] = attribute.booleanValue ? 'evet' : 'hayır' }
    setAttributeSelections(selected); setAttributeTextValues(typed); setVariantAttributeIds([])
    setRemovedVariantIds([])
    setFieldErrors({})
    setSnapshotRequest(value => value + 1)
  })
  const [reseedRequest, setReseedRequest] = useState(0)
  const seedStamp = seedProduct ? editorDataStamp(seedProduct.variants) : ''
  useLayoutEffect(() => seedForm(), [editProductId, seedProduct?.id, seedProduct?.version, seedStamp, reseedRequest])
  // The banner's "reload": drops the unsaved edits and fills the form from the latest saved product.
  function reloadEditedProduct() {
    allowReseedUntil.current = Date.now() + 10_000
    setStaleProductVersion(null)
    void productToEdit.refetch().finally(() => setReseedRequest(value => value + 1))
  }

  useEffect(() => {
    const product = seedProduct
    if ((!editProductId && !duplicateProductId) || !product || !webColorRequirement || requirements.isLoading) return
    const productKey = editorSeedKey(editProductId ? 'edit' : 'copy', product.id, product.version, editorDataStamp(product.variants))
    if (initializedEditProductKey.current !== productKey || initializedEditWebColorKey.current === productKey) return
    initializedEditWebColorKey.current = productKey
    const savedManualColor = product.trendyolWebColorValueId ?? ''
    setWebColorAutoEnabled(!savedManualColor)
    setManualWebColorValueId(savedManualColor)
  }, [duplicateProductId, editProductId, requirements.isLoading, seedProduct, webColorRequirement])

  useEffect(() => {
    const product = seedProduct
    if ((!editProductId && !duplicateProductId) || !product || !allRequirements.length || requirements.isLoading) return
    const productKey = editorSeedKey(editProductId ? 'edit' : 'copy', product.id, product.version, editorDataStamp(product.variants))
    if (initializedEditProductKey.current !== productKey || initializedEditOptionsKey.current === productKey) return
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
    setSnapshotRequest(value => value + 1)
  }, [allRequirements, duplicateProductId, editProductId, optionRequirements, requirements.isLoading, seedProduct])

  function updateField(name: keyof typeof form, value: string) { setForm(current => ({ ...current, [name]: value })) }
  // A marketplace offer keeps the VAT it has; a new one takes the card's KDV Oranı, and with that field empty it is
  // refused (never priced at 0 %).
  function offerVatRate(known: number | null | undefined): number {
    if (known != null) return known
    const issue = editorVatRateIssue(form.vatRate)
    if (issue) {
      setFieldErrors(current => ({ ...current, vatRate: issue }))
      throw new Error(issue)
    }
    return Number(form.vatRate)
  }
  function toggleAttributeValue(attributeId: string, valueId: string) {
    const requirement = mappedRequirements.find(item => item.attributeId === attributeId)
    const alreadySelected = (attributeSelections[attributeId] ?? []).includes(valueId)
    if (!alreadySelected && requirement && isOptionRequirement(requirement) && !variantAttributeIds.includes(attributeId) && variantAttributeIds.length >= 2) {
      const message = 'Bir ürün en fazla 2 seçenek grubuyla varyantlanabilir.'; setNotice(message); showFeedback('Seçenek Grubu Sınırı', message, 'error'); return
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

  function rowOptionEntries(row: VariantDraft) {
    const saved = Object.entries(row.options).filter(([, value]) => value.trim()).map(([name, value]) => ({ name, value }))
    return saved.length ? saved : parseVariantOptionSignature(row.optionSignature)
  }
  function optionAttributeFor(name: string) {
    const key = normalizeVariantOptionName(name)
    return mappedRequirements.find(item => isOptionRequirement(item) && !isWebColorOptionName(item.attribute.name) && normalizeVariantOptionName(item.attribute.name) === key)
  }
  function rowHasOptionValue(row: VariantDraft, name: string, value: string) {
    const key = normalizeVariantOptionName(name)
    return rowOptionEntries(row).some(entry => normalizeVariantOptionName(entry.name) === key && normalizeVariantOptionValue(entry.value) === normalizeVariantOptionValue(value))
  }
  // A changed option value goes to the row (or, with the switch on, every row holding the same value), with its
  // attribute value id so the saved variant attributes follow; the new value joins the option group's selection.
  function applyVariantOptionChanges(rowKey: string, changes: VariantOptionEditChange[], applyToAll: boolean) {
    const nextRows = variantRows.map(row => {
      const applying = changes.filter(change => row.key === rowKey || (applyToAll && rowHasOptionValue(row, change.name, change.from)))
      if (!applying.length) return row
      const options = Object.fromEntries(rowOptionEntries(row).map(entry => [entry.name, entry.value]))
      const attributeValueIds = { ...row.attributeValueIds }
      for (const change of applying) {
        const key = Object.keys(options).find(name => normalizeVariantOptionName(name) === normalizeVariantOptionName(change.name)) ?? change.name
        options[key] = change.to
        const attribute = optionAttributeFor(change.name)
        if (attribute) {
          if (change.valueId) attributeValueIds[attribute.attributeId] = change.valueId
          else delete attributeValueIds[attribute.attributeId]
        }
      }
      return { ...row, options, attributeValueIds, optionSignature: optionSignatureFromOptions(options) || row.optionSignature }
    })
    setVariantRows(nextRows)
    setAttributeSelections(current => {
      const next = { ...current }
      for (const change of changes) {
        const attribute = optionAttributeFor(change.name)
        if (!attribute) continue
        // The old value leaves the selection once no row holds it, so regenerating variants does not bring it back.
        const oldValueId = attribute.attribute.values.find(value => normalizeVariantOptionValue(value.value) === normalizeVariantOptionValue(change.from))?.id
        const oldStillUsed = nextRows.some(row => oldValueId && row.attributeValueIds[attribute.attributeId] === oldValueId)
        next[attribute.attributeId] = [...new Set([...(current[attribute.attributeId] ?? []), ...(change.valueId ? [change.valueId] : [])])]
          .filter(valueId => valueId !== oldValueId || oldStillUsed)
      }
      return next
    })
    setOptionEditRowKey(null)
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
        const message = 'Otomatik barkod oluşturmak için önce model kodunu girin.'; setNotice(message); showFeedback('Model Kodu Gerekli', message, 'error')
        return false
      }
      const generated = buildVariantMatrix(mappedRequirements, attributeIds, attributeSelections, form.baseSku || form.modelCode || form.title, fallbackListPrice, fallbackSalePrice, fallbackCostPrice, initialStock, automaticBarcodeGeneration, form.modelCode)
      if (!generated.length) {
        const message = 'Önce varyant olacak özellikleri ve bu özelliklerin değerlerini seçin.'; setNotice(message); showFeedback('Varyant Özelliği Seçilmedi', message, 'error')
        return false
      }
      setVariantRows(current => {
        if (current.length === 1 && current[0].optionSignature.trim().toLocaleLowerCase('tr-TR') === 'tek ürün' && generated.length === 1) {
          const existing = current[0]
          const [next] = generated
          return [{ ...next, key: existing.key, ...(automaticBarcodeGeneration && !preserveExistingDetails ? {} : { barcode: existing.barcode || next.barcode, sku: existing.sku || next.sku, salePrice: existing.salePrice, listPrice: existing.listPrice }), stock: existing.stock, loadedStock: existing.loadedStock, costPrice: existing.costPrice, loadedCostPrice: existing.loadedCostPrice, mediaRefs: existing.mediaRefs }]
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
      const message = `${generated.length} varyant satırı hazırlandı.`; setNotice(message); showFeedback('Varyantlar Hazırlandı', message, 'success')
      return true
    } catch (reason) { const message = reason instanceof Error ? reason.message : 'Varyantlar oluşturulamadı.'; setNotice(message); showFeedback('Varyantlar Oluşturulamadı', message, 'error'); return false }
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
    setNotice(message); showFeedback('Varyantlar Temizlendi', message, 'success')
  }
  function updateVariantRow(keyValue: string, field: keyof VariantDraft, value: string) { setVariantRows(rows => rows.map(row => row.key !== keyValue ? row : { ...row, [field]: field === 'costPrice' ? (value.trim() === '' ? null : Number(value)) : field === 'stock' || field === 'salePrice' || field === 'listPrice' ? Number(value || 0) : value })) }
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
    showFeedback('Görsel Sırası Değişti', editProductId ? 'Görsel sırası diğer renklerle birlikte Kaydet’te uygulanacak.' : 'Görsel sırası güncellendi. Kalıcı olması için kaydedin.', 'info')
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
    showFeedback('Görsel Sırası Değişti', 'Yüklenen görsel sırası güncellendi. Kalıcı olması için ürünü kaydedin.', 'info')
  }
  // An uploaded image not saved yet leaves the variants that were given it too; their image count kept it before.
  function removeUploadedMedia(id: string) {
    setMediaFiles(files => files.filter(file => file.id !== id))
    setVariantRows(rows => rows.map(row => row.mediaRefs.includes(`file|${id}`) ? { ...row, mediaRefs: row.mediaRefs.filter(reference => reference !== `file|${id}`) } : row))
  }
  // A copy has nothing saved to delete: the removal is written into the page's history entry at once, so it holds after
  // a reload, and the form stays clean when it had no other edits (nothing waits for Kaydet).
  function rememberCopyRemovedMedia(urls: string[]) {
    if (editProductId || !duplicateProductId || !urls.length) return
    copyRemovedMedia.current = withCopyRemovedMedia(copyRemovedMedia.current, urls)
    try { window.history.replaceState(historyStateWithCopyRemovedMedia(window.history.state, copyRemovedMedia.current), '') } catch { /* the in-memory list still holds */ }
    if (!editorDirty) setSnapshotRequest(value => value + 1)
  }
  function clearAllMedia() {
    rememberCopyRemovedMedia(mediaUrls)
    setMediaFiles([])
    updateField('mediaUrls', '')
    setFamilyMediaOrder([])
    setVariantRows(rows => rows.map(row => ({ ...row, mediaRefs: [] })))
    setDraggedMedia(null)
    setDragOverMedia(null)
    showFeedback('Görseller Temizlendi', duplicateProductId && !editProductId ? 'Görseller kopyadan kaldırıldı. Kaynak ürünün görselleri değişmez.' : 'Ürün ve varyant görselleri temizlendi. Kalıcı olması için kaydedin.', 'info')
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
      if (duplicateProductId && !editProductId) {
        rememberCopyRemovedMedia([url])
        showFeedback('Görsel Kaldırıldı', 'Görsel kopyadan kaldırıldı. Kaynak ürünün görselleri değişmez.', 'success')
        return
      }
      showFeedback('Görsel Kaldırıldı', 'Görsel taslaktan kaldırıldı. Kalıcı olması için Kaydet’e basın.', 'info')
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
        headers: { 'Idempotency-Key': newIdempotencyKey(), 'If-Match': `"v${product.version}"` },
        body: JSON.stringify({ productId: product.id, url })
      })
      initialEditMediaUrl.current = savedMediaUrls.filter(item => mediaImageKey(item) !== targetKey).join('\n')
      initialEditVariantMediaRefs.current = Object.fromEntries(Object.entries(initialEditVariantMediaRefs.current).map(([variantId, references]) => [variantId, references.filter(removeVariantReference)]))
      updateField('mediaUrls', nextDraftMediaUrls.join('\n'))
      setVariantRows(rows => rows.map(row => ({ ...row, mediaRefs: row.mediaRefs.filter(removeVariantReference) })))
      setFamilyMediaOrder(current => current.filter(item => mediaImageKey(item) !== targetKey))
      // The deletion is saved already: a form that had no other edits stays "clean" afterwards.
      if (!editorDirty) setSnapshotRequest(value => value + 1)

      const cachedProduct = client.getQueryData<Product>(['product', product.id]) ?? product
      const cachedMediaUrls = (cachedProduct.mediaUrls ?? []).filter(item => mediaImageKey(item) !== targetKey)
      const productVersion = result.productVersions[product.id] ?? result.version
      initializedEditProductKey.current = editorSeedKey('edit', product.id, productVersion, editorDataStamp(cachedProduct.variants))
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
      showFeedback('Görsel Kaldırıldı', `${result.deletedCount} görsel bağlantısı kaldırıldı ve ürün yenilendi.`, 'success')
    } catch (reason) {
      showFeedback('Görsel Kaldırılamadı', reason instanceof Error ? reason.message : 'Görsel kaldırılamadı.', 'error')
    } finally {
      setDeletingProductMediaKey(null)
    }
  }
  async function removeFamilyMedia(item: ProductFamilyMediaItem, confirmed = false) {
    if (item.mediaIds.length === 0) {
      showFeedback('Görsel Kaldırılamadı', 'Görselin kaynak bağlantıları yüklenemedi. Sayfayı yenileyip tekrar deneyin.', 'error')
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
        headers: { 'Idempotency-Key': newIdempotencyKey() },
        body: JSON.stringify({ mediaIds: item.mediaIds })
      })
      setFamilyMediaOrder(current => current.filter(url => mediaImageKey(url) !== mediaImageKey(item.url)))
      // The variants that showed the image drop it too: the rows are not seeded again, so their count stayed.
      const removeVariantReference = (reference: string) => !reference.startsWith('url|') || mediaImageKey(reference.slice(4)) !== mediaImageKey(item.url)
      initialEditVariantMediaRefs.current = Object.fromEntries(Object.entries(initialEditVariantMediaRefs.current).map(([variantId, references]) => [variantId, references.filter(removeVariantReference)]))
      setVariantRows(rows => rows.map(row => ({ ...row, mediaRefs: row.mediaRefs.filter(removeVariantReference) })))
      await productToEdit.refetch()
      await client.invalidateQueries({ queryKey: ['products'] })
      showFeedback('Aile Görseli Kaldırıldı', 'Renk ailesi görseli kaldırıldı.', 'success')
    } catch (reason) {
      showFeedback('Aile Görseli Kaldırılamadı', reason instanceof Error ? reason.message : 'Renk ailesi görseli kaldırılamadı.', 'error')
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
    const platformCode = connections.data?.items.find(item => item.id === id)?.platformCode
    const name = platformCode ? publishPlatformName(platformCode) : 'Pazaryeri'
    setSelectedChannelIds(current => selected ? current.filter(item => item !== id) : [...current, id])
    showFeedback(selected ? 'Platform Seçimden Çıkarıldı' : 'Platform Seçildi', selected ? `${name} yayın seçiminden çıkarıldı; Yayınla ona gönderim yapmaz.` : `${name} yayın için seçildi; Yayınla ile gönderim kuyruğuna alınır.`, 'info')
  }
  function updateableProductConnectionIds() {
    return (connections.data?.items ?? []).filter(item => isProductPublicationConnection(item) && productUpdateCapabilities(item.platformCode).writable && item.status.trim().toUpperCase() === 'ACTIVE').map(item => item.id)
  }
  function openPlatformUpdateDialog() {
    const updateableIds = updateableProductConnectionIds()
    const currentSelection = selectedChannelIds.filter(id => updateableIds.includes(id))
    setPlatformUpdateConnectionIds(currentSelection.length ? currentSelection : updateableIds)
    setPlatformUpdatePreferences(readPlatformUpdatePreferences())
    setPlatformUpdateDialogOpen(true)
  }
  function changePlatformUpdatePreferences(next: PlatformUpdatePreferences) {
    setPlatformUpdatePreferences(next)
    writePlatformUpdatePreferences(next)
  }
  function queuePlatformUpdate(connectionIds: string[], fields: PlatformUpdateField[]) {
    const updateableIds = new Set(updateableProductConnectionIds())
    const safeConnectionIds = [...new Set(connectionIds)].filter(id => updateableIds.has(id))
    if (!safeConnectionIds.length) {
      showFeedback('Platform Seçilmedi', 'Güncelleme için desteklenen ve aktif bir platform seçin.', 'error')
      return
    }
    const formElement = document.getElementById('product-creation-form')
    if (!(formElement instanceof HTMLFormElement)) {
      showFeedback('Güncelleme Başlatılamadı', 'Ürün formu bulunamadı; güncelleme başlatılamadı.', 'error')
      return
    }
    pendingPlatformUpdate.current = { connectionIds: safeConnectionIds, fields }
    setPlatformUpdateDialogOpen(false)
    formElement.requestSubmit()
  }
  function confirmPlatformUpdate(fields: PlatformUpdateField[]) {
    queuePlatformUpdate(platformUpdateConnectionIds, fields)
  }
  function startQuickPlatformUpdate() {
    const connectionIds = quickPlatformUpdateTargets(selectedChannelIds, updateableProductConnectionIds())
    if (!connectionIds.length) {
      openPlatformUpdateDialog()
      showFeedback('Platform Seçin', 'Hızlı güncelleme için önce güncellenecek platformu seçin.', 'info')
      return
    }
    queuePlatformUpdate(connectionIds, priceStockPlatformUpdateFields)
  }
  function channelPriceDraft(connectionId: string): ChannelPricingDraft {
    return channelPricing[connectionId] ?? { listPrice: form.listPrice, salePrice: form.salePrice }
  }
  function toggleCustomChannelPrice(connectionId: string) {
    setChannelPricing(current => {
      if (connectionId in current) { const { [connectionId]: _removed, ...rest } = current; return rest }
      return { ...current, [connectionId]: emptyChannelPricingDraft }
    })
  }
  function updateChannelPrice(connectionId: string, field: keyof ChannelPricingDraft, value: string) {
    setChannelPricing(current => ({ ...current, [connectionId]: { ...(current[connectionId] ?? emptyChannelPricingDraft), [field]: value } }))
  }
  function platformDisplayName(platform: VariantPlatformStatus) {
    const code = platform.platformCode.trim().toLocaleUpperCase('tr-TR')
    return code === 'SHOPIFY' ? 'Shopify' : code === 'TRENDYOL' ? 'Trendyol' : code === 'HEPSIBURADA' ? 'Hepsiburada' : code === 'N11' ? 'N11' : code === 'PAZARAMA' ? 'Pazarama' : code === 'PTTAVM' ? 'PTT AVM' : platform.platform
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
      showFeedback('Fiyat Bilgisi Alınamadı', 'Bu platform bağlantısının fiyat bilgisi alınamadı.', 'error')
      return
    }
    const activeDraft = targetDraft ?? variantPlatformPricingDraft
    const listPrice = Number(activeDraft.listPrice)
    const salePrice = Number(activeDraft.salePrice)
    if (!Number.isFinite(listPrice) || !Number.isFinite(salePrice) || listPrice < 0 || salePrice < 0 || listPrice < salePrice) {
      showFeedback('Geçersiz Fiyat', 'Liste fiyatı satış fiyatından küçük olamaz.', 'error')
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
          vatRate: offerVatRate(platform.vatRate ?? selectedPlatform.vatRate),
          vatInclusion: platform.vatInclusion ?? selectedPlatform.vatInclusion ?? form.vatIncluded,
          roundingMode: platform.roundingMode ?? selectedPlatform.roundingMode ?? 'HALF_EVEN',
          safetyStock: platform.safetyStock ?? selectedPlatform.safetyStock ?? Number(form.safetyStock || 0),
          status: 'ACTIVE',
          reason: `${platformDisplayName(platform)} toplu varyant fiyatı`
        }
        const saved = platform.offerId && platform.offerVersion != null
          ? await hubApi<{ id: string; version: number; listPrice: number; salePrice: number; currency: string }>(`/channel-offers/${platform.offerId}`, { method: 'PATCH', headers: { 'If-Match': `"v${platform.offerVersion}"` }, body: JSON.stringify(body) })
          : await hubApi<{ id: string; version: number; listPrice: number; salePrice: number; currency: string }>('/channel-offers', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ connectionId: platform.connectionId, variantId: row.key, ...body }) })
        savedOffers.push({ rowKey: row.key, platform, saved })
      }
      if (!savedOffers.length) {
        showFeedback('Varyant Bulunamadı', 'Bu platform için güncellenebilir varyant bulunamadı.', 'error')
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
      setNotice(message); showFeedback('Platform Fiyatları Kaydedildi', message, 'success')
      await client.invalidateQueries({ queryKey: ['products'] })
      if (editProductId) {
        await client.invalidateQueries({ queryKey: ['product', editProductId] })
        await productToEdit.refetch()
      }
    } catch (reason) {
      const message = reason instanceof Error ? reason.message : 'Pazaryeri varyant fiyatları kaydedilemedi.'
      showFeedback('Platform Fiyatları Kaydedilemedi', message, 'error')
    } finally {
      setVariantPlatformPricingSaving(false)
    }
  }
  async function saveVariantPlatformPricingRow(targetRow: VariantDraft, targetPlatform: VariantPlatformStatus, targetDraft: ChannelPricingDraft) {
    const platform = targetRow.platformStatuses?.find(item => item.connectionId === targetPlatform.connectionId)
    if (!platform?.connectionId) {
      showFeedback('Fiyat Bilgisi Alınamadı', 'Bu platform bağlantısının fiyat bilgisi alınamadı.', 'error')
      return
    }
    const listPrice = Number(targetDraft.listPrice)
    const salePrice = Number(targetDraft.salePrice)
    if (!Number.isFinite(listPrice) || !Number.isFinite(salePrice) || listPrice < 0 || salePrice < 0 || listPrice < salePrice) {
      showFeedback('Geçersiz Fiyat', 'Liste fiyatı satış fiyatından küçük olamaz.', 'error')
      return
    }
    setVariantPlatformPricingSaving(true)
    try {
      const body = {
        listPrice,
        salePrice,
        currency: platform.currency ?? targetPlatform.currency ?? form.currency ?? 'TRY',
        vatRate: offerVatRate(platform.vatRate ?? targetPlatform.vatRate),
        vatInclusion: platform.vatInclusion ?? targetPlatform.vatInclusion ?? form.vatIncluded,
        roundingMode: platform.roundingMode ?? targetPlatform.roundingMode ?? 'HALF_EVEN',
        safetyStock: platform.safetyStock ?? targetPlatform.safetyStock ?? Number(form.safetyStock || 0),
        status: 'ACTIVE',
        reason: `${platformDisplayName(platform)} varyant fiyatı`
      }
      const saved = platform.offerId && platform.offerVersion != null
        ? await hubApi<{ id: string; version: number; listPrice: number; salePrice: number; currency: string }>(`/channel-offers/${platform.offerId}`, { method: 'PATCH', headers: { 'If-Match': `"v${platform.offerVersion}"` }, body: JSON.stringify(body) })
        : await hubApi<{ id: string; version: number; listPrice: number; salePrice: number; currency: string }>('/channel-offers', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ connectionId: platform.connectionId, variantId: targetRow.key, ...body }) })
      setVariantRows(rows => rows.map(row => row.key !== targetRow.key ? row : { ...row, platformStatuses: row.platformStatuses?.map(item => item.connectionId === platform.connectionId ? { ...item, offerId: saved.id, listPrice: saved.listPrice, salePrice: saved.salePrice, currency: saved.currency, offerVersion: saved.version } : item) }))
      showFeedback('Platform Fiyatı Kaydedildi', `${platformDisplayName(platform)} ${targetRow.optionSignature || targetRow.sku} fiyatı kaydedildi.`, 'success')
      await client.invalidateQueries({ queryKey: ['products'] })
      if (editProductId) {
        await client.invalidateQueries({ queryKey: ['product', editProductId] })
        await productToEdit.refetch()
      }
    } catch (reason) {
      showFeedback('Platform Fiyatı Kaydedilemedi', reason instanceof Error ? reason.message : 'Varyant kanal fiyatı kaydedilemedi.', 'error')
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
      showFeedback('Platform Fiyatı Bulunamadı', 'Bu ürün için güncellenebilir platform fiyatı bulunamadı.', 'error')
      return
    }
    const platformKey = (platform: VariantPlatformStatus) => `${platform.platformCode}:${platform.connectionId ?? platform.platform}`
    const targets = variantRows.flatMap(targetRow => sourcePlatforms.flatMap(sourcePlatform => {
      const platform = targetRow.platformStatuses?.find(item => item.connectionId === sourcePlatform.connectionId) ?? (!editProductId ? sourcePlatform : undefined)
      const draft = drafts[`${targetRow.key}:${platformKey(sourcePlatform)}`]
      return platform?.connectionId && draft ? [{ row: targetRow, platform, draft }] : []
    }))
    if (!targets.length) {
      showFeedback('Varyant Bulunamadı', 'Bu platform için güncellenebilir varyant bulunamadı.', 'error')
      return
    }
    for (const target of targets) {
      const listPrice = Number(target.draft.listPrice)
      const salePrice = Number(target.draft.salePrice)
      if (!Number.isFinite(listPrice) || !Number.isFinite(salePrice) || listPrice < 0 || salePrice < 0 || listPrice < salePrice) {
        showFeedback('Geçersiz Fiyat', 'Liste fiyatı satış fiyatından küçük olamaz.', 'error')
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
      setNotice(message); showFeedback('Platform Fiyatları Hazırlandı', message, 'success')
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
          vatRate: offerVatRate(target.platform.vatRate),
          vatInclusion: target.platform.vatInclusion ?? form.vatIncluded,
          roundingMode: target.platform.roundingMode ?? 'HALF_EVEN',
          safetyStock: target.platform.safetyStock ?? Number(form.safetyStock || 0),
          status: 'ACTIVE',
          reason: `${platformDisplayName(target.platform)} toplu varyant fiyatı`
        }
        const saved = target.platform.offerId && target.platform.offerVersion != null
          ? await hubApi<{ id: string; version: number; listPrice: number; salePrice: number; currency: string }>(`/channel-offers/${target.platform.offerId}`, { method: 'PATCH', headers: { 'If-Match': `"v${target.platform.offerVersion}"` }, body: JSON.stringify(body) })
          : await hubApi<{ id: string; version: number; listPrice: number; salePrice: number; currency: string }>('/channel-offers', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ connectionId: target.platform.connectionId, variantId: target.row.key, ...body }) })
        savedOffers.push({ rowKey: target.row.key, connectionId: target.platform.connectionId!, saved })
      }
      const savedByKey = new Map(savedOffers.map(item => [`${item.rowKey}:${item.connectionId}`, item]))
      setVariantRows(currentRows => currentRows.map(targetRow => ({ ...targetRow, platformStatuses: targetRow.platformStatuses?.map(platform => { const saved = savedByKey.get(`${targetRow.key}:${platform.connectionId}`); return saved ? { ...platform, offerId: saved.saved.id, listPrice: saved.saved.listPrice, salePrice: saved.saved.salePrice, currency: saved.saved.currency, offerVersion: saved.saved.version } : platform }) })))
      setVariantPlatformPricing(null)
      const message = `${savedOffers.length} varyant platform fiyatı kaydedildi.`
      setNotice(message); showFeedback('Platform Fiyatları Kaydedildi', message, 'success')
      await client.invalidateQueries({ queryKey: ['products'] })
      if (editProductId) {
        await client.invalidateQueries({ queryKey: ['product', editProductId] })
        await productToEdit.refetch()
      }
    } catch (reason) {
      showFeedback('Platform Fiyatları Kaydedilemedi', reason instanceof Error ? reason.message : 'Pazaryeri varyant fiyatları kaydedilemedi.', 'error')
    } finally {
      setVariantPlatformPricingSaving(false)
    }
  }
  function applyBulk() {
    const stock = bulkStock === '' ? null : Number(bulkStock); const sale = bulkSalePrice === '' ? null : Number(bulkSalePrice); const cost = bulkCostPrice === '' ? null : Number(bulkCostPrice); const list = bulkListPrice === '' ? null : Number(bulkListPrice)
    const matchingKeys = new Set(variantRows.filter(row => rowMatchesVariantFilters(row)).map(row => row.key))
    if (hasVariantFilters && !matchingKeys.size) {
      const message = 'Seçtiğiniz filtrelerle eşleşen varyant bulunamadı.'; setNotice(message); showFeedback('Eşleşen Varyant Yok', message, 'error')
      return
    }
    setVariantRows(rows => rows.map(row => !matchingKeys.has(row.key) ? row : { ...row, stock: stock == null || !Number.isFinite(stock) ? row.stock : stock, salePrice: sale == null || !Number.isFinite(sale) ? row.salePrice : sale, costPrice: cost == null || !Number.isFinite(cost) ? row.costPrice : cost, listPrice: list == null || !Number.isFinite(list) ? row.listPrice : list }))
    const message = hasVariantFilters ? `Toplu stok, fiyat ve maliyet değerleri ${matchingKeys.size} seçili varyanta uygulandı.` : 'Toplu stok, fiyat ve maliyet değerleri tüm varyantlara uygulandı.'; setNotice(message); showFeedback('Toplu Değerler Uygulandı', message, 'success')
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
    const issue = variantBulkEditIssue(variantBulkEdit.field, value)
    if (issue) {
      setVariantBulkEdit(current => current ? { ...current, error: issue } : current)
      return
    }
    const rowKeys = new Set(targetRows.map(row => row.key))
    setVariantRows(rows => applyVariantBulkEditValue(rows, rowKeys, variantBulkEdit.field, value))
    showFeedback('Toplu Değer Uygulandı', `${variantBulkEditLabels[variantBulkEdit.field]} değeri ${hasVariantBulkEditFilters ? `${targetRows.length} filtre eşleşen varyanta` : `tüm ${targetRows.length} varyanta`} uygulandı.`, 'success')
    setVariantBulkEdit(null)
  }

  function applyBarcodeToSku(mode: 'missing' | 'all') {
    const rowsWithBarcode = variantRows.filter(row => row.barcode.trim())
    if (!rowsWithBarcode.length) {
      const message = 'Stok kodu oluşturmak için önce en az bir barkod girin.'; setNotice(message); showFeedback('Barkod Gerekli', message, 'error'); setBarcodeSkuMenuOpen(false)
      return
    }
    const rowsToUpdate = rowsWithBarcode.filter(row => mode === 'all' || !row.sku.trim())
    if (!rowsToUpdate.length) {
      const message = 'Boş stok kodu bulunamadı; mevcut kodlar korunuyor.'; setNotice(message); showFeedback('Stok Kodları Korundu', message, 'info'); setBarcodeSkuMenuOpen(false)
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
      const message = 'Barkodlar uygulanamadı; stok kodlarında çakışma var. Önce tekrar eden barkodları düzeltin.'; setNotice(message); showFeedback('Stok Kodu Çakışması', message, 'error'); setBarcodeSkuMenuOpen(false)
      return
    }
    const safeKeys = new Set(safeRows.map(row => row.key))
    setVariantRows(rows => rows.map(row => safeKeys.has(row.key) ? { ...row, sku: row.barcode.trim() } : row))
    const skippedCount = rowsToUpdate.length - safeRows.length
    const message = skippedCount
      ? `${safeRows.length} varyanta uygulandı; ${skippedCount} satır çakışma nedeniyle korunuyor.`
      : `${safeRows.length} varyantın stok kodu barkoddan güncellendi.`
    setNotice(message); showFeedback('Stok Kodları Güncellendi', message, skippedCount ? 'info' : 'success'); setBarcodeSkuMenuOpen(false)
  }

  function regenerateVariantCodes(target: 'barcode' | 'sku') {
    const codes = buildSequentialVariantCodes(form.modelCode, variantRows.length)
    if (!codes.length) {
      const message = !form.modelCode.trim() ? 'Otomatik kod üretmek için önce model kodunu girin.' : 'Kod üretimi için varyant sayısı 1-1000 arasında olmalıdır.'
      setNotice(message); showFeedback('Kod Üretilemedi', message, 'error'); setBarcodePasteMenuOpen(false); setBarcodeSkuMenuOpen(false)
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
      setNotice(message); showFeedback('Kod Üretilemedi', message, 'error'); setPendingVariantCodeGeneration(null)
      return
    }

    setVariantRows(rows => applyGeneratedVariantCodes(rows, request.target, request.codes))
    if (request.target === 'barcode') setForm(current => ({ ...current, barcode: request.codes[0] }))
    // A generated code that some active marketplace refuses is named.
    const ruleIssues = platformCodeIssues(codeRulePlatforms, request.target === 'barcode' ? request.codes[0] : '', request.target === 'sku' ? request.codes[0] : '')
    const ruleText = describePlatformCodeIssues(ruleIssues, request.target)
    const message = `${request.codes.length} varyantın ${request.target === 'barcode' ? 'barkodu' : 'stok kodu'} model kodundan oluşturuldu; diğer kod alanı korundu.${request.target === 'barcode' ? ' Üst barkod alanı ilk varyantla eşitlendi.' : ''} Kalıcı olması için Kaydet’e basın.${ruleText ? ` Uyarı: ${ruleText}.` : ''}`
    setNotice(message); showFeedback('Kodlar Oluşturuldu', message, ruleText ? 'info' : 'success'); setPendingVariantCodeGeneration(null)
  }

  async function pasteBarcodesFromClipboard(mode: 'missing' | 'all') {
    if (typeof navigator.clipboard?.readText !== 'function') {
      const message = 'Pano okuma bu tarayıcıda kullanılamıyor; barkodları satırlara elle yapıştırın.'
      setNotice(message); showFeedback('Pano Kullanılamıyor', message, 'error'); setBarcodePasteMenuOpen(false)
      return
    }

    let clipboardText: string
    try {
      clipboardText = await navigator.clipboard.readText()
    } catch {
      const message = 'Panoya erişilemedi. Tarayıcı pano iznini verip tekrar deneyin.'
      setNotice(message); showFeedback('Panoya Erişilemedi', message, 'error'); setBarcodePasteMenuOpen(false)
      return
    }

    const rows = variantRowsRef.current
    const rowsToUpdate = mode === 'missing' ? rows.filter(row => !row.barcode.trim()) : rows
    if (!rowsToUpdate.length) {
      const message = mode === 'missing' ? 'Boş barkod satırı bulunamadı.' : 'Önce varyant satırlarını oluşturun.'
      setNotice(message); showFeedback('Barkod Satırı Yok', message, 'info'); setBarcodePasteMenuOpen(false)
      return
    }
    const values = parseBarcodeClipboardValues(clipboardText)
    const targetKeys = new Set(rowsToUpdate.map(row => row.key))
    const reservedValues = rows.filter(row => !targetKeys.has(row.key)).map(row => row.barcode.trim())
    const issue = barcodeClipboardIssue(values, rowsToUpdate.length, reservedValues)
    if (issue) {
      setNotice(issue); showFeedback('Barkodlar Yapıştırılamadı', issue, 'error'); setBarcodePasteMenuOpen(false)
      return
    }

    const valuesByKey = new Map(rowsToUpdate.map((row, index) => [row.key, values[index].trim()]))
    setVariantRows(currentRows => currentRows.map(row => {
      const barcode = valuesByKey.get(row.key)
      return barcode === undefined ? row : { ...row, barcode }
    }))
    const message = `${rowsToUpdate.length} varyantın barkodu panodan yapıştırıldı.`
    setNotice(message); showFeedback('Barkodlar Yapıştırıldı', message, 'success'); setBarcodePasteMenuOpen(false)
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
      setNotice(message); showFeedback('Seçenek Değeri Gerekli', message, 'error')
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
  function rowMatchesVariantFilters(row: VariantDraft) {
    return filterVariantsByOptions([row], variantFilterGroups, variantFilterSelections, rowMatchesVariantMediaValue).length > 0
  }
  // Image assignments made in the window are saved on their own right away on the edit page: only the variants'
  // pictures, never the rest of the form (unsaved prices or stock stay unsaved). The effect runs after variantRows has
  // committed so it sees the new references. New variants and pictures still to upload wait for the product save.
  const mediaAssignmentFormWasClean = useRef(false)
  const saveVariantMediaAssignments = useEffectEvent(async () => {
    const product = productToEdit.data
    if (!mediaAutoSaveTick || !editProductId || !product) return
    const requests = variantMediaReconcileRequests(variantRows, initialEditVariantMediaRefs.current)
    if (requests === null || submitting) {
      showFeedback('Görsel Ataması Eklendi', 'Görsel ataması forma eklendi; ürünü kaydettiğinizde kaydedilir.', 'info')
      return
    }
    if (!requests.length) return
    const formWasClean = mediaAssignmentFormWasClean.current
    try {
      for (const request of requests) {
        await hubApi('/files/product-media-reconcile', { method: 'PUT', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ productId: product.id, variantId: request.variantId, items: request.items, altText: `${product.title} · ${request.optionSignature}` }) })
        initialEditVariantMediaRefs.current = { ...initialEditVariantMediaRefs.current, [request.variantId]: request.items.map(item => `url|${item.url}`) }
      }
      // A form with no other edits stays clean; other unsaved edits are left for the Kaydet button.
      if (formWasClean) setSnapshotRequest(value => value + 1)
      void client.invalidateQueries({ queryKey: ['products'] })
      showFeedback('Görsel Ataması Kaydedildi', `${requests.length} varyantın görsel ataması kaydedildi.${formWasClean ? '' : ' Formdaki diğer değişiklikler henüz kaydedilmedi.'}`, 'success')
    } catch (reason) {
      const message = reason instanceof Error ? reason.message : 'Görsel ataması kaydedilemedi.'
      showFeedback('Görsel Ataması Kaydedilemedi', `Görsel ataması kaydedilemedi: ${message} Atama formda duruyor; ürünü kaydettiğinizde yeniden denenir.`, 'error')
    }
  })
  useEffect(() => { void saveVariantMediaAssignments() }, [mediaAutoSaveTick])
  function requestVariantMediaSave() {
    mediaAssignmentFormWasClean.current = !editorDirty
    setMediaAutoSaveTick(tick => tick + 1)
  }

  function applyVariantMediaSelection() {
    if (!variantMediaModal) return
    if (variantMediaModal.mode === 'variant') {
      updateVariantMedia(variantMediaModal.rowKey, variantMediaModal.draftRefs)
      setVariantMediaModal(null)
      requestVariantMediaSave()
      showFeedback('Varyant Görselleri Güncellendi', variantMediaModal.draftRefs.length ? `${variantMediaModal.draftRefs.length} varyant görseli seçildi.` : 'Varyant görselleri kaldırıldı.', 'success')
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
      setNotice(message); showFeedback('Eşleşen Varyant Yok', message, 'error')
      return
    }
    setVariantRows(rows => rows.map(row => {
      const assignment = assignments.find(item => item.matchingRows.some(matchingRow => matchingRow.key === row.key))
      return assignment ? { ...row, mediaRefs: assignment.refs } : row
    }))
    setVariantMediaModal(null); requestVariantMediaSave()
    const message = `${group.name} görsel eşlemesi uygulandı: ${assignments.length} değer, ${matchingRowCount} varyant satırı.`
    setNotice(message); showFeedback('Görsel Eşlemesi Uygulandı', message, 'success')
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
     if (form.warrantyMonths.trim() && (!Number.isInteger(Number(form.warrantyMonths)) || Number(form.warrantyMonths) < 0 || Number(form.warrantyMonths) > 240)) issues.push('Garanti süresi 0-240 ay arasında tam sayı olmalıdır.')
     if (!isProductDeliveryDays(form.deliveryDays)) issues.push('Teslimat süresi 1-30 gün arasında tam sayı olmalıdır.')
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
    // Only the first refused field of one save is brought into view.
    if (Date.now() - lastFieldReveal.current > 600) { lastFieldReveal.current = Date.now(); revealFieldError(null, target) }
    setError(new Error(message))
    setNotice(message)
    showFeedback('Zorunlu Alan Eksik', message, 'error')
  }

  // Editing the form takes back the error it was showing.
  function dismissProductError() {
    setError(undefined)
    setNotice('')
    operationToasts.dismissErrors()
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
    // KDV Oranı is required on every card (the server refuses a card without one): stop here, on the first step where
    // the field is, with its message under it.
    const vatRateIssue = editorVatRateIssue(form.vatRate)
    if (vatRateIssue) {
      setFieldErrors(current => ({ ...current, vatRate: vatRateIssue }))
      if (!publishOnly) setWizardStep(1)
      setError(new Error(vatRateIssue)); setNotice(vatRateIssue)
      showFeedback('Zorunlu Alan Eksik', vatRateIssue, 'error')
      return
    }
    if (wizardStep !== 2 && !saveAndStay && !createOnly && !platformUpdate) {
      setWizardStep(2)
      return
    }
    // Whether the product itself had edits to write: the result says "Ürün bilgileri kaydedildi" only then.
    const productChangedAtSubmit = editorDirty
    // The pending toast names what this press does: publishing (Yayınla), updating the stores, or only saving.
    const publishing = !saveAndStay && !createOnly && !platformUpdate && selectedChannelIds.length > 0
    const storeCount = `${selectedChannelIds.length} platforma`
    const [pendingTitle, pendingMessage] = platformUpdate
      ? ['Platformlar Güncelleniyor', 'Ürün kaydediliyor ve seçili mağazalarda güncelleniyor…']
      : publishing
        ? editProductId
          ? ['Ürün Yayınlanıyor', productChangedAtSubmit ? `Ürün kaydediliyor ve ${storeCount} gönderiliyor…` : `Ürün ${storeCount} gönderiliyor…`]
          : ['Ürün Oluşturuluyor', `Ürün oluşturuluyor ve ${storeCount} gönderiliyor…`]
        : editProductId ? ['Ürün Kaydediliyor', 'Ürün değişiklikleri kaydediliyor…'] : ['Ürün Oluşturuluyor', 'Ürün oluşturuluyor…']
    setError(undefined); setFieldErrors({}); setNotice(''); showFeedback(pendingTitle, pendingMessage, 'info', { persistent: true, key: productSaveToastKey }); setSubmitting(true); let productCreated: Product | undefined; let familyMediaChanged = false
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
      // The manual Web Color is sent apart (trendyolWebColorValueId) so it never becomes the product's Renk on other marketplaces.
      const colorGlobalAttributes = webColorRequirement && !variantAttributeIds.includes(webColorRequirement.attributeId)
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
          warrantyMonths: Number(form.warrantyMonths || 0),
          deliveryDays: productDeliveryDays(form.deliveryDays),
          vatRate: Number(form.vatRate || 0),
          vatInclusion: form.vatIncluded || 'INCLUDED',
          trendyolWebColorValueId: !webColorAutoEnabled && manualWebColorValueId ? manualWebColorValueId : '00000000-0000-0000-0000-000000000000',
          ...(shouldPersistAttributes ? { attributes: globalAttributes } : {}),
          variantIdsToDelete: removedVariantIds.filter(id => existingVariantIds.has(id) && !rows.some(row => row.key === id)),
          variantsToCreate: rows.filter(row => !existingVariantIds.has(row.key)).map(variantPayload),
          variantUpdates: rows.filter(row => existingVariantIds.has(row.key)).map(row => ({
            id: row.key,
            sku: row.sku,
            barcode: row.barcode || null,
            modelCode: modelCodeForRow(row.key),
            // Only a cost the user changed is sent, with the cost it was loaded with; an unsent (null) cost is left as
            // it is on the server, and a cost changed there meanwhile refuses the save (409).
            ...editorVariantCostUpdate(row),
            ...editorVariantDesiUpdate(calculateDesi ? desi || 1 : Number(form.desi) || 1, initialEditDesi.current, editProduct.variants.find(variant => variant.id === row.key)?.desi),
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
        product = await sendWithVersionedIdempotencyKey(productCreateKey, key => hubApi<Product>('/products', { method: 'POST', headers: { 'Idempotency-Key': key }, body: JSON.stringify({ title: form.title, status: form.status, description: safeDescription, brandId: form.brandId || null, categoryId: form.categoryId || null, defaultListPrice: Number(form.listPrice || 0), defaultSalePrice: Number(form.salePrice || 0), warrantyMonths: Number(form.warrantyMonths || 0) || null, deliveryDays: productDeliveryDays(form.deliveryDays), vatRate: Number(form.vatRate || 0), vatInclusion: form.vatIncluded || 'INCLUDED', trendyolWebColorValueId: !webColorAutoEnabled && manualWebColorValueId ? manualWebColorValueId : null, attributes: globalAttributes, variants: rows.map(variantPayload) }) }))
      }
      productCreated = product; const completed: string[] = []; const warnings: string[] = []
      const publications: MarketplacePublishOutcome[] = []
      const publicationErrors: Array<{ platformName: string; message: string }> = []
      const updates: MarketplaceUpdateOutcome[] = []
      // The steps after the product (media, stock, channel prices, publication) can take a while; the progress toast names them.
      if (!editProductId) showFeedback('Ürün Oluşturuldu', 'Ürün oluşturuldu; görseller, stok ve fiyatlar kaydediliyor…', 'info', { persistent: true, key: productSaveToastKey })
      const initialProductMediaUrls = initialEditMediaUrl.current.split(/\r?\n|[;|]/u).map(url => url.trim()).filter(Boolean)
      const productMediaChanged = editProductId
        ? !mediaRefsEqual(mediaUrls, initialProductMediaUrls) && !(familyMediaOrderDirty && mediaRefsSameSet(mediaUrls, initialProductMediaUrls))
        : mediaUrls.length > 0
      if (productMediaChanged) await hubApi('/files/product-media-reconcile', { method: 'PUT', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ productId: product.id, variantId: null, items: mediaUrls.map((url, sortOrder) => ({ url, sortOrder })), altText: form.title }) })
      // Uploads and media writes on one product stay one at a time: each raises the product's version, which the
      // server checks. Each new file is uploaded once, here; the variants below reference its stored copy.
      const progressTitle = editProductId ? pendingTitle : 'Ürün Oluşturuldu'
      const showProgress = (message: string) => showFeedback(progressTitle, message, 'info', { persistent: true, key: productSaveToastKey })
      const storedUrlByFileId = new Map<string, string>()
      for (const [fileIndex, pendingMedia] of mediaFiles.entries()) {
        showProgress(mediaSaveProgressMessage('upload', fileIndex, mediaFiles.length))
        const data = new FormData(); data.set('file', pendingMedia.file); data.set('productId', product.id); data.set('mediaRole', mediaUrls.length + fileIndex === 0 ? 'PRIMARY' : 'GALLERY'); data.set('sortOrder', String(mediaUrls.length + fileIndex)); data.set('altText', form.title)
        const uploaded = await hubApi<{ fileAssetId?: string }>('/files/product-media', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: data })
        if (uploaded?.fileAssetId) storedUrlByFileId.set(pendingMedia.id, storedProductMediaUrl(uploaded.fileAssetId))
      }
      const rowsBySku = new Map(rows.map(row => [row.sku.trim().toLocaleUpperCase('tr-TR'), row]))
      const pendingFileIds = new Set(mediaFiles.map(item => item.id))
      const variantMediaWrites = product.variants.flatMap(variant => {
        const row = rowsBySku.get(variant.sku.trim().toLocaleUpperCase('tr-TR'))
        if (!row) return []
        const originalMediaRefs = initialEditVariantMediaRefs.current[variant.id]
        if (editProductId && originalMediaRefs && mediaRefsEqual(row.mediaRefs, originalMediaRefs)) return []
        return [{ variant, row }]
      })
      for (const [writeIndex, { variant, row }] of variantMediaWrites.entries()) {
        if (mediaFiles.length) showProgress(mediaSaveProgressMessage('variants', writeIndex, variantMediaWrites.length))
        const altText = `${form.title} · ${row.optionSignature}`
        const plan = variantMediaReconcilePlan(row.mediaRefs, storedUrlByFileId, pendingFileIds)
        await hubApi('/files/product-media-reconcile', { method: 'PUT', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ productId: product.id, variantId: variant.id, items: plan.items, altText }) })
        for (const upload of plan.uploads) {
          const file = mediaFiles.find(item => item.id === upload.fileId)?.file
          if (file) { const data = new FormData(); data.set('file', file); data.set('productId', product.id); data.set('variantId', variant.id); data.set('mediaRole', upload.sortOrder === 0 ? 'PRIMARY' : 'GALLERY'); data.set('sortOrder', String(upload.sortOrder)); data.set('altText', altText); await hubApi('/files/product-media', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: data }) }
        }
      }
      if (editProductId && familyMediaOrderDirty) {
        const result = await hubApi<{ changedProductCount: number }>('/files/product-media-family-reorder', {
          method: 'PUT',
          headers: { 'Idempotency-Key': newIdempotencyKey() },
          body: JSON.stringify({ productId: product.id, urlsInOrder: visibleMediaUrls })
        })
        familyMediaChanged = result.changedProductCount > 0
        setFamilyMediaOrderDirty(false)
      }
      if (editProductId ? productMediaChanged || mediaFiles.length > 0 : mediaUrls.length || mediaFiles.length) completed.push('görseller')
      if (familyMediaChanged) completed.push('diğer renklerin görsel sırası')
      const targetConnectionIds = createOnly ? [] : platformUpdate?.connectionIds ?? selectedChannelIds
      // Only rows whose stock the user changed are sent, as absolute values against the stock loaded into the form;
      // a stock changed meanwhile (order, return, another tab) is refused for that row instead of being overwritten.
      const stockResult = await applyVariantStockSets(editorStockUpdates(rows, product.variants), update => setVariantOnHand(update, productToEdit.data ? 'Ürün düzenleme stoğu' : 'İlk ürün stoğu'))
      if (stockResult.failed.length) warnings.push(`Stok kaydedilemedi: ${stockResult.failed.map(item => `${item.sku}: ${item.message}`).join(' · ')}`)
      // Every (variant, store) price is its own row, so they are written a few at a time instead of one by one.
      const offerWrites = product.variants.flatMap(variant => {
        const row = rowsBySku.get(variant.sku.trim().toLocaleUpperCase('tr-TR'))
        return row ? targetConnectionIds.map(connectionId => ({ variant, row, connectionId })) : []
      })
      let offersWritten = 0
      await mapWithConcurrency(offerWrites, channelOfferWriteConcurrency, async ({ variant, row, connectionId }) => {
        const pricing = channelPriceDraft(connectionId)
        const platform = row.platformStatuses?.find(item => item.connectionId === connectionId)
        const variantPricing = variantChannelPricing[`${row.key}:${connectionId}`]
        const offerCommand = { connectionId, variantId: variant.id, listPrice: platformUpdate ? row.listPrice : Number(variantPricing?.listPrice ?? pricing.listPrice), salePrice: platformUpdate ? row.salePrice : Number(variantPricing?.salePrice ?? pricing.salePrice), currency: platform?.currency ?? form.currency ?? 'TRY', vatRate: Number(form.vatRate || platform?.vatRate || 0), vatInclusion: form.vatIncluded || platform?.vatInclusion || 'INCLUDED', roundingMode: platform?.roundingMode ?? 'HALF_EVEN', safetyStock: Number(form.safetyStock || platform?.safetyStock || 0), status: 'ACTIVE', reason: platformUpdate ? 'Ürün platform güncellemesi' : 'İlk ürün fiyatı' }
        if (platformUpdate && platform?.offerId) {
          if (platform.offerVersion == null) throw new Error(`${row.sku}: platform fiyat sürümü eksik; fiyat güncellenmedi.`)
          await hubApi(`/channel-offers/${platform.offerId}`, { method: 'PATCH', headers: { 'If-Match': `"v${platform.offerVersion}"` }, body: JSON.stringify(offerCommand) })
        } else {
          await hubApi('/channel-offers', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify(offerCommand) })
        }
        offersWritten++
        if (offerWrites.length > channelOfferWriteConcurrency) showProgress(mediaSaveProgressMessage('prices', offersWritten, offerWrites.length))
      })
      // An edit names the stock only when a changed stock was written; a new product names the stock it opened with.
      if (editProductId ? stockResult.saved.length > 0 : rows.some(row => row.stock > 0)) completed.push('stok')
      if (!editProductId && targetConnectionIds.length) completed.push('kanal fiyatları')
      const publicationConnectionIds = productPublicationTargets(saveAndStay, targetConnectionIds)
      const connectionPlatformName = (connectionId: string) => publishPlatformName(connections.data?.items.find(item => item.id === connectionId)?.platformCode ?? 'Pazaryeri')
      for (const [connectionIndex, connectionId] of publicationConnectionIds.entries()) {
        const platformName = connectionPlatformName(connectionId)
        const progress = publicationConnectionIds.length > 1 ? ` (${connectionIndex + 1}/${publicationConnectionIds.length})` : ''
        showFeedback(platformUpdate ? 'Güncelleme Kuyruğa Alınıyor' : 'Yayın Kuyruğa Alınıyor', platformUpdate ? `${platformName}: güncelleme işleri kuyruğa alınıyor${progress}…` : `${platformName}: yayın işleri kuyruğa alınıyor${progress}…`, 'info', { persistent: true, key: productSaveToastKey })
        const update: MarketplaceUpdateOutcome = { platformName, queued: { productInformation: false, priceInventory: false } }
        try {
          if (platformUpdate) {
            updates.push(update)
            // Only what this marketplace takes of the pick goes: the product fields it carries, and the price and/or stock.
            const plan = platformUpdatePlan(connections.data?.items.find(item => item.id === connectionId)?.platformCode ?? '', platformUpdate.fields)
            if (plan.productFields.length) {
              await hubApi<AcceptedJob>(`/products/${product.id}/update-jobs`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ connectionId, fields: plan.productFields }) })
              update.queued.productInformation = true
            }
            if (plan.price || plan.stock) {
              await hubApi<AcceptedJob>(`/products/${product.id}/connections/${connectionId}/price-inventory-sync-jobs`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ price: plan.price, stock: plan.stock }) })
              update.queued.priceInventory = true
              update.priceInventoryParts = { price: plan.price, stock: plan.stock }
            }
            await client.invalidateQueries({ queryKey: ['publication-status', product.id, connectionId] })
            continue
          }
          // Every colour card of the family (same model code) goes with the opened one; a card already live or still
          // being published on this marketplace is left as it is. One card's failure does not stop the others.
          // The cards are queued in the panel's colour order (not the opened card first), so a marketplace that takes
          // them as they come lists the colours as the products page does.
          const familyIds = familyCardsInPanelOrder([product.id, ...(productToEdit.data?.familyProductIds ?? [])], product.variants, product.id)
          // The same reason on several cards (a connection setting missing) is said once, with how many cards it stopped.
          const familyFailures = new Map<string, number[]>()
          const platformCode = connections.data?.items.find(item => item.id === connectionId)?.platformCode
          const outcome: MarketplacePublishOutcome = { platformName, platformCode: platformCode ?? '', familyCards: familyIds.length, openedCard: familyIds.indexOf(product.id), enqueuedCards: 0, enqueuedVariants: 0, skipped: { live: 0, running: 0, matched: 0 }, failures: familyFailures }
          publications.push(outcome)
          for (const [familyIndex, familyProductId] of familyIds.entries()) {
            try {
              // n11 sends only a card's variants not yet live there, so a partly refused n11 card goes again; elsewhere a
              // card with a matched variant is left as it is (its publication would send the whole card).
              const cardVariants = product.variants.filter(variant => (variant.productId ?? product.id) === familyProductId)
              // A card with a variant the marketplace no longer knows (Bulunamadı, Kısmen yayında) goes again.
              const cardLinks = publishCardVariantLinks(cardVariants.map(variant => ({ platformStatuses: rowsBySku.get(variant.sku.trim().toLocaleUpperCase('tr-TR'))?.platformStatuses })), connectionId)
              const matchedCount = cardLinks.linked
              const hasMissingVariant = cardLinks.missing > 0
              const resendsRefused = platformCode === 'N11'
              if (!hasMissingVariant && matchedCount > 0 && (!resendsRefused || matchedCount === cardVariants.length)) { outcome.skipped.matched++; continue }
              const status = await hubApi<PublicationStatus>(`/products/${familyProductId}/publication-status/${connectionId}`).catch(() => null)
              // A PTT AVM family card shown as "Tekrar Deneyin" goes again; the API closes its waiting approval checks.
              const pttAvmFamily = familyIds.length > 1 || cardVariants.length > 1
              if (status && isSkippedByPublish(platformCode, status.actualStatus, status.lastJobStatus, pttAvmFamily, hasMissingVariant)) {
                if (isPublicationLive(status.actualStatus)) outcome.skipped.live++
                else outcome.skipped.running++
                continue
              }
              let listingProfileVersion: number | undefined
              try {
                const listingProfile = await hubApi<{ version: number }>(`/products/${familyProductId}/listing-profiles/${connectionId}`)
                listingProfileVersion = listingProfile.version
              } catch (reason) {
                if (!(reason instanceof ApiRequestError) || reason.status !== 404) throw reason
              }
              await hubApi(`/products/${familyProductId}/listing-profiles/${connectionId}`, { method: 'PUT', headers: listingProfileVersion == null ? {} : { 'If-Match': `"v${listingProfileVersion}"` }, body: JSON.stringify({ titleOverride: null, descriptionOverride: null, externalCategoryId: null, externalBrandId: null, deliveryTimeDays: null, enabled: true }) })
              await client.invalidateQueries({ queryKey: ['listing-profile', familyProductId, connectionId] })
              await hubApi<AcceptedJob>(`/products/${familyProductId}/publication-jobs`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ connectionId }) })
              await client.invalidateQueries({ queryKey: ['publication-status', familyProductId, connectionId] })
              outcome.enqueuedCards++
              outcome.enqueuedVariants += resendsRefused ? cardVariants.length - matchedCount : cardVariants.length
            } catch (reason) {
              const failure = reason instanceof Error ? reason.message : 'Yayın işi oluşturulamadı.'
              familyFailures.set(failure, [...(familyFailures.get(failure) ?? []), familyIndex])
            }
          }
          continue
        } catch (reason) {
          const failure = reason instanceof Error ? reason.message : platformUpdate ? 'Güncelleme işi oluşturulamadı.' : 'Yayın işi oluşturulamadı.'
          if (platformUpdate) update.error = failure
          else publicationErrors.push({ platformName, message: failure })
        }
      }
      const result = productSaveFeedback({
        created: !editProductId,
        productChanged: productChangedAtSubmit,
        savedParts: completed,
        versionRetried: productVersionRetryUsed,
        saveWarnings: warnings,
        publishRequested: !saveAndStay && !createOnly,
        publications,
        publicationErrors,
        updates: platformUpdate ? updates : undefined
      })
      const message = result.lines.join(' ')
      setRemovedVariantIds([])
      setNotice(createOnly ? 'Ürün oluşturuldu. Ürün sayfası açılıyor.' : message)
      // The saved form is the new clean state; the refetch below may refill it from the server.
      allowReseedUntil.current = Date.now() + 10_000
      setSnapshotRequest(value => value + 1)
      await client.invalidateQueries({ queryKey: ['products'] })
      if (editProductId) {
        await client.invalidateQueries({ queryKey: ['product', editProductId] })
        await productToEdit.refetch()
      }
      // One line per marketplace; a refused marketplace stays on screen until it is closed, so it is not lost among
      // later toasts.
      if (createOnly) operationToasts.dismiss(productSaveToastKey)
      else setFeedback({ message, kind: result.kind, persistent: result.persistent, title: result.title, lines: result.lines, durationMs: result.durationMs, key: productSaveToastKey, context: productCreated ? { subject: productNotificationSubject({ id: productCreated.id, name: form.title, modelCode: form.modelCode }) } : undefined })
      // A new product or a copy opens on its own page once created, whichever button saved it: there it saves as an
      // edit and its publication progress shows. A publish opens the publication step; the copy source is dropped.
      if (productCreated && !editProductId) {
        // The save toast of a publish already went to the history; a bare create (its toast closed) is recorded here.
        if (createOnly) appendNotification({ title: 'Ürün Oluşturuldu', message: 'Ürün kataloğa eklendi.', kind: 'success', subject: productNotificationSubject({ id: productCreated.id, name: form.title, modelCode: form.modelCode }) })
        leavingAfterSave.current = true
        navigate(`/products/${productCreated.id}`, { replace: true, state: createOnly ? null : { wizardStep: 2 } })
      }
    } catch (reason) {
      const message = reason instanceof Error ? reason.message : 'Kayıt tamamlanamadı.'
      const feedbackMessage = productCreated ? `Ürün kaydedildi ancak sonraki işlem tamamlanamadı: ${message}` : message
      setError(reason); setNotice(feedbackMessage)
      setFeedback({ message: feedbackMessage, kind: 'error', persistent: true, title: productCreated ? 'İşlem Yarım Kaldı' : editProductId ? 'Ürün Kaydedilemedi' : 'Ürün Oluşturulamadı', key: productSaveToastKey, context: productCreated ? { subject: productNotificationSubject({ id: productCreated.id, name: form.title, modelCode: form.modelCode }) } : undefined })
      if (!productCreated) setFieldErrors(editorFieldErrors(reason))
      if (productCreated && !editProductId) {
        leavingAfterSave.current = true
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
  // Every active connection's barcode and stock code rules: a product goes to all of them with one pair of codes.
  const codeRulePlatforms = useMemo(() => [...new Set((connections.data?.items ?? []).filter(connection => connection.status === 'ACTIVE' || connection.status === 'VERIFIED').map(connection => connection.platformCode))], [connections.data])
  const variantCodeIssues = useMemo(() => new Map(variantRows.map(row => [row.key, platformCodeIssues(codeRulePlatforms, row.barcode, row.sku)])), [codeRulePlatforms, variantRows])
  const variantCodeIssueSummary = useMemo(() => {
    const rowsWithIssues = [...variantCodeIssues.values()].filter(issues => issues.length > 0)
    const platforms = [...new Set(rowsWithIssues.flat().map(issue => platformCodeNames[issue.platformCode] ?? issue.platformCode))]
    return rowsWithIssues.length ? { rows: rowsWithIssues.length, platforms } : null
  }, [variantCodeIssues])
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
  // The card's own saved pictures the draft no longer holds ("Tümünü Temizle", a removed tile) go on the next save; the
  // family list still carries them until then, so they leave the gallery now instead of after Kaydet.
  const draftMediaKeys = new Set(mediaUrls.map(mediaImageKey))
  const pendingRemovalMediaKeys = new Set(editProductId ? (productToEdit.data?.mediaUrls ?? []).map(mediaImageKey).filter(key => !draftMediaKeys.has(key)) : [])
  const familyOnlyMediaItems = familyMediaItems.filter(item => !draftMediaKeys.has(mediaImageKey(item.url)) && !pendingRemovalMediaKeys.has(mediaImageKey(item.url)))
  const defaultFamilyOrder = productToEdit.data?.familyOrderedMediaUrls?.length
    ? productToEdit.data.familyOrderedMediaUrls
    : [...mediaUrls, ...familyMediaItems.map(item => item.url)]
  const visibleMediaUrls = mediaUrlsInPreferredOrder(uniqueMediaUrls(defaultFamilyOrder), familyMediaOrder.length ? familyMediaOrder : defaultFamilyOrder)
    .filter(url => !pendingRemovalMediaKeys.has(mediaImageKey(url)))
  const mediaChoices: ProductMediaOption[] = ([...new Set([...mediaUrls, ...familyMediaUrls, ...assignedMediaUrls])].map((url, index) => ({ value: `url|${url}`, label: `Görsel ${String(index + 1).padStart(2, '0')}`, url })) as ProductMediaOption[]).concat(mediaFiles.map(item => ({ value: `file|${item.id}`, label: `Yüklenen Görsel · ${item.file.name}`, file: item.file })))
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
    { title: 'Varyant Bilgileri', detail: hasVariantData ? 'Varyant yapısı yayınlanmaya hazır.' : 'Seçilen seçenekler için varyant satırlarını oluşturun.', ok: hasVariantData, shopifyOk: hasVariantData, shopifyDetail: 'Shopify yayını için en az bir stok kodlu varyant oluşturun.' },
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
  if (duplicateProductId && productToCopy.isError) return <Page title="Yeni Ürün Ekle" eyebrow="Katalog"><ErrorToast title="Kopyalanacak Ürün Yüklenemedi" error={productToCopy.error} /><Link className="button-link secondary" to="/products">Ürünlere Dön</Link></Page>

  return <EditorShell publishOnly={publishOnly} className={`product-add-page${editProductId ? ' product-edit-page' : ''}`} title={editProductId ? productToEdit.data?.title || 'Ürün Düzenle' : 'Yeni Ürün Ekle'} eyebrow={editProductId ? 'Ürün Düzenle' : 'Katalog'} meta={editProductId && productToEdit.data?.modelCode ? <p className="product-page-heading-meta">Model Kodu: <b>{productToEdit.data.modelCode}</b></p> : undefined}>
    {!publishOnly && <UnsavedChangesGuard when={editorDirty} allowNavigationRef={leavingAfterSave} />}
    {staleProductVersion != null && <div className="product-stale-banner" role="alert"><span><strong>Ürün başka bir işlemle güncellendi</strong> (güncel sürüm v{staleProductVersion}). Kaydedilmemiş değişiklikleriniz korunuyor; kaydederseniz sunucu sürüm çakışmasını bildirir.</span><button type="button" className="secondary" onClick={reloadEditedProduct}>Güncel Ürünü Yükle</button></div>}
    {variantPlatformPricing && selectedVariantPlatformRow && <BulkVariantPlatformPricingModal row={selectedVariantPlatformRow} rows={variantRows} platforms={selectedVariantPlatformRow.platformStatuses?.length ? selectedVariantPlatformRow.platformStatuses : variantPricingPlatforms} savedDrafts={variantChannelPricing} productName={form.title} modelCode={form.modelCode} filterGroups={variantFilterGroups.filter(group => isColorOptionName(group.name) || isSizeOptionName(group.name))} matchesFilterValue={rowMatchesVariantMediaValue} saving={variantPlatformPricingSaving} onClose={() => setVariantPlatformPricing(null)} onSave={drafts => void saveVariantPlatformPricingMatrix(drafts)} />}
    {!publishOnly && <div className="product-add-wizardbar"><div className="product-add-stepper"><div className="product-add-progress" role="tablist" aria-label={editProductId ? 'Ürün düzenleme adımları' : 'Ürün ekleme adımları'}><button type="button" className={wizardStep === 1 ? 'active' : ''} role="tab" aria-selected={wizardStep === 1} onClick={() => setWizardStep(1)}><span>1</span><strong>Ürün Bilgileri ve Varyantlar</strong></button><i aria-hidden="true" /><button type="button" className={wizardStep === 2 ? 'active' : ''} role="tab" aria-selected={wizardStep === 2} onClick={() => setWizardStep(2)}><span>2</span><strong>Yayınlama</strong></button></div></div></div>}<form id="product-creation-form" className="product-creation-workspace product-add-workspace" data-wizard-step={wizardStep} onSubmit={submit} onInvalidCapture={handleInvalid} onChange={dismissProductError} noValidate>
    <div className="product-top-layout">
      <section className="panel product-step-card product-basics-card">
        <div className="editor-section-title"><span>1</span><div><h2>Temel Ürün Bilgileri</h2><p>Ürün kartının temel başlığı ve katalog bilgileri.</p></div></div>
        <div className="product-step-grid product-basics-grid">
          <label className="product-title-field">Ürün Adı<input value={form.title} aria-invalid={fieldErrors.title ? true : undefined} onChange={event => updateField('title', event.target.value)} required maxLength={320} /><EditorFieldError message={fieldErrors.title} /></label>
          <label>Satış Durumu<select value={form.status} onChange={event => updateField('status', event.target.value)}><option value="ACTIVE">Satışa Açık</option><option value="ARCHIVED">Satışa Kapalı</option><option value="DRAFT">Taslak</option></select></label>
          <label className="product-brand-field">Marka<select value={form.brandId} onChange={event => updateField('brandId', event.target.value)}><option value="">Marka Seçin</option>{activeBrands.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</select><EditorFieldError message={fieldErrors.brandId} /></label>
          <label className="product-category-field">Panel Kategorisi<select aria-label="Panel kategorisi" value={form.categoryId} onChange={event => { updateField('categoryId', event.target.value); setAttributeSelections({}); setAttributeTextValues({}); setVariantAttributeIds([]); setWebColorAutoEnabled(true); setManualWebColorValueId(''); if (!editProductId) setVariantRows([]) }}><option value="">Kategori Seçin</option>{leafCategories.map(item => <option key={item.id} value={item.id}>{item.path}</option>)}</select><EditorFieldError message={fieldErrors.categoryId} /></label>
          <label>Model Kodu<input className="technical-field model-code-value" value={form.modelCode} onChange={event => updateField('modelCode', event.target.value)} /></label>
          <div className="product-identifiers-grid">
            <label title={variantRows.length > 1 ? 'Varyantlı ürünlerde varyanta yazılır' : undefined}>{automaticBarcodeGeneration ? 'Stok Kodu (varyantlarda manuel)' : 'Stok Kodu'}<input className="technical-field sku-value" value={variantRows.length > 1 ? '' : form.baseSku} onChange={event => updateField('baseSku', event.target.value)} placeholder={variantRows.length > 1 ? '' : automaticBarcodeGeneration ? 'Varyant satırlarından girin' : 'RAV-BLUZ'} disabled={automaticBarcodeGeneration || variantRows.length > 1} title={variantRows.length > 1 ? 'Varyantlı ürünlerde varyanta yazılır' : undefined} /></label>
            <label title={variantRows.length > 1 ? 'Varyantlı ürünlerde varyanta yazılır' : undefined}>{automaticBarcodeGeneration ? 'Tek Ürün Barkodu (otomatik oluşturuluyor)' : 'Barkod'}<input className="technical-field barcode-value" value={variantRows.length > 1 ? '' : form.barcode} onChange={event => updateField('barcode', event.target.value)} placeholder={variantRows.length > 1 ? '' : automaticBarcodeGeneration ? 'Model kodu ve sıra numarasından oluşturulur' : 'Varyantsız üründe kullanılır'} disabled={automaticBarcodeGeneration || variantRows.length > 1} title={variantRows.length > 1 ? 'Varyantlı ürünlerde varyanta yazılır' : undefined} /></label>
            <label className="desi-input-field">Desi<span className="desi-inline-control"><input value={form.desi} onChange={event => { setCalculateDesi(false); updateField('desi', event.target.value) }} type="number" min="0.01" step="0.01" required /><button type="button" className="secondary" onClick={() => setDesiCalculatorOpen(true)}>Hesapla</button></span></label>
            <label className="warranty-input-field">Garanti Süresi (Ay)<input value={form.warrantyMonths} onChange={event => updateField('warrantyMonths', event.target.value)} type="number" min="0" max="240" step="1" inputMode="numeric" placeholder="Garanti yok" /></label>
            <label className="delivery-days-input-field" title="Siparişin kargoya teslim süresi; pazaryerlerine teslimat (sevkiyat) süresi olarak gider.">Teslimat Süresi (Gün)<input value={form.deliveryDays} onChange={event => updateField('deliveryDays', event.target.value)} type="number" min="1" max="30" step="1" inputMode="numeric" required /></label>
          </div>
          <div className="wide product-description-field"><span>Açıklama</span><RichTextEditor value={form.description} onChange={value => updateField('description', value)} /><EditorFieldError message={fieldErrors.description} /></div>
        </div>
      </section>
      <div className="product-top-sidebar">
        <section className="panel product-step-card product-pricing-card">
          <div className="editor-section-title"><span>2</span><div><h2>Fiyat, Stok ve Vergi</h2><p>Başlangıç değerleri varyantlara uygulanır.</p></div></div>
          <div className="product-step-grid">
            {/* Like Stok Kodu and Barkod: a card with variants prices each variant, so these stay grey and empty. */}
            <label>Liste Fiyatı<input value={variantRows.length > 1 ? '' : form.listPrice} disabled={variantRows.length > 1} title={variantRows.length > 1 ? 'Varyantlı üründe fiyat varyant tablosundan girilir.' : undefined} onChange={event => updateField('listPrice', event.target.value)} type="number" min="0" step="0.01" /></label>
            <label>Satış Fiyatı<input value={variantRows.length > 1 ? '' : form.salePrice} disabled={variantRows.length > 1} title={variantRows.length > 1 ? 'Varyantlı üründe fiyat varyant tablosundan girilir.' : undefined} onChange={event => updateField('salePrice', event.target.value)} type="number" min="0" step="0.01" /><EditorFieldError message={fieldErrors.prices} /></label>
            {variantRows.length <= 1 && <label>Maliyet<input value={form.costPrice} onChange={event => { updateField('costPrice', event.target.value); if (variantRows.length === 1) updateVariantRow(variantRows[0].key, 'costPrice', event.target.value) }} type="number" min="0" step="0.01" /></label>}
            <label>Para Birimi<select value={form.currency} onChange={event => updateField('currency', event.target.value)}><option>TRY</option><option>USD</option><option>EUR</option></select></label>
            <label><span>KDV Oranı<b className="required-marker" aria-hidden="true">*</b></span><select value={form.vatRate} required aria-required="true" aria-invalid={fieldErrors.vatRate ? true : undefined} onChange={event => { updateField('vatRate', event.target.value); if (event.target.value) setFieldErrors(current => ({ ...current, vatRate: undefined })) }}>{form.vatRate === '' && <option value="" disabled>Seçin</option>}{form.vatRate !== '' && !TURKISH_VAT_RATES.includes(form.vatRate) && <option value={form.vatRate}>%{form.vatRate}</option>}{TURKISH_VAT_RATES.map(rate => <option key={rate} value={rate}>%{rate}</option>)}</select><EditorFieldError message={fieldErrors.vatRate} /></label>
            <label>KDV Dahil mi<select value={form.vatIncluded} onChange={event => updateField('vatIncluded', event.target.value)}><option value="INCLUDED">Evet</option><option value="EXCLUDED">Hayır</option></select></label>
            <label title={showVariantStockTotal || variantRows.length > 1 ? 'Varyantların toplam stoğu' : undefined}>{showVariantStockTotal || variantRows.length > 1 ? 'Toplam Stok' : 'Stok'}<input value={showVariantStockTotal || variantRows.length > 1 ? String(variantStockTotal) : form.initialStock} onChange={event => updateField('initialStock', event.target.value)} type="number" min="0" step="1" readOnly={showVariantStockTotal || variantRows.length > 1} /></label>
            <label>Güvenlik Stoğu<input value={form.safetyStock} onChange={event => updateField('safetyStock', event.target.value)} type="number" min="0" step="1" /></label>
          </div>
          {platformCards.length > 0 && <button type="button" className={`marketplace-pricing-open${marketplacePricingOpen ? ' is-open' : ''}`} aria-expanded={marketplacePricingOpen} aria-controls="marketplace-pricing-panel" onClick={() => setMarketplacePricingOpen(open => !open)}>
            <span className="marketplace-pricing-open-title"><strong>Pazaryerlerine Özel Fiyat</strong><UiIcon name="chevronDown" size={16} /></span>
            {/* Only the marketplaces with their own price are named; the rest take the main price. */}
            {(() => { const custom = platformCards.filter(card => card.connection.id in channelPricing); return custom.length ? <span className="marketplace-pricing-open-states">{custom.map(card => <span key={card.connection.id} className="marketplace-pricing-state is-on"><i aria-hidden="true" />{card.name}</span>)}</span> : <small>Tüm pazaryerleri ana fiyatla satılır.</small> })()}
          </button>}
          {marketplacePricingOpen && <div className="marketplace-pricing-inline" id="marketplace-pricing-panel"><div className="marketplace-pricing-bar-list">{platformCards.map(card => { const pricing = channelPricingInputs(channelPricing[card.connection.id]); const selected = selectedChannelIds.includes(card.connection.id); const customPrice = card.connection.id in channelPricing; return <fieldset className={`marketplace-pricing-bar${selected ? ' selected' : ''}`} key={card.connection.id} aria-label={`${card.name} pazaryeri fiyatlandırması`}><div className="marketplace-pricing-bar-heading"><span className={`publish-platform-mark ${card.tone}`}><img className={`publish-platform-logo ${platformLogoClass(card.connection.platformCode)}`} src={platformLogoSource(card.connection.platformCode) ?? '/platforms/trendyol.webp'} alt="" aria-hidden="true" /></span><span><strong>{card.name}</strong><small>{customPrice ? 'Özel fiyat açık' : 'Ana fiyatla satılır'}</small></span><label className="marketplace-pricing-switch" title="Bu pazaryeri için özel fiyat"><input type="checkbox" role="switch" aria-label={`${card.name} için özel fiyat`} checked={customPrice} onChange={() => toggleCustomChannelPrice(card.connection.id)} /></label></div><div className="marketplace-pricing-fields"><label>Liste Fiyatı<input value={pricing.listPrice} disabled={!customPrice} onChange={event => updateChannelPrice(card.connection.id, 'listPrice', event.target.value)} type="number" min="0" step="0.01" /></label><label>Satış Fiyatı<input value={pricing.salePrice} disabled={!customPrice} onChange={event => updateChannelPrice(card.connection.id, 'salePrice', event.target.value)} type="number" min="0" step="0.01" /></label></div></fieldset>})}</div></div>}
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
    {(() => {
      const row = optionEditRowKey ? variantRows.find(item => item.key === optionEditRowKey) : null
      if (!row) return null
      return <VariantOptionEditDialog
        label={displayVariantOptionSignature(row.optionSignature, row.options)}
        fields={rowOptionEntries(row).map(entry => ({ ...entry, values: optionAttributeFor(entry.name)?.attribute.values.map(value => ({ id: value.id, value: cleanOptionValue(value.value) })) ?? null }))}
        sameValueCount={(name, value) => variantRows.filter(item => rowHasOptionValue(item, name, value)).length}
        onClose={() => setOptionEditRowKey(null)}
        onSave={(changes, applyToAll) => applyVariantOptionChanges(row.key, changes, applyToAll)} />
    })()}
    {(marketplaceCodesOpen || marketplaceCodesVariant) && editProductId && <MarketplaceCodesDialog productId={editProductId} rows={variantRows.filter(row => !marketplaceCodesVariant || row.key === marketplaceCodesVariant).map(row => ({ variantId: row.key, label: displayVariantOptionSignature(row.optionSignature, row.options) }))} onClose={() => { setMarketplaceCodesOpen(false); setMarketplaceCodesVariant(null) }} />}
    {bulkOptionSyncOpen && <div className="workspace-modal-backdrop bulk-option-sync-backdrop" role="presentation" onMouseDown={() => setBulkOptionSyncOpen(false)}><DialogSurface as="section" onClose={() => setBulkOptionSyncOpen(false)} className="workspace-modal bulk-option-sync-modal" aria-labelledby="bulk-option-sync-title" onMouseDown={event => event.stopPropagation()}><header><div><p className="eyebrow">Ürün Seçenekleri</p><h2 id="bulk-option-sync-title">Toplu Eşitleme</h2><p>Seçenek gruplarındaki işaretli değerleri varyant satırlarına ekleyin.</p></div><button type="button" className="modal-close" onClick={() => setBulkOptionSyncOpen(false)} aria-label="Pencereyi kapat"><UiIcon name="close" /></button></header><div className="bulk-option-sync-body"><fieldset><legend>Eşitlenecek Gruplar</legend><div className="bulk-option-sync-options">{bulkSyncOptionRequirements.map(item => { const selectedCount = attributeSelections[item.attributeId]?.length ?? 0; const checked = bulkOptionSyncSelection.includes(item.attributeId); const label = isColorOptionName(item.attribute.name) ? 'Renkler' : 'Bedenler'; return <label className={`bulk-option-sync-option${checked ? ' is-selected' : ''}`} key={item.attributeId}><input type="checkbox" checked={checked} disabled={!selectedCount} onChange={() => setBulkOptionSyncSelection(current => checked ? current.filter(id => id !== item.attributeId) : [...current, item.attributeId])} /><span><strong>{label}</strong><small>{selectedCount} seçili değer · {item.attribute.name}</small></span></label> })}</div></fieldset><p className="bulk-option-sync-note">Hiçbir grup seçmezseniz listelenen tüm gruplar birlikte eşitlenir. Tek grup seçildiğinde diğer etkin seçenek ekseni korunur; mevcut varyant satırlarındaki stok, fiyat, barkod ve görseller değiştirilmez.</p></div><footer><button type="button" className="secondary" onClick={() => setBulkOptionSyncOpen(false)}>Vazgeç</button><button type="button" onClick={syncOptionGroups} disabled={!bulkSyncOptionRequirements.length}>{bulkOptionSyncSelection.length ? 'Seçilenleri Eşitle' : 'Tümünü Eşitle'}</button></footer></DialogSurface></div>}
    {desiCalculatorOpen && <div className="workspace-modal-backdrop" role="presentation" onMouseDown={() => setDesiCalculatorOpen(false)}><DialogSurface as="section" onClose={() => setDesiCalculatorOpen(false)} className="workspace-modal desi-calculator-modal" aria-labelledby="desi-calculator-title" onMouseDown={event => event.stopPropagation()}><header><div><h2 id="desi-calculator-title">Desi Hesapla</h2><p>En × Boy × Yükseklik / 3000 formülü kullanılır.</p></div><button type="button" className="modal-close" onClick={() => setDesiCalculatorOpen(false)} aria-label="Pencereyi kapat"><UiIcon name="close" /></button></header><div className="desi-calculator-body"><div className="product-step-grid"><label>Ağırlık (kg)<input value={form.weight} onChange={event => updateField('weight', event.target.value)} type="number" min="0" step="0.01" /></label><label>En (cm)<input value={form.width} onChange={event => updateField('width', event.target.value)} type="number" min="0" step="0.1" /></label><label>Boy (cm)<input value={form.length} onChange={event => updateField('length', event.target.value)} type="number" min="0" step="0.1" /></label><label>Yükseklik (cm)<input value={form.height} onChange={event => updateField('height', event.target.value)} type="number" min="0" step="0.1" /></label></div><div className="calculated-field"><small>Hesaplanan desi</small><strong>{desi ? desi.toLocaleString('tr-TR', { maximumFractionDigits: 2 }) : 'Ölçüleri girin'}</strong></div></div><footer><button type="button" className="secondary" onClick={() => setDesiCalculatorOpen(false)}>İptal</button><button type="button" disabled={!desi} onClick={() => { updateField('desi', String(Number(desi.toFixed(2)))); setCalculateDesi(true); setDesiCalculatorOpen(false) }}>Uygula</button></footer></DialogSurface></div>}

    <div className="product-layout-grid"><div className="product-main-stack">
      <section className="panel product-step-card product-media-card">
        <div className="editor-section-title">
          <span>4</span>
            <div><h2>Görseller</h2><p>JPEG/PNG dosyası yükleyebilirsiniz. Görselleri sürükleyerek sıralayın; aynı modelin diğer renk görselleri de burada görünür ve sıralama kayıtta renk ürünlerine uygulanır.</p></div>
            <div className="product-media-header-actions">
              {(mediaUrls.length > 0 || mediaFiles.length > 0 || variantRows.some(row => row.mediaRefs.length > 0)) && <button type="button" className="secondary product-media-clear-all-button" onClick={clearAllMedia}>Tümünü Temizle</button>}
            </div>
        </div>
        <label className="upload-ghost-box product-media-upload">
          <input type="file" accept="image/jpeg,image/png" multiple onChange={event => { handleMediaFiles(Array.from(event.target.files ?? [])); event.currentTarget.value = '' }} />
          <strong>{mediaFiles.length ? `${mediaFiles.length} Dosya Seçildi` : 'Ürün Görsellerini Dosya Olarak Seç'}</strong>
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
              {/* eslint-disable-next-line jsx-a11y/click-events-have-key-events, jsx-a11y/no-noninteractive-element-interactions -- mouse shortcut on a draggable tile; keyboard users have MediaZoomButton (a button over the picture would stop drag-sorting in Firefox) */}
              <img src={url} alt={`${form.title || 'Ürün'} ${index + 1}`} className="clickable-thumb" draggable={false} onClick={() => setLightboxImage({ url, title: isProductMedia ? form.title || 'Ürün Görseli' : `${form.title || 'Ürün'} · Renk Ailesi` })} title="Büyütmek için tıklayın" />
              <MediaZoomButton onZoom={() => setLightboxImage({ url, title: isProductMedia ? form.title || 'Ürün Görseli' : `${form.title || 'Ürün'} · Renk Ailesi` })} />
              {isProductMedia
                ? <button type="button" className="image-remove-btn" disabled={deletingProductMedia || submitting} title="Görseli üründen hemen kaldır" aria-label="Görseli üründen hemen kaldır" onClick={event => { event.stopPropagation(); if (ownedUrl) void removeProductMedia(ownedUrl) }}><UiIcon name={deletingProductMedia ? 'loader' : 'close'} /></button>
                : <button type="button" className="image-remove-btn" disabled={deleting || !familyItem} title="Renk ailesi görselini kaynak kayıtlardan kaldır" aria-label="Renk ailesi görselini kaldır" onClick={event => { event.stopPropagation(); if (familyItem) void removeFamilyMedia(familyItem) }}><UiIcon name={deleting ? 'loader' : 'close'} /></button>}
              {visibleMediaUrls.length > 1 && <MediaMoveButtons index={index} count={visibleMediaUrls.length} disabled={submitting || Boolean(deletingProductMediaKey)} onMove={target => reorderMedia(index, target)} />}
              <figcaption>{isProductMedia ? `${ownIndex === 0 ? 'Ana görsel' : `${ownIndex + 1}. görsel`} · sürükle` : 'Renk varyantı görseli · sürükle'}</figcaption>
            </figure>
          })}
          {mediaFiles.map((item, index) => <LocalImagePreview key={item.id} mediaIndex={index} mediaCount={mediaFiles.length} onMove={target => reorderUploadedMedia(index, target)} file={item.file} alt={`${form.title || 'Ürün'} ${visibleMediaUrls.length + index + 1}`} caption={index === 0 && !visibleMediaUrls.length ? 'Ana görsel' : item.file.name} draggable={!touchPointer && !submitting} isDragOver={dragOverMedia?.kind === 'file' && dragOverMedia.index === index} onDragStart={event => { setDraggedMedia({ kind: 'file', index }); event.dataTransfer.effectAllowed = 'move'; event.dataTransfer.setData('text/plain', `file:${index}`) }} onDragOver={event => { if (draggedMedia?.kind !== 'file') return; event.preventDefault(); event.dataTransfer.dropEffect = 'move'; setDragOverMedia({ kind: 'file', index }) }} onDrop={event => { event.preventDefault(); const transferValue = event.dataTransfer.getData('text/plain'); const transferIndex = transferValue.startsWith('file:') ? Number(transferValue.slice(5)) : draggedMedia?.kind === 'file' ? draggedMedia.index : -1; if (draggedMedia?.kind === 'file' && Number.isInteger(transferIndex)) reorderUploadedMedia(transferIndex, index) }} onDragEnd={() => { setDraggedMedia(null); setDragOverMedia(null) }} onRemove={() => removeUploadedMedia(item.id)} onZoom={url => setLightboxImage({ url, title: form.title || 'Ürün Görseli' })} />)}
        </div>}
      </section>

      <section className="panel product-step-card product-options-card">
        <div className="editor-section-title product-options-header">
          <span>5</span>
          <div>
            <h2>Ürün Seçenekleri</h2>
            <p>Seçenek grubu ve değerlerini burada seçin. Mevcut ürünlerde kayıtlı Renk ve Beden değerleri otomatik işaretlenir; yeni seçimler “Ürünleri ekle” ile varyant satırlarına eklenir.</p>
          </div>
        </div>
        {bulkSyncOptionRequirements.length > 0 && <div className="product-options-toolbar"><button type="button" className="secondary product-options-sync-button" onClick={() => { setBulkOptionSyncSelection([]); setBulkOptionSyncOpen(true) }} aria-haspopup="dialog">Toplu Eşitleme</button></div>}
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
             <strong>Varyantları Oluştur</strong>
             <small>{variantAttributeIds.length ? `${variantAttributeIds.map(id => allRequirements.find(item => item.attributeId === id)?.attribute.name).filter(Boolean).join(' × ')} · ${variantAttributeIds.reduce((total, id) => total * Math.max(1, attributeSelections[id]?.length ?? 0), 1)} kombinasyon` : 'Önce seçenek grubunu ve değerlerini işaretleyin.'}</small>
             <label className="variant-auto-barcode-toggle"><input type="checkbox" checked={automaticBarcodeGeneration} onChange={event => setAutomaticBarcodeGeneration(event.target.checked)} /><span><strong>Barkodları Otomatik Oluştur</strong></span></label>
          </div>
          <div className="attribute-variant-actions">
            <button type="button" onClick={() => generateVariants()} disabled={!canAddVariantCombinations}>{canAddVariantCombinations ? 'Ürünleri Ekle' : 'Seçenekler Güncel'}</button>
          </div>
        </div>
      <div className="product-variant-groups-section">
        <div className="editor-section-title"><span>6</span><div><h2>Ürün Seçenek Grupları</h2><p>İşaretlediğiniz özellik değerlerinin tüm kombinasyonları varyant satırı olur.</p></div></div>
        {(fieldErrors.variants || fieldErrors.stock || fieldErrors.attributes) && <div className="product-field-error-list" role="alert">{[fieldErrors.variants, fieldErrors.stock, fieldErrors.attributes].filter(Boolean).map(message => <p key={message}>{message}</p>)}</div>}
        {variantRows.length > 0 && <>
          <div className={`variant-filter-panel${variantFilterOpen ? ' is-open' : ''}`}>
            <div className="variant-filter-panel-head">
              <button type="button" className="variant-filter-panel-toggle" aria-expanded={variantFilterOpen} aria-label={variantFilterOpen ? 'Varyant filtrelerini kapat' : 'Varyant filtrelerini aç'} onClick={() => setVariantFilterOpen(current => !current)}>
                <span><strong>Varyantları Filtrele</strong><span>Renk, beden gibi seçenekleri seçin; eşleşen satırlar aşağıda vurgulansın.</span></span>
                <span className="variant-filter-panel-toggle-action"><small>{variantFilterOpen ? 'Kapat' : 'Aç'}</small><UiIcon name="chevronDown" /></span>
              </button>
              {hasVariantFilters && <div className="variant-filter-panel-summary"><b>{matchingVariantCount} varyant seçildi</b><button type="button" className="variant-filter-clear" onClick={clearVariantFilters}>Temizle</button></div>}
            </div>
            {variantFilterOpen && <div className="variant-filter-panel-body">
              {variantFilterGroups.length ? <div className="variant-filter-grid">{variantFilterGroups.map(group => <VariantFilterDropdown key={group.id} group={group} selectedValueIds={variantFilterSelections[group.id] ?? []} onToggle={valueId => toggleVariantFilter(group.id, valueId)} onClear={() => setVariantFilterSelections(current => ({ ...current, [group.id]: [] }))} />)}</div> : <p className="variant-filter-empty">Filtrelemek için seçenek grubu bulunamadı.</p>}
              <div className="variant-bulk-editor">
                <label><span>Stok</span><input aria-label="Tüm varyantların stoku" value={bulkStock} onChange={event => setBulkStock(event.target.value)} type="number" min="0" placeholder="Tüm stoklar" /></label>
                <label><span>Satış Fiyatı</span><input aria-label="Tüm varyantların satış fiyatı" value={bulkSalePrice} onChange={event => setBulkSalePrice(event.target.value)} type="number" min="0" step="0.01" placeholder="Tüm satış fiyatları" /></label>
                <label><span>Maliyet</span><input aria-label="Tüm varyantların maliyeti" value={bulkCostPrice} onChange={event => setBulkCostPrice(event.target.value)} type="number" min="0" step="0.01" placeholder="Tüm maliyetler" /></label>
                <label><span>Liste Fiyatı</span><input aria-label="Tüm varyantların liste fiyatı" value={bulkListPrice} onChange={event => setBulkListPrice(event.target.value)} type="number" min="0" step="0.01" placeholder="Tüm liste fiyatları" /></label>
                <div className="variant-bulk-action"><span>Toplu Uygulama</span><button type="button" className="secondary" onClick={applyBulk} disabled={hasVariantFilters && matchingVariantCount === 0}>{hasVariantFilters ? `${matchingVariantCount} Seçilene Uygula` : 'Tümüne Uygula'}</button></div>
              </div>
            </div>}
          </div>
            <div className="variant-table-toolbar"><span>Varyant görsellerini tek tek veya seçenek değerine göre toplu atayın.</span><div className="variant-table-toolbar-actions"><button type="button" className="secondary variant-clear-button" onClick={clearVariants}>Oluşan Varyantları Temizle</button><button type="button" className="secondary variant-media-bulk-button" onClick={openBulkVariantMediaPicker} title="Seçenek değerine görsel ata"><VariantImageIcon /> Seçeneklere Görsel Ata</button>{editProductId && <button type="button" className="secondary variant-marketplace-codes-button" onClick={() => setMarketplaceCodesOpen(true)} title="Hepsiburada ve Shopify varyant kodlarını göster"><UiIcon name="externalLink" /> Pazaryeri Kodları</button>}</div></div>
        </>}
            {variantRows.length > 0 && <div className="variant-mobile-groups" role="tablist" aria-label="Görünen varyant alanları">{([['codes', 'Kodlar'], ['stock', 'Stok / Fiyat'], ['cost', 'Maliyet'], ['actions', 'İşlemler']] as const).map(([group, label]) => <button key={group} type="button" role="tab" aria-selected={variantMobileGroup === group} className={variantMobileGroup === group ? 'is-on' : undefined} onClick={() => setVariantMobileGroup(group)}>{label}</button>)}</div>}
            {variantCodeIssueSummary && <p className="variant-code-rule-notice" role="status"><UiIcon name="alert" size={14} /> {variantCodeIssueSummary.rows} varyantın barkodu veya stok kodu şu platformların kuralına uymuyor: {variantCodeIssueSummary.platforms.join(', ')}. Kırmızı alanların üzerine gelince ayrıntı görünür; bu ürün o platformlara bu kodlarla gönderilemez.</p>}
            <div className={`variant-table-editor is-mobile-${variantMobileGroup}`}>
              <div className="variant-table-head">
                <span>#</span><span>Seçenek</span>
                <span className="variant-table-header-with-action"><span>Barkod</span><div className="variant-header-action-shell" ref={barcodePasteActionRef}><button type="button" className="variant-header-action" onClick={() => { setBarcodeSkuMenuOpen(false); setBarcodePasteMenuOpen(current => !current) }} aria-label="Panodan barkod yapıştırma seçenekleri" aria-haspopup="menu" aria-expanded={barcodePasteMenuOpen} title="Panodan barkod yapıştırma seçenekleri"><BarcodePasteIcon /></button>{barcodePasteMenuOpen && <div className="variant-header-action-menu" role="menu"><button type="button" role="menuitem" disabled={!emptyBarcodeRowCount} onClick={() => void pasteBarcodesFromClipboard('missing')}><span><strong>Boş Barkodlara Yapıştır</strong><small>Sadece boş satırlar · {emptyBarcodeRowCount} hedef</small></span><UiIcon name="externalLink" /></button><button type="button" role="menuitem" disabled={!variantRows.length} onClick={() => void pasteBarcodesFromClipboard('all')}><span><strong>Tüm Barkodları Panodan Değiştir</strong><small>Varyant sırası · {variantRows.length} hedef</small></span><i aria-hidden="true">!</i></button><p>Satır sayısı hedefle aynı olmalı; sekmeli tabloda ilk sütun kullanılır. Boş veya yinelenen barkodda işlem yapılmaz.</p></div>}</div></span>
                <span className="variant-table-header-with-action"><span>Stok Kodu</span><div className="variant-header-action-shell" ref={barcodeSkuActionRef}><button type="button" className="variant-header-action" onClick={() => { setBarcodePasteMenuOpen(false); setBarcodeSkuMenuOpen(current => !current) }} aria-label="Barkoddan doldurma seçenekleri" aria-haspopup="menu" aria-expanded={barcodeSkuMenuOpen} title="Barkoddan stok kodu doldurma seçenekleri"><BarcodeFillIcon /></button>{barcodeSkuMenuOpen && <div className="variant-header-action-menu" role="menu"><button type="button" role="menuitem" disabled={!emptySkuBarcodeRowCount} onClick={() => applyBarcodeToSku('missing')}><span><strong>Eksik Stok Kodlarını Doldur</strong><small>Sadece boş satırlar · {emptySkuBarcodeRowCount} aday</small></span><UiIcon name="externalLink" /></button><button type="button" role="menuitem" disabled={!barcodeRowCount} onClick={() => applyBarcodeToSku('all')}><span><strong>Barkodları Stok Koduna Uygula</strong><small>Barkodu olan {barcodeRowCount} satırı güncelle</small></span><i aria-hidden="true">!</i></button><p>Çakışan barkodlar otomatik olarak atlanır; mevcut kodlar ilk seçenekte korunur.</p></div>}</div></span>
                {(['stock', 'salePrice', 'costPrice', 'listPrice'] as VariantBulkEditField[]).map(field => <span className="variant-table-header-with-action variant-bulk-header" key={field}><span>{variantBulkEditLabels[field]}</span><button type="button" className="variant-bulk-edit-trigger" aria-label={`${variantBulkEditLabels[field]} için toplu değişiklik aç`} aria-haspopup="dialog" aria-controls={variantBulkEdit?.field === field ? 'variant-bulk-edit-dialog' : undefined} aria-expanded={variantBulkEdit?.field === field} title={`${variantBulkEditLabels[field]} toplu değiştir`} onClick={() => openVariantBulkEdit(field)} disabled={!variantRows.length}><UiIcon name="edit" size={14} /></button></span>)}
                <span>İşlem</span>
              </div>
              {variantRows.length ? variantRows.map((row, index) => {
                const matchesFilter = rowMatchesVariantFilters(row)
                const rowPlatforms = row.platformStatuses?.length ? row.platformStatuses : !editProductId ? variantPricingPlatforms : []
                const codeIssues = variantCodeIssues.get(row.key) ?? []
                const barcodeIssueText = describePlatformCodeIssues(codeIssues, 'barcode')
                const skuIssueText = describePlatformCodeIssues(codeIssues, 'sku')
                return <div data-variant-row-key={row.key} className={`variant-table-row ${hasVariantFilters && matchesFilter ? 'is-filter-match' : ''} ${hasVariantFilters && !matchesFilter ? 'is-filter-dimmed' : ''} ${draggedVariantKey === row.key ? 'is-dragging' : ''} ${dragOverVariantKey === row.key ? 'is-drag-target' : ''}`} key={row.key}>
                  <div className="variant-row-lead" title="Sıralamak için tutup sürükleyin" aria-label={`${row.optionSignature} varyantını sıralamak için sürükleyin`} onPointerDown={event => beginVariantPointerDrag(event, row.key)}><span className="variant-row-number">{index + 1}</span><span className="variant-drag-handle"><VariantDragHandleIcon /></span></div>
                  <div className="variant-option-cell"><span className="variant-option-text" aria-label={`${index + 1}. varyant seçenekleri`} title={displayVariantOptionSignature(row.optionSignature, row.options)}>{displayVariantOptionSignature(row.optionSignature, row.options)}</span><button type="button" className="variant-option-edit-button" onClick={() => setOptionEditRowKey(row.key)} aria-label={`${index + 1}. varyantın seçeneklerini düzenle`} title="Seçenekleri düzenle" disabled={!rowOptionEntries(row).length}><UiIcon name="edit" size={14} /></button></div>
                  <input aria-label={`${row.optionSignature} barkod`} className={`technical-field barcode-value${barcodeIssueText ? ' has-platform-code-issue' : ''}`} aria-invalid={barcodeIssueText ? true : undefined} title={barcodeIssueText || undefined} value={row.barcode} onChange={event => updateVariantRow(row.key, 'barcode', event.target.value)} placeholder="EAN / barkod" />
                  <input aria-label={`${row.optionSignature} stok kodu`} className={`technical-field sku-value${skuIssueText ? ' has-platform-code-issue' : ''}`} aria-invalid={skuIssueText ? true : undefined} title={skuIssueText || undefined} value={row.sku} onChange={event => updateVariantRow(row.key, 'sku', event.target.value)} placeholder="Varyant SKU" />
                  <input aria-label={`${row.optionSignature} stok`} value={row.stock} onChange={event => updateVariantRow(row.key, 'stock', event.target.value)} type="number" min="0" step="1" />
                  <input aria-label={`${row.optionSignature} satış fiyatı`} value={row.salePrice} onChange={event => updateVariantRow(row.key, 'salePrice', event.target.value)} type="number" min="0" step="0.01" />
                  <input aria-label={`${row.optionSignature} maliyeti`} value={row.costPrice ?? ''} onChange={event => updateVariantRow(row.key, 'costPrice', event.target.value)} type="number" min="0" step="0.01" />
                  <input aria-label={`${row.optionSignature} liste fiyatı`} value={row.listPrice} onChange={event => updateVariantRow(row.key, 'listPrice', event.target.value)} type="number" min="0" step="0.01" />
                  <div className="variant-row-actions" aria-label={`${row.optionSignature} varyant işlemleri`}>
                    {rowPlatforms.length ? (() => { const linkedCount = rowPlatforms.filter(item => item.isLinked).length; const isPartial = linkedCount > 0 && linkedCount < rowPlatforms.length; const summaryClass = linkedCount === rowPlatforms.length ? 'is-linked' : isPartial ? 'is-partial' : 'is-unlinked'; return <button type="button" className={`variant-platform-status variant-platform-status-summary variant-row-action ${summaryClass}`} onClick={() => openVariantPlatformPricing(row, rowPlatforms[0])} title={`${rowPlatforms.length} platform fiyatını düzenle`} aria-label={`${row.optionSignature} platform fiyatlarını düzenle`}><UiIcon name="platforms" /></button> })() : <span className="variant-platform-empty">—</span>}
                    {editProductId && <button type="button" className="variant-row-action variant-row-action-codes" onClick={() => setMarketplaceCodesVariant(row.key)} aria-label={`${row.optionSignature} pazaryeri kodlarını göster`} title="Pazaryeri kodları (Shopify, Hepsiburada)"><UiIcon name="link" /></button>}
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
              familyProductIds={productToEdit.data?.familyProductIds}
              lookupKeys={{ modelCode: form.modelCode, barcode: variantRows[0]?.barcode || form.barcode, sku: variantRows[0]?.sku || form.baseSku }}
              variantLinks={publishCardVariantLinks(variantRows, card.connection.id)}
              categoryId={form.categoryId}
              selected={selectedChannelIds.includes(card.connection.id)}
              productChecks={productChecks}
              selectedAttributes={publicationAttributeSelections}
              variantMediaWarning={variantsWithoutMediaCount > 0 ? `${variantRows.length} varyanttan ${variantsWithoutMediaCount} tanesine görsel seçilmedi. Varyant tablosundan veya “Seçeneklere görsel ata” ile görsel atayın.` : undefined}
              onSelect={() => updateChannel(card.connection.id)}
              onDeselect={() => setSelectedChannelIds(current => current.filter(id => id !== card.connection.id))}
              onFeedback={setFeedback}
            />) : <div className="publish-connections-empty"><strong>Aktif Bağlantı Bulunamadı</strong><p>Yayınlama için önce Platformlar sayfasından aktif bir bağlantı oluşturun.</p><Link to="/integrations">Platformları yönet <UiIcon name="arrowRight" /></Link></div>}
          </div>
          <div className="publish-selection-footer">
            <div><strong>{selectedChannelIds.length ? `${selectedChannelIds.length} platform seçildi` : 'Yayın platformu seçilmedi'}</strong><small>{selectedChannelIds.length ? 'Seçilen her mağazada ayrı yayın kuyruğu ve kontrol listesi bulunur.' : 'Yayınlanacak mağazaları kartların üstünden seçin.'}</small></div>
            {!selectedChannelIds.length && <span className="publish-selection-warning"><UiIcon name="alert" /> En az bir platform seçin</span>}
            <button type="submit" disabled={submitting}>{submitting ? (editProductId ? 'Yayınlanıyor…' : 'Ürün Oluşturuluyor…') : (editProductId ? 'Yayınla' : <><UiIcon name="sparkle" /> Ürünü Oluştur</>)}</button>
          </div>
        </div>
      </div>
    </section>

    <section className="product-submit-sticky"><div><strong>{editProductId ? 'Ürün düzenlemeye hazır' : 'Ürün bilgileri hazır'}</strong><p>{variantRows.length || 1} satış satırı · {selectedChannelIds.length} seçili kanal</p></div><div className="product-submit-actions">{editProductId ? <button type="submit" name="intent" value="save" className="secondary" data-submit-intent="save" form="product-creation-form" disabled={submitting}>{submitting ? 'Kaydediliyor…' : 'Kaydet'}</button> : <button type="submit" name="intent" value="create" className="secondary" data-submit-intent="create" form="product-creation-form" disabled={submitting}>{submitting ? 'Oluşturuluyor…' : 'Ürünü Oluştur'}</button>}{editProductId && <div className="platform-update-split" role="group" aria-label="Platform güncelleme"><button type="button" className="platform-update-quick" onClick={startQuickPlatformUpdate} disabled={submitting || connections.isLoading} title="Seçili aktif platformlarda yalnız fiyat ve stoku güncelle"><span>Hızlı Güncelle</span><UiIcon name="refresh" /></button><button type="button" className="platform-update-settings" onClick={openPlatformUpdateDialog} disabled={submitting || connections.isLoading} aria-label="Platform güncelleme ayarları" title="Platform güncelleme ayarları" aria-haspopup="dialog"><UiIcon name="arrowUp" /></button></div>}<button type="button" onClick={() => setWizardStep(2)}><span className="submit-label-long">Yayınlamaya Devam Et</span><span className="submit-label-short">Yayınla</span> <UiIcon name="arrowRight" /></button></div></section>
    {/* A save or validation error is already a feedback toast in the column; this one is only for what failed to load. */}
    {!error && <ErrorToast title="Ürün Bilgileri Yüklenemedi" error={categories.error ?? brands.error ?? connections.error} />}
    {barcodePasteMenuOpen && <VariantHeaderActionMenu anchorRef={barcodePasteActionRef}>
      <section className="variant-header-action-menu-group" role="group" aria-label="Barkod üret">
        <p className="variant-header-action-menu-title">Model Koduyla Üret</p>
        <button type="button" role="menuitem" disabled={!variantRows.length || !form.modelCode.trim()} onClick={() => regenerateVariantCodes('barcode')}><span><strong>Model Kodundan Barkod Üret</strong><small className="variant-code-generation-preview"><span>Barkod: {generatedCodePreview}</span><span>{variantRows.length.toLocaleString('tr-TR')} varyant</span></small>{codeRulePlatforms.includes('HEPSIBURADA') && <small className="variant-code-generation-warning">Hepsiburada EAN-13 ister; model kodundan üretilen barkodla gönderilemez.</small>}</span><UiIcon name="refresh" /></button>
      </section>
      <section className="variant-header-action-menu-group" role="group" aria-label="Panodan barkod yapıştır">
        <p className="variant-header-action-menu-title">Panodan Barkod Yapıştır</p>
        <button type="button" role="menuitem" disabled={!emptyBarcodeRowCount} onClick={() => void pasteBarcodesFromClipboard('missing')}><span><strong>Boş Barkodları Doldur</strong><small>{emptyBarcodeRowCount.toLocaleString('tr-TR')} boş varyant</small></span><UiIcon name="arrowRight" /></button>
        <button type="button" role="menuitem" disabled={!variantRows.length} onClick={() => void pasteBarcodesFromClipboard('all')}><span><strong>Tüm Barkodları Değiştir</strong><small>{variantRows.length.toLocaleString('tr-TR')} varyant sırası</small></span><UiIcon name="alert" /></button>
      </section>
    </VariantHeaderActionMenu>}
    {barcodeSkuMenuOpen && <VariantHeaderActionMenu anchorRef={barcodeSkuActionRef}>
      <section className="variant-header-action-menu-group" role="group" aria-label="Model kodundan stok kodu üret">
        <p className="variant-header-action-menu-title">Model Koduyla Üret</p>
        <button type="button" role="menuitem" disabled={!variantRows.length || !form.modelCode.trim()} onClick={() => regenerateVariantCodes('sku')}><span><strong>Model Kodundan Stok Kodu Üret</strong><small className="variant-code-generation-preview"><span>Stok Kodu: {generatedCodePreview}</span><span>{variantRows.length.toLocaleString('tr-TR')} varyant</span></small></span><UiIcon name="refresh" /></button>
      </section>
      <section className="variant-header-action-menu-group" role="group" aria-label="Stok kodlarını barkodlardan düzenle">
        <p className="variant-header-action-menu-title">Stok Kodlarını Barkodlardan Düzenle</p>
        <button type="button" role="menuitem" disabled={!emptySkuBarcodeRowCount} onClick={() => applyBarcodeToSku('missing')}><span><strong>Eksik Stok Kodlarını Doldur</strong><small>Sadece boş satırlar · {emptySkuBarcodeRowCount.toLocaleString('tr-TR')} satır</small></span><UiIcon name="arrowRight" /></button>
        <button type="button" role="menuitem" disabled={!barcodeRowCount} onClick={() => applyBarcodeToSku('all')}><span><strong>Barkodları Stok Koduna Uygula</strong><small>Stok kodu sütunu · {barcodeRowCount.toLocaleString('tr-TR')} barkodlu satır</small></span><UiIcon name="alert" /></button>
      </section>
    </VariantHeaderActionMenu>}
    {mediaRemovalConfirmation && createPortal(
      <div className="workspace-modal-backdrop media-removal-confirm-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) setMediaRemovalConfirmation(null) }}>
        <DialogSurface as="section" onClose={() => { setMediaRemovalConfirmation(null) }} className="workspace-modal media-removal-confirm-modal" role="alertdialog" aria-labelledby="media-removal-confirm-title" aria-describedby="media-removal-confirm-description" onMouseDown={event => event.stopPropagation()} onKeyDown={event => { if (event.key === 'Escape') setMediaRemovalConfirmation(null) }}>
          <header>
            <div>
              <p className="eyebrow">Görsel Yönetimi</p>
              <h2 id="media-removal-confirm-title">{mediaRemovalConfirmation.kind === 'product' ? 'Görseli Hemen Kaldır' : 'Renk Ailesi Görselini Kaldır'}</h2>
            </div>
            <button type="button" className="modal-close" onClick={() => setMediaRemovalConfirmation(null)} aria-label="Görsel kaldırma onayını kapat"><UiIcon name="close" /></button>
          </header>
          <div className="media-removal-confirm-body">
            <p id="media-removal-confirm-description">{mediaRemovalConfirmation.kind === 'product'
              ? 'Bu görsel aynı model ailesindeki ürün ve varyant kayıtlarından hemen kaldırılacak. Pazaryerindeki görsel değiştirilmez.'
              : `Bu renk ailesi görseli ${(mediaRemovalConfirmation.item.sourceProductTitles.length ? mediaRemovalConfirmation.item.sourceProductTitles.join(', ') : 'ürün ailesindeki kaynak ürünler')} kayıtlarından kaldırılacak. İşlem yalnızca paneldeki görsel bağlantılarını etkiler; pazaryerindeki ürün veya görsel silinmez.`}</p>
          </div>
          <footer>
            <button type="button" className="secondary" data-autofocus={touchScreen ? undefined : true} onClick={() => setMediaRemovalConfirmation(null)}>Vazgeç</button>
            <button type="button" className="destructive" onClick={confirmMediaRemoval}>Görseli Kaldır</button>
          </footer>
        </DialogSurface>
      </div>,
      document.querySelector('main') ?? document.body
    )}
    {/* The editor's dialogs portal into main: the panel's button and field styles are scoped to main, so one portaled
        into body showed bare browser buttons (live 2026-10-30, Stok Kodlarını Model Kodundan Üret). */}
    {pendingVariantCodeGeneration && createPortal(
      <div className="workspace-modal-backdrop variant-code-confirm-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) setPendingVariantCodeGeneration(null) }}>
        <DialogSurface as="section" onClose={() => { setPendingVariantCodeGeneration(null) }} className="workspace-modal variant-code-confirm-modal" role="alertdialog" aria-labelledby="variant-code-confirm-title" aria-describedby="variant-code-confirm-description" onMouseDown={event => event.stopPropagation()}>
          <header>
            <div>
              <p className="eyebrow">Varyant Kodları</p>
              <h2 id="variant-code-confirm-title">{pendingVariantCodeGeneration.target === 'barcode' ? 'Barkodları Model Kodundan Üret' : 'Stok Kodlarını Model Kodundan Üret'}</h2>
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
            <button type="button" className="secondary" data-autofocus={touchScreen ? undefined : true} onClick={() => setPendingVariantCodeGeneration(null)}>Vazgeç</button>
            <button type="button" onClick={confirmVariantCodeGeneration}>{pendingVariantCodeGeneration.target === 'barcode' ? 'Barkodları Üret' : 'Stok Kodlarını Üret'}</button>
          </footer>
        </DialogSurface>
      </div>,
      document.querySelector('main') ?? document.body
    )}
    {platformUpdateDialogOpen && <PlatformUpdateDialog
      targets={productUpdateTargets}
      selectedIds={platformUpdateConnectionIds}
      onToggle={connectionId => setPlatformUpdateConnectionIds(ids => ids.includes(connectionId) ? ids.filter(id => id !== connectionId) : [...ids, connectionId])}
      fields={platformUpdatePreferences.fields}
      onFieldsChange={fields => changePlatformUpdatePreferences({ ...platformUpdatePreferences, fields })}
      openGroups={platformUpdatePreferences.openGroups}
      onOpenGroupsChange={openGroups => changePlatformUpdatePreferences({ ...platformUpdatePreferences, openGroups })}
      submitting={submitting}
      onClose={() => setPlatformUpdateDialogOpen(false)}
      onConfirm={confirmPlatformUpdate} />}
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
      assignedValueIds={variantMediaModal.mode === 'bulk' ? new Set((bulkMediaGroups.find(group => group.id === variantMediaModal.groupId)?.values ?? []).filter(value => (readVariantMediaAssignmentDraft(variantMediaModal.draftsBySelection, variantMediaAssignmentKey(variantMediaModal.groupId, value.id)) ?? []).length > 0).map(value => value.id)) : undefined}
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
      <DialogSurface as="section" onClose={() => { setVariantBulkEdit(null) }} id="variant-bulk-edit-dialog" className="workspace-modal variant-bulk-edit-modal" aria-labelledby="variant-bulk-edit-title" onMouseDown={event => event.stopPropagation()}>
        <header><div><p className="eyebrow">Varyantlar</p><h2 id="variant-bulk-edit-title">Toplu {variantBulkEditLabels[variantBulkEdit.field]} Değiştir</h2><p>Filtrelerle eşleşen varyant satırlarına uygulanır.</p></div><button type="button" className="modal-close" onClick={() => setVariantBulkEdit(null)} aria-label="Toplu değişiklik penceresini kapat"><UiIcon name="close" /></button></header>
        <div className="variant-bulk-edit-body">
          {variantBulkEditFilterGroups.length > 0 && <section className="variant-bulk-edit-filters" aria-label="Renk ve beden filtreleri"><strong>Renk ve beden seçin</strong><div>{variantBulkEditFilterGroups.map(group => <VariantFilterDropdown key={group.id} group={group} selectedValueIds={variantBulkEditFilterSelections[group.id] ?? []} onToggle={valueId => setVariantBulkEditFilterSelections(current => ({ ...current, [group.id]: (current[group.id] ?? []).includes(valueId) ? current[group.id].filter(id => id !== valueId) : [...(current[group.id] ?? []), valueId] }))} onClear={() => setVariantBulkEditFilterSelections(current => ({ ...current, [group.id]: [] }))} />)}</div></section>}
          <div className="variant-bulk-edit-target"><span>Uygulanacak Varyant</span><strong>{variantBulkEditMatchingRows.length.toLocaleString('tr-TR')} / {variantRows.length.toLocaleString('tr-TR')} satır</strong></div>
          <label htmlFor="variant-bulk-edit-value">Yeni {variantBulkEditLabels[variantBulkEdit.field]} Değeri<input id="variant-bulk-edit-value" data-autofocus={touchScreen ? undefined : true} type="number" min="0" step={variantBulkEdit.field === 'stock' ? '1' : '0.01'} value={variantBulkEdit.value} aria-describedby={variantBulkEdit.error ? 'variant-bulk-edit-help variant-bulk-edit-error' : 'variant-bulk-edit-help'} onChange={event => setVariantBulkEdit(current => current ? { ...current, value: event.target.value, error: '' } : current)} /></label>
          <small id="variant-bulk-edit-help">{variantBulkEdit.field === 'stock' ? 'Stok tam sayı ve sıfırdan büyük veya eşit olmalıdır.' : 'Tutar sıfır veya daha büyük olmalıdır.'}</small>
          {variantBulkEdit.error && <p id="variant-bulk-edit-error" className="variant-bulk-edit-error" role="alert">{variantBulkEdit.error}</p>}
        </div>
        <footer><button type="button" className="secondary" onClick={() => setVariantBulkEdit(null)}>Vazgeç</button><button type="button" onClick={applyVariantBulkEdit} disabled={!variantBulkEditMatchingRows.length}>Uygula</button></footer>
      </DialogSurface>
    </div>}
  </form></EditorShell>
}
// The publish step as a popup over the product list. It is placed in the page's <main> (not in <body>) because the
// publish cards' styles are scoped to the panel's main area, and outside the list's own styling root.
export function ProductPublishPopup({ productId, onClose }: { productId: string; onClose: () => void }) {
  return createPortal(
    <div className="workspace-modal-backdrop product-publish-popup-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) onClose() }}>
      <DialogSurface as="section" onClose={onClose} className="workspace-modal product-publish-popup-modal" aria-labelledby="product-publish-popup-title" onMouseDown={event => event.stopPropagation()}>
        <header><div><p className="eyebrow">Ürün</p><h2 id="product-publish-popup-title">Platformlarda Yayınla</h2></div><button type="button" className="modal-close" onClick={onClose} aria-label="Yayın penceresini kapat"><UiIcon name="close" /></button></header>
        <div className="product-publish-popup-body"><NewProductPage key={productId} editProductId={productId} publishOnly /></div>
      </DialogSurface>
    </div>,
    document.querySelector('main') ?? document.body)
}

export function ProductDetailPage() {
  const { id } = useParams()
  if (!id) return <p className="unknown">Ürün kimliği bulunamadı.</p>
  return <NewProductPage editProductId={id} />
}
