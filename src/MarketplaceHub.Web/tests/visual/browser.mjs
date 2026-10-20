import { existsSync } from 'node:fs'
import { readdir } from 'node:fs/promises'
import { homedir } from 'node:os'
import { join } from 'node:path'
import { chromium } from '@playwright/test'

// Playwright's own browser when it is installed; otherwise the newest Chromium found in the Playwright cache, or the
// one named by PLAYWRIGHT_CHROMIUM_EXECUTABLE (the build must stay the same between two captures that are compared).
export async function chromiumExecutable() {
  if (process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE) return process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE
  if (existsSync(chromium.executablePath())) return undefined
  const cache = process.env.PLAYWRIGHT_BROWSERS_PATH ?? (process.platform === 'win32' ? join(process.env.LOCALAPPDATA ?? homedir(), 'ms-playwright') : process.platform === 'darwin' ? join(homedir(), 'Library', 'Caches', 'ms-playwright') : join(homedir(), '.cache', 'ms-playwright'))
  const builds = (await readdir(cache).catch(() => [])).filter(name => /^chromium-\d+$/.test(name)).sort((a, b) => Number(b.split('-')[1]) - Number(a.split('-')[1]))
  for (const build of builds) {
    for (const candidate of [join(cache, build, 'chrome-win64', 'chrome.exe'), join(cache, build, 'chrome-win', 'chrome.exe'), join(cache, build, 'chrome-linux', 'chrome'), join(cache, build, 'chrome-linux64', 'chrome'), join(cache, build, 'chrome-mac', 'Chromium.app', 'Contents', 'MacOS', 'Chromium')]) {
      if (existsSync(candidate)) return candidate
    }
  }
  throw new Error('No Chromium found: run `npx playwright install chromium` or set PLAYWRIGHT_CHROMIUM_EXECUTABLE.')
}
