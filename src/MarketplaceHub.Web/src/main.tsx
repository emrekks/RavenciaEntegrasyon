import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { App } from './app/App'
import './app.css'
import responsiveStylesheet from './styles/responsive.css?url'
import { installScrollbarVisibility } from './shared/scrollbar-visibility'
import { installDialogScrollLock } from './shared/hooks/useScrollLock'
import { reloadForNewBuild } from './shared/lazy-page'

// Phone and tablet rules for the main-bundle screens live in their own file, linked for small
// screens only, so desktop never applies them and the main CSS budget stays as it is.
const responsiveLink = document.createElement('link')
responsiveLink.rel = 'stylesheet'
responsiveLink.href = responsiveStylesheet
responsiveLink.media = '(max-width: 1100px)'
document.head.appendChild(responsiveLink)

// A preload of the next screen's code failed (an older build is running after a deploy): load the new build.
window.addEventListener('vite:preloadError', event => { if (reloadForNewBuild()) event.preventDefault() })

installScrollbarVisibility()
installDialogScrollLock()

const queryClient = new QueryClient({ defaultOptions: { queries: { retry: 1, staleTime: 60_000, refetchOnWindowFocus: false } } })
createRoot(document.getElementById('root')!).render(<StrictMode><QueryClientProvider client={queryClient}><BrowserRouter><App /></BrowserRouter></QueryClientProvider></StrictMode>)
