import { useEffect, useState, type FormEvent, type ReactNode } from 'react'
import { Link, Navigate, NavLink, Route, Routes, useNavigate } from 'react-router'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import { api, ApiRequestError, hubApi, loadAllPages, type Me } from './api'
import { AttributesPage, BrandsPage, CategoriesPage, ImportDetailPage, ImportsPage, InventoryPage, NewProductPage, ProductDetailPage, ProductsPage } from './CatalogPages'
import { IntegrationDetailPage, IntegrationsPage, MappingPage, OrdersPage, ReturnDetailPage, ReturnsPage, ShipmentDetailPage, ShipmentsPage } from './MarketplacePages'
import { InvoicesPage } from './InvoicingPages'
import { JobsPage } from './OperationsPages'
import { loadShippingLabelSettings, saveShippingLabelSettings, type ShippingLabelSettings } from './shipping-label'

type ShellConnection = {
  id: string
  displayName: string
  platformCode: string
  environment: string
  status: string
  lastSuccessAt: string | null
}

function Shell({ me }: { me: Me }) {
  const [sidebarHoverExpanded, setSidebarHoverExpanded] = useState(false)
  const [sidebarPinned, setSidebarPinned] = useState(() => localStorage.getItem('ravencia.sidebarPinned') === 'true')
  async function logout() { await api('/logout', { method: 'POST' }); window.location.replace(`/?signedOut=${Date.now()}`) }
  const sidebarExpanded = sidebarPinned || sidebarHoverExpanded
  const menuCollapsed = !sidebarExpanded
  function expandSidebarOnHover() {
    setSidebarHoverExpanded(true)
  }
  function collapseSidebarOnLeave() {
    if (!sidebarPinned) setSidebarHoverExpanded(false)
  }
  function toggleSidebarPinned() {
    const nextPinned = !sidebarPinned
    setSidebarPinned(nextPinned)
    localStorage.setItem('ravencia.sidebarPinned', String(nextPinned))
    if (nextPinned) setSidebarHoverExpanded(true)
  }
  const icons: Record<string, ReactNode> = {
    dashboard: <><rect x="3" y="3" width="7" height="7" rx="1.5"/><rect x="14" y="3" width="7" height="7" rx="1.5"/><rect x="3" y="14" width="7" height="7" rx="1.5"/><rect x="14" y="14" width="7" height="7" rx="1.5"/></>,
    products: <><path d="M6 8h12l1 13H5L6 8Z"/><path d="M9 8V6a3 3 0 0 1 6 0v2"/></>,
    orders: <><rect x="5" y="3" width="14" height="18" rx="2"/><path d="M9 3.5h6v3H9z"/><path d="M9 11h6M9 15h6M9 18h3"/></>,
    returns: <><path d="M9 10H4V5"/><path d="M4 10a8 8 0 1 1 2.3 5.7"/><path d="m15 14 3 3-3 3"/><path d="M18 17h-5"/></>,
    invoices: <><path d="M6 3h9l3 3v15H6z"/><path d="M15 3v4h4M9 11h6M9 15h6M9 18h4"/></>,
    jobs: <><path d="M4 5h16M4 12h10M4 19h7"/><path d="m17 14 2 2 3-4"/></>,
    settings: <><path d="M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z"/><circle cx="12" cy="12" r="3"/></>,
    platforms: <><rect x="3" y="4" width="7" height="7" rx="1.5"/><rect x="14" y="13" width="7" height="7" rx="1.5"/><path d="M10 7.5h3a4 4 0 0 1 4 4V13"/><path d="m14 10 3 3 3-3"/></>,
    mappings: <><circle cx="6" cy="6" r="3"/><circle cx="18" cy="18" r="3"/><path d="M8.5 8.5 15.5 15.5"/><path d="M18 9V6h-3"/></>,
    security: <><rect x="3" y="11" width="18" height="11" rx="2" ry="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/></>,
    logout: <><path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4"/><polyline points="16 17 21 12 16 7"/><line x1="21" y1="12" x2="9" y2="12"/></>

  }
  const icon = (name: string) => <svg className="nav-icon" aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">{icons[name]}</svg>
  const item = (to: string, iconName: string, label: string) => <NavLink to={to}>{icon(iconName)}<span className="nav-label">{label}</span></NavLink>
  return <div className={`app-shell stitch-shell ${menuCollapsed ? 'sidebar-collapsed' : ''} ${sidebarExpanded ? 'sidebar-hover-expanded' : ''} ${sidebarPinned ? 'sidebar-pinned' : ''}`}>
    <aside onMouseEnter={expandSidebarOnHover} onMouseLeave={collapseSidebarOnLeave} onFocus={expandSidebarOnHover} onBlur={event => { if (!event.currentTarget.contains(event.relatedTarget as Node | null)) collapseSidebarOnLeave() }}>
      <div className="sidebar-brand-row"><div className="stitch-brand-mark" aria-hidden="true">R</div><div className="brand wordmark"><strong>Ravencia</strong><small>MarketplaceHub</small></div><button type="button" className={`sidebar-pin-toggle ${sidebarPinned ? 'is-pinned' : ''}`} aria-label={sidebarPinned ? 'Menü sabitlemesini kaldır' : 'Menüyü sabitle'} aria-pressed={sidebarPinned} title={sidebarPinned ? 'Menü sabitlendi' : 'Menüyü sabitle'} onClick={toggleSidebarPinned}><svg viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><path d="M8 3h8M9 3v5l-3 3v2h12v-2l-3-3V3M12 13v8" /></svg></button></div>
      <nav aria-label="Ana menü">{item('/dashboard', 'dashboard', 'Dashboard')}{item('/products', 'products', 'Ürünler')}{item('/orders', 'orders', 'Siparişler')}{item('/returns', 'returns', 'İadeler')}{item('/invoices', 'invoices', 'Faturalar')}{item('/jobs', 'jobs', 'İşlem Takibi')}{item('/integrations', 'platforms', 'Platformlar')}{item('/mappings/categories', 'mappings', 'Eşleştirme Ayarları')}</nav>
      <div className="settings-nav">{item('/settings', 'settings', 'Sistem Ayarları')}<button type="button" className="logout-link" onClick={() => void logout()}>{icon('logout')}<span className="nav-label">Çıkış Yap</span></button></div>
    </aside>
    <main>
      <Routes><Route path="/dashboard" element={<Dashboard me={me} />} /><Route path="/products" element={<ProductsPage />} /><Route path="/products/new" element={<NewProductPage />} /><Route path="/products/:id" element={<ProductDetailPage />} /><Route path="/catalog/categories" element={<CategoriesPage />} /><Route path="/catalog/brands" element={<BrandsPage />} /><Route path="/catalog/attributes" element={<AttributesPage />} /><Route path="/imports" element={<ImportsPage />} /><Route path="/imports/:id" element={<ImportDetailPage />} /><Route path="/inventory" element={<InventoryPage />} /><Route path="/integrations" element={<IntegrationsPage />} /><Route path="/integrations/:id" element={<IntegrationDetailPage />} /><Route path="/mappings/categories" element={<MappingPage />} /><Route path="/orders" element={<OrdersPage />} /><Route path="/orders/:id" element={<Navigate to="/orders" replace />} /><Route path="/shipments" element={<ShipmentsPage />} /><Route path="/shipments/:id" element={<ShipmentDetailPage />} /><Route path="/returns" element={<ReturnsPage />} /><Route path="/returns/:id" element={<ReturnDetailPage />} /><Route path="/invoices" element={<InvoicesPage />} /><Route path="/invoices/:id" element={<Navigate to="/orders" replace />} /><Route path="/jobs" element={<JobsPage me={me} />} /><Route path="/settings/billing" element={<Navigate to="/settings" replace />} /><Route path="/settings/security" element={<Navigate to="/settings?tab=security" replace />} /><Route path="/settings" element={<Security />} /><Route path="*" element={<Navigate to="/dashboard" replace />} /></Routes>
    </main>
  </div>
}

