// A model's Temel Ürün Bilgileri (name, description, brand, category, VAT, warranty, delivery days and the card's
// category attributes) are one value for the whole model (owner, 14.11.2026: "Bunlar ortak değerlerdir, her varyantında
// eşit olmalı"): the server opens the editor with the model's value whichever colour row was opened, and every save
// names all of them, so the server writes them to every colour card of the model (each name keeping its own colour
// word). The variant rows (codes, stock, prices, marketplace prices, pictures) stay each variant's own.
export const FAMILY_SHARED_FIELDS = ['title', 'description', 'brand', 'category', 'vat', 'warranty', 'deliveryDays', 'attributes'] as const
