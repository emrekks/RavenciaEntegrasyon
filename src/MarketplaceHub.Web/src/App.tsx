import { useState, type FormEvent, type ReactNode } from 'react'
import { Link, Navigate, NavLink, Route, Routes, useLocation, useNavigate } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, hubApi, type Me } from './api'
import { AttributesPage, BrandsPage, CategoriesPage, ImportDetailPage, ImportsPage, InventoryPage, NewProductPage, ProductDetailPage, ProductsPage } from './CatalogPages'
import { IntegrationDetailPage, IntegrationsPage, MappingPage, OrdersPage, ReturnDetailPage, ReturnsPage, ShipmentDetailPage, ShipmentsPage } from './MarketplacePages'
import { InvoicesPage } from './InvoicingPages'
import { JobsPage } from './OperationsPages'

function Shell({ me }: { me: Me }) {
  const location = useLocation()
  const titles: Record<string, string> = { dashboard: 'Dashboard', products: 'Ürünler', catalog: 'Katalog', imports: 'İçe Aktarım', inventory: 'Stok', integrations: 'Platformlar · Trendyol · E-Faturam', mappings: 'Eşleştirme Ayarları', orders: 'Siparişler', shipments: 'Gönderiler', returns: 'İadeler', invoices: 'Faturalar', jobs: 'İşlem Takibi', settings: 'Ayarlar' }
  const current = titles[location.pathname.split('/')[1]] ?? 'Operasyon Merkezi'
  const [settingsOpen, setSettingsOpen] = useState(location.pathname.startsWith('/settings') || location.pathname === '/integrations' || location.pathname.startsWith('/mappings'))
  const [sidebarCollapsed, setSidebarCollapsed] = useState(() => localStorage.getItem('ravencia.sidebarCollapsed') === '1')
  async function logout() { await api('/logout', { method: 'POST' }); window.location.assign('/') }
  function toggleSidebar() { setSidebarCollapsed(value => { const next = !value; localStorage.setItem('ravencia.sidebarCollapsed', next ? '1' : '0'); return next }) }
  const icons: Record<string, ReactNode> = {
    dashboard: <><rect x="3" y="3" width="7" height="7" rx="1"/><rect x="14" y="3" width="7" height="7" rx="1"/><rect x="3" y="14" width="7" height="7" rx="1"/><rect x="14" y="14" width="7" height="7" rx="1"/></>,
    products: <><path d="m4 7 8-4 8 4-8 4-8-4Z"/><path d="m4 7 8 4 8-4v10l-8 4-8-4V7Z"/><path d="M12 11v10"/></>,
    orders: <><path d="M3 6h18l-2 13H5L3 6Z"/><path d="M8 6V4h8v2M8 11h8"/></>,
    returns: <><path d="M9 7H5v-4"/><path d="M5 7a8 8 0 1 1-1 8"/><path d="m5 7 4-4"/></>,
    invoices: <><path d="M6 3h9l3 3v15H6V3Z"/><path d="M14 3v4h4M9 11h6M9 15h6"/></>,
    jobs: <><circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/></>,
    settings: <><circle cx="12" cy="12" r="3"/><path d="M19 13.5v-3l-2-.7-.8-1.8.9-2-2.1-2.1-2 .9-1.8-.8L10.5 1h-3l-.7 2-1.8.8-2-.9L.9 5l.9 2L1 8.8l-2 .7v3l2 .7.8 1.8-.9 2L3 19.1l2-.9 1.8.8.7 2h3l.7-2 1.8-.8 2 .9 2.1-2.1-.9-2 .8-1.8 2-.7Z" transform="translate(2 1) scale(.83)"/></>,
    platforms: <><circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3c3 3 3 15 0 18M12 3c-3 3-3 15 0 18"/></>,
    mappings: <><path d="M4 7h12M13 4l3 3-3 3M20 17H8M11 14l-3 3 3 3"/></>,
    security: <><path d="M12 3 5 6v5c0 5 3 8 7 10 4-2 7-5 7-10V6l-7-3Z"/><path d="m9 12 2 2 4-5"/></>,
    logout: <><path d="M10 5H5v14h5M14 8l4 4-4 4M18 12H9"/></>
  }
  const icon = (name: string) => <svg className="nav-icon" aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">{icons[name]}</svg>
  const item = (to: string, iconName: string, label: string) => <NavLink to={to}>{icon(iconName)}{label}</NavLink>
  return <div className={`app-shell ${sidebarCollapsed ? 'sidebar-collapsed' : ''}`}><aside><div className="sidebar-brand-row"><div className="brand wordmark"><strong>RAVENCIA</strong><small>MERKEZ PANEL</small></div></div><button type="button" className="sidebar-collapse-toggle" onClick={toggleSidebar} aria-label={sidebarCollapsed ? 'Menüyü genişlet' : 'Menüyü daralt'} title={sidebarCollapsed ? 'Menüyü genişlet' : 'Menüyü daralt'}><svg viewBox="0 0 20 20" aria-hidden="true"><path d={sidebarCollapsed ? 'm8 5 5 5-5 5' : 'm12 5-5 5 5 5'} /></svg></button><nav aria-label="Ana menü"><span className="nav-section">Operasyon</span>{item('/dashboard', 'dashboard', 'Dashboard')}{item('/products', 'products', 'Ürünler')}{item('/orders', 'orders', 'Siparişler')}{item('/returns', 'returns', 'İadeler')}{item('/invoices', 'invoices', 'Faturalar')}{item('/jobs', 'jobs', 'İşlem Takibi')}</nav><div className="settings-nav"><button type="button" className="settings-toggle" aria-expanded={settingsOpen} onClick={() => setSettingsOpen(value => !value)}><span>{icon('settings')}Ayarlar</span><b aria-hidden="true">{settingsOpen ? '⌃' : '⌄'}</b></button>{settingsOpen && <div className="settings-links">{item('/integrations', 'platforms', 'Platformlar')}{item('/mappings/categories', 'mappings', 'Eşleştirme Ayarları')}{item('/settings/security', 'security', 'Sistem Ayarları')}</div>}<button type="button" className="logout-link" onClick={() => void logout()}>{icon('logout')}Çıkış Yap</button></div></aside><main><header className="topbar"><div className="breadcrumb"><span>OPERASYON MERKEZİ</span><b>›</b><strong>{current.toLocaleUpperCase('tr-TR')}</strong></div><div className="top-actions"><span className="live-state"><i /> Sistem aktif</span></div></header><Routes><Route path="/dashboard" element={<Dashboard me={me} />} /><Route path="/products" element={<ProductsPage />} /><Route path="/products/new" element={<NewProductPage />} /><Route path="/products/:id" element={<ProductDetailPage />} /><Route path="/catalog/categories" element={<CategoriesPage />} /><Route path="/catalog/brands" element={<BrandsPage />} /><Route path="/catalog/attributes" element={<AttributesPage />} /><Route path="/imports" element={<ImportsPage />} /><Route path="/imports/:id" element={<ImportDetailPage />} /><Route path="/inventory" element={<InventoryPage />} /><Route path="/integrations" element={<IntegrationsPage />} /><Route path="/integrations/:id" element={<IntegrationDetailPage />} /><Route path="/mappings/categories" element={<MappingPage kind="categories" />} /><Route path="/mappings/attributes" element={<MappingPage kind="attributes" />} /><Route path="/orders" element={<OrdersPage />} /><Route path="/orders/:id" element={<Navigate to="/orders" replace />} /><Route path="/shipments" element={<ShipmentsPage />} /><Route path="/shipments/:id" element={<ShipmentDetailPage />} /><Route path="/returns" element={<ReturnsPage />} /><Route path="/returns/:id" element={<ReturnDetailPage />} /><Route path="/invoices" element={<InvoicesPage />} /><Route path="/invoices/:id" element={<Navigate to="/orders" replace />} /><Route path="/jobs" element={<JobsPage me={me} />} /><Route path="/settings/billing" element={<Navigate to="/settings/security" replace />} /><Route path="/settings/security" element={<Security />} /><Route path="*" element={<Navigate to="/dashboard" replace />} /></Routes></main></div>
}

