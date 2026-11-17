// The editor's windows: Platformlarda Güncelle, the picture lightbox, variant pictures and the variant bulk edit.
import { TitleHint, UiIcon, DialogSurface } from '../../../shared/components'
import { variantBulkModeLabels } from '../variant-bulk-edit'
import { PlatformUpdateDialog } from './PlatformUpdateDialog'
import { readVariantMediaAssignmentDraft, updateVariantMediaAssignmentDraft, variantMediaAssignmentKey } from '../variant-media-assignments'
import { ImageLightboxModal } from '../shared'
import { touchScreen, variantBulkEditLabels } from './editor-helpers'
import { VariantMediaPickerModal } from './VariantMediaPickerModal'
import { VariantFilterDropdown } from './VariantFilterDropdown'
import type { EditorContext } from './editor-context'

export function EditorWindows({ editor }: { editor: EditorContext }) {
  const { submitting, variantRows, platformUpdateDialogOpen, setPlatformUpdateDialogOpen, platformUpdateConnectionIds, setPlatformUpdateConnectionIds, platformUpdatePreferences, savingPlatformUpdateChoice, lightboxImage, setLightboxImage, variantMediaModal, setVariantMediaModal, variantBulkEdit, setVariantBulkEdit, variantBulkEditFilterSelections, setVariantBulkEditFilterSelections, chartKey, productUpdateTargets, platformUpdateFamilyReach, updateFamilyProduct, mediaChoices, bulkMediaGroups, variantBulkEditFilterGroups, variantBulkEditMatchingRows, selectedBulkMediaMatchCount, selectedBulkMediaGroupMatchCount, selectedVariantMediaRefs, applyVariantBulkEdit, applyVariantMediaSelection, changePlatformUpdatePreferences, savePlatformUpdateChoice, sendPlatformUpdateChoice } = editor
  return <>
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
      previewProductIds={updateFamilyProduct ? [...new Set([updateFamilyProduct.id, ...(updateFamilyProduct.familyProductIds ?? [])])] : undefined}
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
  </>
}
