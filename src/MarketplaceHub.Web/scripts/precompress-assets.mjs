// Writes a brotli (.br) and a zstd (.zst) copy next to every script, stylesheet and other text file in dist/assets,
// at the highest levels. Caddy sends them as they are (`file_server { precompressed br zstd }` in
// deploy/caddy/Caddyfile.production) instead of compressing each response at a fast level. A copy that is not
// smaller than its file is not written; the file is then compressed on the fly as before.
import { readdirSync, readFileSync, writeFileSync } from 'node:fs'
import { extname, join } from 'node:path'
import { brotliCompressSync, constants, zstdCompressSync } from 'node:zlib'

const assetsDirectory = join(process.cwd(), 'dist', 'assets')
const textExtensions = new Set(['.js', '.mjs', '.css', '.svg', '.json', '.html', '.txt', '.xml', '.webmanifest', '.wasm'])
const minimumBytes = 1024

let files = 0
let originalBytes = 0
let brotliBytes = 0
let zstdBytes = 0
for (const name of readdirSync(assetsDirectory)) {
  if (!textExtensions.has(extname(name))) continue
  const path = join(assetsDirectory, name)
  const source = readFileSync(path)
  if (source.length < minimumBytes) continue
  const brotli = brotliCompressSync(source, {
    params: {
      [constants.BROTLI_PARAM_MODE]: extname(name) === '.wasm' ? constants.BROTLI_MODE_GENERIC : constants.BROTLI_MODE_TEXT,
      [constants.BROTLI_PARAM_QUALITY]: constants.BROTLI_MAX_QUALITY,
      [constants.BROTLI_PARAM_SIZE_HINT]: source.length
    }
  })
  const zstd = zstdCompressSync(source, {
    params: {
      [constants.ZSTD_c_compressionLevel]: 19,
      [constants.ZSTD_c_checksumFlag]: 1
    },
    pledgedSrcSize: source.length
  })
  files += 1
  originalBytes += source.length
  if (brotli.length < source.length) {
    writeFileSync(`${path}.br`, brotli)
    brotliBytes += brotli.length
  } else brotliBytes += source.length
  if (zstd.length < source.length) {
    writeFileSync(`${path}.zst`, zstd)
    zstdBytes += zstd.length
  } else zstdBytes += source.length
}

const kib = bytes => `${(bytes / 1024).toFixed(1)} KiB`
console.log(`Precompressed ${files} assets: ${kib(originalBytes)} -> brotli ${kib(brotliBytes)}, zstd ${kib(zstdBytes)}`)
