import { readdirSync, statSync } from 'node:fs'
import { join } from 'node:path'

const assetsDirectory = join(process.cwd(), 'dist', 'assets')
const budgets = [
  { pattern: /^index-[^/]+\.js$/, label: 'ana JavaScript', maximumBytes: 450 * 1024 },
  { pattern: /^index-[^/]+\.css$/, label: 'ana CSS', maximumBytes: 650 * 1024 }
]

const assets = readdirSync(assetsDirectory).map(name => ({ name, bytes: statSync(join(assetsDirectory, name)).size }))
const failures = []
for (const budget of budgets) {
  const asset = assets.find(candidate => budget.pattern.test(candidate.name))
  if (!asset) {
    failures.push(`${budget.label} çıktısı bulunamadı.`)
    continue
  }
  const size = `${(asset.bytes / 1024).toFixed(1)} KiB`
  const limit = `${(budget.maximumBytes / 1024).toFixed(0)} KiB`
  console.log(`${budget.label}: ${asset.name} (${size} / ${limit})`)
  if (asset.bytes > budget.maximumBytes) failures.push(`${budget.label} bütçeyi aşıyor: ${size} > ${limit}.`)
}

if (failures.length) {
  console.error(failures.join('\n'))
  process.exitCode = 1
}
