import { lazy, Suspense, useEffect, useRef, useState, type CSSProperties, type FormEvent, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { Link, Navigate, NavLink, Route, Routes, useLocation, useNavigate } from 'react-router'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api, ApiRequestError, hubApi, type Me, type TenantOption } from '../shared/api'
import { Drawer, Modal, UiIcon, type UiIconName } from '../shared/components'
import { MobileActionMenu } from '../shared/MobileActionMenu'
import { AttributesPage, AttributeMappingPage, BrandsPage, CategoriesPage, ImportDetailPage, ImportsPage, NewProductPage, ProductDetailPage, ProductsPage, IntegrationDetailPage, IntegrationsPage, MappingPage, OrdersPage, QuestionsPage, ReturnDetailPage, ReturnsPage, ShipmentDetailPage, ShipmentsPage, InvoiceDetailPage, InvoicesPage, JobsPage } from './route-components'
import { useOperationsRealtime } from './hooks/useOperationsRealtime'
import { appearanceColorCssVariable, appearanceColorTokenOptions, appearanceFontFamilyCss, appearanceFontScale, useAppearanceSettings } from '../features/settings/appearance-settings'
import { readNotificationHistory, subscribeNotificationHistory, type AppNotification } from '../shared/notifications'
import { dashboardOperationalLinks } from '../shared/dashboard-operational-links'
import { DateRangePicker } from '../shared/DateRangePicker'
import { orderNavigationCount, type OrderNavigationSummary } from '../shared/order-navigation-count'
import { questionWaitingSummaryPath } from './questionNavigationSummary'
import { readVisualThemePreference, setVisualThemePreference, visualThemeChangeEvent, type VisualTheme } from '../features/settings/visual-theme'

const NotificationPanel = lazy(() => import('./NotificationPanel').then(module => ({ default: module.NotificationPanel })))
const SystemSettingsPage = lazy(() => import('../features/settings/SystemSettingsPage').then(module => ({ default: module.SystemSettingsPage })))

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
  const navigationSummary = useQuery({ queryKey: ['dashboard-bootstrap'], queryFn: () => hubApi<DashboardBootstrap>('/dashboard/bootstrap'), staleTime: 30_000, refetchOnWindowFocus: true })
  const invoiceWorkspaceSummary = useQuery({ queryKey: ['invoice-workspace-summary'], queryFn: () => hubApi<{ dueSoonCount: number; uninvoicedCount?: number }>('/invoice-workspace/summary'), staleTime: 30_000, refetchOnWindowFocus: true })
  const orderSummary = useQuery({ queryKey: ['orders', 'summary', []], queryFn: () => hubApi<OrderNavigationSummary>('/orders/summary'), staleTime: 30_000, refetchOnMount: 'always', refetchOnWindowFocus: true, refetchInterval: 15_000, refetchIntervalInBackground: true })
  const questionWaitingSummary = useQuery({ queryKey: ['questions', 'navigation-waiting'], queryFn: () => hubApi<{ totalCount: number }>(questionWaitingSummaryPath), staleTime: 30_000, refetchInterval: 60_000, refetchOnWindowFocus: true })
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
  async function logout() { await api('/logout', { method: 'POST' }); window.location.replace(`/?signedOut=${Date.now()}`) }
  // Hover expansion is an overlay state: the layout column stays collapsed so
  // opening and closing the menu does not move the page underneath it.
  const menuCollapsed = !sidebarPinned
  const sidebarHovering = !sidebarPinned && sidebarHoverExpanded
  useEffect(() => {
    document.documentElement.style.setProperty('--rv-font-ui', appearanceFontFamilyCss[appearanceSettings.settings.fontFamily])
    document.documentElement.style.setProperty('--rv-font-scale', String(appearanceFontScale[appearanceSettings.settings.fontSize]))
    const applyTheme = () => {
      document.documentElement.dataset.theme = visualTheme
      document.documentElement.dataset.themeMode = visualTheme
      const palette = appearanceSettings.settings.colors[visualTheme]
      appearanceColorTokenOptions.forEach(({ key }) => document.documentElement.style.setProperty(appearanceColorCssVariable(key), palette[key]))
    }
    applyTheme()
  }, [appearanceSettings.settings.fontFamily, appearanceSettings.settings.fontSize, appearanceSettings.settings.themeMode, appearanceColorsKey, visualTheme])
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
    { label: 'Ana menü', items: [item('/dashboard', 'dashboard', 'Genel bakış', true), item('/products', 'products', 'Ürünler'), item('/orders', 'orders', 'Siparişler', false, orderNavigationCount(orderSummary.data)), item('/returns', 'returns', 'İadeler', false, navigationCounts?.pendingReturns), item('/invoices', 'invoice', 'Faturalar', false, invoiceWorkspaceSummary.data?.dueSoonCount, true, 'faturanın süresi yaklaşıyor', true), item('/questions', 'products', 'Ürün soruları', false, questionWaitingSummary.data?.totalCount, true, 'bekleyen soru'), item('/integrations', 'connect', 'Entegrasyonlar'), item('/mappings/categories', 'layers', 'Eşleştirmeler'), item('/jobs', 'jobs', 'İşlem Takibi')] },
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
      <div className="sidebar-brand-row"><Link className="sidebar-brand-home" to="/dashboard" aria-label="Genel bakışa git"><span className="stitch-brand-mark" aria-hidden="true"><img src="/pack/brand/ravencia-symbol-transparent.png" alt="" /></span><span className="brand wordmark"><strong>Ravencia</strong></span></Link><button type="button" className={`sidebar-pin-toggle ${sidebarPinned ? 'is-pinned' : ''}`} aria-label={sidebarPinned ? 'Menüyü daralt' : 'Menüyü sabitle'} aria-pressed={sidebarPinned} title={sidebarPinned ? 'Menüyü daralt' : 'Menüyü sabitle'} onClick={toggleSidebarPinned}><UiIcon name="pin" size={18} /></button></div>
      <nav aria-label="Ana menü">{navigation}</nav>
      <div className="sidebar-side-bottom"><div className="settings-nav">{item('/settings', 'settings', 'Sistem Ayarları', true)}<button type="button" className="logout-link" aria-label="Oturumu kapat" title="Oturumu kapat" onClick={() => void logout()}>{icon('logout')}<span className="logout-label">Oturumu kapat</span></button></div><div className="sidebar-profile"><span className="sidebar-avatar">{initials}</span><span className="sidebar-profile-copy"><strong>{displayName}</strong></span></div></div>
    </aside>
    <main>
      <header className={`rv-topbar${topbarHidden ? ' is-hidden' : ''}`}><div className="rv-topbar-leading"><button className="rv-mobile-menu-toggle rv-icon-button" type="button" aria-label="Ana menüyü aç" aria-expanded={mobileMenuOpen} onClick={() => setMobileMenuOpen(true)}><UiIcon name="menu" size={22} /></button><div className="rv-breadcrumb"><UiIcon name="layers" size={16} /><span>Operasyon Merkezi</span><UiIcon name="chevronRight" size={14} /><strong>{pageName}</strong></div></div><div className="rv-topbar-actions"><div ref={notificationRef} className="rv-topbar-notifications"><button type="button" className="rv-topbar-notification-trigger" aria-label="Bildirim geçmişini aç" aria-expanded={notificationsOpen} aria-controls="rv-notification-history" onClick={() => setNotificationsOpen(value => !value)}><UiIcon name="bell" size={18} />{unreadNotificationCount > 0 && <span className="rv-topbar-notification-count" aria-label={`${unreadNotificationCount} okunmamış bildirim`}>{unreadNotificationCount > 99 ? '99+' : unreadNotificationCount}</span>}</button></div><button type="button" className="rv-topbar-theme" aria-label={visualTheme === 'light' ? 'Koyu temaya geç' : 'Açık temaya geç'} title={visualTheme === 'light' ? 'Koyu temaya geç' : 'Açık temaya geç'} aria-pressed={visualTheme === 'dark'} onClick={() => setVisualThemePreference(visualTheme === 'light' ? 'dark' : 'light')}><UiIcon name={visualTheme === 'light' ? 'moon' : 'sun'} size={18} /><span>{visualTheme === 'light' ? 'Koyu tema' : 'Açık tema'}</span></button><button className="rv-topbar-search rv-topbar-search-icon" type="button" aria-label="Hızlı aramayı aç" aria-keyshortcuts="Control+k Meta+k" onClick={() => setQuickSearchOpen(true)}><UiIcon name="search" size={20} /></button></div></header>
      {notificationsOpen && createPortal(<div ref={notificationHistoryRef} id="rv-notification-history" className="rv-notification-history" role="dialog" aria-label="Bildirim geçmişi" style={notificationHistoryPosition}><Suspense fallback={null}><NotificationPanel notifications={notifications} onNavigate={href => { setNotificationsOpen(false); navigate(href) }} onCleared={() => setNotifications([])} /></Suspense></div>, document.body)}
      <Modal open={quickSearchOpen} title="Hızlı arama" description="Bir sayfa veya işlem seçin." onClose={() => setQuickSearchOpen(false)}><div className="rv-command-list" role="listbox" aria-label="Hızlı arama sonuçları">{quickSearchItems.map(itemOption => <Link key={itemOption.to} className="rv-command-item" to={itemOption.to} onClick={() => setQuickSearchOpen(false)}><UiIcon name={itemOption.icon} size={20} /><span><strong>{itemOption.label}</strong><small>{itemOption.description}</small></span><UiIcon name="chevronRight" size={16} /></Link>)}</div></Modal>
      <Drawer open={mobileMenuOpen} className="rv-mobile-drawer" title={<img className="rv-drawer-logo" src="/pack/brand/ravencia-wordmark-transparent.png" alt="Ravencia" />} description={null} onClose={() => setMobileMenuOpen(false)}><nav className="rv-mobile-navigation" aria-label="Mobil ana menü">{navigation}{item('/settings', 'settings', 'Sistem Ayarları', true)}<button className="rv-button rv-button-secondary" type="button" onClick={() => void logout()}>Oturumu kapat</button></nav></Drawer>
      <Suspense fallback={<DelayedStatus title="Ekran hazırlanıyor" />}><Routes><Route path="/dashboard" element={<Dashboard />} /><Route path="/products" element={<ProductsPage />} /><Route path="/products/new" element={<NewProductPage />} /><Route path="/products/:id" element={<ProductDetailPage />} /><Route path="/catalog/categories" element={<CategoriesPage />} /><Route path="/catalog/brands" element={<BrandsPage />} /><Route path="/catalog/attributes" element={<AttributesPage />} /><Route path="/imports" element={<ImportsPage />} /><Route path="/imports/:id" element={<ImportDetailPage />} /><Route path="/integrations" element={<IntegrationsPage />} /><Route path="/integrations/:id" element={<IntegrationDetailPage />} /><Route path="/mappings/categories" element={<MappingPage />} /><Route path="/mappings/attributes" element={<AttributeMappingPage />} /><Route path="/mappings/product-matches" element={<Navigate to="/mappings/categories" replace />} /><Route path="/questions" element={<QuestionsPage />} /><Route path="/orders" element={<OrdersPage />} /><Route path="/orders/:id" element={<Navigate to="/orders" replace />} /><Route path="/returns" element={<ReturnsPage />} /><Route path="/returns/:id" element={<ReturnDetailPage />} /><Route path="/shipments" element={<ShipmentsPage />} /><Route path="/shipments/:id" element={<ShipmentDetailPage />} /><Route path="/invoices" element={<InvoicesPage />} /><Route path="/invoices/:id" element={<InvoiceDetailPage />} /><Route path="/jobs" element={<JobsPage me={me} />} /><Route path="/settings/security" element={<Navigate to="/settings?tab=auth" replace />} /><Route path="/settings/appearance" element={<Navigate to="/settings?tab=read" replace />} /><Route path="/settings" element={<SystemSettingsPage />} /><Route path="*" element={<Navigate to="/dashboard" replace />} /></Routes></Suspense>
    </main>
    </div>
}

