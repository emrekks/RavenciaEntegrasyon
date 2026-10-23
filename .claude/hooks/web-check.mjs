// PostToolUse hook: after Claude edits a TypeScript file of the web panel, lint that file and run the Vitest tests
// that import it, so a type-aware lint error or a broken test shows up at once instead of at the end. A failure is
// reported back to Claude (exit 2 with a short summary); the edit itself is never undone. Other files are skipped.
import { execFileSync } from 'node:child_process'
import { existsSync, readFileSync } from 'node:fs'
import path from 'node:path'

let input = {}
try { input = JSON.parse(readFileSync(0, 'utf8') || '{}') } catch { process.exit(0) }
const file = input?.tool_input?.file_path
if (typeof file !== 'string' || !/\.(ts|tsx)$/i.test(file) || !existsSync(file)) process.exit(0)

// The web project the file belongs to: the nearest folder with the Vite config, inside src/MarketplaceHub.Web.
let web = path.dirname(path.resolve(file))
while (!existsSync(path.join(web, 'vite.config.ts'))) {
  const parent = path.dirname(web)
  if (parent === web) process.exit(0)
  web = parent
}
if (path.basename(web) !== 'MarketplaceHub.Web') process.exit(0)
const relative = path.relative(web, path.resolve(file)).split(path.sep).join('/')
if (!relative.startsWith('src/')) process.exit(0)

// The tools are run with node directly: on Windows `npx` is a .cmd file that execFile cannot start.
const tool = (...parts) => path.join(web, 'node_modules', ...parts)
const run = (args, timeout) => {
  try { execFileSync(process.execPath, args, { cwd: web, stdio: 'pipe', timeout, encoding: 'utf8' }); return null }
  catch (error) { return `${error.stdout ?? ''}${error.stderr ?? ''}`.trim() || String(error.message) }
}

const problems = []
const lint = run([tool('eslint', 'bin', 'eslint.js'), relative, '--max-warnings=0'], 90_000)
if (lint) problems.push(`eslint ${relative}:\n${lint.split('\n').slice(0, 25).join('\n')}`)
const tests = run([tool('vitest', 'vitest.mjs'), 'related', relative, '--run', '--reporter=dot', '--passWithNoTests'], 150_000)
if (tests) problems.push(`vitest related ${relative}:\n${tests.split('\n').filter(line => /FAIL|✗|×|Error|expected|Expected|Received|Tests /.test(line)).slice(0, 30).join('\n')}`)

if (problems.length) {
  process.stderr.write(`web-check found problems after editing ${relative}:\n\n${problems.join('\n\n')}\n`)
  process.exit(2)
}
process.exit(0)
