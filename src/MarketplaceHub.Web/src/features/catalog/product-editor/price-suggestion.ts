import { kurus, unitProfit, type UnitProfitCosts } from '../../marketplace/integrations/unit-profit'

// The product editor's "Hesapla" window: the selling price that leaves a wanted profit, worked out with the commission
// tariff's own profit calculation (unitProfit: commission, cost, cargo, service fee, 1 % withholding tax, net VAT).

// "Maliyete Göre": profit ÷ product cost (the reports' Maliyete Göre Kâr); "Satışa Göre": profit ÷ the price the
// customer pays (the reports' Kâr Marjı).
export type PriceTargetBasis = 'cost' | 'sale'

export type PriceSuggestionInput = {
  cost: number
  targetRate: number
  basis: PriceTargetBasis
  commissionRate: number
  cargo: number
  serviceFee: number
  vatRate: number
  // KDV Dahil mi: the price typed in Satış Fiyatı holds the VAT (Evet) or the VAT is added on top of it (Hayır).
  vatIncluded: boolean
}

export type PriceSuggestion = {
  // The price as Satış Fiyatı takes it (with or without VAT, as KDV Dahil mi says) and what the customer pays.
  price: number
  grossPrice: number
  commission: number
  profit: number
  onCost: number | null
  margin: number | null
  costs: UnitProfitCosts
}

// The platform service fee per order the reports and the tariff use (ReportCosts.PlatformServiceFeePerOrder: 10.99 TL
// + 20 % VAT).
export const DEFAULT_SERVICE_FEE = 13.19
// No tariff is known for a product not on a store yet; a common marketplace commission the user can change.
export const DEFAULT_COMMISSION_RATE = 20
export const DEFAULT_VAT_RATE = 20

const grossOf = (price: number, input: Pick<PriceSuggestionInput, 'vatIncluded' | 'vatRate'>) =>
  input.vatIncluded ? kurus(price) : kurus(price * (100 + input.vatRate) / 100)

// The profit of a price typed in Satış Fiyatı, costed as the tariff's ProfitBadge costs it.
export function evaluatePrice(price: number, input: Omit<PriceSuggestionInput, 'targetRate' | 'basis'>): PriceSuggestion {
  const entered = kurus(price)
  const grossPrice = grossOf(entered, input)
  const commission = kurus(grossPrice * input.commissionRate / 100)
  const { profit, costs } = unitProfit(grossPrice, commission, input.cost, { cargo: input.cargo, serviceFee: input.serviceFee, vatRate: input.vatRate })
  return {
    price: entered, grossPrice, commission, profit, costs,
    onCost: input.cost > 0 ? kurus(profit / input.cost * 100) : null,
    margin: grossPrice > 0 ? kurus(profit / grossPrice * 100) : null
  }
}

// The profit the target asks of a price: a share of the cost, or a share of the price the customer pays.
const targetProfit = (input: PriceSuggestionInput, grossPrice: number) =>
  input.basis === 'cost' ? input.cost * input.targetRate / 100 : grossPrice * input.targetRate / 100

// The price (to the kuruş) whose profit comes closest to the target; null when no price reaches it (the fees take a
// larger share of every lira than the target leaves, or a cost-based target without a cost).
export function suggestPrice(input: PriceSuggestionInput): PriceSuggestion | null {
  const values = [input.cost, input.targetRate, input.commissionRate, input.cargo, input.serviceFee, input.vatRate]
  if (values.some(value => !Number.isFinite(value) || value < 0) || input.commissionRate >= 100) return null
  if (input.basis === 'cost' && input.cost <= 0) return null
  const gap = (price: number) => { const result = evaluatePrice(price, input); return { result, gap: result.profit - targetProfit(input, result.grossPrice) } }
  // The profit is linear in the price but for the kuruş rounding: a straight line through two prices finds the point,
  // then the kuruş around it settle the rounding.
  const low = gap(0).gap
  const high = gap(100_000).gap
  const slope = (high - low) / 100_000
  if (slope <= 0) return null
  const estimate = kurus(-low / slope)
  if (!(estimate > 0) || estimate > 100_000_000) return null
  let best: { result: PriceSuggestion; gap: number } | null = null
  for (let step = -20; step <= 20; step += 1) {
    const price = kurus(estimate + step / 100)
    if (price <= 0) continue
    const candidate = gap(price)
    // The closest; on a tie the one that does not fall short of the target.
    if (!best || Math.abs(candidate.gap) < Math.abs(best.gap) - 1e-9 || (Math.abs(Math.abs(candidate.gap) - Math.abs(best.gap)) <= 1e-9 && candidate.gap > best.gap)) best = candidate
  }
  return best?.result ?? null
}

// Fiyatı Uygula: the sale price, and a list price raised to it when it is empty or lower (left alone otherwise).
export function appliedPrices(suggested: number, listPrice: string): { salePrice: string; listPrice: string } {
  const sale = String(kurus(suggested))
  const list = Number(listPrice.replace(',', '.'))
  return { salePrice: sale, listPrice: listPrice.trim() === '' || !Number.isFinite(list) || list < kurus(suggested) ? sale : listPrice }
}
