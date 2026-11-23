// 1 Temel Ürün Bilgileri: name, sale state, brand, category, model code, the card's codes, desi, warranty, delivery
// time and the description.
import { FieldLabel } from '../../../shared/components'
import { ModelCodeHolderNotice } from '../model-code-holder'
import { PanelSelect } from '../../../shared/PanelSelect'
import { EditorFieldError, automaticCodeHelp, variantCodesReadOnlyTitle, RequiredLabel } from './EditorBits'
import { RichTextEditor } from '../../../shared/components/RichTextEditor'
import type { EditorContext } from './editor-context'

export function EditorBasicsSection({ editor }: { editor: EditorContext }) {
  const { editProductId, setCalculateDesi, setDesiCalculatorOpen, form, setAttributeSelections, setAttributeTextValues, setVariantAttributeIds, variantRows, setVariantRows, automaticBarcodeGeneration, setWebColorAutoEnabled, setManualWebColorValueId, fieldErrors, setFieldErrors, updateField, leafCategories, activeBrands, requiredError, requiredFieldInvalid, modelCodeHolder, modelCodeCreateBlock } = editor
  return <>
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
            <label className="desi-input-field"><RequiredLabel>Desi</RequiredLabel><span className="desi-inline-control"><input value={form.desi} aria-invalid={requiredFieldInvalid('desi')} onChange={event => { setCalculateDesi(false); updateField('desi', event.target.value) }} type="text" inputMode="decimal" autoComplete="off" placeholder="Girilmemiş" required /><button type="button" className="secondary" onClick={() => setDesiCalculatorOpen(true)}>Hesapla</button></span><EditorFieldError message={requiredError('desi')} /></label>
            <label className="warranty-input-field" title="0: garanti yok"><RequiredLabel>Garanti Süresi (Ay)</RequiredLabel><input value={form.warrantyMonths} aria-invalid={requiredFieldInvalid('warrantyMonths')} onChange={event => updateField('warrantyMonths', event.target.value)} type="number" min="0" max="240" step="1" inputMode="numeric" placeholder="0 = garanti yok" /><EditorFieldError message={requiredError('warrantyMonths')} /></label>
            <label className="delivery-days-input-field" title="Siparişin kargoya teslim süresi; pazaryerlerine teslimat (sevkiyat) süresi olarak gider."><RequiredLabel>Teslimat Süresi (Gün)</RequiredLabel><input value={form.deliveryDays} aria-invalid={requiredFieldInvalid('deliveryDays')} onChange={event => updateField('deliveryDays', event.target.value)} type="number" min="1" max="30" step="1" inputMode="numeric" required /><EditorFieldError message={requiredError('deliveryDays')} /></label>
          </div>
          <div className="wide product-description-field"><span>Açıklama</span><RichTextEditor value={form.description} onChange={value => updateField('description', value)} /><EditorFieldError message={fieldErrors.description} /></div>
        </div>
      </section>
  </>
}
