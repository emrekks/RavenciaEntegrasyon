import { Suspense, useEffect, useRef, useState, type CSSProperties, type DragEvent, type FormEvent, type PointerEvent as ReactPointerEvent } from 'react'
import { Link, Navigate, NavLink, Route, Routes, useLocation, useNavigate, useSearchParams } from 'react-router'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api, ApiRequestError, hubApi, type Me, type TenantOption } from '../shared/api'
import { UiIcon, type UiIconName } from '../shared/components'
import { AttributesPage, AttributeMappingPage, BrandsPage, CategoriesPage, ImportDetailPage, ImportsPage, InventoryPage, NewProductPage, ProductDetailPage, ProductsPage, IntegrationDetailPage, IntegrationsPage, MappingPage, OrdersPage, ReturnDetailPage, ReturnsPage, ShipmentDetailPage, ShipmentsPage, BillingSettingsPage, JobsPage } from './route-components'
import { useOperationsRealtime } from './hooks/useOperationsRealtime'
import { code128Bars, defaultShippingLabelBlockPosition, defaultShippingLabelSettings, shippingLabelBlockCatalog, shippingLabelFields, useShippingLabelSettings, type ShippingLabelAlignment, type ShippingLabelBlock, type ShippingLabelBlockKind, type ShippingLabelField, type ShippingLabelSettings } from '../features/shipping'
import { appearanceColorCssVariable, appearanceColorTokenOptions, appearanceFontFamilyCss, appearanceFontFamilyOptions, appearanceFontScale, appearanceFontSizeOptions, appearanceThemeModeOptions, defaultAppearanceSettings, useAppearanceSettings, type AppearanceColorTheme, type AppearanceSettings } from '../features/settings/appearance-settings'

function Shell({ me }: { me: Me }) {
  const appearanceSettings = useAppearanceSettings()
  const location = useLocation()
  const [sidebarHoverExpanded, setSidebarHoverExpanded] = useState(false)
  const [sidebarPinned, setSidebarPinned] = useState(() => localStorage.getItem('ravencia.sidebarPinned') === 'true')
  const sidebarHoverTimer = useRef<number | null>(null)
  const sidebarRef = useRef<HTMLElement>(null)
  const appearanceColorsKey = JSON.stringify(appearanceSettings.settings.colors)
  async function logout() { await api('/logout', { method: 'POST' }); window.location.replace(`/?signedOut=${Date.now()}`) }
  const sidebarExpanded = sidebarPinned || sidebarHoverExpanded
  const menuCollapsed = !sidebarExpanded
  useEffect(() => {
    document.documentElement.style.setProperty('--rv-font-ui', appearanceFontFamilyCss[appearanceSettings.settings.fontFamily])
    document.documentElement.style.setProperty('--rv-font-scale', String(appearanceFontScale[appearanceSettings.settings.fontSize]))
    const media = window.matchMedia('(prefers-color-scheme: dark)')
    const applyTheme = () => {
      const resolvedTheme = appearanceSettings.settings.themeMode === 'system' ? (media.matches ? 'dark' : 'light') : appearanceSettings.settings.themeMode
      document.documentElement.dataset.theme = resolvedTheme
      document.documentElement.dataset.themeMode = appearanceSettings.settings.themeMode
      const palette = appearanceSettings.settings.colors[resolvedTheme]
      appearanceColorTokenOptions.forEach(({ key }) => document.documentElement.style.setProperty(appearanceColorCssVariable(key), palette[key]))
    }
    applyTheme()
    media.addEventListener('change', applyTheme)
    return () => media.removeEventListener('change', applyTheme)
  }, [appearanceSettings.settings.fontFamily, appearanceSettings.settings.fontSize, appearanceSettings.settings.themeMode, appearanceColorsKey])
  const pageNames: Record<string, string> = { '/dashboard': 'Dashboard', '/products': 'Ürünler', '/products/new': 'Yeni ürün', '/orders': 'Siparişler', '/returns': 'İadeler', '/jobs': 'İşlem takibi', '/integrations': 'Platformlar', '/mappings/categories': 'Eşleştirme ayarları', '/settings': 'Sistem ayarları', '/settings/appearance': 'Görünüm ayarları' }
  const pageName = pageNames[location.pathname] ?? (location.pathname.startsWith('/products/') ? 'Ürün detayları' : location.pathname.startsWith('/returns/') ? 'İade detayları' : 'Ravencia')
  function expandSidebarOnHover() {
    if (sidebarHoverTimer.current !== null) window.clearTimeout(sidebarHoverTimer.current)
    setSidebarHoverExpanded(true)
  }
  function collapseSidebarOnLeave() {
    if (sidebarPinned) return
    if (sidebarHoverTimer.current !== null) window.clearTimeout(sidebarHoverTimer.current)
    sidebarHoverTimer.current = null
    setSidebarHoverExpanded(false)
  }
  function toggleSidebarPinned() {
    const nextPinned = !sidebarPinned
    setSidebarPinned(nextPinned)
    localStorage.setItem('ravencia.sidebarPinned', String(nextPinned))
    if (nextPinned) setSidebarHoverExpanded(true)
  }
  useEffect(() => {
    if (sidebarPinned) return
    const collapseIfPointerIsOutside = (event: PointerEvent) => {
      if (!sidebarHoverExpanded) return
      const rect = sidebarRef.current?.getBoundingClientRect()
      if (!rect) return
      if (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom) setSidebarHoverExpanded(false)
    }
    const collapseOnWindowBlur = () => setSidebarHoverExpanded(false)
    window.addEventListener('pointermove', collapseIfPointerIsOutside)
    window.addEventListener('blur', collapseOnWindowBlur)
    return () => {
      window.removeEventListener('pointermove', collapseIfPointerIsOutside)
      window.removeEventListener('blur', collapseOnWindowBlur)
      if (sidebarHoverTimer.current !== null) window.clearTimeout(sidebarHoverTimer.current)
    }
  }, [sidebarHoverExpanded, sidebarPinned])
  const icon = (name: UiIconName) => <UiIcon className="nav-icon" name={name} size={22} />
  const item = (to: string, iconName: UiIconName, label: string, end = false) => <NavLink to={to} end={end}>{icon(iconName)}<span className="nav-label">{label}</span></NavLink>
  return <div className={`app-shell stitch-shell ${menuCollapsed ? 'sidebar-collapsed' : ''} ${sidebarExpanded ? 'sidebar-hover-expanded' : ''} ${sidebarPinned ? 'sidebar-pinned' : ''}`}>
    <aside ref={sidebarRef} onPointerEnter={expandSidebarOnHover} onPointerLeave={collapseSidebarOnLeave} onFocus={expandSidebarOnHover} onBlur={event => { if (!event.currentTarget.contains(event.relatedTarget as Node | null)) collapseSidebarOnLeave() }}>
      <div className="sidebar-brand-row"><div className="stitch-brand-mark" aria-hidden="true">R</div><div className="brand wordmark"><strong>Ravencia</strong><small>MarketplaceHub</small></div><button type="button" className={`sidebar-pin-toggle ${sidebarPinned ? 'is-pinned' : ''}`} aria-label={sidebarPinned ? 'Menü sabitlemesini kaldır' : 'Menüyü sabitle'} aria-pressed={sidebarPinned} title={sidebarPinned ? 'Menü sabitlendi' : 'Menüyü sabitle'} onClick={toggleSidebarPinned}><UiIcon name="pin" size={18} /></button></div>
      <nav aria-label="Ana menü">{item('/dashboard', 'dashboard', 'Dashboard')}{item('/products', 'products', 'Ürünler')}{item('/orders', 'orders', 'Siparişler')}{item('/returns', 'returns', 'İadeler')}{item('/jobs', 'jobs', 'İşlem Takibi')}{item('/integrations', 'platforms', 'Platformlar')}{item('/mappings/categories', 'mappings', 'Eşleştirme Ayarları')}</nav>
      <div className="settings-nav">{item('/settings', 'settings', 'Sistem Ayarları', true)}<button type="button" className="logout-link" onClick={() => void logout()}>{icon('logout')}<span className="nav-label">Çıkış Yap</span></button></div>
    </aside>
    <main>
      <header className="rv-topbar"><div className="rv-topbar-context"><small>Ravencia / Operasyon Merkezi</small><strong>{pageName}</strong></div><div className="rv-topbar-actions"><span className="rv-user-chip"><small>Çalışma alanı</small><strong>{me.displayName || me.email}</strong></span></div></header>
      <Suspense fallback={<Status title="Ekran yükleniyor" />}><Routes><Route path="/dashboard" element={<Dashboard me={me} />} /><Route path="/products" element={<ProductsPage />} /><Route path="/products/new" element={<NewProductPage />} /><Route path="/products/:id" element={<ProductDetailPage />} /><Route path="/catalog/categories" element={<CategoriesPage />} /><Route path="/catalog/brands" element={<BrandsPage />} /><Route path="/catalog/attributes" element={<AttributesPage />} /><Route path="/imports" element={<ImportsPage />} /><Route path="/imports/:id" element={<ImportDetailPage />} /><Route path="/inventory" element={<InventoryPage />} /><Route path="/integrations" element={<IntegrationsPage />} /><Route path="/integrations/:id" element={<IntegrationDetailPage />} /><Route path="/mappings/categories" element={<MappingPage />} /><Route path="/mappings/attributes" element={<AttributeMappingPage />} /><Route path="/orders" element={<OrdersPage />} /><Route path="/orders/:id" element={<Navigate to="/orders" replace />} /><Route path="/returns" element={<ReturnsPage />} /><Route path="/returns/:id" element={<ReturnDetailPage />} /><Route path="/shipments" element={<ShipmentsPage />} /><Route path="/shipments/:id" element={<ShipmentDetailPage />} /><Route path="/invoices" element={<Navigate to="/orders" replace />} /><Route path="/invoices/:id" element={<Navigate to="/orders" replace />} /><Route path="/jobs" element={<JobsPage me={me} />} /><Route path="/settings/billing" element={<BillingSettingsPage />} /><Route path="/settings/security" element={<Navigate to="/settings?tab=security" replace />} /><Route path="/settings/appearance" element={<AppearanceSettingsPage />} /><Route path="/settings" element={<Security />} /><Route path="*" element={<Navigate to="/dashboard" replace />} /></Routes></Suspense>
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
  if (me.isLoading) return null
  if (me.isError) return <Routes><Route path="*" element={<Login />} /></Routes>
  if (!me.data) return <Status title="Oturum bilgisi alınamadı" />
  if (me.data.state === 'PASSWORD_CHANGE_REQUIRED') return <ChangePassword />
  if (me.data.state === 'MFA_CHALLENGE') return <MfaChallenge />
  if (me.data.state !== 'ACTIVE') return <Status title="Oturum kilitli" detail="Yeniden giriş yapın." />
  return <Shell me={me.data} />
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

  return <main className="rv-auth-page rv-auth-cyber">
    <div className="rv-auth-cyber-grid" aria-hidden="true" />
    <section className="rv-auth-visual" aria-label="Ravencia operasyon merkezi">
      <header className="rv-auth-brandbar">
        <div className="rv-auth-brand-lockup"><img className="rv-auth-symbol" src="/pack/brand/ravencia-symbol-transparent.png" alt="" /><img className="rv-auth-wordmark" src="/pack/brand/ravencia-wordmark-transparent.png" alt="Ravencia MarketplaceHub" /></div>
        <span className="rv-auth-system-state"><i /> SYSTEM ONLINE</span>
      </header>
      <div className="rv-auth-hero">
        <div className="rv-auth-hero-meta"><span>RV / 01</span><span>OPERATIONS CORE</span></div>
        <h1>Marketplace<br /><em>operations</em><br />reimagined.</h1>
        <p className="rv-auth-hero-lede">Sipariş, stok ve entegrasyon akışlarını tek bir komuta merkezinden yönetin.</p>
        <div className="rv-auth-signal-row" aria-label="Operasyon sinyalleri"><span><b>01</b><small>ORDER FLOW</small></span><span><b>02</b><small>LIVE SYNC</small></span><span><b>03</b><small>SECURE DATA</small></span></div>
      </div>
      <footer className="rv-auth-visual-footer"><span>RAVENCIA / MARKETPLACEHUB</span><span>07—09—2026</span></footer>
    </section>

    <section className="rv-auth-login-panel">
      <div className="rv-auth-login-shell">
        <div className="rv-auth-login-top"><span>ACCESS GATE <b>02</b></span><span><i /> ENCRYPTED SESSION</span></div>
        <div className="rv-auth-card">
          <div className="rv-auth-card-glow" aria-hidden="true" />
          <header className="rv-auth-login-header"><div className="rv-auth-card-icon" aria-hidden="true"><svg viewBox="0 0 24 24"><path d="M12 3 5 6v5c0 5 3 8 7 10 4-2 7-5 7-10V6l-7-3Z" /><path d="m9 12 2 2 4-5" /></svg></div><div><p>YETKİLİ ERİŞİM</p><h2>Komuta merkezine<br />giriş yapın.</h2><span>Ravencia hesabınızla devam edin.</span></div></header>
          <div className="rv-auth-progress" aria-hidden="true"><span /><span /><span /></div>
          <form className="rv-auth-form" onSubmit={submit}>
            <div className="rv-auth-field"><label htmlFor="login-email">E-posta adresi</label><div className="rv-auth-control"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 4h16v16H4z" /><path d="m4 7 8 6 8-6" /></svg><input id="login-email" name="email" type="email" value={email} onChange={event => setEmail(event.target.value)} required autoComplete="username" placeholder="ornek@ravencia.com" autoFocus /></div></div>
            <div className="rv-auth-field"><div className="rv-auth-label-row"><label htmlFor="login-password">Parola</label><button type="button" onClick={() => setError('Parola sıfırlama için sistem yöneticinizle iletişime geçin.')}>Parolamı unuttum</button></div><div className="rv-auth-control"><svg viewBox="0 0 24 24" aria-hidden="true"><rect x="4" y="10" width="16" height="11" rx="2" /><path d="M8 10V7a4 4 0 0 1 8 0v3" /></svg><input id="login-password" name="password" type={showPw ? 'text' : 'password'} required autoComplete="current-password" placeholder="Parolanızı girin" /><button type="button" className="rv-auth-password-toggle" aria-label={showPw ? 'Parolayı gizle' : 'Parolayı göster'} onClick={() => setShowPw(value => !value)}><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12Z" /><circle cx="12" cy="12" r="3" /></svg></button></div></div>
            {tenantOptions.length > 0 && <div className="rv-auth-field"><label htmlFor="login-tenant">Çalışma alanı</label><select id="login-tenant" value={tenantId} onChange={event => setTenantId(event.target.value)} required><option value="">Çalışma alanı seçin</option>{tenantOptions.map(option => <option key={option.id} value={option.id}>{option.displayName}</option>)}</select></div>}
            <label className="rv-auth-remember"><input type="checkbox" checked={rememberMe} onChange={event => setRememberMe(event.target.checked)} /><span>E-posta adresimi bu cihazda hatırla</span></label>
            {error && <div className="rv-auth-error" role="alert"><i /><span>{error}</span></div>}
            <button className="rv-auth-submit" type="submit" disabled={loading}>{loading ? <><i /> Oturum doğrulanıyor…</> : <><span>Güvenli giriş yap</span><UiIcon name="arrowRight" /></>}</button>
          </form>
          <footer className="rv-auth-footer"><span><i /> TLS ile şifrelenmiş bağlantı</span><small>Ravencia · Yetkili erişim</small></footer>
        </div>
      </div>
    </section>
  </main>
}

