import type { VariantCodeIssueSummary } from '../platform-code-rules'

// The last sentence of the variant code callout. Kaydet waits while a code is missing or broken; a draft open in the
// editor can still be kept with Taslak Olarak Kaydet when its codes only miss a barcode or break a marketplace rule
// (a missing, repeated or taken stock code stops a draft too), so there the callout says so (owner, 15 Nov 2026).
export function variantCodeNoticeTail(summary: Pick<VariantCodeIssueSummary, 'missingBarcodes' | 'missingSkus' | 'ruleRows' | 'heldRows' | 'duplicateRows'>, editingDraft: boolean, draftSaveBlocked: boolean) {
  const draftMayBeSaved = editingDraft && !draftSaveBlocked && summary.missingSkus === 0 && summary.heldRows === 0 && summary.duplicateRows === 0
  if (!draftMayBeSaved) return 'Düzeltilene kadar Kaydet pasif kalır.'
  return summary.ruleRows === 0
    ? 'Taslak yine de kaydedilebilir; ürünü satışa açmak için barkod gerekir.'
    : 'Taslak yine de kaydedilebilir; ürünü satışa açmak için kodlar düzeltilmeli.'
}
