import type { ApiAnswer } from '../../../shared/api/api-answer'
import { useState, type FormEvent } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiRequestError, hubApi } from '../../../shared/api'
import { LoadingState, Modal } from '../../../shared/components'
import { SettingsNotice, type SettingsNoticeState } from '../SettingsNotifications'
import { PanelSelect } from '../../../shared/PanelSelect'
import { copyText } from '../../../shared/copy-text'
import { addAlreadyDoneMessage, assignableRoles, availableUserActions, canWithdrawInvitation, invitationSentMessage, isEmailAddress, reauthBody, userLockReason, userRoleHints, userRoleLabel, userRoleLabels, type ManagedUser, type ManagedUserList, type PendingInvitation, type UserAction, type UserRole } from '../user-management'
import { newIdempotencyKey, sendWithVersionedIdempotencyKey, versionedIdempotencyKey } from '../../../shared/idempotency'
import { queryKeys } from '../../../shared/api/query-keys'
import { type SettingsIconName, formatSettingsDate, SettingsIcon, SettingsGlyph, SettingsPaneHead, ReauthFields } from '../settings-ui'

type UserDialog = { kind: 'add' } | { kind: 'manage'; userId: string }
type OneTimePassword = { title: string; email: string | null; password: string }

const userActionCopy: Record<UserAction, { label: string; hint: string; icon: SettingsIconName; confirm: string; danger?: boolean; warning?: string }> = {
  role: { label: 'Rol Değiştir', hint: 'Bu çalışma alanındaki yetkisini değiştirir.', icon: 'user', confirm: 'Rolü Kaydet' },
  'password-reset': { label: 'Şifre Sıfırla', hint: 'Tek kullanımlık yeni parola üretir; ilk girişte değiştirilir.', icon: 'lock', confirm: 'Şifreyi Sıfırla', danger: true, warning: 'Mevcut parolası geçersiz olur ve bütün oturumları kapanır. Yeni parola yalnız bir kez gösterilir.' },
  'mfa-reset': { label: 'İki Adımlı Doğrulamayı Sıfırla', hint: 'Authenticator ve kurtarma kodlarını kaldırır.', icon: 'shield', confirm: 'Doğrulamayı Sıfırla', danger: true, warning: 'Authenticator ve kurtarma kodları silinir, bütün oturumları kapanır. Kullanıcı girişten sonra yeniden kurabilir.' },
  sessions: { label: 'Oturumları Kapat', hint: 'Bu çalışma alanındaki açık oturumlarını sonlandırır.', icon: 'logout', confirm: 'Oturumları Kapat', danger: true, warning: 'Kullanıcının açık oturumları kapanır; yeniden giriş yapması gerekir.' },
  disable: { label: 'Pasife Al', hint: 'Bu çalışma alanına girişini durdurur.', icon: 'logout', confirm: 'Pasife Al', danger: true, warning: 'Kullanıcı bu çalışma alanına giremez ve açık oturumları kapanır. Kayıtları silinmez; dilediğiniz an etkinleştirebilirsiniz.' },
  enable: { label: 'Etkinleştir', hint: 'Bu çalışma alanına yeniden girebilir.', icon: 'reset', confirm: 'Etkinleştir' }
}

function userStatusTag(user: ManagedUser) {
  if (!user.active) return <span className="ss-tag"><SettingsGlyph state="none" />Pasif</span>
  if (user.lockedOut) return <span className="ss-tag"><SettingsGlyph state="bad" />Kilitli</span>
  if (user.passwordChangePending) return <span className="ss-tag" title="İlk girişte parolasını değiştirecek"><SettingsGlyph state="pending" />Parola Bekliyor</span>
  return <span className="ss-tag is-ink"><SettingsGlyph state="done" />Etkin</span>
}

