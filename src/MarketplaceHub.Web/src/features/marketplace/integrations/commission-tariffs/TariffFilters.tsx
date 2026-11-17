import { useEffect, useRef, useState, type ChangeEvent, type FormEvent, type ReactNode } from 'react'
import { UiIcon } from '../../../../shared/components'
import { parseTrNumber } from '../../../../shared/tr-number'
import { useDismissable } from '../../../../shared/hooks/useDismissable'
import { PanelSelect } from '../../../../shared/PanelSelect'
import { type TariffBulk, tiers, type TariffFilter, noFilter, type TariffSort } from './tariff-helpers'

// A header click goes up, then down, then back to no order.
export function nextSort(filter: TariffFilter, sort: TariffSort): TariffFilter {
  if (filter.sort !== sort) return { ...filter, sort, dir: 'asc' }
  return filter.dir === 'asc' ? { ...filter, sort, dir: 'desc' } : { ...filter, sort: undefined, dir: undefined }
}

// A filter card's field as the other filter cards draw it: its label above the input or list.
export function FilterField({ label, className, children }: { label: string; className?: string; children: ReactNode }) {
  return <label className={['commission-field', className].filter(Boolean).join(' ')}><span>{label}</span>{children}</label>
}

// A range field: one label over the least and the most value, each still named on its own for a screen reader.
export function FilterRange({ label, inputMode, min, max, onMin, onMax, minLabel, maxLabel }: {
  label: string; inputMode: 'numeric' | 'decimal'; min: string; max: string
  onMin: (event: ChangeEvent<HTMLInputElement>) => void; onMax: (event: ChangeEvent<HTMLInputElement>) => void; minLabel: string; maxLabel: string
}) {
  return <div className="commission-field" role="group" aria-label={label}>
    <span>{label}</span>
    <span className="commission-filter-range">
      <input type="text" inputMode={inputMode} placeholder="Min" value={min} onChange={onMin} aria-label={minLabel} />
      <i aria-hidden="true">–</i>
      <input type="text" inputMode={inputMode} placeholder="Max" value={max} onChange={onMax} aria-label={maxLabel} />
    </span>
  </div>
}

// Temizle and Filtrele, closing the card on the right as on the other filter cards; each stays in place and fades while
// it has nothing to do (nothing to clear, nothing typed that is not applied yet).
export function FilterActions({ onClear, canClear, canApply }: { onClear: () => void; canClear: boolean; canApply: boolean }) {
  return <div className="commission-filter-actions">
    <button type="button" className="secondary commission-filter-clear" disabled={!canClear} onClick={onClear}>Temizle</button>
    <button type="submit" className="rv-button-primary commission-filter-apply" disabled={!canApply}>Filtrele</button>
  </div>
}
// The filled fields of a filter: a draft and the applied filter are the same while these are.
export const filterEntries = (filter: object) => JSON.stringify(Object.entries(filter).filter(([, field]) => field).sort(([a], [b]) => a.localeCompare(b)))
export const filterHasValue = (filter: object) => Object.entries(filter).some(([key, field]) => key !== 'search' && field)

export function SheetScrim({ onClose }: { onClose: () => void }) {
  // Escape closes the sheet too (a tablet with a keyboard), as it does every other sheet and window.
  const close = useRef(onClose)
  useEffect(() => { close.current = onClose })
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => { if (event.key === 'Escape') close.current() }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [])
  return <span className="commission-sheet-scrim" aria-hidden="true" onClick={event => { event.stopPropagation(); onClose() }} />
}
export function SheetHead({ title }: { title: string }) {
  return <span className="commission-sheet-head" aria-hidden="true"><i className="commission-sheet-grab" /><b>{title}</b></span>
}

// The phone's search box over a list: the list follows the typing after a short pause.
export function PhoneSearch({ value, onSearch }: { value: string; onSearch: (value: string) => void }) {
  const [text, setText] = useState(value)
  const latest = useRef(onSearch)
  useEffect(() => { latest.current = onSearch })
  useEffect(() => {
    if (text === value) return undefined
    const timer = window.setTimeout(() => latest.current(text), 350)
    return () => window.clearTimeout(timer)
  }, [text, value])
  return <input type="search" className="commission-search" aria-label="Ürün ara" placeholder="Ürün, barkod veya model kodu" value={text} onChange={event => setText(event.target.value)} />
}