export function App() {
  const me = useQuery({ queryKey: ['me'], queryFn: () => api<Me>('/me'), retry: false })
  useOperationsRealtime(me.data?.state === 'ACTIVE')
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
  if (me.isError) return <Routes><Route path="*" element={<Login />} /></Routes>
  if (!me.data) return <Status title="Oturum bilgisi alınamadı" />
  if (me.data.state === 'PASSWORD_CHANGE_REQUIRED') return <ChangePassword />
  if (me.data.state === 'MFA_CHALLENGE') return <MfaChallenge />
  if (me.data.state !== 'ACTIVE') return <Status title="Oturum kilitli" detail="Yeniden giriş yapın." />
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

function AuthPageFrame({ ariaLabel, accessLabel, accessMeta, cardTitle, cardDescription, progressStep = 1, children, footerMeta = 'Ravencia Workspace', topAction }: { ariaLabel: string; accessLabel: string; accessMeta: string; cardTitle: string; cardDescription: string; progressStep?: 1 | 2 | 3; children: ReactNode; footerMeta?: string; topAction?: ReactNode }) {
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
      <div className="rv-auth-brand"><img className="rv-auth-symbol" src="/pack/brand/ravencia-symbol-transparent.png" alt="" /><span className="rv-auth-brand-name">Ravencia</span></div>
      <span className="rv-auth-system-status"><i /> Sistem hazır</span>
    </header>
    <section className="rv-auth-stage" aria-label={ariaLabel}>
      <div className="auth-focus-column">
        <div className="rv-auth-hero-brand">
          <img className="rv-auth-hero-symbol" src="/pack/brand/ravencia-symbol-transparent.png" alt="" />
          <img className="rv-auth-hero-wordmark" src="/pack/brand/ravencia-wordmark-transparent.png" alt="Ravencia" />
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

function Login() {
  const navigate = useNavigate()
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
      client.setQueryData(['me'], currentUser)
      navigate('/dashboard', { replace: true })
    } catch (reason) {
      if (reason instanceof ApiRequestError && reason.code === 'TENANT_SELECTION_REQUIRED') { setTenantOptions(reason.tenants ?? []); setError('Bir çalışma alanı seçip girişe devam edin.') }
      else if (reason instanceof ApiRequestError && reason.status === 401) setError('E-posta veya parola hatalı. Art arda başarısız denemelerde hesap 15 dakika geçici olarak kilitlenir.')
      else if (reason instanceof ApiRequestError && reason.status === 429) setError('Çok fazla giriş denemesi yapıldı. Bir dakika bekleyip yeniden deneyin.')
      else if (reason instanceof ApiRequestError && reason.status === 400) setError('Güvenlik doğrulaması tamamlanamadı. Sayfayı yenileyip tekrar deneyin.')
      else setError('Giriş servisine ulaşılamadı. Bağlantınızı kontrol edip yeniden deneyin.')
    } finally {
      setLoading(false)
    }
  }

  return <AuthPageFrame ariaLabel="Ravencia operasyon merkezi girişi" accessLabel="Güvenli erişim" accessMeta="Oturum korumalı" cardTitle="Hesabınıza giriş yapın" cardDescription="Ravencia çalışma alanınıza güvenli erişim.">
          <form className="rv-auth-form" onSubmit={submit}>
            <div className="rv-auth-field"><label htmlFor="login-email">E-posta adresi</label><div className="rv-auth-control"><UiIcon name="mail" size={18} /><input id="login-email" name="email" type="email" value={email} onChange={event => setEmail(event.target.value)} required autoComplete="username" placeholder="ornek@ravencia.com" autoFocus /></div></div>
            <div className="rv-auth-field"><div className="rv-auth-label-row"><label htmlFor="login-password">Parola</label><button type="button" onClick={() => setError('Parola sıfırlama için sistem yöneticinizle iletişime geçin.')}>Parolamı unuttum</button></div><div className="rv-auth-control"><UiIcon name="lock" size={18} /><input id="login-password" name="password" type={showPw ? 'text' : 'password'} required autoComplete="current-password" placeholder="Parolanızı girin" /><button type="button" className="rv-auth-password-toggle" aria-label={showPw ? 'Parolayı gizle' : 'Parolayı göster'} onClick={() => setShowPw(value => !value)}><UiIcon name="eye" size={18} /></button></div></div>
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
    try {
      await api('/change-password', { method: 'POST', body: JSON.stringify({ currentPassword: data.get('current'), newPassword: data.get('next') }) })
      await client.invalidateQueries({ queryKey: ['me'] })
    } catch {
      setError('Parola değiştirilemedi. Mevcut parolanızı ve güvenlik koşullarını kontrol edin.')
    } finally {
      setLoading(false)
    }
  }
  return <AuthPageFrame ariaLabel="Ravencia parola yenileme" accessLabel="Güvenlik adımı" accessMeta="2 / 3" cardTitle="Yeni parolanızı belirleyin" cardDescription="İlk giriş güvenlik adımını tamamlayın." progressStep={2} footerMeta="Parola politikası etkin">
    <form className="rv-auth-form" onSubmit={submit}>
      <div className="rv-auth-field"><label htmlFor="current-password">Geçerli parola</label><div className="rv-auth-control"><UiIcon name="lock" /><input id="current-password" name="current" type="password" autoComplete="current-password" required autoFocus placeholder="Geçerli parolanız" /></div></div>
      <div className="rv-auth-field"><label htmlFor="new-password">Yeni parola</label><div className="rv-auth-control"><UiIcon name="lock" /><input id="new-password" name="next" type="password" autoComplete="new-password" minLength={15} maxLength={64} required placeholder="En az 15 karakter" /></div><small className="rv-auth-hint">15–64 karakter; hesabınıza özel, tahmin edilmesi zor bir parola kullanın.</small></div>
      {error && <div className="rv-auth-error" role="alert"><i /><span>{error}</span></div>}
      <button className="rv-auth-submit" type="submit" disabled={loading}>{loading ? <><i /> Parola güncelleniyor…</> : <><span>Parolayı güncelle</span><UiIcon name="arrowRight" /></>}</button>
    </form>
  </AuthPageFrame>
}

