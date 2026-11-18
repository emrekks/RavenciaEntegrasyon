import '../orders/order-row-marks.css'
import './return-request-tag.css'

/**
 * "Değişim" when the claim says the customer asked for an exchange (customerRequest CHANGE, ReturnCustomerRequestPolicy:
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

export function isPanelApprovalBlocked(item: { platformCode?: string | null; customerRequest?: string | null }) {
  return item.platformCode?.trim().toUpperCase() === 'TRENDYOL' && isExchangeRequest(item.customerRequest)
}

export function ReturnRequestTag({ customerRequest }: { customerRequest: string | null | undefined }) {
  if (!isExchangeRequest(customerRequest)) return null
  return <span className="rr-buyer-tag rr-return-exchange" title="Müşteri ürünün değişimini istedi">Değişim</span>
}
