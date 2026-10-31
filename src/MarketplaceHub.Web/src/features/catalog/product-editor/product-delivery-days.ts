// Teslimat Süresi (Gün) on the product card: the days to hand an order to the carrier, sent as each marketplace's
// delivery/dispatch time (Trendyol deliveryDuration, n11 preparingDay). Whole days 1-30; a new card starts at 1. The API
// checks the same range (ProductDeliveryDays).
export const defaultProductDeliveryDays = 1
export const maxProductDeliveryDays = 30

export function isProductDeliveryDays(value: string) {
  const text = value.trim()
  if (!/^\d+$/.test(text)) return false
  const days = Number(text)
  return days >= 1 && days <= maxProductDeliveryDays
}

// The value sent with the card; the form refuses an invalid one before it gets here, the default stands in otherwise.
export function productDeliveryDays(value: string) {
  return isProductDeliveryDays(value) ? Number(value.trim()) : defaultProductDeliveryDays
}
