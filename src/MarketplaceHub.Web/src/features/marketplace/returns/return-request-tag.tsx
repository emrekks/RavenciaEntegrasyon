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

export function ReturnRequestTag({ customerRequest }: { customerRequest: string | null | undefined }) {
  if (!isExchangeRequest(customerRequest)) return null
  return <span className="rr-buyer-tag rr-return-exchange" title="Müşteri ürünün değişimini istedi">Değişim</span>
}