// Which products the list shows: the fields are typed first and applied with Filtrele (Enter too); Temizle
// empties them and shows every product again.
export function TariffFilters({ value, facets, onApply }: { value: TariffFilter; facets: { categories: string[]; brands: string[] } | undefined; onApply: (filter: TariffFilter) => void }) {
  const [draft, setDraft] = useState(value)
  const set = (key: keyof TariffFilter) => (event: { target: { value: string } }) => setDraft(current => ({ ...current, [key]: event.target.value }))
  // On phones the fields stay closed behind a Filtre button (with the number of filters applied) and open as a block.
  const [open, setOpen] = useState(false)
  const activeCount = [value.name, value.barcode, value.model, value.minStock || value.maxStock, value.category, value.brand,
    value.profitTier ? 'tier' : '', value.sort ?? ''].filter(Boolean).length
  const apply = (event?: FormEvent) => { event?.preventDefault(); onApply(draft); setOpen(false) }
  // Temizle empties the fields; the phone's search box keeps its text.
  const clear = () => { setDraft(noFilter); onApply(value.search ? { ...noFilter, search: value.search } : noFilter); setOpen(false) }
  const noFacts = facets !== undefined && facets.categories.length === 0 && facets.brands.length === 0
  return <div className="commission-filter-wrap">
    <button type="button" className="secondary commission-filter-toggle" aria-expanded={open} onClick={() => setOpen(current => !current)}>
      <UiIcon name="filter" />Filtre{activeCount > 0 && <em>{activeCount}</em>}
    </button>
    {open && <SheetScrim onClose={() => setOpen(false)} />}
  <form className={`commission-filters is-collapsible${open ? ' is-open' : ''}`} onSubmit={apply} aria-label="Ürün filtreleri">
    <SheetHead title="Filtre" />
    <FilterField label="Ürün Adı"><input type="search" value={draft.name} onChange={set('name')} /></FilterField>
    <FilterField label="Barkod"><input type="search" value={draft.barcode} onChange={set('barcode')} /></FilterField>
    <FilterField label="Model Kodu"><input type="search" value={draft.model} onChange={set('model')} /></FilterField>
    <FilterRange label="Stok" inputMode="numeric" min={draft.minStock} max={draft.maxStock} onMin={set('minStock')} onMax={set('maxStock')} minLabel="En az stok" maxLabel="En çok stok" />
    <FilterField label="Kategori"><PanelSelect ariaLabel="Kategori" value={draft.category} disabled={!facets?.categories.length} options={[{ value: '', label: 'Tümü' }, ...(facets?.categories ?? []).map(item => ({ value: item, label: item }))]} onChange={value => set('category')({ target: { value } })} /></FilterField>
    <FilterField label="Marka"><PanelSelect ariaLabel="Marka" value={draft.brand} disabled={!facets?.brands.length} options={[{ value: '', label: 'Tümü' }, ...(facets?.brands ?? []).map(item => ({ value: item, label: item }))]} onChange={value => set('brand')({ target: { value } })} /></FilterField>
    <FilterField label="Kolon Seçimi" className="commission-filter-band"><PanelSelect ariaLabel="Kolon Seçimi" value={String(draft.profitTier ?? '')} options={[{ value: '', label: 'Seçilmedi' }, ...tiers.map(tier => ({ value: String(tier), label: `${tier}. Fiyat Aralığı` }))]} onChange={value => set('profitTier')({ target: { value } })} /></FilterField>
    {draft.profitTier && <div className="commission-filter-profit">
      <FilterRange label="Kâr Tutarı (₺)" inputMode="decimal" min={draft.minProfit ?? ''} max={draft.maxProfit ?? ''} onMin={set('minProfit')} onMax={set('maxProfit')} minLabel="En az kâr tutarı (₺)" maxLabel="En çok kâr tutarı (₺)" />
      <FilterRange label="Kâr Oranı (%)" inputMode="decimal" min={draft.minShare ?? ''} max={draft.maxShare ?? ''} onMin={set('minShare')} onMax={set('maxShare')} minLabel="En az kâr oranı (%)" maxLabel="En çok kâr oranı (%)" />
    </div>}
    {noFacts && <p className="commission-filter-note">Kategori ve marka bu tarifede yok: tarife dosyası önceden yüklendi. Aynı dosyayı yeniden yüklediğinizde dolar.</p>}
    <FilterActions onClear={clear} canClear={filterHasValue(draft) || filterHasValue(value)} canApply={filterEntries(draft) !== filterEntries(value)} />
  </form>
  </div>
}

// A column's header with its order beside it: up (A–Z, low to high), down, then none. A band orders by what it leaves.
export function SortHeader({ label, sort, filter, onFilter, className }: { label: string; sort: TariffSort; filter: TariffFilter; onFilter: (filter: TariffFilter) => void; className?: string }) {
  const on = filter.sort === sort ? filter.dir ?? 'asc' : null
  return <th scope="col" className={className} aria-sort={on === 'asc' ? 'ascending' : on === 'desc' ? 'descending' : 'none'}>
    <button type="button" className={`commission-sort${on ? ' is-on' : ''}`} onClick={() => onFilter(nextSort(filter, sort))}
      title={on === null ? 'Artan sırala' : on === 'asc' ? 'Azalan sırala' : 'Sıralamayı kaldır'}>
      {label}<span aria-hidden="true">{on === 'asc' ? '↑' : on === 'desc' ? '↓' : '↕'}</span>
    </button>
  </th>
}

