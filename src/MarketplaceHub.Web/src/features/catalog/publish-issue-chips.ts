// One gap of a publish card: pressing it opens its fix (`onRun`: the category fields window, a category mapping, a new
// check) or goes to the page that fixes it (`href`); a gap with neither is only information. `values`: the panel values a
// value-mapping gap is about (the colours Web Color has no value for).
// `tip`: the tooltip's lines besides the values (see attributeIssueTip).
export type PublishIssueRow = { key: string; title: string; detail: string; href?: string; onRun?: () => void; values?: string[]; tip?: string[] }

// The badge's popover shows one chip per title: gaps with the same title (two values of one field) share it, their details
// joined for the hover and the screen reader and their values listed together; a title already among the required gaps
// is left out of the optional ones.
export function publishIssueChips(rows: readonly PublishIssueRow[], optionalRows: readonly PublishIssueRow[]) {
  const group = (items: readonly PublishIssueRow[], skip: ReadonlySet<string>) => {
    const chips = new Map<string, PublishIssueRow>()
    for (const row of items) {
      if (skip.has(row.title)) continue
      const chip = chips.get(row.title)
      if (!chip) { chips.set(row.title, { ...row, values: row.values ? [...row.values] : undefined, tip: row.tip ? [...row.tip] : undefined }); continue }
      if (row.detail && !chip.detail.split(' · ').includes(row.detail)) chip.detail = chip.detail ? `${chip.detail} · ${row.detail}` : row.detail
      for (const value of row.values ?? []) if (!chip.values?.includes(value)) chip.values = [...(chip.values ?? []), value]
      for (const line of row.tip ?? []) if (!chip.tip?.includes(line)) chip.tip = [...(chip.tip ?? []), line]
      if (!chip.onRun && !chip.href) { chip.onRun = row.onRun; chip.href = row.href }
    }
    return [...chips.values()]
  }
  const required = group(rows, new Set())
  return { required, optional: group(optionalRows, new Set(required.map(row => row.title))) }
}

// The lines of a mapping gap's tooltip, read from the publish check's own finding (the single source the window also
// reads): a field with no mapping, a mapping on an older list, a product with no value, or the gap's own words.
export function attributeIssueTip(issue: { attribute: string; detail: string; state?: string; panelAttribute?: string; fieldName?: string }): string[] {
  const field = issue.fieldName ?? issue.attribute
  switch (issue.state) {
    case 'unmapped': return [`${field} eşlenmemiş`, ...(issue.panelAttribute && issue.panelAttribute !== field ? [`Eşlenebilecek panel özelliği: ${issue.panelAttribute}`] : issue.panelAttribute ? ['Aynı adlı panel özelliğiyle eşlenebilir'] : [])]
    case 'stale': return ['Eşleme eski kategori listesine göre, yeniden doğrulanmalı', ...(issue.panelAttribute ? [`Kayıtlı panel özelliği: ${issue.panelAttribute}`] : [])]
    case 'no-value': return [`${issue.attribute} için üründe değer seçilmemiş`]
    case 'values': return []
    default: return [issue.detail]
  }
}

// What a chip's tooltip says: the values still without a marketplace value, then its other lines (its detail when it has
// none).
export function publishIssueTipLines(row: PublishIssueRow) {
  const lines = [...(row.values?.length ? [`Eşlenmemiş değerler: ${row.values.join(', ')}`] : []), ...(row.tip ?? [])]
  return lines.length ? lines : row.detail ? [row.detail] : []
}

// The chip's text: its title, or for a value gap "Web Color eşleşmemiş: Mürdüm, Kiremit" (the first three, then "+N").
export function publishIssueChipText(row: PublishIssueRow) {
  const values = row.values?.filter(Boolean) ?? []
  if (!values.length) return row.title
  return `${row.title} eşleşmemiş: ${values.slice(0, 3).join(', ')}${values.length > 3 ? ` +${values.length - 3}` : ''}`
}
