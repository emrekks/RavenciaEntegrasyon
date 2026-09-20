import { Suspense, useEffect, useRef, useState, type CSSProperties, type DragEvent, type FormEvent, type PointerEvent as ReactPointerEvent, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { Link, Navigate, NavLink, Route, Routes, useLocation, useNavigate, useSearchParams } from 'react-router'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api, ApiRequestError, hubApi, type Me, type TenantOption } from '../shared/api'
import { Drawer, Modal, Tabs, Toast, UiIcon, type UiIconName } from '../shared/components'
import { AttributesPage, AttributeMappingPage, BrandsPage, CategoriesPage, ImportDetailPage, ImportsPage, NewProductPage, ProductDetailPage, ProductsPage, IntegrationDetailPage, IntegrationsPage, MappingPage, OrdersPage, ReturnDetailPage, ReturnsPage, ShipmentDetailPage, ShipmentsPage, InvoiceDetailPage, InvoicesPage, JobsPage } from './route-components'
import { useOperationsRealtime } from './hooks/useOperationsRealtime'
import { code128Bars, defaultShippingLabelBlockPosition, defaultShippingLabelSettings, shippingLabelBlockCatalog, shippingLabelFields, useShippingLabelSettings, type ShippingLabelAlignment, type ShippingLabelBlock, type ShippingLabelBlockKind, type ShippingLabelField, type ShippingLabelSettings } from '../features/shipping'
import { appearanceColorCssVariable, appearanceColorTokenOptions, appearanceFontFamilyCss, appearanceFontFamilyOptions, appearanceFontScale, appearanceFontSizeOptions, defaultAppearanceColorTheme, defaultAppearanceSettings, defaultLightPalette, useAppearanceSettings, type AppearanceColorThemeProfile, type AppearanceSettings } from '../features/settings/appearance-settings'
import { clearNotificationHistory, readNotificationHistory, subscribeNotificationHistory, type AppNotification } from '../shared/notifications'

type VisualTheme = 'light' | 'dark'
const visualThemeChangeEvent = 'ravencia:visual-theme-change'

function readVisualThemePreference(): VisualTheme {
  return localStorage.getItem('ravencia.visualTheme') === 'light' ? 'light' : 'dark'
}

function setVisualThemePreference(theme: VisualTheme) {
  localStorage.setItem('ravencia.visualTheme', theme)
  window.dispatchEvent(new CustomEvent<VisualTheme>(visualThemeChangeEvent, { detail: theme }))
}

function Shell({ me }: { me: Me }) {
  const appearanceSettings = useAppearanceSettings()
  const location = useLocation()
  const navigationSummary = useQuery({ queryKey: ['dashboard-bootstrap'], queryFn: () => hubApi<DashboardBootstrap>('/dashboard/bootstrap'), staleTime: 30_000, refetchOnWindowFocus: true })
  const invoiceWorkspaceSummary = useQuery({ queryKey: ['invoice-workspace'], queryFn: () => hubApi<Array<{ isDueSoon: boolean }>>('/invoice-workspace'), staleTime: 30_000, refetchOnWindowFocus: true })
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
      const panelWidth = Math.min(380, window.innerWidth - 32)
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
      const palette = visualTheme === 'light' ? defaultLightPalette : appearanceSettings.settings.colors.dark
      appearanceColorTokenOptions.forEach(({ key }) => document.documentElement.style.setProperty(appearanceColorCssVariable(key), palette[key]))
    }
    applyTheme()
  }, [appearanceSettings.settings.fontFamily, appearanceSettings.settings.fontSize, appearanceSettings.settings.themeMode, appearanceColorsKey, visualTheme])
  const pageNames: Record<string, string> = { '/dashboard': 'Genel bakış', '/products': 'Ürünler', '/products/new': 'Yeni ürün', '/catalog/categories': 'Kategoriler', '/catalog/brands': 'Markalar', '/catalog/attributes': 'Özellikler', '/imports': 'İçe aktarımlar', '/shipments': 'Gönderiler', '/orders': 'Siparişler', '/returns': 'İadeler', '/invoices': 'Faturalar', '/jobs': 'İşlem takibi', '/integrations': 'Platformlar', '/mappings/categories': 'Eşleştirme ayarları', '/mappings/attributes': 'Özellik eşlemeleri', '/settings': 'Sistem ayarları', '/settings/appearance': 'Görünüm ayarları' }
  const pageName = pageNames[location.pathname] ?? (location.pathname.startsWith('/products/') ? 'Ürün detayları' : location.pathname.startsWith('/returns/') ? 'İade detayları' : location.pathname.startsWith('/imports/') ? 'İçe aktarma ayrıntıları' : location.pathname.startsWith('/integrations/') ? 'Platform ayrıntıları' : location.pathname.startsWith('/shipments/') ? 'Gönderi ayrıntıları' : 'Ravencia')
  const displayName = me.displayName || me.email
  const initials = displayName.trim().split(/\s+/).map(part => part[0]).join('').slice(0, 2).toLocaleUpperCase('tr-TR') || 'RA'
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
  const invoiceDueSoonCount = invoiceWorkspaceSummary.data ? invoiceWorkspaceSummary.data.filter(item => item.isDueSoon).length : navigationCounts?.dueSoonInvoices ?? 0
  const item = (to: string, iconName: UiIconName, label: string, end = false, count?: number, showZeroCount = false) => {
    const hasCount = typeof count === 'number' && (count > 0 || showZeroCount)
    const accessibleCount = typeof count === 'number' && count > 999 ? '999 üzeri' : count?.toLocaleString('tr-TR')
    const visibleCount = typeof count === 'number' && count > 999 ? '999' : count?.toLocaleString('tr-TR')
    const accessibleLabel = hasCount ? `${label}, ${accessibleCount} bildirim` : label
    return <NavLink to={to} end={end} aria-label={accessibleLabel}>{icon(iconName)}<span className="nav-label">{label}</span>{hasCount && <span className="nav-count" aria-hidden="true">{visibleCount}</span>}</NavLink>
  }
  const navigationGroups: Array<{ label: string; items: ReactNode[] }> = [
    { label: 'Ana menü', items: [item('/dashboard', 'dashboard', 'Genel bakış', true), item('/products', 'products', 'Ürünler'), item('/orders', 'orders', 'Siparişler', false, navigationCounts?.newAndProcessingOrders ?? navigationCounts?.pendingOrders, true), item('/returns', 'returns', 'İadeler', false, navigationCounts?.pendingReturns ?? 0, true), item('/invoices', 'invoice', 'Faturalar', false, invoiceDueSoonCount, true), item('/integrations', 'connect', 'Entegrasyonlar'), item('/jobs', 'jobs', 'İşlem takibi'), item('/mappings/categories', 'layers', 'Eşleştirmeler')] },
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
      <div className="sidebar-brand-row"><div className="stitch-brand-mark" aria-hidden="true"><img src="/pack/brand/ravencia-symbol-transparent.png" alt="" /></div><div className="brand wordmark"><strong>Ravencia</strong></div><button type="button" className={`sidebar-pin-toggle ${sidebarPinned ? 'is-pinned' : ''}`} aria-label={sidebarPinned ? 'Menüyü daralt' : 'Menüyü sabitle'} aria-pressed={sidebarPinned} title={sidebarPinned ? 'Menüyü daralt' : 'Menüyü sabitle'} onClick={toggleSidebarPinned}><UiIcon name="pin" size={18} /></button></div>
      <nav aria-label="Ana menü">{navigation}</nav>
      <div className="sidebar-side-bottom"><div className="settings-nav">{item('/settings', 'settings', 'Sistem Ayarları', true)}<button type="button" className="logout-link" aria-label="Oturumu kapat" title="Oturumu kapat" onClick={() => void logout()}>{icon('logout')}<span className="logout-label">Oturumu kapat</span></button></div><div className="sidebar-profile"><span className="sidebar-avatar">{initials}</span><span className="sidebar-profile-copy"><strong>{displayName}</strong></span></div></div>
    </aside>
    <main>
      <header className="rv-topbar"><div className="rv-topbar-leading"><button className="rv-mobile-menu-toggle rv-icon-button" type="button" aria-label="Ana menüyü aç" aria-expanded={mobileMenuOpen} onClick={() => setMobileMenuOpen(true)}><UiIcon name="menu" size={22} /></button><div className="rv-breadcrumb"><UiIcon name="layers" size={16} /><span>Operasyon Merkezi</span><UiIcon name="chevronRight" size={14} /><strong>{pageName}</strong></div></div><div className="rv-topbar-actions"><div ref={notificationRef} className="rv-topbar-notifications"><button type="button" className="rv-topbar-notification-trigger" aria-label="Bildirim geçmişini aç" aria-expanded={notificationsOpen} aria-controls="rv-notification-history" onClick={() => setNotificationsOpen(value => !value)}><UiIcon name="bell" size={18} />{notifications.length > 0 && <span className="rv-topbar-notification-count" aria-label={`${notifications.length} bildirim`}>{notifications.length > 99 ? '99+' : notifications.length}</span>}</button></div><button type="button" className="rv-topbar-theme" aria-label={visualTheme === 'light' ? 'Koyu temaya geç' : 'Açık temaya geç'} title={visualTheme === 'light' ? 'Koyu temaya geç' : 'Açık temaya geç'} aria-pressed={visualTheme === 'dark'} onClick={() => setVisualThemePreference(visualTheme === 'light' ? 'dark' : 'light')}><UiIcon name={visualTheme === 'light' ? 'moon' : 'sun'} size={18} /><span>{visualTheme === 'light' ? 'Koyu tema' : 'Açık tema'}</span></button><button className="rv-topbar-search rv-topbar-search-icon" type="button" aria-label="Hızlı aramayı aç" aria-keyshortcuts="Control+k Meta+k" onClick={() => setQuickSearchOpen(true)}><UiIcon name="search" size={20} /></button></div></header>
      {notificationsOpen && createPortal(<div ref={notificationHistoryRef} id="rv-notification-history" className="rv-notification-history" role="dialog" aria-label="Bildirim geçmişi" style={notificationHistoryPosition}><div className="rv-notification-history-heading"><strong>Bildirim geçmişi</strong><button type="button" className="rv-notification-clear" onClick={() => { clearNotificationHistory(); setNotifications([]) }} disabled={!notifications.length}>Temizle</button></div>{notifications.length ? <div className="rv-notification-history-list">{notifications.map(notification => <article className={`rv-notification-history-item is-${notification.kind}`} key={notification.id}><span className="rv-notification-history-icon"><UiIcon name={notification.kind === 'error' ? 'alert' : 'check'} size={14} /></span><div><p>{notification.message}</p><time dateTime={notification.createdAt}>{new Date(notification.createdAt).toLocaleTimeString('tr-TR', { hour: '2-digit', minute: '2-digit' })}</time></div></article>)}</div> : <p className="rv-notification-history-empty">Henüz kaydedilmiş bildirim yok.</p>}</div>, document.body)}
      <Modal open={quickSearchOpen} title="Hızlı arama" description="Bir sayfa veya işlem seçin." onClose={() => setQuickSearchOpen(false)}><div className="rv-command-list" role="listbox" aria-label="Hızlı arama sonuçları">{quickSearchItems.map(itemOption => <Link key={itemOption.to} className="rv-command-item" to={itemOption.to} onClick={() => setQuickSearchOpen(false)}><UiIcon name={itemOption.icon} size={20} /><span><strong>{itemOption.label}</strong><small>{itemOption.description}</small></span><UiIcon name="chevronRight" size={16} /></Link>)}</div></Modal>
      <Drawer open={mobileMenuOpen} title="Ravencia" description="Operasyon merkezi" onClose={() => setMobileMenuOpen(false)}><nav className="rv-mobile-navigation" aria-label="Mobil ana menü">{navigation}{item('/settings', 'settings', 'Sistem Ayarları', true)}<button className="rv-button rv-button-secondary" type="button" onClick={() => void logout()}>Oturumu kapat</button></nav></Drawer>
      <Suspense fallback={<DelayedStatus title="Ekran hazırlanıyor" />}><Routes><Route path="/dashboard" element={<Dashboard />} /><Route path="/products" element={<ProductsPage />} /><Route path="/products/new" element={<NewProductPage />} /><Route path="/products/:id" element={<ProductDetailPage />} /><Route path="/catalog/categories" element={<CategoriesPage />} /><Route path="/catalog/brands" element={<BrandsPage />} /><Route path="/catalog/attributes" element={<AttributesPage />} /><Route path="/imports" element={<ImportsPage />} /><Route path="/imports/:id" element={<ImportDetailPage />} /><Route path="/integrations" element={<IntegrationsPage />} /><Route path="/integrations/:id" element={<IntegrationDetailPage />} /><Route path="/mappings/categories" element={<MappingPage />} /><Route path="/mappings/attributes" element={<AttributeMappingPage />} /><Route path="/orders" element={<OrdersPage />} /><Route path="/orders/:id" element={<Navigate to="/orders" replace />} /><Route path="/returns" element={<ReturnsPage />} /><Route path="/returns/:id" element={<ReturnDetailPage />} /><Route path="/shipments" element={<ShipmentsPage />} /><Route path="/shipments/:id" element={<ShipmentDetailPage />} /><Route path="/invoices" element={<InvoicesPage />} /><Route path="/invoices/:id" element={<InvoiceDetailPage />} /><Route path="/jobs" element={<JobsPage me={me} />} /><Route path="/settings/security" element={<Navigate to="/settings?tab=security" replace />} /><Route path="/settings/appearance" element={<AppearanceSettingsPage />} /><Route path="/settings" element={<Security />} /><Route path="*" element={<Navigate to="/dashboard" replace />} /></Routes></Suspense>
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
  return new Intl.NumberFormat('tr-TR', { style: 'currency', currency: currency || 'TRY', maximumFractionDigits: 2 }).format(amount)
}

