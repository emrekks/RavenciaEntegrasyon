import { RrAnchor, RrIcon } from '../marketplace/returns-ui'

type SortOption = { value: string; label: string }

// One list, the direction inside each choice (as Siparişler's "En Yeni / En Eski Siparişler"): no field picked
// twice and no separate arrow button. The values are the server's sort keys.
export const invoiceSortOptions: SortOption[] = [
  { value: 'ORDER_DESC', label: 'En Yeni Siparişler' },
  { value: 'ORDER_ASC', label: 'En Eski Siparişler' },
  { value: 'DELIVERY_DESC', label: 'Son Kargoya Verilenler' },
  { value: 'DELIVERY_ASC', label: 'İlk Kargoya Verilenler' },
  { value: 'DUE_ASC', label: 'Fatura Süresi Yaklaşanlar' },
  { value: 'AMOUNT_DESC', label: 'En Yüksek Tutar' },
  { value: 'AMOUNT_ASC', label: 'En Düşük Tutar' }
]

/** The filter card's Sıralama field (inside an "rr" scope): an rr-trigger and a radio list under it. */
export function InvoiceSortSelect({ value, onChange }: { value: string; onChange: (value: string) => void }) {
  const current = invoiceSortOptions.find(option => option.value === value) ?? invoiceSortOptions[0]
  return <div className="rr-fld invoice-reference-sort"><small>Sıralama</small><RrAnchor trigger={(open, toggle) => <button type="button" className={`rr-trigger${open ? ' rr-on' : ''}`} aria-haspopup="listbox" aria-expanded={open} aria-label={`Sıralama: ${current.label}`} onClick={toggle}><span>{current.label}</span><RrIcon name="chev" /></button>}>{close => <div className="rr-pop" role="listbox" aria-label="Sıralama" style={{ minWidth: 250 }}>{invoiceSortOptions.map(option => { const on = option.value === current.value; return <button key={option.value} type="button" role="option" aria-selected={on} className={`rr-opt${on ? ' rr-on' : ''}`} onClick={() => { onChange(option.value); close() }}><span className="rr-radio" aria-hidden="true" /><span className="rr-grow">{option.label}</span></button> })}</div>}</RrAnchor></div>
}
