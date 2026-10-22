import { lazyPage } from '../shared/lazy-page'

// Each catalog route loads its own module (product list, product editor, catalog lists, imports) as its own chunk.
export const AttributesPage = lazyPage(() => import('../features/catalog/lists/CatalogListPages').then(module => ({ default: module.AttributesPage })))
export const BrandsPage = lazyPage(() => import('../features/catalog/lists/CatalogListPages').then(module => ({ default: module.BrandsPage })))
export const CategoriesPage = lazyPage(() => import('../features/catalog/lists/CatalogListPages').then(module => ({ default: module.CategoriesPage })))
export const ImportDetailPage = lazyPage(() => import('../features/catalog/imports/ImportPages').then(module => ({ default: module.ImportDetailPage })))
export const ImportsPage = lazyPage(() => import('../features/catalog/imports/ImportPages').then(module => ({ default: module.ImportsPage })))
export const NewProductPage = lazyPage(() => import('../features/catalog/product-editor/ProductEditorPages').then(module => ({ default: module.NewProductPage })))
export const ProductDetailPage = lazyPage(() => import('../features/catalog/product-editor/ProductEditorPages').then(module => ({ default: module.ProductDetailPage })))
export const ProductsPage = lazyPage(() => import('../features/catalog/products/ProductsPage').then(module => ({ default: module.ProductsPage })))

// Each marketplace route loads its own module (orders, shipments, returns, integrations, mappings) as its own chunk.
export const AttributeMappingPage = lazyPage(() => import('../features/marketplace/mappings/MappingPages').then(module => ({ default: module.AttributeMappingPage })))
export const IntegrationDetailPage = lazyPage(() => import('../features/marketplace/integrations/IntegrationPages').then(module => ({ default: module.IntegrationDetailPage })))
export const IntegrationsPage = lazyPage(() => import('../features/marketplace/integrations/IntegrationPages').then(module => ({ default: module.IntegrationsPage })))
export const MappingPage = lazyPage(() => import('../features/marketplace/mappings/MappingPages').then(module => ({ default: module.MappingPage })))
export const OrdersPage = lazyPage(() => import('../features/marketplace/orders/OrdersPage').then(module => ({ default: module.OrdersPage })))
export const ReturnDetailPage = lazyPage(() => import('../features/marketplace/returns/ReturnPages').then(module => ({ default: module.ReturnDetailPage })))
export const ReturnsPage = lazyPage(() => import('../features/marketplace/returns/ReturnPages').then(module => ({ default: module.ReturnsPage })))
export const ShipmentDetailPage = lazyPage(() => import('../features/marketplace/orders/ShipmentPages').then(module => ({ default: module.ShipmentDetailPage })))
export const ShipmentsPage = lazyPage(() => import('../features/marketplace/orders/ShipmentPages').then(module => ({ default: module.ShipmentsPage })))

export const InvoiceDetailPage = lazyPage(() => import('../features/invoicing').then(module => ({ default: module.InvoiceDetailPage })))
export const InvoicesPage = lazyPage(() => import('../features/invoicing').then(module => ({ default: module.InvoicesPage })))
export const ReportsPage = lazyPage(() => import('../features/reports/ReportsPage').then(module => ({ default: module.ReportsPage })))
export const JobsPage = lazyPage(() => import('../features/operations').then(module => ({ default: module.JobsPage })))
export const QuestionsPage = lazyPage(() => import('../features/questions').then(module => ({ default: module.QuestionsPage })))