function dashboardQuantity(value: number) {
  return value.toLocaleString('tr-TR', { maximumFractionDigits: 2 })
}

function dashboardAxisMoney(amount: number, currency = 'TRY') {
  const value = Math.abs(Math.round(amount)); const sign = amount < 0 ? '-' : ''; const prefix = currency === 'TRY' ? '₺' : `${currency || 'TRY'} `
  if (value >= 1_000_000) return `${sign}${prefix}${(value / 1_000_000).toLocaleString('tr-TR', { maximumFractionDigits: 2 })}m`
  if (value >= 10_000) return `${sign}${prefix}${(value / 1_000).toLocaleString('tr-TR', { maximumFractionDigits: 1 })}k`
  return `${sign}${prefix}${value.toLocaleString('tr-TR')}`
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
  const productSummary = useQuery({ queryKey: ['products', 'summary'], queryFn: () => hubApi<DashboardProductSummary>('/products/summary'), ...dashboardRefreshOptions })
  const invoiceWorkspaceSummary = useQuery({ queryKey: ['invoice-workspace'], queryFn: () => hubApi<Array<{ isDueSoon: boolean }>>('/invoice-workspace'), ...dashboardRefreshOptions })
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
  const invoiceDueSoonCount = invoiceWorkspaceSummary.data?.filter(item => item.isDueSoon).length ?? 0
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
    <div className="page-heading"><div><p className="eyebrow">OPERASYON MERKEZİ</p><h1>Genel bakış</h1><p className="lede">Satış, sipariş ve operasyon akışınızı tek ekrandan takip edin.</p></div><div className="dashboard-heading-actions"><div className="dashboard-report-controls"><label className="dashboard-header-period"><span className="sr-only">Rapor dönemi</span><span className="dashboard-period-control"><UiIcon name="calendar" size={18} /><select aria-label="Rapor dönemi" value={reportRange} onChange={event => setReportRange(event.target.value as DashboardRevenueRange)}><option value="7">Son 7 gün</option><option value="30">Son 30 gün</option><option value="90">Son 90 gün</option><option value="custom">Özel tarih</option></select></span></label><button type="button" className="dashboard-report-download" onClick={downloadDashboardReport} disabled={reportRevenueQuery.isLoading}><UiIcon name="download" size={18} /> Raporu indir</button></div></div></div>
    {reportRange === 'custom' && <div className="dashboard-custom-range dashboard-report-custom-range"><label><span>Başlangıç</span><input type="date" value={reportFrom} max={reportTo} onChange={event => setReportFrom(event.target.value)} /></label><label><span>Bitiş</span><input type="date" value={reportTo} min={reportFrom} onChange={event => setReportTo(event.target.value)} /></label></div>}
    {errors.length > 0 && <div role="alert" className="error">Bazı rapor verileri alınamadı; görünen değerler kısmi olabilir.</div>}
    <section className="dashboard-operational-section" aria-label="Operasyon metrikleri"><div className="dashboard-section-bridge dashboard-section-bridge-operational" aria-hidden="true"><span /><small>OPERASYON METRİKLERİ</small><span /></div><div className="dashboard-operational-grid"><DashboardOperationalCard kind="pendingOrders" label="Bekleyen siparişler" detail="İşleme alınmayı bekliyor" value={bootstrap.isLoading ? null : dashboardMetrics?.pendingOrders ?? 0} platformCounts={dashboardMetrics?.pendingByPlatform} to="/orders?status=NEW" /><DashboardOperationalCard kind="lateOrders" label="Geciken siparişler" detail="Süre aşımı olanlar" value={bootstrap.isLoading ? null : dashboardMetrics?.lateOrders ?? 0} platformCounts={dashboardMetrics?.lateByPlatform} to="/orders" /><DashboardOperationalCard kind="pendingReturns" label="Bekleyen iadeler" detail="İnceleme bekliyor" value={bootstrap.isLoading ? null : dashboardMetrics?.pendingReturns ?? 0} platformCounts={dashboardMetrics?.pendingReturnsByPlatform} to="/returns" /><DashboardOperationalCard kind="invoicePending" label="Bekleyen faturalar" detail="Fatura kesilmesi gerekenler" value={bootstrap.isLoading ? null : dashboardMetrics?.uninvoicedInvoices ?? 0} to="/invoices" /><DashboardOperationalCard kind="invoiceDue" label="Yaklaşan faturalar" detail="Vadesi yaklaşanlar" value={bootstrap.isLoading || invoiceWorkspaceSummary.isLoading ? null : invoiceDueSoonCount} to="/invoices" /><DashboardOperationalCard kind="lowStock" label="Düşük stok" detail="Yenileme gerektiren ürünler" value={bootstrap.isLoading ? null : dashboardMetrics?.lowStockProducts ?? 0} to="/products" /></div></section>
    <section className="panel dashboard-health-panel" aria-labelledby="dashboard-health-title"><header className="dashboard-section-heading"><div><h2 id="dashboard-health-title">İşlem sağlığı</h2><p>Kuyruk, senkronizasyon ve gözlem verileri · son 24 saat</p></div></header><div className="dashboard-health-grid"><article className="dashboard-health-item"><span>Kuyruk yaşı</span><strong>{dashboardAgeLabel(dashboardMetrics?.oldestQueuedJobAt)}</strong><small>{dashboardDateTimeLabel(dashboardMetrics?.oldestQueuedJobAt)}</small></article><article className="dashboard-health-item"><span>Son doğrulanmış senkronizasyon</span><strong>{dashboardAgeLabel(dashboardMetrics?.lastVerifiedSynchronizationAt)}</strong><small>{dashboardDateTimeLabel(dashboardMetrics?.lastVerifiedSynchronizationAt)}</small></article><article className="dashboard-health-item"><span>Dead işler</span><strong>{(dashboardMetrics?.deadJobCount ?? 0).toLocaleString('tr-TR')}</strong><small>Kalıcı olarak durdurulan işler</small></article><article className="dashboard-health-item"><span>Manuel inceleme</span><strong>{(dashboardMetrics?.manualReviewJobCount ?? 0).toLocaleString('tr-TR')}</strong><small>Operatör kararı bekleyen işler</small></article><article className="dashboard-health-item"><span>429 oranı · 24 saat</span><strong>{recentRateLimitRate === null ? '—' : `%${recentRateLimitRate.toLocaleString('tr-TR', { maximumFractionDigits: 1 })}`}</strong><small>{(dashboardMetrics?.recentRateLimitJobCount ?? 0).toLocaleString('tr-TR')} / {recentJobCount.toLocaleString('tr-TR')} iş</small></article><article className="dashboard-health-item"><span>Eski stok gözlemi</span><strong>{dashboardAgeLabel(dashboardMetrics?.oldestStockObservationAt)}</strong><small>{dashboardDateTimeLabel(dashboardMetrics?.oldestStockObservationAt)}</small></article></div></section>
    <div className="dashboard-section-bridge" aria-hidden="true"><span /><small>PERFORMANS METRİKLERİ</small><span /></div>
    <div className="dashboard-summary-grid">
      <article tabIndex={0} className="dashboard-summary-card dashboard-summary-card-with-platforms is-emphasis"><div className="dashboard-summary-card-head"><span>Toplam gelir</span><DashboardMetricIcon kind="revenue" /></div><div className="dashboard-summary-value-row"><strong>{reportRevenueQuery.isLoading ? '—' : dashboardMoney(reportTotal, reportCurrency)}</strong><span className={`dashboard-summary-trend ${dashboardTrendClass(reportTotal, previousReportTotal)}`}><DashboardTrendIcon current={reportTotal} previous={previousReportTotal} />{dashboardTrendLabel(reportTotal, previousReportTotal)}</span></div><DashboardPlatformDistribution rows={summaryPlatformRows} valueKey="amount" label="Platform gelir tutarları" /><DashboardSparkline className="dashboard-sparkline-primary" /></article>
      <article tabIndex={0} className="dashboard-summary-card dashboard-summary-card-with-platforms"><div className="dashboard-summary-card-head"><span>Toplam sipariş</span><DashboardMetricIcon kind="orders" /></div><div className="dashboard-summary-value-row"><strong>{reportRevenueQuery.isLoading ? '—' : reportOrderCount.toLocaleString('tr-TR')}</strong><span className={`dashboard-summary-trend ${dashboardTrendClass(reportOrderCount, previousReportOrderCount)}`}><DashboardTrendIcon current={reportOrderCount} previous={previousReportOrderCount} />{dashboardTrendLabel(reportOrderCount, previousReportOrderCount)}</span></div><DashboardPlatformDistribution rows={summaryPlatformRows} valueKey="orders" label="Platform sipariş adetleri" /><DashboardSparkline className="dashboard-sparkline-accent" flip /></article>
      <article className="dashboard-summary-card"><div className="dashboard-summary-card-head"><span>Ortalama sepet</span><DashboardMetricIcon kind="basket" /></div><div className="dashboard-summary-value-row"><strong>{reportRevenueQuery.isLoading ? '—' : dashboardMoney(averageBasket, reportCurrency)}</strong><span className={`dashboard-summary-trend ${dashboardTrendClass(averageBasket, previousAverageBasket)}`}><DashboardTrendIcon current={averageBasket} previous={previousAverageBasket} />{dashboardTrendLabel(averageBasket, previousAverageBasket)}</span></div><DashboardSparkline className="dashboard-sparkline-blue" /></article>
      <article className="dashboard-summary-card dashboard-summary-card-compact"><div className="dashboard-summary-card-head"><span>Aktif ürün</span><DashboardMetricIcon kind="product" /></div><strong>{productSummary.isLoading ? '—' : productCount.toLocaleString('tr-TR')}</strong><DashboardSparkline className="dashboard-sparkline-green" flip /></article>
    </div>
    <div className="dashboard-section-bridge dashboard-section-bridge-detail" aria-hidden="true"><span /><small>DETAYLI GÖRÜNÜM</small><span /></div>
    <div className="dashboard-performance-grid">
      <article className="panel dashboard-performance-card"><header className="dashboard-card-header"><div><h2>Gelir performansı</h2><p>Satışlarınızın büyük resmini görün.</p></div><div className="dashboard-performance-filters"><label className="dashboard-chart-period"><span>Dönem</span><select aria-label="Gelir performansı dönemi" value={chartRange} onChange={event => setChartRange(event.target.value as Exclude<DashboardRevenueRange, 'custom'>)}><option value="7">Son 7 gün</option><option value="30">Son 30 gün</option><option value="90">Son 90 gün</option></select></label><label className="dashboard-platform-filter"><span>Platform</span><select aria-label="Gelir platformu" value={chartPlatform} onChange={event => setChartPlatform(event.target.value)}><option value="ALL">Tüm platformlar</option>{revenuePlatformOptions.map(platform => <option value={platform} key={platform}>{platform}</option>)}</select></label></div></header><div className="dashboard-performance-toolbar"><div className="dashboard-performance-total"><span>{chartPlatform === 'ALL' ? chartPeriodLabel : `${chartPeriodLabel} · ${chartPlatform}`}</span><strong>{chartRevenueQuery.isLoading ? '—' : dashboardMoney(chartSeries.reduce((sum, item) => sum + item.amount, 0), chartCurrency)}</strong><small className={`dashboard-summary-trend ${dashboardTrendClass(chartSeries.reduce((sum, item) => sum + item.amount, 0), previousChartSeries.reduce((sum, item) => sum + item.amount, 0))}`}><DashboardTrendIcon current={chartSeries.reduce((sum, item) => sum + item.amount, 0)} previous={previousChartSeries.reduce((sum, item) => sum + item.amount, 0)} />{dashboardTrendLabel(chartSeries.reduce((sum, item) => sum + item.amount, 0), previousChartSeries.reduce((sum, item) => sum + item.amount, 0))}</small></div></div><div ref={performanceChartRef} className="dashboard-performance-chart" aria-label={`${chartPeriodLabel} gelir performansı grafiği`} >{chartCurrentPath ? <svg viewBox="0 0 652 204" role="img" aria-label="Gelir performansı"><defs><linearGradient id="dashboard-performance-area" x1="0" x2="0" y1="0" y2="1"><stop offset="0%" stopColor="var(--rv-color-primary)" stopOpacity=".34" /><stop offset="100%" stopColor="var(--rv-color-primary)" stopOpacity="0" /></linearGradient></defs>{chartGrid.map(tick => <g key={tick.y}><line className="dashboard-chart-grid" x1={dashboardChartLeft} x2={dashboardChartRight} y1={tick.y} y2={tick.y} /><text className="dashboard-chart-y" x="38" y={tick.y + 4} textAnchor="end">{dashboardAxisMoney(chartAxisMax * tick.ratio, chartCurrency)}</text></g>)}{chartAreaPath && <path className="dashboard-performance-area" d={chartAreaPath} fill="url(#dashboard-performance-area)" />}{chartPreviousPath && <path className="dashboard-performance-previous" d={chartPreviousPath} />}{<path className="dashboard-performance-line" d={chartCurrentPath} />}<DashboardChartPoints points={chartSeries} maxValue={chartAxisMax} currency={chartCurrency} chartViewport={chartViewport} /></svg> : <div className="dashboard-performance-empty">Bu dönem için gelir verisi bulunmuyor.</div>}<div className="dashboard-performance-x-axis" aria-hidden="true">{chartSeries.map((point, index) => index === 0 || index === chartSeries.length - 1 || index % chartLabelStep === 0 ? <span key={point.key} style={{ left: `${chartSeries.length > 1 ? (index / (chartSeries.length - 1)) * 100 : 0}%` }}>{point.label}</span> : null)}</div></div><footer className="dashboard-chart-legend"><span><i className="current" />Bu dönem</span><span><i className="previous" />Önceki dönem</span></footer></article>
      <article className="panel dashboard-channel-card"><header className="dashboard-card-header"><div><h2>Satış kanalları</h2><p>Seçilen dönemin gelir dağılımı</p></div><Link className="dashboard-panel-link" to="/integrations">Kanalları yönet <UiIcon name="arrowRight" /></Link></header><div className="dashboard-channel-donut" style={{ background: channelGradient }}><div><span>Toplam sipariş</span><strong>{channelTotalOrders.toLocaleString('tr-TR')}</strong><small>{reportPeriodLabel}</small></div></div><div className="dashboard-channel-list">{channelRowsWithShare.length ? channelRowsWithShare.map(channel => <div key={channel.platform}><span><i style={{ background: channel.color }} />{channel.platform}</span><strong>%{channel.share.toLocaleString('tr-TR', { maximumFractionDigits: 1 })}</strong></div>) : <p className="dashboard-channel-empty">Bu dönem için kanal dağılımı bulunmuyor.</p>}</div><footer className="dashboard-channel-footer"><span>{channelRowsWithShare.length} kanal · {reportPeriodLabel}</span><Link to="/integrations">Bağlantıları yönet <UiIcon name="arrowRight" /></Link></footer></article>
    </div>
  </section>
}
type SecurityStatus = { totpState: string; recoveryCodesRemaining: number }
type SecuritySession = { id: string; state: string; current: boolean; issuedAt: string; lastSeenAt: string; expiresAt: string }
type MfaSetup = { otpauthUri: string; qrSvg: string; expiresAt: string }
type SettingsTabKey = 'security' | 'database' | 'shipping' | 'appearance'