function MfaChallenge() {
  const client = useQueryClient()
  const [mode, setMode] = useState<'code' | 'recovery'>('code')
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(false)
  const [leaving, setLeaving] = useState(false)
  async function backToLogin() {
    setLeaving(true)
    try { await api('/logout', { method: 'POST' }) } finally { window.location.replace(`/?signedOut=${Date.now()}`) }
  }
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setError('')
    setLoading(true)
    const data = new FormData(event.currentTarget)
    try {
      await api('/mfa/challenge', { method: 'POST', body: JSON.stringify({ code: mode === 'code' ? data.get('code') : null, recoveryCode: mode === 'recovery' ? data.get('recovery') : null }) })
      await client.invalidateQueries({ queryKey: ['me'] })
    } catch {
      setError(mode === 'code' ? 'Doğrulama kodu geçersiz veya süresi dolmuş.' : 'Kurtarma kodu geçersiz veya daha önce kullanılmış.')
    } finally {
      setLoading(false)
    }
  }
  return <AuthPageFrame ariaLabel="Ravencia iki adımlı doğrulama" accessLabel="Kimlik doğrulama" accessMeta="3 / 3" cardTitle="Erişiminizi doğrulayın" cardDescription={mode === 'code' ? 'Authenticator uygulamanızdaki 6 haneli kodu girin.' : 'Tek kullanımlık kurtarma kodlarınızdan birini girin.'} progressStep={3} footerMeta="Doğrulama koruması etkin" topAction={<button type="button" className="rv-auth-back" onClick={() => void backToLogin()} disabled={leaving}><UiIcon name="arrowLeft" size={16} />{leaving ? 'Giriş ekranına dönülüyor…' : 'Giriş ekranına dön'}</button>}>
    <div className="rv-auth-choice" role="tablist" aria-label="Doğrulama yöntemi"><button type="button" role="tab" aria-selected={mode === 'code'} className={mode === 'code' ? 'is-active' : ''} onClick={() => { setMode('code'); setError('') }}>Authenticator kodu</button><button type="button" role="tab" aria-selected={mode === 'recovery'} className={mode === 'recovery' ? 'is-active' : ''} onClick={() => { setMode('recovery'); setError('') }}>Kurtarma kodu</button></div>
    <form className="rv-auth-form rv-auth-mfa-form" onSubmit={submit}>
      {mode === 'code' ? <div className="rv-auth-field"><label htmlFor="mfa-code">6 haneli doğrulama kodu</label><div className="rv-auth-control"><UiIcon name="shield" /><input id="mfa-code" name="code" inputMode="numeric" autoComplete="one-time-code" pattern="[0-9]{6}" minLength={6} maxLength={6} required autoFocus placeholder="000000" /></div></div> : <div className="rv-auth-field"><label htmlFor="mfa-recovery">Kurtarma kodu</label><div className="rv-auth-control"><UiIcon name="lock" /><input id="mfa-recovery" name="recovery" autoComplete="off" required autoFocus placeholder="Kurtarma kodunu girin" /></div></div>}
      {error && <div className="rv-auth-error" role="alert"><i /><span>{error}</span></div>}
      <button className="rv-auth-submit" type="submit" disabled={loading}>{loading ? <><i /> Doğrulanıyor…</> : <><span>Erişimi doğrula</span><UiIcon name="arrowRight" /></>}</button>
    </form>
  </AuthPageFrame>
}
type DashboardMetrics = { pendingOrders: number; newAndProcessingOrders?: number; lateOrders: number; todayOrders: number; todayProductQuantity: number; monthOrders: number; monthProductQuantity: number; pendingReturns: number; dueSoonInvoices: number; uninvoicedInvoices: number; lowStockProducts: number; activeConnections: number; pendingByPlatform: Record<string, number>; lateByPlatform?: Record<string, number>; pendingReturnsByPlatform?: Record<string, number>; oldestQueuedJobAt?: string | null; lastVerifiedSynchronizationAt?: string | null; deadJobCount?: number; manualReviewJobCount?: number; recentJobCount?: number; recentRateLimitJobCount?: number; oldestStockObservationAt?: string | null }
type DashboardLowStock = { id: string; title: string; totalStock: number; primaryImageUrl: string | null }
type DashboardSyncStatus = { resourceType: string; label: string; kind: string; status: string; lastAttemptAt: string | null; lastSuccessAt: string | null; lastErrorCode: string | null }
type DashboardBootstrap = { metrics: DashboardMetrics; lowStock: DashboardLowStock[]; sync: DashboardSyncStatus[]; platforms: { name: string; status: string }[]; generatedAt: string; version: number }
type DashboardRevenuePoint = { day: string; amount: number; orderCount: number; productQuantity: number; shipmentCount: number; currency: string }
type DashboardRevenueRange = '7' | '30' | '90' | 'custom'
type DashboardProductSummary = { totalCount: number; activeCount: number; outOfStockCount: number; lowStockCount: number; platforms: string[] }

function dashboardDateKey(value: Date) {
  if (Number.isNaN(value.getTime())) return ''
  const pad = (part: number) => String(part).padStart(2, '0')
  return `${value.getFullYear()}-${pad(value.getMonth() + 1)}-${pad(value.getDate())}`
}

function dashboardDateInputValue(value = new Date()) { return dashboardDateKey(value) }
type DashboardHealthRow = { key: string; title: string; value: string; detail: string; attention: boolean; to?: string; action?: string; when?: string }

const minutesSince = (timestamp: string | null | undefined) => {
  if (!timestamp) return null
  const parsed = new Date(timestamp).getTime()
  return Number.isNaN(parsed) ? null : Math.max(0, (Date.now() - parsed) / 60000)
}

