import { useState } from 'react'
import { createPortal } from 'react-dom'
import { Modal } from '../../../shared/components'
import '../commission-tariffs.css'

// What one unit sold at a price pays besides its commission (UnitProfitCostsView on the server): the product cost, the
// store's cargo tariff, the service fee, the withholding tax and the net VAT, with the VAT rate they were taken at.
export type UnitProfitCosts = { cost: number; cargo: number; serviceFee: number; stoppage: number; netVat: number; vatRate: number }

const expenseVatRate = 20
const vat = (amount: number, rate: number) => rate > 0 ? amount * rate / (100 + rate) : 0
// To kuruş, half away from zero as the server rounds (16,865 is 16,87, not the float's 16,86).
export const kurus = (value: number) => { const rounded = Math.round(Number((Math.abs(value) * 100).toPrecision(12))) / 100; return value < 0 ? -rounded : rounded }

// The profit of a price of the seller's own, worked out as the server's UnitProfit does; the cargo, fee and VAT rate
// come from a band of the same product the server priced.
export function unitProfit(price: number, commission: number, cost: number, basis: Pick<UnitProfitCosts, 'cargo' | 'serviceFee' | 'vatRate'>): { profit: number; costs: UnitProfitCosts } {
  const { cargo, serviceFee, vatRate } = basis
  const stoppage = kurus((price - vat(price, vatRate)) * 0.01)
  const netVat = kurus(kurus(vat(price, vatRate)) - kurus(vat(cost, vatRate)) - kurus(vat(commission, expenseVatRate)) - kurus(vat(cargo, expenseVatRate)) - kurus(vat(serviceFee, expenseVatRate)))
  const costs = { cost, cargo, serviceFee, stoppage, netVat, vatRate }
  return { profit: kurus(price - commission - cost - cargo - serviceFee - stoppage - netVat), costs }
}

// The profit as a share of the product cost (Kâr Oranı).
export const onCost = (profit: number, costs: UnitProfitCosts | null | undefined) => costs && costs.cost > 0 ? kurus(profit / costs.cost * 100) : null

const money = (value: number) => `${value < 0 ? '−' : ''}${Math.abs(value).toLocaleString('tr-TR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })} ₺`
const percent = (value: number) => `%${value < 0 ? '−' : ''}${Math.abs(value).toLocaleString('tr-TR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`

// "100,85 ₺ Kâr (%63,03)": the profit and its share of the cost.
export function profitText(profit: number, costs: UnitProfitCosts | null | undefined) {
  const share = onCost(profit, costs)
  return `${money(profit)} ${profit < 0 ? 'Zarar' : 'Kâr'}${share === null ? '' : ` (${percent(share)})`}`
}

export type ProfitPrice = { price: number; rate: number | null; commission: number | null; remaining: number | null; costs?: UnitProfitCosts | null }

// A price's profit as a badge that opens its calculation (label: the range of a model's variants instead); the reason it
// is not known otherwise.
export function ProfitBadge({ price, title, missing, label }: { price: ProfitPrice; title?: string; missing?: string; label?: string }) {
  const [open, setOpen] = useState(false)
  if (price.remaining === null || !price.costs) return <b className="unit-profit-badge is-unknown">{missing ?? 'Kâr hesaplanamadı'}</b>
  return <>
    <button type="button" className={`unit-profit-badge${price.remaining < 0 ? ' is-loss' : ''}`} onClick={event => { event.stopPropagation(); setOpen(true) }}
      title="Nasıl hesaplandığını görmek için tıklayın">
      {label ?? profitText(price.remaining, price.costs)}<span aria-hidden="true">›</span>
    </button>
    {/* Out of the table cell, so the card's styles do not reach it. */}
    {open && createPortal(<ProfitBreakdown price={price} title={title} onClose={() => setOpen(false)} />, document.body)}
  </>
}

// Hesaplama Kalemleri: every charge the price pays, and what is left.
export function ProfitBreakdown({ price, title, onClose }: { price: ProfitPrice; title?: string; onClose: () => void }) {
  const costs = price.costs!
  const rows: Array<[string, string, number]> = [
    ['Satış Fiyatı', 'Müşterinin ödediği', price.price],
    ['Komisyon Tutarı', price.rate === null ? 'Komisyon oranı' : `Komisyon oranı ${percent(price.rate)}`, -(price.commission ?? 0)],
    ['Ürün Maliyeti', `Ürün ayarlarındaki maliyet, KDV'si %${costs.vatRate.toLocaleString('tr-TR')}`, -costs.cost],
    ['Kargo Ücreti', 'Mağazanın güncel kargo tarifesi', -costs.cargo],
    ['Hizmet Bedeli', 'Sipariş başına 10,99 ₺ + KDV', -costs.serviceFee],
    ['Stopaj Kesintisi', "KDV hariç fiyatın %1'i", -costs.stoppage],
    ['Net KDV', "Satış KDV'si − maliyet, komisyon, kargo ve hizmet bedeli KDV'si", -costs.netVat]
  ]
  return <Modal open onClose={onClose} className="unit-profit-modal" title="Hesaplama Kalemleri" description={title}>
    <ul className="unit-profit-rows">
      {rows.map(([label, note, amount]) => <li key={label}><span><b>{label}</b><small>{note}</small></span><strong>{money(amount)}</strong></li>)}
    </ul>
    <p className={`unit-profit-total${price.remaining! < 0 ? ' is-loss' : ''}`}><span>{price.remaining! < 0 ? 'Zarar' : 'Kâr'}</span><strong>{profitText(price.remaining!, costs)}</strong></p>
  </Modal>
}
