// The groups of the database cleanup in Ayarlar › Veri: each data type with what it holds and the noun its count is shown with.
export type DataResetOption<S extends string = string> = {
  scope: S
  label: string
  description: string
  // The noun the count is shown with (ürün, kategori…).
  unit: string
}
export type DataResetGroup<S extends string = string> = { label: string; description: string; options: DataResetOption<S>[] }