// Integration health in the seller's words: what each line means and whether it needs a look.
function DashboardHealthPanel({ metrics, rateLimitRate, recentJobCount }: { metrics: DashboardMetrics | undefined; rateLimitRate: number | null; recentJobCount: number }) {
  const syncAge = minutesSince(metrics?.lastVerifiedSynchronizationAt)
  const queueAge = minutesSince(metrics?.oldestQueuedJobAt)
  const stockAge = minutesSince(metrics?.oldestStockObservationAt)
  const dead = metrics?.deadJobCount ?? 0
  const review = metrics?.manualReviewJobCount ?? 0
  const limited = metrics?.recentRateLimitJobCount ?? 0
  const rows: DashboardHealthRow[] = [
    { key: 'sync', title: 'Pazaryerleriyle son veri alışverişi', value: dashboardAgeLabel(metrics?.lastVerifiedSynchronizationAt), detail: 'Sipariş, stok ve fiyat bilgisi en son bu zaman pazaryerleriyle doğrulandı.', attention: syncAge === null || syncAge > 120, when: dashboardDateTimeLabel(metrics?.lastVerifiedSynchronizationAt) },
    { key: 'queue', title: 'Sıradaki işlemler', value: queueAge === null ? 'Bekleyen yok' : `En eskisi ${dashboardAgeLabel(metrics?.oldestQueuedJobAt).toLocaleLowerCase('tr-TR')}`, detail: 'Pazaryerine gönderilmeyi bekleyen işlemler. Uzun bekleme gecikme demektir.', attention: queueAge !== null && queueAge > 30, when: queueAge === null ? undefined : dashboardDateTimeLabel(metrics?.oldestQueuedJobAt), to: '/jobs', action: 'İşlemleri gör' },
    { key: 'dead', title: 'Tamamlanamayan işlemler', value: dead.toLocaleString('tr-TR'), detail: dead ? 'Tekrar denenmeyecek. Nedenini İşlem Takibi’nde görüp düzeltin.' : 'Durdurulan işlem yok.', attention: dead > 0, to: '/jobs', action: 'Nedenleri incele' },
    { key: 'review', title: 'Kararınızı bekleyenler', value: review.toLocaleString('tr-TR'), detail: review ? 'Onay ya da düzeltme isteyen işlemler.' : 'Onay bekleyen işlem yok.', attention: review > 0, to: '/jobs', action: 'Karar ver' },
    { key: 'limit', title: 'Pazaryeri hız sınırı', value: rateLimitRate === null ? '—' : `%${rateLimitRate.toLocaleString('tr-TR', { maximumFractionDigits: 1 })}`, detail: `Son 24 saatte ${recentJobCount.toLocaleString('tr-TR')} istekten ${limited.toLocaleString('tr-TR')} tanesi pazaryeri sınırına takıldı.`, attention: (rateLimitRate ?? 0) >= 5 },
    { key: 'stock', title: 'Stok karşılaştırması', value: dashboardAgeLabel(metrics?.oldestStockObservationAt), detail: 'Pazaryerindeki stokların panelle en son karşılaştırıldığı zaman.', attention: stockAge !== null && stockAge > 2 * 24 * 60, when: dashboardDateTimeLabel(metrics?.oldestStockObservationAt) },
  ]
  const issues = rows.filter(row => row.attention).length
  return <section className="panel dashboard-health-panel" aria-labelledby="dashboard-health-title">
    <header className="dashboard-section-heading dashboard-health-heading"><div><h2 id="dashboard-health-title">Entegrasyon durumu</h2><p>Pazaryerleriyle bağlantının son 24 saati</p></div><span className={`dashboard-health-summary${issues ? ' has-issues' : ''}`}><i aria-hidden="true" />{issues ? `${issues} konu dikkatinizi bekliyor` : 'Her şey yolunda'}</span></header>
    <div className="dashboard-health-grid">{rows.map(row => <article key={row.key} className={`dashboard-health-item${row.attention ? ' needs-attention' : ''}`}>
      <span><i aria-hidden="true" />{row.title}</span>
      <strong title={row.when}>{row.value}</strong>
      <small>{row.detail}</small>
      {row.to && row.attention && <Link to={row.to} className="dashboard-health-link">{row.action} <UiIcon name="arrowRight" size={14} /></Link>}
    </article>)}</div>
  </section>
}

function dashboardAgeLabel(timestamp: string | null | undefined) {
  if (!timestamp) return 'Veri yok'
  const parsed = new Date(timestamp)
  if (Number.isNaN(parsed.getTime())) return 'Veri yok'
  const minutes = Math.max(0, Math.floor((Date.now() - parsed.getTime()) / 60000))
  if (minutes < 1) return 'Şimdi'
  if (minutes < 60) return `${minutes} dk önce`
  const hours = Math.floor(minutes / 60)
  if (hours < 48) return `${hours} sa önce`
  return `${Math.floor(hours / 24)} gün önce`
}

function dashboardDateTimeLabel(timestamp: string | null | undefined) {
  if (!timestamp) return 'Henüz ölçüm yok'
  const parsed = new Date(timestamp)
  return Number.isNaN(parsed.getTime()) ? 'Henüz ölçüm yok' : parsed.toLocaleString('tr-TR')
}

function DashboardMetricIcon({ kind }: { kind: string }) {
  const icons: Record<string, UiIconName> = {
    pending: 'clock',
    pendingOrders: 'clock',
    late: 'alert',
    lateOrders: 'truck',
    today: 'calendar',
    month: 'calendar',
    return: 'returns',
    pendingReturns: 'returns',
    invoice: 'invoice',
    uninvoiced: 'invoice',
    invoicePending: 'invoice',
    invoiceDue: 'calendar',
    stock: 'box',
    lowStock: 'warehouse',
    revenue: 'chart',
    orders: 'orders',
    basket: 'cart',
    product: 'products',
  }
  return <span className={`dashboard-metric-icon ${kind}`} aria-hidden="true"><UiIcon name={icons[kind] ?? 'pendingOrders'} size={22} /></span>
}

function DashboardSparkline({ className = '', flip = false }: { className?: string; flip?: boolean }) {
  const path = flip ? 'M1 26 12 19 23 24 34 12 45 16 56 8 67 12 78 3 89 8 99 2' : 'M1 29 12 25 23 29 34 19 45 22 56 11 67 15 78 8 89 11 99 2'
  return <svg className={`dashboard-sparkline ${className}`.trim()} viewBox="0 0 100 36" aria-hidden="true"><path d={path} fill="none" stroke="currentColor" strokeWidth="2" /></svg>
}

type DashboardPlatformShare = { platform: string; color: string; amount: number; orders: number; amountShare: number; orderShare: number }

function DashboardPlatformDistribution({ rows, valueKey, label }: { rows: DashboardPlatformShare[]; valueKey: 'amount' | 'orders'; label: string }) {
  const visibleRows = rows.filter(row => row[valueKey] > 0)
  return <div className="dashboard-summary-platform-list" role="tooltip" aria-label={label}>
    {visibleRows.length ? visibleRows.map(row => <span key={row.platform}><i style={{ background: row.color }} /><span className="dashboard-summary-platform-name" title={row.platform}>{row.platform}</span><b>{valueKey === 'amount' ? dashboardMoney(row.amount) : `${row.orders.toLocaleString('tr-TR')} adet`}</b></span>) : <small>Platform dağılımı yok</small>}
  </div>
}

function DashboardOperationalCard({ kind, label, value, to, platformCounts }: { kind: string; label: string; detail?: string; value: number | null; to: string; platformCounts?: Record<string, number> }) {
  const platformRows = Object.entries(platformCounts ?? {}).filter(([, count]) => count > 0).sort((left, right) => right[1] - left[1])
  return <Link className={`dashboard-operational-card ${kind}`} to={to}><DashboardMetricIcon kind={kind} /><span className="dashboard-operational-copy"><strong>{value === null ? '—' : value.toLocaleString('tr-TR')}</strong><span>{label}</span></span><UiIcon name="arrowRight" size={17} />{platformRows.length ? <div className="dashboard-operational-platform-tooltip" role="tooltip"><div>{platformRows.map(([platform, count]) => <span key={platform}><span title={platform}>{platform}</span><b>{count.toLocaleString('tr-TR')}</b></span>)}</div></div> : null}</Link>
}

function dashboardMoney(amount: number, currency = 'TRY') {
  return new Intl.NumberFormat('tr-TR', { style: 'currency', currency: currency || 'TRY', minimumFractionDigits: 0, maximumFractionDigits: 0 }).format(amount)
}

