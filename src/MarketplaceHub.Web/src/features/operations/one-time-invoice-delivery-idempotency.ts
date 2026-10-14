export function oneTimeInvoiceDeliveryIdempotencyKey(id: string): string {
  return `one-time-invoice-delivery:stage-test-v2:${id}`
}
