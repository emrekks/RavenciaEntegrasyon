import { Suspense, useEffect, useEffectEvent, useRef, useState, type CSSProperties, type FormEvent, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { Link, Navigate, NavLink, Route, Routes, useLocation, useNavigate } from 'react-router'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { lazyPage } from '../shared/lazy-page'
import { api, ApiRequestError, hubApi, hubPoll, setUnauthorizedHandler, trackUserActivity, type Me, type TenantOption } from '../shared/api'
import { StepUpPrompt } from './StepUpPrompt'
import { Drawer, ErrorBoundary, ErrorBox, Modal, UiIcon, type UiIconName } from '../shared/components'
import { AttributesPage, AttributeMappingPage, BrandsPage, CategoriesPage, ImportDetailPage, ImportsPage, NewProductPage, ProductDetailPage, ProductsPage, IntegrationDetailPage, IntegrationsPage, MappingPage, OrdersPage, QuestionsPage, ReturnDetailPage, ReturnsPage, ShipmentDetailPage, ShipmentsPage, InvoiceDetailPage, InvoicesPage, JobsPage } from './route-components'
import { useOperationsRealtime } from './hooks/useOperationsRealtime'
import { releaseOrphanedScrollLock, useScrollLock } from '../shared/hooks/useScrollLock'
import { useMediaQuery } from '../shared/hooks/useMediaQuery'
import { appearanceColorCssVariable, appearanceColorTokenOptions, appearanceFontFamilyCss, appearanceFontScale, useAppearanceSettings } from '../features/settings/appearance-settings'
import { readNotificationHistory, subscribeNotificationHistory, type AppNotification } from '../shared/notifications'
import { orderNavigationCount, type OrderNavigationSummary } from '../shared/order-navigation-count'
import { clearPersonalStorage, setStorageScope } from '../shared/storage-scope'
import { questionWaitingSummaryPath } from './questionNavigationSummary'
import { readVisualThemePreference, setVisualThemePreference, visualThemeChangeEvent, type VisualTheme } from '../features/settings/visual-theme'
import { focusOnMount } from '../shared/focus'

const NotificationPanel = lazyPage(() => import('./NotificationPanel').then(module => ({ default: module.NotificationPanel })))
const Dashboard = lazyPage(() => import('../features/dashboard/DashboardPage').then(module => ({ default: module.DashboardPage })))
const SystemSettingsPage = lazyPage(() => import('../features/settings/SystemSettingsPage').then(module => ({ default: module.SystemSettingsPage })))

function Shell({ me }: { me: Me }) {
  const appearanceSettings = useAppearanceSettings()
  const [topbarHidden, setTopbarHidden] = useState(false)
  useEffect(() => {
    let last = window.scrollY
    let frame = 0
    const onScroll = () => {
      cancelAnimationFrame(frame)
      frame = requestAnimationFrame(() => {
        const y = Math.max(0, window.scrollY)
        const delta = y - last
        if (y < 64) setTopbarHidden(false)
        else if (delta > 6) setTopbarHidden(true)
        else if (delta < -6) setTopbarHidden(false)
        if (Math.abs(delta) > 6 || y < 64) last = y
      })
    }
    window.addEventListener('scroll', onScroll, { passive: true })
    return () => { cancelAnimationFrame(frame); window.removeEventListener('scroll', onScroll) }
  }, [])
  const location = useLocation()
  const navigate = useNavigate()
  // Every overlay holds the one counted page lock (shared/hooks/useScrollLock). After a page change, or when the browser
  // brings the tab back from its cache (iPhone browsers kept a stale lock until a reload), a lock style that no overlay
  // holds any more is cleared.
  useEffect(() => { releaseOrphanedScrollLock() }, [location.pathname, location.search])
  useEffect(() => {
    const onReturn = () => { if (document.visibilityState === 'visible') releaseOrphanedScrollLock() }
    document.addEventListener('visibilitychange', onReturn)
    window.addEventListener('pageshow', onReturn)
    return () => { document.removeEventListener('visibilitychange', onReturn); window.removeEventListener('pageshow', onReturn) }
  }, [])
  const navigationSummary = useQuery({ queryKey: ['dashboard-bootstrap'], queryFn: () => hubApi<DashboardBootstrap>('/dashboard/bootstrap'), staleTime: 30_000, refetchOnWindowFocus: true })
  const invoiceWorkspaceSummary = useQuery({ queryKey: ['invoice-workspace-summary'], queryFn: () => hubApi<{ dueSoonCount: number; uninvoicedCount?: number }>('/invoice-workspace/summary'), staleTime: 30_000, refetchOnWindowFocus: true })
  // The menu counts poll only while the tab is on screen, and say they are automatic (hubPoll) so the server does not count
  // them as the person using the panel (otherwise an open tab would never reach the idle sign-out).
  const orderSummary = useQuery({ queryKey: ['orders', 'summary', []], queryFn: () => hubPoll<OrderNavigationSummary>('/orders/summary?tabs=PENDING'), staleTime: 30_000, refetchOnMount: 'always', refetchOnWindowFocus: true, refetchInterval: 15_000 })
  const questionWaitingSummary = useQuery({ queryKey: ['questions', 'navigation-waiting'], queryFn: () => hubPoll<{ totalCount: number }>(questionWaitingSummaryPath), staleTime: 30_000, refetchInterval: 60_000, refetchOnWindowFocus: true })
  const [sidebarPinned, setSidebarPinned] = useState(() => localStorage.getItem('ravencia.sidebarPinned') !== 'false')
  const [sidebarHoverExpanded, setSidebarHoverExpanded] = useState(false)
  const [mobileMenuOpen, setMobileMenuOpen] = useState(false)
  const [quickSearchOpen, setQuickSearchOpen] = useState(false)
  const [visualTheme, setVisualTheme] = useState<VisualTheme>(readVisualThemePreference)
  const [notifications, setNotifications] = useState<AppNotification[]>(readNotificationHistory)
  const [notificationsOpen, setNotificationsOpen] = useState(false)
  const notificationRef = useRef<HTMLDivElement>(null)
  const notificationHistoryRef = useRef<HTMLDivElement>(null)
  const [notificationHistoryPosition, setNotificationHistoryPosition] = useState<CSSProperties>({})
  useEffect(() => { setMobileMenuOpen(false); setQuickSearchOpen(false) }, [location.pathname])
  useEffect(() => {
    const syncVisualTheme = (event: Event) => {
      const nextTheme = (event as CustomEvent<VisualTheme>).detail
      if (nextTheme === 'light' || nextTheme === 'dark') setVisualTheme(nextTheme)
    }
    window.addEventListener(visualThemeChangeEvent, syncVisualTheme)
    return () => window.removeEventListener(visualThemeChangeEvent, syncVisualTheme)
  }, [])
  useEffect(() => {
    const unsubscribe = subscribeNotificationHistory(() => setNotifications(readNotificationHistory()))
    const closeOnOutsidePointer = (event: PointerEvent) => {
      if (!(event.target instanceof Node)) return
      if (notificationRef.current?.contains(event.target) || notificationHistoryRef.current?.contains(event.target)) return
      setNotificationsOpen(false)
    }
    document.addEventListener('pointerdown', closeOnOutsidePointer)
    return () => {
      unsubscribe()
      document.removeEventListener('pointerdown', closeOnOutsidePointer)
    }
  }, [])
  useEffect(() => {
    if (!notificationsOpen) return
    const positionHistory = () => {
      const anchor = notificationRef.current?.getBoundingClientRect()
      if (!anchor) return
      const panelWidth = Math.min(440, window.innerWidth - 32)
      const left = Math.max(16, Math.min(anchor.right - panelWidth, window.innerWidth - panelWidth - 16))
      setNotificationHistoryPosition({ top: anchor.bottom + 10, left, width: panelWidth })
    }
    positionHistory()
    window.addEventListener('resize', positionHistory)
    window.addEventListener('scroll', positionHistory, true)
    return () => {
      window.removeEventListener('resize', positionHistory)
      window.removeEventListener('scroll', positionHistory, true)
    }
  }, [notificationsOpen])
  useEffect(() => {
    const openQuickSearch = (event: KeyboardEvent) => {
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') { event.preventDefault(); setQuickSearchOpen(true) }
    }
    window.addEventListener('keydown', openQuickSearch)
    return () => window.removeEventListener('keydown', openQuickSearch)
  }, [])
  const appearanceColorsKey = JSON.stringify(appearanceSettings.settings.colors)
  // A failed sign-out request (network, expired session) still leaves the workspace.
  async function logout() { try { await api('/logout', { method: 'POST' }) } catch { /* the redirect below leaves anyway */ } finally { clearPersonalStorage(); window.location.replace(`/?signedOut=${Date.now()}`) } }
  // Hover expansion is an overlay state: the layout column stays collapsed so
  // opening and closing the menu does not move the page underneath it.
  const menuCollapsed = !sidebarPinned
  const sidebarHovering = !sidebarPinned && sidebarHoverExpanded
  const applyAppearance = useEffectEvent(() => {
    document.documentElement.style.setProperty('--rv-font-ui', appearanceFontFamilyCss[appearanceSettings.settings.fontFamily])
    document.documentElement.style.setProperty('--rv-font-scale', String(appearanceFontScale[appearanceSettings.settings.fontSize]))
    const applyTheme = () => {
      document.documentElement.dataset.theme = visualTheme
      document.documentElement.dataset.themeMode = visualTheme
      const palette = appearanceSettings.settings.colors[visualTheme]
      appearanceColorTokenOptions.forEach(({ key }) => document.documentElement.style.setProperty(appearanceColorCssVariable(key), palette[key]))
    }
    applyTheme()
  })
  useEffect(() => applyAppearance(), [appearanceSettings.settings.fontFamily, appearanceSettings.settings.fontSize, appearanceSettings.settings.themeMode, appearanceColorsKey, visualTheme])
  const pageNames: Record<string, string> = { '/dashboard': 'Genel bakış', '/products': 'Ürünler', '/products/new': 'Yeni ürün', '/catalog/categories': 'Kategoriler', '/catalog/brands': 'Markalar', '/catalog/attributes': 'Özellikler', '/imports': 'İçe aktarımlar', '/shipments': 'Gönderiler', '/orders': 'Siparişler', '/returns': 'İadeler', '/invoices': 'Faturalar', '/questions': 'Ürün soruları', '/jobs': 'İşlem Takibi', '/integrations': 'Platformlar', '/mappings/categories': 'Eşleştirme ayarları', '/mappings/attributes': 'Özellik eşlemeleri', '/settings': 'Sistem ayarları', '/settings/appearance': 'Görünüm ayarları' }
  const pageName = pageNames[location.pathname] ?? (location.pathname.startsWith('/products/') ? 'Ürün detayları' : location.pathname.startsWith('/returns/') ? 'İade detayları' : location.pathname.startsWith('/imports/') ? 'İçe aktarma ayrıntıları' : location.pathname.startsWith('/integrations/') ? 'Platform ayrıntıları' : location.pathname.startsWith('/shipments/') ? 'Gönderi ayrıntıları' : 'Ravencia')
  const displayName = me.displayName || me.email
  const initials = displayName.trim().split(/\s+/).map(part => part[0]).join('').slice(0, 2).toLocaleUpperCase('tr-TR') || 'RA'
  const unreadNotificationCount = notifications.filter(notification => !notification.read).length
  function toggleSidebarPinned() {
    const nextPinned = !sidebarPinned
    setSidebarPinned(nextPinned)
    setSidebarHoverExpanded(false)
    localStorage.setItem('ravencia.sidebarPinned', String(nextPinned))
  }
  function handleSidebarMouseEnter() { if (!sidebarPinned) setSidebarHoverExpanded(true) }
  function handleSidebarMouseLeave() { if (!sidebarPinned) setSidebarHoverExpanded(false) }
  const icon = (name: UiIconName) => <span className="nav-icon-slot" aria-hidden="true"><UiIcon className="nav-icon" name={name} size={22} /></span>
  const navigationCounts = navigationSummary.data?.metrics
  const item = (to: string, iconName: UiIconName, label: string, end = false, count?: number, showZeroCount = false, countNoun = 'bildirim', warnWhenPositive = false) => {
    const hasCount = typeof count === 'number' && (count > 0 || showZeroCount)
    const accessibleCount = typeof count === 'number' && count > 999 ? '999 üzeri' : count?.toLocaleString('tr-TR')
    const visibleCount = typeof count === 'number' && count > 999 ? '999' : count?.toLocaleString('tr-TR')
    const accessibleLabel = hasCount ? `${label}, ${accessibleCount} ${countNoun}` : label
    return <NavLink to={to} end={end} aria-label={accessibleLabel}>{icon(iconName)}<span className="nav-label">{label}</span>{hasCount && <span className={`nav-count${warnWhenPositive && count! > 0 ? ' is-warn' : ''}`} aria-hidden="true" title={`${accessibleCount} ${countNoun}`}>{visibleCount}</span>}</NavLink>
  }
  const navigationGroups: Array<{ label: string; items: ReactNode[] }> = [
    { label: 'Ana menü', items: [item('/dashboard', 'dashboard', 'Genel bakış', true), item('/products', 'products', 'Ürünler'), item('/orders', 'orders', 'Siparişler', false, orderNavigationCount(orderSummary.data), true, 'bekleyen sipariş'), item('/returns', 'returns', 'İadeler', false, navigationCounts?.pendingReturns, true, 'bekleyen iade'), item('/invoices', 'invoice', 'Faturalar', false, invoiceWorkspaceSummary.data?.dueSoonCount, true, 'faturanın süresi yaklaşıyor', true), item('/questions', 'products', 'Ürün soruları', false, questionWaitingSummary.data?.totalCount, true, 'bekleyen soru'), item('/integrations', 'connect', 'Entegrasyonlar'), item('/mappings/categories', 'layers', 'Eşleştirmeler'), item('/jobs', 'jobs', 'İşlem Takibi')] },
  ]
  const navigation = <>{navigationGroups.map(group => <div className="nav-group" key={group.label}>{group.items}</div>)}</>
  const quickSearchItems: Array<{ to: string; label: string; description: string; icon: UiIconName }> = [
    { to: '/dashboard', label: 'Genel bakış', description: 'Operasyon merkezini aç', icon: 'dashboard' },
    { to: '/products', label: 'Ürünler', description: 'Kataloğu ve stokları yönet', icon: 'products' },
    { to: '/orders', label: 'Siparişler', description: 'Sipariş akışını incele', icon: 'orders' },
    { to: '/integrations', label: 'Entegrasyonlar', description: 'Platform bağlantılarını yönet', icon: 'connect' },
    { to: '/mappings/categories', label: 'Eşleştirmeler', description: 'Kategori ve özellik eşlemeleri', icon: 'layers' },
    { to: '/settings', label: 'Sistem ayarları', description: 'Güvenlik ve görünüm ayarları', icon: 'settings' },
  ]
  return <div className={`app-shell stitch-shell ${menuCollapsed ? 'sidebar-collapsed' : ''} ${sidebarHovering ? 'sidebar-hover-expanded' : ''} ${sidebarPinned ? 'sidebar-pinned' : ''}`}>
    <aside aria-label="Ravencia ana menüsü" onMouseEnter={handleSidebarMouseEnter} onMouseLeave={handleSidebarMouseLeave}>
      <div className="sidebar-brand-row"><Link className="sidebar-brand-home" to="/dashboard" aria-label="Genel bakışa git"><span className="stitch-brand-mark" aria-hidden="true"><img src="/pack/brand/ravencia-symbol-128.webp" alt="" /></span><span className="brand wordmark"><strong>Ravencia</strong></span></Link><button type="button" className={`sidebar-pin-toggle ${sidebarPinned ? 'is-pinned' : ''}`} aria-label={sidebarPinned ? 'Menüyü daralt' : 'Menüyü sabitle'} aria-pressed={sidebarPinned} title={sidebarPinned ? 'Menüyü daralt' : 'Menüyü sabitle'} onClick={toggleSidebarPinned}><UiIcon name="pin" size={18} /></button></div>
      <nav aria-label="Ana menü">{navigation}</nav>
      <div className="sidebar-side-bottom"><div className="settings-nav">{item('/settings', 'settings', 'Sistem Ayarları', true)}<button type="button" className="logout-link" aria-label="Oturumu kapat" title="Oturumu kapat" onClick={() => void logout()}>{icon('logout')}<span className="logout-label">Oturumu kapat</span></button></div><div className="sidebar-profile"><span className="sidebar-avatar">{initials}</span><span className="sidebar-profile-copy"><strong>{displayName}</strong></span></div></div>
    </aside>
    <main>
      <header className={`rv-topbar${topbarHidden ? ' is-hidden' : ''}`}><div className="rv-topbar-leading"><button className="rv-mobile-menu-toggle rv-icon-button" type="button" aria-label="Ana menüyü aç" aria-expanded={mobileMenuOpen} onClick={() => setMobileMenuOpen(true)}><UiIcon name="menu" size={22} /></button><div className="rv-breadcrumb"><UiIcon name="layers" size={16} /><span>Operasyon Merkezi</span><UiIcon name="chevronRight" size={14} /><strong>{pageName}</strong></div></div><div className="rv-topbar-actions"><div ref={notificationRef} className="rv-topbar-notifications"><button type="button" className="rv-topbar-notification-trigger" aria-label="Bildirim geçmişini aç" aria-expanded={notificationsOpen} aria-controls="rv-notification-history" onClick={() => setNotificationsOpen(value => !value)}><UiIcon name="bell" size={18} />{unreadNotificationCount > 0 && <span className="rv-topbar-notification-count" aria-label={`${unreadNotificationCount} okunmamış bildirim`}>{unreadNotificationCount > 99 ? '99+' : unreadNotificationCount}</span>}</button></div><button type="button" className="rv-topbar-theme" aria-label={visualTheme === 'light' ? 'Koyu temaya geç' : 'Açık temaya geç'} title={visualTheme === 'light' ? 'Koyu temaya geç' : 'Açık temaya geç'} aria-pressed={visualTheme === 'dark'} onClick={() => setVisualThemePreference(visualTheme === 'light' ? 'dark' : 'light')}><UiIcon name={visualTheme === 'light' ? 'moon' : 'sun'} size={18} /><span>{visualTheme === 'light' ? 'Koyu tema' : 'Açık tema'}</span></button><button className="rv-topbar-search rv-topbar-search-icon" type="button" aria-label="Hızlı aramayı aç" aria-keyshortcuts="Control+k Meta+k" onClick={() => setQuickSearchOpen(true)}><UiIcon name="search" size={20} /></button></div></header>
      {notificationsOpen && createPortal(<div ref={notificationHistoryRef} id="rv-notification-history" className="rv-notification-history" role="dialog" aria-label="Bildirim geçmişi" style={notificationHistoryPosition}><Suspense fallback={null}><NotificationPanel notifications={notifications} onNavigate={href => { setNotificationsOpen(false); navigate(href) }} onCleared={() => setNotifications([])} /></Suspense></div>, document.body)}
      <Modal open={quickSearchOpen} title="Hızlı arama" description="Bir sayfa veya işlem seçin." onClose={() => setQuickSearchOpen(false)}><div className="rv-command-list" role="listbox" aria-label="Hızlı arama sonuçları">{quickSearchItems.map(itemOption => <Link key={itemOption.to} className="rv-command-item" to={itemOption.to} onClick={() => setQuickSearchOpen(false)}><UiIcon name={itemOption.icon} size={20} /><span><strong>{itemOption.label}</strong><small>{itemOption.description}</small></span><UiIcon name="chevronRight" size={16} /></Link>)}</div></Modal>
      <Drawer open={mobileMenuOpen} className="rv-mobile-drawer" title={<img className="rv-drawer-logo" src="/pack/brand/ravencia-wordmark.webp" alt="Ravencia" />} description={null} onClose={() => setMobileMenuOpen(false)}><nav className="rv-mobile-navigation" aria-label="Mobil ana menü">{navigation}{item('/settings', 'settings', 'Sistem Ayarları', true)}<button className="rv-button rv-button-secondary" type="button" onClick={() => void logout()}>Oturumu kapat</button></nav></Drawer>
      <StepUpPrompt />
      <ErrorBoundary resetKey={location.pathname}><Suspense fallback={<DelayedStatus title="Ekran hazırlanıyor" />}><Routes><Route path="/dashboard" element={<Dashboard />} /><Route path="/products" element={<ProductsPage />} /><Route path="/products/new" element={<NewProductPage />} /><Route path="/products/:id" element={<ProductDetailPage />} /><Route path="/catalog/categories" element={<CategoriesPage />} /><Route path="/catalog/brands" element={<BrandsPage />} /><Route path="/catalog/attributes" element={<AttributesPage />} /><Route path="/imports" element={<ImportsPage />} /><Route path="/imports/:id" element={<ImportDetailPage />} /><Route path="/integrations" element={<IntegrationsPage />} /><Route path="/integrations/:id" element={<IntegrationDetailPage />} /><Route path="/mappings/categories" element={<MappingPage />} /><Route path="/mappings/attributes" element={<AttributeMappingPage />} /><Route path="/mappings/product-matches" element={<Navigate to="/mappings/categories" replace />} /><Route path="/questions" element={<QuestionsPage />} /><Route path="/orders" element={<OrdersPage />} /><Route path="/orders/:id" element={<Navigate to="/orders" replace />} /><Route path="/returns" element={<ReturnsPage />} /><Route path="/returns/:id" element={<ReturnDetailPage />} /><Route path="/shipments" element={<ShipmentsPage />} /><Route path="/shipments/:id" element={<ShipmentDetailPage />} /><Route path="/invoices" element={<InvoicesPage />} /><Route path="/invoices/:id" element={<InvoiceDetailPage />} /><Route path="/jobs" element={<JobsPage me={me} />} /><Route path="/settings/security" element={<Navigate to="/settings?tab=auth" replace />} /><Route path="/settings/appearance" element={<Navigate to="/settings?tab=read" replace />} /><Route path="/settings" element={<SystemSettingsPage />} /><Route path="*" element={<Navigate to="/dashboard" replace />} /></Routes></Suspense></ErrorBoundary>
    </main>
    </div>
}

const sessionExpiredNotice = 'Oturumunuzun süresi doldu. Devam etmek için yeniden giriş yapın.'
const isSignedOut = (error: unknown) => error instanceof ApiRequestError && error.status === 401

export function App() {
  const client = useQueryClient()
  const me = useQuery({ queryKey: ['me'], queryFn: () => api<Me>('/me'), retry: false })
  // A request heard 401: the session ended while the panel was open. The workspace gives way to the sign-in screen at
  // once (no reload, no screens retrying forever); signing in again brings the person back to the same page.
  useEffect(() => setUnauthorizedHandler(() => {
    const current = client.getQueryData<Me>(['me'])
    if (!current || current.state === 'REVOKED') return
    void client.cancelQueries({ predicate: query => query.queryKey[0] !== 'me' })
    client.setQueryData<Me>(['me'], { ...current, state: 'REVOKED' })
  }), [client])
  useOperationsRealtime(me.data?.state === 'ACTIVE')
  // Key presses, clicks and scrolling mark the person as present; polls made while nobody is (see hubPoll) leave the
  // session's idle clock alone.
  useEffect(() => trackUserActivity(), [])
  useEffect(() => {
    const blurNumberInputOnWheel = (event: WheelEvent) => {
      const target = event.target
      if (target instanceof HTMLInputElement && target.type === 'number' && document.activeElement === target) target.blur()
    }
    document.addEventListener('wheel', blurNumberInputOnWheel, { capture: true })
    return () => document.removeEventListener('wheel', blurNumberInputOnWheel, { capture: true })
  }, [])
  const authPreview = import.meta.env.DEV ? new URLSearchParams(window.location.search).get('authPreview') : null
  if (authPreview === 'password') return <ChangePassword />
  if (authPreview === 'mfa') return <MfaChallenge />
  if (me.isLoading) return <DelayedStatus title="Çalışma alanı hazırlanıyor" />
  // Only "not signed in" asks for the password; a server that is restarting or unreachable shows the error and tries again.
  if (me.isError) return isSignedOut(me.error) ? <Routes><Route path="*" element={<Login />} /></Routes> : <SessionLoadError error={me.error} onRetry={() => void me.refetch()} />
  if (!me.data) return <Status title="Oturum bilgisi alınamadı" />
  // Browser-stored notification history and remembered counts belong to this tenant and user only.
  setStorageScope(me.data.tenantId, me.data.id)
  if (me.data.state === 'REVOKED') return <Routes><Route path="*" element={<Login notice={sessionExpiredNotice} />} /></Routes>
  if (me.data.state === 'PASSWORD_CHANGE_REQUIRED') return <ChangePassword />
  if (me.data.state === 'MFA_CHALLENGE') return <MfaChallenge />
  if (me.data.state !== 'ACTIVE') return <LockedSession />
  return <Shell me={me.data} />
}

const authPlatforms = [
  { id: 'trendyol', name: 'Trendyol' },
  { id: 'hepsiburada', name: 'Hepsiburada' },
  { id: 'shopify', name: 'Shopify' },
  { id: 'n11', name: 'n11' },
] as const

const authActivity = [
  { id: 'order', platform: 'Trendyol', message: 'Yeni sipariş alındı', time: 'Az önce', tone: 'trendyol' },
  { id: 'stock', platform: 'Hepsiburada', message: 'Stok senkronize edildi', time: '1 dk', tone: 'hepsiburada' },
  { id: 'product', platform: 'Shopify', message: 'Ürün aktarımı tamamlandı', time: '2 dk', tone: 'shopify' },
  { id: 'invoice', platform: 'n11', message: 'Fatura durumu güncellendi', time: '3 dk', tone: 'n11' },
  { id: 'shipment', platform: 'Trendyol', message: 'Kargo etiketi oluşturuldu', time: '5 dk', tone: 'trendyol' },
  { id: 'price', platform: 'Hepsiburada', message: 'Fiyat listesi senkronize edildi', time: '8 dk', tone: 'hepsiburada' },
] as const

// The same query as the phone sign-in rules in styles/responsive.css.
const authFitsOneScreen = '(max-width: 640px) and (min-height: 560px)'

function AuthPageFrame({ ariaLabel, accessLabel, accessMeta, cardTitle, cardDescription, progressStep = 1, children, footerMeta = 'Ravencia Workspace', topAction }: { ariaLabel: string; accessLabel: string; accessMeta: string; cardTitle: string; cardDescription: string; progressStep?: 1 | 2 | 3; children: ReactNode; footerMeta?: string; topAction?: ReactNode }) {
  // Phones where the sign-in screen fits one screen: it holds the page still through the one counted page lock, which
  // is released for sure when the screen goes (iOS Safari kept an html:has(.rv-auth-page) lock after signing in).
  useScrollLock(useMediaQuery(authFitsOneScreen), false, { root: true })
  return <main className="rv-auth-page rv-auth-single">
    <div className="rv-auth-atmosphere" aria-hidden="true">
      <span className="auth-aurora auth-aurora-primary" />
      <span className="auth-aurora auth-aurora-accent" />
      <div className="auth-signal-scene">
        <svg className="auth-signal-routes" viewBox="0 0 940 940" preserveAspectRatio="xMidYMid meet">
          <defs><filter id="auth-signal-packet-glow" x="-120%" y="-120%" width="340%" height="340%"><feGaussianBlur stdDeviation="2.5" result="blur" /><feMerge><feMergeNode in="blur" /><feMergeNode in="SourceGraphic" /></feMerge></filter></defs>
          <g className="auth-signal-orbits">
            <ellipse className="auth-signal-orbit auth-signal-orbit-primary" cx="470" cy="470" rx="356" ry="164" />
            <ellipse className="auth-signal-orbit auth-signal-orbit-accent" cx="470" cy="470" rx="356" ry="236" transform="rotate(60 470 470)" />
            <ellipse className="auth-signal-orbit auth-signal-orbit-primary" cx="470" cy="470" rx="356" ry="236" transform="rotate(-60 470 470)" />
          </g>
          <g className="auth-signal-route-lines">
            <path id="auth-route-line-core-left" className="auth-route-line" d="M470 470 C410 445 325 372 88 280" />
            <path id="auth-route-line-core-right" className="auth-route-line" d="M470 470 C540 435 650 375 850 316" />
            <path id="auth-route-line-core-bottom-left" className="auth-route-line" d="M470 470 C405 515 300 605 108 674" />
            <path id="auth-route-line-core-bottom-right" className="auth-route-line" d="M470 470 C545 520 655 618 844 710" />
            <path id="auth-route-line-platform-top" className="auth-route-line auth-route-line-platform" d="M88 280 C300 145 645 145 850 316" />
            <path id="auth-route-line-platform-bottom" className="auth-route-line auth-route-line-platform" d="M108 674 C300 820 645 820 844 710" />
          </g>
          <g filter="url(#auth-signal-packet-glow)">
            <circle className="auth-route-packet auth-route-packet-primary" r="3.25"><animateMotion dur="9.2s" begin="-1.4s" repeatCount="indefinite"><mpath href="#auth-route-line-core-left" /></animateMotion></circle>
            <circle className="auth-route-packet auth-route-packet-accent" r="3"><animateMotion dur="10.8s" begin="-5.2s" repeatCount="indefinite"><mpath href="#auth-route-line-core-right" /></animateMotion></circle>
            <circle className="auth-route-packet auth-route-packet-primary" r="2.75"><animateMotion dur="9.8s" begin="-7.6s" repeatCount="indefinite"><mpath href="#auth-route-line-core-bottom-left" /></animateMotion></circle>
            <circle className="auth-route-packet auth-route-packet-accent" r="3.25"><animateMotion dur="11.6s" begin="-3.1s" repeatCount="indefinite"><mpath href="#auth-route-line-core-bottom-right" /></animateMotion></circle>
            <circle className="auth-route-packet auth-route-packet-platform" r="2.5"><animateMotion dur="14s" begin="-4.8s" repeatCount="indefinite"><mpath href="#auth-route-line-platform-top" /></animateMotion></circle>
            <circle className="auth-route-packet auth-route-packet-platform" r="2.5"><animateMotion dur="15.4s" begin="-9.3s" repeatCount="indefinite"><mpath href="#auth-route-line-platform-bottom" /></animateMotion></circle>
          </g>
        </svg>
        <span className="auth-signal-core" />
        {authPlatforms.map(platform => <span className={`auth-orbit-tag auth-orbit-tag-${platform.id}`} key={platform.id}><i /><b>{platform.name}</b><small>bağlı</small></span>)}
      </div>
    </div>
    <header className="rv-auth-header">
      <div className="rv-auth-brand"><img className="rv-auth-symbol" src="/pack/brand/ravencia-symbol-128.webp" alt="" /><span className="rv-auth-brand-name">Ravencia</span></div>
      <span className="rv-auth-context"><UiIcon name="layers" /> Operasyon Merkezi</span>
      <span className="rv-auth-system-status"><i /> Sistem hazır</span>
    </header>
    <section className="rv-auth-stage" aria-label={ariaLabel}>
      <div className="auth-focus-column">
        <div className="rv-auth-hero-brand">
          <img className="rv-auth-hero-symbol" src="/pack/brand/ravencia-symbol-256.webp" alt="" />
          <img className="rv-auth-hero-wordmark" src="/pack/brand/ravencia-wordmark.webp" alt="Ravencia" />
        </div>
        <div className="rv-auth-login-shell">
          <div className="rv-auth-login-top"><span><i /> {accessLabel}</span><span>{accessMeta}</span></div>
          <div className="rv-auth-card">
            <div className="rv-auth-card-glow" aria-hidden="true" />
            {topAction && <div className="rv-auth-card-top-action">{topAction}</div>}
            <header className="rv-auth-login-header"><div><h2>{cardTitle}</h2><span>{cardDescription}</span></div></header>
            <div className={`rv-auth-progress rv-auth-progress-step-${progressStep}`} aria-hidden="true"><span /></div>
            {children}
            <footer className="rv-auth-footer"><span><i /> TLS şifreli bağlantı</span><small>{footerMeta}</small></footer>
          </div>
        </div>
      </div>
    </section>
    <div className="auth-notification-center" aria-label="Canlı entegrasyon bildirimleri">
      <div className="auth-notification-label"><UiIcon name="bell" size={17} /><span><strong>Bildirim merkezi</strong><small><i /> Canlı akış</small></span></div>
      <div className="auth-notification-viewport"><div className="auth-notification-marquee">{[0, 1].map(copy => <div className="auth-notification-events" key={copy} aria-hidden={copy === 1 ? true : undefined}>{authActivity.map(item => <span className={`auth-notification-event event-${item.tone}`} key={item.id}><i /><b>{item.platform}</b><span>{item.message}</span><time>{item.time}</time></span>)}</div>)}</div></div>
      <span className="auth-notification-count">3 yeni</span>
    </div>
  </main>
}

function Login({ notice }: { notice?: string }) {
  const navigate = useNavigate()
  const location = useLocation()
  const client = useQueryClient()
  const rememberedEmail = localStorage.getItem('ravencia.rememberedEmail') ?? ''
  const [email, setEmail] = useState(rememberedEmail)
  const [rememberMe, setRememberMe] = useState(Boolean(rememberedEmail))
  const [showPw, setShowPw] = useState(false)
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(false)
  const [tenantOptions, setTenantOptions] = useState<TenantOption[]>([])
  const [tenantId, setTenantId] = useState('')

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setError('')
    setLoading(true)
    const data = new FormData(event.currentTarget)
    try {
      await api<{ state: string }>('/login', { method: 'POST', body: JSON.stringify({ email: email.trim(), password: data.get('password'), tenantId: tenantId || null }) })
      const currentUser = await api<Me>('/me')
      if (rememberMe) localStorage.setItem('ravencia.rememberedEmail', email.trim())
      else localStorage.removeItem('ravencia.rememberedEmail')
      // Nothing read under the previous session (perhaps another account or workspace) is shown to this one.
      client.removeQueries({ predicate: query => query.queryKey[0] !== 'me' })
      client.setQueryData(['me'], currentUser)
      if (location.pathname === '/' || location.pathname === '') navigate('/dashboard', { replace: true })
    } catch (reason) {
      if (reason instanceof ApiRequestError && reason.code === 'TENANT_SELECTION_REQUIRED') { setTenantOptions(reason.tenants ?? []); setError('Bir çalışma alanı seçip girişe devam edin.') }
      else if (reason instanceof ApiRequestError && reason.status === 401) setError('E-posta veya parola hatalı. Art arda başarısız denemelerde bu ağdan giriş bir süre engellenir.')
      else if (reason instanceof ApiRequestError && reason.status === 429) setError('Çok fazla giriş denemesi yapıldı. Bir dakika bekleyip yeniden deneyin.')
      else if (reason instanceof ApiRequestError && reason.status === 400) setError('Güvenlik doğrulaması tamamlanamadı. Sayfayı yenileyip tekrar deneyin.')
      else setError('Giriş servisine ulaşılamadı. Bağlantınızı kontrol edip yeniden deneyin.')
    } finally {
      setLoading(false)
    }
  }

  return <AuthPageFrame ariaLabel="Ravencia operasyon merkezi girişi" accessLabel="Güvenli erişim" accessMeta="Oturum korumalı" cardTitle="Hesabınıza giriş yapın" cardDescription="Ravencia çalışma alanınıza güvenli erişim.">
          <form className="rv-auth-form" onSubmit={submit}>
            {notice && !error && <div className="rv-auth-error" role="status"><i /><span>{notice}</span></div>}
            <div className="rv-auth-field"><label htmlFor="login-email">E-posta adresi</label><div className="rv-auth-control"><UiIcon name="mail" size={18} /><input id="login-email" name="email" type="email" value={email} onChange={event => setEmail(event.target.value)} required autoComplete="username" placeholder="ornek@ravencia.com" ref={focusOnMount} /></div></div>
            <div className="rv-auth-field"><div className="rv-auth-label-row"><label htmlFor="login-password">Parola</label><button type="button" onClick={() => setError('Parola sıfırlama için sistem yöneticinizle iletişime geçin.')}>Parolamı unuttum</button></div><div className="rv-auth-control"><UiIcon name="lock" size={18} /><input id="login-password" name="password" type={showPw ? 'text' : 'password'} required autoComplete="current-password" placeholder="Parolanızı girin" /><button type="button" className="rv-auth-password-toggle" aria-label={showPw ? 'Parolayı gizle' : 'Parolayı göster'} onClick={() => setShowPw(value => !value)}><UiIcon name={showPw ? 'eye' : 'eyeOff'} size={18} /></button></div></div>
            {tenantOptions.length > 0 && <div className="rv-auth-field"><label htmlFor="login-tenant">Çalışma alanı</label><select id="login-tenant" value={tenantId} onChange={event => setTenantId(event.target.value)} required><option value="">Çalışma alanı seçin</option>{tenantOptions.map(option => <option key={option.id} value={option.id}>{option.displayName}</option>)}</select></div>}
            <label className="rv-auth-remember"><input type="checkbox" checked={rememberMe} onChange={event => setRememberMe(event.target.checked)} /><span>E-posta adresimi bu cihazda hatırla</span></label>
            {error && <div className="rv-auth-error" role="alert"><i /><span>{error}</span></div>}
            <button className="rv-auth-submit" type="submit" disabled={loading}>{loading ? <><i /> Oturum doğrulanıyor…</> : <><span>Güvenli giriş yap</span><UiIcon name="arrowRight" /></>}</button>
          </form>
  </AuthPageFrame>
}

