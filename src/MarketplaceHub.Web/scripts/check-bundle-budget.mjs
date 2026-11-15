import { readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'

// The bundle budget, run after `npm run build` (npm run check:bundle, CI "Check frontend bundle budget").
//   1. The main chunks (what every first visit loads) stay under fixed limits.
//   2. WEB-ALTYAPI-14: every other chunk (a page, a dialog, shared code) may not grow more than its headroom over the
//      size bundle-budget-baseline.json records for it, and all JavaScript together neither. A new chunk may not be
//      larger than the biggest page chunk allowed. A change that has to grow a chunk records the new sizes with
//      `node scripts/check-bundle-budget.mjs --write-baseline` after a build, so the growth is seen in review.
const assetsDirectory = join(process.cwd(), 'dist', 'assets')
const baselineFile = join(process.cwd(), 'scripts', 'bundle-budget-baseline.json')
const budgets = [
  { pattern: /^index-[^/]+\.js$/, label: 'ana JavaScript', maximumBytes: 450 * 1024 },
  { pattern: /^index-[^/]+\.css$/, label: 'ana CSS', maximumBytes: 672 * 1024 }
]
// A chunk may grow by 10 % of its recorded size, and by 4 KiB at least (a small chunk would fail on any edit otherwise).
const headroom = bytes => Math.max(Math.ceil(bytes * 0.1), 4 * 1024)
// No chunk outside the main ones and the vendor libraries may be larger than this, a new one included.
const largestChunkMaximumBytes = 350 * 1024

const kib = bytes => `${(bytes / 1024).toFixed(1)} KiB`
const assets = readdirSync(assetsDirectory).map(name => ({ name, bytes: statSync(join(assetsDirectory, name)).size }))
const failures = []
for (const budget of budgets) {
  const asset = assets.find(candidate => budget.pattern.test(candidate.name))
  if (!asset) {
    failures.push(`${budget.label} çıktısı bulunamadı.`)
    continue
  }
  const size = kib(asset.bytes)
  const limit = `${(budget.maximumBytes / 1024).toFixed(0)} KiB`
  console.log(`${budget.label}: ${asset.name} (${size} / ${limit})`)
  if (asset.bytes > budget.maximumBytes) failures.push(`${budget.label} bütçeyi aşıyor: ${size} > ${limit}.`)
}

// Chunks by name without the content hash ("ProductEditorPages.js"); a few names repeat (shared.js), so each name holds
// its chunks' sizes, largest first, and is compared size by size.
const code = assets.filter(asset => /\.(js|css)$/.test(asset.name))
const stem = name => name.replace(/-[A-Za-z0-9_-]{8}(\.(?:js|css))$/, '$1')
const isPageChunk = name => !/^(index|vendor-[^-]+)-/.test(name)
const chunks = {}
for (const asset of code.filter(asset => isPageChunk(asset.name))) (chunks[stem(asset.name)] ??= []).push(asset.bytes)
for (const sizes of Object.values(chunks)) sizes.sort((a, b) => b - a)
const totalJs = code.filter(asset => asset.name.endsWith('.js')).reduce((sum, asset) => sum + asset.bytes, 0)

if (process.argv.includes('--write-baseline')) {
  const sorted = Object.fromEntries(Object.entries(chunks).sort(([a], [b]) => a.localeCompare(b)))
  writeFileSync(baselineFile, `${JSON.stringify({ totalJs, chunks: sorted }, null, 2)}\n`)
  console.log(`Parça tabanı yazıldı: ${Object.keys(sorted).length} ad, toplam JavaScript ${kib(totalJs)}.`)
} else {
  const baseline = JSON.parse(readFileSync(baselineFile, 'utf8'))
  for (const [name, sizes] of Object.entries(chunks)) {
    const recorded = baseline.chunks[name] ?? []
    sizes.forEach((bytes, index) => {
      const base = recorded[index]
      const limit = Math.min(base === undefined ? largestChunkMaximumBytes : base + headroom(base), largestChunkMaximumBytes)
      if (bytes > limit) failures.push(`${name} bütçeyi aşıyor: ${kib(bytes)} > ${kib(limit)}${base === undefined ? ' (yeni parça)' : ` (taban ${kib(base)})`}.`)
    })
  }
  const totalLimit = baseline.totalJs + Math.ceil(baseline.totalJs * 0.1)
  console.log(`toplam JavaScript: ${kib(totalJs)} / ${kib(totalLimit)}`)
  if (totalJs > totalLimit) failures.push(`Toplam JavaScript bütçeyi aşıyor: ${kib(totalJs)} > ${kib(totalLimit)}.`)
  const largest = Object.entries(chunks).flatMap(([name, sizes]) => sizes.map(bytes => ({ name, bytes }))).sort((a, b) => b.bytes - a.bytes).slice(0, 5)
  console.log(`en büyük sayfa parçaları: ${largest.map(chunk => `${chunk.name} ${kib(chunk.bytes)}`).join(', ')}`)
}

if (failures.length) {
  console.error(failures.join('\n'))
  process.exitCode = 1
}
