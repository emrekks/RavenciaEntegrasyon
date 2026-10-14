import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { App } from './app/App'
import './app.css'
import responsiveStylesheet from './styles/responsive.css?url'

// Phone and tablet rules for the main-bundle screens live in their own file, linked for small
// screens only, so desktop never applies them and the main CSS budget stays as it is.
const responsiveLink = document.createElement('link')
responsiveLink.rel = 'stylesheet'
responsiveLink.href = responsiveStylesheet
responsiveLink.media = '(max-width: 1100px)'
document.head.appendChild(responsiveLink)

const queryClient = new QueryClient({ defaultOptions: { queries: { retry: 1, staleTime: 60_000, refetchOnWindowFocus: false } } })
createRoot(document.getElementById('root')!).render(<StrictMode><QueryClientProvider client={queryClient}><BrowserRouter><App /></BrowserRouter></QueryClientProvider></StrictMode>)
