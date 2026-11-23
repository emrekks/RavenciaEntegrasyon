// Every piece of state and every ref of the product editor, with the toast helpers (no effects of its own besides
// the toast column's and the saved Hızlı Güncelle choice's, which come first as they did in the page).
import { useMemo, useReducer, useRef, useState, type Dispatch, type SetStateAction } from 'react'
import { newIdempotencyKey } from '../../../shared/idempotency'
import { resetSeededFields } from './seeded-fields'
import { productNotificationSubject } from '../../../shared/notifications'
import { type VariantBulkEditField, type VariantBulkMode } from '../variant-bulk-edit'
import { type RemovedVariantRow } from '../variant-row-removal'
import { defaultPlatformUpdatePreferences, type PlatformUpdatePreferences } from './platform-update-fields'
import { usePlatformUpdateChoice } from './use-platform-update-choice'
import { type RequiredEditorField } from './required-fields'
import { sizeChartKey } from './size-chart-order'
import { copyRemovedMediaKeys } from '../product-copy-media-removals'
import { type EditorFieldErrors, type EditorWebColor } from '../product-editor-state'
import { editorReducer, initialEditorState, type DirtyFields, type EditorFields } from './editor-reducer'
import { useOperationFeedbackToasts, type OperationFeedback } from '../operation-feedback-toast'
import { type ChannelPricingDraft } from './marketplace-channel-pricing'
import { Product, VariantPlatformStatus } from '../shared'
import { ProductUpdateRequest, MediaRemovalConfirmation, PendingProductMediaFile, VariantMediaModalState, VariantFilterSelections, VariantDraft, PendingVariantCodeChange } from './editor-types'
import type { EditorProps } from './editor-types'

