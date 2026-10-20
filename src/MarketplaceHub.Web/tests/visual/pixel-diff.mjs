// Pixel comparison of two screenshots in headless Chromium (no image library needed): number of differing pixels and
// the box that holds them. Used by diff.mjs when two screenshots are not byte-identical.
import { readFile } from 'node:fs/promises'
import { chromium } from '@playwright/test'

let browserPromise = null
export async function pixelDiff(beforePath, afterPath, executablePath) {
  browserPromise ??= chromium.launch({ executablePath })
  const browser = await browserPromise
  const page = await browser.newPage()
  try {
    const [before, after] = await Promise.all([readFile(beforePath), readFile(afterPath)])
    return await page.evaluate(async ([a, b]) => {
      const load = async source => { const image = new Image(); image.src = `data:image/png;base64,${source}`; await image.decode(); return image }
      const [first, second] = await Promise.all([load(a), load(b)])
      if (first.width !== second.width || first.height !== second.height) return { sizeChanged: `${first.width}x${first.height} -> ${second.width}x${second.height}` }
      const read = image => { const canvas = new OffscreenCanvas(image.width, image.height); const context = canvas.getContext('2d'); context.drawImage(image, 0, 0); return context.getImageData(0, 0, image.width, image.height).data }
      const x = read(first)
      const y = read(second)
      let count = 0
      let box = null
      for (let index = 0; index < x.length; index += 4) {
        if (x[index] === y[index] && x[index + 1] === y[index + 1] && x[index + 2] === y[index + 2] && x[index + 3] === y[index + 3]) continue
        count += 1
        const pixel = index / 4
        const left = pixel % first.width
        const top = Math.floor(pixel / first.width)
        box = box ? [Math.min(box[0], left), Math.min(box[1], top), Math.max(box[2], left), Math.max(box[3], top)] : [left, top, left, top]
      }
      return { count, box }
    }, [before.toString('base64'), after.toString('base64')])
  } finally {
    await page.close()
  }
}

export async function closePixelDiff() {
  if (browserPromise) await (await browserPromise).close()
  browserPromise = null
}