function ChangePassword() {
  const client = useQueryClient()
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(false)
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setError('')
    setLoading(true)
    const data = new FormData(event.currentTarget)
    if (data.get('next') !== data.get('confirm')) {
      setError('Yeni parola ile tekrarı aynı değil.')
      setLoading(false)
      return
    }
    try {
      await api('/change-password', { method: 'POST', body: JSON.stringify({ currentPassword: data.get('current'), newPassword: data.get('next') }) })
      await client.invalidateQueries({ queryKey: ['me'] })
    } catch (reason) {
      setError(passwordChangeErrorMessage(reason))
    } finally {
      setLoading(false)
    }
  }
  return <AuthPageFrame ariaLabel="Ravencia parola yenileme" accessLabel="Güvenlik adımı" accessMeta="2 / 3" cardTitle="Yeni parolanızı belirleyin" cardDescription="İlk giriş güvenlik adımını tamamlayın." progressStep={2} footerMeta="Parola politikası etkin">
    <form className="rv-auth-form" onSubmit={submit}>
      <div className="rv-auth-field"><label htmlFor="current-password">Geçerli parola</label><div className="rv-auth-control"><UiIcon name="lock" /><input id="current-password" name="current" type="password" autoComplete="current-password" required ref={focusOnMount} placeholder="Geçerli parolanız" /></div></div>
      <div className="rv-auth-field"><label htmlFor="new-password">Yeni parola</label><div className="rv-auth-control"><UiIcon name="lock" /><input id="new-password" name="next" type="password" autoComplete="new-password" minLength={15} maxLength={64} required placeholder="En az 15 karakter" /></div><small className="rv-auth-hint">15–64 karakter; büyük ve küçük harf, rakam ve simge içeren, yaygın kelimelerden, adınızdan ve e-postanızdan oluşmayan bir parola kullanın.</small></div>
      <div className="rv-auth-field"><label htmlFor="confirm-password">Yeni parola (tekrar)</label><div className="rv-auth-control"><UiIcon name="lock" /><input id="confirm-password" name="confirm" type="password" autoComplete="new-password" minLength={15} maxLength={64} required placeholder="Yeni parolayı tekrar girin" /></div></div>
      {error && <div className="rv-auth-error" role="alert"><i /><span>{error}</span></div>}
      <button className="rv-auth-submit" type="submit" disabled={loading}>{loading ? <><i /> Parola güncelleniyor…</> : <><span>Parolayı güncelle</span><UiIcon name="arrowRight" /></>}</button>
    </form>
  </AuthPageFrame>
}

