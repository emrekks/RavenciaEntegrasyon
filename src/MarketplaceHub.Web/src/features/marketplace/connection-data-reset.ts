import type { DataResetGroup, DataResetOption } from '../../shared/components/DataResetLedger'

export type ConnectionDataResetScope = 'ORDERS' | 'RETURNS' | 'INVOICES' | 'PRODUCTS' | 'CATEGORIES' | 'CATEGORY_ATTRIBUTES' | 'BRANDS' | 'COMMISSION_TARIFFS' | 'PROMOTION_OFFERS'

export type ConnectionDataResetOption = DataResetOption<ConnectionDataResetScope>
export type ConnectionDataResetGroup = DataResetGroup<ConnectionDataResetScope>

const invoiceOption: ConnectionDataResetOption = {
  scope: 'INVOICES',
  label: 'Fatura Verileri',
  description: 'Bu bağlantıyla sağlayıcı veya sipariş üzerinden ilişkili faturaları ve belgelerini kaldırır.',
  unit: 'fatura'
}

const orderOptions: ConnectionDataResetOption[] = [
  {
    scope: 'ORDERS',
    label: 'Sipariş Verileri',
    description: 'Siparişleri ve gönderi kayıtlarını kaldırır; bağlı iadeler ve faturalar da silinir.',
    unit: 'sipariş',
    // The server removes an order's returns and invoices with it (ResetConnectionDataAsync).
    implies: ['RETURNS', 'INVOICES']
  },
  {
    scope: 'RETURNS',
    label: 'İade Verileri',
    description: 'Bu bağlantının iade taleplerini, kararlarını ve kanıtlarını kaldırır.',
    unit: 'iade'
  },
  invoiceOption
]

const productOption: ConnectionDataResetOption = {
  scope: 'PRODUCTS',
  label: 'Ürün Verileri',
  description: 'Bu bağlantının ürün aktarım ve kanal kayıtlarını kaldırır. Başka bağlantılarda kullanılan ortak ürünler korunur.',
  unit: 'ürün'
}

const categoryOptions: ConnectionDataResetOption[] = [
  {
    scope: 'CATEGORIES',
    label: 'Kategori Verileri',
    description: 'İndirilen kategori ağacını, kategori eşleştirmelerini ve özellik snapshotlarını kaldırır. Paneldeki yerel kategori ağacı korunur.',
    unit: 'kategori'
  },
  {
    scope: 'CATEGORY_ATTRIBUTES',
    label: 'Kategori Özellik Verileri',
    description: 'Bu bağlantının kategori özelliklerini, değerlerini ve özellik eşleştirmelerini kaldırır.',
    unit: 'kayıt'
  }
]

const brandOption: ConnectionDataResetOption = {
  scope: 'BRANDS',
  label: 'Marka Verileri',
  description: 'Bu bağlantının indirilen marka listesini ve marka eşleştirmelerini kaldırır.',
  unit: 'marka'
}

// The files uploaded on the Promotions page (Trendyol only, as the page is).
const pricingOptions: ConnectionDataResetOption[] = [
  {
    scope: 'COMMISSION_TARIFFS',
    label: 'Komisyon Tarifeleri',
    description: 'Yüklenen komisyon tarifesi dosyalarını ve satırlarını kaldırır.',
    unit: 'dosya'
  },
  {
    scope: 'PROMOTION_OFFERS',
    label: 'Promosyon Teklifleri',
    description: 'Plus, Avantajlı Ürün Etiketi ve Flash Ürünler dosyalarını kaldırır.',
    unit: 'dosya'
  }
]

export function connectionDataResetGroups(platformCode: string): ConnectionDataResetGroup[] {
  const normalizedPlatformCode = platformCode.trim().toUpperCase()
  if (normalizedPlatformCode === 'TRENDYOL_EFATURAM') return [{
    label: 'Fatura Verileri',
    description: 'Yalnızca bu e-fatura bağlantısına ait kayıtlar.',
    options: [invoiceOption]
  }]

  const groups: ConnectionDataResetGroup[] = [{
    label: 'Sipariş ve Finans',
    description: 'Sipariş, iade ve fatura kayıtları.',
    options: [...orderOptions]
  }]
  const catalogOptions = [productOption]
  if (normalizedPlatformCode === 'TRENDYOL' || normalizedPlatformCode === 'HEPSIBURADA' || normalizedPlatformCode === 'N11') catalogOptions.push(...categoryOptions)
  if (normalizedPlatformCode === 'TRENDYOL') catalogOptions.push(brandOption)
  groups.push({
    label: 'Ürün Kataloğu',
    description: 'Bu bağlantıdan alınan ürün ve katalog eşleştirmeleri.',
    options: catalogOptions
  })
  if (normalizedPlatformCode === 'TRENDYOL') groups.push({
    label: 'Fiyat ve Promosyon',
    description: 'Promosyonlar sayfasına yüklenen dosyalar.',
    options: [...pricingOptions]
  })
  return groups
}

export function connectionDataResetOptions(platformCode: string): ConnectionDataResetOption[] {
  return connectionDataResetGroups(platformCode).flatMap(group => group.options)
}