// Ayarlar › Kullanıcılar: owners and administrators list, add and change the workspace's users (user-management.ts).
export function UserManagementSection({ mfa }: { mfa: boolean }) {
  const client = useQueryClient()
  const query = useQuery({ queryKey: queryKeys.managedUsers(), queryFn: ({ signal }) => hubApi<ManagedUserList>('/settings/users', { signal }), retry: false })
  const forbidden = query.error instanceof ApiRequestError && query.error.status === 403
  const [dialog, setDialog] = useState<UserDialog | null>(null)
  const [step, setStep] = useState<UserAction | null>(null)
  const [password, setPassword] = useState('')
  const [factor, setFactor] = useState('')
  const [role, setRole] = useState<UserRole>('OPERATIONS')
  const [email, setEmail] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [oneTime, setOneTime] = useState<OneTimePassword | null>(null)
  const [notice, setNotice] = useState<SettingsNoticeState | null>(null)
  // One Idempotency-Key per Kullanıcı Ekle window: a double click or a resend after a lost answer adds the person once.
  const [addKey, setAddKey] = useState(() => newIdempotencyKey('user-add'))
  const [withdrawing, setWithdrawing] = useState<string | null>(null)
  const list = query.data
  const actorRole = list?.currentRole ?? ''
  const roles = assignableRoles(actorRole)
  const managed = dialog?.kind === 'manage' ? list?.items.find(user => user.id === dialog.userId) ?? null : null

  function open(next: UserDialog) {
    setDialog(next); setStep(null); setPassword(''); setFactor(''); setError('')
    if (next.kind === 'add') { setEmail(''); setDisplayName(''); setRole('OPERATIONS'); setAddKey(newIdempotencyKey('user-add')) }
  }
  function close() { if (!busy) { setDialog(null); setStep(null); setPassword(''); setFactor(''); setError('') } }
  function chooseStep(action: UserAction | null) {
    setStep(action); setPassword(''); setFactor(''); setError('')
    if (action === 'role' && managed) setRole(managed.role)
  }

  async function submitAdd(event: FormEvent) {
    event.preventDefault()
    if (!isEmailAddress(email)) { setError('Geçerli bir e-posta adresi girin.'); return }
    setBusy(true); setError('')
    const person = { email: email.trim(), displayName: displayName.trim(), role }
    // The key's fingerprint covers who is added, never the password typed to confirm it.
    const versionedKey = versionedIdempotencyKey('user-add', addKey, 0, JSON.stringify(person))
    try {
      const result = await sendWithVersionedIdempotencyKey(versionedKey, key => hubApi<ApiAnswer<'postSettingsUsers'>>('/settings/users', { method: 'POST', headers: { 'Idempotency-Key': key }, body: JSON.stringify({ ...person, ...reauthBody(password, factor) }) }))
      await client.invalidateQueries({ queryKey: queryKeys.managedUsers() })
      setDialog(null)
      if (result.temporaryPassword) setOneTime({ title: 'Kullanıcı Eklendi', email: person.email, password: result.temporaryPassword })
      else setNotice({ kind: 'success', message: invitationSentMessage(person.email, role) })
    } catch (reason) {
      if (reason instanceof ApiRequestError && reason.code === 'IDEMPOTENCY_RESPONSE_WITHHELD') {
        await client.invalidateQueries({ queryKey: queryKeys.managedUsers() })
        setDialog(null)
        setNotice({ kind: 'info', message: addAlreadyDoneMessage })
        return
      }
      setError(reason instanceof Error ? reason.message : 'Kullanıcı eklenemedi.')
    } finally { setBusy(false) }
  }

  async function withdraw(invitation: PendingInvitation) {
    setWithdrawing(invitation.id)
    try {
      await hubApi(`/settings/users/invitations/${invitation.id}`, { method: 'DELETE' })
      await client.invalidateQueries({ queryKey: queryKeys.managedUsers() })
      setNotice({ kind: 'success', message: `${invitation.email} için davet geri çekildi.` })
    } catch (reason) {
      setNotice({ kind: 'error', message: reason instanceof Error ? reason.message : 'Davet geri çekilemedi.' })
    } finally { setWithdrawing(null) }
  }

  async function submitAction(event: FormEvent) {
    event.preventDefault()
    if (!managed || !step) return
    setBusy(true); setError('')
    const reauth = reauthBody(password, factor)
    const base = `/settings/users/${managed.id}`
    try {
      if (step === 'role') await hubApi(`${base}/role`, { method: 'PUT', body: JSON.stringify({ role, ...reauth }) })
      else if (step === 'disable' || step === 'enable') await hubApi(`${base}/status`, { method: 'PUT', body: JSON.stringify({ active: step === 'enable', ...reauth }) })
      else if (step === 'mfa-reset') await hubApi(`${base}/mfa-reset`, { method: 'POST', body: JSON.stringify(reauth) })
      else if (step === 'sessions') await hubApi(`${base}/sessions/revoke`, { method: 'POST', body: JSON.stringify(reauth) })
      else {
        const result = await hubApi<{ temporaryPassword: string }>(`${base}/password-reset`, { method: 'POST', body: JSON.stringify(reauth) })
        setOneTime({ title: 'Şifre Sıfırlandı', email: managed.email, password: result.temporaryPassword })
      }
      await client.invalidateQueries({ queryKey: queryKeys.managedUsers() })
      const done: Record<UserAction, string> = { role: `${managed.displayName} artık ${userRoleLabel(role)}.`, 'password-reset': '', 'mfa-reset': `${managed.displayName} için iki adımlı doğrulama sıfırlandı.`, sessions: `${managed.displayName} için açık oturumlar kapatıldı.`, disable: `${managed.displayName} pasife alındı.`, enable: `${managed.displayName} etkinleştirildi.` }
      if (done[step]) setNotice({ kind: 'success', message: done[step] })
      setDialog(null); setStep(null); setPassword(''); setFactor('')
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'İşlem tamamlanamadı.')
    } finally { setBusy(false) }
  }

  async function copyOneTime() {
    if (!oneTime) return
    setNotice(await copyText(oneTime.password) ? { kind: 'success', message: 'Tek kullanımlık parola panoya kopyalandı.' } : { kind: 'error', message: 'Parola kopyalanamadı; elle not alın.' })
  }

  const actionCopy = step ? userActionCopy[step] : null
  const lockReason = managed ? userLockReason(managed, actorRole) : null
  const actions = managed ? availableUserActions(managed, actorRole) : []
  const activeCount = list?.items.filter(user => user.active).length ?? 0

  return <>
    {notice && <SettingsNotice {...notice} />}
    <SettingsPaneHead section="users" description="Bu çalışma alanının kullanıcıları. Yalnız hesap sahibi ve yöneticiler görür; her değişiklik parolanızla onaylanır ve kayda geçer." actions={list && !forbidden ? <button type="button" className="ss-btn is-primary" onClick={() => open({ kind: 'add' })}><SettingsIcon name="plus" />Kullanıcı Ekle</button> : undefined} />
    {query.isPending ? <div className="ss-body"><LoadingState>Kullanıcılar yükleniyor…</LoadingState></div>
      : forbidden ? <div className="ss-body"><div className="ss-callout" role="alert"><SettingsIcon name="lock" /><span>Kullanıcıları yalnız hesap sahibi ve yöneticiler görür ve yönetir.</span></div></div>
        : query.isError || !list ? <div className="ss-body"><div className="ss-callout is-danger" role="alert"><SettingsIcon name="alert" /><span>Kullanıcılar alınamadı.</span></div></div>
          : <div className="ss-body is-flush"><div className="ss-sess-sub">Etkin <span className="ss-count">{activeCount}</span>{list.items.length > activeCount ? <> · Pasif <span className="ss-count">{list.items.length - activeCount}</span></> : null}{list.invitations.length ? <> · Davet Bekliyor <span className="ss-count">{list.invitations.length}</span></> : null}</div>
            <table className="ss-users">
              <thead><tr><th scope="col">Ad Soyad ve E-posta</th><th scope="col">Rol</th><th scope="col">2FA</th><th scope="col">Son Giriş</th><th scope="col">Durum</th><th scope="col" aria-label="İşlemler" /></tr></thead>
              <tbody>{list.items.map(user => <tr key={user.id} className={user.active ? undefined : 'is-off'}>
                <td className="ss-users-name"><span><b title={user.displayName}>{user.displayName}</b>{user.self && <span className="ss-tag is-ink">Siz</span>}</span><small title={user.email ?? undefined}>{user.email ?? '—'}</small></td>
                <td data-label="Rol">{userRoleLabel(user.role)}</td>
                <td data-label="2FA"><span className={`ss-tag${user.mfaEnabled ? ' is-ink' : ''}`}><SettingsGlyph state={user.mfaEnabled ? 'done' : 'none'} />{user.mfaEnabled ? 'Açık' : 'Kapalı'}</span></td>
                <td data-label="Son Giriş" className="is-num">{user.lastLoginAt ? formatSettingsDate(user.lastLoginAt) : 'Hiç Girmedi'}</td>
                <td data-label="Durum">{userStatusTag(user)}</td>
                <td className="ss-users-act"><button type="button" className="ss-btn is-xs" onClick={() => open({ kind: 'manage', userId: user.id })} aria-label={`${user.displayName} kullanıcısını yönet`}>Yönet</button></td>
              </tr>)}
              {/* Invited, not yet accepted (API-GUVENLIK-6): no access until the person accepts after signing in. */}
              {list.invitations.map(invitation => <tr key={`invitation-${invitation.id}`} className="is-off">
                <td className="ss-users-name"><span><b title={invitation.displayName}>{invitation.displayName}</b></span><small title={invitation.email}>{invitation.email}</small></td>
                <td data-label="Rol">{userRoleLabel(invitation.role)}</td>
                <td data-label="2FA">—</td>
                <td data-label="Son Giriş" className="is-num" title={`Davet: ${formatSettingsDate(invitation.invitedAt)} · Son geçerlilik: ${formatSettingsDate(invitation.expiresAt)}`}>Hiç Girmedi</td>
                <td data-label="Durum">{invitation.expired
                  ? <span className="ss-tag" title="Kabul süresi doldu; yeniden ekleyerek daveti yenileyebilirsiniz."><SettingsGlyph state="none" />Süresi Doldu</span>
                  : <span className="ss-tag" title={`Davet ${formatSettingsDate(invitation.expiresAt)} tarihine kadar geçerli; kişi kendi hesabıyla girip kabul edince katılır.`}><SettingsGlyph state="pending" />Bekliyor</span>}</td>
                <td className="ss-users-act">{canWithdrawInvitation(invitation, actorRole) && <button type="button" className="ss-btn is-xs" disabled={withdrawing === invitation.id} onClick={() => void withdraw(invitation)} aria-label={`${invitation.email} davetini geri çek`}>{withdrawing === invitation.id ? 'Çekiliyor…' : 'Geri Çek'}</button>}</td>
              </tr>)}</tbody>
            </table>
          </div>}

    <Modal open={dialog?.kind === 'add'} title="Kullanıcı Ekle" hint="Panel e-posta göndermez. Bu e-postayla hesap yoksa yeni hesaba tek kullanımlık bir parola üretilir, yalnız size bir kez gösterilir; kullanıcı ilk girişte kendi parolasını belirler. Hesap zaten varsa kişi davet edilir: kendi hesabıyla girdiğinde daveti görür ve kabul edene kadar bu çalışma alanına erişemez. Davet 14 gün geçerlidir." className="ss-modal is-md" onClose={close} footer={<><button type="button" className="ss-btn" disabled={busy} onClick={close}>Vazgeç</button><button type="submit" form="ss-user-add" className="ss-btn is-primary" disabled={busy}><SettingsIcon name="plus" />{busy ? 'Ekleniyor…' : 'Kullanıcı Ekle'}</button></>}>
      <form id="ss-user-add" className="ss-stack" onSubmit={event => void submitAdd(event)}>
        <div className="ss-pair">
          <label className="ss-fld"><small>Ad Soyad</small><input className="ss-in" required maxLength={160} autoComplete="off" value={displayName} onChange={event => setDisplayName(event.target.value)} data-autofocus /></label>
          <label className="ss-fld"><small>E-posta</small><input className="ss-in" type="email" inputMode="email" required maxLength={256} autoComplete="off" spellCheck={false} value={email} onChange={event => setEmail(event.target.value)} placeholder="ad@ravencia.com" /></label>
        </div>
        <div className="ss-fld"><small>Rol</small><PanelSelect ariaLabel="Rol" value={role} onChange={setRole} options={roles.map(value => ({ value, label: userRoleLabels[value] }))} /><span className="ss-help">{userRoleHints[role]}</span></div>
        <ReauthFields password={password} onPassword={setPassword} factor={factor} onFactor={setFactor} mfa={mfa} />
        {error && <span className="ss-ferr" role="alert"><SettingsGlyph state="bad" />{error}</span>}
      </form>
    </Modal>

    <Modal open={dialog?.kind === 'manage' && !!managed} title={managed ? managed.displayName : 'Kullanıcı'} description={managed ? `${managed.email ?? ''} · ${userRoleLabel(managed.role)}` : undefined} className="ss-modal is-md" onClose={close}
      footer={step ? <><button type="button" className="ss-btn" disabled={busy} onClick={() => chooseStep(null)}><SettingsIcon name="back" />Geri</button><button type="submit" form="ss-user-action" className={`ss-btn${actionCopy?.danger ? ' is-danger is-solid' : ' is-primary'}`} disabled={busy || (step === 'role' && role === managed?.role)}>{busy ? 'İşlem Yapılıyor…' : actionCopy?.confirm}</button></> : <button type="button" className="ss-btn" onClick={close}>Kapat</button>}>
      {managed && !step && <>
        <div className="ss-sum">
          <div><SettingsGlyph state={managed.mfaEnabled ? 'done' : 'none'} /><span>İki Adımlı Doğrulama</span><span className="ss-sum-r">{managed.mfaEnabled ? 'Açık' : 'Kapalı'}</span></div>
          <div><SettingsGlyph state={managed.openSessions ? 'done' : 'none'} /><span>Açık Oturum</span><span className="ss-sum-r">{managed.openSessions}</span></div>
          <div><SettingsGlyph state={managed.lastLoginAt ? 'done' : 'none'} /><span>Son Giriş</span><span className="ss-sum-r">{managed.lastLoginAt ? formatSettingsDate(managed.lastLoginAt) : 'Hiç Girmedi'}</span></div>
        </div>
        {lockReason ? <div className="ss-callout" role="note"><SettingsIcon name="lock" /><span>{lockReason}</span></div>
          : <div className="ss-users-actions" role="group" aria-label="Kullanıcı işlemleri">{actions.map(action => <button type="button" key={action} className={`ss-users-action${userActionCopy[action].danger ? ' is-danger' : ''}`} onClick={() => chooseStep(action)}><span className="ss-tile is-sm"><SettingsIcon name={userActionCopy[action].icon} /></span><span><b>{userActionCopy[action].label}</b><small>{userActionCopy[action].hint}</small></span><SettingsIcon name="chev" /></button>)}</div>}
      </>}
      {managed && step && actionCopy && <form id="ss-user-action" className="ss-stack" onSubmit={event => void submitAction(event)}>
        <h3 className="ss-users-step">{actionCopy.label}</h3>
        {step === 'role' && <div className="ss-fld"><small>Yeni Rol</small><PanelSelect ariaLabel="Yeni Rol" value={role} onChange={setRole} options={roles.map(value => ({ value, label: userRoleLabels[value] }))} /><span className="ss-help">{userRoleHints[role]}</span></div>}
        {actionCopy.warning && <div className="ss-callout is-warn"><SettingsIcon name="alert" /><span>{actionCopy.warning}</span></div>}
        <ReauthFields password={password} onPassword={setPassword} factor={factor} onFactor={setFactor} mfa={mfa} />
        {error && <span className="ss-ferr" role="alert"><SettingsGlyph state="bad" />{error}</span>}
      </form>}
    </Modal>

    <Modal open={!!oneTime} title={oneTime?.title ?? ''} description={oneTime?.email ?? undefined} className="ss-modal is-sm" onClose={() => setOneTime(null)} footer={<><button type="button" className="ss-btn is-quiet" onClick={() => void copyOneTime()}><SettingsIcon name="copy" />Kopyala</button><span className="ss-modal-note" /><button type="button" className="ss-btn is-primary" onClick={() => setOneTime(null)}>Parolayı Not Aldım</button></>}>
      {oneTime && <><div className="ss-codes ss-users-otp"><code>{oneTime.password}</code></div><div className="ss-callout is-warn"><SettingsIcon name="alert" /><span><b>Bu parola yalnız şimdi gösterilir.</b> Kullanıcıya güvenli bir yoldan iletin; ilk girişte kendi parolasını belirlemesi istenir.</span></div></>}
    </Modal>
  </>
}