export function App() {
  const me = useQuery({ queryKey: ['me'], queryFn: () => api<Me>('/me'), retry: false })
  if (me.isLoading) return <Status title="Güvenli oturum doğrulanıyor" />
  if (me.isError) return <Routes><Route path="*" element={<Login />} /></Routes>
  if (!me.data) return <Status title="Oturum bilgisi alınamadı" />
  if (me.data.state === 'PASSWORD_CHANGE_REQUIRED') return <ChangePassword />
  if (me.data.state === 'MFA_CHALLENGE') return <MfaChallenge />
  if (me.data.state !== 'ACTIVE') return <Status title="Oturum kilitli" detail="Yeniden giriş yapın." />
  return <Shell me={me.data} />
}

function Login() {
  const navigate = useNavigate(); const client = useQueryClient(); const [error, setError] = useState(''); const [loading, setLoading] = useState(false)
  async function submit(event: FormEvent<HTMLFormElement>) { event.preventDefault(); setError(''); setLoading(true); const data = new FormData(event.currentTarget); try { await api('/login', { method: 'POST', body: JSON.stringify({ email: data.get('email'), password: data.get('password') }) }); await client.invalidateQueries({ queryKey: ['me'] }); navigate('/dashboard') } catch (reason) { setError(reason instanceof Error ? reason.message : 'Giriş başarısız.') } finally { setLoading(false) } }
  return (
    <div className="relative min-h-screen bg-[#060711] text-white flex flex-col font-sans overflow-hidden">
      <style>{`
        @import url('https://fonts.googleapis.com/css2?family=Alex+Brush&family=Cinzel:wght@400;500;600&display=swap');
        .login-input { background: #080911 !important; border: 1px solid rgba(255, 255, 255, 0.08) !important; color: white !important; border-radius: 8px !important; padding: 0.85rem 1rem !important; font-size: 0.85rem !important; transition: all 0.2s; }
        .login-input:focus { border-color: rgba(99, 102, 241, 0.5) !important; outline: none !important; box-shadow: 0 0 0 1px rgba(99, 102, 241, 0.2) !important; }
        .login-input::placeholder { color: #64748b !important; }
        .login-button { background: linear-gradient(90deg, #2b2e4d 0%, #1e2039 100%) !important; border: 1px solid rgba(255, 255, 255, 0.05) !important; color: #e2e8f0 !important; border-radius: 8px !important; padding: 0.9rem !important; font-size: 0.85rem !important; font-weight: 500 !important; transition: all 0.2s; }
        .login-button:hover { background: linear-gradient(90deg, #333659 0%, #242644 100%) !important; }
        .login-checkbox { appearance: none !important; width: 14px !important; height: 14px !important; border: 1px solid rgba(255, 255, 255, 0.2) !important; background: transparent !important; border-radius: 3px !important; padding: 0 !important; margin: 0 !important; display: grid; place-content: center; cursor: pointer; transition: all 0.2s; }
        .login-checkbox:checked { border-color: #818cf8 !important; }
        .login-checkbox:checked::before { content: ""; width: 8px; height: 8px; background: #818cf8; border-radius: 1px; }
      `}</style>
      
      {/* Background Effects & Diagonal Rays */}
      <div className="absolute inset-0 pointer-events-none overflow-hidden">
         <div className="absolute top-[-50%] left-[-50%] w-[200%] h-[200%]">
            <div className="absolute top-1/2 left-[45%] w-[120%] h-[1px] bg-gradient-to-r from-transparent via-blue-300/10 to-transparent -translate-x-1/2 -translate-y-1/2 rotate-[-35deg] shadow-[0_0_15px_rgba(96,165,250,0.3)]"></div>
            <div className="absolute top-[60%] left-1/2 w-[120%] h-[1px] bg-gradient-to-r from-transparent via-indigo-300/10 to-transparent -translate-x-1/2 -translate-y-1/2 rotate-[35deg] shadow-[0_0_15px_rgba(129,140,248,0.3)]"></div>
         </div>
         {/* Huge subtle glowing ring on the left */}
         <div className="absolute top-1/2 left-[20%] -translate-x-1/2 -translate-y-1/2 w-[700px] h-[700px] rounded-full border border-blue-400/[0.04] shadow-[0_0_120px_rgba(30,58,138,0.15)] bg-gradient-to-tr from-blue-900/5 to-transparent mix-blend-screen"></div>
      </div>

      {/* Güvenli Erişim Box */}
      <div className="absolute bottom-16 left-12 max-w-[320px] p-4 rounded-xl border border-white/[0.04] bg-[#0c0d14]/40 backdrop-blur-md flex items-start gap-4 shadow-2xl z-20 hidden xl:flex">
        <div className="p-2.5 rounded-lg border border-white/[0.08] text-slate-400 opacity-80 shrink-0">
           <svg width="22" height="22" fill="none" stroke="currentColor" strokeWidth="1.5" viewBox="0 0 24 24"><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/><path d="M8 11h8v6H8z"/><path d="M10 11V9a2 2 0 114 0v2"/></svg>
        </div>
        <div>
          <div className="text-[#818cf8] text-[0.65rem] font-bold tracking-widest uppercase mb-1">Güvenli Erişim</div>
          <div className="text-slate-400 text-[0.7rem] leading-relaxed opacity-80">Tüm bağlantılar uçtan uca şifrelenmiştir.<br/>Verileriniz güvende.</div>
        </div>
      </div>

      <div className="flex-1 flex flex-col lg:flex-row items-center justify-center relative z-10 px-6 w-full max-w-[1300px] mx-auto gap-12 lg:gap-32">
        
        {/* Left Side: Logo */}
        <div className="lg:w-1/2 flex flex-col items-center justify-center">
          <div className="relative flex flex-col items-center justify-center mb-8">
             <div className="text-[170px] leading-[0.8] relative z-10 pr-4 drop-shadow-[0_10px_20px_rgba(0,0,0,0.5)]" 
                  style={{ fontFamily: "'Alex Brush', cursive", background: "linear-gradient(180deg, #ffffff 0%, #94a3b8 100%)", WebkitBackgroundClip: "text", WebkitTextFillColor: "transparent" }}>
               R
             </div>
             <div className="text-[1.25rem] tracking-[0.45em] font-light mt-4 text-slate-300/90 relative z-10 ml-3 uppercase" style={{ fontFamily: "Inter, sans-serif" }}>
               Ravencia
             </div>
          </div>
        </div>

        {/* Right Side: Login Card */}
        <div className="lg:w-1/2 flex items-center justify-center w-full">
          <div className="w-full max-w-[460px] p-10 bg-[#0a0b12]/60 backdrop-blur-2xl border border-white/[0.06] rounded-[18px] shadow-[0_20px_50px_rgba(0,0,0,0.5)] relative">
             <div className="absolute top-0 left-1/2 -translate-x-1/2 w-48 h-[1px] bg-gradient-to-r from-transparent via-blue-400/40 to-transparent shadow-[0_0_15px_rgba(96,165,250,0.4)]" />
             <div className="absolute bottom-0 left-1/2 -translate-x-1/2 w-48 h-[1px] bg-gradient-to-r from-transparent via-indigo-400/40 to-transparent shadow-[0_0_15px_rgba(129,140,248,0.4)]" />
             
             <div className="text-center mb-10 mt-2">
                <h1 className="text-[1.7rem] text-slate-200 tracking-wide" style={{ fontFamily: "'Cinzel', serif", fontWeight: 500 }}>Ravencia Yönetim Paneli</h1>
                <div className="mt-4 flex items-center justify-center opacity-40">
                   <div className="h-px bg-gradient-to-r from-transparent to-slate-400 w-16"></div>
                   <svg className="w-2.5 h-2.5 mx-3 text-slate-300" viewBox="0 0 24 24" fill="currentColor"><path d="M12 0l2 10 10 2-10 2-2 10-2-10-10-2 10-2z"/></svg>
                   <div className="h-px bg-gradient-to-l from-transparent to-slate-400 w-16"></div>
                </div>
             </div>
             
             <h2 className="text-slate-300 text-[0.8rem] mb-4 pl-1">Oturum aç</h2>
             
             <form onSubmit={submit} className="space-y-4">
                <div className="relative">
                   <input name="email" type="email" autoComplete="username" required placeholder="E-posta" className="login-input w-full" />
                   <div className="absolute right-4 top-1/2 -translate-y-1/2 text-slate-500">
                      <svg width="15" height="15" fill="none" stroke="currentColor" strokeWidth="1.5" viewBox="0 0 24 24"><path d="M3 8l7.89 5.26a2 2 0 002.22 0L21 8M5 19h14a2 2 0 002-2V7a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z"/></svg>
                   </div>
                </div>
                
                <div className="relative">
                   <input name="password" type="password" autoComplete="current-password" required minLength={15} maxLength={64} placeholder="Parola" className="login-input w-full" />
                   <div className="absolute right-4 top-1/2 -translate-y-1/2 flex items-center gap-3 text-slate-500">
                      <svg width="13" height="13" fill="none" stroke="currentColor" strokeWidth="1.5" viewBox="0 0 24 24"><path d="M12 15v2m-6 4h12a2 2 0 002-2v-6a2 2 0 00-2-2H6a2 2 0 00-2 2v6a2 2 0 002 2zm10-10V7a4 4 0 00-8 0v4h8z"/></svg>
                      <svg width="15" height="15" fill="none" stroke="currentColor" strokeWidth="1.5" viewBox="0 0 24 24"><path d="M15 12a3 3 0 11-6 0 3 3 0 016 0z"/><path d="M2.458 12C3.732 7.943 7.523 5 12 5c4.478 0 8.268 2.943 9.542 7-1.274 4.057-5.064 7-9.542 7-4.477 0-8.268-2.943-9.542-7z"/></svg>
                   </div>
                </div>
                
                <div className="flex items-center justify-between text-[0.75rem] py-1 px-1">
                   <label className="flex items-center gap-2 text-slate-300 cursor-pointer">
                      <input type="checkbox" className="login-checkbox" />
                      <span>Beni hatırla</span>
                   </label>
                   <a href="#" className="text-[#818cf8] hover:text-[#a5b4fc] transition-colors">Şifremi unuttum?</a>
                </div>
                
                {error && (
                   <div className="p-2.5 rounded-lg bg-red-900/20 border border-red-500/20 text-red-400 text-xs flex items-center gap-2">
                     <svg className="w-4 h-4 shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" /></svg>
                     <span>{error}</span>
                   </div>
                )}
                
                <button type="submit" disabled={loading} className="login-button w-full mt-3 flex items-center justify-center gap-2 disabled:opacity-70 group">
                   {loading ? 'Giriş yapılıyor...' : 'Güvenli giriş'}
                   {!loading && <svg className="w-3.5 h-3.5 text-slate-400 group-hover:translate-x-1 transition-transform" fill="none" stroke="currentColor" strokeWidth="1.5" viewBox="0 0 24 24"><path d="M4 12h16m-7-7l7 7-7 7"/></svg>}
                </button>
             </form>

             {/* System Info Grid */}
             <div className="mt-8 pt-5 border-t border-white/[0.04] grid grid-cols-3 gap-2 text-center opacity-80">
               <div className="flex flex-col items-center justify-center">
                 <div className="text-slate-500 text-[0.6rem] mb-1.5">Sistem Ortamı</div>
                 <div className="text-slate-300 text-[0.7rem] flex items-center gap-1.5"><div className="w-1.5 h-1.5 rounded-full bg-emerald-500"></div> PROD</div>
               </div>
               <div className="flex flex-col items-center justify-center border-l border-white/[0.04]">
                 <div className="text-slate-500 text-[0.6rem] mb-1.5">Sürüm</div>
                 <div className="text-slate-300 text-[0.7rem]">v2.6.0</div>
               </div>
               <div className="flex flex-col items-center justify-center border-l border-white/[0.04]">
                 <div className="text-slate-500 text-[0.6rem] mb-1.5">Son Güncelleme</div>
                 <div className="text-slate-300 text-[0.7rem]">25.05.2025 10:42</div>
               </div>
             </div>
          </div>
        </div>
      </div>
      
      {/* Footer */}
      <div className="relative z-10 w-full px-10 py-5 border-t border-white/[0.03] bg-transparent flex flex-col sm:flex-row items-center justify-between text-[0.7rem] text-slate-500 mt-auto opacity-70">
         <div>© {new Date().getFullYear()} Ravencia. Tüm hakları saklıdır.</div>
         <div className="flex items-center gap-1.5 mt-2 sm:mt-0">
            <svg width="12" height="12" fill="none" stroke="currentColor" strokeWidth="1.5" viewBox="0 0 24 24"><path d="M9 12l2 2 4-4m5.618-4.016A11.955 11.955 0 0112 2.944a11.955 11.955 0 01-8.618 3.04A12.02 12.02 0 003 9c0 5.591 3.824 10.29 9 11.622 5.176-1.332 9-6.03 9-11.622 0-1.042-.133-2.052-.382-3.016z"/></svg>
            Ravencia Yönetim Paneli v2.6.0
         </div>
      </div>
    </div>
  )
}

