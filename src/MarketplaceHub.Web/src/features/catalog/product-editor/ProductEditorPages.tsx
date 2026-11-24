// Ürün ekleme ve düzenleme (/products/new, /products/:id).
import './product-editor-design-system.css'
import './product-editor-hydration.css'
import { lazy, Suspense } from 'react'
import { Link, useLocation, useNavigate, useParams } from 'react-router'
import { createPortal } from 'react-dom'
import { useQueryClient } from '@tanstack/react-query'
import { UiIcon, DialogSurface, UnsavedChangesGuard } from '../../../shared/components'
// The wizard's step tabs are .rv-tabs.rv-view-tabs (underline, numbered round badge): the page loads their sheet itself, not only by luck of another page's chunk.
import '../../../shared/view-tabs.css'
import { ErrorToast } from '../../../shared/components/NoticeToast'
import { MarketplaceCodesDialog } from './MarketplaceCodesDialog'
import { VariantOptionEditDialog } from './VariantOptionEditDialog'
import { Page, cleanOptionValue, isColorOptionName } from '../shared'
import { isSizeOptionName, displayVariantOptionSignature, rowMatchesVariantMediaValue } from './editor-helpers'
import { EditorShell } from './EditorBits'
import { BulkVariantPlatformPricingModal } from './BulkVariantPlatformPricingModal'
import { CategoryAttributeMappingPanel } from './CategoryAttributeMappingPanel'
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
import { useEditorChecks } from './use-editor-checks'
import { useEditorSubmit } from './use-editor-submit'
import { EditorBasicsSection } from './EditorBasicsSection'
import { EditorPricingSection } from './EditorPricingSection'
import { DesiCalculatorDialog } from './DesiCalculatorDialog'
import { EditorMediaSection } from './EditorMediaSection'
import { EditorPublishStep } from './EditorPublishStep'
import { EditorSubmitBar } from './EditorSubmitBar'
import { EditorDialogs } from './EditorDialogs'
import { EditorWindows } from './EditorWindows'
import { EditorOptionsSection } from './EditorOptionsSection'
import { channelTakesListPrice, priceFieldText } from '../quick-price-edit'
export { BulkVariantPlatformPricingModal } from './BulkVariantPlatformPricingModal'
export { PublishPlatformCard, cardLookupKey, publishCardState } from './PublishPlatformCard'

// The products list's Toplu Düzenleme window, opened on the editor's unsaved rows (Toplu Düzenle in the variant toolbar).
const ProductQuickEditModal = lazy(() => import('../products/ProductQuickEditModal').then(module => ({ default: module.ProductQuickEditModal })))

