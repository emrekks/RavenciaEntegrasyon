import { formatMoney } from '../../shared/format-money'

export type PreviewSummaryItem = { canConfirm: boolean; payableTotal: number; currency: string }

// The whole selection at a glance before the approval (owner, 16 Nov 2026: the selection is kept across pages, so the
// window may hold orders the user no longer sees): every order the server read again, how many the approval will invoice,
// what those come to (per currency), and how many it refuses.
export function invoicePreviewSummary(previews: readonly PreviewSummaryItem[]) {
  const eligible = previews.filter(item => item.canConfirm)
  const totals = new Map<string, number>()
  for (const item of eligible) totals.set(item.currency, (totals.get(item.currency) ?? 0) + Math.round(item.payableTotal * 100))
  const amounts = [...totals.entries()].map(([currency, kurus]) => formatMoney(kurus / 100, currency))
  const refused = previews.length - eligible.length
  return [
    `${previews.length} sipariş okundu`,
    `${eligible.length} sipariş faturalanacak`,
    ...(amounts.length ? [`Fatura Toplamı ${amounts.join(' + ')}`] : []),
    ...(refused ? [`${refused} sipariş faturalanamaz`] : [])
  ].join(' · ')
}