function ChangePassword() { const client = useQueryClient(); const [message, setMessage] = useState('İlk girişte parolanızı değiştirmeniz gerekir.'); async function submit(event: FormEvent<HTMLFormElement>) { event.preventDefault(); const data = new FormData(event.currentTarget); try { await api('/change-password', { method: 'POST', body: JSON.stringify({ currentPassword: data.get('current'), newPassword: data.get('next') }) }); await client.invalidateQueries({ queryKey: ['me'] }) } catch { setMessage('Parola değiştirilemedi; politika ve mevcut parolayı kontrol edin.') } } return <div className="auth-page"><section className="auth-card"><h1>Parolanızı değiştirin</h1><p role="status">{message}</p><form onSubmit={submit}><label>Geçerli parola<input name="current" type="password" required /></label><label>Yeni parola<input name="next" type="password" minLength={15} maxLength={64} required /></label><button>Parolayı değiştir</button></form></section></div> }
function MfaChallenge() { const client = useQueryClient(); const [error, setError] = useState(''); async function submit(event: FormEvent<HTMLFormElement>) { event.preventDefault(); const data = new FormData(event.currentTarget); try { await api('/mfa/challenge', { method: 'POST', body: JSON.stringify({ code: data.get('code'), recoveryCode: data.get('recovery') || null }) }); await client.invalidateQueries({ queryKey: ['me'] }) } catch { setError('Kod geçersiz veya daha önce kullanılmış.') } } return <div className="auth-page"><section className="auth-card"><h1>İki adımlı doğrulama</h1><p>Authenticator uygulamanızdaki 6 haneli kodu girin.</p><form onSubmit={submit}><label>Doğrulama kodu<input name="code" inputMode="numeric" pattern="[0-9]{6}" /></label><label>Kurtarma kodu (alternatif)<input name="recovery" /></label>{error && <div role="alert" className="error">{error}</div>}<button>Doğrula</button></form></section></div> }
type DashboardPage<T> = { items: T[]; hasMore: boolean }
type DashboardOrder = { orderNumber: string; derivedStatus: string; orderedAt: string; platformDisplayName: string; shipmentDueAt: string | null; isDeadlineCritical: boolean; cargoProviderName: string | null; productQuantity: number }
type DashboardReturn = { status: string }
type DashboardInvoice = { invoiceId: string | null; isDueSoon: boolean; canCreateInvoice: boolean }
type DashboardProduct = { id: string; title: string; totalStock: number; primaryImageUrl: string | null; activePlatforms: string[] | null }
function Dashboard({ me }: { me: Me }) {
  const connections = useQuery({ queryKey: ['dashboard-connections'], queryFn: () => hubApi<DashboardPage<{ status: string }>>('/connections?limit=200') })
  const orders = useQuery({ queryKey: ['dashboard-orders'], queryFn: () => hubApi<DashboardPage<DashboardOrder>>('/orders?limit=200') })
  const returns = useQuery({ queryKey: ['dashboard-returns'], queryFn: () => hubApi<DashboardPage<DashboardReturn>>('/returns?limit=200') })
  const invoices = useQuery({ queryKey: ['dashboard-invoice-workspace'], queryFn: () => hubApi<DashboardPage<DashboardInvoice>>('/invoice-workspace?limit=200') })
  const products = useQuery({ queryKey: ['dashboard-products'], queryFn: () => hubApi<DashboardPage<DashboardProduct>>('/products?limit=200') })
  const loading = [connections, orders, returns, invoices, products].some(query => query.isLoading)
  const now = new Date(); const orderItems = orders.data?.items ?? []; const productItems = products.data?.items ?? []
  const terminal = new Set(['DELIVERED', 'CANCELLED', 'CANCELED', 'RETURNED'])
  const pending = orderItems.filter(item => !terminal.has(item.derivedStatus.toUpperCase()))
  const late = pending.filter(item => item.shipmentDueAt && new Date(item.shipmentDueAt) < now)
  const today = orderItems.filter(item => { const value = new Date(item.orderedAt); return value.getFullYear() === now.getFullYear() && value.getMonth() === now.getMonth() && value.getDate() === now.getDate() })
  const month = orderItems.filter(item => { const value = new Date(item.orderedAt); return value.getFullYear() === now.getFullYear() && value.getMonth() === now.getMonth() })
  const pendingReturns = (returns.data?.items ?? []).filter(item => ['REQUESTED', 'AWAITING_SHIPMENT', 'IN_TRANSIT', 'ACTION_REQUIRED', 'DISPUTED'].includes(item.status)).length
  const uninvoiced = (invoices.data?.items ?? []).filter(item => !item.invoiceId && item.canCreateInvoice).length
  const dueSoon = (invoices.data?.items ?? []).filter(item => !item.invoiceId && item.isDueSoon).length
  const lowStock = productItems.filter(item => item.totalStock <= 5)
  const verified = (connections.data?.items ?? []).filter(item => item.status === 'VERIFIED' || item.status === 'ACTIVE').length
  const group = (values: string[]) => Object.entries(values.reduce<Record<string, number>>((acc, value) => { const label = value || 'Belirtilmemiş'; acc[label] = (acc[label] ?? 0) + 1; return acc }, {})).sort((a, b) => b[1] - a[1])
  const byPlatform = group(pending.map(item => item.platformDisplayName || 'Trendyol'))
  const byCargo = group(orderItems.map(item => item.cargoProviderName || 'Kargo bekleniyor'))
  const errors = [connections.error, orders.error, returns.error, invoices.error, products.error].filter(Boolean)
  return <section className="content dashboard"><div className="page-heading"><div><p className="eyebrow">Operasyon merkezi</p><h1>Genel Bakış</h1><p className="lede">Merhaba {me.displayName}. Günlük operasyonun önemli sinyalleri tek ekranda.</p></div><div className="dashboard-heading-actions"><span className="dashboard-date">{now.toLocaleDateString('tr-TR', { day: 'numeric', month: 'long', year: 'numeric' })}</span><Link className="button-link" to="/orders">Siparişleri aç</Link></div></div>
    {errors.length > 0 && <div role="alert" className="error">Bazı operasyon verileri alınamadı; görünen metrikler kısmi olabilir.</div>}
    <nav className="dashboard-toolbar" aria-label="Hızlı işlemler"><Link to="/orders">Siparişler</Link><Link to="/returns">İadeler</Link><Link to="/invoices">Faturalar</Link><Link to="/products">Ürünler</Link><Link to="/jobs">İşlem takibi</Link></nav>
    <div className="metrics dashboard-metrics operational-metrics"><article><small>BEKLEYEN SİPARİŞ</small><strong>{loading ? '—' : pending.length}</strong><p>{byPlatform.map(([name, count]) => `${name}: ${count}`).join(' · ') || 'Bekleyen yok'}</p></article><article className={late.length ? 'danger-metric' : ''}><small>GECİKEN SİPARİŞ</small><strong>{loading ? '—' : late.length}</strong><p>Termin zamanı aşılmış</p></article><article><small>BUGÜNKÜ SİPARİŞ</small><strong>{loading ? '—' : today.length}</strong><p>{today.reduce((sum, item) => sum + item.productQuantity, 0)} ürün</p></article><article><small>BU AY</small><strong>{loading ? '—' : month.length}</strong><p>Ay içindeki siparişler</p></article><article><small>BEKLEYEN İADE</small><strong>{loading ? '—' : pendingReturns}</strong><p>İşlem veya taşıma aşaması</p></article><article className={dueSoon ? 'warning-metric' : ''}><small>SÜRESİ YAKLAŞAN FATURA</small><strong>{loading ? '—' : dueSoon}</strong><p>5. gün ve sonrası</p></article><article><small>FATURALANDIRILMAMIŞ</small><strong>{loading ? '—' : uninvoiced}</strong><p>Tek fatura korumalı</p></article><article><small>DÜŞÜK / YOK STOK</small><strong>{loading ? '—' : lowStock.length}</strong><p>5 adet ve altı</p></article></div>
    <div className="dashboard-report-grid"><article className="panel"><div className="panel-title"><div><h2>Kargo bazlı operasyon</h2><p>Yüklenen son 200 siparişin dağılımı</p></div><Link to="/shipments">Gönderiler →</Link></div><div className="report-bars">{byCargo.slice(0, 8).map(([name, count]) => <div key={name}><span>{name}</span><b style={{ width: `${Math.max(8, count / Math.max(1, orderItems.length) * 100)}%` }}>{count}</b></div>)}{!byCargo.length && <p>Henüz kargo verisi yok.</p>}</div></article><article className="panel"><div className="panel-title"><div><h2>Ürün bazlı stok riski</h2><p>En düşük stoklu ürünler</p></div><Link to="/products">Ürünler →</Link></div><div className="dashboard-product-list">{lowStock.sort((a, b) => a.totalStock - b.totalStock).slice(0, 8).map(item => <Link to={`/products/${item.id}`} key={item.id}>{item.primaryImageUrl ? <img src={item.primaryImageUrl} alt="" /> : <span>▧</span>}<strong>{item.title}</strong><b>{item.totalStock}</b></Link>)}{!lowStock.length && <p>Düşük stoklu ürün yok.</p>}</div></article></div>
    <div className="dashboard-panels"><article className="panel operation-card"><div className="panel-title"><div><h2>Sipariş süreci</h2><p>Operasyon kayıtlarının anlık durum özeti</p></div><span className="live-state"><i /> CANLI</span></div><div className="flow-grid"><Link to="/orders"><span>{pending.length}</span><strong>Bekleyen</strong><small>İşlem sırasındaki siparişler</small></Link><Link to="/orders"><span>{late.length}</span><strong>Geciken</strong><small>Termin aşımı</small></Link><Link to="/invoices"><span>{uninvoiced}</span><strong>Fatura bekliyor</strong><small>Kesilebilir kayıtlar</small></Link><Link to="/returns"><span>{pendingReturns}</span><strong>İade</strong><small>Aksiyon veya taşıma</small></Link><Link to="/products"><span>{lowStock.length}</span><strong>Stok riski</strong><small>5 ve altı</small></Link><Link to="/integrations"><span>{verified}</span><strong>Bağlantı</strong><small>Doğrulanmış veya aktif</small></Link></div></article></div>
  </section>
}
type SecurityStatus = { totpState: string; recoveryCodesRemaining: number }
type SecuritySession = { id: string; state: string; current: boolean; issuedAt: string; lastSeenAt: string; expiresAt: string }
type MfaSetup = { otpauthUri: string; qrSvg: string; expiresAt: string }

