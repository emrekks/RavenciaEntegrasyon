import { defineConfig, loadEnv } from 'vite'
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

export default defineConfig(({ mode }) => {
  const apiProxy = loadEnv(mode, '.', '').VITE_API_PROXY ?? 'http://localhost:5192'

  return {
    plugins: [react()],
    css: { postcss: { plugins: [hoverOnlyOnPointers] } },
    build: {
      rollupOptions: {
        output: {
          // Keep the application entry focused on the shell while allowing
          // browser-cached dependencies to be downloaded as stable vendor
          // chunks. The bundle budget intentionally measures the entry chunk.
          manualChunks(id) {
            if (id.includes('/node_modules/react/') || id.includes('/node_modules/react-dom/') || id.includes('/node_modules/react-router/')) return 'vendor-react'
            if (id.includes('/node_modules/@tanstack/react-query/')) return 'vendor-query'
            if (id.includes('/node_modules/@microsoft/signalr/') || id.includes('/node_modules/dompurify/')) return 'vendor-integration'
            return undefined
          },
        },
      },
    },
    server: { host: '127.0.0.1', port: 5173, proxy: { '/api': apiProxy, '/health': apiProxy, '/hubs': { target: apiProxy, ws: true } } },
  }
})
