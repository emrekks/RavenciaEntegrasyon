// The variant code column menus and the editor's confirmations (variant delete, picture removal, code change),
// portaled into main where the panel's styles apply.
import { createPortal } from 'react-dom'
import { UiIcon, DialogSurface } from '../../../shared/components'
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
  </>
}
