// Which button sent the product form (WEB-SAYFALAR-10): Kaydet, Ürünü Oluştur or Taslak Olarak Kaydet, read from the
// submitter's data-submit-intent, its name="intent" value, or the form's intent field.
export function readSubmitIntent(submitter: Element | null | undefined, formIntent: FormDataEntryValue | null) {
  const named = (value: string) => submitter?.getAttribute('name') === 'intent' && submitter?.getAttribute('value') === value
  return {
    draft: submitter?.getAttribute('data-submit-intent') === 'draft',
    save: submitter?.getAttribute('data-submit-intent') === 'save' || named('save') || formIntent === 'save',
    create: submitter?.getAttribute('data-submit-intent') === 'create' || named('create')
  }
}
export type SubmitIntent = ReturnType<typeof readSubmitIntent>