function SettingsTabs({ value, onChange }: { value: SettingsTabKey; onChange: (value: SettingsTabKey) => void }) {
  return <Tabs className="settings-tabs" ariaLabel="Sistem ayarları" value={value} onChange={nextValue => onChange(nextValue as SettingsTabKey)} items={[{ value: 'security', label: 'Güvenlik ve oturumlar' }, { value: 'database', label: 'Veritabanı temizliği' }, { value: 'shipping', label: 'Kargo ayarları' }, { value: 'appearance', label: 'Görünüm' }]} />
}

function SettingsStickyActions({ className = '', children }: { className?: string; children: ReactNode }) {
  const anchorRef = useRef<HTMLDivElement>(null)
  const actionsRef = useRef<HTMLDivElement>(null)
  const [isFloating, setIsFloating] = useState(false)
  const [anchorHeight, setAnchorHeight] = useState(0)
  const [floatingStyle, setFloatingStyle] = useState<CSSProperties>()

  useEffect(() => {
    let frame = 0
    const update = () => {
      if (frame) return
      frame = window.requestAnimationFrame(() => {
        frame = 0
        const anchor = anchorRef.current
        const actions = actionsRef.current
        if (!anchor || !actions) return
        const anchorRect = anchor.getBoundingClientRect()
        const nextFloating = anchorRect.bottom > window.innerHeight
        const nextHeight = nextFloating ? actions.getBoundingClientRect().height : 0
        setIsFloating(current => current === nextFloating ? current : nextFloating)
        setAnchorHeight(current => Math.abs(current - nextHeight) < 1 ? current : nextHeight)
        if (nextFloating) {
          setFloatingStyle(current => current && Math.abs((current.left as number) - anchorRect.left) < 1 && Math.abs((current.width as number) - anchorRect.width) < 1 ? current : { left: anchorRect.left, width: anchorRect.width, bottom: 0 })
        } else {
          setFloatingStyle(undefined)
        }
      })
    }
    update()
    window.addEventListener('scroll', update, { passive: true })
    window.addEventListener('resize', update)
    const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(update)
    if (observer) {
      if (anchorRef.current) observer.observe(anchorRef.current)
      if (actionsRef.current) observer.observe(actionsRef.current)
    }
    return () => {
      window.removeEventListener('scroll', update)
      window.removeEventListener('resize', update)
      observer?.disconnect()
      if (frame) window.cancelAnimationFrame(frame)
    }
  }, [])

  const floatingReady = isFloating && Boolean(floatingStyle)
  return <div ref={anchorRef} className={`settings-sticky-actions-anchor${className ? ` ${className}-anchor` : ''}`} style={anchorHeight ? { minHeight: anchorHeight } : undefined}>
    <div ref={actionsRef} className={`settings-sticky-actions${className ? ` ${className}` : ''}${floatingReady ? ' is-floating' : ''}`} style={floatingReady ? floatingStyle : undefined}>{children}</div>
  </div>
}

type SessionConfirmation = { action: 'revoke' | 'revoke-others' | 'delete' | 'delete-closed'; sessionId?: string }

