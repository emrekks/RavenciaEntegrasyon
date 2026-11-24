import { chromium } from '@playwright/test'
import { chromiumExecutable } from './browser.mjs'
import { startStaticServer } from './server.mjs'
import { installApiMock } from './api-mock.mjs'
import { frozenNow } from './scenarios.mjs'
import { resolve } from 'node:path'
const out = 'C:/Users/emrek/OneDrive/Belgeler/V3-Ravencia-Entegrasyon/.local-docs/ekran'
const browser = await chromium.launch({ executablePath: await chromiumExecutable() })
const server = await startStaticServer(resolve(import.meta.dirname, '../../dist'))
for (const w of [769, 820, 1024]) {
  const ctx = await browser.newContext({ viewport: { width: w, height: 1000 }, deviceScaleFactor: 1, locale: 'tr-TR' })
  await ctx.route(u => !u.href.startsWith(server.origin), r => r.abort())
  const page = await ctx.newPage(); await page.clock.setFixedTime(frozenNow)
  await installApiMock(page, { signedIn: true, unhandled: new Set() })
  await page.goto(server.origin + '/products/p1'); await page.waitForSelector('.app-shell main'); await page.waitForTimeout(2000)
  const m = await page.evaluate(() => { const vis = e => e && e.offsetParent !== null && getComputedStyle(e).visibility !== 'hidden'; const bar = document.querySelector('.rv-action-bar.product-submit-sticky'); const r = bar?.getBoundingClientRect(); return { dock: vis(document.querySelector('.rv-dock-panel')), topBack: vis(document.querySelector('.rv-topbar-back')), pageBack: vis(document.querySelector('a.product-heading-back')), hamburger: vis(document.querySelector('.rv-mobile-menu-toggle')), sidebar: vis(document.querySelector('aside')), bar: r ? [Math.round(r.width), Math.round(r.height)] : null } })
  console.log(w, JSON.stringify(m))
  await page.screenshot({ path: `${out}/dock16-tablet-${w}.png` }); await ctx.close()
}
await browser.close(); await server.close()
