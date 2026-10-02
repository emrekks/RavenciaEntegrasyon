export type ConnectionDataResetScope = 'ORDERS' | 'RETURNS' | 'INVOICES' | 'PRODUCTS' | 'CATEGORIES' | 'CATEGORY_ATTRIBUTES' | 'BRANDS'

export type ConnectionDataResetOption = {
  scope: ConnectionDataResetScope
  label: string
  description: string
}

export type ConnectionDataResetGroup = {
  label: string
  description: string
  options: ConnectionDataResetOption[]
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
    description: 'Siparişleri ve gönderi kayıtlarını; bunlara bağlı iadeleri ve faturaları kaldırır.'
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

export function connectionDataResetGroups(platformCode: string): ConnectionDataResetGroup[] {
  const normalizedPlatformCode = platformCode.trim().toUpperCase()
  if (normalizedPlatformCode === 'TRENDYOL_EFATURAM') return [{
    label: 'Fatura verileri',
    description: 'Yalnızca bu e-fatura bağlantısına ait kayıtlar.',
    options: [invoiceOption]
  }]

  const groups: ConnectionDataResetGroup[] = [{
    label: 'Sipariş ve finans',
    description: 'Sipariş, iade ve fatura kayıtları.',
    options: [...orderOptions]
  }]
  const catalogOptions = [productOption]
  if (normalizedPlatformCode === 'TRENDYOL' || normalizedPlatformCode === 'HEPSIBURADA') catalogOptions.push(...categoryOptions)
  if (normalizedPlatformCode === 'TRENDYOL') catalogOptions.push(brandOption)
  groups.push({
    label: 'Ürün kataloğu',
    description: 'Bu bağlantıdan alınan ürün ve katalog eşleştirmeleri.',
    options: catalogOptions
  })
  return groups
}

export function connectionDataResetOptions(platformCode: string): ConnectionDataResetOption[] {
  return connectionDataResetGroups(platformCode).flatMap(group => group.options)
}