export function NewProductPage({ editProductId, publishOnly = false }: { editProductId?: string; publishOnly?: boolean } = {}) {
  const client = useQueryClient();
  const location = useLocation()
  const navigate = useNavigate()
  const routeState = location.state as { duplicateProductId?: unknown; wizardStep?: unknown } | null
  const duplicateProductId = typeof routeState?.duplicateProductId === 'string' ? routeState.duplicateProductId : undefined
  // The editor's hooks, each given what the earlier ones returned (WEB-SAYFALAR-10). Their order is the order the page's
  // effects had before the split (React runs effects in call order): the seed's layout effects after the queries, the
  // Web Color seed after the snapshot, the picture assignment save before the code checks. Do not reorder them.
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
  const c16 = { ...c15, ...useEditorChecks(c15) }
  const c17 = { ...c16, ...useEditorSubmit(c16) }
  const editor: EditorContext = c17
  const { error, submitting, form, attributeSelections, attributeTextValues, setAttributeTextValues, variantRows, setVariantRows, variantChannelPricing, marketplaceCodesOpen, setMarketplaceCodesOpen, quickEditOpen, setQuickEditOpen, marketplaceCodesVariant, setMarketplaceCodesVariant, wizardStep, setWizardStep, webColorAutoEnabled, manualWebColorValueId, setManualWebColorValueId, staleProductVersion, leavingAfterSave, variantPlatformPricing, setVariantPlatformPricing, variantPlatformPricingSaving, showFeedback, optionEditRowKey, setOptionEditRowKey, editorDirty, productToEdit, productToCopy, categories, brands, connections, editorHydrating, requirements, leafCategories, mappedRequirements, webColorRequirement, reloadEditedProduct, variantPricingPlatforms, variantFilterGroups, selectedVariantPlatformRow, toggleAttributeValue, rowOptionEntries, optionAttributeFor, rowHasOptionValue, applyVariantOptionChanges, toggleWebColorAuto, saveVariantPlatformPricingMatrix, handleInvalid, dismissProductError, submit } = editor

  if (duplicateProductId && productToCopy.isLoading) return <Page title="Yeni Ürün Ekle" eyebrow="Katalog"><div className="panel" role="status">Kaynak ürünün bilgileri forma hazırlanıyor…</div></Page>
  if (duplicateProductId && productToCopy.isError) return <Page title="Yeni Ürün Ekle" eyebrow="Katalog"><ErrorToast title="Kopyalanacak Ürün Yüklenemedi" error={productToCopy.error} /><Link className="button-link secondary" to="/products">Ürünlere Dön</Link></Page>

  return <EditorShell publishOnly={publishOnly} className={`product-add-page${editProductId ? ' product-edit-page' : ''}${editorHydrating ? ' is-hydrating' : ''}`} title={editProductId ? productToEdit.data?.title || 'Ürün Düzenle' : 'Yeni Ürün Ekle'} eyebrow={editProductId ? 'Ürün Düzenle' : 'Katalog'} meta={editProductId && productToEdit.data?.modelCode ? <p className="product-page-heading-meta">Model Kodu: <b>{productToEdit.data.modelCode}</b></p> : editProductId && productToEdit.isPending ? <p className="product-page-heading-meta" aria-hidden="true">Model Kodu: <b>…</b></p> : undefined}>
    {!publishOnly && <UnsavedChangesGuard when={editorDirty} allowNavigationRef={leavingAfterSave} />}
    {staleProductVersion != null && <div className="product-stale-banner" role="alert"><span><strong>Ürün başka bir işlemle güncellendi.</strong> Kaydedilmemiş değişiklikleriniz korunuyor; kaydederseniz sunucu sürüm çakışmasını bildirir.</span><button type="button" className="secondary" onClick={reloadEditedProduct}>Güncel Ürünü Yükle</button></div>}
    {quickEditOpen && <Suspense fallback={null}><ProductQuickEditModal onClose={() => setQuickEditOpen(false)} draft={{ title: form.title, modelCode: form.modelCode, rows: variantRows,
      // The marketplaces of the card: the same Özel Fiyat state as the card's switch, and each variant's offer price there (a staged draft first).
      isNewProduct: !editor.editProductId,
      channels: editor.platformCards.map(card => ({ connectionId: card.connection.id, name: card.name, platformCode: card.connection.platformCode, takesListPrice: channelTakesListPrice(card.connection.platformCode), custom: card.connection.id in editor.channelPricing, onToggleCustom: () => editor.toggleCustomChannelPrice(card.connection.id) })),
      // As Turkish text ("1250,5") with the numbers it was made from: a side left as loaded goes back as that number.
      channelPrice: (rowKey, connectionId) => {
        const staged = variantChannelPricing[`${rowKey}:${connectionId}`]
        if (staged) return { list: priceFieldText(staged.listPrice), sale: priceFieldText(staged.salePrice), follows: false, values: { list: staged.listPrice, sale: staged.salePrice } }
        const platform = variantRows.find(row => row.key === rowKey)?.platformStatuses?.find(item => item.connectionId === connectionId)
        return platform?.offerId ? { list: priceFieldText(platform.listPrice), sale: priceFieldText(platform.salePrice), follows: platform.followsPanelPrice !== false, values: { list: platform.listPrice ?? null, sale: platform.salePrice ?? null } } : null
      },
      // Staged as the editor's per-variant marketplace price drafts (variantChannelPricing): only the page's Kaydet writes them.
      onApplyChannelPrices: changes => {
        editor.setVariantChannelPricing(current => ({ ...current, ...Object.fromEntries(changes.map(change => [`${change.rowKey}:${change.connectionId}`, { listPrice: change.listPrice, salePrice: change.salePrice }])) }))
        showFeedback('Pazaryeri Fiyatları Hazırlandı', `${changes.length} fiyat taslağa yazıldı. Kalıcı olması için Kaydet’e basın.`, 'success')
      },
      onApply: changes => {
      // Only the table changes: the rows are saved (and sent on) with the page's Kaydet, as a hand-typed cell would be.
      const byKey = new Map(changes.map(change => [change.key, change]))
      setVariantRows(rows => rows.map(row => { const change = byKey.get(row.key); return change ? { ...row, stock: change.stock, listPrice: change.listPrice, salePrice: change.salePrice, costPrice: change.costPrice } : row }))
      if (changes.length) showFeedback('Toplu Düzenleme Uygulandı', `${changes.length} varyantın değerleri tabloya yazıldı. Kalıcı olması için Kaydet’e basın.`, 'success')
    } }} /></Suspense>}
    {variantPlatformPricing && selectedVariantPlatformRow && <BulkVariantPlatformPricingModal row={selectedVariantPlatformRow} rows={variantRows} platforms={selectedVariantPlatformRow.platformStatuses?.length ? selectedVariantPlatformRow.platformStatuses : variantPricingPlatforms} savedDrafts={variantChannelPricing} productName={form.title} modelCode={form.modelCode} filterGroups={variantFilterGroups.filter(group => isColorOptionName(group.name) || isSizeOptionName(group.name))} matchesFilterValue={rowMatchesVariantMediaValue} saving={variantPlatformPricingSaving} onClose={() => setVariantPlatformPricing(null)} onSave={drafts => void saveVariantPlatformPricingMatrix(drafts)} confirmWrites={Boolean(editProductId)} />}
    {!publishOnly && <div className="product-add-wizardbar"><div className="product-add-stepper"><div className="product-add-steptabs rv-tabs rv-view-tabs" role="tablist" aria-label={editProductId ? 'Ürün düzenleme adımları' : 'Ürün ekleme adımları'}><button type="button" className={wizardStep === 1 ? 'is-active' : ''} role="tab" aria-selected={wizardStep === 1} onClick={() => setWizardStep(1)}><span className="rv-tab-label"><span className="rv-step-num">1</span><span>Ürün Bilgileri<span className="product-step-label-more"> ve Varyantlar</span></span></span></button><button type="button" className={wizardStep === 2 ? 'is-active' : ''} role="tab" aria-selected={wizardStep === 2} onClick={() => setWizardStep(2)}><span className="rv-tab-label"><span className="rv-step-num">2</span><span>Yayınlama</span></span></button></div></div></div>}<form id="product-creation-form" className="product-creation-workspace product-add-workspace" data-wizard-step={wizardStep} aria-busy={editorHydrating || submitting || undefined} inert={editorHydrating || submitting || undefined} onSubmit={submit} onInvalidCapture={handleInvalid} onChange={dismissProductError} noValidate>
    <div className="product-top-layout">
      <EditorBasicsSection editor={editor} />
      <EditorPricingSection editor={editor} />
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
    <DesiCalculatorDialog editor={editor} />

    <div className="product-layout-grid"><div className="product-main-stack">
      <EditorMediaSection editor={editor} />

      <EditorOptionsSection editor={editor} />
    </div></div>

    <EditorPublishStep editor={editor} />

    <EditorSubmitBar editor={editor} />
    {/* A save or validation error is already a feedback toast in the column; this one is only for what failed to load. */}
    {!error && <ErrorToast title="Ürün Bilgileri Yüklenemedi" error={categories.error ?? brands.error ?? connections.error} />}
    <EditorDialogs editor={editor} />
    <EditorWindows editor={editor} />
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
