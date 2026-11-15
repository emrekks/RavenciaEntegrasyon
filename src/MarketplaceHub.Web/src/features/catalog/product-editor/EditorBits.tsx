// Small presentational pieces of the product editor: icons, field labels and messages, the page shell.
import { type ReactNode } from 'react'
import { Page } from '../shared'

export function VariantDragHandleIcon() {
  return <svg className="variant-drag-icon" viewBox="0 0 16 20" focusable="false" aria-hidden="true"><circle cx="5" cy="4" r="1.4" /><circle cx="11" cy="4" r="1.4" /><circle cx="5" cy="10" r="1.4" /><circle cx="11" cy="10" r="1.4" /><circle cx="5" cy="16" r="1.4" /><circle cx="11" cy="16" r="1.4" /></svg>
}
export function BarcodeFillIcon() {
  return <svg className="variant-header-action-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" focusable="false" aria-hidden="true"><path d="M11 14h10" /><path d="M16 4h2a2 2 0 0 1 2 2v1.344" /><path d="m17 18 4-4-4-4" /><path d="M8 4H6a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h12a2 2 0 0 0 1.793-1.113" /><rect x="8" y="2" width="8" height="4" rx="1" /></svg>
}
export function BarcodePasteIcon() {
  return <BarcodeFillIcon />
}
// A message the server attached to one field of a refused save (422 fieldErrors), shown under that field.
export function EditorFieldError({ message }: { message?: string }) {
  return message ? <small className="product-field-error" role="alert">{message}</small> : null
}
// Barkodları Otomatik Oluştur: what the codes are made of (buildSequentialVariantCode), behind the field's "?".
export function automaticCodeHelp(field: 'barcode' | 'stock') {
  const rule = 'Model kodu büyük harfe çevrilir, Türkçe harfler sadeleşir (Ş→S, Ğ→G, İ→I), boşluklar “-” olur ve yalnız A-Z, 0-9, “.”, “-”, “_” kalır; sonuna “-” ve iki haneli sıra numarası eklenir: MZ070-01, MZ070-02 … Formda ya da katalogda kullanılan bir numara atlanır; kod en fazla 40 karakterdir.'
  return field === 'barcode'
    ? <>Otomatik oluşturulur. {rule} Tek ürünün barkodu 01 numarasını alır.</>
    : <>Otomatik oluşturulur; her varyantın stok kodu kendi barkoduyla aynıdır. {rule} Varyant tablosunda değiştirebilirsiniz.</>
}
export const variantCodesReadOnlyTitle = 'Varyantlı üründe kodlar varyant tablosunda düzenlenir.'
// A required field's label: its name and the red asterisk KDV Oranı has (required-fields.ts).
export function RequiredLabel({ children }: { children: ReactNode }) {
  return <span>{children}<b className="required-marker" aria-hidden="true">*</b></span>
}
// The product list's "Platformlarda Yayınla" opens the editor's publish step in a popup: the same cards, checks and
// Yayınla, without the page heading, the step bar or the leave-page guard (the popup closes instead of navigating).
export function EditorShell({ publishOnly, className, title, eyebrow = 'Katalog', meta, children }: { publishOnly: boolean; className: string; title: string; eyebrow?: string; meta?: ReactNode; children: ReactNode }) {
  return publishOnly ? <div className={`${className} product-publish-popup`}>{children}</div> : <Page className={className} title={title} eyebrow={eyebrow} meta={meta}>{children}</Page>
}
