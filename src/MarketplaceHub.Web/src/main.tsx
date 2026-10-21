import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { App } from './app/App'
import { ErrorBoundary } from './shared/components/ErrorBoundary'
import './app.css'
import responsiveStylesheet from './styles/responsive.css?url'
import phoneSpacingStylesheet from './styles/phone-spacing.css?url'
import { installScrollbarVisibility } from './shared/scrollbar-visibility'
import { reloadForNewBuild } from './shared/lazy-page'
import { ApiRequestError } from './shared/api'
import { installSheetSwipeClose } from './shared/sheet-swipe'
import { persistQuerySnapshot, restoreQuerySnapshot } from './shared/query-snapshot'

// Phone and tablet rules for the main-bundle screens live in their own file, linked for small
// screens only, so desktop never applies them and the main CSS budget stays as it is.
const responsiveLink = document.createElement('link')
responsiveLink.rel = 'stylesheet'
responsiveLink.href = responsiveStylesheet
responsiveLink.media = '(max-width: 1100px)'
document.head.appendChild(responsiveLink)
// The phone spacing scale (gutter, gaps, block padding) for every page and window, phones only.
const phoneSpacingLink = document.createElement('link')
phoneSpacingLink.rel = 'stylesheet'
phoneSpacingLink.href = phoneSpacingStylesheet
phoneSpacingLink.media = '(max-width: 760px)'
document.head.appendChild(phoneSpacingLink)

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
createRoot(document.getElementById('root')!).render(<StrictMode><QueryClientProvider client={queryClient}><BrowserRouter><ErrorBoundary title="Panel açılamadı"><App /></ErrorBoundary></BrowserRouter></QueryClientProvider></StrictMode>)