function ChangePassword() { const client = useQueryClient(); const [message, setMessage] = useState('İlk girişte parolanızı değiştirmeniz gerekir.'); async function submit(event: FormEvent<HTMLFormElement>) { event.preventDefault(); const data = new FormData(event.currentTarget); try { await api('/change-password', { method: 'POST', body: JSON.stringify({ currentPassword: data.get('current'), newPassword: data.get('next') }) }); await client.invalidateQueries({ queryKey: ['me'] }) } catch { setMessage('Parola değiştirilemedi; politika ve mevcut parolayı kontrol edin.') } } return <div className="auth-page"><section className="auth-card"><h1>Parolanızı değiştirin</h1><p role="status">{message}</p><form onSubmit={submit}><label>Geçerli parola<input name="current" type="password" required /></label><label>Yeni parola<input name="next" type="password" minLength={15} maxLength={64} required /></label><button>Parolayı değiştir</button></form></section></div> }
function MfaChallenge() { const client = useQueryClient(); const [error, setError] = useState(''); async function submit(event: FormEvent<HTMLFormElement>) { event.preventDefault(); const data = new FormData(event.currentTarget); try { await api('/mfa/challenge', { method: 'POST', body: JSON.stringify({ code: data.get('code'), recoveryCode: data.get('recovery') || null }) }); await client.invalidateQueries({ queryKey: ['me'] }) } catch { setError('Kod geçersiz veya daha önce kullanılmış.') } } return <div className="auth-page"><section className="auth-card"><h1>İki adımlı doğrulama</h1><p>Authenticator uygulamanızdaki 6 haneli kodu girin.</p><form onSubmit={submit}><label>Doğrulama kodu<input name="code" inputMode="numeric" pattern="[0-9]{6}" /></label><label>Kurtarma kodu (alternatif)<input name="recovery" /></label>{error && <div role="alert" className="error">{error}</div>}<button>Doğrula</button></form></section></div> }
type DashboardMetrics = { pendingOrders: number; lateOrders: number; todayOrders: number; todayProductQuantity: number; monthOrders: number; monthProductQuantity: number; pendingReturns: number; dueSoonInvoices: number; uninvoicedInvoices: number; lowStockProducts: number; activeConnections: number; pendingByPlatform: Record<string, number> }
type DashboardLowStock = { id: string; title: string; totalStock: number; primaryImageUrl: string | null }
type DashboardSyncStatus = { resourceType: string; label: string; kind: string; status: string; lastAttemptAt: string | null; lastSuccessAt: string | null; lastErrorCode: string | null }
type DashboardBootstrap = { metrics: DashboardMetrics; lowStock: DashboardLowStock[]; sync: DashboardSyncStatus[]; platforms: { name: string; status: string }[]; generatedAt: string; version: number }
type DashboardRevenuePoint = { day: string; amount: number; orderCount: number; currency: string }
type DashboardRevenueRange = '1' | '3' | '7' | '14' | '30' | 'month' | 'custom'