// The server's reason, said plainly (W30): a wrong current password, a new password the rules refuse, too many tries.
function passwordChangeErrorMessage(reason: unknown) {
  if (!(reason instanceof ApiRequestError)) return 'Parola değiştirilemedi. Bağlantınızı kontrol edip yeniden deneyin.'
  if (reason.code === 'CURRENT_PASSWORD_INVALID') return 'Mevcut parola doğrulanamadı. Art arda hatalı denemelerde bu ağdan giriş bir süre engellenir.'
  if (reason.status === 429) return 'Çok fazla deneme yapıldı. Bir dakika bekleyip yeniden deneyin.'
  if (reason.code === 'PASSWORD_POLICY_FAILED' || reason.code === 'PASSWORD_CHANGE_FAILED') return reason.message
  return reason.message || 'Parola değiştirilemedi.'
}

function mfaChallengeErrorMessage(reason: unknown, mode: 'code' | 'recovery') {
  if (reason instanceof ApiRequestError && reason.code === 'MFA_ATTEMPTS_EXCEEDED') return 'Çok fazla hatalı kod girildi; güvenliğiniz için oturum kapatıldı. Giriş ekranına dönüp yeniden giriş yapın.'
  if (reason instanceof ApiRequestError && reason.status === 429) return 'Çok fazla deneme yapıldı. Bir dakika bekleyip yeniden deneyin.'
  if (reason instanceof ApiRequestError && reason.code === 'TENANT_NOT_AVAILABLE') return 'Bu hesap için aktif bir çalışma alanı bulunamadı.'
  if (reason instanceof ApiRequestError && reason.status >= 500) return 'Doğrulama servisine ulaşılamadı. Kısa süre sonra yeniden deneyin.'
  if (!(reason instanceof ApiRequestError)) return 'Doğrulama servisine ulaşılamadı. Bağlantınızı kontrol edip yeniden deneyin.'
  return mode === 'code' ? 'Doğrulama kodu geçersiz veya süresi dolmuş.' : 'Kurtarma kodu geçersiz veya daha önce kullanılmış.'
}

