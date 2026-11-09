import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { createBrowserRouter, RouterProvider } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { App } from './app/App'
import { ErrorBoundary } from './shared/components/ErrorBoundary'
import './app.css'
import responsiveStylesheet from './styles/responsive.css?url'
import phoneSpacingStylesheet from './styles/phone-spacing.css?url'
import tabletOpsStylesheet from './styles/tablet-ops.css?url'
import tabletCatalogStylesheet from './styles/tablet-catalog.css?url'
import { installScrollbarVisibility } from './shared/scrollbar-visibility'
import { reloadForNewBuild } from './shared/lazy-page'
import { ApiRequestError } from './shared/api'
import { installSheetSwipeClose } from './shared/sheet-swipe'
import { persistQuerySnapshot, restoreQuerySnapshot } from './shared/query-snapshot'

// Phone and tablet rules for the main-bundle screens live in their own files, linked for small screens only, so desktop
// never applies them and the main CSS budget stays as it is. The build links them from index.html (vite.config.ts,
// link-screen-stylesheets) so a phone has them before the first paint; the dev server has no such links and gets them
// here. Media queries (the same in vite.config.ts): responsive, phones and tablets; the phone spacing scale (gutter,
// gaps, block padding), phones only; the shell and operations tablet layout and the catalog pages' tablet layout
// (Ürünler, product editor, Eşleştirmeler, Entegrasyonlar, Ayarlar), 761-1024 px only.
if (!document.querySelector('link[data-screen-stylesheet]')) {
  for (const [href, media] of [
    [responsiveStylesheet, '(max-width: 1024px)'],
    [phoneSpacingStylesheet, '(max-width: 760px)'],
    [tabletOpsStylesheet, '(min-width: 761px) and (max-width: 1024px)'],
    [tabletCatalogStylesheet, '(min-width: 761px) and (max-width: 1024px)'],
  ]) {
    const link = document.createElement('link')
    link.rel = 'stylesheet'
    link.href = href
    link.media = media
    document.head.appendChild(link)
  }
}

// A preload of the next screen's code failed (an older build is running after a deploy): load the new build.
window.addEventListener('vite:preloadError', event => { if (reloadForNewBuild()) event.preventDefault() })

installScrollbarVisibility()
installSheetSwipeClose()

// One quick repeat for server and network failures; a client error (4xx) would answer the same way again.
const retryOnce = (failureCount: number, error: unknown) => failureCount < 1 && !(error instanceof ApiRequestError && error.status < 500)
const queryClient = new QueryClient({ defaultOptions: { queries: { retry: retryOnce, staleTime: 60_000, refetchOnWindowFocus: false } } })
// After F5 the last view shows at once from this tab's snapshot while everything on it is asked for again.
restoreQuerySnapshot(queryClient)
persistQuerySnapshot(queryClient)
// A data router (one catch-all route; App keeps its own <Routes>) so screens can hold back in-app navigation while
// they have unsaved edits (useBlocker).
const router = createBrowserRouter([{ path: '*', element: <ErrorBoundary title="Panel Açılamadı"><App /></ErrorBoundary> }])
createRoot(document.getElementById('root')!).render(<StrictMode><QueryClientProvider client={queryClient}><RouterProvider router={router} /></QueryClientProvider></StrictMode>)
