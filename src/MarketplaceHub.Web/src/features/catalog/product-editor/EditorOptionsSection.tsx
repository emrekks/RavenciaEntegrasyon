// 5 Ürün Seçenekleri: the option groups and their values, Varyantları Oluştur, and (6) the variant groups.
import { TitleHint, UiIcon } from '../../../shared/components'
import { filterAttributeOptionValues, optionValueToAdd } from '../attribute-value-search'
import { OptionValueAddButton } from './OptionValueAddButton'
import { OptionValueChips, sortOptionValues } from './OptionValueChips'
import { PanelSelect } from '../../../shared/PanelSelect'
import { VariantGroupsSection } from './VariantGroupsSection'
import type { EditorContext } from './editor-context'

export function EditorOptionsSection({ editor }: { editor: EditorContext }) {
  const { form, attributeSelections, attributeTextValues, setAttributeTextValues, variantAttributeIds, automaticBarcodeGeneration, setAutomaticBarcodeGeneration, attributeValueQueries, setAttributeValueQueries, expandedOptionGroupIds, setExpandedOptionGroupIds, editorHydrating, requirements, allRequirements, visibleOptionRequirements, canAddVariantCombinations, toggleAttributeValue, addingOptionValueFor, addOptionValue, generateVariants } = editor
  return <>
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
      <VariantGroupsSection editor={editor} />
      </section>
  </>
}
