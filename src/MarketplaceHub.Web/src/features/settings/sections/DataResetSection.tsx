import { StickyActionBar } from '../../../shared/components'
import type { DataResetGroup } from '../data-reset-groups'
import { marketplaceResetTotal, progressText, type BulkResetProgress } from '../marketplace-bulk-reset'
import { PlatformSquareMark } from '../../../shared/platform-square-mark'
import { UiIcon } from '../../../shared/components/UiIcon'
import { PanelFactoryResetSection } from '../PanelFactoryResetSection'
import { dataResetConfirmWord, panelResetConfirmed, type PanelFactoryResetView } from '../panel-factory-reset'
import { ReauthInputs, reauthReady } from '../../../shared/reauth'
import { type SettingsIconName, SettingsIcon, SettingsPaneHead } from '../settings-ui'

// The categories of Ayarlar › Veri come from the server (PanelFactoryResetInventory): together they are Paneli Tamamen
// Sıfırla, so nothing of the tenant's records is left out of them.
export function resetGroupsOf(view: PanelFactoryResetView | undefined): DataResetGroup[] {
  if (!view) return []
  const sections = Array.from(new Set(view.groups.map(group => group.section)))
  return sections.map(section => ({ label: section, description: '', options: view.groups.filter(group => group.section === section).map(group => ({ scope: group.key, label: group.label, description: group.description, unit: 'kayıt' })) }))
}
const resetNumber = (value: number) => value.toLocaleString('tr-TR')
const resetGroupIcons: Record<string, SettingsIconName> = { 'Satış ve Faturalar': 'barcode', Katalog: 'db', 'Pazaryeri Kayıtları': 'save', 'Sistem Kayıtları': 'bell' }

