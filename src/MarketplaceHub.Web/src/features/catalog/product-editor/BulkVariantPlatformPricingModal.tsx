import { useEffect, useEffectEvent, useMemo, useRef, useState, type CSSProperties } from 'react'
import { UiIcon, DialogSurface } from '../../../shared/components'
import { RrModal } from '../../../shared/ui/rr-components'
import { filterVariantsByOptions, selectVariantDraftsByKeys, type VariantOptionFilterSelections } from '../variant-filtering'
import { refreshedMatrixDrafts, type ChannelPricingDraft } from './marketplace-channel-pricing'
import { VariantPlatformStatus } from '../shared'
import { VariantMediaGroup, VariantDraft } from './editor-types'
import { pricingVariantLabel, sortVariantsAlphabetically, displayVariantOptionSignature, touchScreen, marketplacePlatformName, pricingPlatformRank } from './editor-helpers'
import { VariantFilterDropdown } from './VariantFilterDropdown'
import { priceInputNumber } from './card-prices'
import { priceFieldText } from '../quick-price-edit'

export function BulkVariantPlatformPricingModal({ row, rows, platforms: unorderedPlatforms, savedDrafts, productName, modelCode, filterGroups, matchesFilterValue, saving, onClose, onSave }: { row: VariantDraft; rows: VariantDraft[]; platforms: VariantPlatformStatus[]; savedDrafts: Record<string, ChannelPricingDraft>; productName: string; modelCode: string; filterGroups: VariantMediaGroup[]; matchesFilterValue: (row: VariantDraft, group: VariantMediaGroup, value: { id: string; value: string }) => boolean; saving: boolean; onClose: () => void; onSave: (drafts: Record<string, ChannelPricingDraft>) => void }) {
  // The columns follow the marketplaces in the panel's order (Trendyol, Hepsiburada, N11, Pazarama, PttAVM, Shopify).
  const platforms = useMemo(() => [...unorderedPlatforms].sort((left, right) => pricingPlatformRank(left.platformCode) - pricingPlatformRank(right.platformCode)), [unorderedPlatforms])
  const sortedRows = sortVariantsAlphabetically(rows)
  const platformKey = (platform: VariantPlatformStatus) => `${platform.platformCode}:${platform.connectionId ?? platform.platform}`
  // The drafts start again only when the marketplaces or their prices change, not when the caller passes an equal list
  // anew: a new array on every render reset the typed prices and the dirty keys on each render (Kaydet stayed off).
  const platformsKey = platforms.map(platform => `${platformKey(platform)}=${platform.listPrice ?? ''}/${platform.salePrice ?? ''}`).join('|')
  const initialMatrixDrafts = () => Object.fromEntries(sortedRows.flatMap(item => platforms.map(platform => {
    const status = item.platformStatuses?.find(value => value.connectionId === platform.connectionId)
    const saved = platform.connectionId ? savedDrafts[`${item.key}:${platform.connectionId}`] : undefined
    return [`${item.key}:${platformKey(platform)}`, { listPrice: saved?.listPrice ?? priceFieldText(status?.listPrice ?? platform.listPrice ?? item.listPrice), salePrice: saved?.salePrice ?? priceFieldText(status?.salePrice ?? platform.salePrice ?? item.salePrice) }]
  }))) as Record<string, ChannelPricingDraft>
  const [matrixDrafts, setMatrixDrafts] = useState<Record<string, ChannelPricingDraft>>(initialMatrixDrafts)
  const [dirtyMatrixDraftKeys, setDirtyMatrixDraftKeys] = useState<string[]>([])
  const [matrixBulkEdit, setMatrixBulkEdit] = useState<{ platform: VariantPlatformStatus; field: keyof ChannelPricingDraft; label: string; value: string; error: string } | null>(null)
  const [filterSelections, setFilterSelections] = useState<VariantOptionFilterSelections>({})
  // Another variant's window starts afresh. New rows for the same window (an offer was just saved) clear only the
  // cells whose stored price now matches what was typed: one failed price among many keeps the other typed prices.
  const matrixRowKey = useRef(row.key)
  const resetMatrixDrafts = useEffectEvent(() => {
    if (matrixRowKey.current !== row.key) {
      matrixRowKey.current = row.key
      setMatrixDrafts(initialMatrixDrafts()); setDirtyMatrixDraftKeys([])
      return
    }
    const next = refreshedMatrixDrafts(matrixDrafts, initialMatrixDrafts(), dirtyMatrixDraftKeys)
    setMatrixDrafts(next.drafts); setDirtyMatrixDraftKeys(next.dirtyKeys)
  })
  useEffect(() => resetMatrixDrafts(), [row.key, rows, platformsKey, savedDrafts])
  const matchingRows = filterVariantsByOptions(sortedRows, filterGroups, filterSelections, matchesFilterValue)
  // Platform and Fiyat Türü narrow the columns shown (none chosen shows them all); Beden / Renk narrow the bulk rows.
  const priceFields = [{ id: 'listPrice', value: 'Liste Fiyatı' }, { id: 'salePrice', value: 'Satış Fiyatı' }] as const
  const columnFilterGroups: VariantMediaGroup[] = [{ id: 'platform', name: 'Platform', values: platforms.map(platform => ({ id: platformKey(platform), value: marketplacePlatformName(platform) })) }, { id: 'priceType', name: 'Fiyat Türü', values: priceFields.map(field => ({ ...field })) }]
  const [columnFilters, setColumnFilters] = useState<Record<string, string[]>>({})
  const shownPlatforms = columnFilters.platform?.length ? platforms.filter(platform => columnFilters.platform.includes(platformKey(platform))) : platforms
  const shownFields = columnFilters.priceType?.length ? priceFields.filter(field => columnFilters.priceType.includes(field.id)) : priceFields
  // A row reads by its values only ("XL - Lacivert"), as in the editor's table; the option names are on hover.
  const rowLabel = (item: VariantDraft) => { const label = displayVariantOptionSignature(item.optionSignature, item.options); return label && label !== '—' ? label : item.sku }
  const matrixDraft = (item: VariantDraft, platform: VariantPlatformStatus) => matrixDrafts[`${item.key}:${platformKey(platform)}`] ?? { listPrice: '', salePrice: '' }
  const updateMatrixDraft = (item: VariantDraft, platform: VariantPlatformStatus, field: keyof ChannelPricingDraft, value: string) => {
    const draftKey = `${item.key}:${platformKey(platform)}`
    setDirtyMatrixDraftKeys(current => current.includes(draftKey) ? current : [...current, draftKey])
    setMatrixDrafts(current => ({ ...current, [draftKey]: { ...(current[draftKey] ?? { listPrice: '', salePrice: '' }), [field]: value } }))
  }
  const openMatrixBulkEdit = (platform: VariantPlatformStatus, field: keyof ChannelPricingDraft, label: string) => setMatrixBulkEdit({ platform, field, label, value: matrixDraft(matchingRows[0] ?? row, platform)[field], error: '' })
  const applyMatrixBulkEdit = () => {
    if (!matrixBulkEdit) return
    const value = priceInputNumber(matrixBulkEdit.value)
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
            <div className="variant-platform-pricing-filter-grid" style={{ '--pricing-filter-count': filterGroups.length + columnFilterGroups.length } as CSSProperties} aria-label="Platform fiyatı filtreleri">{filterGroups.map(group => <VariantFilterDropdown key={group.id} group={group} selectedValueIds={filterSelections[group.id] ?? []} onToggle={valueId => setFilterSelections(current => ({ ...current, [group.id]: (current[group.id] ?? []).includes(valueId) ? current[group.id].filter(id => id !== valueId) : [...(current[group.id] ?? []), valueId] }))} onClear={() => setFilterSelections(current => ({ ...current, [group.id]: [] }))} />)}{columnFilterGroups.map(group => <VariantFilterDropdown key={group.id} group={group} selectedValueIds={columnFilters[group.id] ?? []} onToggle={valueId => setColumnFilters(current => ({ ...current, [group.id]: (current[group.id] ?? []).includes(valueId) ? current[group.id].filter(id => id !== valueId) : [...(current[group.id] ?? []), valueId] }))} onClear={() => setColumnFilters(current => ({ ...current, [group.id]: [] }))} />)}</div>
            <div className="variant-platform-pricing-matrix-scroll">
              <div className="variant-platform-pricing-matrix-grid" style={{ gridTemplateColumns: `220px repeat(${shownPlatforms.length * shownFields.length}, 128px)` }}>
                <div className="variant-platform-pricing-matrix-cell variant-platform-pricing-matrix-corner"><strong>Varyant</strong><small>Model / barkod</small></div>
                {shownPlatforms.flatMap(platform => shownFields.map(field => renderMatrixHeader(platform, field.value)))}
                {sortedRows.flatMap(item => [
                  <div className="variant-platform-pricing-matrix-cell variant-platform-pricing-matrix-row-label" key={`${item.key}:label`}><strong title={displayVariantOptionSignature(item.optionSignature, item.options, true)}>{rowLabel(item)}</strong><small>{item.barcode || item.sku}</small></div>,
                  ...shownPlatforms.flatMap(platform => {
                    const draft = matrixDraft(item, platform)
                    return shownFields.map(field => <div className="variant-platform-pricing-matrix-cell" key={`${item.key}:${platformKey(platform)}:${field.id === 'listPrice' ? 'list' : 'sale'}`}><input aria-label={`${pricingVariantLabel(item.optionSignature) || item.sku} ${marketplacePlatformName(platform)} ${field.value.toLocaleLowerCase('tr-TR')}`} type="text" inputMode="decimal" autoComplete="off" value={draft[field.id]} disabled={saving || !platform.connectionId} onChange={event => updateMatrixDraft(item, platform, field.id, event.target.value)} /></div>)
                  })
                ])}
              </div>
            </div>
          </section>
        </div>
        <footer><button type="button" onClick={() => onSave(selectVariantDraftsByKeys(matrixDrafts, dirtyMatrixDraftKeys))} disabled={saving || !dirtyMatrixDraftKeys.length || !platforms.some(platform => platform.connectionId)}>{saving ? 'Kaydediliyor…' : 'Kaydet'}</button><button type="button" className="secondary" onClick={onClose} disabled={saving}>Vazgeç</button></footer>
      </DialogSurface>
    </div>
    {matrixBulkEdit && <RrModal crumb="Platform Fiyatları" title={`${marketplacePlatformName(matrixBulkEdit.platform)} ${matrixBulkEdit.label} Toplu Değiştir`} hint={<>Değer, seçili renk ve bedenlerdeki varyantların bu platformdaki {matrixBulkEdit.label.toLocaleLowerCase('tr-TR')} alanına uygulanır. Uygula yalnız taslağı değiştirir; kalıcı olması için ana penceredeki Kaydet’e basın.</>} size="md" modalClass="variant-platform-pricing-bulk-modal" onClose={() => setMatrixBulkEdit(null)}
      foot={<><span className="rr-note" /><button type="button" className="rr-btn" onClick={() => setMatrixBulkEdit(null)}>Vazgeç</button><button type="button" className="rr-btn rr-primary" onClick={applyMatrixBulkEdit} disabled={!matchingRows.length}>Uygula</button></>}>
      <p className="variant-bulk-edit-target"><span>Uygulanacak Varyant</span><strong>{matchingRows.length.toLocaleString('tr-TR')} satır</strong></p>
      <label className="rr-fld" htmlFor="variant-platform-pricing-bulk-value"><small>Yeni Fiyat</small><input id="variant-platform-pricing-bulk-value" className="rr-in" data-autofocus={touchScreen ? undefined : true} type="text" inputMode="decimal" autoComplete="off" value={matrixBulkEdit.value} aria-describedby={matrixBulkEdit.error ? 'variant-platform-pricing-bulk-error' : undefined} onChange={event => setMatrixBulkEdit(current => current ? { ...current, value: event.target.value, error: '' } : current)} /></label>
      {matrixBulkEdit.error && <p className="rr-callout rr-danger" id="variant-platform-pricing-bulk-error" role="alert">{matrixBulkEdit.error}</p>}
    </RrModal>}
  </>
}
