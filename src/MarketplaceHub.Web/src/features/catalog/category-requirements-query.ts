// A category's attribute requirements change only through a mapping edit or an attribute sync. The product editor
// keeps them for 10 minutes instead of reading them again on every opening; the screens that change them invalidate
// ['category-requirements'], and the API serves them from its own cache until the catalog changes.
export const categoryRequirementsStaleTimeMs = 10 * 60_000
