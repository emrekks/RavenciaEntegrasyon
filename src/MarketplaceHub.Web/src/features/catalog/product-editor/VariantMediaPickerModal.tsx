import { useState } from 'react'
import { TitleHint, UiIcon, DialogSurface } from '../../../shared/components'
import { VariantMediaBulkSelect } from './VariantMediaBulkSelect'
import { isSizeChartRef } from './size-chart-order'
import { MediaOptionThumb, toggleVariantMediaRef, type ProductMediaOption } from './variant-media-options'
import { VariantMediaGroup } from './editor-types'

export function VariantMediaPickerModal({
  mode,
  options,
  chartKey,
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
  // The model's size chart: its tile is ticked and locked (it goes last with every variant by itself).
  chartKey: string | null
  selectedRefs: string[]
  groups?: VariantMediaGroup[]
  selectedGroupId?: string
  selectedValueId?: string
  matchedVariantCount?: number
  matchedGroupVariantCount?: number
  assignedValueIds?: ReadonlyMap<string, number>
  onRefsChange: (values: string[]) => void
  onGroupChange?: (value: string) => void
  onValueChange?: (value: string) => void
  onApply: () => void
  onClose: () => void
}) {
  // The ticks the window opened with: a changed choice keeps it open on a click outside (scan D-11).
  const [openedRefs] = useState(selectedRefs)
  const changed = openedRefs.length !== selectedRefs.length || openedRefs.some((ref, index) => ref !== selectedRefs[index])
  const selectedGroup = groups?.find(group => group.id === selectedGroupId)
  const selectedValue = selectedGroup?.values.find(value => value.id === selectedValueId)
  return <div className="workspace-modal-backdrop variant-media-picker-backdrop" role="presentation" onMouseDown={() => { if (!changed) onClose() }}>
    <DialogSurface as="section" onClose={onClose} className={`workspace-modal variant-media-picker-modal${mode === 'bulk' ? ' is-bulk' : ''}`} aria-labelledby="variant-media-picker-title" onMouseDown={event => event.stopPropagation()}>
      <header>
        <div><p className="eyebrow">Varyant Görselleri</p><h2 id="variant-media-picker-title">{mode === 'bulk' ? 'Seçeneklere Görsel Ata' : 'Varyant Görsellerini Seç'}<TitleHint label={mode === 'bulk' ? 'Seçeneklere Görsel Ata' : 'Varyant Görsellerini Seç'}>{mode === 'bulk' ? 'Seçenek değerlerine görsel atayın; değerler arasında geçince seçimler korunur, hepsi tek seferde kaydedilir.' : 'Bu varyanta ait görselleri seçin; sıra ürün görsellerindeki sırayla aynı kaydedilir.'}</TitleHint></h2></div>
        <button type="button" className="modal-close" onClick={onClose} aria-label="Pencereyi kapat"><UiIcon name="close" /></button>
      </header>
      {mode === 'bulk' && groups?.length ? <><div className="variant-media-bulk-fields"><VariantMediaBulkSelect label="Seçenek Grubu" placeholder="Seçenek Grubu Seçin" value={selectedGroupId ?? ''} options={groups.map(group => ({ id: group.id, label: group.name }))} onChange={value => onGroupChange?.(value)} /><VariantMediaBulkSelect label="Seçenek Değeri" placeholder="Değer Seçin" value={selectedValueId ?? ''} options={selectedGroup?.values.map(value => ({ id: value.id, label: value.value, assigned: assignedValueIds?.has(value.id), imageCount: assignedValueIds?.get(value.id) })) ?? []} disabled={!selectedGroup} onChange={value => onValueChange?.(value)} /></div>{selectedValue && <div className={`variant-media-bulk-match-summary ${matchedVariantCount ? '' : 'is-empty'}`} role="status"><strong>{selectedGroup?.name}: {selectedValue.value}</strong><span>{matchedVariantCount ? `${matchedVariantCount} varyant eşleşti` : 'Bu değerle eşleşen varyant bulunamadı'}</span></div>}</> : null}
      <div className="variant-media-picker-grid">
        {options.length ? options.map(option => <MediaOptionThumb key={option.value} option={option} selected={selectedRefs.includes(option.value)} automatic={isSizeChartRef(option.value, chartKey)}
          onClick={() => onRefsChange(toggleVariantMediaRef(options, selectedRefs, option.value, chartKey))} />) : <div className="empty small"><strong>Seçilebilir görsel yok</strong><p>Önce ürün görsellerine HTTPS linki veya dosya ekleyin.</p></div>}
      </div>
      <footer><button type="button" className="secondary" onClick={() => onRefsChange([])}>{mode === 'bulk' ? 'Bu Değerin Görsellerini Kaldır' : 'Görselleri Kaldır'}</button><button type="button" className="secondary" onClick={onClose}>Vazgeç</button><button type="button" onClick={onApply} disabled={!options.length || (mode === 'bulk' && (!selectedGroupId || !selectedValueId || !matchedGroupVariantCount))}>{mode === 'bulk' ? 'Tüm Eşlemeleri Kaydet' : 'Görselleri Kaydet'}</button></footer>
    </DialogSurface>
  </div>
}