export function useEditorState(c: EditorProps) {
  const { editProductId, publishOnly, routeState } = c
  // Images removed from a copy stay removed (reload, reseed from the source); the copy's own list, never the source's.
  const copyRemovedMedia = useRef<string[]>(editProductId ? [] : copyRemovedMediaKeys(routeState))
  const [error, setError] = useState<unknown>(); const [, setNotice] = useState(''); const operationToasts = useOperationFeedbackToasts()
  // Every notice of the editor names the product it is about (and the marketplace, when a card adds it).
  const setFeedback = (feedback: OperationFeedback) => operationToasts.show({ ...feedback, context: { subject: productNotificationSubject({ id: editProductId, name: form.title, modelCode: form.modelCode }), ...feedback.context } }); const [submitting, setSubmitting] = useState(false); const [mediaAutoSaveTick, setMediaAutoSaveTick] = useState(0);const [calculateDesi, setCalculateDesi] = useState(false); const [desiCalculatorOpen, setDesiCalculatorOpen] = useState(false)
  // The form data and its baseline in one state object (editor-reducer.ts); each field keeps its own setter.
  const [editorState, dispatch] = useReducer(editorReducer, undefined, initialEditorState)
  const { form, variantRows, attributeSelections, attributeTextValues, familyMediaOrder, mediaFiles, removedVariantIds, pendingFamilyMediaRemovals, channelPricing, variantChannelPricing, webColorAutoEnabled, manualWebColorValueId, sizeChartUrl } = editorState.fields
  const fieldSetters = useMemo(() => {
    const setter = <K extends keyof EditorFields>(field: K): Dispatch<SetStateAction<EditorFields[K]>> => update => dispatch({ type: 'setField', field, update } as Extract<Parameters<typeof dispatch>[0], { type: 'setField' }>)
    return {
      setForm: setter('form'), setVariantRows: setter('variantRows'), setAttributeSelections: setter('attributeSelections'), setAttributeTextValues: setter('attributeTextValues'), setFamilyMediaOrder: setter('familyMediaOrder'),
      setMediaFiles: setter('mediaFiles'), setRemovedVariantIds: setter('removedVariantIds'), setPendingFamilyMediaRemovals: setter('pendingFamilyMediaRemovals'), setChannelPricing: setter('channelPricing'),
      setVariantChannelPricing: setter('variantChannelPricing'), setWebColorAutoEnabled: setter('webColorAutoEnabled'), setManualWebColorValueId: setter('manualWebColorValueId'), setSizeChartUrl: setter('sizeChartUrl'),
      // Unsaved-edit tracking (W6): a new baseline from the form as the next render shows it, or the baseline moved for
      // what the editor saved on its own.
      requestBaseline: () => dispatch({ type: 'requestBaseline' }),
      takeBaseline: (baseline: DirtyFields) => dispatch({ type: 'takeBaseline', baseline }),
      adjustBaseline: (adjust: (baseline: DirtyFields) => DirtyFields) => dispatch({ type: 'adjustBaseline', adjust }),
      requestWebColorBaseline: () => dispatch({ type: 'requestWebColorBaseline' })
    }
  }, [])
  const { setForm, setVariantRows, setAttributeSelections, setAttributeTextValues, setFamilyMediaOrder, setMediaFiles, setRemovedVariantIds, setPendingFamilyMediaRemovals, setChannelPricing, setVariantChannelPricing, setWebColorAutoEnabled, setManualWebColorValueId, setSizeChartUrl, requestBaseline, takeBaseline, adjustBaseline, requestWebColorBaseline } = fieldSetters
  const { baseline, baselineRequest, webColorBaselineRequest } = editorState
  const [variantAttributeIds, setVariantAttributeIds] = useState<string[]>([]); const [draggedVariantKey, setDraggedVariantKey] = useState<string | null>(null); const [dragOverVariantKey, setDragOverVariantKey] = useState<string | null>(null); const [selectedChannelIds, setSelectedChannelIds] = useState<string[]>([]); const [blockedPublishCards, setBlockedPublishCards] = useState<Record<string, number>>({})
  const [automaticBarcodeGeneration, setAutomaticBarcodeGeneration] = useState(false)
  const [marketplaceCodesOpen, setMarketplaceCodesOpen] = useState(false)
  const [quickEditOpen, setQuickEditOpen] = useState(false)
  // The card whose saved data the form holds (edit page): until it is set, no field is shown as missing.
  const [seededProductId, setSeededProductId] = useState<string | null>(null)
  // The variant whose marketplace codes the row's link button opened, or null for the whole table.
  const [marketplaceCodesVariant, setMarketplaceCodesVariant] = useState<string | null>(null)
  const [attributeValueQueries, setAttributeValueQueries] = useState<Record<string, string>>({})
  const initializedEditProductKey = useRef<string | null>(null)
  const initializedEditOptionsKey = useRef<string | null>(null)
  const initializedEditWebColorKey = useRef<string | null>(null)
  const [wizardStep, setWizardStep] = useState<1 | 2>(() => publishOnly || routeState?.wizardStep === 2 ? 2 : 1)
  const [platformUpdateDialogOpen, setPlatformUpdateDialogOpen] = useState(false)
  const [platformUpdateConnectionIds, setPlatformUpdateConnectionIds] = useState<string[]>([])
  // The fields picked in "Platformlarda Güncelle" stay as they were left, for every product and device (kept per tenant on
  // the server; usePlatformUpdateChoice).
  const [platformUpdatePreferences, setPlatformUpdatePreferences] = useState<PlatformUpdatePreferences>(defaultPlatformUpdatePreferences)
  const [savingPlatformUpdateChoice, setSavingPlatformUpdateChoice] = useState(false)
  const platformUpdateChoice = usePlatformUpdateChoice()
  const pendingPlatformUpdate = useRef<ProductUpdateRequest | null>(null)
  const [lightboxImage, setLightboxImage] = useState<{ url: string; title: string; modelCode?: string } | null>(null)
  const [variantMediaModal, setVariantMediaModal] = useState<VariantMediaModalState | null>(null)
  const [barcodeSkuMenuOpen, setBarcodeSkuMenuOpen] = useState(false)
  const barcodeSkuActionRef = useRef<HTMLDivElement>(null)
  const [barcodePasteMenuOpen, setBarcodePasteMenuOpen] = useState(false)
  // Phones show the option column beside one group of fields at a time; the other columns stay in the grid, hidden.
  const [variantMobileGroup, setVariantMobileGroup] = useState<'codes' | 'stock' | 'price' | 'actions'>('stock')
  const barcodePasteActionRef = useRef<HTMLDivElement>(null)
  const [pendingVariantCodeGeneration, setPendingVariantCodeGeneration] = useState<PendingVariantCodeChange | null>(null)
  const variantRowsRef = useRef(variantRows)
  variantRowsRef.current = variantRows
  const [expandedOptionGroupIds, setExpandedOptionGroupIds] = useState<Record<string, boolean>>({})
  const [variantBulkEdit, setVariantBulkEdit] = useState<{ field: VariantBulkEditField; mode: VariantBulkMode; value: string; error: string } | null>(null)
  const [variantBulkEditFilterSelections, setVariantBulkEditFilterSelections] = useState<VariantFilterSelections>({})
  const [deletingProductMediaKey, setDeletingProductMediaKey] = useState<string | null>(null)
  const [mediaRemovalConfirmation, setMediaRemovalConfirmation] = useState<MediaRemovalConfirmation | null>(null)
  const [familyMediaOrderDirty, setFamilyMediaOrderDirty] = useState(false)
  const [draggedMedia, setDraggedMedia] = useState<number | null>(null); const [dragOverMedia, setDragOverMedia] = useState<number | null>(null)
  // The strip as the user last arranged it (mediaStripRefs): where the new files stand among the saved pictures.
  const [mediaStripOrder, setMediaStripOrder] = useState<string[]>([])
  // The picture marked as the card's size chart (Beden Tablosu): always the last picture of the strip and of every
  // variant; written with Kaydet (PUT /products/{id}/size-chart) when it differs from the loaded one.
  const initialSizeChartUrl = useRef<string | null>(null)
  // The chart a pending family picture removal cleared, restored by its Geri Al.
  const sizeChartBeforeFamilyRemoval = useRef<string | null>(null)
  // "Beden Tablosu Seç" beside Tümünü Temizle: while on, a tap on one of the card's own saved pictures marks it as the
  // size chart (the tiles' zoom, remove and move buttons and dragging are off meanwhile); Escape or Vazgeç leaves it.
  const [sizeChartPicking, setSizeChartPicking] = useState(false)
  const chartKey = sizeChartKey(sizeChartUrl)
  const [pointerDraggedVariantKey, setPointerDraggedVariantKey] = useState<string | null>(null)
  const pointerDraggedVariantRef = useRef<string | null>(null)
  const pointerDragSourceRef = useRef<HTMLDivElement | null>(null)
  const pointerDragIdRef = useRef<number | null>(null)
  // The saved rows taken off the table, where they stood: a refused delete puts them back.
  const [removedVariantRows, setRemovedVariantRows] = useState<RemovedVariantRow<VariantDraft>[]>([])
  const [variantDeleteConfirmation, setVariantDeleteConfirmation] = useState<VariantDraft | null>(null)
  // A saved "Tek Ürün" placeholder replaced by generated variants: the server removes it on save only when nothing
  // (options, marketplace links or offers, orders, stock movements) is tied to it, and keeps it otherwise.
  const [replacedPlaceholderIds, setReplacedPlaceholderIds] = useState<string[]>([])
  // The Web Color the open product was filled with, taken into the baseline once it is set (it waits for requirements).
  const webColorSeed = useRef<EditorWebColor | null>(null)
  const [staleProductVersion, setStaleProductVersion] = useState<number | null>(null)
  const allowReseedUntil = useRef(0)
  // True while the editor's own save runs: a refetch landing then does not refill the form or flag a newer version.
  const editorSaveInFlight = useRef(false)
  // The saved product the edit form was filled from (WEB-SAYFALAR-1). A refetch landing on a form with unsaved edits
  // only raises the stale banner, so Kaydet must name this version in If-Match, not the refetched one: else the server
  // takes the save and the other change is overwritten. The editor's own writes move it on (adoptOwnProductWrite).
  const seededProduct = useRef<Product | null>(null)
  // The product as it was when Kaydet was pressed: the "Görseller" strip reads it until the save is done. Each upload
  // raises a realtime refetch, and a product with colour variants lists only its first product-level picture until the
  // variants get theirs, so the strip took every other stored copy for a family picture and showed it beside its own
  // local preview, one more per upload (live 2026-11-06, 19 pictures: they "copied themselves").
  const [productAtSave, setProductAtSave] = useState<Product | null>(null)
  // Codes a refused save named as held by another card, keyed by field and code, with that card's title.
  const [variantCodeConflicts, setVariantCodeConflicts] = useState<Record<string, string>>({})
  // New files a half-finished save did not upload: the refill after it keeps them queued instead of dropping them.
  const mediaFilesKeptOnReseed = useRef<PendingProductMediaFile[] | null>(null)
  const mediaFileInputRef = useRef<HTMLInputElement>(null)
  // Idempotency-Keys of new channel offers in the variant price table, per (variant, marketplace).
  const offerPostKeys = useRef(new Map<string, { key: string; body: string }>())
  // "Pazaryerlerine Özel Fiyat" as the product opened (the marketplaces with their own price) and each card's price when
  // it was filled or switched on: a card whose price was not changed leaves every variant's own offer price as it is.
  const initialCustomChannelIds = useRef<ReadonlySet<string>>(new Set())
  const channelPricingBase = useRef<Record<string, ChannelPricingDraft>>({})
  // A new editor starts with no loaded box texts (seeded-fields.ts); the seed fills them for a product it opens.
  useState(() => { resetSeededFields(); return null })
  const [fieldErrors, setFieldErrors] = useState<EditorFieldErrors>({})
  const [requiredErrors, setRequiredErrors] = useState<Partial<Record<RequiredEditorField, string>>>({})
  // A save refused with a field message scrolls to that field, frames it red and shakes it.
  const lastFieldReveal = useRef(0)
  // One create key for the form's whole life: a resend after a lost answer (network error) is replayed by the server
  // instead of creating the product twice; once the server answered with an error the next attempt gets a new key.
  const [productCreateKey] = useState(() => newIdempotencyKey('product-create'))
  // The editor's own navigation after a save (to the new product's page) is not asked about.
  const leavingAfterSave = useRef(false)
  const [variantPlatformPricing, setVariantPlatformPricing] = useState<{ rowKey: string; platform: VariantPlatformStatus } | null>(null)
  const [variantPlatformPricingDraft, setVariantPlatformPricingDraft] = useState<ChannelPricingDraft>({ listPrice: '', salePrice: '' })
  const [variantPlatformPricingSaving, setVariantPlatformPricingSaving] = useState(false)
  const initialEditMediaUrl = useRef('')
  const initialEditModelCode = useRef('')
  const initialEditDesi = useRef(1)
  const initialEditVariantMediaRefs = useRef<Record<string, string[]>>({})
  function showFeedback(title: string, message: string, kind: OperationFeedback['kind'], options: { persistent?: boolean; key?: string } = {}) {
    setFeedback({ title, message, kind, ...options })
  }
  const [optionEditRowKey, setOptionEditRowKey] = useState<string | null>(null)
  function updateField(name: keyof typeof form, value: string) { setForm(current => ({ ...current, [name]: value })) }
  const mediaAssignmentFormWasClean = useRef(false)
  // The running assignment save (WEB-SAYFALAR-9): Kaydet waits for it, and a refetch landing meanwhile is the editor's
  // own write, neither a refill nor another operation's change (seedForm).
  const mediaAutoSave = useRef<Promise<void> | null>(null)
  const mediaAutoSaveInFlight = useRef(false)
  return { copyRemovedMedia, error, setError, setNotice, operationToasts, setFeedback, submitting, setSubmitting, mediaAutoSaveTick, setMediaAutoSaveTick, calculateDesi, setCalculateDesi, desiCalculatorOpen, setDesiCalculatorOpen, form, setForm, attributeSelections, setAttributeSelections, attributeTextValues, setAttributeTextValues, variantAttributeIds, setVariantAttributeIds, variantRows, setVariantRows, draggedVariantKey, setDraggedVariantKey, dragOverVariantKey, setDragOverVariantKey, selectedChannelIds, setSelectedChannelIds, blockedPublishCards, setBlockedPublishCards, channelPricing, setChannelPricing, variantChannelPricing, setVariantChannelPricing, automaticBarcodeGeneration, setAutomaticBarcodeGeneration, marketplaceCodesOpen, setMarketplaceCodesOpen, quickEditOpen, setQuickEditOpen, seededProductId, setSeededProductId, marketplaceCodesVariant, setMarketplaceCodesVariant, attributeValueQueries, setAttributeValueQueries, initializedEditProductKey, initializedEditOptionsKey, initializedEditWebColorKey, wizardStep, setWizardStep, platformUpdateDialogOpen, setPlatformUpdateDialogOpen, platformUpdateConnectionIds, setPlatformUpdateConnectionIds, platformUpdatePreferences, setPlatformUpdatePreferences, savingPlatformUpdateChoice, setSavingPlatformUpdateChoice, platformUpdateChoice, pendingPlatformUpdate, lightboxImage, setLightboxImage, variantMediaModal, setVariantMediaModal, barcodeSkuMenuOpen, setBarcodeSkuMenuOpen, barcodeSkuActionRef, barcodePasteMenuOpen, setBarcodePasteMenuOpen, variantMobileGroup, setVariantMobileGroup, barcodePasteActionRef, pendingVariantCodeGeneration, setPendingVariantCodeGeneration, variantRowsRef, expandedOptionGroupIds, setExpandedOptionGroupIds, variantBulkEdit, setVariantBulkEdit, variantBulkEditFilterSelections, setVariantBulkEditFilterSelections, mediaFiles, setMediaFiles, deletingProductMediaKey, setDeletingProductMediaKey, mediaRemovalConfirmation, setMediaRemovalConfirmation, pendingFamilyMediaRemovals, setPendingFamilyMediaRemovals, familyMediaOrder, setFamilyMediaOrder, familyMediaOrderDirty, setFamilyMediaOrderDirty, draggedMedia, setDraggedMedia, dragOverMedia, setDragOverMedia, mediaStripOrder, setMediaStripOrder, sizeChartUrl, setSizeChartUrl, initialSizeChartUrl, sizeChartBeforeFamilyRemoval, sizeChartPicking, setSizeChartPicking, chartKey, pointerDraggedVariantKey, setPointerDraggedVariantKey, pointerDraggedVariantRef, pointerDragSourceRef, pointerDragIdRef, removedVariantIds, setRemovedVariantIds, removedVariantRows, setRemovedVariantRows, variantDeleteConfirmation, setVariantDeleteConfirmation, replacedPlaceholderIds, setReplacedPlaceholderIds, baselineRequest, requestBaseline, baseline, takeBaseline, adjustBaseline, webColorAutoEnabled, setWebColorAutoEnabled, manualWebColorValueId, setManualWebColorValueId, webColorSeed, webColorBaselineRequest, requestWebColorBaseline, staleProductVersion, setStaleProductVersion, allowReseedUntil, editorSaveInFlight, seededProduct, productAtSave, setProductAtSave, variantCodeConflicts, setVariantCodeConflicts, mediaFilesKeptOnReseed, mediaFileInputRef, offerPostKeys, initialCustomChannelIds, channelPricingBase, fieldErrors, setFieldErrors, requiredErrors, setRequiredErrors, lastFieldReveal, productCreateKey, leavingAfterSave, variantPlatformPricing, setVariantPlatformPricing, variantPlatformPricingDraft, setVariantPlatformPricingDraft, variantPlatformPricingSaving, setVariantPlatformPricingSaving, initialEditMediaUrl, initialEditModelCode, initialEditDesi, initialEditVariantMediaRefs, showFeedback, optionEditRowKey, setOptionEditRowKey, updateField, mediaAssignmentFormWasClean, mediaAutoSave, mediaAutoSaveInFlight }
}
export type EditorStateResult = ReturnType<typeof useEditorState>
