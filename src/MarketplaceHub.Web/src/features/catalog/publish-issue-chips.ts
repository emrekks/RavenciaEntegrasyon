// One gap of a publish card: pressing it opens its fix (`onRun`: the category fields window, a category mapping, a new
// check) or goes to the page that fixes it (`href`); a gap with neither is only information.
export type PublishIssueRow = { key: string; title: string; detail: string; href?: string; onRun?: () => void }

// The badge's popover shows one chip per title: gaps with the same title (two values of one field) share it, their details
// joined for the hover and the screen reader; a title already among the required gaps is left out of the optional ones.
export function publishIssueChips(rows: readonly PublishIssueRow[], optionalRows: readonly PublishIssueRow[]) {
  const group = (items: readonly PublishIssueRow[], skip: ReadonlySet<string>) => {
    const chips = new Map<string, PublishIssueRow>()
    for (const row of items) {
      if (skip.has(row.title)) continue
      const chip = chips.get(row.title)
      if (!chip) { chips.set(row.title, { ...row }); continue }
      if (row.detail && !chip.detail.split(' · ').includes(row.detail)) chip.detail = chip.detail ? `${chip.detail} · ${row.detail}` : row.detail
      if (!chip.onRun && !chip.href) { chip.onRun = row.onRun; chip.href = row.href }
    }
    return [...chips.values()]
  }
  const required = group(rows, new Set())
  return { required, optional: group(optionalRows, new Set(required.map(row => row.title))) }
}
