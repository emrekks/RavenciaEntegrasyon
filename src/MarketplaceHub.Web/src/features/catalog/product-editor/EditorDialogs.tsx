// The variant code column menus and the editor's confirmations (variant delete, picture removal, code change),
// shared windows (RrModal).
import { RrModal } from '../../../shared/ui/rr-components'
import { VariantCodeMenu } from './VariantCodeMenu'
import { variantRemovalConfirmText } from '../variant-row-removal'
import { clearAllMediaConfirmationText } from '../family-media-clear'
import { displayVariantOptionSignature, touchScreen } from './editor-helpers'
import { VariantHeaderActionMenu } from './VariantHeaderActionMenu'
import type { EditorContext } from './editor-context'

export function EditorDialogs({ editor }: { editor: EditorContext }) {
  const { form, variantRows, barcodeSkuMenuOpen, barcodeSkuActionRef, barcodePasteMenuOpen, barcodePasteActionRef, pendingVariantCodeGeneration, setPendingVariantCodeGeneration, mediaRemovalConfirmation, setMediaRemovalConfirmation, variantDeleteConfirmation, setVariantDeleteConfirmation, barcodeRowCount, emptySkuBarcodeRowCount, emptyBarcodeRowCount, skuRowCount, emptySkuRowCount, pendingVariantCodeConfirm, variantCodeGenerationPreview, confirmVariantRowRemoval, applyBarcodeToSku, requestVariantCodeChange, confirmVariantCodeGeneration, pasteBarcodesFromClipboard, confirmMediaRemoval } = editor
  return <>
    {barcodePasteMenuOpen && <VariantHeaderActionMenu anchorRef={barcodePasteActionRef} label="Barkod işlemleri">
      <VariantCodeMenu noun="Barkod" rowCount={variantRows.length} emptyCount={emptyBarcodeRowCount} filledCount={barcodeRowCount} canGenerate={Boolean(form.modelCode.trim())} generatePreview={variantCodeGenerationPreview('barcode')}
        onGenerate={scope => void requestVariantCodeChange('barcode', scope)} onPaste={scope => void pasteBarcodesFromClipboard(scope)} onClear={() => void requestVariantCodeChange('barcode', 'clear')} />
    </VariantHeaderActionMenu>}
    {barcodeSkuMenuOpen && <VariantHeaderActionMenu anchorRef={barcodeSkuActionRef} label="Stok kodu işlemleri">
      <VariantCodeMenu noun="Stok Kodu" rowCount={variantRows.length} emptyCount={emptySkuRowCount} filledCount={skuRowCount} canGenerate={Boolean(form.modelCode.trim())} generatePreview={variantCodeGenerationPreview('sku')}
        onGenerate={scope => void requestVariantCodeChange('sku', scope)} onPaste={scope => void pasteBarcodesFromClipboard(scope, 'sku')} onClear={() => void requestVariantCodeChange('sku', 'clear')}
        fromBarcode={{ missingCount: emptySkuBarcodeRowCount, allCount: barcodeRowCount, onApply: applyBarcodeToSku }} />
    </VariantHeaderActionMenu>}
    {variantDeleteConfirmation && <RrModal role="alertdialog" describedBy="variant-delete-confirm-description" crumb="Varyantlar" title={`${displayVariantOptionSignature(variantDeleteConfirmation.optionSignature, variantDeleteConfirmation.options)} Varyantını Sil`} size="md" modalClass="variant-delete-confirm-modal" onClose={() => setVariantDeleteConfirmation(null)}
      foot={<><span className="rr-note" /><button type="button" className="rr-btn" data-autofocus={touchScreen ? undefined : true} onClick={() => setVariantDeleteConfirmation(null)}>Vazgeç</button><button type="button" className="rr-btn rr-solid" onClick={confirmVariantRowRemoval}>Varyantı Sil</button></>}>
      <p id="variant-delete-confirm-description" className="editor-confirm-text">{variantRemovalConfirmText(variantDeleteConfirmation)}</p>
    </RrModal>}
    {mediaRemovalConfirmation && <RrModal role="alertdialog" describedBy="media-removal-confirm-description" crumb="Görsel Yönetimi" title={mediaRemovalConfirmation.kind === 'all' ? 'Tüm Görselleri Temizle' : 'Görseli Hemen Kaldır'} size="md" modalClass="media-removal-confirm-modal" onClose={() => setMediaRemovalConfirmation(null)}
      foot={<><span className="rr-note" /><button type="button" className="rr-btn" data-autofocus={touchScreen ? undefined : true} onClick={() => setMediaRemovalConfirmation(null)}>Vazgeç</button><button type="button" className="rr-btn rr-solid" onClick={confirmMediaRemoval}>{mediaRemovalConfirmation.kind === 'all' ? 'Tümünü Temizle' : 'Görseli Kaldır'}</button></>}>
      <p id="media-removal-confirm-description" className="editor-confirm-text">{mediaRemovalConfirmation.kind === 'all'
        ? clearAllMediaConfirmationText(mediaRemovalConfirmation.ownCount, mediaRemovalConfirmation.familyItems)
        : 'Bu görsel aynı model ailesindeki ürün ve varyant kayıtlarından hemen kaldırılacak. Pazaryerindeki görsel değiştirilmez.'}</p>
    </RrModal>}
    {pendingVariantCodeGeneration && pendingVariantCodeConfirm && <RrModal role="alertdialog" describedBy="variant-code-confirm-description" crumb="Varyant Kodları" title={pendingVariantCodeConfirm.title} desc={<span id="variant-code-confirm-description">{pendingVariantCodeConfirm.description}</span>} size="md" modalClass="variant-code-confirm-modal" onClose={() => setPendingVariantCodeGeneration(null)}
      foot={<><span className="rr-note" /><button type="button" className="rr-btn" data-autofocus={touchScreen ? undefined : true} onClick={() => setPendingVariantCodeGeneration(null)}>Vazgeç</button><button type="button" className={pendingVariantCodeGeneration.mode === 'clear' ? 'rr-btn rr-solid' : 'rr-btn rr-primary'} onClick={confirmVariantCodeGeneration}>{pendingVariantCodeConfirm.action}</button></>}>
      <div className="variant-code-confirm-body">
        {pendingVariantCodeConfirm.range && <><p>Üretilecek kod aralığı</p><code>{pendingVariantCodeConfirm.range}</code></>}
        {pendingVariantCodeGeneration.mode !== 'clear' && <p className="variant-code-confirm-scan">{pendingVariantCodeGeneration.skipped ? `Panelde başka ürünlerde kullanılan ${pendingVariantCodeGeneration.skipped.toLocaleString('tr-TR')} numara atlandı; üretilen kodların hiçbiri kullanılmıyor.` : 'Üretilen kodların hiçbiri panelde kullanılmıyor.'}</p>}
        <small>{pendingVariantCodeGeneration.mode === 'clear' ? 'Onaylayınca alan, Kaydet’e basmadan bu formdaki varyantlarda boşaltılır; Kaydet’e basmadan sayfadan çıkarsanız kayıtlı kodlar değişmez. Diğer kod sütunu korunur.' : 'Onaylayınca kodlar, Kaydet’e basmadan bu formdaki varyantlara uygulanır. Diğer kod sütunu korunur; başka üründe kullanılan kodlar Kaydet sırasında reddedilebilir.'}</small>
      </div>
    </RrModal>}
  </>
}