function Security() {
  const client = useQueryClient()
  const [searchParams, setSearchParams] = useSearchParams()
  const requestedSettingsTab = searchParams.get('tab')
  const settingsTab: SettingsTabKey = requestedSettingsTab === 'database' || requestedSettingsTab === 'shipping' || requestedSettingsTab === 'appearance' ? requestedSettingsTab : 'security'
  function setSettingsTab(tab: SettingsTabKey) {
    setSearchParams(tab === 'security' ? {} : { tab })
  }
  const shippingSettings = useShippingLabelSettings()
  const labelSettings = shippingSettings.settings
  const [resetScopes, setResetScopes] = useState<string[]>([])
  const [resetConfirmation, setResetConfirmation] = useState('')
  const [resetBusy, setResetBusy] = useState(false)
  const status = useQuery({ queryKey: ['security'], queryFn: () => api<SecurityStatus>('/security-status') })
  const sessions = useQuery({ queryKey: ['sessions'], queryFn: () => api<SecuritySession[]>('/sessions') })
  const [mfaStep, setMfaStep] = useState<'closed' | 'password' | 'verify' | 'recovery'>('closed')
  const [setup, setSetup] = useState<MfaSetup | null>(null); const [recoveryCodes, setRecoveryCodes] = useState<string[]>([])
  const [busy, setBusy] = useState(false); const [message, setMessage] = useState('')
  const [sessionConfirmation, setSessionConfirmation] = useState<SessionConfirmation | null>(null)
  const [sessionActionBusy, setSessionActionBusy] = useState(false)
  const activeOtherSessions = (sessions.data ?? []).filter(session => !session.current && session.state === 'ACTIVE')
  const closedSessions = (sessions.data ?? []).filter(session => !session.current && session.state !== 'ACTIVE')

  function toggleResetScope(scope: string, checked: boolean) { setResetScopes(current => checked ? Array.from(new Set([...current, scope])) : current.filter(value => value !== scope)) }
  async function resetOperationalData() {
    if (!resetScopes.length || resetConfirmation !== 'Verileri sil') return
    setResetBusy(true); setMessage('')
    try {
      const result = await hubApi<{ products: number; orders: number; returns: number; invoices: number; categories: number; categoryAttributes: number; brands: number; options: number }>('/settings/data-reset', { method: 'POST', headers: { 'Idempotency-Key': crypto.randomUUID() }, body: JSON.stringify({ scopes: resetScopes, confirmation: resetConfirmation }) })
      setMessage(`Temizlik tamamlandı: ${result.products} ürün, ${result.categories} kategori, ${result.categoryAttributes} kategori özelliği, ${result.brands} marka, ${result.options} ürün seçeneği, ${result.orders} sipariş, ${result.returns} iade, ${result.invoices} fatura.`)
      setResetScopes([]); setResetConfirmation('')
      await client.invalidateQueries()
    } catch (reason) { setMessage(reason instanceof Error ? reason.message : 'Seçili veriler sıfırlanamadı.') } finally { setResetBusy(false) }
  }

  async function prepareMfa(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); setBusy(true); setMessage('')
    const data = new FormData(event.currentTarget)
    try {
      await api('/reauthenticate', { method: 'POST', body: JSON.stringify({ password: data.get('password') }) })
      const enrollment = await api<MfaSetup>('/mfa/setup', { method: 'POST' })
      setSetup(enrollment); setMfaStep('verify')
    } catch { setMessage('Parola doğrulanamadı veya güvenli kurulum başlatılamadı.') } finally { setBusy(false) }
  }
  async function confirmMfa(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); setBusy(true); setMessage('')
    const data = new FormData(event.currentTarget)
    try {
      const result = await api<{ recoveryCodes: string[] }>('/mfa/confirm', { method: 'POST', body: JSON.stringify({ code: data.get('code') }) })
      setRecoveryCodes(result.recoveryCodes); setMfaStep('recovery'); await client.invalidateQueries({ queryKey: ['security'] })
    } catch { setMessage('Doğrulama kodu geçersiz veya kurulum süresi dolmuş.') } finally { setBusy(false) }
  }
  async function confirmSessionAction() {
    const confirmation = sessionConfirmation
    if (!confirmation || sessionActionBusy) return
    setSessionActionBusy(true); setMessage('')
    try {
      if (confirmation.action === 'revoke' && confirmation.sessionId) {
        await api(`/sessions/${confirmation.sessionId}/revoke`, { method: 'POST' })
        setMessage('Oturum sonlandırıldı.')
      } else if (confirmation.action === 'revoke-others') {
        await api('/sessions/revoke-others', { method: 'POST' })
        setMessage('Diğer aktif oturumlar sonlandırıldı.')
      } else if (confirmation.action === 'delete' && confirmation.sessionId) {
        await api(`/sessions/${confirmation.sessionId}`, { method: 'DELETE' })
        setMessage('Kapalı oturum silindi.')
      } else if (confirmation.action === 'delete-closed') {
        await api('/sessions/closed', { method: 'DELETE' })
        setMessage('Kapalı oturumlar silindi.')
      }
      await client.invalidateQueries({ queryKey: ['sessions'] })
    } catch {
      setMessage(confirmation.action.startsWith('delete') ? 'Kapalı oturum silinemedi.' : 'Oturumlar sonlandırılamadı.')
    } finally {
      setSessionActionBusy(false)
      setSessionConfirmation(null)
    }
  }
  const resetScopeDetails: Record<string, { label: string; description: string }> = {
    PRODUCTS: { label: 'Ürünler listesi', description: 'Ürünleri, varyantları ve bu ürünlere bağlı görsel, stok ve kanal kayıtlarını temizler.' },
    CATEGORIES: { label: 'Kategori listesi', description: 'Panel kategori ağacını ve kategori-pazaryeri eşleştirmelerini kaldırır; ürünlerin kategori bağlantısını boşaltır.' },
    CATEGORY_ATTRIBUTES: { label: 'Kategori özellikleri', description: 'Kategori özellik başlıklarını, değerlerini, kategori atamalarını, ürün değerlerini ve pazaryeri özellik eşleştirmelerini kaldırır.' },
    BRANDS: { label: 'Marka listesi', description: 'Marka kayıtlarını ve marka-pazaryeri eşleştirmelerini kaldırır; ürünlerin marka bağlantısını boşaltır.' },
    OPTIONS: { label: 'Ürün seçenekleri', description: 'Ürün seçenek gruplarını, seçenek değerlerini ve varyantlarla olan seçenek bağlantılarını temizler.' },
    ORDERS: { label: 'Siparişler listesi', description: 'Siparişleri ve siparişe bağlı kargo, satır, durum geçmişi ve finansal kayıtları temizler; ilişkili iadeleri ve faturaları da kaldırır.' },
    RETURNS: { label: 'İadeler listesi', description: 'İade taleplerini, iade satırlarını, kararlarını, kanıtlarını ve stok işlemlerini temizler.' },
    INVOICES: { label: 'Faturalar listesi', description: 'Faturaları ve bunlara bağlı belge, satır, teslimat ve gönderim denemelerini temizler.' }
  }
  function resetScopeOption(scope: string) {
    const detail = resetScopeDetails[scope]
    return <label key={scope}><input type="checkbox" checked={resetScopes.includes(scope)} onChange={event => toggleResetScope(scope, event.target.checked)} /><span className="database-scope-label" data-description={detail.description}><strong>{detail.label}</strong></span></label>
  }

  async function saveLabelSettings() {
    try {
      await shippingSettings.save(labelSettings)
      setMessage('Kargo etiketi ayarları hesaba kaydedildi.')
    } catch (reason) {
      setMessage(reason instanceof Error ? reason.message : 'Kargo etiketi ayarları kaydedilemedi.')
    }
  }

  if (settingsTab === 'database') return <section className="content security-page"><div className="page-heading"><div><p className="eyebrow">Ayarlar</p><h1>Sistem ayarları</h1><p className="lede">Güvenlik ve yerel operasyon verilerini tek ekranda yönetin.</p></div></div><div className="settings-tabs" role="tablist"><button type="button" role="tab" aria-selected={false} onClick={() => setSettingsTab('security')}>Güvenlik ve oturumlar</button><button type="button" role="tab" aria-selected={true} className="active">Veritabanı temizliği</button><button type="button" role="tab" aria-selected={false} onClick={() => setSettingsTab('shipping')}>Kargo ayarları</button><button type="button" role="tab" aria-selected={false} onClick={() => setSettingsTab('appearance')}>Görünüm</button></div>{message && <div className="notice" role="status">{message}</div>}<div className="panel database-reset-panel"><div className="database-reset-intro"><span className="security-state">Yetkili İşlemi</span><h2>Yerel veritabanı listelerini sıfırla</h2><p>Yalnız seçtiğiniz alanlar bu hesabın yerel veritabanından silinir. Her başlığın kapsam ve bağlı kayıt ayrıntılarını görebilirsiniz.</p></div><div className="database-scope-groups"><section className="database-scope-group"><div><h3>Katalog</h3><p>Ürün kataloğunda kullanılan temel listeleri temizleyin.</p></div><div className="database-scope-list">{['PRODUCTS', 'CATEGORIES', 'CATEGORY_ATTRIBUTES', 'BRANDS'].map(resetScopeOption)}</div></section><section className="database-scope-group"><div><h3>Ürün seçenekleri</h3><p>Ürün seçeneklerini ve seçenek değerlerini temizleyin.</p></div><div className="database-scope-list">{['OPTIONS'].map(resetScopeOption)}</div></section><section className="database-scope-group"><div><h3>Operasyon</h3><p>İşlem ve satış kayıtlarını temizleyin.</p></div><div className="database-scope-list">{['ORDERS', 'RETURNS', 'INVOICES'].map(resetScopeOption)}</div></section></div><label className="database-confirmation">Onay için <b>Verileri sil</b> yazın<input value={resetConfirmation} onChange={event => setResetConfirmation(event.target.value)} /></label><SettingsStickyActions><div className="settings-sticky-copy"><strong>Seçili listeleri kalıcı olarak temizle</strong><span>Bu işlem seçtiğiniz kayıtları geri alınamayacak şekilde kaldırır. Önce onay metnini girin.</span></div><button type="button" className="destructive" disabled={!resetScopes.length || resetConfirmation !== 'Verileri sil' || resetBusy} onClick={() => void resetOperationalData()}>{resetBusy ? 'Temizleniyor…' : 'Seçili listeleri kalıcı sil'}</button></SettingsStickyActions></div></section>

  const sessionConfirmationCopy = sessionConfirmation?.action === 'revoke'
    ? { title: 'Oturumu sonlandır', description: 'Bu cihazın bağlantısı sonlandırılacak. Devam etmek istiyor musunuz?', confirm: 'Oturumu sonlandır' }
    : sessionConfirmation?.action === 'revoke-others'
      ? { title: 'Diğer oturumları kapat', description: `${activeOtherSessions.length} aktif oturum sonlandırılacak. Devam etmek istiyor musunuz?`, confirm: 'Oturumları kapat' }
      : sessionConfirmation?.action === 'delete'
        ? { title: 'Kapalı oturum kaydını sil', description: 'Bu kapalı oturum kaydı kalıcı olarak silinecek. Devam etmek istiyor musunuz?', confirm: 'Kaydı sil' }
        : sessionConfirmation?.action === 'delete-closed'
          ? { title: 'Kapalı oturumları sil', description: `${closedSessions.length} kapalı oturum kaydı kalıcı olarak silinecek. Devam etmek istiyor musunuz?`, confirm: 'Kayıtları sil' }
          : null
  if (sessionConfirmationCopy) return <Modal open title={sessionConfirmationCopy.title} description={sessionConfirmationCopy.description} className="settings-confirm-modal" onClose={() => { if (!sessionActionBusy) setSessionConfirmation(null) }} footer={<><button type="button" className="secondary" disabled={sessionActionBusy} onClick={() => setSessionConfirmation(null)}>Vazgeç</button><button type="button" className="destructive" disabled={sessionActionBusy} onClick={() => void confirmSessionAction()}>{sessionActionBusy ? 'İşlem yapılıyor…' : sessionConfirmationCopy.confirm}</button></>}><div className="settings-confirm-content"><span className="settings-confirm-icon" aria-hidden="true"><UiIcon name="alert" /></span><div><strong>Bu işlem hesap oturumlarını etkiler.</strong><p>Devam etmeden önce yapmak istediğiniz işlemi kontrol edin.</p></div></div></Modal>

  const legacySettingsTab = settingsTab as string; if (settingsTab === 'appearance') return <AppearanceSettingsPage />; if (settingsTab === 'shipping') return <section className="content security-page"><div className="page-heading"><div><p className="eyebrow">Ayarlar</p><h1>Sistem ayarları</h1><p className="lede">Etiket ölçüsü ve yazdırma düzenini yönetin.</p></div></div><div className="settings-tabs" role="tablist"><button type="button" role="tab" aria-selected={false} onClick={() => setSettingsTab('security')}>Güvenlik ve oturumlar</button><button type="button" role="tab" aria-selected={false} onClick={() => setSettingsTab('database')}>Veritabanı temizliği</button><button type="button" role="tab" aria-selected={true} className="active">Kargo ayarları</button><button type="button" role="tab" aria-selected={false} onClick={() => setSettingsTab('appearance')}>Görünüm</button></div>{message && <Toast tone={message.includes('kaydedilemedi') ? 'danger' : 'success'}>{message}</Toast>}{shippingSettings.isError && !shippingSettings.settings && <div className="error" role="alert">Hesap kargo ayarları alınamadı; geçici yerel ayarlar gösteriliyor.</div>}<ShippingLabelSettingsPanel settings={labelSettings} onChange={shippingSettings.setSettings} onSave={saveLabelSettings} /></section>
  return <section className="content security-page"><div className="page-heading"><div><p className="eyebrow">Ayarlar</p><h1>Sistem ayarları</h1><p className="lede">Güvenlik ve yerel operasyon verilerini tek ekrandan yönetin.</p></div></div><div className="settings-tabs" role="tablist"><button type="button" role="tab" aria-selected={settingsTab === 'security'} className={settingsTab === 'security' ? 'active' : ''} onClick={() => setSettingsTab('security')}>Güvenlik ve oturumlar</button><button type="button" role="tab" aria-selected={legacySettingsTab === 'database'} className={legacySettingsTab === 'database' ? 'active' : ''} onClick={() => setSettingsTab('database')}>Veritabanı temizliği</button><button type="button" role="tab" aria-selected={legacySettingsTab === 'shipping'} className={legacySettingsTab === 'shipping' ? 'active' : ''} onClick={() => setSettingsTab('shipping')}>Kargo ayarları</button><button type="button" role="tab" aria-selected={legacySettingsTab === 'appearance'} className={legacySettingsTab === 'appearance' ? 'active' : ''} onClick={() => setSettingsTab('appearance')}>Görünüm</button></div>{message && <div className="notice" role="status">{message}</div>}{settingsTab === 'security' && <>{status.isLoading ? <DelayedStatus title="Güvenlik durumu yükleniyor" /> : status.isError || !status.data ? <div role="alert" className="error">Güvenlik durumu alınamadı.</div> : <div className="panel security-authenticator-card"><div><span className={`security-state ${status.data.totpState === 'ENABLED' ? 'enabled' : ''}`}>{status.data.totpState === 'ENABLED' ? 'Etkin' : 'Kapalı'}</span><h2>Authenticator</h2><p>Giriş sırasında telefonunuzdaki tek kullanımlık kodla hesabınızı koruyun.</p><small>Kalan kurtarma kodu: <strong>{status.data.recoveryCodesRemaining}</strong></small></div>{status.data.totpState === 'ENABLED' ? <span className="security-check" aria-label="Authenticator etkin"><UiIcon name="check" /></span> : <button type="button" onClick={() => { setMessage(''); setMfaStep('password') }}>Authenticator’ı etkinleştir</button>}</div>}
     <div className="panel security-sessions-card"><div className="panel-title"><div><h2>Oturumlar</h2><p>Hesabınıza bağlı cihazları ve son etkinliklerini görüntüleyin.</p></div><div className="session-bulk-actions">{activeOtherSessions.length > 0 && <button type="button" className="secondary danger-outline" onClick={() => setSessionConfirmation({ action: 'revoke-others' })}>Diğer tüm oturumları kapat</button>}{closedSessions.length > 0 && <button type="button" className="secondary danger-outline" onClick={() => setSessionConfirmation({ action: 'delete-closed' })}>Kapalı oturumları sil</button>}</div></div>{sessions.isLoading ? <p>Yükleniyor…</p> : sessions.isError || !sessions.data ? <div role="alert" className="error">Oturumlar alınamadı.</div> : <ul className="sessions">{sessions.data.map(session => <li key={session.id} className={session.current ? 'current' : ''}><span className="session-device-icon" aria-hidden="true"><UiIcon name={session.current ? 'check' : 'grid'} /></span><span><strong>{session.current ? 'Bu cihaz' : 'Diğer oturum'}</strong><small>{session.state === 'ACTIVE' ? 'Aktif' : 'Sonlandırıldı'} · Son etkinlik {new Date(session.lastSeenAt).toLocaleString('tr-TR')}</small><small>Bitiş {new Date(session.expiresAt).toLocaleString('tr-TR')}</small></span>{session.current ? <b>Mevcut oturum</b> : session.state === 'ACTIVE' ? <button type="button" className="secondary danger-outline" onClick={() => setSessionConfirmation({ action: 'revoke', sessionId: session.id })}>Oturumu sonlandır</button> : <button type="button" className="secondary danger-outline session-delete-action" onClick={() => setSessionConfirmation({ action: 'delete', sessionId: session.id })}>Kaydı sil</button>}</li>)}</ul>}</div>
    {mfaStep !== 'closed' && <div className="workspace-modal-backdrop" role="presentation"><section className="workspace-modal security-modal" role="dialog" aria-modal="true" aria-labelledby="mfa-title"><header><div><h2 id="mfa-title">Authenticator kurulumu</h2><p>{mfaStep === 'password' ? 'Önce hesabın size ait olduğunu doğrulayın.' : mfaStep === 'verify' ? 'QR kodu uygulamanıza ekleyip üretilen kodu girin.' : 'Kurtarma kodlarını şimdi güvenli bir yerde saklayın.'}</p></div><button className="modal-close" type="button" aria-label="Kapat" onClick={() => setMfaStep('closed')}><UiIcon name="close" /></button></header>{mfaStep === 'password' && <form className="security-modal-body" onSubmit={prepareMfa}><label>Mevcut parola<input name="password" type="password" autoComplete="current-password" required /></label><button disabled={busy}>{busy ? 'Doğrulanıyor…' : 'Devam et'}</button></form>}{mfaStep === 'verify' && setup && <form className="security-modal-body mfa-verify" onSubmit={confirmMfa}><img src={`data:image/svg+xml;utf8,${encodeURIComponent(setup.qrSvg)}`} alt="Authenticator QR kodu" /><div><p>QR kodu Google Authenticator, Microsoft Authenticator veya uyumlu uygulamanızla tarayın.</p><details><summary>Kurulum anahtarını elle göster</summary><code>{setup.otpauthUri}</code></details><label>6 haneli doğrulama kodu<input name="code" inputMode="numeric" pattern="[0-9]{6}" autoComplete="one-time-code" required /></label><button disabled={busy}>{busy ? 'Kontrol ediliyor…' : 'Etkinleştir'}</button></div></form>}{mfaStep === 'recovery' && <div className="security-modal-body"><div className="recovery-code-grid">{recoveryCodes.map(code => <code key={code}>{code}</code>)}</div><p>Bu kodlar yalnızca bir kez gösterilir. Her kod tek kullanımlıktır.</p><button type="button" onClick={() => setMfaStep('closed')}>Kodları sakladım</button></div>}{message && <div className="error security-modal-error" role="alert">{message}</div>}</section></div>}</>}
    {legacySettingsTab === 'database' && <div className="panel database-reset-panel"><div className="database-reset-intro"><span className="security-state">Yetkili İşlemi</span><h2>Yerel veritabanı listelerini sıfırla</h2><p>Seçilen kayıtlar yalnız bu hesabın yerel veritabanından silinir. Bağlı alt kayıtlar güvenli sırayla temizlenir.</p></div><div className="database-scope-groups"><section className="database-scope-group"><div><h3>Katalog</h3><p>Ürün kataloğunda kullanılan temel listeleri temizleyin.</p></div><div className="database-scope-list">{[['PRODUCTS','Ürünler listesi'],['CATEGORIES','Kategori listesi'],['BRANDS','Marka listesi']].map(([scope,label]) => <label key={scope}><input type="checkbox" checked={resetScopes.includes(scope)} onChange={event => toggleResetScope(scope, event.target.checked)} /><span><strong>{label}</strong><small>Yerel kayıtları ve bağlı alt kayıtları temizle</small></span></label>)}</div></section><section className="database-scope-group"><div><h3>Ürün seçenekleri</h3><p>Ürün seçeneklerini ve seçenek değerlerini temizleyin.</p></div><div className="database-scope-list">{[['OPTIONS','Seçenekler listesi']].map(([scope,label]) => <label key={scope}><input type="checkbox" checked={resetScopes.includes(scope)} onChange={event => toggleResetScope(scope, event.target.checked)} /><span><strong>{label}</strong><small>Ürün seçeneklerini ve bağlı değerleri temizle</small></span></label>)}</div></section><section className="database-scope-group"><div><h3>Operasyon</h3><p>İşlem ve satış kayıtlarını temizleyin.</p></div><div className="database-scope-list">{[['ORDERS','Siparişler listesi'],['RETURNS','İadeler listesi'],['INVOICES','Faturalar listesi']].map(([scope,label]) => <label key={scope}><input type="checkbox" checked={resetScopes.includes(scope)} onChange={event => toggleResetScope(scope, event.target.checked)} /><span><strong>{label}</strong><small>Yerel kayıtları ve bağlı alt kayıtları temizle</small></span></label>)}</div></section></div><label className="database-confirmation">Onay için <b>Verileri sil</b> yazın<input value={resetConfirmation} onChange={event => setResetConfirmation(event.target.value)} /></label><div className="settings-sticky-actions"><div className="settings-sticky-copy"><strong>Seçili listeleri kalıcı olarak temizle</strong><span>Bu işlem seçtiğiniz kayıtları geri alınamayacak şekilde kaldırır. Önce onay metnini girin.</span></div><button type="button" className="destructive" disabled={!resetScopes.length || resetConfirmation !== 'Verileri sil' || resetBusy} onClick={() => void resetOperationalData()}>{resetBusy ? 'Temizleniyor…' : 'Seçili listeleri kalıcı sil'}</button></div></div>}
  </section>
}

