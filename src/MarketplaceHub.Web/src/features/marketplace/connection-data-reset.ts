export type ConnectionDataResetScope = 'ORDERS' | 'RETURNS' | 'INVOICES' | 'PRODUCTS' | 'CATEGORIES' | 'CATEGORY_ATTRIBUTES' | 'BRANDS'

export type ConnectionDataResetOption = {
  scope: ConnectionDataResetScope
  label: string
  description: string
}

const invoiceOption: ConnectionDataResetOption = {
  scope: 'INVOICES',
  label: 'Fatura verileri',
  description: 'Bu bağlantıyla sağlayıcı veya sipariş üzerinden ilişkili faturaları ve belgelerini kaldırır.'
}

const orderOptions: ConnectionDataResetOption[] = [
  {
    scope: 'ORDERS',
    label: 'Sipariş verileri',
    description: 'Bu bağlantının siparişlerini, bağlı iadeleri ve faturaları kaldırır.'
  },
  {
    scope: 'RETURNS',
    label: 'İade verileri',
    description: 'Bu bağlantının iade taleplerini, kararlarını ve kanıtlarını kaldırır.'
  },
  invoiceOption
]

const productOption: ConnectionDataResetOption = {
  scope: 'PRODUCTS',
  label: 'Ürün verileri',
  description: 'Bu bağlantının ürün aktarım ve kanal kayıtlarını kaldırır. Başka bağlantılarda kullanılan ortak ürünler korunur.'
}

const categoryOptions: ConnectionDataResetOption[] = [
  {
    scope: 'CATEGORIES',
    label: 'Kategori verileri',
    description: 'Bu bağlantının indirilen kategori ağacını, kategori eşleştirmelerini ve kategori özellik snapshotlarını kaldırır. Paneldeki yerel kategori ağacı korunur.'
  },
  {
    scope: 'CATEGORY_ATTRIBUTES',
    label: 'Kategori özellik verileri',
    description: 'Bu bağlantının kategori özelliklerini, değerlerini ve özellik eşleştirmelerini kaldırır.'
  }
]

const brandOption: ConnectionDataResetOption = {
  scope: 'BRANDS',
  label: 'Marka verileri',
  description: 'Bu bağlantının indirilen marka listesini ve marka eşleştirmelerini kaldırır.'
}

export function connectionDataResetOptions(platformCode: string): ConnectionDataResetOption[] {
  if (platformCode === 'TRENDYOL_EFATURAM') return [invoiceOption]

  const options = [...orderOptions, productOption]
  if (platformCode === 'TRENDYOL' || platformCode === 'HEPSIBURADA') options.push(...categoryOptions)
  if (platformCode === 'TRENDYOL') options.push(brandOption)
  return options
}