function MfaChallenge() {
  const client = useQueryClient()
  const [mode, setMode] = useState<'code' | 'recovery'>('code')
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(false)
  const [leaving, setLeaving] = useState(false)
  async function backToLogin() {
    setLeaving(true)
    try { await api('/logout', { method: 'POST' }) } finally { clearPersonalStorage(); window.location.replace(`/?signedOut=${Date.now()}`) }
  }
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setError('')
    setLoading(true)
    const data = new FormData(event.currentTarget)
    try {
      await api('/mfa/challenge', { method: 'POST', body: JSON.stringify({ code: mode === 'code' ? data.get('code') : null, recoveryCode: mode === 'recovery' ? data.get('recovery') : null }) })
      await client.invalidateQueries({ queryKey: ['me'] })
    } catch (reason) {
      setError(mfaChallengeErrorMessage(reason, mode))
    } finally {
      setLoading(false)
    }
  }
  return <AuthPageFrame ariaLabel="Ravencia iki adımlı doğrulama" accessLabel="Kimlik doğrulama" accessMeta="3 / 3" cardTitle="Erişiminizi doğrulayın" cardDescription={mode === 'code' ? 'Authenticator uygulamanızdaki 6 haneli kodu girin.' : 'Tek kullanımlık kurtarma kodlarınızdan birini girin.'} progressStep={3} footerMeta="Doğrulama koruması etkin" topAction={<button type="button" className="rv-auth-back" onClick={() => void backToLogin()} disabled={leaving}><UiIcon name="arrowLeft" size={16} />{leaving ? 'Giriş ekranına dönülüyor…' : 'Giriş ekranına dön'}</button>}>
    <div className="rv-auth-choice" role="tablist" aria-label="Doğrulama yöntemi"><button type="button" role="tab" aria-selected={mode === 'code'} className={mode === 'code' ? 'is-active' : ''} onClick={() => { setMode('code'); setError('') }}>Authenticator kodu</button><button type="button" role="tab" aria-selected={mode === 'recovery'} className={mode === 'recovery' ? 'is-active' : ''} onClick={() => { setMode('recovery'); setError('') }}>Kurtarma kodu</button></div>
    <form className="rv-auth-form rv-auth-mfa-form" onSubmit={submit}>
      {mode === 'code' ? <div className="rv-auth-field"><label htmlFor="mfa-code">6 haneli doğrulama kodu</label><div className="rv-auth-control"><UiIcon name="shield" /><input id="mfa-code" name="code" inputMode="numeric" autoComplete="one-time-code" pattern="[0-9]{6}" minLength={6} maxLength={6} required ref={focusOnMount} placeholder="000000" /></div></div> : <div className="rv-auth-field"><label htmlFor="mfa-recovery">Kurtarma kodu</label><div className="rv-auth-control"><UiIcon name="lock" /><input id="mfa-recovery" name="recovery" autoComplete="off" required ref={focusOnMount} placeholder="Kurtarma kodunu girin" /></div></div>}
      {error && <div className="rv-auth-error" role="alert"><i /><span>{error}</span></div>}
      <button className="rv-auth-submit" type="submit" disabled={loading}>{loading ? <><i /> Doğrulanıyor…</> : <><span>Erişimi doğrula</span><UiIcon name="arrowRight" /></>}</button>
    </form>
  </AuthPageFrame>
}
type DashboardMetrics = { pendingOrders: number; newAndProcessingOrders?: number; lateOrders: number; todayOrders: number; todayProductQuantity: number; monthOrders: number; monthProductQuantity: number; pendingReturns: number; dueSoonInvoices: number; uninvoicedInvoices: number; lowStockProducts: number; activeConnections: number; pendingByPlatform: Record<string, number>; lateByPlatform?: Record<string, number>; pendingReturnsByPlatform?: Record<string, number>; oldestQueuedJobAt?: string | null; lastVerifiedSynchronizationAt?: string | null; deadJobCount?: number; manualReviewJobCount?: number; recentJobCount?: number; recentRateLimitJobCount?: number; oldestStockObservationAt?: string | null }
type DashboardLowStock = { id: string; title: string; totalStock: number; primaryImageUrl: string | null }
type DashboardSyncStatus = { resourceType: string; label: string; kind: string; status: string; lastAttemptAt: string | null; lastSuccessAt: string | null; lastErrorCode: string | null }
type DashboardBootstrap = { metrics: DashboardMetrics; lowStock: DashboardLowStock[]; sync: DashboardSyncStatus[]; platforms: { name: string; status: string }[]; generatedAt: string; version: number }