function useOperationsRealtime(enabled: boolean) {
  const client = useQueryClient()
  useEffect(() => {
    if (!enabled) return
    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/operations')
      .withAutomaticReconnect([0, 1000, 3000, 10_000])
      .configureLogging(LogLevel.Warning)
      .build()
    connection.on('operationsChanged', ({ resources }: { resources?: string[] }) => {
      const queryKeysByResource: Record<string, string[][]> = {
        orders: [['orders'], ['dashboard-orders'], ['dashboard-invoice-workspace']],
        returns: [['returns'], ['dashboard-returns']],
        products: [['products'], ['dashboard-products']],
        inventory: [['inventory'], ['dashboard-products']],
        invoices: [['invoices'], ['dashboard-invoice-workspace']],
        connections: [['connections'], ['dashboard-connections']],
        jobs: [['jobs'], ['dashboard-jobs']]
      }
      for (const resource of resources ?? []) {
        for (const queryKey of queryKeysByResource[resource.toLowerCase()] ?? []) void client.invalidateQueries({ queryKey })
      }
      void client.invalidateQueries({ queryKey: ['jobs'] })
      void client.invalidateQueries({ queryKey: ['dashboard-jobs'] })
    })
    void connection.start()
    return () => { void connection.stop() }
  }, [client, enabled])
}

export function App() {
  const me = useQuery({ queryKey: ['me'], queryFn: () => api<Me>('/me'), retry: false })
  useOperationsRealtime(me.data?.state === 'ACTIVE')
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

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setError('')
    setLoading(true)
    const data = new FormData(event.currentTarget)
    try {
      await api<{ state: string }>('/login', { method: 'POST', body: JSON.stringify({ email: email.trim(), password: data.get('password') }) })
      const currentUser = await api<Me>('/me')
      if (rememberMe) localStorage.setItem('ravencia.rememberedEmail', email.trim())
      else localStorage.removeItem('ravencia.rememberedEmail')
      client.setQueryData(['me'], currentUser)
      navigate('/dashboard', { replace: true })
    } catch (reason) {
      if (reason instanceof ApiRequestError && reason.status === 401) setError('E-posta veya parola hatalı. Art arda başarısız denemelerde hesap 15 dakika geçici olarak kilitlenir.')
      else if (reason instanceof ApiRequestError && reason.status === 429) setError('Çok fazla giriş denemesi yapıldı. Bir dakika bekleyip yeniden deneyin.')
      else if (reason instanceof ApiRequestError && reason.status === 400) setError('Güvenlik doğrulaması tamamlanamadı. Sayfayı yenileyip tekrar deneyin.')
      else setError('Giriş servisine ulaşılamadı. Bağlantınızı kontrol edip yeniden deneyin.')
    } finally {
      setLoading(false)
    }
  }

  return <main className="cyber-login-page">
    <div className="cyber-login-grid" aria-hidden="true" />
    <section className="cyber-login-story" aria-label="Ravencia operasyon merkezi">
      <div className="cyber-login-brand"><span>R</span><div><strong>RAVENCIA</strong><small>MARKETPLACEHUB</small></div></div>
      <div className="cyber-login-copy">
        <p>OPERASYON MERKEZİ</p>
        <h1>Tüm pazaryeri operasyonu, tek güvenli merkezde.</h1>
        <span>Sipariş, ürün, stok, iade, fatura ve entegrasyon akışlarını yerel veritabanınızdan yönetin.</span>
      </div>
      <div className="cyber-login-signals">
        <article><i className="good" /><div><strong>Yerel veri katmanı</strong><small>Liste ekranları API yerine Ravencia veritabanından okunur.</small></div></article>
        <article><i /><div><strong>Asenkron senkronizasyon</strong><small>Pazaryeri güncellemeleri güvenli işlem kuyruğunda yürütülür.</small></div></article>
        <article><i className="violet" /><div><strong>İzlenebilir operasyon</strong><small>Her kritik işlem sonuç ve hata geçmişiyle takip edilir.</small></div></article>
      </div>
      <div className="cyber-login-route" aria-hidden="true"><span>PAZARYERİ</span><b /><i>R</i><b /><span>YEREL VERİ</span></div>
    </section>

    <section className="cyber-login-panel">
      <div className="cyber-login-card">
        <header>
          <span className="cyber-login-shield" aria-hidden="true"><svg viewBox="0 0 24 24"><path d="M12 3 5 6v5c0 5 3 8 7 10 4-2 7-5 7-10V6l-7-3Z"/><path d="m9 12 2 2 4-5"/></svg></span>
          <div><p>GÜVENLİ OTURUM</p><h2>Yönetim paneline giriş</h2><span>Yetkili hesabınızla devam edin.</span></div>
        </header>
        <form className="cyber-login-form" onSubmit={submit}>
          <label htmlFor="login-email">E-posta adresi</label>
          <div className="cyber-login-input">
            <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 4h16v16H4z"/><path d="m4 7 8 6 8-6"/></svg>
            <input id="login-email" name="email" type="email" value={email} onChange={event => setEmail(event.target.value)} required autoComplete="username" placeholder="ornek@ravencia.com" autoFocus />
          </div>
          <div className="cyber-login-label-row"><label htmlFor="login-password">Parola</label><button type="button" onClick={() => setError('Parola sıfırlama için sistem yöneticinizle iletişime geçin.')}>Parolamı unuttum</button></div>
          <div className="cyber-login-input">
            <svg viewBox="0 0 24 24" aria-hidden="true"><rect x="4" y="10" width="16" height="11" rx="2"/><path d="M8 10V7a4 4 0 0 1 8 0v3"/></svg>
            <input id="login-password" name="password" type={showPw ? 'text' : 'password'} required autoComplete="current-password" placeholder="Parolanızı girin" />
            <button type="button" className="cyber-password-toggle" aria-label={showPw ? 'Parolayı gizle' : 'Parolayı göster'} onClick={() => setShowPw(value => !value)}><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12Z"/><circle cx="12" cy="12" r="3"/></svg></button>
          </div>
          <label className="cyber-remember"><input type="checkbox" checked={rememberMe} onChange={event => setRememberMe(event.target.checked)} /><span>E-posta adresimi bu cihazda hatırla</span></label>
          {error && <div className="cyber-login-error" role="alert"><i /> <span>{error}</span></div>}
          <button className="cyber-login-submit" type="submit" disabled={loading}>{loading ? <><i /> Oturum doğrulanıyor…</> : <>Güvenli giriş yap <span>→</span></>}</button>
        </form>
        <footer><span><i /> TLS ile şifrelenmiş bağlantı</span><small>Ravencia · Yetkili erişim</small></footer>
      </div>
    </section>
  </main>
}