// Toplu Seçim: a column (a price band), then a criterion on its profit (its share
// of the cost or its amount) above or below a value, and the buttons that apply it, take that column's picks back or
// take every pick back.
export function BulkPicker({ bulk, onApply, onClearColumn, onClearAll }: { bulk: TariffBulk; onApply: (bulk: TariffBulk) => void; onClearColumn: (column: string) => void; onClearAll: () => void }) {
  const [open, setOpen] = useState(false)
  // Only the price bands can be picked as a column.
  const [column, setColumn] = useState(bulk.kind === 'tier' ? String(bulk.tier) : '')
  // There is no unconditional choice: a bulk pick always filters on profit, by share (%) unless the saved pick used an amount.
  const [criterion, setCriterion] = useState<'rate' | 'amount'>(bulk.minAmount != null || bulk.maxAmount != null ? 'amount' : 'rate')
  const [above, setAbove] = useState(bulk.maxRate == null && bulk.maxAmount == null)
  const [value, setValue] = useState(String(bulk.minRate ?? bulk.minAmount ?? bulk.maxRate ?? bulk.maxAmount ?? ''))
  const ref = useRef<HTMLDivElement>(null)
  useDismissable(open, [ref], () => setOpen(false))
  const limit = parseTrNumber(value)
  const valid = column !== '' && limit !== null
  const apply = () => {
    const tierColumn = /^[1-4]$/.test(column)
    const at = valid ? limit : null
    onApply({ kind: tierColumn ? 'tier' : column as TariffBulk['kind'], tier: tierColumn ? Number(column) : 1,
      minRate: criterion === 'rate' && above ? at : null, minAmount: criterion === 'amount' && above ? at : null,
      maxRate: criterion === 'rate' && !above ? at : null, maxAmount: criterion === 'amount' && !above ? at : null })
    setOpen(false)
  }
  const active = bulk.kind !== ''
  return <div className="commission-bulk" ref={ref}>
    <button type="button" className={`commission-bulk-trigger${active ? ' is-active' : ''}`} aria-haspopup="dialog" aria-expanded={open} onClick={() => setOpen(value => !value)}>
      {active && <em>Filtre Aktif</em>}Toplu Seçim <UiIcon name="chevronDown" size={14} />
    </button>
    {open && <SheetScrim onClose={() => setOpen(false)} />}
    {open && <div className="commission-bulk-popover" role="dialog" aria-label="Toplu seçim">
      <SheetHead title="Toplu Seçim" />
      <label htmlFor="commission-bulk-column"><span>Kolon</span>
        <PanelSelect id="commission-bulk-column" ariaLabel="Kolon" value={column} onChange={setColumn} options={[{ value: '', label: 'Kolon Seçimi' }, ...tiers.map(tier => ({ value: String(tier), label: `${tier}. Fiyat Aralığı` }))]} />
      </label>
      {column !== '' && <>
      <hr />
      <label htmlFor="commission-bulk-criterion"><span>Ölçüt</span>
        <PanelSelect id="commission-bulk-criterion" ariaLabel="Ölçüt" value={criterion} onChange={setCriterion} width={240} options={[{ value: 'rate', label: 'Kâr / Zarar Oranına Göre (%)' }, { value: 'amount', label: 'Kâr / Zarar Tutarına Göre (₺)' }]} />
      </label>
      <label htmlFor="commission-bulk-compare"><span>Karşılaştırma</span>
        <PanelSelect id="commission-bulk-compare" ariaLabel="Karşılaştırma" value={above ? 'above' : 'below'} onChange={next => setAbove(next === 'above')} width={240} options={[{ value: 'above', label: 'Verilenden Büyük Değerler' }, { value: 'below', label: 'Verilenden Küçük Değerler' }]} />
      </label>
      <label><span>Değer</span>
        <input type="text" inputMode="decimal" value={value} onChange={event => setValue(event.target.value)} placeholder={criterion === 'rate' ? 'ör. 30' : 'ör. 100'} />
      </label>
      <hr />
      <button type="button" className="rv-button-primary" disabled={!valid} onClick={apply}>Belirlenen Kriterlere Göre Seçimleri Yap</button>
      <button type="button" className="secondary is-danger" onClick={() => { onClearColumn(column); setOpen(false) }}>Yapılan Seçimleri Kaldır</button>
      </>}
      <hr />
      <button type="button" className="secondary" onClick={() => { onClearAll(); setColumn(''); setCriterion('rate'); setValue(''); setOpen(false) }}>Tüm Kolonlardan Seçimleri Kaldır</button>
    </div>}
  </div>
}