// The session is neither active nor waiting for a sign-in step: the way out is a fresh sign-in.
function LockedSession() {
  const [leaving, setLeaving] = useState(false)
  async function backToLogin() {
    setLeaving(true)
    try { await api('/logout', { method: 'POST' }) } catch { /* the redirect below leaves anyway */ } finally { clearPersonalStorage(); window.location.replace(`/?signedOut=${Date.now()}`) }
  }
  return <Status title="Oturum kilitli" detail="Bu oturumla devam edilemiyor. Giriş ekranına dönüp yeniden giriş yapın." action={<button type="button" className="rv-button rv-button-primary rv-button-md" onClick={() => void backToLogin()} disabled={leaving}>{leaving ? 'Giriş ekranına dönülüyor…' : 'Giriş Ekranına Dön'}</button>} />
}

// The session could not be read for another reason (server restarting during a deploy, network): no password prompt.
function SessionLoadError({ error, onRetry }: { error: unknown; onRetry: () => void }) {
  return <div className="rv-splash-screen">
    <div className="rv-splash-card">
      <div className="rv-splash-brand" aria-label="Ravencia"><img className="rv-splash-symbol" src="/pack/brand/ravencia-symbol-128.webp" alt="" /><img className="rv-splash-wordmark" src="/pack/brand/ravencia-wordmark.webp" alt="Ravencia MarketplaceHub" /></div>
      <ErrorBox error={error} onRetry={onRetry} />
    </div>
  </div>
}