function ChangePassword() { const client = useQueryClient(); const [message, setMessage] = useState('İlk girişte parolanızı değiştirmeniz gerekir.'); async function submit(event: FormEvent<HTMLFormElement>) { event.preventDefault(); const data = new FormData(event.currentTarget); try { await api('/change-password', { method: 'POST', body: JSON.stringify({ currentPassword: data.get('current'), newPassword: data.get('next') }) }); await client.invalidateQueries({ queryKey: ['me'] }) } catch { setMessage('Parola değiştirilemedi; politika ve mevcut parolayı kontrol edin.') } } return <div className="auth-page"><section className="auth-card"><h1>Parolanızı değiştirin</h1><p role="status">{message}</p><form onSubmit={submit}><label>Geçerli parola<input name="current" type="password" required /></label><label>Yeni parola<input name="next" type="password" minLength={15} maxLength={64} required /></label><button>Parolayı değiştir</button></form></section></div> }
function MfaChallenge() { const client = useQueryClient(); const [error, setError] = useState(''); async function submit(event: FormEvent<HTMLFormElement>) { event.preventDefault(); const data = new FormData(event.currentTarget); try { await api('/mfa/challenge', { method: 'POST', body: JSON.stringify({ code: data.get('code'), recoveryCode: data.get('recovery') || null }) }); await client.invalidateQueries({ queryKey: ['me'] }) } catch { setError('Kod geçersiz veya daha önce kullanılmış.') } } return <div className="auth-page"><section className="auth-card"><h1>İki adımlı doğrulama</h1><p>Authenticator uygulamanızdaki 6 haneli kodu girin.</p><form onSubmit={submit}><label>Doğrulama kodu<input name="code" inputMode="numeric" pattern="[0-9]{6}" /></label><label>Kurtarma kodu (alternatif)<input name="recovery" /></label>{error && <div role="alert" className="error">{error}</div>}<button>Doğrula</button></form></section></div> }
type DashboardOrder = { orderNumber: string; derivedStatus: string; orderedAt: string; currency: string; grossAmount: number; netAmount: number; platformDisplayName: string; shipmentDueAt: string | null; isDeadlineCritical: boolean; cargoProviderName: string | null; productQuantity: number }
type DashboardReturn = { status: string }
type DashboardInvoice = { invoiceId: string | null; isDueSoon: boolean; canCreateInvoice: boolean }
type DashboardProduct = { id: string; title: string; totalStock: number; primaryImageUrl: string | null; activePlatforms: string[] | null }
type DashboardJob = { jobType: string; status: string; createdAt: string; completedAt: string | null }
type DashboardRevenueRange = '1' | '3' | '7' | '14' | '30' | 'month' | 'custom'

function dashboardDateKey(value: Date) {
  if (Number.isNaN(value.getTime())) return ''
  const pad = (part: number) => String(part).padStart(2, '0')
  return `${value.getFullYear()}-${pad(value.getMonth() + 1)}-${pad(value.getDate())}`
}

function dashboardDateInputValue(value = new Date()) { return dashboardDateKey(value) }
function dashboardRevenueAmount(item: DashboardOrder) {
  const status = item.derivedStatus.toUpperCase()
  return status === 'CANCELLED' || status === 'CANCELED' || status === 'RETURNED' ? 0 : item.netAmount || item.grossAmount || 0
}

function DashboardMetricIcon({ kind }: { kind: string }) {
  const icons: Record<string, ReactNode> = {
    pending: <><path d="M4 6h16v12H4z"/><path d="M8 4v4M16 4v4M7 11h4M7 15h7"/></>,
    late: <><path d="M12 4 3.5 19h17L12 4Z"/><path d="M12 10v4M12 17h.01"/></>,
    today: <><rect x="4" y="5" width="16" height="15" rx="2"/><path d="M8 3v4M16 3v4M4 10h16M8 14h3M8 17h5"/></>,
    month: <><rect x="4" y="5" width="16" height="15" rx="2"/><path d="M8 3v4M16 3v4M4 10h16M8 14h8M8 17h5"/></>,
    return: <><path d="M5 8h10a4 4 0 0 1 0 8H9"/><path d="m8 5-3 3 3 3"/><path d="M19 16h-4"/></>,
    invoice: <><path d="M7 3h10v18l-5-2-5 2V3Z"/><path d="M9 8h6M9 12h6"/></>,
    uninvoiced: <><path d="M6 3h12v18l-6-2-6 2V3Z"/><path d="M9 8h6M9 12h4"/></>,
    stock: <><path d="m4 8 8-4 8 4-8 4-8-4Z"/><path d="M4 8v8l8 4 8-4V8M12 12v8"/></>
  }
  return <span className={`dashboard-metric-icon ${kind}`} aria-hidden="true"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">{icons[kind] ?? icons.pending}</svg></span>
}

