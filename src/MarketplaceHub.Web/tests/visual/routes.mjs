// The main screens of the panel (name, path), shared by the smoke check and the screenshot tool.
export const routes = [
  ['dashboard', '/dashboard'], ['orders', '/orders'], ['returns', '/returns'], ['invoices', '/invoices'],
  ['products', '/products'], ['product-editor', '/products/p1'], ['product-new', '/products/new'], ['questions', '/questions'],
  ['reports', '/reports'], ...['profit', 'payouts', 'orders', 'products', 'stock', 'ops', 'customers', 'geo'].map(view => [`reports-${view}`, `/reports?view=${view}`]),
  ['jobs', '/jobs'], ...['loglar', 'api-saglik', 'kuyruk-kota', 'bildirimler'].map(tab => [`jobs-${tab}`, `/jobs?sekme=${tab}`]),
  ['integrations', '/integrations'], ['integration-detail', '/integrations/c-ty'], ['mappings', '/mappings/categories'], ['mappings-brands', '/mappings/categories?view=brands'],
  ['promotions', '/promotions'], ['promotions-plus', '/promotions?tab=plus'], ['settings', '/settings'],
  ...['sessions', 'alerts', 'users', 'database', 'offsite', 'ai', 'sender', 'dims', 'designer', 'read', 'palette', 'themes', 'notify'].map(tab => [`settings-${tab}`, `/settings?tab=${tab}`]),
]
