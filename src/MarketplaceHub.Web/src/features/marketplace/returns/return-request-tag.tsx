import '../orders/order-row-marks.css'
import './return-request-tag.css'

/**
 * "Değişim Talebi" (as Trendyol's seller panel writes it) when the claim says the customer asked for an exchange
 * (customerRequest CHANGE, ReturnCustomerRequestPolicy:
 * Hepsiburada's claim type, Trendyol's exchange reason code or replacement package). A refund or an unknown request shows
 * nothing: an unknown one is never guessed.
 */
export function isExchangeRequest(customerRequest: string | null | undefined) {
  return customerRequest?.trim().toUpperCase() === 'CHANGE'
}

/**
 * A Trendyol exchange request is approved in Trendyol's seller panel ("Değişim Yap"); its API documents no exchange
 * action, so the panel sends no approval for it (the server refuses one: TRENDYOL_EXCHANGE_APPROVE_IN_SELLER_PANEL).
 */
export const TRENDYOL_EXCHANGE_NOTE = 'Değişim talebi Trendyol satıcı panelinden “Değişim Yap” ile işlenir; panelden iade onayı gönderilmez.'

/**
 * A Trendyol exchange request Trendyol accepted (its 48-hour auto-acceptance, or an approval in its panel) may be a refund
 * or an exchange; its claim does not say, so it counts as a return as before and the owner checks (review, 18 Nov 2026).
 */
export const TRENDYOL_EXCHANGE_ACCEPTED_NOTE = 'Değişim talebi Trendyol\'da iade olarak kabul edilmiş görünüyor; para iadesi mi değişim mi yapıldığını Trendyol panelinden kontrol edin.'

/** The owner's own "Değişim Onaylandı" mark on a request Trendyol answered Rejected. */
export const TRENDYOL_EXCHANGE_MARKED_NOTE = 'Değişim Onaylandı olarak elle işaretlendi; Trendyol\'a bir şey gönderilmedi. İşareti İşlemler menüsünden kaldırabilirsiniz.'

export function isPanelApprovalBlocked(item: { platformCode?: string | null; customerRequest?: string | null }) {
  return item.platformCode?.trim().toUpperCase() === 'TRENDYOL' && isExchangeRequest(item.customerRequest)
}

/**
 * A Trendyol claim waiting for the seller (WaitingInAction) is accepted by Trendyol itself 48 hours later
 * (trendyol-api-son.md getClaims: autoAccepted, "48 saat beklemede kalan iadeler sistem tarafından otomatik olarak kabul
 * edilir"); its deadline (actionDueAt) is that moment, the "Otomatik Onaya Kalan Süre" of Trendyol's seller panel.
 */
export function trendyolAutoAccept(item: { platformCode?: string | null; rawStatus?: string | null }) {
  return item.platformCode?.trim().toUpperCase() === 'TRENDYOL' && item.rawStatus?.replace(/[_\s-]/gu, '').toUpperCase() === 'WAITINGINACTION'
}

/** A Trendyol exchange the marketplace made: Trendyol answers it Rejected; the panel names it so (owner, 18 Nov 2026). */
export const EXCHANGE_APPROVED_LABEL = 'Değişim Onaylandı'

export function ReturnRequestTag({ customerRequest }: { customerRequest: string | null | undefined }) {
  if (!isExchangeRequest(customerRequest)) return null
  return <span className="rr-buyer-tag rr-return-exchange" title="Müşteri ürünün değişimini istedi">Değişim Talebi</span>
}