// Database cleanup: each group has one header with its own select-all and a selected/total count; each data type is
// a whole-row tile (checkbox beside the title, count as a tag); the notes sit in one callout and the sticky bar shows
// what goes as chips, the total, the typed confirmation and the reset button.
export type ResetMarketplace = { id: string; platformCode: string; displayName: string; passive: boolean; count: number | undefined }
export function SettingsDataReset({ forbidden, groups, counts, countsFailed, picked, onToggle, marketplaces, marketplacePicked, onToggleMarketplaces, progress, confirmation, onConfirmation, password, onPassword, factor, onFactor, mfa, busy, onConfirm }: { forbidden: boolean; groups: DataResetGroup[]; counts?: Record<string, number>; countsFailed: boolean; picked: string[]; onToggle: (scopes: string[], checked: boolean) => void; marketplaces: ResetMarketplace[]; marketplacePicked: string[]; onToggleMarketplaces: (ids: string[], checked: boolean) => void; progress: Record<string, BulkResetProgress>; confirmation: string; onConfirmation: (value: string) => void; password: string; onPassword: (value: string) => void; factor: string; onFactor: (value: string) => void; mfa: boolean; busy: boolean; onConfirm: () => void }) {
  const options = groups.flatMap(group => group.options)
  const going = options.filter(option => picked.includes(option.scope))
  const goingMarketplaces = marketplaces.filter(entry => marketplacePicked.includes(entry.id))
  const marketplaceTotal = marketplaceResetTotal(goingMarketplaces.map(entry => entry.id), Object.fromEntries(marketplaces.map(entry => [entry.id, entry.count])))
  const known = going.every(option => counts?.[option.scope] !== undefined) && marketplaceTotal !== undefined
  const total = going.reduce((sum, option) => sum + (counts?.[option.scope] ?? 0), 0) + (marketplaceTotal ?? 0)
  const goingCount = going.length + goingMarketplaces.length
  // Every data type chosen is Paneli Tamamen Sıfırla: the page says so before the confirmation and asks for its word.
  const fullReset = options.length > 0 && going.length === options.length
  const resetConfirmWord = dataResetConfirmWord(options.length, going.length)
  const confirmed = panelResetConfirmed(confirmation, resetConfirmWord)
  const authenticated = reauthReady(password, factor, mfa)
  const countText = (scope: string, unit: string) => {
    const value = counts?.[scope]
    if (value !== undefined) return `${resetNumber(value)} ${unit}`
    return countsFailed ? 'Sayı alınamadı' : counts ? null : 'Sayılıyor…'
  }
  // The counts, the marketplaces and the reset are the owner's and administrators' (the server answers others 403).
  if (forbidden) return <>
    <SettingsPaneHead section="database" description="Yalnız seçtiğiniz alanlar bu hesabın yerel veritabanından silinir." />
    <div className="ss-body"><div className="ss-callout" role="alert"><SettingsIcon name="lock" /><span>Veritabanı temizliğini yalnız hesap sahibi ve yöneticiler görür ve yapar.</span></div></div>
  </>
  return <>
    <SettingsPaneHead section="database" description="Yalnız seçtiğiniz alanlar bu hesabın yerel veritabanından silinir." />
    <div className="ss-body ss-dr" role="group" aria-label="Sıfırlanacak veriler">
      {groups.map(group => {
        const chosen = group.options.filter(option => picked.includes(option.scope)).length
        const all = chosen === group.options.length
        return <section className="ss-dr-group" key={group.label} aria-label={group.label}>
          <header className="ss-dr-gh">
            <span className="ss-tile is-sm ss-dr-ico"><SettingsIcon name={resetGroupIcons[group.label] ?? 'db'} /></span>
            <div><h3>{group.label}</h3></div>
            <span className={`ss-tag ss-dr-sel${chosen ? ' is-on' : ''}`}>{chosen}/{group.options.length} Seçili</span>
            <button type="button" className="ss-btn is-xs" disabled={busy} onClick={() => onToggle(group.options.map(option => option.scope), !all)}>{all ? 'Seçimi Kaldır' : 'Tümünü Seç'}</button>
          </header>
          <div className="ss-dr-list">{group.options.map(option => {
            const on = picked.includes(option.scope)
            const count = countText(option.scope, option.unit)
            return <label key={option.scope} className={`ss-dr-opt${on ? ' is-on' : ''}`}>
              <input type="checkbox" className="ss-cb" checked={on} disabled={busy} onChange={event => onToggle([option.scope], event.target.checked)} />
              <span className="ss-dr-text"><b>{option.label}</b><small>{option.description}</small></span>
              {count ? <span className="ss-tag ss-dr-count">{count}</span> : null}
            </label>
          })}</div>
        </section>
      })}
      {marketplaces.length > 0 && <section className="ss-dr-group" aria-label="Pazaryerleri">
        <header className="ss-dr-gh">
          <span className="ss-tile is-sm ss-dr-ico"><SettingsIcon name="truck" /></span>
          <div><h3>Pazaryerleri</h3><p>Tam Sıfırlama: bağlantının tüm kayıtları silinir, bağlantı ayarları korunur.</p></div>
          <span className={`ss-tag ss-dr-sel${goingMarketplaces.length ? ' is-on' : ''}`}>{goingMarketplaces.length}/{marketplaces.length} Seçili</span>
          <button type="button" className="ss-btn is-xs" disabled={busy} onClick={() => onToggleMarketplaces(marketplaces.map(entry => entry.id), goingMarketplaces.length !== marketplaces.length)}>{goingMarketplaces.length === marketplaces.length ? 'Seçimi Kaldır' : 'Tümünü Seç'}</button>
        </header>
        <div className="ss-dr-list">{marketplaces.map(entry => {
          const on = marketplacePicked.includes(entry.id)
          const state = progress[entry.id]
          const text = progressText(state)
          return <label key={entry.id} className={`ss-dr-opt is-mk${on ? ' is-on' : ''}`}>
            <input type="checkbox" className="ss-cb" checked={on} disabled={busy} onChange={event => onToggleMarketplaces([entry.id], event.target.checked)} />
            <PlatformSquareMark code={entry.platformCode} name={entry.displayName} />
            <span className="ss-dr-text"><b>{entry.displayName}</b><small>Sipariş, iade, fatura, ürün bağı, kategori listeleri ve finans kayıtları; ayarlar korunur.{entry.passive ? ' Bağlantı pasif.' : ''}</small>
              {text && <span className={`ss-dr-prog is-${state?.state}`} role="status">{state?.state === 'running' && <i style={{ width: `${state.percent ?? 0}%` }} />}<em>{text}</em></span>}</span>
            {entry.count !== undefined ? <span className="ss-tag ss-dr-count">{resetNumber(entry.count)} kayıt</span> : <span className="ss-tag ss-dr-count">Sayılıyor…</span>}
          </label>
        })}</div>
      </section>}
      <div className="ss-callout ss-dr-note">
        <p className="is-warn"><SettingsIcon name="alert" /><span><b>Geri alınamaz.</b> Yetkili işlemi: bağlı alt kayıtlar güvenli sırayla temizlenir.</span></p>
        <p><SettingsIcon name="info" /><span>Yukarıdaki veri türleri bütün pazaryerleri için birlikte silinir. Yalnız bir pazaryerinin kayıtları için <b>Pazaryerleri</b> bölümünü ya da <b>Entegrasyonlar</b> sayfasında bağlantının <b>Verileri Sıfırla</b> menüsünü kullanın.</span></p>
        {fullReset && <p className="is-warn" role="alert"><SettingsIcon name="alert" /><span><b>Bütün veri türleri seçili: bu, Paneli Tamamen Sıfırla demektir.</b> Bu oturum dışındaki bütün oturumlar kapanır, panelde saklanan bütün dosyalar silinir. Onay için <b>{resetConfirmWord}</b> yazın.</span></p>}
      </div>
      <PanelFactoryResetSection />
    </div>
    <StickyActionBar label="Silinecekler" className="ss-bar ss-dr-bar">
      <div className="ss-dr-sum" role="status">
        <div className="ss-dr-sum-head"><strong>Silinecekler</strong><span className="ss-dr-total">Toplam <b>{goingCount && !known ? '…' : `${resetNumber(total)} kayıt`}</b></span></div>
        {goingCount > 0 && <div className="ss-dr-chips">{goingMarketplaces.map(entry => <span key={entry.id} className="ss-dr-chip">{entry.displayName} (Tam)<b>{entry.count !== undefined ? resetNumber(entry.count) : '…'}</b></span>)}{going.map(option => <span key={option.scope} className="ss-dr-chip">{option.label}{counts?.[option.scope] !== undefined ? <b>{resetNumber(counts[option.scope])}</b> : null}</span>)}</div>}
      </div>
      <div className="ss-bar-actions">
        <label className="ss-dr-ask" htmlFor="settings-reset-confirm">Onaylamak için <b>{resetConfirmWord}</b> yazın{confirmed && <UiIcon name="check" size={14} />}</label>
        <input id="settings-reset-confirm" className={`ss-in ss-dr-in${confirmed ? ' is-match' : ''}`} value={confirmation} onChange={event => onConfirmation(event.target.value)} disabled={busy} autoComplete="off" placeholder={`${resetConfirmWord} yazın`} aria-label={`Onaylamak için ${resetConfirmWord} yazın`} title={`Onaylamak için ${resetConfirmWord} yazın`} />
        <ReauthInputs bare idPrefix="settings-reset" password={password} onPassword={onPassword} factor={factor} onFactor={onFactor} mfa={mfa} disabled={busy} inputClassName="ss-in ss-dr-in ss-dr-auth" />
        <button type="button" className="ss-btn is-danger ss-dr-go" disabled={!goingCount || !confirmed || !authenticated || busy} onClick={onConfirm}><SettingsIcon name="trash" />{busy ? 'Sıfırlanıyor…' : goingMarketplaces.length ? `${goingCount} Kalemi Sıfırla` : going.length ? `${going.length} Veri Türünü Sıfırla` : 'Seçilen Verileri Sıfırla'}</button>
      </div>
    </StickyActionBar>
  </>
}
