// PostToolUse hook: after Claude edits a C# file, fix its whitespace the way Validate's `dotnet format` expects.
// Folder mode needs no MSBuild load (about a second). Never blocks: a failure only leaves the file as it was.
import { execFileSync } from 'node:child_process'
import { existsSync, readFileSync } from 'node:fs'
import path from 'node:path'

let input = {}
try { input = JSON.parse(readFileSync(0, 'utf8') || '{}') } catch { process.exit(0) }
const file = input?.tool_input?.file_path
if (typeof file !== 'string' || !file.toLowerCase().endsWith('.cs') || !existsSync(file)) process.exit(0)

// The checkout (or worktree) the file belongs to: the nearest folder with the solution.
let root = path.dirname(path.resolve(file))
while (!existsSync(path.join(root, 'MarketplaceHub.sln'))) {
  const parent = path.dirname(root)
  if (parent === root) process.exit(0)
  root = parent
}

// `--include` takes forward slashes; a Windows path with backslashes matches nothing.
const include = path.relative(root, path.resolve(file)).split(path.sep).join('/')
try {
  execFileSync('dotnet', ['format', 'whitespace', '--folder', '--include', include], { cwd: root, stdio: 'ignore', timeout: 60_000 })
} catch { /* leave the file as it was */ }
process.exit(0)
