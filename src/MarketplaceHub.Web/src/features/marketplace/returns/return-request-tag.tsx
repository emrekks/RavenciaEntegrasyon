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
 * An approved Trendyol exchange is no return (owner, 18 Nov 2026: no invoice work). Trendyol's claim names no refund, so
 * one it turned into a refund is not seen here: the owner checks the invoice by hand.
 */
export const TRENDYOL_EXCHANGE_KEPT_NOTE = 'Değişim olarak işlendi: faturada ve raporlarda iade sayılmaz, Stoğa Ekle açık kalır. Trendyol para iadesine çevirdiyse panel bunu göremez; faturayı elle kontrol edin.'

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

export function ReturnRequestTag({ customerRequest }: { customerRequest: string | null | undefined }) {
  if (!isExchangeRequest(customerRequest)) return null
  return <span className="rr-buyer-tag rr-return-exchange" title="Müşteri ürünün değişimini istedi">Değişim Talebi</span>
}