function AppearanceSettingsPage() {
  const appearance = useAppearanceSettings()
  const [message, setMessage] = useState('')
  const [busy, setBusy] = useState(false)
  const [newThemeName, setNewThemeName] = useState('')
  const [themeMenuOpen, setThemeMenuOpen] = useState(false)
  const [selectedThemeId, setSelectedThemeId] = useState(defaultAppearanceColorTheme.id)
  const [themeToDelete, setThemeToDelete] = useState<AppearanceColorThemeProfile | null>(null)
  const colorTheme = 'dark' as const
  const [visualTheme, setVisualTheme] = useState<VisualTheme>(readVisualThemePreference)
  const savedAppearance = useRef(appearance.settings)

  useEffect(() => {
    const syncVisualTheme = (event: Event) => {
      const nextTheme = (event as CustomEvent<VisualTheme>).detail
      if (nextTheme === 'light' || nextTheme === 'dark') {
        setVisualTheme(nextTheme)
        setSelectedThemeId(nextTheme === 'light' ? 'default-light' : defaultAppearanceColorTheme.id)
      }
    }
    window.addEventListener(visualThemeChangeEvent, syncVisualTheme)
    return () => window.removeEventListener(visualThemeChangeEvent, syncVisualTheme)
  }, [])

  useEffect(() => {
    savedAppearance.current = appearance.settings
  }, [appearance.settings])

  useEffect(() => {
    const root = document.documentElement
    const applyPreview = () => {
      root.dataset.theme = visualTheme
      root.dataset.themeMode = visualTheme
      root.style.setProperty('--rv-font-ui', appearanceFontFamilyCss[appearance.draft.fontFamily])
      root.style.setProperty('--rv-font-scale', String(appearanceFontScale[appearance.draft.fontSize]))
      const palette = visualTheme === 'light' ? defaultLightPalette : appearance.draft.colors.dark
      appearanceColorTokenOptions.forEach(({ key }) => root.style.setProperty(appearanceColorCssVariable(key), palette[key]))
    }
    applyPreview()
  }, [appearance.draft, visualTheme])

  useEffect(() => () => {
    const root = document.documentElement
    const saved = savedAppearance.current
    const savedTheme: VisualTheme = localStorage.getItem('ravencia.visualTheme') === 'light' ? 'light' : 'dark'
    root.dataset.theme = savedTheme
    root.dataset.themeMode = savedTheme
    root.style.setProperty('--rv-font-ui', appearanceFontFamilyCss[saved.fontFamily])
    root.style.setProperty('--rv-font-scale', String(appearanceFontScale[saved.fontSize]))
    const palette = savedTheme === 'light' ? defaultLightPalette : saved.colors.dark
    appearanceColorTokenOptions.forEach(({ key }) => root.style.setProperty(appearanceColorCssVariable(key), palette[key]))
  }, [])

  async function save() {
    setBusy(true)
    setMessage('')
    try {
      await appearance.save(appearance.draft)
      setMessage('Görünüm ayarları hesabınıza kaydedildi.')
    } catch (reason) {
      setMessage(reason instanceof Error ? reason.message : 'Görünüm ayarları kaydedilemedi.')
    } finally {
      setBusy(false)
    }
  }

  function applyColorTheme(theme: typeof appearance.draft.colorThemes[number]) {
    setVisualThemePreference('dark')
    setSelectedThemeId(theme.id)
    appearance.setDraft({ ...appearance.draft, themeMode: 'dark', colors: { dark: { ...theme.palette } } })
    setMessage(`${theme.name} önizlemeye uygulandı. Kalıcı yapmak için değişiklikleri kaydedin.`)
  }

  function applyDefaultAppearance() {
    const defaults: AppearanceSettings = {
      ...defaultAppearanceSettings,
      colors: { dark: { ...defaultAppearanceSettings.colors.dark } },
      colorThemes: appearance.draft.colorThemes.map(theme => ({ ...theme, palette: { ...theme.palette } }))
    }
    setVisualThemePreference('dark')
    setSelectedThemeId(defaultAppearanceColorTheme.id)
    appearance.setDraft(defaults)
    setMessage('Varsayılan görünüm önizlemeye uygulandı. Kalıcı yapmak için değişiklikleri kaydedin.')
  }

  function applyLightTheme() {
    setSelectedThemeId('default-light')
    setVisualThemePreference('light')
    setMessage('Ravencia — Aydınlık önizlemeye uygulandı.')
  }

  async function saveColorTheme() {
    const name = newThemeName.trim()
    if (!name) {
      setMessage('Kaydetmek için bir tema adı yazın.')
      return
    }
    const id = `custom-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`
    const profile = { id, name: name.slice(0, 60), palette: { ...palette }, builtIn: false }
    const nextDraft = { ...appearance.draft, colorThemes: [...appearance.draft.colorThemes, profile] }
    setBusy(true)
    setMessage('')
    try {
      await appearance.save(nextDraft)
      setSelectedThemeId(id)
      setNewThemeName('')
      setMessage(`“${profile.name}” renk teması kaydedildi.`)
    } catch (reason) {
      setMessage(reason instanceof Error ? reason.message : 'Renk teması kaydedilemedi.')
    } finally {
      setBusy(false)
    }
  }

  async function deleteColorTheme() {
    const theme = themeToDelete
    if (!theme || theme.builtIn) return
    const nextThemes = appearance.draft.colorThemes.filter(item => item.id !== theme.id)
    const nextDraft = { ...appearance.draft, colorThemes: nextThemes }
    if (selectedThemeId === theme.id) {
      setSelectedThemeId(defaultAppearanceColorTheme.id)
      nextDraft.colors = { dark: { ...defaultAppearanceColorTheme.palette } }
    }
    setBusy(true)
    setMessage('')
    try {
      await appearance.save(nextDraft)
      setMessage(`“${theme.name}” renk teması silindi.`)
    } catch (reason) {
      setMessage(reason instanceof Error ? reason.message : 'Renk teması silinemedi.')
    } finally {
      setBusy(false)
      setThemeToDelete(null)
    }
  }

  if (themeToDelete) return <Modal open title="Renk temasını sil" description={`“${themeToDelete.name}” renk teması kalıcı olarak silinecek. Devam etmek istiyor musunuz?`} className="settings-confirm-modal" onClose={() => { if (!busy) setThemeToDelete(null) }} footer={<><button type="button" className="secondary" disabled={busy} onClick={() => setThemeToDelete(null)}>Vazgeç</button><button type="button" className="destructive" disabled={busy} onClick={() => void deleteColorTheme()}>{busy ? 'Siliniyor…' : 'Temayı sil'}</button></>}><div className="settings-confirm-content"><span className="settings-confirm-icon" aria-hidden="true"><UiIcon name="alert" /></span><div><strong>Bu işlem geri alınamaz.</strong><p>Temayı silmeden önce seçiminizi kontrol edin.</p></div></div></Modal>

  const selectableThemes: Array<{ id: string; label: string; description: string; mode: VisualTheme; profile: typeof defaultAppearanceColorTheme | null }> = [
    { id: 'default-light', label: 'Ravencia — Aydınlık', description: 'Açık çalışma alanı görünümü', mode: 'light', profile: null },
    { id: defaultAppearanceColorTheme.id, label: defaultAppearanceColorTheme.name, description: defaultAppearanceColorTheme.builtIn ? 'Çalışma alanının varsayılan koyu teması' : 'Kayıtlı özel tema', mode: 'dark', profile: defaultAppearanceColorTheme },
    ...appearance.draft.colorThemes.filter(theme => !theme.builtIn).map(theme => ({ id: theme.id, label: theme.name, description: 'Kayıtlı özel tema', mode: 'dark' as const, profile: theme }))
  ]
  const activeSelectableTheme = selectableThemes.find(theme => theme.id === (visualTheme === 'light' ? 'default-light' : selectedThemeId)) ?? selectableThemes[0]
  const activeThemeLabel = visualTheme === 'light' ? 'Ravencia — Aydınlık' : appearance.draft.colorThemes.find(theme => theme.id === selectedThemeId)?.name ?? defaultAppearanceColorTheme.name
  const previewStyle: CSSProperties = {
    fontFamily: appearanceFontFamilyCss[appearance.draft.fontFamily],
    fontSize: 'var(--rv-font-size-md)',
    ...Object.fromEntries(appearanceColorTokenOptions.map(({ key }) => [appearanceColorCssVariable(key), (visualTheme === 'light' ? defaultLightPalette : appearance.draft.colors[colorTheme])[key]]))
  }

  const palette = appearance.draft.colors[colorTheme]
  function updateColor(key: typeof appearanceColorTokenOptions[number]['key'], value: string) {
    appearance.setDraft({ ...appearance.draft, colors: { ...appearance.draft.colors, [colorTheme]: { ...appearance.draft.colors[colorTheme], [key]: value } } })
  }

  const navigate = useNavigate()

  return <section className="content security-page">
    <div className="page-heading"><div><p className="eyebrow">Ayarlar</p><h1>Sistem ayarları</h1><p className="lede">Güvenlik ve yerel operasyon verilerini tek ekranda yönetin.</p></div></div>
    <SettingsTabs value="appearance" onChange={tab => navigate(tab === 'security' ? '/settings' : `/settings?tab=${tab}`)} />
    {message && <div className="notice" role="status">{message}</div>}
    {appearance.isError && <div className="error" role="alert">Hesap görünüm ayarları alınamadı; varsayılan görünüm gösteriliyor.</div>}
    <section className="panel appearance-settings-panel">
      <div className="panel-title"><div><span className="security-state">Görünüm ayarları</span><h2>Okunabilirlik</h2><p>Tablo ve işlem ekranlarındaki yazıları hesabınız için özelleştirin.</p></div></div>
      <div className="appearance-settings-grid">
        <label><span>Font tipi</span><select value={appearance.draft.fontFamily} onChange={event => appearance.setDraft({ ...appearance.draft, fontFamily: event.target.value as AppearanceSettings['fontFamily'] })}>{appearanceFontFamilyOptions.map(option => <option key={option.value} value={option.value}>{option.label}</option>)}</select></label>
        <label><span>Yazı boyutu</span><select value={appearance.draft.fontSize} onChange={event => appearance.setDraft({ ...appearance.draft, fontSize: event.target.value as AppearanceSettings['fontSize'] })}>{appearanceFontSizeOptions.map(option => <option key={option.value} value={option.value}>{option.label} — {option.description}</option>)}</select></label>
        <div className="appearance-theme-field"><span>Tema</span><div className="appearance-theme-picker" onBlur={event => { if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setThemeMenuOpen(false) }}><button type="button" className="appearance-theme-trigger" aria-label="Arayüz teması" aria-haspopup="listbox" aria-expanded={themeMenuOpen} onClick={() => setThemeMenuOpen(value => !value)} onKeyDown={event => { if (event.key === 'Escape') setThemeMenuOpen(false) }}><span>{activeSelectableTheme ? `${activeSelectableTheme.label} — ${activeSelectableTheme.description}` : 'Tema seçin'}</span><UiIcon name="chevronDown" /></button>{themeMenuOpen && <div className="appearance-theme-menu" role="listbox" aria-label="Arayüz teması seçenekleri">{selectableThemes.map(theme => <button type="button" role="option" aria-selected={theme.id === activeSelectableTheme?.id} key={theme.id} onMouseDown={event => event.preventDefault()} onClick={() => { if (theme.mode === 'light') applyLightTheme(); else if (theme.profile) applyColorTheme(theme.profile); setThemeMenuOpen(false) }}><span className="appearance-theme-menu-copy"><strong>{theme.label}</strong><small>{theme.description}</small></span></button>)}</div>}</div></div>
      </div>
      <section className="appearance-color-editor">
        <div className="appearance-color-editor-heading"><div><h2>Renk paleti</h2><p>Değişiklikler kaydetmeden önce anlık önizlenir. Hazır paletleri saklayabilir, daha sonra yeniden uygulayabilirsiniz.</p></div><div className="appearance-color-actions"><strong className="appearance-color-theme-label">{activeThemeLabel}</strong></div></div>
        <div className="appearance-color-themes" aria-label="Kayıtlı renk temaları"><div className="appearance-color-themes-heading"><div><h3>Kayıtlı temalar</h3><p>Varsayılan tema korunur ve silinemez.</p></div><div className="appearance-color-theme-create"><input value={newThemeName} maxLength={60} placeholder="Yeni tema adı" aria-label="Yeni tema adı" onChange={event => setNewThemeName(event.target.value)} /><button type="button" className="rv-button rv-button-primary rv-button-sm" disabled={busy || !newThemeName.trim()} onClick={() => void saveColorTheme()}>Renk temasını kaydet</button></div></div><div className="appearance-color-theme-list">{appearance.draft.colorThemes.map(theme => <article className={`appearance-color-theme-card ${selectedThemeId === theme.id ? 'is-selected' : ''}`} key={theme.id}><button type="button" className="appearance-color-theme-select" onClick={() => applyColorTheme(theme)}><span className="appearance-color-theme-swatches" aria-hidden="true">{[theme.palette.bg, theme.palette.surface, theme.palette.primary, theme.palette.accent].map(color => <i key={color} style={{ backgroundColor: color }} />)}</span><span><strong>{theme.name}</strong><small>{theme.builtIn ? 'Varsayılan tema' : 'Kayıtlı özel tema'}</small></span></button>{theme.builtIn ? <span className="appearance-color-theme-protected">Korunuyor</span> : <button type="button" className="appearance-color-theme-delete" disabled={busy} onClick={() => setThemeToDelete(theme)} aria-label={`${theme.name} temasını sil`}>Sil</button>}</article>)}</div></div>
        <div className="appearance-color-grid">{appearanceColorTokenOptions.map(({ key, label, description }) => <label className="appearance-color-field" key={key}><span><b>{label}</b><small>{description}</small></span><span className="appearance-color-control"><input type="color" value={palette[key]} onChange={event => updateColor(key, event.target.value)} aria-label={`${label} rengi`} /><code>{palette[key].toUpperCase()}</code></span></label>)}</div>
      </section>
      <div className="appearance-preview" style={previewStyle}><small>Önizleme</small><strong>Ravencia MarketplaceHub</strong><p>Bu ayar sipariş, iade, ürün ve diğer çalışma ekranlarındaki metinleri etkiler.</p></div>
      <SettingsStickyActions className="appearance-settings-actions"><div className="settings-sticky-copy"><strong>Görünüm ayarlarını uygula</strong><span>Değişiklikleri hesabınıza kaydetmeden önce önizleyin.</span></div><button type="button" className="rv-button rv-button-secondary" onClick={applyDefaultAppearance} disabled={busy}>Varsayılanlara dön</button><button type="button" className="rv-button rv-button-primary" onClick={() => void save()} disabled={busy}>{busy ? 'Kaydediliyor…' : 'Görünüm ayarlarını kaydet'}</button></SettingsStickyActions>
    </section>
  </section>
}