function dashboardQuantity(value: number) {
  return value.toLocaleString('tr-TR', { maximumFractionDigits: 2 })
}

function dashboardAxisMoney(amount: number, currency = 'TRY') {
  return new Intl.NumberFormat('tr-TR', { style: 'currency', currency: currency || 'TRY', maximumFractionDigits: 0 }).format(amount)
}

function dashboardNiceAxisStep(maxValue: number, targetSteps = 4) {
  const roughStep = Math.max(1, maxValue) / targetSteps
  const magnitude = 10 ** Math.floor(Math.log10(roughStep))
  return Math.ceil(roughStep / magnitude) * magnitude
}

function dashboardRevenueSeries(points: DashboardRevenuePoint[], firstPreviousPoint?: DashboardRevenuePoint) {
  return points.map((point, index) => {
    const date = new Date(point.day)
    const month = date.toLocaleDateString('tr-TR', { month: 'short' }).replace(/\.$/, '')
    const yesterdayPoint = index > 0 ? points[index - 1] : firstPreviousPoint
    return { ...point, key: dashboardDateKey(date), label: index === 0 || date.getDate() === 1 ? `${date.getDate()} ${month}` : String(date.getDate()), fullLabel: date.toLocaleDateString('tr-TR', { day: 'numeric', month: 'long', year: 'numeric' }), yesterdayPoint }
  })
}

function dashboardTooltipDateLabel(day: string) {
  const date = new Date(day)
  return date.toLocaleDateString('tr-TR', { day: 'numeric', month: 'short' }).replace(/\.$/, '')
}

const dashboardChartLeft = 44
const dashboardChartRight = 644
const dashboardChartTop = 30
const dashboardChartBottom = 180
const dashboardChartTooltipWidth = 310
// Keep the foreignObject tall enough for the full information card so its
// visual bottom can remain above the hovered chart point.
const dashboardChartTooltipHeight = 194
const dashboardChartTooltipGap = 26
type DashboardChartViewport = { svgTop: number; svgHeight: number }

function dashboardChartY(amount: number, maxValue: number) {
  const ratio = Math.min(1, Math.max(0, amount / Math.max(1, maxValue)))
  return dashboardChartBottom - ratio * (dashboardChartBottom - dashboardChartTop)
}

function dashboardChartX(index: number, pointCount: number) {
  const denominator = Math.max(1, pointCount - 1)
  return dashboardChartLeft + (index / denominator) * (dashboardChartRight - dashboardChartLeft)
}

function dashboardLinePath(points: Array<{ amount: number }>, maxValue: number) {
  if (!points.length) return ''
  return points.map((point, index) => {
    const x = dashboardChartX(index, points.length)
    return `${index === 0 ? 'M' : 'L'} ${x.toFixed(2)} ${dashboardChartY(point.amount, maxValue).toFixed(2)}`
  }).join(' ')
}

function dashboardAreaPath(points: Array<{ amount: number }>, maxValue: number) {
  const line = dashboardLinePath(points, maxValue)
  return line ? `${line} L ${dashboardChartRight} ${dashboardChartBottom} L ${dashboardChartLeft} ${dashboardChartBottom} Z` : ''
}

function DashboardChartPoints({ points, maxValue, currency, chartViewport }: { points: ReturnType<typeof dashboardRevenueSeries>; maxValue: number; currency: string; chartViewport: DashboardChartViewport | null }) {
  return <>{points.map((point, index) => {
    const x = dashboardChartX(index, points.length)
    const y = dashboardChartY(point.amount, maxValue)
    const tooltipX = Math.min(Math.max(x - dashboardChartTooltipWidth / 2, dashboardChartLeft), dashboardChartRight - dashboardChartTooltipWidth)
    const chartScale = chartViewport ? chartViewport.svgHeight / 204 : 0
    const pointViewportY = chartViewport ? chartViewport.svgTop + y * chartScale : 0
    const tooltipViewportHeight = dashboardChartTooltipHeight * chartScale
    const tooltipViewportGap = dashboardChartTooltipGap * chartScale
    const tooltipFitsAbove = chartViewport ? pointViewportY - tooltipViewportHeight - tooltipViewportGap >= 12 : false
    const tooltipY = tooltipFitsAbove ? y - dashboardChartTooltipHeight - dashboardChartTooltipGap : y + dashboardChartTooltipGap
    const productQuantity = point.productQuantity ?? 0
    const shipmentCount = point.shipmentCount ?? 0
    const yesterdayPoint = index > 0 ? points[index - 1] : point.yesterdayPoint
    const yesterdayProductQuantity = yesterdayPoint?.productQuantity ?? 0
    const yesterdayShipmentCount = yesterdayPoint?.shipmentCount ?? 0
    const yesterdayLabel = yesterdayPoint ? dashboardTooltipDateLabel(yesterdayPoint.day) : 'Veri yok'
    const yesterdayDescription = yesterdayPoint ? `${yesterdayPoint.orderCount} sipariş, ${dashboardQuantity(yesterdayProductQuantity)} adet ürün, ${yesterdayShipmentCount} paket, ${dashboardMoney(yesterdayPoint.amount, currency)} ciro` : 'veri yok'
    return <g className="dashboard-chart-point" key={point.key} tabIndex={0} role="group" aria-label={`${point.fullLabel}: ${point.orderCount} sipariş, ${dashboardQuantity(productQuantity)} adet ürün, ${shipmentCount} paket, ${dashboardMoney(point.amount, currency)} ciro; Öncesi gün: ${yesterdayDescription}`}>
      <circle className="dashboard-chart-point-hit" cx={x} cy={y} r="12" />
      <circle className="dashboard-chart-point-dot" cx={x} cy={y} r="3" />
      <foreignObject className="dashboard-chart-point-tooltip" x={tooltipX} y={tooltipY} width={dashboardChartTooltipWidth} height={dashboardChartTooltipHeight}>
        <div className="dashboard-chart-tooltip">
          <strong>{point.fullLabel}</strong>
          <div className="dashboard-chart-tooltip-compare-head"><span aria-hidden="true" /><span>Seçilen gün</span><span>{yesterdayLabel}</span></div>
          <div className="dashboard-chart-tooltip-compare-row"><span>Sipariş</span><b>{point.orderCount.toLocaleString('tr-TR')}</b><b>{yesterdayPoint ? yesterdayPoint.orderCount.toLocaleString('tr-TR') : '—'}</b></div>
          <div className="dashboard-chart-tooltip-compare-row"><span>Ürün</span><b>{dashboardQuantity(productQuantity)} adet</b><b>{yesterdayPoint ? `${dashboardQuantity(yesterdayProductQuantity)} adet` : '—'}</b></div>
          <div className="dashboard-chart-tooltip-compare-row"><span>Kargo</span><b>{shipmentCount.toLocaleString('tr-TR')} paket</b><b>{yesterdayPoint ? `${yesterdayShipmentCount.toLocaleString('tr-TR')} paket` : '—'}</b></div>
          <div className="dashboard-chart-tooltip-compare-row"><span>Ciro</span><b>{dashboardMoney(point.amount, currency)}</b><b>{yesterdayPoint ? dashboardMoney(yesterdayPoint.amount, currency) : '—'}</b></div>
        </div>
      </foreignObject>
    </g>
  })}</>
}

function dashboardRangeDates(range: DashboardRevenueRange, from: string, to: string, now: Date) {
  if (range === 'custom') {
    const fromDate = new Date(`${from}T00:00:00`)
    const toDate = new Date(`${to}T23:59:59.999`)
    return fromDate <= toDate ? { start: fromDate, end: toDate } : { start: toDate, end: fromDate }
  }
  const start = new Date(now)
  start.setHours(0, 0, 0, 0)
  start.setDate(start.getDate() - (Number(range) - 1))
  return { start, end: now }
}

function dashboardPreviousRange(start: Date, end: Date) {
  const day = 24 * 60 * 60 * 1000
  const previousEnd = new Date(start.getTime() - day)
  const duration = Math.max(day, end.getTime() - start.getTime())
  return { start: new Date(previousEnd.getTime() - duration), end: previousEnd }
}

