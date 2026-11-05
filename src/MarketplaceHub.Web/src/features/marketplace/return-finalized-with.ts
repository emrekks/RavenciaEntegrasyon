/** Hepsiburada's approval choice (FinalizedWith): the money back, or the product exchanged. */
export type FinalizedWith = 'Refund' | 'Change'

export const finalizedWithLabels: Record<FinalizedWith, string> = { Refund: 'Para İadesi', Change: 'Değişim' }

/**
 * The choice preselected from what the customer asked for (ReturnCustomerRequestPolicy: REFUND or CHANGE, read from
 * the Hepsiburada claim); '' when the claim does not say, and the seller must choose before sending.
 */
export function finalizedWithFor(customerRequest: string | null | undefined): FinalizedWith | '' {
  const value = customerRequest?.trim().toUpperCase()
  return value === 'REFUND' ? 'Refund' : value === 'CHANGE' ? 'Change' : ''
}

/** The line under the choice: what the customer asked for, or that it is unknown. */
export function customerRequestNote(customerRequest: string | null | undefined) {
  const preset = finalizedWithFor(customerRequest)
  return preset ? `Müşteri İsteği: ${finalizedWithLabels[preset]}` : 'Müşteri isteği bilinmiyor; göndermeden önce seçin.'
}
