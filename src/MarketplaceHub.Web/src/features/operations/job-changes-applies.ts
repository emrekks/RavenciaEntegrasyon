// Which iş details carry Gidenler ve Değişenler (JobChanges.tsx, loaded only then): the price/stock sends, the stock
// scan and dispatch that queue them, and the product imports. Kept apart so the job list does not load the section.
const productImportTypes = ['PRODUCT_SYNC', 'SHOPIFY_PRODUCT_SYNC', 'HEPSIBURADA_PRODUCT_SYNC']
const sendTypes = ['PRICE_INVENTORY_SYNC', 'STOCK_RECONCILIATION', 'STOCK_PROJECTION_DISPATCH']

/** Whether an iş has the section. */
export function jobHasChanges(jobType: string) {
  const type = jobType.toUpperCase()
  return sendTypes.includes(type) || productImportTypes.includes(type)
}

export function isProductImportJob(jobType: string) {
  return productImportTypes.includes(jobType.toUpperCase())
}