function Security() {
  const client = useQueryClient()
  const status = useQuery({ queryKey: ['security'], queryFn: () => api<SecurityStatus>('/security-status') })
  const sessions = useQuery({ queryKey: ['sessions'], queryFn: () => api<SecuritySession[]>('/sessions') })
  const [mfaStep, setMfaStep] = useState<'closed' | 'password' | 'verify' | 'recovery'>('closed')
  const [setup, setSetup] = useState<MfaSetup | null>(null); const [recoveryCodes, setRecoveryCodes] = useState<string[]>([])
  const [busy, setBusy] = useState(false); const [message, setMessage] = useState('')
  const activeOtherSessions = (sessions.data ?? []).filter(session => !session.current && session.state === 'ACTIVE')
  const closedSessions = (sessions.data ?? []).filter(session => !session.current && session.state !== 'ACTIVE')

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
  async function deleteClosedSessions() {
    if (!window.confirm('Tüm kapalı oturum kayıtları silinsin mi?')) return
    setMessage('')
    try { await api('/sessions/closed', { method: 'DELETE' }); await client.invalidateQueries({ queryKey: ['sessions'] }); setMessage('Kapalı oturumlar silindi.') } catch { setMessage('Kapalı oturumlar silinemedi.') }
  }

  return <section className="content security-page"><div className="page-heading"><div><p className="eyebrow">Ayarlar</p><h1>Güvenlik ve oturumlar</h1><p className="lede">Hesabınızın ikinci doğrulama adımını ve açık oturumlarını yönetin.</p></div></div>{message && <div className="notice" role="status">{message}</div>}{status.isLoading ? <Status title="Güvenlik durumu yükleniyor" /> : status.isError || !status.data ? <div role="alert" className="error">Güvenlik durumu alınamadı.</div> : <div className="panel security-authenticator-card"><div><span className={`security-state ${status.data.totpState === 'ENABLED' ? 'enabled' : ''}`}>{status.data.totpState === 'ENABLED' ? 'Etkin' : 'Kapalı'}</span><h2>Authenticator</h2><p>Giriş sırasında telefonunuzdaki tek kullanımlık kodla hesabınızı koruyun.</p><small>Kalan kurtarma kodu: <strong>{status.data.recoveryCodesRemaining}</strong></small></div>{status.data.totpState === 'ENABLED' ? <span className="security-check" aria-label="Authenticator etkin">✓</span> : <button type="button" onClick={() => { setMessage(''); setMfaStep('password') }}>Authenticator’ı etkinleştir</button>}</div>}
    <div className="panel security-sessions-card"><div className="panel-title"><div><h2>Oturumlar</h2><p>Hesabınıza bağlı cihazları ve son etkinliklerini görüntüleyin.</p></div><div className="session-bulk-actions">{activeOtherSessions.length > 0 && <button type="button" className="secondary danger-outline" onClick={() => void revokeOthers()}>Diğer tüm oturumları kapat</button>}{closedSessions.length > 0 && <button type="button" className="secondary danger-outline" onClick={() => void deleteClosedSessions()}>Kapalı oturumları sil</button>}</div></div>{sessions.isLoading ? <p>Yükleniyor…</p> : sessions.isError || !sessions.data ? <div role="alert" className="error">Oturumlar alınamadı.</div> : <ul className="sessions">{sessions.data.map(session => <li key={session.id} className={session.current ? 'current' : ''}><span className="session-device-icon" aria-hidden="true">{session.current ? '●' : '○'}</span><span><strong>{session.current ? 'Bu cihaz' : 'Diğer oturum'}</strong><small>{session.state === 'ACTIVE' ? 'Aktif' : 'Sonlandırıldı'} · Son etkinlik {new Date(session.lastSeenAt).toLocaleString('tr-TR')}</small><small>Bitiş {new Date(session.expiresAt).toLocaleString('tr-TR')}</small></span>{session.current ? <b>Mevcut oturum</b> : session.state === 'ACTIVE' ? <button type="button" className="secondary danger-outline" onClick={() => void revokeSession(session.id)}>Oturumu sonlandır</button> : <button type="button" className="secondary danger-outline" onClick={() => void deleteSession(session.id)}>Kaydı sil</button>}</li>)}</ul>}</div>
    {mfaStep !== 'closed' && <div className="workspace-modal-backdrop" role="presentation"><section className="workspace-modal security-modal" role="dialog" aria-modal="true" aria-labelledby="mfa-title"><header><div><h2 id="mfa-title">Authenticator kurulumu</h2><p>{mfaStep === 'password' ? 'Önce hesabın size ait olduğunu doğrulayın.' : mfaStep === 'verify' ? 'QR kodu uygulamanıza ekleyip üretilen kodu girin.' : 'Kurtarma kodlarını şimdi güvenli bir yerde saklayın.'}</p></div><button className="modal-close" type="button" aria-label="Kapat" onClick={() => setMfaStep('closed')}>×</button></header>{mfaStep === 'password' && <form className="security-modal-body" onSubmit={prepareMfa}><label>Mevcut parola<input name="password" type="password" autoComplete="current-password" required /></label><button disabled={busy}>{busy ? 'Doğrulanıyor…' : 'Devam et'}</button></form>}{mfaStep === 'verify' && setup && <form className="security-modal-body mfa-verify" onSubmit={confirmMfa}><img src={`data:image/svg+xml;utf8,${encodeURIComponent(setup.qrSvg)}`} alt="Authenticator QR kodu" /><div><p>QR kodu Google Authenticator, Microsoft Authenticator veya uyumlu uygulamanızla tarayın.</p><details><summary>Kurulum anahtarını elle göster</summary><code>{setup.otpauthUri}</code></details><label>6 haneli doğrulama kodu<input name="code" inputMode="numeric" pattern="[0-9]{6}" autoComplete="one-time-code" required /></label><button disabled={busy}>{busy ? 'Kontrol ediliyor…' : 'Etkinleştir'}</button></div></form>}{mfaStep === 'recovery' && <div className="security-modal-body"><div className="recovery-code-grid">{recoveryCodes.map(code => <code key={code}>{code}</code>)}</div><p>Bu kodlar yalnızca bir kez gösterilir. Her kod tek kullanımlıktır.</p><button type="button" onClick={() => setMfaStep('closed')}>Kodları sakladım</button></div>}{message && <div className="error security-modal-error" role="alert">{message}</div>}</section></div>}
  </section>
}
function Status({ title, detail }: { title: string; detail?: string }) { return <div className="status" role="status"><div className="spinner" /><strong>{title}</strong>{detail && <p>{detail}</p>}</div> }