function dashboardTrendLabel(current: number, previous: number) {
  if (previous <= 0) return current > 0 ? '%100' : '%0'
  const delta = ((current - previous) / previous) * 100
  const sign = delta < 0 ? '-' : ''
  return `${sign}%${Math.abs(delta).toLocaleString('tr-TR', { maximumFractionDigits: 1 })}`
}

function dashboardTrendClass(current: number, previous: number) {
  return previous > 0 && current < previous ? 'is-negative' : 'is-positive'
}

function DashboardTrendIcon({ current, previous }: { current: number; previous: number }) {
  const isNegative = dashboardTrendClass(current, previous) === 'is-negative'
  return <UiIcon name="trend" className={`dashboard-trend-icon${isNegative ? ' is-negative' : ''}`} size={14} />
}

function Dashboard() {
  const [reportRange, setReportRange] = useState<DashboardRevenueRange>('30')
  const [reportFrom, setReportFrom] = useState(() => dashboardDateInputValue())
  const [reportTo, setReportTo] = useState(() => dashboardDateInputValue())
  const [chartRange, setChartRange] = useState<Exclude<DashboardRevenueRange, 'custom'>>('30')
  const [chartPlatform, setChartPlatform] = useState('ALL')
  const performanceChartRef = useRef<HTMLDivElement>(null)
  const [chartViewport, setChartViewport] = useState<DashboardChartViewport | null>(null)
  const dashboardRefreshOptions = { refetchInterval: 60_000, refetchIntervalInBackground: true, refetchOnWindowFocus: true, staleTime: 30_000 } as const
  const bootstrap = useQuery({ queryKey: ['dashboard-bootstrap'], queryFn: () => hubApi<DashboardBootstrap>('/dashboard/bootstrap'), ...dashboardRefreshOptions })
  const invoiceWorkspaceSummary = useQuery({ queryKey: ['invoice-workspace-summary'], queryFn: () => hubApi<{ dueSoonCount: number; uninvoicedCount?: number }>('/invoice-workspace/summary'), ...dashboardRefreshOptions })
  const productSummary = useQuery({ queryKey: ['products', 'summary'], queryFn: () => hubApi<DashboardProductSummary>('/products/summary'), ...dashboardRefreshOptions })
  const now = new Date()
  const revenuePlatformOptions = Array.from(new Set((bootstrap.data?.platforms ?? []).map(platform => platform.name)))
  const reportDates = dashboardRangeDates(reportRange, reportFrom, reportTo, now)
  const chartDates = dashboardRangeDates(chartRange, reportFrom, reportTo, now)
  const reportPreviousDates = dashboardPreviousRange(reportDates.start, reportDates.end)
  const chartPreviousDates = dashboardPreviousRange(chartDates.start, chartDates.end)
  const revenueRequest = (from: Date, to: Date, platform = 'ALL') => `/dashboard/revenue-series?from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(to.toISOString())}&platform=${encodeURIComponent(platform)}`
  const reportPeriodKey = `${reportRange}:${reportFrom}:${reportTo}`
  const chartPeriodKey = chartRange
  const reportRevenueQuery = useQuery({ queryKey: ['dashboard-revenue-series', reportPeriodKey], queryFn: () => hubApi<DashboardRevenuePoint[]>(revenueRequest(reportDates.start, reportDates.end)), ...dashboardRefreshOptions })
  const previousReportRevenueQuery = useQuery({ queryKey: ['dashboard-revenue-series-previous', reportPeriodKey], queryFn: () => hubApi<DashboardRevenuePoint[]>(revenueRequest(reportPreviousDates.start, reportPreviousDates.end)), ...dashboardRefreshOptions })
  const chartRevenueQuery = useQuery({ queryKey: ['dashboard-chart-revenue-series', chartPeriodKey, chartPlatform], queryFn: () => hubApi<DashboardRevenuePoint[]>(revenueRequest(chartDates.start, chartDates.end, chartPlatform)), ...dashboardRefreshOptions })
  const previousChartRevenueQuery = useQuery({ queryKey: ['dashboard-chart-revenue-series-previous', chartPeriodKey, chartPlatform], queryFn: () => hubApi<DashboardRevenuePoint[]>(revenueRequest(chartPreviousDates.start, chartPreviousDates.end, chartPlatform)), ...dashboardRefreshOptions })
  const channelRevenueQuery = useQuery({
    queryKey: ['dashboard-channel-revenue', reportPeriodKey, revenuePlatformOptions],
    queryFn: async () => Promise.all(revenuePlatformOptions.map(async platform => ({ platform, points: await hubApi<DashboardRevenuePoint[]>(revenueRequest(reportDates.start, reportDates.end, platform)) }))),
    enabled: revenuePlatformOptions.length > 0,
    ...dashboardRefreshOptions,
  })
  const reportSeries = dashboardRevenueSeries(reportRevenueQuery.data ?? [])
  const previousReportSeries = dashboardRevenueSeries(previousReportRevenueQuery.data ?? [])
  const previousChartSeries = dashboardRevenueSeries(previousChartRevenueQuery.data ?? [])
  const chartSeries = dashboardRevenueSeries(chartRevenueQuery.data ?? [], previousChartSeries[previousChartSeries.length - 1])
  const invoiceDueSoonCount = invoiceWorkspaceSummary.data?.dueSoonCount ?? 0
  const reportCurrency = reportSeries.find(item => item.amount > 0)?.currency || reportSeries[0]?.currency || 'TRY'
  const chartCurrency = chartSeries.find(item => item.amount > 0)?.currency || chartSeries[0]?.currency || 'TRY'
  const reportTotal = reportSeries.reduce((sum, item) => sum + item.amount, 0)
  const reportOrderCount = reportSeries.reduce((sum, item) => sum + item.orderCount, 0)
  const previousReportTotal = previousReportSeries.reduce((sum, item) => sum + item.amount, 0)
  const previousReportOrderCount = previousReportSeries.reduce((sum, item) => sum + item.orderCount, 0)
  const averageBasket = reportOrderCount ? reportTotal / reportOrderCount : 0
  const previousAverageBasket = previousReportOrderCount ? previousReportTotal / previousReportOrderCount : 0
  const reportPeriodLabel = reportRange === 'custom' ? 'Özel dönem' : `Son ${reportRange} gün`
  const chartPeriodLabel = `Son ${chartRange} gün`
  const chartMaxAmount = Math.max(1, ...chartSeries.map(item => item.amount), ...previousChartSeries.map(item => item.amount))
  const chartAxisStep = dashboardNiceAxisStep(chartMaxAmount, 5)
  const chartAxisMax = chartAxisStep * 5
  const chartCurrentPath = dashboardLinePath(chartSeries, chartAxisMax)
  const chartPreviousPath = dashboardLinePath(previousChartSeries, chartAxisMax)
  const chartAreaPath = dashboardAreaPath(chartSeries, chartAxisMax)
  const hasChart = Boolean(chartCurrentPath)
  useEffect(() => {
    let frame = 0
    const measureChart = () => {
      const svg = performanceChartRef.current?.querySelector('svg')
      const rect = svg?.getBoundingClientRect()
      if (!rect || rect.height <= 0) {
        setChartViewport(null)
        return
      }
      const nextViewport = { svgTop: rect.top, svgHeight: rect.height }
      setChartViewport(current => current && Math.abs(current.svgTop - nextViewport.svgTop) < 0.5 && Math.abs(current.svgHeight - nextViewport.svgHeight) < 0.5 ? current : nextViewport)
    }
    const scheduleMeasure = () => {
      window.cancelAnimationFrame(frame)
      frame = window.requestAnimationFrame(measureChart)
    }
    scheduleMeasure()
    window.addEventListener('resize', scheduleMeasure)
    window.addEventListener('scroll', scheduleMeasure, { passive: true })
    return () => {
      window.cancelAnimationFrame(frame)
      window.removeEventListener('resize', scheduleMeasure)
      window.removeEventListener('scroll', scheduleMeasure)
    }
  }, [hasChart, chartPlatform, chartRange, chartSeries.length])
  const chartLabelStep = Math.max(1, Math.ceil(Math.max(chartSeries.length, 1) / 6))
  const chartGrid = Array.from({ length: 6 }, (_, index) => {
    const ratio = 1 - index / 5
    return { ratio, y: dashboardChartTop + (1 - ratio) * (dashboardChartBottom - dashboardChartTop) }
  })
  const productCount = productSummary.data?.activeCount ?? 0
  const dashboardMetrics = bootstrap.data?.metrics
  const recentJobCount = dashboardMetrics?.recentJobCount ?? 0
  const recentRateLimitRate = recentJobCount > 0 ? ((dashboardMetrics?.recentRateLimitJobCount ?? 0) / recentJobCount) * 100 : null
  const channelRows = (channelRevenueQuery.data ?? []).map(channel => {
    const amount = channel.points.reduce((sum, point) => sum + point.amount, 0)
    const orders = channel.points.reduce((sum, point) => sum + point.orderCount, 0)
    return { ...channel, amount, orders }
  }).filter(channel => channel.amount > 0 || channel.orders > 0)
  const channelTotalAmount = channelRows.reduce((sum, channel) => sum + channel.amount, 0)
  const channelTotalOrders = channelRows.reduce((sum, channel) => sum + channel.orders, 0)
  const channelBasis = channelTotalAmount > 0 ? channelTotalAmount : channelTotalOrders
  const channelColors = ['#9b7aff', '#59d6bd', '#79a4ff', '#f0b15a']
  const channelRowsWithShare = channelRows.map((channel, index) => ({ ...channel, share: channelBasis > 0 ? ((channelTotalAmount > 0 ? channel.amount : channel.orders) / channelBasis) * 100 : 0, color: channelColors[index % channelColors.length] }))
  const summaryPlatformRows = channelRowsWithShare.map(channel => ({ ...channel, amountShare: channelTotalAmount > 0 ? (channel.amount / channelTotalAmount) * 100 : 0, orderShare: channelTotalOrders > 0 ? (channel.orders / channelTotalOrders) * 100 : 0 }))
  const channelGradient = channelRowsWithShare.length ? `conic-gradient(${channelRowsWithShare.map((channel, index) => { const start = channelRowsWithShare.slice(0, index).reduce((sum, value) => sum + value.share, 0); return `${channel.color} ${start}% ${start + channel.share}%` }).join(', ')})` : 'conic-gradient(var(--rv-color-surface-soft) 0 100%)'
  const errors = [bootstrap.error, productSummary.error, reportRevenueQuery.error, previousReportRevenueQuery.error, chartRevenueQuery.error, previousChartRevenueQuery.error, channelRevenueQuery.error].filter(Boolean)
  function downloadDashboardReport() {
    const escapeCsv = (value: string | number) => `"${String(value).replace(/"/g, '""')}"`
    const rows = [
      ['Tarih', 'Ciro', 'Sipariş', 'Para birimi'],
      ...reportSeries.map(point => [point.fullLabel, point.amount.toLocaleString('tr-TR', { maximumFractionDigits: 2 }), point.orderCount, point.currency]),
    ]
    const csv = `\uFEFF${rows.map(row => row.map(escapeCsv).join(';')).join('\r\n')}`
    const url = URL.createObjectURL(new Blob([csv], { type: 'text/csv;charset=utf-8' }))
    const link = document.createElement('a')
    link.href = url
    link.download = `ravencia-dashboard-${dashboardDateKey(new Date())}.csv`
    link.click()
    window.setTimeout(() => URL.revokeObjectURL(url), 0)
  }
  return <section className="content dashboard" aria-label="Dashboard">
    <div className="page-heading"><div><p className="eyebrow">OPERASYON MERKEZİ</p><h1>Genel bakış</h1></div><div className="dashboard-heading-actions"><MobileActionMenu label="Rapor işlemleri" className="dashboard-report-controls"><label className="dashboard-header-period"><span className="sr-only">Rapor dönemi</span><span className="dashboard-period-control"><UiIcon name="calendar" size={18} /><select aria-label="Rapor dönemi" value={reportRange} onChange={event => setReportRange(event.target.value as DashboardRevenueRange)}><option value="7">Son 7 gün</option><option value="30">Son 30 gün</option><option value="90">Son 90 gün</option><option value="custom">Özel tarih</option></select></span></label><button type="button" className="dashboard-report-download" onClick={downloadDashboardReport} disabled={reportRevenueQuery.isLoading}><UiIcon name="download" size={18} /> Raporu indir</button></MobileActionMenu></div></div>
    {reportRange === 'custom' && <div className="dashboard-custom-range dashboard-report-custom-range"><DateRangePicker from={reportFrom} to={reportTo} onChange={range => { setReportFrom(range.from); setReportTo(range.to) }} label="Rapor Tarihi" /></div>}
    {errors.length > 0 && <div role="alert" className="error">Bazı rapor verileri alınamadı; görünen değerler kısmi olabilir.</div>}
    <section className="dashboard-operational-section" aria-label="Operasyon metrikleri"><div className="dashboard-section-bridge dashboard-section-bridge-operational" aria-hidden="true"><span /><small>OPERASYON METRİKLERİ</small><span /></div><div className="dashboard-operational-grid"><DashboardOperationalCard kind="pendingOrders" label="Bekleyen siparişler" detail="İşleme alınmayı bekliyor" value={bootstrap.isLoading ? null : dashboardMetrics?.pendingOrders ?? 0} platformCounts={dashboardMetrics?.pendingByPlatform} to={dashboardOperationalLinks.pendingOrders} /><DashboardOperationalCard kind="lateOrders" label="Geciken siparişler" detail="Süre aşımı olanlar" value={bootstrap.isLoading ? null : dashboardMetrics?.lateOrders ?? 0} platformCounts={dashboardMetrics?.lateByPlatform} to="/orders" /><DashboardOperationalCard kind="pendingReturns" label="Bekleyen iadeler" detail="İnceleme bekliyor" value={bootstrap.isLoading ? null : dashboardMetrics?.pendingReturns ?? 0} platformCounts={dashboardMetrics?.pendingReturnsByPlatform} to={dashboardOperationalLinks.pendingReturns} /><DashboardOperationalCard kind="invoicePending" label="Bekleyen faturalar" detail="Fatura kesilmesi gerekenler" value={bootstrap.isLoading || invoiceWorkspaceSummary.isLoading ? null : invoiceWorkspaceSummary.data?.uninvoicedCount ?? dashboardMetrics?.uninvoicedInvoices ?? 0} to={dashboardOperationalLinks.pendingInvoices} /><DashboardOperationalCard kind="invoiceDue" label="Yaklaşan faturalar" detail="Vadesi yaklaşanlar" value={bootstrap.isLoading || invoiceWorkspaceSummary.isLoading ? null : invoiceDueSoonCount} to={dashboardOperationalLinks.dueInvoices} /><DashboardOperationalCard kind="lowStock" label="Düşük stok" detail="Yenileme gerektiren ürünler" value={bootstrap.isLoading ? null : dashboardMetrics?.lowStockProducts ?? 0} to="/products" /></div></section>
    <div className="dashboard-section-bridge" aria-hidden="true"><span /><small>PERFORMANS METRİKLERİ</small><span /></div>
    <div className="dashboard-summary-grid">
      <article tabIndex={0} className="dashboard-summary-card dashboard-summary-card-with-platforms"><div className="dashboard-summary-card-head"><span>Toplam gelir</span><DashboardMetricIcon kind="revenue" /></div><div className="dashboard-summary-value-row"><strong>{reportRevenueQuery.isLoading ? '—' : dashboardMoney(reportTotal, reportCurrency)}</strong><span className={`dashboard-summary-trend ${dashboardTrendClass(reportTotal, previousReportTotal)}`}><DashboardTrendIcon current={reportTotal} previous={previousReportTotal} />{dashboardTrendLabel(reportTotal, previousReportTotal)}</span></div><DashboardPlatformDistribution rows={summaryPlatformRows} valueKey="amount" label="Platform gelir tutarları" /><DashboardSparkline className="dashboard-sparkline-primary" /></article>
      <article tabIndex={0} className="dashboard-summary-card dashboard-summary-card-with-platforms"><div className="dashboard-summary-card-head"><span>Toplam sipariş</span><DashboardMetricIcon kind="orders" /></div><div className="dashboard-summary-value-row"><strong>{reportRevenueQuery.isLoading ? '—' : reportOrderCount.toLocaleString('tr-TR')}</strong><span className={`dashboard-summary-trend ${dashboardTrendClass(reportOrderCount, previousReportOrderCount)}`}><DashboardTrendIcon current={reportOrderCount} previous={previousReportOrderCount} />{dashboardTrendLabel(reportOrderCount, previousReportOrderCount)}</span></div><DashboardPlatformDistribution rows={summaryPlatformRows} valueKey="orders" label="Platform sipariş adetleri" /><DashboardSparkline className="dashboard-sparkline-accent" flip /></article>
      <article className="dashboard-summary-card"><div className="dashboard-summary-card-head"><span>Ortalama sepet</span><DashboardMetricIcon kind="basket" /></div><div className="dashboard-summary-value-row"><strong>{reportRevenueQuery.isLoading ? '—' : dashboardMoney(averageBasket, reportCurrency)}</strong><span className={`dashboard-summary-trend ${dashboardTrendClass(averageBasket, previousAverageBasket)}`}><DashboardTrendIcon current={averageBasket} previous={previousAverageBasket} />{dashboardTrendLabel(averageBasket, previousAverageBasket)}</span></div><DashboardSparkline className="dashboard-sparkline-blue" /></article>
      <article className="dashboard-summary-card dashboard-summary-card-compact"><div className="dashboard-summary-card-head"><span>Aktif ürün</span><DashboardMetricIcon kind="product" /></div><strong>{productSummary.isLoading ? '—' : productCount.toLocaleString('tr-TR')}</strong><DashboardSparkline className="dashboard-sparkline-green" flip /></article>
    </div>
    <div className="dashboard-section-bridge dashboard-section-bridge-detail" aria-hidden="true"><span /><small>DETAYLI GÖRÜNÜM</small><span /></div>
    <div className="dashboard-performance-grid">
      <article className="panel dashboard-performance-card"><header className="dashboard-card-header"><div><h2>Gelir performansı</h2><p>Satışlarınızın büyük resmini görün.</p></div><div className="dashboard-performance-filters"><label className="dashboard-chart-period"><span>Dönem</span><select aria-label="Gelir performansı dönemi" value={chartRange} onChange={event => setChartRange(event.target.value as Exclude<DashboardRevenueRange, 'custom'>)}><option value="7">Son 7 gün</option><option value="30">Son 30 gün</option><option value="90">Son 90 gün</option></select></label><label className="dashboard-platform-filter"><span>Platform</span><select aria-label="Gelir platformu" value={chartPlatform} onChange={event => setChartPlatform(event.target.value)}><option value="ALL">Tüm platformlar</option>{revenuePlatformOptions.map(platform => <option value={platform} key={platform}>{platform}</option>)}</select></label></div></header><div className="dashboard-performance-toolbar"><div className="dashboard-performance-total"><span>{chartPlatform === 'ALL' ? chartPeriodLabel : `${chartPeriodLabel} · ${chartPlatform}`}</span><strong>{chartRevenueQuery.isLoading ? '—' : dashboardMoney(chartSeries.reduce((sum, item) => sum + item.amount, 0), chartCurrency)}</strong><small className={`dashboard-summary-trend ${dashboardTrendClass(chartSeries.reduce((sum, item) => sum + item.amount, 0), previousChartSeries.reduce((sum, item) => sum + item.amount, 0))}`}><DashboardTrendIcon current={chartSeries.reduce((sum, item) => sum + item.amount, 0)} previous={previousChartSeries.reduce((sum, item) => sum + item.amount, 0)} />{dashboardTrendLabel(chartSeries.reduce((sum, item) => sum + item.amount, 0), previousChartSeries.reduce((sum, item) => sum + item.amount, 0))}</small></div></div><div ref={performanceChartRef} className="dashboard-performance-chart" aria-label={`${chartPeriodLabel} gelir performansı grafiği`} >{chartCurrentPath ? <svg viewBox="0 0 652 204" role="img" aria-label="Gelir performansı"><defs><linearGradient id="dashboard-performance-area" x1="0" x2="0" y1="0" y2="1"><stop offset="0%" stopColor="var(--rv-color-primary)" stopOpacity=".34" /><stop offset="100%" stopColor="var(--rv-color-primary)" stopOpacity="0" /></linearGradient></defs>{chartGrid.map(tick => <g key={tick.y}><line className="dashboard-chart-grid" x1={dashboardChartLeft} x2={dashboardChartRight} y1={tick.y} y2={tick.y} /><text className="dashboard-chart-y" x="38" y={tick.y + 4} textAnchor="end">{dashboardAxisMoney(chartAxisMax * tick.ratio, chartCurrency)}</text></g>)}{chartAreaPath && <path className="dashboard-performance-area" d={chartAreaPath} fill="url(#dashboard-performance-area)" />}{chartPreviousPath && <path className="dashboard-performance-previous" d={chartPreviousPath} />}{<path className="dashboard-performance-line" d={chartCurrentPath} />}<DashboardChartPoints points={chartSeries} maxValue={chartAxisMax} currency={chartCurrency} chartViewport={chartViewport} /></svg> : <div className="dashboard-performance-empty">Bu dönem için gelir verisi bulunmuyor.</div>}<div className="dashboard-performance-x-axis" aria-hidden="true">{chartSeries.map((point, index) => index === 0 || index === chartSeries.length - 1 || index % chartLabelStep === 0 ? <span key={point.key} style={{ left: `${chartSeries.length > 1 ? (index / (chartSeries.length - 1)) * 100 : 0}%` }}>{point.label}</span> : null)}</div></div><footer className="dashboard-chart-legend"><span><i className="current" />Bu dönem</span><span><i className="previous" />Önceki dönem</span></footer></article>
      <article className="panel dashboard-channel-card"><header className="dashboard-card-header"><div><h2>Satış kanalları</h2><p>Seçilen dönemin gelir dağılımı</p></div><Link className="dashboard-panel-link" to="/integrations">Kanalları yönet <UiIcon name="arrowRight" /></Link></header><div className="dashboard-channel-donut" style={{ background: channelGradient }}><div><span>Toplam sipariş</span><strong>{channelTotalOrders.toLocaleString('tr-TR')}</strong><small>{reportPeriodLabel}</small></div></div><div className="dashboard-channel-list">{channelRowsWithShare.length ? channelRowsWithShare.map(channel => <div key={channel.platform}><span><i style={{ background: channel.color }} />{channel.platform}</span><strong>%{channel.share.toLocaleString('tr-TR', { maximumFractionDigits: 1 })}</strong></div>) : <p className="dashboard-channel-empty">Bu dönem için kanal dağılımı bulunmuyor.</p>}</div><footer className="dashboard-channel-footer"><span>{channelRowsWithShare.length} kanal · {reportPeriodLabel}</span><Link to="/integrations">Bağlantıları yönet <UiIcon name="arrowRight" /></Link></footer></article>
    </div>
    <DashboardHealthPanel metrics={dashboardMetrics} rateLimitRate={recentRateLimitRate} recentJobCount={recentJobCount} />
  </section>
}
function Status({ title, detail }: { title: string; detail?: string }) {
  return (
    <div className="rv-splash-screen" role="status" aria-live="polite">
      <div className="rv-splash-card">
        <div className="rv-splash-brand" aria-label="Ravencia"><img className="rv-splash-symbol" src="/pack/brand/ravencia-symbol-transparent.png" alt="" /><img className="rv-splash-wordmark" src="/pack/brand/ravencia-wordmark-transparent.png" alt="Ravencia MarketplaceHub" /></div>
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
        <small className="rv-splash-detail">Güvenli bağlantı kuruluyor</small>
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
