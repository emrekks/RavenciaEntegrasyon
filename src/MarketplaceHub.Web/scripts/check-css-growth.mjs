// WEB-ALTYAPI-13: a growth barrier for the CSS, not a rewrite. Each CSS file under src may hold no more `!important`
// declarations and no more repeated-`:root` specificity bumps (`:root:root …`, written to outweigh an earlier override;
// each extra `:root` of a chain counts once)
// than css-growth-baseline.json records for it. A fix that lowers a count lowers the baseline with it
// (`node scripts/check-css-growth.mjs --write-baseline`); the numbers only go down. A new file starts at zero.
//
//   node scripts/check-css-growth.mjs                  check (npm run check:css, run in CI after lint)
//   node scripts/check-css-growth.mjs --write-baseline record the current counts (only after lowering them)
import { readdirSync, readFileSync, writeFileSync } from 'node:fs'
import { join, relative } from 'node:path'

const root = process.cwd()
const sourceDirectory = join(root, 'src')
const baselineFile = join(root, 'scripts', 'css-growth-baseline.json')

function cssFiles(directory) {
  return readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const full = join(directory, entry.name)
    if (entry.isDirectory()) return cssFiles(full)
    return entry.name.endsWith('.css') ? [full] : []
  })
}

/** The counts of one stylesheet, comments left out. */
export function countCss(text) {
  const code = text.replace(/\/\*[\s\S]*?\*\//g, '')
  return {
    important: (code.match(/!\s*important\b/gi) ?? []).length,
    // Every :root beyond the first in a chain is one specificity bump over an earlier rule (`:root:root:root` = 2), so
    // both a new chain and a longer one count.
    rootBumps: (code.match(/:root(?::root)+/g) ?? []).reduce((sum, chain) => sum + chain.split(':root').length - 2, 0),
  }
}

const current = Object.fromEntries(cssFiles(sourceDirectory)
  .map(file => [relative(root, file).replace(/\\/g, '/'), countCss(readFileSync(file, 'utf8'))])
  .sort(([a], [b]) => a.localeCompare(b)))

if (process.argv.includes('--write-baseline')) {
  const recorded = Object.fromEntries(Object.entries(current).filter(([, counts]) => counts.important || counts.rootBumps))
  writeFileSync(baselineFile, `${JSON.stringify(recorded, null, 2)}\n`)
  console.log(`CSS tabanı yazıldı: ${Object.keys(recorded).length} dosya.`)
} else {
  const baseline = JSON.parse(readFileSync(baselineFile, 'utf8'))
  const failures = []
  const lowered = []
  const totals = { important: 0, rootBumps: 0 }
  for (const [file, counts] of Object.entries(current)) {
    const allowed = baseline[file] ?? { important: 0, rootBumps: 0 }
    totals.important += counts.important
    totals.rootBumps += counts.rootBumps
    for (const [measure, label] of [['important', '!important'], ['rootBumps', ':root tekrarı']]) {
      if (counts[measure] > allowed[measure]) failures.push(`${file}: ${label} ${counts[measure]} > taban ${allowed[measure]}`)
      else if (counts[measure] < allowed[measure]) lowered.push(`${file}: ${label} ${counts[measure]} < taban ${allowed[measure]}`)
    }
  }
  console.log(`CSS büyüme kontrolü: ${Object.keys(current).length} dosya, ${totals.important} !important, ${totals.rootBumps} :root tekrarı.`)
  if (lowered.length) console.log(`Taban düşürülebilir (node scripts/check-css-growth.mjs --write-baseline):\n${lowered.join('\n')}`)
  if (failures.length) {
    console.error(`CSS özgüllüğü büyüdü. Yeni kuralı !important ya da ek :root olmadan, ilgili özellik CSS'ine yazın:\n${failures.join('\n')}`)
    process.exitCode = 1
  }
}
