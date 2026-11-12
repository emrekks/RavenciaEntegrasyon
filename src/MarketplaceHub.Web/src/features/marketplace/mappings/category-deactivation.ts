// "Kategoriyi kaldır" switches the category off in place. PATCH /catalog/categories/{id} sets the parent it is sent
// (CatalogService.UpdateCategoryAsync), so the request carries the category's own parent: a null would move a
// sub-category to the root and rewrite its path.
export function categoryDeactivationBody(category: { name: string; parentId?: string | null }) {
  return { name: category.name, parentId: category.parentId ?? null, isActive: false }
}
