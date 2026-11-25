// Kategori Özellikleri: the category's attribute fields of the product editor.
import { moveListboxFocus } from '../../../shared/listbox-keys'
import { useRef, useState } from 'react'
import { TitleHint, UiIcon } from '../../../shared/components'
import { isOptionRequirement, type CategoryRequirement } from './publication-readiness'
import { focusOnMount } from '../../../shared/focus'
import { sortOptionValues } from './OptionValueChips'
import { cleanOptionValue } from '../shared'
import { useDismissable } from '../../../shared/hooks/useDismissable'
import { useMenuFlip } from '../../../shared/hooks/useMenuFlip'
import { PanelSelect } from '../../../shared/PanelSelect'
import { touchScreen } from './editor-helpers'
import { attributeNumberHelp, attributeNumberUnreadable } from './editor-number'

export function CategoryAttributeValueDropdown({
  attributeName,
  required,
  values,
  selectedValues,
  onToggleValue
}: {
  attributeName: string
  required: boolean
  values: Array<{ id: string; value: string }>
  selectedValues: string[]
  onToggleValue: (valueId: string) => void
}) {
  const [open, setOpen] = useState(false)
  const [search, setSearch] = useState('')
  const [alignMenuRight, setAlignMenuRight] = useState(false)
  const rootRef = useRef<HTMLDivElement>(null)
  const isSingle = true // a category attribute takes one value
  const selected = values.filter(value => selectedValues.includes(value.id))
  const normalizedSearch = search.trim().toLocaleLowerCase('tr-TR')
  const filteredValues = values.filter(value => !normalizedSearch || value.value.toLocaleLowerCase('tr-TR').includes(normalizedSearch))
  const summary = selected.length === 0 ? 'Değer Seçin' : isSingle ? selected[0].value : selected.length === 1 ? selected[0].value : `${selected[0].value} + ${selected.length - 1} değer`

  useDismissable(open, [rootRef], () => setOpen(false))
  useMenuFlip(open, rootRef, '.category-attribute-dropdown-menu')

  function clearSelection() {
    selected.forEach(value => onToggleValue(value.id))
  }

  function toggleDropdown() {
    if (!open) {
      const bounds = rootRef.current?.getBoundingClientRect()
      // Phones open the list as wide as its bar (at least 200px, styles/responsive.css), so that width decides the side.
      const menuWidth = window.innerWidth <= 760 && bounds ? Math.max(200, bounds.width) : Math.min(320, window.innerWidth - 32)
      setAlignMenuRight(Boolean(bounds && bounds.left + menuWidth > window.innerWidth - 16))
    }
    setOpen(current => !current)
  }

  return <div className="category-attribute-dropdown" ref={rootRef}>
    <button type="button" className={`category-attribute-select-trigger ${selected.length ? 'active' : 'is-empty'} ${required ? 'is-required' : 'is-optional'}`} aria-haspopup="listbox" aria-expanded={open} onClick={toggleDropdown}>
      <span>{summary}</span>
      <UiIcon name="chevronDown" />
    </button>
    {open && <div className={`category-attribute-dropdown-menu ${alignMenuRight ? 'is-edge-aligned' : ''}`} role="listbox" aria-label={`${attributeName} değerleri`} tabIndex={-1} onKeyDown={moveListboxFocus}>
      <div className="category-attribute-dropdown-tools">
        <input ref={touchScreen ? undefined : focusOnMount} value={search} onChange={event => setSearch(event.target.value)} placeholder="Değer ara..." aria-label={`${attributeName} değerlerinde ara`} />
      </div>
      {selected.length > 0 && <button type="button" className="category-attribute-clear-selection" onClick={clearSelection}>Seçimi Temizle</button>}
      <div className="category-attribute-dropdown-options">
        {filteredValues.length ? filteredValues.map(value => {
          const isSelected = selectedValues.includes(value.id)
          return <button type="button" role="option" aria-selected={isSelected} className={isSelected ? 'active' : ''} key={value.id} onClick={() => { onToggleValue(value.id); if (isSingle) setOpen(false) }}><span className={isSingle ? 'pm-mark-radio' : 'pm-mark-check'} aria-hidden="true" /><span>{value.value}</span></button>
        }) : <span className="category-attribute-dropdown-empty">Aramaya uygun değer yok.</span>}
      </div>
    </div>}
  </div>
}
export function CategoryAttributeMappingPanel({
  categoryId,
  categoryLabel,
  requirements,
  isLoading,
  isError,
  attributeSelections,
  attributeTextValues,
  onToggleValue,
  onTextChange,
  webColorRequirement,
  webColorAutoEnabled,
  manualWebColorValueId,
  onToggleWebColorAuto,
  onManualWebColorValueChange
}: {
  categoryId: string
  categoryLabel: string
  requirements: CategoryRequirement[]
  isLoading: boolean
  isError: boolean
  attributeSelections: Record<string, string[]>
  attributeTextValues: Record<string, string>
  onToggleValue: (attributeId: string, valueId: string) => void
  onTextChange: (attributeId: string, value: string) => void
  webColorRequirement?: CategoryRequirement
  webColorAutoEnabled: boolean
  manualWebColorValueId: string
  onToggleWebColorAuto: (enabled: boolean) => void
  onManualWebColorValueChange: (valueId: string) => void
}) {
  const attributes = requirements
    .filter(item => !isOptionRequirement(item) && item.attributeId !== webColorRequirement?.attributeId)
    .sort((left, right) => Number(right.isRequired) - Number(left.isRequired) || left.attribute.name.localeCompare(right.attribute.name, 'tr-TR', { sensitivity: 'base' }))

  return <section className="panel product-step-card product-category-mapping-card">
    <div className="editor-section-title">
      <span>3</span>
      <div>
        <h2>Kategori Özellikleri</h2>
        {categoryLabel ? <p>Seçili kategori: {categoryLabel}</p> : null}
      </div>
    </div>
    {isLoading ? (
      <p>Kategori özellikleri yükleniyor…</p>
    ) : !categoryId ? (
      <div className="unknown"><strong>Önce kategori seçin</strong><p>Kategori seçildiğinde eşlenmiş özellik başlıkları burada görünür.</p></div>
    ) : isError ? (
      <div className="unknown"><strong>Kategori özellikleri alınamadı</strong><p>Özellik başlıklarını Eşleştirmeler › Kategori Eşleme › Kategori Özellikleri ve Değerleri bölümünde hazırlayın.</p></div>
    ) : attributes.length || webColorRequirement ? (
      <div className="category-attribute-mapping-list">
        {webColorRequirement && <article className="category-attribute-field category-attribute-field-web-color required has-selection">
          <div className="category-attribute-field-head">
            <div>
              <strong>Web Color <b className="required-marker" aria-hidden="true">*</b></strong>
              <small>Yalnız Trendyol · Zorunlu</small>
            </div>
          </div>
          <div className={`attribute-builder-web-color-mode${webColorAutoEnabled ? ' is-auto' : ' is-manual'}`}>
            <label className="attribute-builder-web-color-toggle">
              <input type="checkbox" checked={webColorAutoEnabled} onChange={event => onToggleWebColorAuto(event.target.checked)} />
              <span><strong>Varyant Renklerini Otomatik Aktar<TitleHint label="Varyant Renklerini Otomatik Aktar">{webColorAutoEnabled ? 'Açık · Her varyant kendi rengiyle Trendyol Web Color olarak gider.' : 'Kapalı · Seçilen renk tüm varyantlara Trendyol Web Color olarak gider; diğer pazaryerleri etkilenmez.'}</TitleHint></strong></span>
            </label>
            {webColorAutoEnabled ? null : (
              <label className="attribute-builder-web-color-manual">Trendyol Katalog Rengi<PanelSelect ariaLabel="Manuel Trendyol katalog rengi" value={manualWebColorValueId} placeholder="Trendyol Katalog Rengi Seçin" onChange={onManualWebColorValueChange} options={[{ value: '', label: 'Trendyol Katalog Rengi Seçin' }, ...sortOptionValues('Renk', webColorRequirement.attribute.values).map(value => ({ value: String(value.id), label: cleanOptionValue(value.value) }))]} /></label>
            )}
          </div>
        </article>}
        {attributes.map(item => {
          const selectedValues = attributeSelections[item.attributeId] ?? []
          const typedValue = attributeTextValues[item.attributeId] ?? ''
          const hasValue = item.attribute.values.length > 0 ? selectedValues.length > 0 : typedValue.trim().length > 0
          const displayName = item.attribute.name
          return <article className={`category-attribute-field ${item.isRequired ? 'required' : ''} ${hasValue ? 'has-selection' : ''} ${item.isRequired && !hasValue ? 'is-missing' : ''}`} key={item.attributeId}>
            <div className="category-attribute-field-head">
              <div>
                <strong>{displayName}{item.isRequired && <b className="required-marker" aria-hidden="true"> *</b>}</strong>
              </div>
            </div>
            {item.attribute.values.length ? (
              <CategoryAttributeValueDropdown attributeName={displayName} required={item.isRequired} values={item.attribute.values} selectedValues={selectedValues} onToggleValue={valueId => onToggleValue(item.attributeId, valueId)} />
            ) : item.attribute.dataType === 'BOOLEAN' ? (
              <PanelSelect ariaLabel={`${displayName} değeri`} value={attributeTextValues[item.attributeId] ?? ''} onChange={next => onTextChange(item.attributeId, next)} options={[{ value: '', label: 'Değer Seçin' }, { value: 'evet', label: 'Evet' }, { value: 'hayır', label: 'Hayır' }]} />
            ) : item.attribute.dataType === 'NUMBER' ? (
              // D-1: a Turkish text box ("12,5"), never type="number"; text the reader refuses stays on screen, marked.
              <input aria-label={`${displayName} değeri`} value={typedValue} onChange={event => onTextChange(item.attributeId, event.target.value)} type="text" inputMode="decimal" autoComplete="off" aria-invalid={attributeNumberUnreadable(typedValue) || undefined} title={attributeNumberUnreadable(typedValue) ? `Sayı okunamadı; ${attributeNumberHelp}` : undefined} placeholder="Örn. 12,5" />
            ) : (
              <input aria-label={`${displayName} değeri`} value={typedValue} onChange={event => onTextChange(item.attributeId, event.target.value)} type="text" placeholder={item.allowsCustomValue ? 'Değer girin' : 'Özellik değerini girin'} />
            )}
          </article>
        })}
      </div>
    ) : (
      <div className="empty small"><strong>Bu kategori için özellik başlığı yok</strong><p>Önce Eşleştirmeler › Kategori Eşleme › Kategori Özellikleri ve Değerleri bölümünden özellik başlıklarını oluşturun.</p></div>
    )}
  </section>
}
