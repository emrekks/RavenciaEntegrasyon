// Klasik içe aktarma (/imports) template: the CSV header row the panel offers and the column mapping that goes with it.
// Garanti Süresi (Ay) and Teslimat Süresi (Gün) were added on 13 Nov 2026 (owner); the server checks them with the
// editor's limits (0-240 months, 0 = no warranty; 1-30 days) and a file without them keeps no warranty and 1 day.
export const importTemplateColumns: ReadonlyArray<{ header: string; field: string }> = [
  { header: 'Ürün Adı', field: 'title' },
  { header: 'Stok Kodu', field: 'sku' },
  { header: 'Barkod', field: 'barcode' },
  { header: 'Model Kodu', field: 'modelCode' },
  { header: 'Varyant Grubu', field: 'variantGroupKey' },
  { header: 'Renk', field: 'color' },
  { header: 'Beden', field: 'size' },
  { header: 'Marka', field: 'brand' },
  { header: 'Kategori', field: 'category' },
  { header: 'Açıklama', field: 'description' },
  { header: 'Liste Fiyatı', field: 'listPrice' },
  { header: 'Satış Fiyatı', field: 'salePrice' },
  { header: 'KDV Oranı', field: 'vatRate' },
  { header: 'Stok', field: 'stock' },
  { header: 'Desi', field: 'desi' },
  { header: 'Garanti Süresi (Ay)', field: 'warrantyMonths' },
  { header: 'Teslimat Süresi (Gün)', field: 'deliveryDays' }
]

export const importTemplateHeaders = importTemplateColumns.map(column => column.header).join(',')
export const importTemplateFields = importTemplateColumns.map(column => column.field).join(',')

// Turkish Excel opens a UTF-8 file with a byte order mark and ';' between fields as columns.
export function importTemplateCsv(): string {
  return `\uFEFF${importTemplateColumns.map(column => column.header).join(';')}\r\n`
}
