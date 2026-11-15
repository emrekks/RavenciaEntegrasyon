// Ürün ekleme ve düzenleme (/products/new, /products/:id).
import './product-editor-design-system.css'
import './product-editor-hydration.css'
import { lazy, Suspense, type CSSProperties, type DragEvent } from 'react'
import { Link, useLocation, useNavigate, useParams } from 'react-router'
import { createPortal } from 'react-dom'
import { useQueryClient } from '@tanstack/react-query'
import { FieldLabel, TitleHint, UiIcon, DialogSurface, UnsavedChangesGuard, StickyActionBar } from '../../../shared/components'
import { ErrorToast } from '../../../shared/components/NoticeToast'
import { platformLogoClass, platformLogoSource } from '../../../shared/platform-logos'
import { filterAttributeOptionValues, optionValueToAdd } from '../attribute-value-search'
import { OptionValueAddButton } from './OptionValueAddButton'
import { mediaImageKey, mediaStripOwnPosition } from '../product-media-editor'
import { variantBulkModeLabels, type VariantBulkEditField } from '../variant-bulk-edit'
import { VariantCodeMenu } from './VariantCodeMenu'
import { variantRemovalConfirmText } from '../variant-row-removal'
import { LocalImagePreview, MediaAddTile, MediaMoveButtons, MediaZoomButton } from './media-tiles'
import { MarketplaceCodesDialog } from './MarketplaceCodesDialog'
import { PlatformUpdateDialog, manualPriceStockNote } from './PlatformUpdateDialog'
import { publishCardVariantLinks } from './publish-card-variants'
import { VariantOptionEditDialog } from './VariantOptionEditDialog'
import { PriceSuggestionLink } from './PriceSuggestionDialog'
import { maxProductDesi } from '../product-desi'
import { SizeChartButton } from './SizeChartDialog'
import { isSizeChartRef } from './size-chart-order'
import { variantMediaHint, VariantImageIcon } from './variant-media-options'
import { variantCodeFieldIssueText, variantCodeFieldKey } from '../platform-code-rules'
import { clearAllMediaConfirmationText } from '../family-media-clear'
import { TURKISH_VAT_RATES, variantCodeKey } from '../product-editor-state'
import { readVariantMediaAssignmentDraft, updateVariantMediaAssignmentDraft, variantMediaAssignmentKey } from '../variant-media-assignments'
import { ModelCodeHolderNotice } from '../model-code-holder'
import { channelPricingInputs } from './marketplace-channel-pricing'
import { OptionValueChips, sortOptionValues } from './OptionValueChips'
import { ImageLightboxModal, Page, cleanOptionValue, isColorOptionName } from '../shared'
import { VariantStatusDot } from './VariantStatusDot'
import { variantStatus } from './variant-status'
import { PanelSelect } from '../../../shared/PanelSelect'
import { thumbnailSrcSet, thumbnailUrl } from '../../../shared/product-thumbnail'
import { isSizeOptionName, displayVariantOptionSignature, touchScreen, variantBulkEditLabels, rowMatchesVariantMediaValue } from './editor-helpers'
import { VariantDragHandleIcon, BarcodeFillIcon, BarcodePasteIcon, EditorFieldError, automaticCodeHelp, variantCodesReadOnlyTitle, RequiredLabel, EditorShell } from './EditorBits'
import { VariantHeaderActionMenu } from './VariantHeaderActionMenu'
import { VariantMediaPickerModal } from './VariantMediaPickerModal'
import { BulkVariantPlatformPricingModal } from './BulkVariantPlatformPricingModal'
import { RichTextEditor } from './RichTextEditor'
import { CategoryAttributeMappingPanel } from './CategoryAttributeMappingPanel'
import { VariantFilterDropdown } from './VariantFilterDropdown'
import { PublishPlatformCardSkeletons, PublishPlatformCard } from './PublishPlatformCard'
import type { EditorContext } from './editor-context'
import { useEditorState } from './use-editor-state'
import { useEditorMediaPointer } from './use-editor-media-pointer'
import { useEditorDirtyState } from './use-editor-dirty-state'
import { useVariantCodeMenus } from './use-variant-code-menus'
import { useEditorData } from './use-editor-data'
import { useEditorSeed } from './use-editor-seed'
import { useVariantRowDrag } from './use-variant-row-drag'
import { useVariantMediaAutoSave } from './use-variant-media-autosave'
import { useEditorView } from './use-editor-view'
import { useAttributeActions } from './use-attribute-actions'
import { useVariantActions } from './use-variant-actions'
import { useVariantCodeActions } from './use-variant-code-actions'
import { useEditorMediaActions } from './use-editor-media-actions'
import { usePlatformUpdate } from './use-platform-update'
import { useChannelPricing } from './use-channel-pricing'
import { useEditorSubmit } from './use-editor-submit'
export { BulkVariantPlatformPricingModal } from './BulkVariantPlatformPricingModal'
export { PublishPlatformCard, cardLookupKey, publishCardState } from './PublishPlatformCard'
// Ürün ekleme ve düzenleme (/products/new, /products/:id).

// The products list's Toplu Düzenleme window, opened on the editor's unsaved rows (Toplu Düzenle in the variant toolbar).
const ProductQuickEditModal = lazy(() => import('../products/ProductQuickEditModal').then(module => ({ default: module.ProductQuickEditModal })))