type ShippingDesignerTab = 'general' | 'text' | 'barcode'
type ShippingResizeDirection = 'nw' | 'ne' | 'se' | 'sw'
type ShippingRotateDirection = 'n' | 'e' | 's' | 'w'
type ShippingLabelTemplate = { id: string; name: string; savedAt: string; settings: ShippingLabelSettings }
const shippingLabelTemplateStorageKey = 'ravencia.shippingLabelTemplates'
const shippingResizeDirections: ShippingResizeDirection[] = ['nw', 'ne', 'se', 'sw']
const shippingRotateDirections: ShippingRotateDirection[] = ['n', 'e', 's', 'w']
const shippingResizeDirectionLabels: Record<ShippingResizeDirection, string> = { nw: 'sol üst köşe', ne: 'sağ üst köşe', se: 'sağ alt köşe', sw: 'sol alt köşe' }
const shippingRotateDirectionLabels: Record<ShippingRotateDirection, string> = { n: 'üst kenar', e: 'sağ kenar', s: 'alt kenar', w: 'sol kenar' }

function loadShippingLabelTemplates(): ShippingLabelTemplate[] {
  try {
    const value = JSON.parse(localStorage.getItem(shippingLabelTemplateStorageKey) ?? '[]')
    return Array.isArray(value) ? value.filter(item => item && typeof item.id === 'string' && typeof item.name === 'string' && item.settings) : []
  } catch { return [] }
}