function dashboardDateKey(value: Date) {
  if (Number.isNaN(value.getTime())) return ''
  const pad = (part: number) => String(part).padStart(2, '0')
  return `${value.getFullYear()}-${pad(value.getMonth() + 1)}-${pad(value.getDate())}`
}

function dashboardDateInputValue(value = new Date()) { return dashboardDateKey(value) }
function DashboardMetricIcon({ kind }: { kind: string }) {
  const icons: Record<string, UiIconName> = {
    pending: 'pendingOrders',
    late: 'lateOrders',
    today: 'todayOrders',
    month: 'monthOrders',
    return: 'pendingReturns',
    invoice: 'invoiceDue',
    uninvoiced: 'invoicePending',
    stock: 'stock',
  }
  return <span className={`dashboard-metric-icon ${kind}`} aria-hidden="true"><UiIcon name={icons[kind] ?? 'pendingOrders'} size={22} /></span>
}

function dashboardSyncTime(sync: DashboardSyncStatus | undefined) {
  if (!sync || !sync.lastSuccessAt) return 'Kayıt yok'
  const timestamp = new Date(sync.lastSuccessAt)
  if (Number.isNaN(timestamp.getTime())) return 'Kayıt yok'
  const minutes = Math.max(0, Math.floor((Date.now() - timestamp.getTime()) / 60_000))
  if (minutes < 1) return 'Az önce'
  if (minutes < 60) return `${minutes} dk önce`
  const hours = Math.floor(minutes / 60)
  if (hours < 24) return `${hours} sa önce`
  return timestamp.toLocaleDateString('tr-TR', { day: 'numeric', month: 'short' }) + ` ${timestamp.toLocaleTimeString('tr-TR', { hour: '2-digit', minute: '2-digit' })}`
}

function dashboardMoney(amount: number, currency = 'TRY') {
  return new Intl.NumberFormat('tr-TR', { style: 'currency', currency: currency || 'TRY', maximumFractionDigits: 2 }).format(amount)
}

function dashboardAxisMoney(amount: number, currency = 'TRY') {
  const value = Math.abs(Math.round(amount)); const sign = amount < 0 ? '-' : ''; const prefix = currency === 'TRY' ? '₺' : `${currency || 'TRY'} `
  if (value >= 1_000_000) return `${sign}${prefix}${(value / 1_000_000).toLocaleString('tr-TR', { maximumFractionDigits: 1 })}m`
  if (value >= 1_000) return `${sign}${prefix}${(value / 1_000).toLocaleString('tr-TR', { maximumFractionDigits: 1 })}k`
  return `${sign}${prefix}${value.toLocaleString('tr-TR')}`
}

function dashboardNiceAxisStep(maxValue: number, targetSteps = 10) {
  const roughStep = Math.max(1, maxValue) / targetSteps
  const magnitude = 10 ** Math.floor(Math.log10(roughStep))
  const normalized = roughStep / magnitude
  const factor = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10
  return factor * magnitude
}

