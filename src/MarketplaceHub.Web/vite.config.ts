import { defineConfig, loadEnv, type Plugin } from 'vite'
import react from '@vitejs/plugin-react'
import type { AtRule, Plugin as PostcssPlugin } from 'postcss'

// Touch screens keep :hover on the last element touched, so a tapped card or row stayed lit after the finger lifted.
// Every :hover selector is moved into @media (hover: hover) at build time: pointers that can hover keep every hover
// effect as written, touch screens never get one (taps, focus and the open states still work). A rule that mixes
// :hover with other selectors keeps the others where they were. Rules already under (hover: hover) and keyframes stay.
const hoverOnlyOnPointers: PostcssPlugin = {
  postcssPlugin: 'hover-only-on-pointers',
  Once(root, helpers) {
    root.walkRules(rule => {
      if (!rule.selector.includes(':hover')) return
      for (let parent = rule.parent; parent && parent.type !== 'root'; parent = parent.parent) {
        if (parent.type !== 'atrule') continue
        const at = parent as AtRule
        if (at.name.endsWith('keyframes') || (at.name === 'media' && /\(\s*hover\s*:\s*hover\s*\)/.test(at.params))) return
      }
      const hover = rule.selectors.filter(selector => selector.includes(':hover'))
      const rest = rule.selectors.filter(selector => !selector.includes(':hover'))
      const media = new helpers.AtRule({ name: 'media', params: '(hover: hover)' })
      media.append(rule.clone({ selectors: hover }))
      rule.after(media)
      if (rest.length) rule.selectors = rest
      else rule.remove()
    })
  },
}

// The phone and tablet stylesheets (imported with ?url in main.tsx) are linked from index.html itself at build time, so
// a phone has them before the first paint instead of after the script ran (a layout shift). Each keeps its media query,
// so a desktop never applies them; they come after the main CSS, as when main.tsx appended them. The dev server has no
// bundle: main.tsx appends them there, and skips that when the links are already in the page.
const screenStylesheets = [
  { source: 'src/styles/responsive.css', media: '(max-width: 1024px)' },
  { source: 'src/styles/phone-spacing.css', media: '(max-width: 760px)' },
  { source: 'src/styles/tablet-ops.css', media: '(min-width: 761px) and (max-width: 1024px)' },
  { source: 'src/styles/tablet-catalog.css', media: '(min-width: 761px) and (max-width: 1024px)' },
] as const

const linkScreenStylesheets: Plugin = {
  name: 'link-screen-stylesheets',
  transformIndexHtml: {
    order: 'post',
    handler(_html, context) {
      if (!context.bundle) return []
      const assets = Object.values(context.bundle).filter(item => item.type === 'asset')
      return screenStylesheets.map(({ source, media }) => {
        const asset = assets.find(item => item.originalFileNames?.some(name => name.replace(/\\/g, '/').endsWith(source)))
        if (!asset) throw new Error(`link-screen-stylesheets: ${source} was not emitted`)
        return { tag: 'link', attrs: { rel: 'stylesheet', href: `/${asset.fileName}`, media, 'data-screen-stylesheet': '' }, injectTo: 'head' as const }
      })
    },
  },
}

export default defineConfig(({ mode }) => {
  const apiProxy = loadEnv(mode, '.', '').VITE_API_PROXY ?? 'http://localhost:5192'

  return {
    plugins: [react(), linkScreenStylesheets],
    css: { postcss: { plugins: [hoverOnlyOnPointers] } },
    build: {
      // No source maps in the shipped bundle (Vite's default, kept explicit): the live server serves only the built code.
      sourcemap: false,
      rollupOptions: {
        output: {
          // Keep the application entry focused on the shell while allowing
          // browser-cached dependencies to be downloaded as stable vendor
          // chunks. The bundle budget intentionally measures the entry chunk.
          manualChunks(id) {
            if (id.includes('/node_modules/react/') || id.includes('/node_modules/react-dom/') || id.includes('/node_modules/react-router/')) return 'vendor-react'
            if (id.includes('/node_modules/@tanstack/react-query/')) return 'vendor-query'
            // SignalR is read after the first render (useOperationsRealtime), DOMPurify only by the product editor's
            // rich text: each in its own chunk, neither on the first load.
            if (id.includes('/node_modules/@microsoft/signalr/')) return 'vendor-integration'
            if (id.includes('/node_modules/dompurify/')) return 'vendor-sanitize'
            return undefined
          },
        },
      },
    },
    server: { host: '127.0.0.1', port: 5173, proxy: { '/api': apiProxy, '/health': apiProxy, '/hubs': { target: apiProxy, ws: true } } },
  }
})
