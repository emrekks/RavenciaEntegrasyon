import { useMemo, useState } from 'react'
import { createPortal } from 'react-dom'
import { Button, Modal } from '../../../shared/components'
import { DEFAULT_COMMISSION_RATE, DEFAULT_SERVICE_FEE, DEFAULT_VAT_RATE, appliedPrices, suggestPrice, type PriceTargetBasis } from './price-suggestion'
import './price-suggestion.css'

const twoDecimals = new Intl.NumberFormat('tr-TR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
const money = (value: number) => `${value < 0 ? '−' : ''}${twoDecimals.format(Math.abs(value))} ₺`
const percent = (value: number | null) => value === null ? '—' : `%${value < 0 ? '−' : ''}${twoDecimals.format(Math.abs(value))}`
const numberOf = (value: string) => { const parsed = Number(value.replace(',', '.')); return value.trim() === '' || !Number.isFinite(parsed) ? null : parsed }

// The commission and cargo a user typed last are kept in this browser (a product not on a store has no tariff yet).
const storageKey = 'rv.priceSuggestion.assumptions'
type Assumptions = { commissionRate: string; cargo: string; serviceFee: string }
function readAssumptions(): Assumptions {
  const fallback = { commissionRate: String(DEFAULT_COMMISSION_RATE), cargo: '0', serviceFee: String(DEFAULT_SERVICE_FEE) }
  try {
    const saved = JSON.parse(window.localStorage.getItem(storageKey) ?? 'null') as Partial<Assumptions> | null
    return saved ? { ...fallback, ...Object.fromEntries(Object.entries(saved).filter(([, value]) => typeof value === 'string')) } : fallback
  } catch { return fallback }
}
function writeAssumptions(value: Assumptions) { try { window.localStorage.setItem(storageKey, JSON.stringify(value)) } catch { /* private window: nothing kept */ } }

export type PriceSuggestionFields = { cost: string; vatRate: string; vatIncluded: boolean; listPrice: string }

// "Hesapla" beside Fiyat, Stok ve Vergi: the window that suggests a selling price for a wanted profit.
export function PriceSuggestionLink({ fields, onApply }: { fields: PriceSuggestionFields; onApply: (prices: { salePrice: string; listPrice: string }) => void }) {
  const [open, setOpen] = useState(false)
  return <>
    <button type="button" className="price-suggestion-link" onClick={() => setOpen(true)}>Hesapla</button>
    {/* Out of the pricing card, so the card's styles do not reach it (as the tariff's ProfitBadge). */}
    {open && createPortal(<PriceSuggestionDialog fields={fields} onClose={() => setOpen(false)} onApply={prices => { onApply(prices); setOpen(false) }} />, document.body)}
  </>
}

export function PriceSuggestionDialog({ fields, onClose, onApply }: { fields: PriceSuggestionFields; onClose: () => void; onApply: (prices: { salePrice: string; listPrice: string }) => void }) {
  const [basis, setBasis] = useState<PriceTargetBasis>('cost')
  const [cost, setCost] = useState(fields.cost)
  const [targetRate, setTargetRate] = useState('50')
  const [assumptions, setAssumptions] = useState(readAssumptions)
  const vatRate = numberOf(fields.vatRate) ?? DEFAULT_VAT_RATE
  const updateAssumption = (name: keyof Assumptions, value: string) => setAssumptions(current => { const next = { ...current, [name]: value }; writeAssumptions(next); return next })

  const suggestion = useMemo(() => {
    const values = { cost: numberOf(cost), targetRate: numberOf(targetRate), commissionRate: numberOf(assumptions.commissionRate), cargo: numberOf(assumptions.cargo), serviceFee: numberOf(assumptions.serviceFee) }
    if (Object.values(values).some(value => value === null)) return null
    return suggestPrice({ cost: values.cost!, targetRate: values.targetRate!, basis, commissionRate: values.commissionRate!, cargo: values.cargo!, serviceFee: values.serviceFee!, vatRate, vatIncluded: fields.vatIncluded })
  }, [cost, targetRate, assumptions, basis, vatRate, fields.vatIncluded])

  const missing = numberOf(cost) === null || numberOf(cost)! <= 0 ? 'Maliyeti girin.' : numberOf(targetRate) === null ? 'Hedef kâr oranını girin.' : 'Bu oran için fiyat bulunamadı; masraflar satışın bu kadarını bırakmıyor.'
  const footer = <>
    <Button variant="secondary" onClick={onClose}>İptal</Button>
    <Button disabled={!suggestion} onClick={() => suggestion && onApply(appliedPrices(suggestion.price, fields.listPrice))}>Fiyatı Uygula</Button>
  </>
  return <Modal open onClose={onClose} className="price-suggestion-modal" title="Fiyat Hesapla" hint="Maliyet ve istediğiniz kâr oranından satış fiyatı önerir; kâr, komisyon tarifesindeki kâr hesabıyla aynı kalemlerle bulunur." footer={footer}>
    <div className="price-suggestion-basis" role="group" aria-label="Kâr oranı neye göre">
      {([['cost', 'Maliyete Göre'], ['sale', 'Satışa Göre']] as const).map(([value, label]) => <button key={value} type="button" aria-pressed={basis === value} onClick={() => setBasis(value)}>{label}</button>)}
    </div>
    <div className="price-suggestion-fields">
      <label>Maliyet (₺)<input value={cost} onChange={event => setCost(event.target.value)} type="number" min="0" step="0.01" inputMode="decimal" /></label>
      <label>Hedef Kâr Oranı (%)<input value={targetRate} onChange={event => setTargetRate(event.target.value)} type="number" min="0" step="0.1" inputMode="decimal" /></label>
      <label>Komisyon Oranı (%)<input value={assumptions.commissionRate} onChange={event => updateAssumption('commissionRate', event.target.value)} type="number" min="0" max="99" step="0.1" inputMode="decimal" /></label>
      <label>Kargo Ücreti (₺)<input value={assumptions.cargo} onChange={event => updateAssumption('cargo', event.target.value)} type="number" min="0" step="0.01" inputMode="decimal" /></label>
      <label>Hizmet Bedeli (₺)<input value={assumptions.serviceFee} onChange={event => updateAssumption('serviceFee', event.target.value)} type="number" min="0" step="0.01" inputMode="decimal" /></label>
    </div>
    {suggestion ? <div className={`price-suggestion-result${suggestion.profit < 0 ? ' is-loss' : ''}`} aria-live="polite">
      <div className="price-suggestion-price"><span>Önerilen Satış Fiyatı{fields.vatIncluded ? '' : ' (KDV Hariç)'}</span><strong data-testid="price-suggestion-price">{money(suggestion.price)}</strong>
        {!fields.vatIncluded && <small>Müşterinin ödediği {money(suggestion.grossPrice)}</small>}</div>
      <dl>
        <div><dt>Kâr</dt><dd>{money(suggestion.profit)}</dd></div>
        <div><dt>Maliyete Göre</dt><dd>{percent(suggestion.onCost)}</dd></div>
        <div><dt>Satışa Göre</dt><dd>{percent(suggestion.margin)}</dd></div>
      </dl>
    </div> : <p className="price-suggestion-empty" aria-live="polite">{missing}</p>}
  </Modal>
}