function ShippingLabelSettingsPanel({ settings, onChange, onSave }: { settings: ShippingLabelSettings; onChange: (value: ShippingLabelSettings) => void; onSave: () => void }) {
  const [format, setFormat] = useState<'a4' | 'sticker'>(() => settings.defaultFormat)
  const [selectedId, setSelectedId] = useState<string | null>(settings.layout.a4[0]?.id ?? null)
  const [designerTab, setDesignerTab] = useState<ShippingDesignerTab>('general')
  const [customTitle, setCustomTitle] = useState('')
  const [customText, setCustomText] = useState('')
  const [templates, setTemplates] = useState<ShippingLabelTemplate[]>(() => loadShippingLabelTemplates())
  const [selectedTemplateId, setSelectedTemplateId] = useState('')
  const [templateNameDraft, setTemplateNameDraft] = useState('')
  const [templateNameOpen, setTemplateNameOpen] = useState(false)
  const [templateMessage, setTemplateMessage] = useState('')
  const pointerDrag = useRef<{ id: string; offsetX: number; offsetY: number } | null>(null)
  const pointerResize = useRef<{ id: string; direction: ShippingResizeDirection; startClientX: number; startClientY: number; position: ReturnType<typeof positionFor> } | null>(null)
  const pointerRotate = useRef<{ id: string; startAngle: number; startRotation: number } | null>(null)
  const [stickerWidthDraft, setStickerWidthDraft] = useState(() => String(settings.stickerWidthMm))
  const [stickerHeightDraft, setStickerHeightDraft] = useState(() => String(settings.stickerHeightMm))
  const [fontSizeDraft, setFontSizeDraft] = useState('')
  const layout = settings.layout[format]
  const activeBlock = layout.find(block => block.id === selectedId) ?? layout[0] ?? null

  useEffect(() => { setSelectedId(settings.layout[format][0]?.id ?? null) }, [format])
  useEffect(() => { setStickerWidthDraft(String(settings.stickerWidthMm)) }, [settings.stickerWidthMm])
  useEffect(() => { setStickerHeightDraft(String(settings.stickerHeightMm)) }, [settings.stickerHeightMm])
  useEffect(() => { setFontSizeDraft(activeBlock ? String(activeBlock.fontSize ?? 14) : '') }, [format, activeBlock?.id])

  function update<K extends keyof ShippingLabelSettings>(key: K, value: ShippingLabelSettings[K]) {
    onChange({ ...settings, [key]: value })
  }
  function updateBlock(blockId: string, patch: Partial<ShippingLabelBlock>) {
    onChange({ ...settings, layout: { ...settings.layout, [format]: layout.map(block => block.id === blockId ? { ...block, ...patch } : block) } })
  }
  function rotateActiveBlock(delta: number) {
    if (!activeBlock) return
    rotateBlock(activeBlock, delta)
  }
  function rotateBlock(block: ShippingLabelBlock, delta: number) {
    const current = block.rotation ?? 0
    updateBlock(block.id, { rotation: (current + delta + 360) % 360 })
  }
  function positionFor(block: ShippingLabelBlock, index: number) {
    return block.position ?? defaultShippingLabelBlockPosition(block.kind, index)
  }
  const snapStep = 2.5
  function snapPosition(value: number, maximum: number) { return Math.min(maximum, Math.max(0, Math.round(value / snapStep) * snapStep)) }
  function beginPointerDrag(event: ReactPointerEvent<HTMLElement>, block: ShippingLabelBlock) {
    if (event.button !== 0) return
    const blockRect = event.currentTarget.getBoundingClientRect()
    pointerDrag.current = { id: block.id, offsetX: event.clientX - blockRect.left, offsetY: event.clientY - blockRect.top }
    setSelectedId(block.id)
    event.currentTarget.setPointerCapture(event.pointerId)
    event.preventDefault()
  }
  function movePointerDrag(event: ReactPointerEvent<HTMLElement>) {
    const drag = pointerDrag.current
    if (!drag) return
    const paperRect = event.currentTarget.parentElement?.getBoundingClientRect()
    const block = layout.find(item => item.id === drag.id)
    if (!paperRect || !block) return
    const position = positionFor(block, layout.indexOf(block))
    const x = ((event.clientX - paperRect.left - drag.offsetX) / paperRect.width) * 100
    const y = ((event.clientY - paperRect.top - drag.offsetY) / paperRect.height) * 100
    updateBlock(block.id, { position: { ...position, x: snapPosition(x, 100 - position.width), y: snapPosition(y, 100 - position.height) } })
  }
  function beginPointerResize(event: ReactPointerEvent<HTMLButtonElement>, block: ShippingLabelBlock, direction: ShippingResizeDirection) {
    if (event.button !== 0) return
    event.preventDefault()
    event.stopPropagation()
    const paper = event.currentTarget.closest<HTMLElement>('.shipping-designer-paper')
    if (!paper) return
    pointerResize.current = { id: block.id, direction, startClientX: event.clientX, startClientY: event.clientY, position: positionFor(block, layout.indexOf(block)) }
    setSelectedId(block.id)
    event.currentTarget.setPointerCapture(event.pointerId)
  }
  function beginPointerRotate(event: ReactPointerEvent<HTMLButtonElement>, block: ShippingLabelBlock) {
    if (event.button !== 0) return
    event.preventDefault()
    event.stopPropagation()
    const blockElement = event.currentTarget.closest<HTMLElement>('.shipping-designer-block')
    if (!blockElement) return
    const rect = blockElement.getBoundingClientRect()
    const startAngle = Math.atan2(event.clientY - (rect.top + rect.height / 2), event.clientX - (rect.left + rect.width / 2)) * 180 / Math.PI
    pointerRotate.current = { id: block.id, startAngle, startRotation: block.rotation ?? 0 }
    setSelectedId(block.id)
    event.currentTarget.setPointerCapture(event.pointerId)
  }
  function movePointerRotate(event: ReactPointerEvent<HTMLElement>) {
    const rotate = pointerRotate.current
    if (!rotate) return
    const blockElement = event.currentTarget.closest<HTMLElement>('.shipping-designer-block')
    const block = layout.find(item => item.id === rotate.id)
    if (!blockElement || !block) return
    const rect = blockElement.getBoundingClientRect()
    const angle = Math.atan2(event.clientY - (rect.top + rect.height / 2), event.clientX - (rect.left + rect.width / 2)) * 180 / Math.PI
    let delta = angle - rotate.startAngle
    if (delta > 180) delta -= 360
    if (delta < -180) delta += 360
    const nextRotation = ((Math.round(rotate.startRotation + delta) % 360) + 360) % 360
    updateBlock(block.id, { rotation: nextRotation })
  }
  function movePointerResize(event: ReactPointerEvent<HTMLElement>) {
    const resize = pointerResize.current
    if (!resize) return
    const paper = event.currentTarget.closest<HTMLElement>('.shipping-designer-paper')
    if (!paper) return
    const rect = paper.getBoundingClientRect()
    const deltaX = ((event.clientX - resize.startClientX) / rect.width) * 100
    const deltaY = ((event.clientY - resize.startClientY) / rect.height) * 100
    const minimumWidth = 8
    const minimumHeight = 5
    const nextPosition = { ...resize.position }
    if (resize.direction.includes('w')) {
      const nextX = snapPosition(resize.position.x + deltaX, resize.position.x + resize.position.width - minimumWidth)
      nextPosition.x = nextX
      nextPosition.width = resize.position.x + resize.position.width - nextX
    } else if (resize.direction.includes('e')) {
      nextPosition.width = Math.max(minimumWidth, snapPosition(resize.position.width + deltaX, 100 - resize.position.x))
    }
    if (resize.direction.includes('n')) {
      const nextY = snapPosition(resize.position.y + deltaY, resize.position.y + resize.position.height - minimumHeight)
      nextPosition.y = nextY
      nextPosition.height = resize.position.y + resize.position.height - nextY
    } else if (resize.direction.includes('s')) {
      nextPosition.height = Math.max(minimumHeight, snapPosition(resize.position.height + deltaY, 100 - resize.position.y))
    }
    updateBlock(resize.id, { position: nextPosition })
  }
  function endPointerInteraction() { pointerDrag.current = null; pointerResize.current = null; pointerRotate.current = null }
  function saveTemplate(name: string) {
    const trimmed = name.trim()
    if (!trimmed) {
      setTemplateMessage('Şablon adı gerekli.')
      return
    }
    const template: ShippingLabelTemplate = { id: `template-${Date.now()}`, name: trimmed.slice(0, 80), savedAt: new Date().toISOString(), settings: JSON.parse(JSON.stringify(settings)) as ShippingLabelSettings }
    const next = [...templates.filter(item => item.name !== template.name), template]
    try {
      localStorage.setItem(shippingLabelTemplateStorageKey, JSON.stringify(next))
      setTemplates(next)
      setSelectedTemplateId(template.id)
      setTemplateNameOpen(false)
      setTemplateMessage(`“${template.name}” şablonu kaydedildi.`)
    } catch {
      setTemplateMessage('Şablon kaydedilemedi. Tarayıcı depolamasına erişim izni verin.')
    }
  }
  function applyTemplate(id: string) {
    setSelectedTemplateId(id)
    const template = templates.find(item => item.id === id)
    if (template) {
      const nextSettings = JSON.parse(JSON.stringify(template.settings)) as ShippingLabelSettings
      const nextLayout = nextSettings.layout[format]
      setSelectedId(nextLayout[0]?.id ?? null)
      setFontSizeDraft(nextLayout[0] ? String(nextLayout[0].fontSize ?? 14) : '')
      onChange(nextSettings)
    }
  }
  function deleteTemplate() {
    if (!selectedTemplateId) return
    const next = templates.filter(item => item.id !== selectedTemplateId)
    setTemplates(next)
    setSelectedTemplateId('')
    try { localStorage.setItem(shippingLabelTemplateStorageKey, JSON.stringify(next)) } catch { /* Private browsing may disallow local storage. */ }
  }
  function commitDimension(key: 'stickerWidthMm' | 'stickerHeightMm', value: string, fallback: number) {
    const parsed = Number(value)
    const next = Number.isFinite(parsed) && value.trim() ? Math.min(300, Math.max(40, parsed)) : fallback
    if (key === 'stickerWidthMm') setStickerWidthDraft(String(next)); else setStickerHeightDraft(String(next))
    update(key, next)
  }
  function resetLayout() {
    const next = defaultShippingLabelSettings.layout[format].map(block => ({ ...block, fields: [...block.fields], position: block.position ? { ...block.position } : undefined }))
    onChange({ ...settings, layout: { ...settings.layout, [format]: next } })
    setSelectedId(next[0]?.id ?? null)
  }
  function toggleField(block: ShippingLabelBlock, field: ShippingLabelField) {
    updateBlock(block.id, { fields: block.fields.includes(field) ? block.fields.filter(value => value !== field) : [...block.fields, field] })
  }
  function removeBlock(blockId: string) {
    const next = layout.filter(block => block.id !== blockId)
    onChange({ ...settings, layout: { ...settings.layout, [format]: next } })
    setSelectedId(next[0]?.id ?? null)
  }
  function addBlock(kind: ShippingLabelBlockKind, x = 10, y = 10) {
    const catalog = shippingLabelBlockCatalog.find(block => block.kind === kind)
    if (!catalog || (kind !== 'custom' && layout.some(block => block.kind === kind))) return null
    const id = kind === 'custom' ? `custom-${Date.now()}` : kind
    const block: ShippingLabelBlock = {
      id,
      kind,
      title: kind === 'custom' ? customTitle.trim() || 'Özel içerik' : catalog.label,
      fields: [...catalog.fields],
      align: kind === 'trackingBarcode' || kind === 'packageBarcode' ? 'center' : 'left',
      text: kind === 'custom' ? customText : '',
      position: { x: Math.min(92, Math.max(0, x)), y: Math.min(92, Math.max(0, y)), width: kind === 'trackingBarcode' || kind === 'packageBarcode' ? 84 : 80, height: kind === 'address' ? 25 : kind === 'custom' ? 14 : 13 }
    }
    onChange({ ...settings, layout: { ...settings.layout, [format]: [...layout, block] } })
    setSelectedId(block.id)
    if (kind === 'custom') { setCustomTitle(''); setCustomText('') }
    return block
  }
  function dragStart(event: DragEvent<HTMLElement>, value: string) {
    event.dataTransfer.setData('application/x-ravencia-label', value)
    event.dataTransfer.effectAllowed = value.startsWith('block:') ? 'move' : 'copy'
  }
  function dropOnCanvas(event: DragEvent<HTMLDivElement>) {
    event.preventDefault()
    const value = event.dataTransfer.getData('application/x-ravencia-label')
    if (!value) return
    const rect = event.currentTarget.getBoundingClientRect()
    const pointX = ((event.clientX - rect.left) / rect.width) * 100
    const pointY = ((event.clientY - rect.top) / rect.height) * 100
    if (value.startsWith('block:')) {
      const blockId = value.slice(6)
      const block = layout.find(item => item.id === blockId)
      if (!block) return
      const position = positionFor(block, layout.indexOf(block))
      updateBlock(block.id, { position: { ...position, x: snapPosition(pointX - position.width / 2, 100 - position.width), y: snapPosition(pointY - position.height / 2, 100 - position.height) } })
      setSelectedId(block.id)
      return
    }
    const kind = value.replace('catalog:', '') as ShippingLabelBlockKind
    const existing = layout.find(block => block.kind === kind)
    if (existing && kind !== 'custom') {
      const position = positionFor(existing, layout.indexOf(existing))
      updateBlock(existing.id, { position: { ...position, x: snapPosition(pointX - position.width / 2, 100 - position.width), y: snapPosition(pointY - position.height / 2, 100 - position.height) } })
      setSelectedId(existing.id)
      return
    }
    addBlock(kind, snapPosition(pointX - 40, 100 - 80), snapPosition(pointY - 7, 100 - 14))
  }
  return <div className="panel shipping-settings-panel shipping-designer-panel">
    <div className="shipping-settings-intro"><span className="security-state enabled">Etiket Tasarımcısı</span><h2>Kargo etiketi düzeni</h2><p>Alanları sağdaki kütüphaneden kâğıda sürükleyin. Seçili alanı taşıyın, boyutlandırın ve özelliklerini anında düzenleyin. Bu ayarlar hesabınıza kaydedilir ve tüm tarayıcılarda aynı uygulanır.</p></div>
    <div className="shipping-settings-grid shipping-designer-page-settings">
      <label>Gönderici adı<input value={settings.senderName} maxLength={120} onChange={event => update('senderName', event.target.value)} /></label>
      <label>Gönderici adresi<textarea value={settings.senderAddress} maxLength={500} rows={3} onChange={event => update('senderAddress', event.target.value)} placeholder="İsteğe bağlı" /></label>
      <label>A4 sayfa düzeni<select value={settings.a4LabelsPerPage} onChange={event => update('a4LabelsPerPage', Number(event.target.value) as ShippingLabelSettings['a4LabelsPerPage'])}><option value={1}>Sayfada 1 etiket</option><option value={2}>Sayfada 2 etiket</option><option value={4}>Sayfada 4 etiket</option></select></label>
      <label>Varsayılan çıktı<select value={settings.defaultFormat} onChange={event => update('defaultFormat', event.target.value as ShippingLabelSettings['defaultFormat'])}><option value="a4">Kargo etiketi · A4</option><option value="sticker">Kargo etiketi · Sticker</option></select></label>
      <label className="shipping-settings-check"><input type="checkbox" checked={settings.showA4Button} onChange={event => update('showA4Button', event.target.checked)} /><span className="shipping-settings-check-indicator"><UiIcon name="check" size={14} /></span><span>A4 yazdırma butonunu göster</span></label>
      <label className="shipping-settings-check"><input type="checkbox" checked={settings.showStickerButton} onChange={event => update('showStickerButton', event.target.checked)} /><span className="shipping-settings-check-indicator"><UiIcon name="check" size={14} /></span><span>Sticker yazdırma butonunu göster</span></label>
      <label>Sticker genişliği (mm)<input type="number" min={40} max={300} value={stickerWidthDraft} onChange={event => setStickerWidthDraft(event.target.value)} onBlur={() => commitDimension('stickerWidthMm', stickerWidthDraft, settings.stickerWidthMm)} /></label>
      <label>Sticker yüksekliği (mm)<input type="number" min={40} max={300} value={stickerHeightDraft} onChange={event => setStickerHeightDraft(event.target.value)} onBlur={() => commitDimension('stickerHeightMm', stickerHeightDraft, settings.stickerHeightMm)} /></label>
      <label>Bloklar arası boşluk (mm)<input type="number" min={0} max={20} value={settings.sectionGapMm} onChange={event => update('sectionGapMm', Math.min(20, Math.max(0, Number(event.target.value) || 0)))} /></label>
      <label className="shipping-settings-check"><input type="checkbox" checked={settings.showCustomerPhone} onChange={event => update('showCustomerPhone', event.target.checked)} /><span className="shipping-settings-check-indicator"><UiIcon name="check" size={14} /></span><span>Müşteri iletişim alanını göster</span></label>
    </div>
    <section className="shipping-layout-editor shipping-designer" aria-labelledby="shipping-designer-title">
      <header className="shipping-designer-toolbar"><div><span className="shipping-designer-kicker">Kâğıt çalışma alanı</span><h3 id="shipping-designer-title">Etiket tasarımını oluştur</h3></div><div className="shipping-designer-format-tabs" role="tablist"><button type="button" className={format === 'a4' ? 'is-active' : ''} role="tab" aria-selected={format === 'a4'} onClick={() => setFormat('a4')}>A4 <small>{settings.a4LabelsPerPage} etiket</small></button><button type="button" className={format === 'sticker' ? 'is-active' : ''} role="tab" aria-selected={format === 'sticker'} onClick={() => setFormat('sticker')}>Sticker <small>{settings.stickerWidthMm} × {settings.stickerHeightMm} mm</small></button></div><div className="shipping-designer-template-actions"><select aria-label="Kayıtlı şablon seçin" value={selectedTemplateId} onChange={event => applyTemplate(event.target.value)}><option value="">Şablon seçin…</option>{templates.map(template => <option key={template.id} value={template.id}>{template.name}</option>)}</select><button type="button" className="secondary" onClick={() => { setTemplateNameDraft(`${format === 'a4' ? 'A4' : 'Sticker'} şablonu`); setTemplateMessage(''); setTemplateNameOpen(true) }}>Şablon kaydet</button><button type="button" className="secondary" disabled={!selectedTemplateId} onClick={deleteTemplate}>Şablonu sil</button></div><button type="button" className="secondary" onClick={resetLayout}>Varsayılanı yükle</button></header>
      {templateNameOpen && <div className="shipping-template-save-dialog" role="dialog" aria-label="Şablon kaydet"><label>Şablon adı<input autoFocus value={templateNameDraft} maxLength={80} onChange={event => setTemplateNameDraft(event.target.value)} onKeyDown={event => { if (event.key === 'Enter') saveTemplate(templateNameDraft); if (event.key === 'Escape') setTemplateNameOpen(false) }} /></label><div><button type="button" className="secondary" onClick={() => setTemplateNameOpen(false)}>Vazgeç</button><button type="button" onClick={() => saveTemplate(templateNameDraft)}>Kaydet</button></div></div>}
      {templateMessage && <p className="shipping-template-status" role="status">{templateMessage}</p>}
      <div className="shipping-designer-workspace">
        <aside className="shipping-designer-palette"><div className="shipping-designer-panel-heading"><span>İçerik kütüphanesi</span><small>Sürükleyip kâğıda bırakın</small></div><div className="shipping-designer-palette-list">{shippingLabelBlockCatalog.map(item => <button type="button" key={item.kind} draggable onDragStart={event => dragStart(event, `catalog:${item.kind}`)} onClick={() => addBlock(item.kind)} disabled={item.kind !== 'custom' && layout.some(block => block.kind === item.kind)}><span className={`shipping-designer-palette-icon ${item.kind}`}><UiIcon name={item.kind === 'trackingBarcode' || item.kind === 'packageBarcode' ? 'barcode' : item.kind === 'custom' ? 'sparkle' : 'layout'} /></span><span><strong>{item.label}</strong><small>{item.description}</small></span><UiIcon name="plus" /></button>)}</div><div className="shipping-designer-custom-fields"><label>Özel blok başlığı<input value={customTitle} onChange={event => setCustomTitle(event.target.value)} placeholder="Örn. mağaza notu" /></label><label>Özel blok içeriği<textarea value={customText} onChange={event => setCustomText(event.target.value)} rows={3} placeholder="Bu blokta görünecek metin" /></label></div></aside>
        <main className="shipping-designer-stage"><div className="shipping-designer-stage-bar"><span>{format === 'a4' ? 'A4 çalışma yüzeyi' : 'Sticker çalışma yüzeyi'}</span><small>Izgara görünümü · %{layout.length ? layout.length : 0} blok yerleşti</small></div><div className={`shipping-designer-paper is-${format}`} onDragOver={event => event.preventDefault()} onDrop={dropOnCanvas} style={{ aspectRatio: format === 'a4' ? '210 / 297' : `${settings.stickerWidthMm} / ${settings.stickerHeightMm}` }} role="application" aria-label={`${format === 'a4' ? 'A4' : 'Sticker'} etiket tasarım alanı`}>{layout.map((block, index) => { const position = positionFor(block, index); return <article key={block.id} className={`shipping-designer-block${activeBlock?.id === block.id ? ' is-selected' : ''}`} draggable onDragStart={event => dragStart(event, `block:${block.id}`)} onPointerDown={event => beginPointerDrag(event, block)} onPointerMove={movePointerDrag} onPointerUp={endPointerInteraction} onPointerCancel={endPointerInteraction} onClick={() => setSelectedId(block.id)} style={{ left: `${position.x}%`, top: `${position.y}%`, width: `${position.width}%`, height: `${position.height}%`, textAlign: block.align, fontSize: `${block.fontSize ?? 14}px`, transform: block.rotation ? `rotate(${block.rotation}deg)` : undefined, transformOrigin: 'center' }}>{activeBlock?.id === block.id && <>{shippingRotateDirections.map(direction => <button key={direction} type="button" className={`shipping-designer-resize-handle shipping-designer-rotate-handle handle-${direction}`} aria-label={`${shippingRotateDirectionLabels[direction]} tutamacını sağa veya sola sürükleyerek derece derece döndür`} title={`${shippingRotateDirectionLabels[direction]} tutamacını sürükleyerek derece derece döndür`} onPointerDown={event => beginPointerRotate(event, block)} onPointerMove={movePointerRotate} onPointerUp={endPointerInteraction} onPointerCancel={endPointerInteraction} />)}{shippingResizeDirections.map(direction => <button key={direction} type="button" className={`shipping-designer-resize-handle handle-${direction}`} aria-label={`${shippingResizeDirectionLabels[direction]} boyutlandır`} title={`${shippingResizeDirectionLabels[direction]} boyutlandır`} onPointerDown={event => beginPointerResize(event, block, direction)} onPointerMove={movePointerResize} onPointerUp={endPointerInteraction} onPointerCancel={endPointerInteraction} />)}</>}<div className="shipping-designer-block-preview"><ShippingPreviewBlock block={block} /></div></article>})}{layout.length === 0 && <div className="shipping-designer-drop-empty"><strong>İçerik bırakın</strong><span>Sağ panelden bir alanı buraya sürükleyin.</span></div>}</div><div className="shipping-designer-stage-help">Bloğu sürükleyerek taşıyın · Kenar tutamaçlarını sağa veya sola sürükleyerek derece derece döndürün · Köşe tutamaçlarıyla boyutlandırın · Sağ panelden yazı ve alanları düzenleyin</div></main>
        <aside className="shipping-designer-inspector"><div className="shipping-designer-panel-heading"><span>Özellikler</span><small>{activeBlock ? `Seçili: ${activeBlock.title}` : 'Bir alan seçin'}</small></div><nav className="shipping-designer-inspector-tabs" role="tablist"><button type="button" className={designerTab === 'general' ? 'is-active' : ''} onClick={() => setDesignerTab('general')}>Genel</button><button type="button" className={designerTab === 'text' ? 'is-active' : ''} onClick={() => setDesignerTab('text')}>Yazı</button><button type="button" className={designerTab === 'barcode' ? 'is-active' : ''} onClick={() => setDesignerTab('barcode')}>Barkod</button></nav>{!activeBlock ? <div className="shipping-designer-empty">Düzenlemek için kâğıt üzerindeki bir bloğa tıklayın.</div> : <div className="shipping-designer-inspector-body">{designerTab === 'general' && <><label>Blok türü<select value={activeBlock.kind} onChange={event => { const kind = event.target.value as ShippingLabelBlockKind; const catalog = shippingLabelBlockCatalog.find(item => item.kind === kind); if (catalog) updateBlock(activeBlock.id, { kind, title: kind === 'custom' ? activeBlock.title : catalog.label, fields: [...catalog.fields], text: kind === 'custom' ? activeBlock.text : '' }) }}>{shippingLabelBlockCatalog.map(item => <option key={item.kind} value={item.kind}>{item.label}</option>)}</select></label><label>Hizalama<select value={activeBlock.align} onChange={event => updateBlock(activeBlock.id, { align: event.target.value as ShippingLabelAlignment })}><option value="left">Sol</option><option value="center">Orta</option><option value="right">Sağ</option></select></label><p className="shipping-designer-inspector-hint">Boyutu doğrudan kâğıt üzerindeki tutamaçlardan ayarlayın.</p><label>Alana ekle<select value="" onChange={event => { if (event.target.value) toggleField(activeBlock, event.target.value as ShippingLabelField) }}><option value="">Bir alan seçin…</option>{shippingLabelFields.filter(item => !activeBlock.fields.includes(item.id)).map(item => <option value={item.id} key={item.id}>{item.label}</option>)}</select></label><div className="shipping-designer-field-chips">{activeBlock.fields.length ? activeBlock.fields.map(field => <button type="button" key={field} onClick={() => toggleField(activeBlock, field)}>{shippingLabelFields.find(item => item.id === field)?.label}<UiIcon name="close" /></button>) : <span>Alan eklenmedi</span>}</div></>}{designerTab === 'text' && <><label>Yazı hizası<select value={activeBlock.align} onChange={event => updateBlock(activeBlock.id, { align: event.target.value as ShippingLabelAlignment })}><option value="left">Sol</option><option value="center">Orta</option><option value="right">Sağ</option></select></label><div className="shipping-designer-font-size-control"><div className="shipping-designer-font-size-head"><span>Yazı boyutu</span><output>{fontSizeDraft || String(activeBlock.fontSize ?? 14)} px</output></div><input type="range" min={8} max={72} step={1} value={Number(fontSizeDraft || activeBlock.fontSize || 14)} aria-label="Yazı boyutu" onChange={event => { const next = Number(event.target.value); setFontSizeDraft(String(next)); updateBlock(activeBlock.id, { fontSize: next }) }} /><div className="shipping-designer-range-scale"><span>8 px</span><span>72 px</span></div></div><p className="shipping-designer-help">Yazı rengi sabit olarak siyahtır.</p>{activeBlock.kind === 'custom' && <label>Metin<textarea value={activeBlock.text} maxLength={500} rows={8} onChange={event => updateBlock(activeBlock.id, { text: event.target.value })} /></label>}<p className="shipping-designer-help">Alanların gerçek değerleri sipariş yazdırılırken otomatik doldurulur.</p></>}{designerTab === 'barcode' && <div className="shipping-designer-barcode-settings"><strong>{activeBlock.kind === 'packageBarcode' ? 'Paket barkodu' : activeBlock.kind === 'trackingBarcode' ? 'Takip barkodu' : 'Barkod ayarı'}</strong><p>Barkod genişliği tuvaldeki blok genişliğine göre otomatik ölçeklenir. Modül aralıkları tarayıcı okunabilirliğini korur.</p><label>Alan<select value={activeBlock.fields[0] ?? ''} onChange={event => { if (event.target.value) updateBlock(activeBlock.id, { fields: [event.target.value as ShippingLabelField] }) }}><option value="">Alan seçin…</option>{shippingLabelFields.filter(field => field.id === 'trackingNumber' || field.id === 'packageNumber').map(field => <option value={field.id} key={field.id}>{field.label}</option>)}</select></label></div>}{activeBlock && <div className="shipping-designer-rotation-control" aria-label="Blok döndürme"><div className="shipping-designer-rotation-head"><span>Döndürme</span><output>{activeBlock.rotation ?? 0}°</output></div><div className="shipping-designer-rotation-actions"><button type="button" className="secondary" aria-label="Sola 90 derece döndür" title="Sola 90 derece döndür" onClick={() => rotateActiveBlock(-90)}>−90°</button><button type="button" className="secondary" aria-label="Sağa 90 derece döndür" title="Sağa 90 derece döndür" onClick={() => rotateActiveBlock(90)}>+90°</button><button type="button" className="secondary" aria-label="Döndürmeyi sıfırla" title="Döndürmeyi sıfırla" onClick={() => updateBlock(activeBlock.id, { rotation: 0 })} disabled={!activeBlock.rotation}>0°</button></div></div>}<button type="button" className="shipping-designer-delete" onClick={() => removeBlock(activeBlock.id)}>Bloğu kaldır</button></div>}</aside>
       </div>
     </section>
     <SettingsStickyActions className="shipping-settings-actions"><div className="settings-sticky-copy"><strong>Etiket düzenini yayınla</strong><span>Gönderici bilgileri ve kâğıt yerleşimi tüm yazdırma akışlarında kullanılır.</span></div><button type="button" onClick={onSave}>Ayarları kaydet</button></SettingsStickyActions>
  </div>
}