function dashboardSyncTime(job: DashboardJob | undefined) {
  if (!job) return 'Kayıt yok'
  const timestamp = new Date(job.completedAt ?? job.createdAt)
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

function Dashboard({ me }: { me: Me }) {
  const [revenueRange, setRevenueRange] = useState<DashboardRevenueRange>('1')
  const [revenueFrom, setRevenueFrom] = useState(() => dashboardDateInputValue())
  const [revenueTo, setRevenueTo] = useState(() => dashboardDateInputValue())
  const [revenuePlatform, setRevenuePlatform] = useState('ALL')
  const dashboardRefreshOptions = { refetchInterval: 60_000, refetchOnWindowFocus: true } as const
  const connections = useQuery({ queryKey: ['dashboard-connections'], queryFn: () => loadAllPages<ShellConnection>('/connections'), ...dashboardRefreshOptions })
  const orders = useQuery({ queryKey: ['dashboard-orders'], queryFn: () => loadAllPages<DashboardOrder>('/orders'), ...dashboardRefreshOptions })
  const returns = useQuery({ queryKey: ['dashboard-returns'], queryFn: () => loadAllPages<DashboardReturn>('/returns'), ...dashboardRefreshOptions })
  const invoices = useQuery({ queryKey: ['dashboard-invoice-workspace'], queryFn: () => hubApi<DashboardInvoice[]>('/invoice-workspace'), ...dashboardRefreshOptions })
  const products = useQuery({ queryKey: ['dashboard-products'], queryFn: () => loadAllPages<DashboardProduct>('/products'), ...dashboardRefreshOptions })
  const jobs = useQuery({ queryKey: ['dashboard-jobs'], queryFn: () => hubApi<DashboardJob[]>('/jobs'), ...dashboardRefreshOptions })
  const loading = [connections, orders, returns, invoices, products, jobs].some(query => query.isLoading)
  const now = new Date(); const orderItems = orders.data?.items ?? []; const productItems = products.data?.items ?? []
  const terminal = new Set(['DELIVERED', 'CANCELLED', 'CANCELED', 'RETURNED'])
  const pending = orderItems.filter(item => !terminal.has(item.derivedStatus.toUpperCase()))
  const late = pending.filter(item => item.shipmentDueAt && new Date(item.shipmentDueAt) < now)
  const today = orderItems.filter(item => { const value = new Date(item.orderedAt); return value.getFullYear() === now.getFullYear() && value.getMonth() === now.getMonth() && value.getDate() === now.getDate() })
  const month = orderItems.filter(item => { const value = new Date(item.orderedAt); return value.getFullYear() === now.getFullYear() && value.getMonth() === now.getMonth() })
  const pendingReturns = (returns.data?.items ?? []).filter(item => ['REQUESTED', 'AWAITING_SHIPMENT', 'IN_TRANSIT', 'ACTION_REQUIRED', 'DISPUTED'].includes(item.status.toUpperCase())).length
  const uninvoiced = (invoices.data ?? []).filter(item => !item.invoiceId && item.canCreateInvoice).length
  const dueSoon = (invoices.data ?? []).filter(item => !item.invoiceId && item.isDueSoon).length
  const lowStock = productItems.filter(item => item.totalStock <= 5)
  const verified = (connections.data?.items ?? []).filter(item => item.status === 'VERIFIED' || item.status === 'ACTIVE').length
  const group = (values: string[]) => Object.entries(values.reduce<Record<string, number>>((acc, value) => { const label = value || 'Belirtilmemiş'; acc[label] = (acc[label] ?? 0) + 1; return acc }, {})).sort((a, b) => b[1] - a[1])
  const byPlatform = group(pending.map(item => item.platformDisplayName || 'Trendyol'))
  const revenuePlatformOptions = group(orderItems.map(item => item.platformDisplayName || 'Belirtilmemiş')).map(([name]) => name)
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
  const revenueDays = Math.max(1, Math.floor((revenueEnd.getTime() - revenueStart.getTime()) / 86_400_000) + 1)
  const revenueOrders = orderItems.filter(item => { const orderedAt = new Date(item.orderedAt); return dashboardRevenueAmount(item) > 0 && orderedAt >= revenueStart && orderedAt <= revenueEnd && (revenuePlatform === 'ALL' || (item.platformDisplayName || 'Belirtilmemiş') === revenuePlatform) })
  const revenueSeries = Array.from({ length: revenueDays }, (_, index) => {
    const date = new Date(revenueStart)
    date.setDate(revenueStart.getDate() + index)
    const key = dashboardDateKey(date)
    const amount = revenueOrders.filter(item => dashboardDateKey(new Date(item.orderedAt)) === key).reduce((sum, item) => sum + dashboardRevenueAmount(item), 0)
    return { key, amount, label: date.toLocaleDateString('tr-TR', { day: 'numeric', month: 'short' }), currency: revenueOrders.find(item => dashboardDateKey(new Date(item.orderedAt)) === key)?.currency || 'TRY' }
  })
  const maxRevenue = Math.max(1, ...revenueSeries.map(item => item.amount))
  const revenueTotal = revenueSeries.reduce((sum, item) => sum + item.amount, 0)
  const connectionItems = connections.data?.items ?? []
  const activeConnections = connectionItems.filter(item => ['ACTIVE', 'VERIFIED', 'CONNECTED'].includes(item.status.toUpperCase()))
  const syncConnection = activeConnections[0] ?? connectionItems[0]
  const latestSuccessfulJob = (terms: string[]) => (jobs.data ?? []).filter(job => job.status === 'SUCCEEDED' && terms.some(term => job.jobType.toUpperCase().includes(term))).sort((a, b) => new Date(b.completedAt ?? b.createdAt).getTime() - new Date(a.completedAt ?? a.createdAt).getTime())[0]
  const syncDefinitions = [
    { label: 'Siparişler', kind: 'orders', terms: ['ORDER', 'SHIPMENT', 'PACKAGE', 'WEBHOOK'], required: true },
    { label: 'İadeler', kind: 'returns', terms: ['RETURN', 'CLAIM'], required: true },
    { label: 'Stok', kind: 'stock', terms: ['INVENTORY', 'STOCK', 'PRICE'], required: true },
    { label: 'Ürünler', kind: 'products', terms: ['PRODUCT', 'CATALOG', 'PUBLICATION'], required: false },
    { label: 'Faturalar', kind: 'invoices', terms: ['INVOICE', 'EFATURAM', 'BILLING'], required: false },
    { label: 'Kategoriler', kind: 'catalog', terms: ['CATEGORY', 'BRAND', 'ATTRIBUTE', 'MAPPING'], required: false }
  ]
  const syncRows = syncDefinitions.map(definition => ({ ...definition, job: latestSuccessfulJob(definition.terms) })).filter(row => row.required || row.job)
  const latestSyncJob = syncRows.map(row => row.job).filter((job): job is DashboardJob => Boolean(job)).sort((a, b) => new Date(b.completedAt ?? b.createdAt).getTime() - new Date(a.completedAt ?? a.createdAt).getTime())[0]
  const errors = [connections.error, orders.error, returns.error, invoices.error, products.error, jobs.error].filter(Boolean)
  return <section className="content dashboard"><div className="page-heading"><div><p className="eyebrow">Operasyon merkezi</p><h1>Genel Bakış</h1><p className="lede">Merhaba {me.displayName}. Günlük operasyonun önemli sinyalleri tek ekranda.</p></div><div className="dashboard-heading-actions"><span className="dashboard-date">{now.toLocaleDateString('tr-TR', { day: 'numeric', month: 'long', year: 'numeric' })}</span></div></div>
    {errors.length > 0 && <div role="alert" className="error">Bazı operasyon verileri alınamadı; görünen metrikler kısmi olabilir.</div>}
    <div className="metrics dashboard-metrics operational-metrics"><article><DashboardMetricIcon kind="pending" /><small>BEKLEYEN SİPARİŞ</small><strong>{loading ? '—' : pending.length}</strong><p>{byPlatform.map(([name, count]) => `${name}: ${count}`).join(' · ') || 'Bekleyen yok'}</p></article><article className={late.length ? 'danger-metric' : ''}><DashboardMetricIcon kind="late" /><small>GECİKEN SİPARİŞ</small><strong>{loading ? '—' : late.length}</strong><p>Termin zamanı aşılmış</p></article><article><DashboardMetricIcon kind="today" /><small>BUGÜNKÜ SİPARİŞ</small><strong>{loading ? '—' : today.length}</strong><p>{today.reduce((sum, item) => sum + item.productQuantity, 0)} ürün</p></article><article><DashboardMetricIcon kind="month" /><small>BU AYKİ SİPARİŞ</small><strong>{loading ? '—' : month.length}</strong><p>Ay içindeki siparişler</p></article><article><DashboardMetricIcon kind="return" /><small>BEKLEYEN İADE</small><strong>{loading ? '—' : pendingReturns}</strong><p>İşlem veya taşıma aşaması</p></article><article className={dueSoon ? 'warning-metric' : ''}><DashboardMetricIcon kind="invoice" /><small>SÜRESİ YAKLAŞAN FATURA</small><strong>{loading ? '—' : dueSoon}</strong><p>5. gün ve sonrası</p></article><article><DashboardMetricIcon kind="uninvoiced" /><small>FATURALANDIRILMAMIŞ</small><strong>{loading ? '—' : uninvoiced}</strong><p>Kesilebilir kayıtlar</p></article><article><DashboardMetricIcon kind="stock" /><small>DÜŞÜK / YOK STOK</small><strong>{loading ? '—' : lowStock.length}</strong><p>5 adet ve altı</p></article></div>
    <div className="dashboard-report-grid"><article className="panel dashboard-revenue-panel"><div className="panel-title"><div><h2>Satış cirosu</h2><p>Seçilen dönemde gerçekleşen sipariş toplamı</p></div><div className="dashboard-revenue-controls"><label className="dashboard-period-select"><span>Ciro dönemi</span><select aria-label="Ciro dönemi" value={revenueRange} onChange={event => setRevenueRange(event.target.value as DashboardRevenueRange)}><option value="1">Günlük</option><option value="3">Son 3 gün</option><option value="7">Son 7 gün</option><option value="14">Son 14 gün</option><option value="30">Son 30 gün</option><option value="month">Bu ay</option><option value="custom">Özel tarih</option></select></label><label><span>Platform</span><select aria-label="Ciro platformu" value={revenuePlatform} onChange={event => setRevenuePlatform(event.target.value)}><option value="ALL">Tüm platformlar</option>{revenuePlatformOptions.map(platform => <option value={platform} key={platform}>{platform}</option>)}</select></label></div></div>{revenueRange === 'custom' && <div className="dashboard-custom-range"><label><span>Başlangıç</span><input type="date" value={revenueFrom} max={revenueTo} onChange={event => setRevenueFrom(event.target.value)} /></label><label><span>Bitiş</span><input type="date" value={revenueTo} min={revenueFrom} onChange={event => setRevenueTo(event.target.value)} /></label></div>}<div className="dashboard-revenue-summary"><strong>{dashboardMoney(revenueTotal, revenueSeries.find(item => item.amount > 0)?.currency || 'TRY')}</strong><span>{revenueOrders.length} sipariş</span></div><div className="dashboard-revenue-chart">{revenueSeries.map(point => <div className="dashboard-revenue-column" key={point.key}><div className="dashboard-revenue-bar-wrap"><b className="dashboard-revenue-bar" style={{ height: `${Math.max(point.amount ? 12 : 4, point.amount / maxRevenue * 100)}%` }} title={dashboardMoney(point.amount, point.currency)} /></div><span>{point.label}</span></div>)}</div></article><article className="panel dashboard-api-panel"><div className="panel-title"><div><h2>Son senkronizasyonlar</h2><p>Sipariş, iade ve stok kayıtlarının güncel zamanı</p></div><Link to="/jobs">İşlem takibi →</Link></div><div className="dashboard-api-list dashboard-sync-list">{syncRows.map(row => <Link to="/jobs" key={row.label}><span className={`dashboard-sync-icon ${row.kind}`} aria-hidden="true">↻</span><span><strong>{row.label}</strong><small>{row.job ? 'Başarılı senkronizasyon' : 'Henüz kayıt yok'}</small></span><b>{dashboardSyncTime(row.job)}</b></Link>)}</div><div className="dashboard-sync-meta"><span aria-hidden="true">↻</span><span className="dashboard-sync-meta-copy"><strong>{latestSyncJob ? `Son veri senkronizasyonu: ${dashboardSyncTime(latestSyncJob)}` : 'Senkronizasyon kaydı yok'}</strong><small>Otomatik kontrol: 60 sn · Veri: {latestSyncJob ? dashboardSyncTime(latestSyncJob) : 'Kayıt yok'}</small></span></div></article></div>
    <div className="dashboard-bottom-grid"><article className="panel dashboard-flow-panel"><div className="panel-title"><div><h2>Sipariş akışı</h2><p>Operasyon kayıtlarının anlık özeti</p></div><Link to="/orders">Detaylar →</Link></div><div className="dashboard-flow-list"><Link to="/orders"><span><i className="flow-dot new" /><strong>Bekleyen sipariş</strong></span><b>{loading ? '—' : pending.length}</b></Link><Link to="/orders"><span><i className="flow-dot late" /><strong>Geciken sipariş</strong></span><b>{loading ? '—' : late.length}</b></Link><Link to="/invoices"><span><i className="flow-dot invoice" /><strong>Fatura bekliyor</strong></span><b>{loading ? '—' : uninvoiced}</b></Link><Link to="/returns"><span><i className="flow-dot return" /><strong>Bekleyen iade</strong></span><b>{loading ? '—' : pendingReturns}</b></Link></div></article><article className="panel dashboard-active-platform"><div className="dashboard-platform-orbit" aria-hidden="true"><span /><span /><span /><i /></div><small>AKTİF PLATFORM</small><strong>{loading ? '—' : activeConnections.length || verified}</strong><p>{syncConnection?.displayName ?? 'Bağlantı bekleniyor'}</p><Link to="/integrations">Platformları yönet →</Link></article><article className="panel dashboard-stock-panel"><div className="panel-title"><div><h2>Stok durumu</h2><p>En düşük stoklu ürünler</p></div><Link to="/products">Ürünler →</Link></div><div className="dashboard-product-list">{lowStock.sort((a, b) => a.totalStock - b.totalStock).slice(0, 4).map(item => <Link to={`/products/${item.id}`} key={item.id}>{item.primaryImageUrl ? <img src={item.primaryImageUrl} alt="" /> : <span>▧</span>}<strong>{item.title}</strong><b>{item.totalStock}</b></Link>)}{!lowStock.length && <p>Düşük stoklu ürün yok.</p>}</div></article></div>
  </section>
}
type SecurityStatus = { totpState: string; recoveryCodesRemaining: number }
type SecuritySession = { id: string; state: string; current: boolean; issuedAt: string; lastSeenAt: string; expiresAt: string }
type MfaSetup = { otpauthUri: string; qrSvg: string; expiresAt: string }

function Security() {
  const client = useQueryClient()
  const [settingsTab, setSettingsTab] = useState<'security' | 'database' | 'shipping'>(() => {
    const tab = new URLSearchParams(window.location.search).get('tab')
    return tab === 'database' || tab === 'shipping' ? tab : 'security'
  })
  const [labelSettings, setLabelSettings] = useState<ShippingLabelSettings>(() => loadShippingLabelSettings())
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
    if (!resetScopes.length || resetConfirmation !== 'VERİLERİ SİL') return
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
    return <label key={scope}><input type="checkbox" checked={resetScopes.includes(scope)} onChange={event => toggleResetScope(scope, event.target.checked)} /><span className="database-scope-label" data-description={detail.description}><strong>{detail.label}</strong><span className="database-scope-help" role="img" tabIndex={0} data-tooltip={detail.description} aria-label={`${detail.label}: ${detail.description}`}>!</span></span></label>
  }

  function saveLabelSettings() {
    saveShippingLabelSettings(labelSettings)
    setMessage('Kargo etiketi ayarları bu tarayıcıya kaydedildi.')
  }

  if (settingsTab === 'database') return <section className="content security-page"><div className="page-heading"><div><p className="eyebrow">Ayarlar</p><h1>Sistem ayarları</h1><p className="lede">Güvenlik ve yerel operasyon verilerini tek ekranda yönetin.</p></div></div><div className="settings-tabs" role="tablist"><button type="button" role="tab" aria-selected={false} onClick={() => setSettingsTab('security')}>Güvenlik ve oturumlar</button><button type="button" role="tab" aria-selected={true} className="active">Veritabanı temizliği</button><button type="button" role="tab" aria-selected={false} onClick={() => setSettingsTab('shipping')}>Kargo ayarları</button></div>{message && <div className="notice" role="status">{message}</div>}<div className="panel database-reset-panel"><div className="database-reset-intro"><span className="security-state">YETKİLİ İŞLEMİ</span><h2>Yerel veritabanı listelerini sıfırla</h2><p>Yalnız seçtiğiniz alanlar bu hesabın yerel veritabanından silinir. Her başlığın yanındaki ünlemden kapsam ve bağlı kayıt ayrıntılarını görebilirsiniz.</p></div><div className="database-scope-groups"><section className="database-scope-group"><div><h3>Katalog</h3><p>Ürün kataloğunda kullanılan temel listeleri temizleyin.</p></div><div className="database-scope-list">{['PRODUCTS', 'CATEGORIES', 'CATEGORY_ATTRIBUTES', 'BRANDS'].map(resetScopeOption)}</div></section><section className="database-scope-group"><div><h3>Ürün seçenekleri</h3><p>Ürün seçeneklerini ve seçenek değerlerini temizleyin.</p></div><div className="database-scope-list">{['OPTIONS'].map(resetScopeOption)}</div></section><section className="database-scope-group"><div><h3>Operasyon</h3><p>İşlem ve satış kayıtlarını temizleyin.</p></div><div className="database-scope-list">{['ORDERS', 'RETURNS', 'INVOICES'].map(resetScopeOption)}</div></section></div><label className="database-confirmation">Onay için <b>VERİLERİ SİL</b> yazın<input value={resetConfirmation} onChange={event => setResetConfirmation(event.target.value)} /></label><button type="button" className="destructive" disabled={!resetScopes.length || resetConfirmation !== 'VERİLERİ SİL' || resetBusy} onClick={() => void resetOperationalData()}>{resetBusy ? 'Temizleniyor…' : 'Seçili listeleri kalıcı sil'}</button></div></section>

  async function deleteClosedSessions() {
    if (!window.confirm('Tüm kapalı oturum kayıtları silinsin mi?')) return
    setMessage('')
    try { await api('/sessions/closed', { method: 'DELETE' }); await client.invalidateQueries({ queryKey: ['sessions'] }); setMessage('Kapalı oturumlar silindi.') } catch { setMessage('Kapalı oturumlar silinemedi.') }
  }

  const legacySettingsTab = settingsTab as string; if (settingsTab === 'shipping') return <section className="content security-page"><div className="page-heading"><div><p className="eyebrow">Ayarlar</p><h1>Sistem ayarları</h1><p className="lede">Etiket ölçüsü ve yazdırma düzenini yönetin.</p></div></div><div className="settings-tabs" role="tablist"><button type="button" role="tab" aria-selected={false} onClick={() => setSettingsTab('security')}>Güvenlik ve oturumlar</button><button type="button" role="tab" aria-selected={false} onClick={() => setSettingsTab('database')}>Veritabanı temizliği</button><button type="button" role="tab" aria-selected={true} className="active">Kargo ayarları</button></div>{message && <div className="notice" role="status">{message}</div>}<ShippingLabelSettingsPanel settings={labelSettings} onChange={setLabelSettings} onSave={saveLabelSettings} /></section>
  return <section className="content security-page"><div className="page-heading"><div><p className="eyebrow">Ayarlar</p><h1>Sistem ayarları</h1><p className="lede">Güvenlik ve yerel operasyon verilerini tek ekrandan yönetin.</p></div></div><div className="settings-tabs" role="tablist"><button type="button" role="tab" aria-selected={settingsTab === 'security'} className={settingsTab === 'security' ? 'active' : ''} onClick={() => setSettingsTab('security')}>Güvenlik ve oturumlar</button><button type="button" role="tab" aria-selected={legacySettingsTab === 'database'} className={legacySettingsTab === 'database' ? 'active' : ''} onClick={() => setSettingsTab('database')}>Veritabanı temizliği</button><button type="button" role="tab" aria-selected={legacySettingsTab === 'shipping'} className={legacySettingsTab === 'shipping' ? 'active' : ''} onClick={() => setSettingsTab('shipping')}>Kargo ayarları</button></div>{message && <div className="notice" role="status">{message}</div>}{settingsTab === 'security' && <>{status.isLoading ? <Status title="Güvenlik durumu yükleniyor" /> : status.isError || !status.data ? <div role="alert" className="error">Güvenlik durumu alınamadı.</div> : <div className="panel security-authenticator-card"><div><span className={`security-state ${status.data.totpState === 'ENABLED' ? 'enabled' : ''}`}>{status.data.totpState === 'ENABLED' ? 'Etkin' : 'Kapalı'}</span><h2>Authenticator</h2><p>Giriş sırasında telefonunuzdaki tek kullanımlık kodla hesabınızı koruyun.</p><small>Kalan kurtarma kodu: <strong>{status.data.recoveryCodesRemaining}</strong></small></div>{status.data.totpState === 'ENABLED' ? <span className="security-check" aria-label="Authenticator etkin">✓</span> : <button type="button" onClick={() => { setMessage(''); setMfaStep('password') }}>Authenticator’ı etkinleştir</button>}</div>}
    <div className="panel security-sessions-card"><div className="panel-title"><div><h2>Oturumlar</h2><p>Hesabınıza bağlı cihazları ve son etkinliklerini görüntüleyin.</p></div><div className="session-bulk-actions">{activeOtherSessions.length > 0 && <button type="button" className="secondary danger-outline" onClick={() => void revokeOthers()}>Diğer tüm oturumları kapat</button>}{closedSessions.length > 0 && <button type="button" className="secondary danger-outline" onClick={() => void deleteClosedSessions()}>Kapalı oturumları sil</button>}</div></div>{sessions.isLoading ? <p>Yükleniyor…</p> : sessions.isError || !sessions.data ? <div role="alert" className="error">Oturumlar alınamadı.</div> : <ul className="sessions">{sessions.data.map(session => <li key={session.id} className={session.current ? 'current' : ''}><span className="session-device-icon" aria-hidden="true">{session.current ? '●' : '○'}</span><span><strong>{session.current ? 'Bu cihaz' : 'Diğer oturum'}</strong><small>{session.state === 'ACTIVE' ? 'Aktif' : 'Sonlandırıldı'} · Son etkinlik {new Date(session.lastSeenAt).toLocaleString('tr-TR')}</small><small>Bitiş {new Date(session.expiresAt).toLocaleString('tr-TR')}</small></span>{session.current ? <b>Mevcut oturum</b> : session.state === 'ACTIVE' ? <button type="button" className="secondary danger-outline" onClick={() => void revokeSession(session.id)}>Oturumu sonlandır</button> : <button type="button" className="secondary danger-outline" onClick={() => void deleteSession(session.id)}>Kaydı sil</button>}</li>)}</ul>}</div>
    {mfaStep !== 'closed' && <div className="workspace-modal-backdrop" role="presentation"><section className="workspace-modal security-modal" role="dialog" aria-modal="true" aria-labelledby="mfa-title"><header><div><h2 id="mfa-title">Authenticator kurulumu</h2><p>{mfaStep === 'password' ? 'Önce hesabın size ait olduğunu doğrulayın.' : mfaStep === 'verify' ? 'QR kodu uygulamanıza ekleyip üretilen kodu girin.' : 'Kurtarma kodlarını şimdi güvenli bir yerde saklayın.'}</p></div><button className="modal-close" type="button" aria-label="Kapat" onClick={() => setMfaStep('closed')}>×</button></header>{mfaStep === 'password' && <form className="security-modal-body" onSubmit={prepareMfa}><label>Mevcut parola<input name="password" type="password" autoComplete="current-password" required /></label><button disabled={busy}>{busy ? 'Doğrulanıyor…' : 'Devam et'}</button></form>}{mfaStep === 'verify' && setup && <form className="security-modal-body mfa-verify" onSubmit={confirmMfa}><img src={`data:image/svg+xml;utf8,${encodeURIComponent(setup.qrSvg)}`} alt="Authenticator QR kodu" /><div><p>QR kodu Google Authenticator, Microsoft Authenticator veya uyumlu uygulamanızla tarayın.</p><details><summary>Kurulum anahtarını elle göster</summary><code>{setup.otpauthUri}</code></details><label>6 haneli doğrulama kodu<input name="code" inputMode="numeric" pattern="[0-9]{6}" autoComplete="one-time-code" required /></label><button disabled={busy}>{busy ? 'Kontrol ediliyor…' : 'Etkinleştir'}</button></div></form>}{mfaStep === 'recovery' && <div className="security-modal-body"><div className="recovery-code-grid">{recoveryCodes.map(code => <code key={code}>{code}</code>)}</div><p>Bu kodlar yalnızca bir kez gösterilir. Her kod tek kullanımlıktır.</p><button type="button" onClick={() => setMfaStep('closed')}>Kodları sakladım</button></div>}{message && <div className="error security-modal-error" role="alert">{message}</div>}</section></div>}</>}
    {legacySettingsTab === 'database' && <div className="panel database-reset-panel"><div className="database-reset-intro"><span className="security-state">YETKİLİ İŞLEMİ</span><h2>Yerel veritabanı listelerini sıfırla</h2><p>Seçilen kayıtlar yalnız bu hesabın yerel veritabanından silinir. Bağlı alt kayıtlar güvenli sırayla temizlenir.</p></div><div className="database-scope-groups"><section className="database-scope-group"><div><h3>Katalog</h3><p>Ürün kataloğunda kullanılan temel listeleri temizleyin.</p></div><div className="database-scope-list">{[['PRODUCTS','Ürünler listesi'],['CATEGORIES','Kategori listesi'],['BRANDS','Marka listesi']].map(([scope,label]) => <label key={scope}><input type="checkbox" checked={resetScopes.includes(scope)} onChange={event => toggleResetScope(scope, event.target.checked)} /><span><strong>{label}</strong><small>Yerel kayıtları ve bağlı alt kayıtları temizle</small></span></label>)}</div></section><section className="database-scope-group"><div><h3>Ürün seçenekleri</h3><p>Ürün seçeneklerini ve seçenek değerlerini temizleyin.</p></div><div className="database-scope-list">{[['OPTIONS','Seçenekler listesi']].map(([scope,label]) => <label key={scope}><input type="checkbox" checked={resetScopes.includes(scope)} onChange={event => toggleResetScope(scope, event.target.checked)} /><span><strong>{label}</strong><small>Ürün seçeneklerini ve bağlı değerleri temizle</small></span></label>)}</div></section><section className="database-scope-group"><div><h3>Operasyon</h3><p>İşlem ve satış kayıtlarını temizleyin.</p></div><div className="database-scope-list">{[['ORDERS','Siparişler listesi'],['RETURNS','İadeler listesi'],['INVOICES','Faturalar listesi']].map(([scope,label]) => <label key={scope}><input type="checkbox" checked={resetScopes.includes(scope)} onChange={event => toggleResetScope(scope, event.target.checked)} /><span><strong>{label}</strong><small>Yerel kayıtları ve bağlı alt kayıtları temizle</small></span></label>)}</div></section></div><label className="database-confirmation">Onay için <b>VERİLERİ SİL</b> yazın<input value={resetConfirmation} onChange={event => setResetConfirmation(event.target.value)} /></label><button type="button" className="destructive" disabled={!resetScopes.length || resetConfirmation !== 'VERİLERİ SİL' || resetBusy} onClick={() => void resetOperationalData()}>{resetBusy ? 'Temizleniyor…' : 'Seçili listeleri kalıcı sil'}</button></div>}
  </section>
}
function ShippingLabelSettingsPanel({ settings, onChange, onSave }: { settings: ShippingLabelSettings; onChange: (value: ShippingLabelSettings) => void; onSave: () => void }) {
  function update<K extends keyof ShippingLabelSettings>(key: K, value: ShippingLabelSettings[K]) {
    onChange({ ...settings, [key]: value })
  }
  return <div className="panel shipping-settings-panel">
    <div className="shipping-settings-intro"><span className="security-state enabled">YAZDIRMA DÜZENİ</span><h2>Kargo etiketi ayarları</h2><p>A4 ve sticker etiketleri sipariş ekranından ayrılmadan hazırlayın. Bu ayarlar yalnızca bu tarayıcıda saklanır.</p></div>
    <div className="shipping-settings-grid">
      <label>Gönderici adı<input value={settings.senderName} maxLength={120} onChange={event => update('senderName', event.target.value)} /></label>
      <label>Gönderici adresi<textarea value={settings.senderAddress} maxLength={500} rows={3} onChange={event => update('senderAddress', event.target.value)} placeholder="İsteğe bağlı" /></label>
      <label>A4 sayfa düzeni<select value={settings.a4LabelsPerPage} onChange={event => update('a4LabelsPerPage', Number(event.target.value) as ShippingLabelSettings['a4LabelsPerPage'])}><option value={1}>Sayfada 1 etiket</option><option value={2}>Sayfada 2 etiket</option><option value={4}>Sayfada 4 etiket</option></select></label>
      <label>Sticker genişliği (mm)<input type="number" min={40} max={300} value={settings.stickerWidthMm} onChange={event => update('stickerWidthMm', Math.min(300, Math.max(40, Number(event.target.value) || 40)))} /></label>
      <label>Sticker yüksekliği (mm)<input type="number" min={40} max={300} value={settings.stickerHeightMm} onChange={event => update('stickerHeightMm', Math.min(300, Math.max(40, Number(event.target.value) || 40)))} /></label>
      <label className="shipping-settings-check"><input type="checkbox" checked={settings.showCustomerPhone} onChange={event => update('showCustomerPhone', event.target.checked)} /><span>Müşteri iletişim alanını göster</span></label>
    </div>
    <div className="shipping-settings-actions"><button type="button" onClick={onSave}>Ayarları kaydet</button></div>
  </div>
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
