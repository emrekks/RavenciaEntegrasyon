import { lazy } from 'react'

export const AttributesPage = lazy(() => import('../features/catalog').then(module => ({ default: module.AttributesPage })))
export const BrandsPage = lazy(() => import('../features/catalog').then(module => ({ default: module.BrandsPage })))
export const CategoriesPage = lazy(() => import('../features/catalog').then(module => ({ default: module.CategoriesPage })))
export const ImportDetailPage = lazy(() => import('../features/catalog').then(module => ({ default: module.ImportDetailPage })))
export const ImportsPage = lazy(() => import('../features/catalog').then(module => ({ default: module.ImportsPage })))
export const InventoryPage = lazy(() => import('../features/catalog').then(module => ({ default: module.InventoryPage })))
export const NewProductPage = lazy(() => import('../features/catalog').then(module => ({ default: module.NewProductPage })))
export const ProductDetailPage = lazy(() => import('../features/catalog').then(module => ({ default: module.ProductDetailPage })))
export const ProductsPage = lazy(() => import('../features/catalog').then(module => ({ default: module.ProductsPage })))

export const AttributeMappingPage = lazy(() => import('../features/marketplace').then(module => ({ default: module.AttributeMappingPage })))
export const IntegrationDetailPage = lazy(() => import('../features/marketplace').then(module => ({ default: module.IntegrationDetailPage })))
export const IntegrationsPage = lazy(() => import('../features/marketplace').then(module => ({ default: module.IntegrationsPage })))
export const MappingPage = lazy(() => import('../features/marketplace').then(module => ({ default: module.MappingPage })))
export const OrdersPage = lazy(() => import('../features/marketplace').then(module => ({ default: module.OrdersPage })))
export const ShipmentDetailPage = lazy(() => import('../features/marketplace').then(module => ({ default: module.ShipmentDetailPage })))
export const ShipmentsPage = lazy(() => import('../features/marketplace').then(module => ({ default: module.ShipmentsPage })))

export const BillingSettingsPage = lazy(() => import('../features/invoicing').then(module => ({ default: module.BillingSettingsPage })))
export const JobsPage = lazy(() => import('../features/operations').then(module => ({ default: module.JobsPage })))
