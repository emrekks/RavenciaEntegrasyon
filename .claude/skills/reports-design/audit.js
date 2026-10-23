// Layout audit of the Raporlar page. Run it in the preview page (http://127.0.0.1:4319/reports) with the browser's
// javascript tool; it returns at once and fills window.__reportsAudit = { done, findings } in about two minutes.
// For each tab and width it loads the page in a hidden frame and lists:
//   HEIGHT  cards (panels) side by side in one row whose heights differ by more than 2px;
//   GAP     a vertical gap between rows of a grid other than 20px;
//   SCROLL  a page wider than the window (horizontal scroll);
//   EMPTY   a tab that rendered no report body.
// An empty findings list means the layout follows the spacing rules.
window.__reportsAudit = { done: false, findings: [] };
(async () => {
  const views = ['', '?view=geo', '?view=products', '?view=products&sub=brands', '?view=profit', '?view=ops&sub=returns', '?view=ops&sub=cancellations',
    '?view=ops&sub=cargo', '?view=ops&sub=invoices', '?view=ops&sub=stock', '?view=customers', '?view=tariffs']
  const widths = [1000, 1280, 1800]
  const findings = window.__reportsAudit.findings
  const box = item => item.matches('.reports-panel, .reports-card, section') ? item : item.querySelector('.reports-panel, .reports-card, section') ?? item
  for (const width of widths) for (const view of views) {
    const frame = document.createElement('iframe')
    frame.style.cssText = `position:fixed;left:0;top:0;width:${width}px;height:900px;opacity:0;pointer-events:none`
    frame.src = `/reports${view}${view ? '&' : '?'}audit=${Math.random()}`
    document.body.append(frame)
    await new Promise(resolve => { frame.onload = resolve })
    await new Promise(resolve => setTimeout(resolve, 2500))
    const where = `${width}px ${view || '?view=overview'}`
    const doc = frame.contentDocument
    const body = doc.querySelector('.reports-body')
    if (!body) { findings.push(`EMPTY ${where}`); frame.remove(); continue }
    if (doc.documentElement.scrollWidth > width + 1) findings.push(`SCROLL ${where}: ${doc.documentElement.scrollWidth}px wide`)
    for (const grid of body.querySelectorAll('.reports-grid, .reports-lists, .reports-summary, .reports-overview, .reports-overview-row')) {
      const items = [...grid.children].filter(item => item.offsetHeight > 0)
      const rows = {}
      for (const item of items) (rows[Math.round(item.getBoundingClientRect().top)] ??= []).push(item)
      for (const row of Object.values(rows)) {
        if (row.length < 2) continue
        const boxes = row.map(box)
        if (!boxes.every(item => /reports-(panel|card)/.test(item.className))) continue
        const heights = boxes.map(item => Math.round(item.getBoundingClientRect().height))
        if (Math.max(...heights) - Math.min(...heights) > 2)
          findings.push(`HEIGHT ${where} .${grid.className.split(' ').join('.')}: ${boxes.map((item, index) => `${item.querySelector('h2')?.textContent ?? item.className} ${heights[index]}px`).join(' | ')}`)
      }
      const tops = Object.keys(rows).map(Number).sort((a, b) => a - b)
      for (let index = 1; index < tops.length; index++) {
        const bottom = Math.max(...rows[tops[index - 1]].map(item => item.getBoundingClientRect().bottom))
        const gap = Math.round(tops[index] - bottom)
        if (gap !== 20) findings.push(`GAP ${where} .${grid.className.split(' ').join('.')}: ${gap}px`)
      }
    }
    frame.remove()
  }
  window.__reportsAudit.done = true
})()
'started'
