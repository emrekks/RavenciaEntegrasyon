// The product editor's card fields as the form holds them (text, as typed), and the empty form a new product opens with.
export const EMPTY_EDITOR_FORM = { title: '', description: '', brandId: '', categoryId: '', baseSku: '', barcode: '', modelCode: '', weight: '', width: '', length: '', height: '', desi: '1', warrantyMonths: '0', deliveryDays: '1', listPrice: '', salePrice: '', costPrice: '', currency: 'TRY', vatRate: '10', vatIncluded: 'INCLUDED', initialStock: '0', safetyStock: '0', mediaUrls: '', status: 'ACTIVE' }

export type EditorForm = typeof EMPTY_EDITOR_FORM