export function NewProductPage({ editProductId, publishOnly = false }: { editProductId?: string; publishOnly?: boolean } = {}) {
  const client = useQueryClient();
  const location = useLocation()
  const navigate = useNavigate()
  const routeState = location.state as { duplicateProductId?: unknown; wizardStep?: unknown } | null
  const duplicateProductId = typeof routeState?.duplicateProductId === 'string' ? routeState.duplicateProductId : undefined
  const c0 = { editProductId, publishOnly, client, location, navigate, routeState, duplicateProductId }
  const c1 = { ...c0, ...useEditorState(c0) }
  const c2 = { ...c1, ...useEditorMediaPointer(c1) }
  const c3 = { ...c2, ...useEditorDirtyState(c2) }
  const c4 = { ...c3, ...useVariantCodeMenus(c3) }
  const c5 = { ...c4, ...useEditorData(c4) }
  const c6 = { ...c5, ...useEditorSeed(c5) }
  const c7 = { ...c6, ...useVariantRowDrag(c6) }
  const c8 = { ...c7, ...useVariantMediaAutoSave(c7) }
  const c9 = { ...c8, ...useEditorView(c8) }
  const c10 = { ...c9, ...useAttributeActions(c9) }
  const c11 = { ...c10, ...useVariantActions(c10) }
  const c12 = { ...c11, ...useVariantCodeActions(c11) }
  const c13 = { ...c12, ...useEditorMediaActions(c12) }
  const c14 = { ...c13, ...usePlatformUpdate(c13) }
  const c15 = { ...c14, ...useChannelPricing(c14) }
  const c16 = { ...c15, ...useEditorSubmit(c15) }
  const editor: EditorContext = c16
  const { error, setFeedback, submitting, setCalculateDesi, desiCalculatorOpen, setDesiCalculatorOpen, form, attributeSelections, setAttributeSelections, attributeTextValues, setAttributeTextValues, variantAttributeIds, setVariantAttributeIds, variantRows, setVariantRows, draggedVariantKey, dragOverVariantKey, selectedChannelIds, setSelectedChannelIds, setBlockedPublishCards, channelPricing, variantChannelPricing, automaticBarcodeGeneration, setAutomaticBarcodeGeneration, marketplaceCodesOpen, setMarketplaceCodesOpen, quickEditOpen, setQuickEditOpen, marketplaceCodesVariant, setMarketplaceCodesVariant, attributeValueQueries, setAttributeValueQueries, wizardStep, setWizardStep, platformUpdateDialogOpen, setPlatformUpdateDialogOpen, platformUpdateConnectionIds, setPlatformUpdateConnectionIds, platformUpdatePreferences, savingPlatformUpdateChoice, lightboxImage, setLightboxImage, variantMediaModal, setVariantMediaModal, barcodeSkuMenuOpen, setBarcodeSkuMenuOpen, barcodeSkuActionRef, barcodePasteMenuOpen, setBarcodePasteMenuOpen, variantMobileGroup, setVariantMobileGroup, barcodePasteActionRef, pendingVariantCodeGeneration, setPendingVariantCodeGeneration, expandedOptionGroupIds, setExpandedOptionGroupIds, variantBulkEdit, setVariantBulkEdit, variantBulkEditFilterSelections, setVariantBulkEditFilterSelections, mediaFiles, deletingProductMediaKey, mediaRemovalConfirmation, setMediaRemovalConfirmation, pendingFamilyMediaRemovals, draggedMedia, setDraggedMedia, dragOverMedia, setDragOverMedia, sizeChartUrl, setSizeChartPicking, chartKey, variantDeleteConfirmation, setVariantDeleteConfirmation, webColorAutoEnabled, setWebColorAutoEnabled, manualWebColorValueId, setManualWebColorValueId, staleProductVersion, mediaFileInputRef, fieldErrors, setFieldErrors, leavingAfterSave, variantPlatformPricing, setVariantPlatformPricing, variantPlatformPricingSaving, showFeedback, optionEditRowKey, setOptionEditRowKey, updateField, touchPointer, mediaTouch, suppressMediaClick, mediaStripRef, editorDirty, productToEdit, productToCopy, categories, brands, connections, editorHydrating, requirements, leafCategories, activeBrands, missingRequiredFields, requiredError, requiredFieldInvalid, variantStockTotal, desi, mediaUrls, allRequirements, mappedRequirements, webColorRequirement, visibleOptionRequirements, reloadEditedProduct, beginVariantPointerDrag, draftSaveAvailable, editingDraftProduct, marketplacePricingOpen, setMarketplacePricingOpen, publishLinkRows, platformCards, variantCodeIssues, variantCodeDuplicateRows, heldVariantCodes, variantCodeIssueSummary, codeRuleSaveBlock, modelCodeHolder, modelCodeCreateBlock, goToFirstVariantCodeIssue, blockedByVariantCodeRules, variantPricingPlatforms, productUpdateTargets, platformUpdateFamilyReach, familyMediaItems, draftMediaKeys, sizeChartSizes, sizeChartDisabledReason, mediaStrip, sizeChartPickable, pickingSizeChart, familyOnlyMediaItems, pictureColourCaption, outgoingByRow, trendyolMixedColours, hasMediaTiles, mediaUploadHint, mediaChoices, bulkMediaGroups, variantFilterGroups, variantBulkEditFilterGroups, variantBulkEditMatchingRows, barcodeRowCount, emptySkuBarcodeRowCount, emptyBarcodeRowCount, skuRowCount, emptySkuRowCount, pendingVariantCodeConfirm, variantCodeGenerationPreview, selectedBulkMediaMatchCount, selectedBulkMediaGroupMatchCount, selectedVariantMediaRefs, productChecks, publicationAttributeSelections, canAddVariantCombinations, selectedVariantPlatformRow, showVariantStockTotal, toggleAttributeValue, addingOptionValueFor, addOptionValue, rowOptionEntries, optionAttributeFor, rowHasOptionValue, applyVariantOptionChanges, toggleWebColorAuto, generateVariants, clearVariants, requestVariantRowRemoval, confirmVariantRowRemoval, updateVariantRow, openVariantBulkEdit, applyVariantBulkEdit, openVariantMediaPicker, openBulkVariantMediaPicker, applyVariantMediaSelection, applyBarcodeToSku, requestVariantCodeChange, confirmVariantCodeGeneration, pasteBarcodesFromClipboard, handleMediaFiles, applySizeChart, markSizeChart, reorderMedia, startMediaTouch, moveMediaTouch, endMediaTouch, cancelMediaTouch, removeUploadedMedia, clearAllMedia, removeProductMedia, removeFamilyMedia, undoPendingFamilyMediaRemovals, confirmMediaRemoval, mediaFileInput, updateChannel, openPlatformUpdateDialog, changePlatformUpdatePreferences, savePlatformUpdateChoice, sendPlatformUpdateChoice, startQuickPlatformUpdate, toggleCustomChannelPrice, updateChannelPrice, openVariantPlatformPricing, saveVariantPlatformPricingMatrix, handleInvalid, dismissProductError, blockedByRequiredFields, submit } = editor

  if (duplicateProductId && productToCopy.isLoading) return <Page title="Yeni Ürün Ekle" eyebrow="Katalog"><div className="panel" role="status">Kaynak ürünün bilgileri forma hazırlanıyor…</div></Page>
  if (duplicateProductId && productToCopy.isError) return <Page title="Yeni Ürün Ekle" eyebrow="Katalog"><ErrorToast title="Kopyalanacak Ürün Yüklenemedi" error={productToCopy.error} /><Link className="button-link secondary" to="/products">Ürünlere Dön</Link></Page>

  return <EditorShell publishOnly={publishOnly} className={`product-add-page${editProductId ? ' product-edit-page' : ''}${editorHydrating ? ' is-hydrating' : ''}`} title={editProductId ? productToEdit.data?.title || 'Ürün Düzenle' : 'Yeni Ürün Ekle'} eyebrow={editProductId ? 'Ürün Düzenle' : 'Katalog'} meta={editProductId && productToEdit.data?.modelCode ? <p className="product-page-heading-meta">Model Kodu: <b>{productToEdit.data.modelCode}</b></p> : editProductId && productToEdit.isPending ? <p className="product-page-heading-meta" aria-hidden="true">Model Kodu: <b>…</b></p> : undefined}>
    {!publishOnly && <UnsavedChangesGuard when={editorDirty} allowNavigationRef={leavingAfterSave} />}
    {staleProductVersion != null && <div className="product-stale-banner" role="alert"><span><strong>Ürün başka bir işlemle güncellendi.</strong> Kaydedilmemiş değişiklikleriniz korunuyor; kaydederseniz sunucu sürüm çakışmasını bildirir.</span><button type="button" className="secondary" onClick={reloadEditedProduct}>Güncel Ürünü Yükle</button></div>}
    {quickEditOpen && <Suspense fallback={null}><ProductQuickEditModal mode="both" onClose={() => setQuickEditOpen(false)} draft={{ title: form.title, modelCode: form.modelCode, rows: variantRows, onApply: changes => {
      // Only the table changes: the rows are saved (and sent on) with the page's Kaydet, as a hand-typed cell would be.
      const byKey = new Map(changes.map(change => [change.key, change]))
      setVariantRows(rows => rows.map(row => { const change = byKey.get(row.key); return change ? { ...row, stock: change.stock, listPrice: change.listPrice, salePrice: change.salePrice, costPrice: change.costPrice } : row }))
      if (changes.length) showFeedback('Toplu Düzenleme Uygulandı', `${changes.length} varyantın değerleri tabloya yazıldı. Kalıcı olması için Kaydet’e basın.`, 'success')
    } }} /></Suspense>}
    {variantPlatformPricing && selectedVariantPlatformRow && <BulkVariantPlatformPricingModal row={selectedVariantPlatformRow} rows={variantRows} platforms={selectedVariantPlatformRow.platformStatuses?.length ? selectedVariantPlatformRow.platformStatuses : variantPricingPlatforms} savedDrafts={variantChannelPricing} productName={form.title} modelCode={form.modelCode} filterGroups={variantFilterGroups.filter(group => isColorOptionName(group.name) || isSizeOptionName(group.name))} matchesFilterValue={rowMatchesVariantMediaValue} saving={variantPlatformPricingSaving} onClose={() => setVariantPlatformPricing(null)} onSave={drafts => void saveVariantPlatformPricingMatrix(drafts)} />}
    {!publishOnly && <div className="product-add-wizardbar"><div className="product-add-stepper"><div className="product-add-steptabs rv-tabs rv-view-tabs" role="tablist" aria-label={editProductId ? 'Ürün düzenleme adımları' : 'Ürün ekleme adımları'}><button type="button" className={wizardStep === 1 ? 'is-active' : ''} role="tab" aria-selected={wizardStep === 1} onClick={() => setWizardStep(1)}><span className="rv-tab-label"><span className="rv-step-num">1</span><span>Ürün Bilgileri<span className="product-step-label-more"> ve Varyantlar</span></span></span></button><button type="button" className={wizardStep === 2 ? 'is-active' : ''} role="tab" aria-selected={wizardStep === 2} onClick={() => setWizardStep(2)}><span className="rv-tab-label"><span className="rv-step-num">2</span><span>Yayınlama</span></span></button></div></div></div>}<form id="product-creation-form" className="product-creation-workspace product-add-workspace" data-wizard-step={wizardStep} aria-busy={editorHydrating || submitting || undefined} inert={editorHydrating || submitting || undefined} onSubmit={submit} onInvalidCapture={handleInvalid} onChange={dismissProductError} noValidate>
    <div className="product-top-layout">
      <section className="panel product-step-card product-basics-card">
        <div className="editor-section-title"><span>1</span><div><h2>Temel Ürün Bilgileri</h2></div></div>
        <div className="product-step-grid product-basics-grid">
          <label className="product-title-field"><RequiredLabel>Ürün Adı</RequiredLabel><input value={form.title} aria-invalid={fieldErrors.title || requiredFieldInvalid('title') ? true : undefined} onChange={event => updateField('title', event.target.value)} required maxLength={320} /><EditorFieldError message={requiredError('title') ?? fieldErrors.title} /></label>
          <label htmlFor="product-editor-status">Satış Durumu<PanelSelect id="product-editor-status" ariaLabel="Satış Durumu" value={form.status} onChange={value => updateField('status', value)} options={[{ value: 'ACTIVE', label: 'Satışa Açık' }, { value: 'ARCHIVED', label: 'Satışa Kapalı' }]} /></label>
          <label className="product-brand-field" htmlFor="product-editor-brand"><RequiredLabel>Marka</RequiredLabel><PanelSelect id="product-editor-brand" className={requiredFieldInvalid('brandId') ? 'is-invalid' : undefined} ariaLabel="Marka" value={form.brandId} onChange={value => updateField('brandId', value)} options={[{ value: '', label: 'Marka Seçin' }, ...activeBrands.map(item => ({ value: item.id, label: item.name }))]} /><EditorFieldError message={requiredError('brandId') ?? fieldErrors.brandId} /></label>
          <label className="product-category-field" htmlFor="product-editor-category"><RequiredLabel>Panel Kategorisi</RequiredLabel><PanelSelect id="product-editor-category" className={requiredFieldInvalid('categoryId') ? 'is-invalid' : undefined} ariaLabel="Panel kategorisi" value={form.categoryId} onChange={value => { updateField('categoryId', value); setAttributeSelections({}); setAttributeTextValues({}); setVariantAttributeIds([]); setWebColorAutoEnabled(true); setManualWebColorValueId(''); if (!editProductId) setVariantRows([]) }} options={[{ value: '', label: 'Kategori Seçin' }, ...leafCategories.map(item => ({ value: item.id, label: item.path }))]} /><EditorFieldError message={requiredError('categoryId') ?? fieldErrors.categoryId} /></label>
          <label><RequiredLabel>Model Kodu</RequiredLabel><input className="technical-field model-code-value" value={form.modelCode} aria-invalid={modelCodeCreateBlock || fieldErrors.modelCode || requiredFieldInvalid('modelCode') ? true : undefined} onChange={event => { updateField('modelCode', event.target.value); if (fieldErrors.modelCode) setFieldErrors(current => ({ ...current, modelCode: undefined })) }} />{modelCodeCreateBlock ? <ModelCodeHolderNotice holder={modelCodeHolder} /> : <EditorFieldError message={requiredError('modelCode') ?? fieldErrors.modelCode} />}</label>
          <div className="product-identifiers-grid">
            {/* A card with variants shows its first variant's codes read-only (selectable, not required): each variant's
                codes are edited in the table (owner, 11 Nov 2026: "Salt okunur kalsın"). Without variants they are the
                card's own codes, required. Automatic barcodes (made from the model code) keep them closed. */}
            {variantRows.length > 1 && !automaticBarcodeGeneration
              ? <>
                <label title={variantCodesReadOnlyTitle}>Stok Kodu<input className="technical-field sku-value is-read-only" value={form.baseSku || variantRows[0]?.sku || ''} readOnly title={variantCodesReadOnlyTitle} /></label>
                <label title={variantCodesReadOnlyTitle}>Barkod<input className="technical-field barcode-value is-read-only" value={form.barcode || variantRows[0]?.barcode || ''} readOnly title={variantCodesReadOnlyTitle} /></label>
              </>
              : <>
                <label>{automaticBarcodeGeneration ? <FieldLabel text="Stok Kodu" help={automaticCodeHelp('stock')} /> : <RequiredLabel>Stok Kodu</RequiredLabel>}<input className="technical-field sku-value" value={form.baseSku} aria-invalid={requiredFieldInvalid('baseSku')} onChange={event => updateField('baseSku', event.target.value)} placeholder={automaticBarcodeGeneration ? '' : 'RAV-BLUZ'} disabled={automaticBarcodeGeneration} /><EditorFieldError message={requiredError('baseSku')} /></label>
                <label>{automaticBarcodeGeneration ? <FieldLabel text="Barkod" help={automaticCodeHelp('barcode')} /> : <RequiredLabel>Barkod</RequiredLabel>}<input className="technical-field barcode-value" value={form.barcode} aria-invalid={requiredFieldInvalid('barcode')} onChange={event => updateField('barcode', event.target.value)} placeholder={automaticBarcodeGeneration ? '' : 'EAN / barkod'} disabled={automaticBarcodeGeneration} /><EditorFieldError message={requiredError('barcode')} /></label>
              </>}
            <label className="desi-input-field"><RequiredLabel>Desi</RequiredLabel><span className="desi-inline-control"><input value={form.desi} aria-invalid={requiredFieldInvalid('desi')} onChange={event => { setCalculateDesi(false); updateField('desi', event.target.value) }} type="number" min="0.01" max={maxProductDesi} step="0.01" placeholder="Girilmemiş" required /><button type="button" className="secondary" onClick={() => setDesiCalculatorOpen(true)}>Hesapla</button></span><EditorFieldError message={requiredError('desi')} /></label>
            <label className="warranty-input-field" title="0: garanti yok"><RequiredLabel>Garanti Süresi (Ay)</RequiredLabel><input value={form.warrantyMonths} aria-invalid={requiredFieldInvalid('warrantyMonths')} onChange={event => updateField('warrantyMonths', event.target.value)} type="number" min="0" max="240" step="1" inputMode="numeric" placeholder="0 = garanti yok" /><EditorFieldError message={requiredError('warrantyMonths')} /></label>
            <label className="delivery-days-input-field" title="Siparişin kargoya teslim süresi; pazaryerlerine teslimat (sevkiyat) süresi olarak gider."><RequiredLabel>Teslimat Süresi (Gün)</RequiredLabel><input value={form.deliveryDays} aria-invalid={requiredFieldInvalid('deliveryDays')} onChange={event => updateField('deliveryDays', event.target.value)} type="number" min="1" max="30" step="1" inputMode="numeric" required /><EditorFieldError message={requiredError('deliveryDays')} /></label>
          </div>
          <div className="wide product-description-field"><span>Açıklama</span><RichTextEditor value={form.description} onChange={value => updateField('description', value)} /><EditorFieldError message={fieldErrors.description} /></div>
        </div>
      </section>
      <div className="product-top-sidebar">
        <section className="panel product-step-card product-pricing-card">
          <div className="editor-section-title"><span>2</span><div className="price-suggestion-title"><h2>Fiyat, Stok ve Vergi<TitleHint label="Fiyat, Stok ve Vergi">Başlangıç değerleri varyantlara uygulanır.</TitleHint></h2>
            {/* On the new and the edit page alike. A card with variants takes the suggested price on every variant row too
                (each row's list price is raised to it only when lower); the cost comes from the card or its first costed row. */}
            <PriceSuggestionLink fields={{ cost: form.costPrice.trim() !== '' ? form.costPrice : String(variantRows.find(row => row.costPrice != null && row.costPrice > 0)?.costPrice ?? ''), vatRate: form.vatRate, vatIncluded: form.vatIncluded !== 'EXCLUDED', listPrice: form.listPrice }} onApply={prices => {
              updateField('salePrice', prices.salePrice); updateField('listPrice', prices.listPrice)
              const sale = Number(prices.salePrice)
              if (variantRows.length && Number.isFinite(sale)) setVariantRows(rows => rows.map(row => ({ ...row, salePrice: sale, listPrice: row.listPrice >= sale ? row.listPrice : sale })))
            }} /></div></div>
          <div className="product-step-grid">
            {/* Open on a card with variants too (owner, 10 Nov 2026): there they are the card's default price and the first
                price of every variant row added later; the rows already in the table keep theirs. Decimal comma read. */}
            <label title={variantRows.length > 1 ? 'Yeni eklenen varyantların ilk liste fiyatı; mevcut varyantların fiyatı değişmez.' : undefined}><RequiredLabel>Liste Fiyatı</RequiredLabel><input value={form.listPrice} aria-invalid={requiredFieldInvalid('listPrice')} onChange={event => updateField('listPrice', event.target.value)} type="text" inputMode="decimal" placeholder="0,00" /><EditorFieldError message={requiredError('listPrice')} /></label>
            <label title={variantRows.length > 1 ? 'Yeni eklenen varyantların ilk satış fiyatı; mevcut varyantların fiyatı değişmez.' : undefined}><RequiredLabel>Satış Fiyatı</RequiredLabel><input value={form.salePrice} aria-invalid={requiredFieldInvalid('salePrice')} onChange={event => updateField('salePrice', event.target.value)} type="text" inputMode="decimal" placeholder="0,00" /><EditorFieldError message={requiredError('salePrice') ?? fieldErrors.prices} /></label>
            {/* Maliyet takes two columns: the grid stays even, so Beden Tablosu sits beside Güvenlik Stoğu, not alone on a row. */}
            {variantRows.length <= 1 && <label className="product-cost-field">Maliyet<input value={form.costPrice} onChange={event => { updateField('costPrice', event.target.value); if (variantRows.length === 1) updateVariantRow(variantRows[0].key, 'costPrice', event.target.value) }} type="number" min="0" step="0.01" /></label>}
            <label htmlFor="product-editor-currency">Para Birimi<PanelSelect id="product-editor-currency" ariaLabel="Para Birimi" value={form.currency} onChange={value => updateField('currency', value)} options={[{ value: 'TRY', label: 'TRY' }, { value: 'USD', label: 'USD' }, { value: 'EUR', label: 'EUR' }]} /></label>
            <label htmlFor="product-editor-vat-rate"><span>KDV Oranı<b className="required-marker" aria-hidden="true">*</b></span><PanelSelect id="product-editor-vat-rate" ariaLabel="KDV Oranı" placeholder="Seçin" className={fieldErrors.vatRate ? 'is-invalid' : undefined} value={form.vatRate} onChange={value => { updateField('vatRate', value); if (value) setFieldErrors(current => ({ ...current, vatRate: undefined })) }} options={[...(form.vatRate !== '' && !TURKISH_VAT_RATES.includes(form.vatRate) ? [{ value: form.vatRate, label: `%${form.vatRate}` }] : []), ...TURKISH_VAT_RATES.map(rate => ({ value: rate, label: `%${rate}` }))]} /><EditorFieldError message={fieldErrors.vatRate} /></label>
            <label htmlFor="product-editor-vat-included">KDV Dahil mi<PanelSelect id="product-editor-vat-included" ariaLabel="KDV Dahil mi" value={form.vatIncluded} onChange={value => updateField('vatIncluded', value)} options={[{ value: 'INCLUDED', label: 'Evet' }, { value: 'EXCLUDED', label: 'Hayır' }]} /></label>
            <label title={showVariantStockTotal || variantRows.length > 1 ? 'Varyantların toplam stoğu' : undefined}>{showVariantStockTotal || variantRows.length > 1 ? 'Toplam Stok' : 'Stok'}<input value={showVariantStockTotal || variantRows.length > 1 ? String(variantStockTotal) : form.initialStock} onChange={event => updateField('initialStock', event.target.value)} type="number" min="0" step="1" readOnly={showVariantStockTotal || variantRows.length > 1} /></label>
            <label>Güvenlik Stoğu<input value={form.safetyStock} onChange={event => updateField('safetyStock', event.target.value)} type="number" min="0" step="1" /></label>
            <SizeChartButton productId={editProductId ?? null} sizes={sizeChartSizes} disabledReason={sizeChartDisabledReason} onUse={applySizeChart} />
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
      isLoading={editorHydrating || requirements.isLoading}
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
    {desiCalculatorOpen && <div className="workspace-modal-backdrop" role="presentation" onMouseDown={() => setDesiCalculatorOpen(false)}><DialogSurface as="section" onClose={() => setDesiCalculatorOpen(false)} className="workspace-modal desi-calculator-modal" aria-labelledby="desi-calculator-title" onMouseDown={event => event.stopPropagation()}><header><div><h2 id="desi-calculator-title">Desi Hesapla<TitleHint label="Desi Hesapla">En × Boy × Yükseklik / 3000 formülü kullanılır.</TitleHint></h2></div><button type="button" className="modal-close" onClick={() => setDesiCalculatorOpen(false)} aria-label="Pencereyi kapat"><UiIcon name="close" /></button></header><div className="desi-calculator-body"><div className="product-step-grid"><label>Ağırlık (kg)<input value={form.weight} onChange={event => updateField('weight', event.target.value)} type="number" min="0" step="0.01" /></label><label>En (cm)<input value={form.width} onChange={event => updateField('width', event.target.value)} type="number" min="0" step="0.1" /></label><label>Boy (cm)<input value={form.length} onChange={event => updateField('length', event.target.value)} type="number" min="0" step="0.1" /></label><label>Yükseklik (cm)<input value={form.height} onChange={event => updateField('height', event.target.value)} type="number" min="0" step="0.1" /></label></div><div className="calculated-field"><small>Hesaplanan desi</small><strong>{desi ? desi.toLocaleString('tr-TR', { maximumFractionDigits: 2 }) : 'Ölçüleri girin'}</strong></div></div><footer><button type="button" className="secondary" onClick={() => setDesiCalculatorOpen(false)}>İptal</button><button type="button" disabled={!desi} onClick={() => { updateField('desi', String(Number(desi.toFixed(2)))); setCalculateDesi(true); setDesiCalculatorOpen(false) }}>Uygula</button></footer></DialogSurface></div>}

    <div className="product-layout-grid"><div className="product-main-stack">
      <section className="panel product-step-card product-media-card">
        <div className="editor-section-title">
          <span>4</span>
            <div><h2>Görseller<TitleHint label="Görseller">JPEG veya PNG yükleyin, sürükleyerek sıralayın. Modelin tüm renklerinin görselleri tek galeride görünür. Kaydet her görseli Seçeneklere Görsel Ata ile atandığı rengin kartına yazar; beden tablosu ve atanmamış yeni görseller her renge gider. Kutucuktaki ad görselin gideceği renktir.</TitleHint></h2></div>
            <div className="product-media-header-actions">
              {sizeChartPickable && <button type="button" className="secondary product-media-size-chart-button" aria-pressed={pickingSizeChart} disabled={submitting} title="Kayıtlı görsellerden birini (pazaryerinden gelenler ve diğer renklerinkiler dahil) beden tablosu olarak işaretleyin; her renkte son sıraya alınır." aria-label="Beden Tablosu Seç" onClick={() => setSizeChartPicking(current => !current)}><span className="media-action-full">Beden Tablosu Seç</span><span className="media-action-short" aria-hidden="true">Beden Tablosu</span></button>}
              {editProductId && sizeChartUrl && <button type="button" className="secondary product-media-size-chart-button" disabled={submitting} title="Beden tablosu işaretini kaldırın; görsel kartta kalır." aria-label="İşareti Kaldır" onClick={() => markSizeChart(null)}>İşareti Kaldır</button>}
              {(mediaUrls.length > 0 || mediaFiles.length > 0 || familyOnlyMediaItems.length > 0 || variantRows.some(row => row.mediaRefs.length > 0)) && <button type="button" className="secondary product-media-clear-all-button" aria-label="Tümünü Temizle" title="Tümünü Temizle" onClick={() => clearAllMedia()}><span className="media-action-full">Tümünü Temizle</span><span className="media-action-short" aria-hidden="true">Temizle</span></button>}
            </div>
        </div>
        {/* The wide box while there is no picture; after the first one the plus tile at the end of the strip opens the
            same picker (one input, rendered where its opener is). */}
        {!hasMediaTiles && <label className="upload-ghost-box product-media-upload">
          {mediaFileInput}
          <strong>Ürün Görsellerini Dosya Olarak Seç</strong>
          <small>{mediaUploadHint}</small>
        </label>}
        {hasMediaTiles && <span className="product-media-file-slot">{mediaFileInput}</span>}
        {pickingSizeChart && hasMediaTiles && <div className="size-chart-pick-hint" role="status"><span>Beden tablosu olacak görsele dokunun</span><button type="button" className="secondary" onClick={() => setSizeChartPicking(false)}>Vazgeç</button></div>}
        {hasMediaTiles && <div ref={mediaStripRef} className={`media-preview-strip${pickingSizeChart ? ' is-size-chart-picking' : ''}`} onPointerDown={pickingSizeChart ? undefined : startMediaTouch} onPointerMove={moveMediaTouch} onPointerUp={endMediaTouch} onPointerCancel={cancelMediaTouch} onContextMenu={event => { if (mediaTouch.current) event.preventDefault() }} onClickCapture={event => { if (suppressMediaClick.current) { event.preventDefault(); event.stopPropagation() } }}>
          {/* One ordered strip: saved pictures, the family's and new files mix; the first own picture is the main one. */}
          {mediaStrip.map((ref, index) => {
            const ownPosition = mediaStripOwnPosition(mediaStrip, index, draftMediaKeys)
            const mediaDrag = {
              onDragStart: (event: DragEvent<HTMLElement>) => { setDraggedMedia(index); event.dataTransfer.effectAllowed = 'move'; event.dataTransfer.setData('text/plain', `media:${index}`) },
              onDragOver: (event: DragEvent<HTMLElement>) => { if (draggedMedia === null) return; event.preventDefault(); event.dataTransfer.dropEffect = 'move'; setDragOverMedia(index) },
              onDrop: (event: DragEvent<HTMLElement>) => { event.preventDefault(); const transferValue = event.dataTransfer.getData('text/plain'); const transferIndex = transferValue.startsWith('media:') ? Number(transferValue.slice(6)) : draggedMedia ?? -1; if (draggedMedia !== null && Number.isInteger(transferIndex)) reorderMedia(transferIndex, index) },
              onDragEnd: () => { setDraggedMedia(null); setDragOverMedia(null) }
            }
            if (ref.startsWith('file|')) {
              const item = mediaFiles.find(file => file.id === ref.slice(5))
              return item && <LocalImagePreview key={item.id} mediaIndex={index} file={item.file} alt={`${form.title || 'Ürün'} ${index + 1}`} caption={pictureColourCaption(ref) || (ownPosition === 0 ? 'Ana görsel' : item.file.name)} draggable={!touchPointer && !submitting && !pickingSizeChart} mediaCount={mediaStrip.length} onMove={target => reorderMedia(index, target)} moveDisabled={submitting} isDragOver={dragOverMedia === index} {...mediaDrag} onRemove={() => removeUploadedMedia(item.id)} onZoom={url => setLightboxImage({ url, title: form.title || 'Ürün Görseli', modelCode: form.modelCode })} />
            }
            const url = ref.slice(4)
            const ownedUrl = mediaUrls.find(current => mediaImageKey(current) === mediaImageKey(url))
            const familyItem = familyMediaItems.find(item => mediaImageKey(item.url) === mediaImageKey(url))
            const isProductMedia = ownedUrl !== undefined
            const deletingProductMedia = deletingProductMediaKey === mediaImageKey(url)
            const isSizeChart = isSizeChartRef(ref, chartKey)
            const pickable = pickingSizeChart
            return <figure key={`family-${mediaImageKey(url)}`} data-media-kind="url" data-media-index={index} data-size-chart={isSizeChart || undefined} className={`image-preview-card media-sortable${isProductMedia ? '' : ' family-media-preview'}${pickable ? ' is-size-chart-pickable' : ''} ${dragOverMedia === index ? 'is-media-drag-over' : ''}`} draggable={!touchPointer && !submitting && !deletingProductMediaKey && !isSizeChart && !pickingSizeChart} {...mediaDrag}>
              <img src={thumbnailUrl(url)} srcSet={thumbnailSrcSet(url)} sizes="88px" alt={`${form.title || 'Ürün'} ${index + 1}`} className="media-tile-image" draggable={false} />
              {isSizeChart && <span className="size-chart-badge">Beden Tablosu</span>}
              {pickable && <button type="button" className="size-chart-pick-target" aria-label={isSizeChart ? 'Bu görsel zaten beden tablosu' : 'Bu görseli beden tablosu olarak işaretle'} onClick={event => { event.stopPropagation(); if (isSizeChart) setSizeChartPicking(false); else markSizeChart(ownedUrl ?? url) }} />}
              <MediaZoomButton onZoom={() => setLightboxImage({ url, title: isProductMedia ? form.title || 'Ürün Görseli' : `${form.title || 'Ürün'} · Renk Ailesi`, modelCode: form.modelCode })} />
              {isProductMedia
                ? <button type="button" className="image-remove-btn" disabled={deletingProductMedia || submitting} title="Görseli modelin tüm renklerinden hemen kaldır" aria-label="Görseli modelin tüm renklerinden hemen kaldır" onClick={event => { event.stopPropagation(); if (ownedUrl) void removeProductMedia(ownedUrl) }}><UiIcon name={deletingProductMedia ? 'loader' : 'close'} /></button>
                : <button type="button" className="image-remove-btn" disabled={!familyItem || submitting} title="Renk ailesi görselini kaldır; Kaydet’te diğer renk ürünlerinden de kaldırılır" aria-label="Renk ailesi görselini kaldır" onClick={event => { event.stopPropagation(); if (familyItem) removeFamilyMedia(familyItem) }}><UiIcon name="close" /></button>}
              <MediaMoveButtons index={index} count={mediaStrip.length} onMove={target => reorderMedia(index, target)} disabled={submitting || Boolean(deletingProductMediaKey)} />
              <figcaption>{pictureColourCaption(ref) || (isProductMedia ? (ownPosition === 0 ? 'Ana görsel' : `${ownPosition + 1}. görsel`) : 'Renk varyantı görseli')}<span className="media-drag-hint"> · sürükle</span></figcaption>
            </figure>
          })}
          <MediaAddTile hint={mediaUploadHint} disabled={submitting} onOpen={() => mediaFileInputRef.current?.click()} onFiles={handleMediaFiles} />
        </div>}
      </section>

      <section className="panel product-step-card product-options-card">
        <div className="editor-section-title product-options-header">
          <span>5</span>
          <div>
            <h2>Ürün Seçenekleri<TitleHint label="Ürün Seçenekleri">Seçenek grubu ve değerlerini seçin. Kayıtlı Renk ve Beden değerleri otomatik işaretlenir; yeni seçimler “Ürünleri Ekle” ile varyant satırı olur.</TitleHint></h2>
          </div>
        </div>
        {editorHydrating || requirements.isLoading ? (
          <p>Kategori özellikleri yükleniyor…</p>
        ) : !form.categoryId ? (
          <div className="unknown"><strong>Önce kategori seçin</strong><p>Kategori seçildiğinde o kategoriye bağlanan özellikler burada görünür.</p></div>
        ) : requirements.isError ? (
          <div className="unknown"><strong>Kategori özellikleri alınamadı</strong><p>Önce kategori eşleme ekranında ilgili kategorinin özellik başlıklarını hazırlayın.</p></div>
        ) : (
          <div className="attribute-builder-list">
            {visibleOptionRequirements.map(item => {
              const expandable = item.attribute.values.length > 0
              const expanded = !expandable || (expandedOptionGroupIds[item.attributeId] ?? false)
              const valueQuery = attributeValueQueries[item.attributeId] ?? ''
              const sortedValues = sortOptionValues(item.attribute.name, item.attribute.values)
              const filteredValues = filterAttributeOptionValues(sortedValues, valueQuery)
              return (
              <article className={`attribute-builder-card ${expanded ? 'is-open' : ''}`} key={item.attributeId}>
                <button type="button" className="attribute-builder-disclosure" aria-expanded={expanded} aria-controls={expanded ? `option-values-${item.attributeId}` : undefined} disabled={!expandable} title={expanded ? 'Bütün seçenek gruplarını kapat' : 'Bütün seçenek gruplarını birlikte aç'} onClick={() => { if (window.matchMedia('(max-width: 760px)').matches) { setExpandedOptionGroupIds(current => ({ ...current, [item.attributeId]: !expanded })); return } setExpandedOptionGroupIds(Object.fromEntries(visibleOptionRequirements.map(other => [other.attributeId, !expanded]))) }}>
                  <span className="attribute-builder-disclosure-copy"><strong>{item.attribute.name}</strong></span>
                  <span className="attribute-builder-disclosure-meta"><small className={variantAttributeIds.includes(item.attributeId) ? 'attribute-builder-selected' : ''}>{`${attributeSelections[item.attributeId]?.length ?? 0} / ${item.attribute.values.length} Seçili`}</small>{expandable && <UiIcon name="chevronDown" />}</span>
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
                    onKeyDown={event => {
                      if (event.key !== 'Enter') return
                      // Enter adds the typed value when the group lacks it, and never submits the product form.
                      event.preventDefault()
                      const newValue = optionValueToAdd(item.attribute.name, item.attribute.values, valueQuery)
                      if (newValue) void addOptionValue(item, newValue)
                    }}
                  />
                  <OptionValueAddButton attributeName={item.attribute.name} values={item.attribute.values} query={valueQuery} adding={addingOptionValueFor === item.attributeId} onAdd={newValue => void addOptionValue(item, newValue)} />
                  <OptionValueChips groupName={item.attribute.name} values={sortedValues} selectedIds={attributeSelections[item.attributeId] ?? []} filter={rest => filterAttributeOptionValues(rest, valueQuery)} onToggle={valueId => toggleAttributeValue(item.attributeId, valueId)} />
                  {filteredValues.length === 0 && !optionValueToAdd(item.attribute.name, item.attribute.values, valueQuery) && <p className="attribute-builder-no-values">Eşleşen değer bulunamadı.</p>}
                  </>
                ) : item.attribute.dataType === 'BOOLEAN' ? (
                  <label>Değer<PanelSelect ariaLabel="Değer" value={attributeTextValues[item.attributeId] ?? ''} onChange={next => setAttributeTextValues(current => ({ ...current, [item.attributeId]: next }))} options={[{ value: '', label: 'Seçin' }, { value: 'evet', label: 'Evet' }, { value: 'hayır', label: 'Hayır' }]} /></label>
                ) : (
                  <label>Değer<input value={attributeTextValues[item.attributeId] ?? ''} onChange={event => setAttributeTextValues(current => ({ ...current, [item.attributeId]: event.target.value }))} type={item.attribute.dataType === 'NUMBER' ? 'number' : 'text'} placeholder="Değer girin" /></label>
                )}
                </div>}
              </article>
              )
            })}
            {!visibleOptionRequirements.length && (
              <div className="empty small" style={{ gridColumn: '1 / -1' }}>
                <p>Bu ürün için kayıtlı seçenek grubu bulunamadı. Eşleştirmeler › Kategori Eşleme › Ürün Seçenekleri bölümünden seçenek grubu oluşturabilirsiniz.</p>
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
        <div className="editor-section-title"><span>6</span><div><h2>Ürün Seçenek Grupları<TitleHint label="Ürün Seçenek Grupları">İşaretlediğiniz özellik değerlerinin tüm kombinasyonları varyant satırı olur.</TitleHint></h2></div></div>
        {(fieldErrors.variants || fieldErrors.stock || fieldErrors.attributes) && <div className="product-field-error-list" role="alert">{[fieldErrors.variants, fieldErrors.stock, fieldErrors.attributes].filter(Boolean).map(message => <p key={message}>{message}</p>)}</div>}
        {variantRows.length > 0 && <>
            {trendyolMixedColours.length > 0 && <p className="family-field-note trendyol-picture-note" role="note">Trendyol uyarısı: {trendyolMixedColours.join(', ')} bedenleri farklı görsellerle gidiyor. Trendyol'da bir rengin bedenleri tek içerik altında tek görsel listesi taşır; böyle bir rengin güncellemesi reddedilir. Aynı görselleri o rengin tüm bedenlerine atayın.</p>}
            <div className="variant-table-toolbar"><div className="variant-table-toolbar-actions"><button type="button" className="secondary is-danger variant-clear-button" onClick={clearVariants}><UiIcon name="trash" /> Oluşan Varyantları Temizle</button>{editProductId && <button type="button" className="secondary variant-marketplace-codes-button" onClick={() => setMarketplaceCodesOpen(true)} title="Hepsiburada ve Shopify varyant kodlarını göster"><UiIcon name="externalLink" /> Pazaryeri Kodları</button>}<button type="button" className="secondary variant-quick-edit-button" onClick={() => setQuickEditOpen(true)} disabled={!variantRows.length} title="Stok, maliyet ve fiyatları toplu düzenle; Kaydet’e basılana kadar hiçbir şey kaydedilmez"><UiIcon name="edit" /> Toplu Düzenle</button><button type="button" className="secondary variant-media-bulk-button" onClick={openBulkVariantMediaPicker} title="Seçenek değerine görsel ata"><VariantImageIcon /> Seçeneklere Görsel Ata</button></div></div>
        </>}
            {variantRows.length > 0 && <div className="variant-mobile-groups" role="tablist" aria-label="Görünen varyant alanları">{([['codes', 'Kodlar'], ['stock', 'Stok / Maliyet'], ['price', 'Fiyat'], ['actions', 'İşlemler']] as const).map(([group, label]) => <button key={group} type="button" role="tab" aria-selected={variantMobileGroup === group} className={variantMobileGroup === group ? 'is-on' : undefined} onClick={() => setVariantMobileGroup(group)}>{label}</button>)}</div>}
            {variantCodeIssueSummary && <div className="variant-code-rule-notice" role="status">
              <span className="variant-code-rule-notice-icon" aria-hidden="true"><UiIcon name="alert" size={16} /></span>
              <div className="variant-code-rule-notice-text">
                <strong>{variantCodeIssueSummary.title}</strong>
                {variantCodeIssueSummary.rulePlatforms.length > 0 && <ul className="variant-code-rule-notice-platforms" aria-label="Kuralı ihlal edilen platformlar">{variantCodeIssueSummary.rulePlatforms.map(platform => <li key={platform}>{platform}</li>)}</ul>}
                <small>{variantCodeIssueSummary.ruleRows ? 'Kırmızı alanların üzerine gelince ayrıntı görünür.' : 'Eksik kodları kırmızı alanlara girin.'} Düzeltilene kadar Kaydet pasif kalır.</small>
              </div>
              <button type="button" className="secondary variant-code-rule-notice-action" onClick={goToFirstVariantCodeIssue}>İlk Hataya Git</button>
            </div>}
            <div className={`variant-table-editor is-mobile-${variantMobileGroup}`} style={{ '--vopt-ch': variantRows.reduce((longest, row) => Math.max(longest, displayVariantOptionSignature(row.optionSignature, row.options).length), 8) } as CSSProperties}>
              <div className="variant-table-head">
                <span>#</span><span>Seçenek</span>
                <span className="variant-table-header-with-action"><span>Barkod</span><div className="variant-header-action-shell" ref={barcodePasteActionRef}><button type="button" className="variant-header-action" onClick={() => { setBarcodeSkuMenuOpen(false); setBarcodePasteMenuOpen(current => !current) }} aria-label="Panodan barkod yapıştırma seçenekleri" aria-haspopup="menu" aria-expanded={barcodePasteMenuOpen} title="Panodan barkod yapıştırma seçenekleri"><BarcodePasteIcon /></button>{barcodePasteMenuOpen && <div className="variant-header-action-menu" role="menu"><button type="button" role="menuitem" disabled={!emptyBarcodeRowCount} onClick={() => void pasteBarcodesFromClipboard('missing')}><span><strong>Boş Barkodlara Yapıştır</strong><small>Sadece boş satırlar · {emptyBarcodeRowCount} hedef</small></span><UiIcon name="externalLink" /></button><button type="button" role="menuitem" disabled={!variantRows.length} onClick={() => void pasteBarcodesFromClipboard('all')}><span><strong>Tüm Barkodları Panodan Değiştir</strong><small>Varyant sırası · {variantRows.length} hedef</small></span><i aria-hidden="true">!</i></button><p>Satır sayısı hedefle aynı olmalı; sekmeli tabloda ilk sütun kullanılır. Boş veya yinelenen barkodda işlem yapılmaz.</p></div>}</div></span>
                <span className="variant-table-header-with-action"><span>Stok Kodu</span><div className="variant-header-action-shell" ref={barcodeSkuActionRef}><button type="button" className="variant-header-action" onClick={() => { setBarcodePasteMenuOpen(false); setBarcodeSkuMenuOpen(current => !current) }} aria-label="Barkoddan doldurma seçenekleri" aria-haspopup="menu" aria-expanded={barcodeSkuMenuOpen} title="Barkoddan stok kodu doldurma seçenekleri"><BarcodeFillIcon /></button>{barcodeSkuMenuOpen && <div className="variant-header-action-menu" role="menu"><button type="button" role="menuitem" disabled={!emptySkuBarcodeRowCount} onClick={() => applyBarcodeToSku('missing')}><span><strong>Eksik Stok Kodlarını Doldur</strong><small>Sadece boş satırlar · {emptySkuBarcodeRowCount} aday</small></span><UiIcon name="externalLink" /></button><button type="button" role="menuitem" disabled={!barcodeRowCount} onClick={() => applyBarcodeToSku('all')}><span><strong>Barkodları Stok Koduna Uygula</strong><small>Barkodu olan {barcodeRowCount} satırı güncelle</small></span><i aria-hidden="true">!</i></button><p>Çakışan barkodlar otomatik olarak atlanır; mevcut kodlar ilk seçenekte korunur.</p></div>}</div></span>
                {(['stock', 'salePrice', 'costPrice', 'listPrice'] as VariantBulkEditField[]).map(field => <span className="variant-table-header-with-action variant-bulk-header" key={field}><span>{variantBulkEditLabels[field]}</span><button type="button" className="variant-bulk-edit-trigger" aria-label={`${variantBulkEditLabels[field]} için toplu değişiklik aç`} aria-haspopup="dialog" aria-controls={variantBulkEdit?.field === field ? 'variant-bulk-edit-dialog' : undefined} aria-expanded={variantBulkEdit?.field === field} title={`${variantBulkEditLabels[field]} toplu değiştir`} onClick={() => openVariantBulkEdit(field)} disabled={!variantRows.length}><UiIcon name="edit" size={14} /></button></span>)}
                <span>İşlem</span>
              </div>
              {variantRows.length ? variantRows.map((row, index) => {
                const rowPlatforms = row.platformStatuses?.length ? row.platformStatuses : !editProductId ? variantPricingPlatforms : []
                const codeIssues = variantCodeIssues.get(row.key) ?? []
                // An empty needed code says "… eksik"; a filled one names the rules it breaks. Every variant needs its
                // own stock code, with or without an active marketplace.
                const barcodeIssueText = variantCodeFieldIssueText(codeIssues, 'barcode', row.barcode, heldVariantCodes[variantCodeKey('barcode', row.barcode)], variantCodeDuplicateRows.get(variantCodeFieldKey(row.key, 'barcode')))
                const skuIssueText = variantCodeFieldIssueText(codeIssues, 'sku', row.sku, heldVariantCodes[variantCodeKey('sku', row.sku)], variantCodeDuplicateRows.get(variantCodeFieldKey(row.key, 'sku')))
                return <div data-variant-row-key={row.key} className={`variant-table-row ${draggedVariantKey === row.key ? 'is-dragging' : ''} ${dragOverVariantKey === row.key ? 'is-drag-target' : ''}`} key={row.key}>
                  <div className="variant-row-lead" title="Sıralamak için tutup sürükleyin" aria-label={`${row.optionSignature} varyantını sıralamak için sürükleyin`} onPointerDown={event => beginVariantPointerDrag(event, row.key)}><span className="variant-row-number">{index + 1}</span><span className="variant-drag-handle"><VariantDragHandleIcon /></span></div>
                  <div className="variant-option-cell"><VariantStatusDot rowLabel={`${index + 1}. varyant`} status={variantStatus({ barcodeIssue: barcodeIssueText, skuIssue: skuIssueText, salePrice: row.salePrice, listPrice: row.listPrice, platformStatuses: row.platformStatuses })} /><span className="variant-option-text" aria-label={`${index + 1}. varyant seçenekleri`} title={displayVariantOptionSignature(row.optionSignature, row.options, true)}>{displayVariantOptionSignature(row.optionSignature, row.options)}</span><button type="button" className="variant-option-edit-button" onClick={() => setOptionEditRowKey(row.key)} aria-label={`${index + 1}. varyantın seçeneklerini düzenle`} title="Seçenekleri düzenle" disabled={!rowOptionEntries(row).length}><UiIcon name="edit" size={14} /></button></div>
                  <input aria-label={`${row.optionSignature} barkod`} className={`technical-field barcode-value${barcodeIssueText ? ' has-platform-code-issue' : ''}`} aria-invalid={barcodeIssueText ? true : undefined} title={barcodeIssueText || undefined} value={row.barcode} onChange={event => updateVariantRow(row.key, 'barcode', event.target.value)} placeholder="EAN / barkod" />
                  <input aria-label={`${row.optionSignature} stok kodu`} className={`technical-field sku-value${skuIssueText ? ' has-platform-code-issue' : ''}`} aria-invalid={skuIssueText ? true : undefined} title={skuIssueText || undefined} value={row.sku} onChange={event => updateVariantRow(row.key, 'sku', event.target.value)} placeholder="Varyant SKU" />
                  <input aria-label={`${row.optionSignature} stok`} value={row.stock} onChange={event => updateVariantRow(row.key, 'stock', event.target.value)} type="number" min="0" step="1" />
                  <input aria-label={`${row.optionSignature} satış fiyatı`} value={row.salePrice} onChange={event => updateVariantRow(row.key, 'salePrice', event.target.value)} type="number" min="0" step="0.01" />
                  <input aria-label={`${row.optionSignature} maliyeti`} value={row.costPrice ?? ''} onChange={event => updateVariantRow(row.key, 'costPrice', event.target.value)} type="number" min="0" step="0.01" />
                  <input aria-label={`${row.optionSignature} liste fiyatı`} value={row.listPrice} onChange={event => updateVariantRow(row.key, 'listPrice', event.target.value)} type="number" min="0" step="0.01" />
                  <div className="variant-row-actions" aria-label={`${row.optionSignature} varyant işlemleri`}>
                    {rowPlatforms.length ? (() => { const linkedCount = rowPlatforms.filter(item => item.isLinked).length; const isPartial = linkedCount > 0 && linkedCount < rowPlatforms.length; const summaryClass = linkedCount === rowPlatforms.length ? 'is-linked' : isPartial ? 'is-partial' : 'is-unlinked'; return <button type="button" className={`variant-platform-status variant-platform-status-summary variant-row-action ${summaryClass}`} onClick={() => openVariantPlatformPricing(row, rowPlatforms[0])} title={`${rowPlatforms.length} platform fiyatını düzenle`} aria-label={`${row.optionSignature} platform fiyatlarını düzenle`}><UiIcon name="platforms" /></button> })() : <span className="variant-platform-empty">—</span>}
                    {editProductId && <button type="button" className="variant-row-action variant-row-action-codes" onClick={() => setMarketplaceCodesVariant(row.key)} aria-label={`${row.optionSignature} pazaryeri kodlarını göster`} title="Pazaryeri kodları (Shopify, Hepsiburada)"><UiIcon name="link" /></button>}
                    {(() => { const outgoing = outgoingByRow.get(row.key); const { count, hint } = variantMediaHint(outgoing, chartKey); return <button type="button" className={`variant-media-button variant-row-action ${outgoing?.assigned ? 'has-media' : ''}`} onClick={() => openVariantMediaPicker(row.key)} aria-label={`${row.optionSignature} görsellerini seç. ${hint}`} title={hint}><VariantImageIcon />{count > 0 && <i aria-hidden="true">{count}</i>}</button> })()}
                    <button type="button" className="variant-row-action variant-row-action-delete" onClick={() => requestVariantRowRemoval(row)} aria-label={`${row.optionSignature} varyantını sil`} title="Varyantı sil"><UiIcon name="trash" /></button>
                  </div>
                </div>
              }) : <div className="empty small"><strong>Henüz varyant yok</strong><p>Özellik değerlerini seçip “Ürünleri Ekle” dediğinizde varyant satırları burada oluşur.</p></div>}
            </div>
      </div>
      </section>
    </div></div>

    <section className="product-publish-step" aria-label={editProductId ? 'Ürün yayınlama ve güncelleme' : 'Ürün yayınlama'}>
      <div className="product-publish-layout">
        <div className="product-publish-main">
          <div className="publish-platform-grid">
            {connections.isPending ? <PublishPlatformCardSkeletons /> : platformCards.length ? platformCards.map(card => <PublishPlatformCard
              card={card}
              key={card.connection.id}
              productId={editProductId}
              familyProductIds={productToEdit.data?.familyProductIds}
              lookupKeys={{ modelCode: form.modelCode, barcode: variantRows[0]?.barcode || form.barcode, sku: variantRows[0]?.sku || form.baseSku }}
              variantLinks={publishCardVariantLinks(publishLinkRows, card.connection.id)}
              categoryId={form.categoryId}
              categoryName={leafCategories.find(item => item.id === form.categoryId)?.name ?? ''}
              selected={selectedChannelIds.includes(card.connection.id)}
              settling={editorHydrating || (Boolean(form.categoryId) && requirements.isLoading)}
              productChecks={productChecks}
              selectedAttributes={publicationAttributeSelections}
              onSelect={() => updateChannel(card.connection.id)}
              onDeselect={() => setSelectedChannelIds(current => current.filter(id => id !== card.connection.id))}
              onBlockedChange={gaps => setBlockedPublishCards(current => (current[card.connection.id] ?? 0) === gaps ? current : { ...current, [card.connection.id]: gaps })}
              onFeedback={setFeedback}
              onEditProductData={publishOnly ? undefined : () => setWizardStep(1)}
            />) : <div className="publish-connections-empty"><strong>Aktif Bağlantı Bulunamadı</strong><p>Yayınlama için önce Entegrasyonlar sayfasından aktif bir bağlantı oluşturun.</p><Link to="/integrations">Entegrasyonları yönet <UiIcon name="arrowRight" /></Link></div>}
          </div>
          <StickyActionBar label="Yayınlama" className="publish-selection-footer">
            <div><strong>{selectedChannelIds.length ? `${selectedChannelIds.length} platform seçildi` : 'Yayın platformu seçilmedi'}</strong><small>{selectedChannelIds.length ? `Seçilen her mağazada ayrı yayın kuyruğu ve kontrol listesi bulunur. ${manualPriceStockNote}` : 'Yayınlanacak mağazaları kartların üstünden seçin.'}</small></div>
            {!selectedChannelIds.length && <span className="publish-selection-warning"><UiIcon name="alert" /> En az bir platform seçin</span>}
            <button type="submit" disabled={submitting || Boolean(modelCodeCreateBlock)} title={modelCodeCreateBlock || undefined}>{submitting ? (editProductId ? 'Yayınlanıyor…' : 'Ürün Oluşturuluyor…') : (editProductId ? 'Yayınla' : <><UiIcon name="sparkle" /> Ürünü Oluştur</>)}</button>
          </StickyActionBar>
        </div>
      </div>
    </section>

    <StickyActionBar label="Ürün kaydetme" className="product-submit-sticky"><div><strong>{editingDraftProduct ? 'Taslak ürün · Kaydet ile satışa açılır' : missingRequiredFields.length > 0 ? `${missingRequiredFields.length} zorunlu alan eksik · ${editProductId ? 'Kaydetmek için tamamlayın' : 'Taslak olarak kaydedilir'}` : editProductId ? 'Ürün düzenlemeye hazır' : 'Ürün bilgileri hazır'}</strong><p>{variantRows.length || 1} satış satırı · {selectedChannelIds.length} seçili kanal</p>{codeRuleSaveBlock && <button type="button" className="product-submit-code-block" onClick={goToFirstVariantCodeIssue}><UiIcon name="alert" size={14} /><span>{codeRuleSaveBlock}</span></button>}{pendingFamilyMediaRemovals.length > 0 && <p className="product-submit-family-media-note" role="status"><span>{pendingFamilyMediaRemovals.length} renk ailesi görseli Kaydet’te diğer renklerden de kaldırılacak</span><button type="button" className="product-submit-undo-button" onClick={undoPendingFamilyMediaRemovals} disabled={submitting}>Geri Al</button></p>}</div><div className={`product-submit-actions${codeRuleSaveBlock ? ' is-code-blocked' : ''}`}>{draftSaveAvailable && <button type="submit" name="intent" value="draft" className="secondary product-submit-draft" data-submit-intent="draft" formNoValidate form="product-creation-form" disabled={submitting || Boolean(modelCodeCreateBlock)} title={modelCodeCreateBlock || "Zorunlu alanlar eksik olsa da kaydeder; taslak pazaryerlerine gönderilmez ve stok sayımlarına girmez"}><span className="submit-label-long">Taslak Olarak Kaydet</span><span className="submit-label-short">Taslak</span></button>}{editProductId ? <button type="submit" name="intent" value="save" className="secondary" data-submit-intent="save" form="product-creation-form" disabled={submitting} aria-disabled={codeRuleSaveBlock ? true : undefined} title={codeRuleSaveBlock || undefined} onClick={event => { if (codeRuleSaveBlock) { event.preventDefault(); blockedByVariantCodeRules() } }}>{submitting ? 'Kaydediliyor…' : 'Kaydet'}</button> : <button type="submit" name="intent" value="create" className="secondary" data-submit-intent="create" form="product-creation-form" disabled={submitting || Boolean(modelCodeCreateBlock)} aria-disabled={codeRuleSaveBlock ? true : undefined} title={modelCodeCreateBlock || codeRuleSaveBlock || undefined} onClick={event => { if (codeRuleSaveBlock) { event.preventDefault(); blockedByVariantCodeRules() } }}>{submitting ? 'Oluşturuluyor…' : 'Ürünü Oluştur'}</button>}{editProductId && !editingDraftProduct && <div className="platform-update-split" role="group" aria-label="Platform güncelleme"><button type="button" className="platform-update-quick" onClick={() => { if (!blockedByVariantCodeRules() && !blockedByRequiredFields()) startQuickPlatformUpdate() }} disabled={submitting || connections.isLoading} aria-disabled={codeRuleSaveBlock ? true : undefined} title={codeRuleSaveBlock || `Sağdaki ayarda kaydedilen bilgileri seçili platformlara pencere açmadan gönderir (ayar yoksa fiyat ve stok). ${manualPriceStockNote}`}><span>Hızlı Güncelle</span><UiIcon name="refresh" /></button><button type="button" className="platform-update-settings" onClick={() => { if (!blockedByVariantCodeRules() && !blockedByRequiredFields()) openPlatformUpdateDialog() }} disabled={submitting || connections.isLoading} aria-disabled={codeRuleSaveBlock ? true : undefined} aria-label="Platform güncelleme ayarları" title={codeRuleSaveBlock || 'Platform güncelleme ayarları'} aria-haspopup="dialog"><UiIcon name="arrowUp" /></button></div>}<button type="button" onClick={() => { if (!blockedByVariantCodeRules() && !blockedByRequiredFields()) setWizardStep(2) }} aria-disabled={codeRuleSaveBlock ? true : undefined} className="product-submit-publish" aria-label="Yayınlamaya Devam Et" title={codeRuleSaveBlock || "Yayınlamaya Devam Et"}><span className="submit-label-long">Yayınlamaya Devam Et</span><span className="submit-label-short">Yayınla</span> <UiIcon name="arrowRight" /></button></div></StickyActionBar>
    {/* A save or validation error is already a feedback toast in the column; this one is only for what failed to load. */}
    {!error && <ErrorToast title="Ürün Bilgileri Yüklenemedi" error={categories.error ?? brands.error ?? connections.error} />}
    {barcodePasteMenuOpen && <VariantHeaderActionMenu anchorRef={barcodePasteActionRef} label="Barkod işlemleri">
      <VariantCodeMenu noun="Barkod" rowCount={variantRows.length} emptyCount={emptyBarcodeRowCount} filledCount={barcodeRowCount} canGenerate={Boolean(form.modelCode.trim())} generatePreview={variantCodeGenerationPreview('barcode')}
        onGenerate={scope => void requestVariantCodeChange('barcode', scope)} onPaste={scope => void pasteBarcodesFromClipboard(scope)} onClear={() => void requestVariantCodeChange('barcode', 'clear')} />
    </VariantHeaderActionMenu>}
    {barcodeSkuMenuOpen && <VariantHeaderActionMenu anchorRef={barcodeSkuActionRef} label="Stok kodu işlemleri">
      <VariantCodeMenu noun="Stok Kodu" rowCount={variantRows.length} emptyCount={emptySkuRowCount} filledCount={skuRowCount} canGenerate={Boolean(form.modelCode.trim())} generatePreview={variantCodeGenerationPreview('sku')}
        onGenerate={scope => void requestVariantCodeChange('sku', scope)} onPaste={scope => void pasteBarcodesFromClipboard(scope, 'sku')} onClear={() => void requestVariantCodeChange('sku', 'clear')}
        fromBarcode={{ missingCount: emptySkuBarcodeRowCount, allCount: barcodeRowCount, onApply: applyBarcodeToSku }} />
    </VariantHeaderActionMenu>}
    {variantDeleteConfirmation && createPortal(
      <div className="workspace-modal-backdrop media-removal-confirm-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) setVariantDeleteConfirmation(null) }}>
        <DialogSurface as="section" onClose={() => { setVariantDeleteConfirmation(null) }} className="workspace-modal media-removal-confirm-modal variant-delete-confirm-modal" role="alertdialog" aria-labelledby="variant-delete-confirm-title" aria-describedby="variant-delete-confirm-description" onMouseDown={event => event.stopPropagation()} onKeyDown={event => { if (event.key === 'Escape') setVariantDeleteConfirmation(null) }}>
          <header>
            <div>
              <p className="eyebrow">Varyantlar</p>
              <h2 id="variant-delete-confirm-title">{displayVariantOptionSignature(variantDeleteConfirmation.optionSignature, variantDeleteConfirmation.options)} Varyantını Sil</h2>
            </div>
            <button type="button" className="modal-close" onClick={() => setVariantDeleteConfirmation(null)} aria-label="Varyant silme onayını kapat"><UiIcon name="close" /></button>
          </header>
          <div className="media-removal-confirm-body">
            <p id="variant-delete-confirm-description">{variantRemovalConfirmText(variantDeleteConfirmation)}</p>
          </div>
          <footer>
            <button type="button" className="secondary" data-autofocus={touchScreen ? undefined : true} onClick={() => setVariantDeleteConfirmation(null)}>Vazgeç</button>
            <button type="button" className="destructive" onClick={confirmVariantRowRemoval}>Varyantı Sil</button>
          </footer>
        </DialogSurface>
      </div>,
      document.querySelector('main') ?? document.body
    )}
    {mediaRemovalConfirmation && createPortal(
      <div className="workspace-modal-backdrop media-removal-confirm-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) setMediaRemovalConfirmation(null) }}>
        <DialogSurface as="section" onClose={() => { setMediaRemovalConfirmation(null) }} className="workspace-modal media-removal-confirm-modal" role="alertdialog" aria-labelledby="media-removal-confirm-title" aria-describedby="media-removal-confirm-description" onMouseDown={event => event.stopPropagation()} onKeyDown={event => { if (event.key === 'Escape') setMediaRemovalConfirmation(null) }}>
          <header>
            <div>
              <p className="eyebrow">Görsel Yönetimi</p>
              <h2 id="media-removal-confirm-title">{mediaRemovalConfirmation.kind === 'all' ? 'Tüm Görselleri Temizle' : 'Görseli Hemen Kaldır'}</h2>
            </div>
            <button type="button" className="modal-close" onClick={() => setMediaRemovalConfirmation(null)} aria-label="Görsel kaldırma onayını kapat"><UiIcon name="close" /></button>
          </header>
          <div className="media-removal-confirm-body">
            <p id="media-removal-confirm-description">{mediaRemovalConfirmation.kind === 'all'
              ? clearAllMediaConfirmationText(mediaRemovalConfirmation.ownCount, mediaRemovalConfirmation.familyItems)
              : 'Bu görsel aynı model ailesindeki ürün ve varyant kayıtlarından hemen kaldırılacak. Pazaryerindeki görsel değiştirilmez.'}</p>
          </div>
          <footer>
            <button type="button" className="secondary" data-autofocus={touchScreen ? undefined : true} onClick={() => setMediaRemovalConfirmation(null)}>Vazgeç</button>
            <button type="button" className="destructive" onClick={confirmMediaRemoval}>{mediaRemovalConfirmation.kind === 'all' ? 'Tümünü Temizle' : 'Görseli Kaldır'}</button>
          </footer>
        </DialogSurface>
      </div>,
      document.querySelector('main') ?? document.body
    )}
    {/* The editor's dialogs portal into main: the panel's button and field styles are scoped to main, so one portaled
        into body showed bare browser buttons (live 2026-10-30, Stok Kodlarını Model Kodundan Üret). */}
    {pendingVariantCodeGeneration && pendingVariantCodeConfirm && createPortal(
      <div className="workspace-modal-backdrop variant-code-confirm-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) setPendingVariantCodeGeneration(null) }}>
        <DialogSurface as="section" onClose={() => { setPendingVariantCodeGeneration(null) }} className="workspace-modal variant-code-confirm-modal" role="alertdialog" aria-labelledby="variant-code-confirm-title" aria-describedby="variant-code-confirm-description" onMouseDown={event => event.stopPropagation()}>
          <header>
            <div>
              <p className="eyebrow">Varyant Kodları</p>
              <h2 id="variant-code-confirm-title">{pendingVariantCodeConfirm.title}</h2>
              <p id="variant-code-confirm-description">{pendingVariantCodeConfirm.description}</p>
            </div>
            <button type="button" className="modal-close" onClick={() => setPendingVariantCodeGeneration(null)} aria-label="Kod değiştirme onayını kapat"><UiIcon name="close" /></button>
          </header>
          <div className="variant-code-confirm-body">
            {pendingVariantCodeConfirm.range && <><p>Üretilecek kod aralığı</p><code>{pendingVariantCodeConfirm.range}</code></>}
            {pendingVariantCodeGeneration.mode !== 'clear' && <p className="variant-code-confirm-scan">{pendingVariantCodeGeneration.skipped ? `Panelde başka ürünlerde kullanılan ${pendingVariantCodeGeneration.skipped.toLocaleString('tr-TR')} numara atlandı; üretilen kodların hiçbiri kullanılmıyor.` : 'Üretilen kodların hiçbiri panelde kullanılmıyor.'}</p>}
            <small>{pendingVariantCodeGeneration.mode === 'clear' ? 'Onaylayınca alan, Kaydet’e basmadan bu formdaki varyantlarda boşaltılır; Kaydet’e basmadan sayfadan çıkarsanız kayıtlı kodlar değişmez. Diğer kod sütunu korunur.' : 'Onaylayınca kodlar, Kaydet’e basmadan bu formdaki varyantlara uygulanır. Diğer kod sütunu korunur; başka üründe kullanılan kodlar Kaydet sırasında reddedilebilir.'}</small>
          </div>
          <footer>
            <button type="button" className="secondary" data-autofocus={touchScreen ? undefined : true} onClick={() => setPendingVariantCodeGeneration(null)}>Vazgeç</button>
            <button type="button" className={pendingVariantCodeGeneration.mode === 'clear' ? 'destructive' : undefined} onClick={confirmVariantCodeGeneration}>{pendingVariantCodeConfirm.action}</button>
          </footer>
        </DialogSurface>
      </div>,
      document.querySelector('main') ?? document.body
    )}
    {platformUpdateDialogOpen && <PlatformUpdateDialog
      targets={productUpdateTargets}
      selectedIds={platformUpdateConnectionIds}
      onSelectedIdsChange={setPlatformUpdateConnectionIds}
      fields={platformUpdatePreferences.fields}
      onFieldsChange={fields => changePlatformUpdatePreferences({ ...platformUpdatePreferences, fields })}
      openGroups={platformUpdatePreferences.openGroups}
      onOpenGroupsChange={openGroups => changePlatformUpdatePreferences({ ...platformUpdatePreferences, openGroups })}
      submitting={submitting || savingPlatformUpdateChoice}
      familyReach={platformUpdateFamilyReach}
      onClose={() => setPlatformUpdateDialogOpen(false)}
      onSave={savePlatformUpdateChoice}
      onSend={sendPlatformUpdateChoice} />}
    {lightboxImage && <ImageLightboxModal image={lightboxImage} onClose={() => setLightboxImage(null)} />}
    {variantMediaModal && <VariantMediaPickerModal
      mode={variantMediaModal.mode}
      options={mediaChoices}
      chartKey={chartKey}
      selectedRefs={selectedVariantMediaRefs}
      groups={variantMediaModal.mode === 'bulk' ? bulkMediaGroups : undefined}
      selectedGroupId={variantMediaModal.mode === 'bulk' ? variantMediaModal.groupId : undefined}
      selectedValueId={variantMediaModal.mode === 'bulk' ? variantMediaModal.valueId : undefined}
      matchedVariantCount={variantMediaModal.mode === 'bulk' ? selectedBulkMediaMatchCount : undefined}
      matchedGroupVariantCount={variantMediaModal.mode === 'bulk' ? selectedBulkMediaGroupMatchCount : undefined}
      assignedValueIds={variantMediaModal.mode === 'bulk' ? new Map((bulkMediaGroups.find(group => group.id === variantMediaModal.groupId)?.values ?? []).map(value => [value.id, (readVariantMediaAssignmentDraft(variantMediaModal.draftsBySelection, variantMediaAssignmentKey(variantMediaModal.groupId, value.id)) ?? []).length] as const).filter(([, count]) => count > 0)) : undefined}
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
        <header><div><p className="eyebrow">Varyantlar</p><h2 id="variant-bulk-edit-title">Toplu {variantBulkEditLabels[variantBulkEdit.field]} Değiştir<TitleHint label={`Toplu ${variantBulkEditLabels[variantBulkEdit.field]} Değiştir`}>Filtrelerle eşleşen varyant satırlarına uygulanır.</TitleHint></h2></div><button type="button" className="modal-close" onClick={() => setVariantBulkEdit(null)} aria-label="Toplu değişiklik penceresini kapat"><UiIcon name="close" /></button></header>
        <div className="variant-bulk-edit-body">
          {variantBulkEditFilterGroups.length > 0 && <section className="variant-bulk-edit-filters" aria-label="Renk ve beden filtreleri"><strong>Renk ve beden seçin</strong><div>{variantBulkEditFilterGroups.map(group => <VariantFilterDropdown key={group.id} group={group} selectedValueIds={variantBulkEditFilterSelections[group.id] ?? []} onToggle={valueId => setVariantBulkEditFilterSelections(current => ({ ...current, [group.id]: (current[group.id] ?? []).includes(valueId) ? current[group.id].filter(id => id !== valueId) : [...(current[group.id] ?? []), valueId] }))} onClear={() => setVariantBulkEditFilterSelections(current => ({ ...current, [group.id]: [] }))} />)}</div></section>}
          <div className="variant-bulk-edit-target"><span>Uygulanacak Varyant</span><strong>{variantBulkEditMatchingRows.length.toLocaleString('tr-TR')} / {variantRows.length.toLocaleString('tr-TR')} satır</strong></div>
          <div className="variant-bulk-modes" role="group" aria-label="İşlem türü">{(['set', 'increase', 'decrease'] as const).map(mode => <button key={mode} type="button" className={variantBulkEdit.mode === mode ? 'is-on' : undefined} aria-pressed={variantBulkEdit.mode === mode} onClick={() => setVariantBulkEdit(current => current ? { ...current, mode, error: '' } : current)}>{variantBulkModeLabels[mode]}</button>)}</div>
          <label htmlFor="variant-bulk-edit-value">Yeni {variantBulkEditLabels[variantBulkEdit.field]} Değeri<input id="variant-bulk-edit-value" data-autofocus={touchScreen ? undefined : true} type="number" min="0" step={variantBulkEdit.field === 'stock' ? '1' : '0.01'} value={variantBulkEdit.value} aria-describedby={variantBulkEdit.error ? 'variant-bulk-edit-help variant-bulk-edit-error' : 'variant-bulk-edit-help'} onChange={event => setVariantBulkEdit(current => current ? { ...current, value: event.target.value, error: '' } : current)} /></label>
          <small id="variant-bulk-edit-help">{variantBulkEdit.field === 'stock' ? 'Stok tam sayı ve sıfırdan büyük veya eşit olmalıdır.' : 'Tutar sıfır veya daha büyük olmalıdır.'}{variantBulkEdit.mode === 'increase' ? ' Girilen değer mevcut değere eklenir.' : variantBulkEdit.mode === 'decrease' ? ' Girilen değer mevcut değerden çıkarılır; sonuç 0\'ın altına inmez.' : ''}</small>
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
  // Keyed by the product: going from one product's page to another's starts a fresh editor (selected stores, wizard
  // step, open dialogs) instead of carrying the first product's state over.
  return <NewProductPage key={id} editProductId={id} />
}