function Dashboard({ me }: { me: Me }) {
  const [revenueRange, setRevenueRange] = useState<DashboardRevenueRange>('30')
  const [revenueFrom, setRevenueFrom] = useState(() => dashboardDateInputValue())
  const [revenueTo, setRevenueTo] = useState(() => dashboardDateInputValue())
  const [revenuePlatform, setRevenuePlatform] = useState('ALL')
  const dashboardRefreshOptions = { refetchInterval: 60_000, refetchIntervalInBackground: true, refetchOnWindowFocus: true, staleTime: 30_000 } as const
  const bootstrap = useQuery({ queryKey: ['dashboard-bootstrap'], queryFn: () => hubApi<DashboardBootstrap>('/dashboard/bootstrap'), ...dashboardRefreshOptions })
  const loading = bootstrap.isLoading
  const now = new Date(); const metrics = bootstrap.data?.metrics
  const revenuePlatformOptions = (bootstrap.data?.platforms ?? []).map(platform => platform.name)
  const customFromDate = new Date(`${revenueFrom}T00:00:00`)
  const customToDate = new Date(`${revenueTo}T23:59:59.999`)
  const customStart = customFromDate <= customToDate ? customFromDate : customToDate
  const customEnd = customFromDate <= customToDate ? customToDate : customFromDate
  const revenueStart = revenueRange === 'custom'
    ? customStart
    : revenueRange === 'month'
      ? new Date(now.getFullYear(), now.getMonth(), 1)
      : (() => { const date = new Date(now); date.setHours(0, 0, 0, 0); date.setDate(date.getDate() - (Number(revenueRange) - 1)); return date })()
  const revenueEnd = revenueRange === 'custom' ? customEnd : now
  const revenueQuery = useQuery({ queryKey: ['dashboard-revenue-series', revenueRange, revenueFrom, revenueTo, revenuePlatform], queryFn: () => hubApi<DashboardRevenuePoint[]>(`/dashboard/revenue-series?from=${encodeURIComponent(revenueStart.toISOString())}&to=${encodeURIComponent(revenueEnd.toISOString())}&platform=${encodeURIComponent(revenuePlatform)}`), ...dashboardRefreshOptions })
  const revenueSeries = (revenueQuery.data ?? []).map((point, index) => {
    const date = new Date(point.day)
    const month = date.toLocaleDateString('tr-TR', { month: 'short' }).replace(/\.$/, '')
    return { ...point, key: dashboardDateKey(date), label: index === 0 || date.getDate() === 1 ? `${date.getDate()} ${month}` : String(date.getDate()), fullLabel: date.toLocaleDateString('tr-TR', { day: 'numeric', month: 'long', year: 'numeric' }) }
  })
  const maxRevenue = Math.max(1, ...revenueSeries.map(item => item.amount))
  const revenueAxisStep = dashboardNiceAxisStep(maxRevenue, 5)
  const revenueAxisMax = revenueAxisStep * 5
  const revenueCurrency = revenueSeries.find(item => item.amount > 0)?.currency || revenueSeries[0]?.currency || 'TRY'
  const revenueAxisTicks = Array.from({ length: 6 }, (_, index) => {
    const ratio = index / 5
    return { ratio, amount: revenueAxisMax * ratio, major: true }
  })
  const revenueLabelEvery = Math.max(1, Math.ceil(revenueSeries.length / 8))
  const revenueTotal = revenueSeries.reduce((sum, item) => sum + item.amount, 0)
  const revenueOrderCount = revenueSeries.reduce((sum, item) => sum + item.orderCount, 0)
  const platformItems = bootstrap.data?.platforms ?? []
  const activeConnections = platformItems.filter(item => ['ACTIVE', 'VERIFIED', 'CONNECTED'].includes(item.status.toUpperCase()))
  const syncConnection = activeConnections[0] ?? platformItems[0]
  const syncRows = bootstrap.data?.sync ?? []
  const latestSync = [...syncRows].sort((a, b) => new Date(b.lastSuccessAt ?? 0).getTime() - new Date(a.lastSuccessAt ?? 0).getTime())[0]
  const errors = [bootstrap.error, revenueQuery.error].filter(Boolean)
  return <section className="content dashboard"><div className="page-heading"><div><p className="eyebrow">Operasyon merkezi</p><h1>Genel Bakış</h1><p className="lede">Merhaba {me.displayName}. Günlük operasyonun önemli sinyalleri tek ekranda.</p></div><div className="dashboard-heading-actions"><span className="dashboard-date">{now.toLocaleDateString('tr-TR', { day: 'numeric', month: 'long', year: 'numeric' })}</span></div></div>
    {errors.length > 0 && <div role="alert" className="error">Bazı operasyon verileri alınamadı; görünen metrikler kısmi olabilir.</div>}
     <div className="metrics dashboard-metrics operational-metrics"><article><DashboardMetricIcon kind="pending" /><small>Bekleyen Sipariş</small><strong>{loading ? '—' : metrics?.pendingOrders ?? 0}</strong></article><article className={(metrics?.lateOrders ?? 0) ? 'danger-metric' : ''}><DashboardMetricIcon kind="late" /><small>Geciken Sipariş</small><strong>{loading ? '—' : metrics?.lateOrders ?? 0}</strong></article><article><DashboardMetricIcon kind="today" /><small>Bugünkü Sipariş</small><strong>{loading ? '—' : metrics?.todayOrders ?? 0}</strong></article><article><DashboardMetricIcon kind="month" /><small>Bu Ayki Sipariş</small><strong>{loading ? '—' : metrics?.monthOrders ?? 0}</strong></article><article><DashboardMetricIcon kind="return" /><small>Aksiyon Bekleyen İade</small><strong>{loading ? '—' : metrics?.pendingReturns ?? 0}</strong></article><article className={(metrics?.dueSoonInvoices ?? 0) ? 'warning-metric' : ''}><DashboardMetricIcon kind="invoice" /><small>Süresi Yaklaşan Fatura</small><strong>{loading ? '—' : metrics?.dueSoonInvoices ?? 0}</strong></article><article><DashboardMetricIcon kind="uninvoiced" /><small>Fatura bekliyor</small><strong>{loading ? '—' : metrics?.uninvoicedInvoices ?? 0}</strong></article><article><DashboardMetricIcon kind="stock" /><small>Düşük / Yok Stok</small><strong>{loading ? '—' : metrics?.lowStockProducts ?? 0}</strong></article></div>
    <div className="dashboard-report-grid"><article className="panel dashboard-revenue-panel"><div className="panel-title"><div><h2>Satış Cirosu</h2><p>Seçilen dönemde gerçekleşen sipariş toplamı</p></div><div className="dashboard-revenue-controls"><label className="dashboard-period-select"><span>Ciro dönemi</span><select aria-label="Ciro dönemi" value={revenueRange} onChange={event => setRevenueRange(event.target.value as DashboardRevenueRange)}><option value="1">Günlük</option><option value="3">Son 3 gün</option><option value="7">Son 7 gün</option><option value="14">Son 14 gün</option><option value="30">Son 30 gün</option><option value="month">Bu ay</option><option value="custom">Özel tarih</option></select></label><label><span>Platform</span><select aria-label="Ciro platformu" value={revenuePlatform} onChange={event => setRevenuePlatform(event.target.value)}><option value="ALL">Tüm platformlar</option>{revenuePlatformOptions.map(platform => <option value={platform} key={platform}>{platform}</option>)}</select></label></div></div>{revenueRange === 'custom' && <div className="dashboard-custom-range"><label><span>Başlangıç</span><input type="date" value={revenueFrom} max={revenueTo} onChange={event => setRevenueFrom(event.target.value)} /></label><label><span>Bitiş</span><input type="date" value={revenueTo} min={revenueFrom} onChange={event => setRevenueTo(event.target.value)} /></label></div>}<div className="dashboard-revenue-summary"><strong>{dashboardMoney(revenueTotal, revenueCurrency)}</strong><span>{revenueOrderCount} sipariş</span></div><div className="dashboard-revenue-chart" aria-label="Günlük satış cirosu"><div className="dashboard-revenue-axis" aria-hidden="true">{revenueAxisTicks.map(tick => <span key={tick.ratio} style={{ bottom: `${tick.ratio * 100}%` }}>{dashboardAxisMoney(tick.amount, revenueCurrency)}</span>)}</div><div className="dashboard-revenue-plot"><div className="dashboard-revenue-plot-inner"><div className="dashboard-revenue-gridlines" aria-hidden="true">{revenueAxisTicks.map(tick => <i className="is-major" key={tick.ratio} style={{ bottom: `${tick.ratio * 100}%` }} />)}</div><div className="dashboard-revenue-columns" style={{ gridTemplateColumns: `repeat(${Math.max(revenueSeries.length, 1)}, minmax(0, 1fr))` }}>{revenueSeries.map((point, index) => <div className="dashboard-revenue-column" key={point.key}><div className="dashboard-revenue-bar-wrap"><span className="dashboard-revenue-bar" style={{ height: `${Math.max(point.amount ? 7 : 3, point.amount / revenueAxisMax * 100)}%` }} aria-label={`${point.fullLabel}: ${dashboardMoney(point.amount, point.currency)}, ${point.orderCount} sipariş`} tabIndex={0}><span className="dashboard-revenue-hover"><strong>{point.fullLabel}</strong><span>{dashboardMoney(point.amount, point.currency)} · {point.orderCount} sipariş</span></span></span></div><span>{index === 0 || index === revenueSeries.length - 1 || index % revenueLabelEvery === 0 ? point.label : ''}</span></div>)}</div></div></div></div></article><article className="panel dashboard-api-panel"><div className="panel-title"><div><h2>Son senkronizasyonlar</h2><p>Sipariş, iade ve stok kayıtlarının güncel zamanı</p></div><Link className="dashboard-panel-link" to="/jobs">İşlem takibi <UiIcon name="arrowRight" /></Link></div><div className="dashboard-api-list dashboard-sync-list">{syncRows.map(row => <Link to="/jobs" key={row.resourceType}><span className={`dashboard-sync-icon ${row.kind}`} aria-hidden="true"><UiIcon name="sync" /></span><span><strong>{row.label}</strong><small>{row.status === 'SUCCEEDED' ? 'Başarılı senkronizasyon' : 'Henüz kayıt yok'}</small></span><b>{dashboardSyncTime(row)}</b></Link>)}</div><div className="dashboard-sync-meta"><UiIcon name="sync" /><span className="dashboard-sync-meta-copy"><strong>{latestSync ? `Son veri senkronizasyonu: ${dashboardSyncTime(latestSync)}` : 'Senkronizasyon kaydı yok'}</strong><small>Projection güncellemesi: {latestSync ? dashboardSyncTime(latestSync) : 'Kayıt yok'}</small></span></div></article></div>
    <div className="dashboard-bottom-grid"><article className="panel dashboard-flow-panel"><div className="panel-title"><div><h2>Sipariş akışı</h2><p>Operasyon kayıtlarının anlık özeti</p></div><Link className="dashboard-panel-link" to="/orders">Detaylar <UiIcon name="arrowRight" /></Link></div><div className="dashboard-flow-list"><Link to="/orders"><span><i className="flow-dot late" /><strong>Geciken sipariş</strong></span><b>{loading ? '—' : metrics?.lateOrders ?? 0}</b></Link><Link to="/orders"><span><i className="flow-dot invoice" /><strong>Fatura bekliyor</strong></span><b>{loading ? '—' : metrics?.uninvoicedInvoices ?? 0}</b></Link><Link to="/orders"><span><i className="flow-dot return" /><strong>Aksiyon bekleyen iade</strong></span><b>{loading ? '—' : metrics?.pendingReturns ?? 0}</b></Link></div></article><article className="panel dashboard-active-platform"><span className="dashboard-platform-icon" aria-hidden="true"><UiIcon name="platforms" size={28} /></span><small>Aktif Platform</small><strong>{loading ? '—' : activeConnections.length}</strong><p>{syncConnection?.name ?? 'Bağlantı bekleniyor'}</p><Link className="dashboard-panel-link" to="/integrations">Platformları yönet <UiIcon name="arrowRight" /></Link></article></div>
  </section>
}
type SecurityStatus = { totpState: string; recoveryCodesRemaining: number }
type SecuritySession = { id: string; state: string; current: boolean; issuedAt: string; lastSeenAt: string; expiresAt: string }
type MfaSetup = { otpauthUri: string; qrSvg: string; expiresAt: string }