function Status({ title, detail, action }: { title: string; detail?: string; action?: ReactNode }) {
  return (
    <div className="rv-splash-screen" role="status" aria-live="polite">
      <div className="rv-splash-card">
        <div className="rv-splash-brand" aria-label="Ravencia"><img className="rv-splash-symbol" src="/pack/brand/ravencia-symbol-128.webp" alt="" /><img className="rv-splash-wordmark" src="/pack/brand/ravencia-wordmark.webp" alt="Ravencia MarketplaceHub" /></div>
        <div className="rv-splash-copy"><span className="rv-splash-kicker">OPERASYON MERKEZİ</span><h1>{title}</h1><p>{detail ?? 'Çalışma alanınız hazırlanıyor.'}</p></div>
        <div className="rv-splash-loading-bar" aria-hidden="true"><span className="rv-splash-loading-progress" /></div>
        <div className="rv-splash-skeleton" aria-hidden="true">
          <div className="rv-splash-skeleton-heading"><span className="rv-splash-skeleton-line line-wide" /><span className="rv-splash-skeleton-line line-short" /></div>
          <div className="rv-splash-skeleton-grid">
            <div className="rv-splash-skeleton-card"><span className="rv-splash-skeleton-media" /><span className="rv-splash-skeleton-line line-wide" /><span className="rv-splash-skeleton-line line-short" /></div>
            <div className="rv-splash-skeleton-card"><span className="rv-splash-skeleton-media" /><span className="rv-splash-skeleton-line line-medium" /><span className="rv-splash-skeleton-line line-short" /></div>
            <div className="rv-splash-skeleton-card is-wide"><span className="rv-splash-skeleton-media" /><span className="rv-splash-skeleton-line line-wide" /><span className="rv-splash-skeleton-line line-medium" /></div>
          </div>
        </div>
        {action ? <div className="rv-splash-action">{action}</div> : <small className="rv-splash-detail">Güvenli bağlantı kuruluyor</small>}
      </div>
    </div>
  )
}

function DelayedStatus({ title, detail, delayMs = 450 }: { title: string; detail?: string; delayMs?: number }) {
  const [visible, setVisible] = useState(false)
  useEffect(() => {
    const timer = window.setTimeout(() => setVisible(true), delayMs)
    return () => window.clearTimeout(timer)
  }, [delayMs])
  return <div className={`rv-loading-deferred${visible ? ' is-visible' : ''}`} aria-hidden={!visible}><Status title={title} detail={detail} /></div>
}