function ShippingPreviewBlock({ block }: { block: ShippingLabelBlock }) {
  if (block.kind === 'trackingBarcode' || block.kind === 'packageBarcode') {
    const value = block.kind === 'trackingBarcode' ? '73300036563130080' : '9236253'
    const bars = code128Bars(value)
    return <div className="shipping-preview-barcode" style={{ fontSize: `${block.fontSize ?? 14}px` }} aria-label={`Barkod önizlemesi: ${value}`}><span className="shipping-preview-bars" style={{ '--barcode-module-count': bars.length } as CSSProperties} aria-hidden="true">{bars.map((isBar, index) => <i className={isBar ? 'is-bar' : undefined} key={index} />)}</span><strong>{value}</strong></div>
  }
  const values: Record<ShippingLabelField, string> = { trackingNumber: '73300036563130080', packageNumber: '9236253', orderNumber: '#1972215187', customerName: 'FEHİME MAT', address: 'KARAKÖPRÜ / ŞANLIURFA', cargoProvider: 'Trendyol Express', senderName: 'RAVENCIA', senderAddress: '403.CAD.NO:24/2', customerEmail: 'musteri@example.com' }
  return <div className={`shipping-preview-content preview-content-${block.kind}`} style={{ fontSize: `${block.fontSize ?? 14}px` }}>{block.kind === 'custom' && <strong className="shipping-preview-title">{block.title}</strong>}{block.text && <strong>{block.text}</strong>}{block.fields.map(field => <span key={field}><b>{shippingLabelFields.find(option => option.id === field)?.label}</b>{values[field]}</span>)}</div>
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