function Security() {
  const client = useQueryClient()
  const [searchParams, setSearchParams] = useSearchParams()
  const requestedSettingsTab = searchParams.get('tab')
  const settingsTab: 'security' | 'database' | 'shipping' | 'appearance' = requestedSettingsTab === 'database' || requestedSettingsTab === 'shipping' || requestedSettingsTab === 'appearance' ? requestedSettingsTab : 'security'
  function setSettingsTab(tab: 'security' | 'database' | 'shipping' | 'appearance') {
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
  async function revokeSession(id: string) {
    if (!window.confirm('Bu oturumun bağlantısı sonlandırılsın mı?')) return
    setMessage('')
    try { await api(`/sessions/${id}/revoke`, { method: 'POST' }); await client.invalidateQueries({ queryKey: ['sessions'] }); setMessage('Oturum sonlandırıldı.') } catch { setMessage('Oturum sonlandırılamadı.') }
  }
  async function revokeOthers() {
    if (!window.confirm('Bu cihaz dışındaki tüm aktif oturumlar sonlandırılsın mı?')) return
    setMessage('')
    try { await api('/sessions/revoke-others', { method: 'POST' }); await client.invalidateQueries({ queryKey: ['sessions'] }); setMessage('Diğer aktif oturumlar sonlandırıldı.') } catch { setMessage('Oturumlar sonlandırılamadı.') }
  }
  async function deleteSession(id: string) {
    if (!window.confirm('Bu kapalı oturum kaydı silinsin mi?')) return
    setMessage('')
    try { await api(`/sessions/${id}`, { method: 'DELETE' }); await client.invalidateQueries({ queryKey: ['sessions'] }); setMessage('Kapalı oturum silindi.') } catch { setMessage('Kapalı oturum silinemedi.') }
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

  if (settingsTab === 'database') return <section className="content security-page"><div className="page-heading"><div><p className="eyebrow">Ayarlar</p><h1>Sistem ayarları</h1><p className="lede">Güvenlik ve yerel operasyon verilerini tek ekranda yönetin.</p></div></div><div className="settings-tabs" role="tablist"><button type="button" role="tab" aria-selected={false} onClick={() => setSettingsTab('security')}>Güvenlik ve oturumlar</button><button type="button" role="tab" aria-selected={true} className="active">Veritabanı temizliği</button><button type="button" role="tab" aria-selected={false} onClick={() => setSettingsTab('shipping')}>Kargo ayarları</button><button type="button" role="tab" aria-selected={false} onClick={() => setSettingsTab('appearance')}>Görünüm</button></div>{message && <div className="notice" role="status">{message}</div>}<div className="panel database-reset-panel"><div className="database-reset-intro"><span className="security-state">Yetkili İşlemi</span><h2>Yerel veritabanı listelerini sıfırla</h2><p>Yalnız seçtiğiniz alanlar bu hesabın yerel veritabanından silinir. Her başlığın kapsam ve bağlı kayıt ayrıntılarını görebilirsiniz.</p></div><div className="database-scope-groups"><section className="database-scope-group"><div><h3>Katalog</h3><p>Ürün kataloğunda kullanılan temel listeleri temizleyin.</p></div><div className="database-scope-list">{['PRODUCTS', 'CATEGORIES', 'CATEGORY_ATTRIBUTES', 'BRANDS'].map(resetScopeOption)}</div></section><section className="database-scope-group"><div><h3>Ürün seçenekleri</h3><p>Ürün seçeneklerini ve seçenek değerlerini temizleyin.</p></div><div className="database-scope-list">{['OPTIONS'].map(resetScopeOption)}</div></section><section className="database-scope-group"><div><h3>Operasyon</h3><p>İşlem ve satış kayıtlarını temizleyin.</p></div><div className="database-scope-list">{['ORDERS', 'RETURNS', 'INVOICES'].map(resetScopeOption)}</div></section></div><label className="database-confirmation">Onay için <b>Verileri sil</b> yazın<input value={resetConfirmation} onChange={event => setResetConfirmation(event.target.value)} /></label><button type="button" className="destructive" disabled={!resetScopes.length || resetConfirmation !== 'Verileri sil' || resetBusy} onClick={() => void resetOperationalData()}>{resetBusy ? 'Temizleniyor…' : 'Seçili listeleri kalıcı sil'}</button></div></section>

  async function deleteClosedSessions() {
    if (!window.confirm('Tüm kapalı oturum kayıtları silinsin mi?')) return
    setMessage('')
    try { await api('/sessions/closed', { method: 'DELETE' }); await client.invalidateQueries({ queryKey: ['sessions'] }); setMessage('Kapalı oturumlar silindi.') } catch { setMessage('Kapalı oturumlar silinemedi.') }
  }

  const legacySettingsTab = settingsTab as string; if (settingsTab === 'appearance') return <AppearanceSettingsPage />; if (settingsTab === 'shipping') return <section className="content security-page"><div className="page-heading"><div><p className="eyebrow">Ayarlar</p><h1>Sistem ayarları</h1><p className="lede">Etiket ölçüsü ve yazdırma düzenini yönetin.</p></div></div><div className="settings-tabs" role="tablist"><button type="button" role="tab" aria-selected={false} onClick={() => setSettingsTab('security')}>Güvenlik ve oturumlar</button><button type="button" role="tab" aria-selected={false} onClick={() => setSettingsTab('database')}>Veritabanı temizliği</button><button type="button" role="tab" aria-selected={true} className="active">Kargo ayarları</button><button type="button" role="tab" aria-selected={false} onClick={() => setSettingsTab('appearance')}>Görünüm</button></div>{message && <div className="shipping-settings-toast" role="status">{message}</div>}{shippingSettings.isError && !shippingSettings.settings && <div className="error" role="alert">Hesap kargo ayarları alınamadı; geçici yerel ayarlar gösteriliyor.</div>}<ShippingLabelSettingsPanel settings={labelSettings} onChange={shippingSettings.setSettings} onSave={saveLabelSettings} /></section>
  return <section className="content security-page"><div className="page-heading"><div><p className="eyebrow">Ayarlar</p><h1>Sistem ayarları</h1><p className="lede">Güvenlik ve yerel operasyon verilerini tek ekrandan yönetin.</p></div></div><div className="settings-tabs" role="tablist"><button type="button" role="tab" aria-selected={settingsTab === 'security'} className={settingsTab === 'security' ? 'active' : ''} onClick={() => setSettingsTab('security')}>Güvenlik ve oturumlar</button><button type="button" role="tab" aria-selected={legacySettingsTab === 'database'} className={legacySettingsTab === 'database' ? 'active' : ''} onClick={() => setSettingsTab('database')}>Veritabanı temizliği</button><button type="button" role="tab" aria-selected={legacySettingsTab === 'shipping'} className={legacySettingsTab === 'shipping' ? 'active' : ''} onClick={() => setSettingsTab('shipping')}>Kargo ayarları</button><button type="button" role="tab" aria-selected={legacySettingsTab === 'appearance'} className={legacySettingsTab === 'appearance' ? 'active' : ''} onClick={() => setSettingsTab('appearance')}>Görünüm</button></div>{message && <div className="notice" role="status">{message}</div>}{settingsTab === 'security' && <>{status.isLoading ? <Status title="Güvenlik durumu yükleniyor" /> : status.isError || !status.data ? <div role="alert" className="error">Güvenlik durumu alınamadı.</div> : <div className="panel security-authenticator-card"><div><span className={`security-state ${status.data.totpState === 'ENABLED' ? 'enabled' : ''}`}>{status.data.totpState === 'ENABLED' ? 'Etkin' : 'Kapalı'}</span><h2>Authenticator</h2><p>Giriş sırasında telefonunuzdaki tek kullanımlık kodla hesabınızı koruyun.</p><small>Kalan kurtarma kodu: <strong>{status.data.recoveryCodesRemaining}</strong></small></div>{status.data.totpState === 'ENABLED' ? <span className="security-check" aria-label="Authenticator etkin"><UiIcon name="check" /></span> : <button type="button" onClick={() => { setMessage(''); setMfaStep('password') }}>Authenticator’ı etkinleştir</button>}</div>}
     <div className="panel security-sessions-card"><div className="panel-title"><div><h2>Oturumlar</h2><p>Hesabınıza bağlı cihazları ve son etkinliklerini görüntüleyin.</p></div><div className="session-bulk-actions">{activeOtherSessions.length > 0 && <button type="button" className="secondary danger-outline" onClick={() => void revokeOthers()}>Diğer tüm oturumları kapat</button>}{closedSessions.length > 0 && <button type="button" className="secondary danger-outline" onClick={() => void deleteClosedSessions()}>Kapalı oturumları sil</button>}</div></div>{sessions.isLoading ? <p>Yükleniyor…</p> : sessions.isError || !sessions.data ? <div role="alert" className="error">Oturumlar alınamadı.</div> : <ul className="sessions">{sessions.data.map(session => <li key={session.id} className={session.current ? 'current' : ''}><span className="session-device-icon" aria-hidden="true"><UiIcon name={session.current ? 'check' : 'grid'} /></span><span><strong>{session.current ? 'Bu cihaz' : 'Diğer oturum'}</strong><small>{session.state === 'ACTIVE' ? 'Aktif' : 'Sonlandırıldı'} · Son etkinlik {new Date(session.lastSeenAt).toLocaleString('tr-TR')}</small><small>Bitiş {new Date(session.expiresAt).toLocaleString('tr-TR')}</small></span>{session.current ? <b>Mevcut oturum</b> : session.state === 'ACTIVE' ? <button type="button" className="secondary danger-outline" onClick={() => void revokeSession(session.id)}>Oturumu sonlandır</button> : <button type="button" className="secondary danger-outline" onClick={() => void deleteSession(session.id)}>Kaydı sil</button>}</li>)}</ul>}</div>
    {mfaStep !== 'closed' && <div className="workspace-modal-backdrop" role="presentation"><section className="workspace-modal security-modal" role="dialog" aria-modal="true" aria-labelledby="mfa-title"><header><div><h2 id="mfa-title">Authenticator kurulumu</h2><p>{mfaStep === 'password' ? 'Önce hesabın size ait olduğunu doğrulayın.' : mfaStep === 'verify' ? 'QR kodu uygulamanıza ekleyip üretilen kodu girin.' : 'Kurtarma kodlarını şimdi güvenli bir yerde saklayın.'}</p></div><button className="modal-close" type="button" aria-label="Kapat" onClick={() => setMfaStep('closed')}><UiIcon name="close" /></button></header>{mfaStep === 'password' && <form className="security-modal-body" onSubmit={prepareMfa}><label>Mevcut parola<input name="password" type="password" autoComplete="current-password" required /></label><button disabled={busy}>{busy ? 'Doğrulanıyor…' : 'Devam et'}</button></form>}{mfaStep === 'verify' && setup && <form className="security-modal-body mfa-verify" onSubmit={confirmMfa}><img src={`data:image/svg+xml;utf8,${encodeURIComponent(setup.qrSvg)}`} alt="Authenticator QR kodu" /><div><p>QR kodu Google Authenticator, Microsoft Authenticator veya uyumlu uygulamanızla tarayın.</p><details><summary>Kurulum anahtarını elle göster</summary><code>{setup.otpauthUri}</code></details><label>6 haneli doğrulama kodu<input name="code" inputMode="numeric" pattern="[0-9]{6}" autoComplete="one-time-code" required /></label><button disabled={busy}>{busy ? 'Kontrol ediliyor…' : 'Etkinleştir'}</button></div></form>}{mfaStep === 'recovery' && <div className="security-modal-body"><div className="recovery-code-grid">{recoveryCodes.map(code => <code key={code}>{code}</code>)}</div><p>Bu kodlar yalnızca bir kez gösterilir. Her kod tek kullanımlıktır.</p><button type="button" onClick={() => setMfaStep('closed')}>Kodları sakladım</button></div>}{message && <div className="error security-modal-error" role="alert">{message}</div>}</section></div>}</>}
    {legacySettingsTab === 'database' && <div className="panel database-reset-panel"><div className="database-reset-intro"><span className="security-state">Yetkili İşlemi</span><h2>Yerel veritabanı listelerini sıfırla</h2><p>Seçilen kayıtlar yalnız bu hesabın yerel veritabanından silinir. Bağlı alt kayıtlar güvenli sırayla temizlenir.</p></div><div className="database-scope-groups"><section className="database-scope-group"><div><h3>Katalog</h3><p>Ürün kataloğunda kullanılan temel listeleri temizleyin.</p></div><div className="database-scope-list">{[['PRODUCTS','Ürünler listesi'],['CATEGORIES','Kategori listesi'],['BRANDS','Marka listesi']].map(([scope,label]) => <label key={scope}><input type="checkbox" checked={resetScopes.includes(scope)} onChange={event => toggleResetScope(scope, event.target.checked)} /><span><strong>{label}</strong><small>Yerel kayıtları ve bağlı alt kayıtları temizle</small></span></label>)}</div></section><section className="database-scope-group"><div><h3>Ürün seçenekleri</h3><p>Ürün seçeneklerini ve seçenek değerlerini temizleyin.</p></div><div className="database-scope-list">{[['OPTIONS','Seçenekler listesi']].map(([scope,label]) => <label key={scope}><input type="checkbox" checked={resetScopes.includes(scope)} onChange={event => toggleResetScope(scope, event.target.checked)} /><span><strong>{label}</strong><small>Ürün seçeneklerini ve bağlı değerleri temizle</small></span></label>)}</div></section><section className="database-scope-group"><div><h3>Operasyon</h3><p>İşlem ve satış kayıtlarını temizleyin.</p></div><div className="database-scope-list">{[['ORDERS','Siparişler listesi'],['RETURNS','İadeler listesi'],['INVOICES','Faturalar listesi']].map(([scope,label]) => <label key={scope}><input type="checkbox" checked={resetScopes.includes(scope)} onChange={event => toggleResetScope(scope, event.target.checked)} /><span><strong>{label}</strong><small>Yerel kayıtları ve bağlı alt kayıtları temizle</small></span></label>)}</div></section></div><label className="database-confirmation">Onay için <b>Verileri sil</b> yazın<input value={resetConfirmation} onChange={event => setResetConfirmation(event.target.value)} /></label><button type="button" className="destructive" disabled={!resetScopes.length || resetConfirmation !== 'Verileri sil' || resetBusy} onClick={() => void resetOperationalData()}>{resetBusy ? 'Temizleniyor…' : 'Seçili listeleri kalıcı sil'}</button></div>}
  </section>
}

function AppearanceSettingsPage() {
  const appearance = useAppearanceSettings()
  const [message, setMessage] = useState('')
  const [busy, setBusy] = useState(false)
  const [colorTheme, setColorTheme] = useState<AppearanceColorTheme>('dark')
  const savedAppearance = useRef(appearance.settings)

  useEffect(() => {
    savedAppearance.current = appearance.settings
  }, [appearance.settings])

  useEffect(() => {
    const root = document.documentElement
    const media = window.matchMedia('(prefers-color-scheme: dark)')
    const applyPreview = () => {
      const resolvedTheme = appearance.draft.themeMode === 'system' ? (media.matches ? 'dark' : 'light') : appearance.draft.themeMode
      root.dataset.theme = resolvedTheme
      root.dataset.themeMode = appearance.draft.themeMode
      root.style.setProperty('--rv-font-ui', appearanceFontFamilyCss[appearance.draft.fontFamily])
      root.style.setProperty('--rv-font-scale', String(appearanceFontScale[appearance.draft.fontSize]))
      appearanceColorTokenOptions.forEach(({ key }) => root.style.setProperty(appearanceColorCssVariable(key), appearance.draft.colors[resolvedTheme][key]))
    }
    applyPreview()
    media.addEventListener('change', applyPreview)
    return () => media.removeEventListener('change', applyPreview)
  }, [appearance.draft])

  useEffect(() => () => {
    const root = document.documentElement
    const saved = savedAppearance.current
    const media = window.matchMedia('(prefers-color-scheme: dark)')
    const resolvedTheme = saved.themeMode === 'system' ? (media.matches ? 'dark' : 'light') : saved.themeMode
    root.dataset.theme = resolvedTheme
    root.dataset.themeMode = saved.themeMode
    root.style.setProperty('--rv-font-ui', appearanceFontFamilyCss[saved.fontFamily])
    root.style.setProperty('--rv-font-scale', String(appearanceFontScale[saved.fontSize]))
    appearanceColorTokenOptions.forEach(({ key }) => root.style.setProperty(appearanceColorCssVariable(key), saved.colors[resolvedTheme][key]))
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

  const previewStyle: CSSProperties = {
    fontFamily: appearanceFontFamilyCss[appearance.draft.fontFamily],
    fontSize: 'var(--rv-font-size-md)',
    ...Object.fromEntries(appearanceColorTokenOptions.map(({ key }) => [appearanceColorCssVariable(key), appearance.draft.colors[colorTheme][key]]))
  }

  const palette = appearance.draft.colors[colorTheme]
  function updateColor(key: typeof appearanceColorTokenOptions[number]['key'], value: string) {
    appearance.setDraft({ ...appearance.draft, colors: { ...appearance.draft.colors, [colorTheme]: { ...appearance.draft.colors[colorTheme], [key]: value } } })
  }

  const navigate = useNavigate()

  return <section className="content security-page">
    <div className="page-heading"><div><p className="eyebrow">Ayarlar</p><h1>Sistem ayarları</h1><p className="lede">Güvenlik ve yerel operasyon verilerini tek ekranda yönetin.</p></div></div>
<div className="rv-tabs" role="tablist">
      <button type="button" role="tab" aria-selected={false} onClick={() => navigate('/settings')}>Güvenlik ve oturumlar</button>
      <button type="button" role="tab" aria-selected={false} onClick={() => navigate('/settings?tab=database')}>Veritabanı temizliği</button>
      <button type="button" role="tab" aria-selected={false} onClick={() => navigate('/settings?tab=shipping')}>Kargo ayarları</button>
      <button type="button" role="tab" aria-selected={true} className="active">Görünüm</button>
    </div>
    {message && <div className="notice" role="status">{message}</div>}
    {appearance.isError && <div className="error" role="alert">Hesap görünüm ayarları alınamadı; varsayılan görünüm gösteriliyor.</div>}
    <section className="panel appearance-settings-panel">
      <div className="panel-title"><div><h2>Okunabilirlik</h2><p>Tablo ve işlem ekranlarındaki yazıları hesabınız için özelleştirin.</p></div></div>
      <div className="appearance-settings-grid">
        <label><span>Font tipi</span><select value={appearance.draft.fontFamily} onChange={event => appearance.setDraft({ ...appearance.draft, fontFamily: event.target.value as AppearanceSettings['fontFamily'] })}>{appearanceFontFamilyOptions.map(option => <option key={option.value} value={option.value}>{option.label}</option>)}</select></label>
        <label><span>Yazı boyutu</span><select value={appearance.draft.fontSize} onChange={event => appearance.setDraft({ ...appearance.draft, fontSize: event.target.value as AppearanceSettings['fontSize'] })}>{appearanceFontSizeOptions.map(option => <option key={option.value} value={option.value}>{option.label} — {option.description}</option>)}</select></label>
        <label><span>Tema</span><select value={appearance.draft.themeMode} onChange={event => appearance.setDraft({ ...appearance.draft, themeMode: event.target.value as AppearanceSettings['themeMode'] })}>{appearanceThemeModeOptions.map(option => <option key={option.value} value={option.value}>{option.label} — {option.description}</option>)}</select></label>
      </div>
      <section className="appearance-color-editor">
        <div className="appearance-color-editor-heading"><div><h2>Renk paleti</h2><p>Her renk tokenını ayrı ayrı düzenleyin. Değişiklikler seçilen tema için kaydedilir.</p></div><div className="appearance-color-actions"><div className="rv-tabs appearance-color-theme-tabs" role="tablist" aria-label="Renk teması"><button type="button" role="tab" aria-selected={colorTheme === 'light'} className={colorTheme === 'light' ? 'is-active' : ''} onClick={() => setColorTheme('light')}>Açık tema</button><button type="button" role="tab" aria-selected={colorTheme === 'dark'} className={colorTheme === 'dark' ? 'is-active' : ''} onClick={() => setColorTheme('dark')}>Koyu tema</button></div><button type="button" className="rv-button rv-button-secondary rv-button-sm" onClick={() => appearance.setDraft({ ...appearance.draft, colors: { ...appearance.draft.colors, [colorTheme]: { ...defaultAppearanceSettings.colors[colorTheme] } } })}>Varsayılanlara dön</button></div></div>
        <div className="appearance-color-grid">{appearanceColorTokenOptions.map(({ key, label, description }) => <label className="appearance-color-field" key={key}><span><b>{label}</b><small>{description}</small></span><span className="appearance-color-control"><input type="color" value={palette[key]} onChange={event => updateColor(key, event.target.value)} aria-label={`${label} rengi`} /><code>{palette[key].toUpperCase()}</code></span></label>)}</div>
      </section>
      <div className="appearance-preview" style={previewStyle}><small>Önizleme</small><strong>Ravencia MarketplaceHub</strong><p>Bu ayar sipariş, iade, ürün ve diğer çalışma ekranlarındaki metinleri etkiler.</p></div>
      <button type="button" onClick={() => void save()} disabled={busy}>{busy ? 'Kaydediliyor…' : 'Görünüm ayarlarını kaydet'}</button>
    </section>
  </section>
}

type ShippingDesignerTab = 'general' | 'text' | 'barcode'
type ShippingLabelTemplate = { id: string; name: string; savedAt: string; settings: ShippingLabelSettings }
const shippingLabelTemplateStorageKey = 'ravencia.shippingLabelTemplates'

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
  function endPointerDrag() { pointerDrag.current = null }
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
  function commitFontSize(value: string) {
    if (!activeBlock) return
    const parsed = Number(value)
    const next = Number.isFinite(parsed) && value.trim() ? Math.min(72, Math.max(8, parsed)) : activeBlock.fontSize ?? 14
    setFontSizeDraft(String(next))
    updateBlock(activeBlock.id, { fontSize: next })
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
  function numberPositionField(label: string, key: 'x' | 'y' | 'width' | 'height', value: number) {
    if (!activeBlock) return null
    const position = positionFor(activeBlock, layout.indexOf(activeBlock))
    return <label className="shipping-designer-number-field">{label}<input type="number" min={0} max={100} value={value} onChange={event => { const next = Math.min(100, Math.max(0, Number(event.target.value) || 0)); const nextPosition = { ...position, [key]: next }; if (key === 'x') nextPosition.x = Math.min(next, 100 - nextPosition.width); if (key === 'y') nextPosition.y = Math.min(next, 100 - nextPosition.height); if (key === 'width') nextPosition.width = Math.min(next, 100 - nextPosition.x); if (key === 'height') nextPosition.height = Math.min(next, 100 - nextPosition.y); updateBlock(activeBlock.id, { position: nextPosition }) }} /></label>
  }
  return <div className="panel shipping-settings-panel shipping-designer-panel">
    <div className="shipping-settings-intro"><span className="security-state enabled">Etiket Tasarımcısı</span><h2>Kargo etiketi düzeni</h2><p>Alanları sağdaki kütüphaneden kâğıda sürükleyin. Seçili alanı taşıyın, boyutlandırın ve özelliklerini anında düzenleyin. Bu ayarlar hesabınıza kaydedilir ve tüm tarayıcılarda aynı uygulanır.</p></div>
    <div className="shipping-settings-grid shipping-designer-page-settings">
      <label>Gönderici adı<input value={settings.senderName} maxLength={120} onChange={event => update('senderName', event.target.value)} /></label>
      <label>Gönderici adresi<textarea value={settings.senderAddress} maxLength={500} rows={3} onChange={event => update('senderAddress', event.target.value)} placeholder="İsteğe bağlı" /></label>
      <label>A4 sayfa düzeni<select value={settings.a4LabelsPerPage} onChange={event => update('a4LabelsPerPage', Number(event.target.value) as ShippingLabelSettings['a4LabelsPerPage'])}><option value={1}>Sayfada 1 etiket</option><option value={2}>Sayfada 2 etiket</option><option value={4}>Sayfada 4 etiket</option></select></label>
      <label>Varsayılan çıktı<select value={settings.defaultFormat} onChange={event => update('defaultFormat', event.target.value as ShippingLabelSettings['defaultFormat'])}><option value="a4">Kargo etiketi · A4</option><option value="sticker">Kargo etiketi · Sticker</option></select></label>
      <div className="shipping-settings-check"><input type="checkbox" checked={settings.showA4Button} onChange={event => update('showA4Button', event.target.checked)} /><span>A4 yazdırma butonunu göster</span></div>
      <div className="shipping-settings-check"><input type="checkbox" checked={settings.showStickerButton} onChange={event => update('showStickerButton', event.target.checked)} /><span>Sticker yazdırma butonunu göster</span></div>
      <label>Sticker genişliği (mm)<input type="number" min={40} max={300} value={stickerWidthDraft} onChange={event => setStickerWidthDraft(event.target.value)} onBlur={() => commitDimension('stickerWidthMm', stickerWidthDraft, settings.stickerWidthMm)} /></label>
      <label>Sticker yüksekliği (mm)<input type="number" min={40} max={300} value={stickerHeightDraft} onChange={event => setStickerHeightDraft(event.target.value)} onBlur={() => commitDimension('stickerHeightMm', stickerHeightDraft, settings.stickerHeightMm)} /></label>
      <label>Bloklar arası boşluk (mm)<input type="number" min={0} max={20} value={settings.sectionGapMm} onChange={event => update('sectionGapMm', Math.min(20, Math.max(0, Number(event.target.value) || 0)))} /></label>
      <div className="shipping-settings-check"><input type="checkbox" checked={settings.showCustomerPhone} onChange={event => update('showCustomerPhone', event.target.checked)} /><span>Müşteri iletişim alanını göster</span></div>
    </div>
    <section className="shipping-layout-editor shipping-designer" aria-labelledby="shipping-designer-title">
      <header className="shipping-designer-toolbar"><div><span className="shipping-designer-kicker">Kâğıt çalışma alanı</span><h3 id="shipping-designer-title">Etiket tasarımını oluştur</h3></div><div className="shipping-designer-format-tabs" role="tablist"><button type="button" className={format === 'a4' ? 'is-active' : ''} role="tab" aria-selected={format === 'a4'} onClick={() => setFormat('a4')}>A4 <small>{settings.a4LabelsPerPage} etiket</small></button><button type="button" className={format === 'sticker' ? 'is-active' : ''} role="tab" aria-selected={format === 'sticker'} onClick={() => setFormat('sticker')}>Sticker <small>{settings.stickerWidthMm} × {settings.stickerHeightMm} mm</small></button></div><div className="shipping-designer-template-actions"><select aria-label="Kayıtlı şablon seçin" value={selectedTemplateId} onChange={event => applyTemplate(event.target.value)}><option value="">Şablon seçin…</option>{templates.map(template => <option key={template.id} value={template.id}>{template.name}</option>)}</select><button type="button" className="secondary" onClick={() => { setTemplateNameDraft(`${format === 'a4' ? 'A4' : 'Sticker'} şablonu`); setTemplateMessage(''); setTemplateNameOpen(true) }}>Şablon kaydet</button><button type="button" className="secondary" disabled={!selectedTemplateId} onClick={deleteTemplate}>Şablonu sil</button></div><button type="button" className="secondary" onClick={resetLayout}>Varsayılanı yükle</button></header>
      {templateNameOpen && <div className="shipping-template-save-dialog" role="dialog" aria-label="Şablon kaydet"><label>Şablon adı<input autoFocus value={templateNameDraft} maxLength={80} onChange={event => setTemplateNameDraft(event.target.value)} onKeyDown={event => { if (event.key === 'Enter') saveTemplate(templateNameDraft); if (event.key === 'Escape') setTemplateNameOpen(false) }} /></label><div><button type="button" className="secondary" onClick={() => setTemplateNameOpen(false)}>Vazgeç</button><button type="button" onClick={() => saveTemplate(templateNameDraft)}>Kaydet</button></div></div>}
      {templateMessage && <p className="shipping-template-status" role="status">{templateMessage}</p>}
      <div className="shipping-designer-workspace">
        <aside className="shipping-designer-palette"><div className="shipping-designer-panel-heading"><span>İçerik kütüphanesi</span><small>Sürükleyip kâğıda bırakın</small></div><div className="shipping-designer-palette-list">{shippingLabelBlockCatalog.map(item => <button type="button" key={item.kind} draggable onDragStart={event => dragStart(event, `catalog:${item.kind}`)} onClick={() => addBlock(item.kind)} disabled={item.kind !== 'custom' && layout.some(block => block.kind === item.kind)}><span className={`shipping-designer-palette-icon ${item.kind}`}><UiIcon name={item.kind === 'trackingBarcode' || item.kind === 'packageBarcode' ? 'barcode' : item.kind === 'custom' ? 'sparkle' : 'layout'} /></span><span><strong>{item.label}</strong><small>{item.description}</small></span><UiIcon name="plus" /></button>)}</div><div className="shipping-designer-custom-fields"><label>Özel blok başlığı<input value={customTitle} onChange={event => setCustomTitle(event.target.value)} placeholder="Örn. mağaza notu" /></label><label>Özel blok içeriği<textarea value={customText} onChange={event => setCustomText(event.target.value)} rows={3} placeholder="Bu blokta görünecek metin" /></label></div></aside>
        <main className="shipping-designer-stage"><div className="shipping-designer-stage-bar"><span>{format === 'a4' ? 'A4 çalışma yüzeyi' : 'Sticker çalışma yüzeyi'}</span><small>Izgara görünümü · %{layout.length ? layout.length : 0} blok yerleşti</small></div><div className={`shipping-designer-paper is-${format}`} onDragOver={event => event.preventDefault()} onDrop={dropOnCanvas} style={{ aspectRatio: format === 'a4' ? '210 / 297' : `${settings.stickerWidthMm} / ${settings.stickerHeightMm}` }} role="application" aria-label={`${format === 'a4' ? 'A4' : 'Sticker'} etiket tasarım alanı`}>{layout.map((block, index) => { const position = positionFor(block, index); return <article key={block.id} className={`shipping-designer-block${activeBlock?.id === block.id ? ' is-selected' : ''}`} draggable onDragStart={event => dragStart(event, `block:${block.id}`)} onPointerDown={event => beginPointerDrag(event, block)} onPointerMove={movePointerDrag} onPointerUp={endPointerDrag} onPointerCancel={endPointerDrag} onClick={() => setSelectedId(block.id)} style={{ left: `${position.x}%`, top: `${position.y}%`, width: `${position.width}%`, height: `${position.height}%`, textAlign: block.align, fontSize: `${block.fontSize ?? 14}px` }}><span className="shipping-designer-block-handle"><UiIcon name="moreVertical" /></span><span className="shipping-designer-block-index">{index + 1}</span><div className="shipping-designer-block-preview"><ShippingPreviewBlock block={block} /></div></article>})}{layout.length === 0 && <div className="shipping-designer-drop-empty"><strong>İçerik bırakın</strong><span>Sağ panelden bir alanı buraya sürükleyin.</span></div>}</div><div className="shipping-designer-stage-help">Tutamak noktasından sürükleyerek taşıyın · Bir alana tıklayarak sağ özelliklerini açın</div></main>
        <aside className="shipping-designer-inspector"><div className="shipping-designer-panel-heading"><span>Özellikler</span><small>{activeBlock ? `Seçili: ${activeBlock.title}` : 'Bir alan seçin'}</small></div><nav className="shipping-designer-inspector-tabs" role="tablist"><button type="button" className={designerTab === 'general' ? 'is-active' : ''} onClick={() => setDesignerTab('general')}>Genel</button><button type="button" className={designerTab === 'text' ? 'is-active' : ''} onClick={() => setDesignerTab('text')}>Yazı</button><button type="button" className={designerTab === 'barcode' ? 'is-active' : ''} onClick={() => setDesignerTab('barcode')}>Barkod</button></nav>{!activeBlock ? <div className="shipping-designer-empty">Düzenlemek için kâğıt üzerindeki bir bloğa tıklayın.</div> : <div className="shipping-designer-inspector-body">{designerTab === 'general' && <><label>Blok türü<select value={activeBlock.kind} onChange={event => { const kind = event.target.value as ShippingLabelBlockKind; const catalog = shippingLabelBlockCatalog.find(item => item.kind === kind); if (catalog) updateBlock(activeBlock.id, { kind, title: kind === 'custom' ? activeBlock.title : catalog.label, fields: [...catalog.fields], text: kind === 'custom' ? activeBlock.text : '' }) }}>{shippingLabelBlockCatalog.map(item => <option key={item.kind} value={item.kind}>{item.label}</option>)}</select></label><label>Hizalama<select value={activeBlock.align} onChange={event => updateBlock(activeBlock.id, { align: event.target.value as ShippingLabelAlignment })}><option value="left">Sol</option><option value="center">Orta</option><option value="right">Sağ</option></select></label><div className="shipping-designer-position-grid">{numberPositionField('Sol %', 'x', positionFor(activeBlock, layout.indexOf(activeBlock)).x)}{numberPositionField('Üst %', 'y', positionFor(activeBlock, layout.indexOf(activeBlock)).y)}{numberPositionField('Genişlik %', 'width', positionFor(activeBlock, layout.indexOf(activeBlock)).width)}{numberPositionField('Yükseklik %', 'height', positionFor(activeBlock, layout.indexOf(activeBlock)).height)}</div><label>Alana ekle<select value="" onChange={event => { if (event.target.value) toggleField(activeBlock, event.target.value as ShippingLabelField) }}><option value="">Bir alan seçin…</option>{shippingLabelFields.filter(item => !activeBlock.fields.includes(item.id)).map(item => <option value={item.id} key={item.id}>{item.label}</option>)}</select></label><div className="shipping-designer-field-chips">{activeBlock.fields.length ? activeBlock.fields.map(field => <button type="button" key={field} onClick={() => toggleField(activeBlock, field)}>{shippingLabelFields.find(item => item.id === field)?.label}<UiIcon name="close" /></button>) : <span>Alan eklenmedi</span>}</div></>}{designerTab === 'text' && <><label>Yazı hizası<select value={activeBlock.align} onChange={event => updateBlock(activeBlock.id, { align: event.target.value as ShippingLabelAlignment })}><option value="left">Sol</option><option value="center">Orta</option><option value="right">Sağ</option></select></label><label>Yazı boyutu (px)<input type="number" min={8} max={72} value={fontSizeDraft} onChange={event => setFontSizeDraft(event.target.value)} onBlur={() => commitFontSize(fontSizeDraft)} onKeyDown={event => { if (event.key === 'Enter') event.currentTarget.blur() }} /></label><p className="shipping-designer-help">Yazı rengi sabit olarak siyahtır.</p>{activeBlock.kind === 'custom' && <label>Metin<textarea value={activeBlock.text} maxLength={500} rows={8} onChange={event => updateBlock(activeBlock.id, { text: event.target.value })} /></label>}<p className="shipping-designer-help">Alanların gerçek değerleri sipariş yazdırılırken otomatik doldurulur.</p></>}{designerTab === 'barcode' && <div className="shipping-designer-barcode-settings"><strong>{activeBlock.kind === 'packageBarcode' ? 'Paket barkodu' : activeBlock.kind === 'trackingBarcode' ? 'Takip barkodu' : 'Barkod ayarı'}</strong><p>Barkod genişliği tuvaldeki blok genişliğine göre otomatik ölçeklenir. Modül aralıkları tarayıcı okunabilirliğini korur.</p><label>Alan<select value={activeBlock.fields[0] ?? ''} onChange={event => { if (event.target.value) updateBlock(activeBlock.id, { fields: [event.target.value as ShippingLabelField] }) }}><option value="">Alan seçin…</option>{shippingLabelFields.filter(field => field.id === 'trackingNumber' || field.id === 'packageNumber').map(field => <option value={field.id} key={field.id}>{field.label}</option>)}</select></label></div>}<button type="button" className="shipping-designer-delete" onClick={() => removeBlock(activeBlock.id)}>Bloğu kaldır</button></div>}</aside>
      </div>
    </section>
    <div className="shipping-settings-actions"><button type="button" onClick={onSave}>Ayarları kaydet</button></div>
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
    <div className="rv-splash-screen" role="status">
       <div className="rv-splash-logo-container">
          <img src="/pack/brand/ravencia-symbol-transparent.png" alt="" className="rv-splash-symbol" />
       </div>
       <div className="rv-splash-wordmark-container">
          <img src="/pack/brand/ravencia-wordmark-transparent.png" alt="Ravencia" className="rv-splash-wordmark" />
       </div>
       <div className="rv-splash-loading-bar">
          <div className="rv-splash-loading-progress"></div>
       </div>
       <strong className="rv-splash-text">{title}</strong>
       {detail && <p className="rv-splash-detail">{detail}</p>}
    </div>
  )
}
