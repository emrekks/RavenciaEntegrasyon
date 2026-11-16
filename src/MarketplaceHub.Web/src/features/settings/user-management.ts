// Ayarlar › Kullanıcılar: the workspace's users, managed by the owner and administrators (the server answers others 403).
// Rules the server enforces too (UserManagementEndpoints): nobody changes their own account here, an administrator
// cannot touch an owner or make one, every change asks for the caller's password (and authenticator code) again.
import type { ApiAnswer } from '../../shared/api/api-answer'

export type UserRole = 'OWNER' | 'ADMINISTRATOR' | 'OPERATIONS' | 'ACCOUNTING' | 'READONLY'

type ManagedUserAnswer = ApiAnswer<'getSettingsUsers'>

export type ManagedUser = Omit<ManagedUserAnswer['items'][number], 'role'> & {
  // CONTRACT: the API declares a string; a membership role is one of the five above (EndpointRules, UserManagementEndpoints).
  role: UserRole
}

// An existing account invited to the workspace, not yet answered (API-GUVENLIK-6): the name and e-mail the inviter typed.
export type PendingInvitation = Omit<ManagedUserAnswer['invitations'][number], 'role'> & {
  // CONTRACT: the API declares a string; the offered role is one of the five membership roles.
  role: UserRole
}

export type ManagedUserList = Omit<ManagedUserAnswer, 'items' | 'invitations'> & { items: ManagedUser[]; invitations: PendingInvitation[] }

// Only an owner withdraws an invitation to the owner role (the server refuses an administrator, OWNER_PROTECTED).
export function canWithdrawInvitation(invitation: PendingInvitation, actorRole: string) {
  return invitation.role !== 'OWNER' || actorRole.toUpperCase() === 'OWNER'
}

// What the inviter is told after Kullanıcı Ekle: a new account comes with its one-time password (the one-time window);
// without one, the e-mail belongs to an existing account that was invited and joins only when it accepts.
export function invitationSentMessage(email: string, role: string) {
  return `${email} adresine ${userRoleLabel(role)} rolüyle davet gönderildi. Kişi kendi hesabıyla giriş yaptığında daveti görür; kabul edene kadar bu çalışma alanına erişemez.`
}

// A repeat of an add that already went through (same Idempotency-Key): the one-time password is shown only once.
export const addAlreadyDoneMessage = 'Bu ekleme daha önce tamamlandı. Tek kullanımlık parola yalnız ilk yanıtta gösterilir; gerekirse kullanıcıyı listeden açıp Şifre Sıfırla ile yeni parola üretin.'

export type UserAction = 'role' | 'password-reset' | 'mfa-reset' | 'sessions' | 'disable' | 'enable'

export const userRoleLabels: Record<UserRole, string> = {
  OWNER: 'Hesap Sahibi',
  ADMINISTRATOR: 'Yönetici',
  OPERATIONS: 'Operasyon',
  ACCOUNTING: 'Muhasebe',
  READONLY: 'Salt Okunur'
}

export const userRoleHints: Record<UserRole, string> = {
  OWNER: 'Her şey, kullanıcı ve sahip yönetimi dahil.',
  ADMINISTRATOR: 'Her şey; sahip hesaplarını değiştiremez, sahip atayamaz.',
  OPERATIONS: 'Sipariş, ürün, kargo ve pazaryeri işleri.',
  ACCOUNTING: 'Faturalar ve finans; sipariş etiketleri.',
  READONLY: 'Yalnız görüntüleme.'
}

const roleOrder: UserRole[] = ['OWNER', 'ADMINISTRATOR', 'OPERATIONS', 'ACCOUNTING', 'READONLY']

export function userRoleLabel(role: string) {
  return userRoleLabels[role as UserRole] ?? role
}

// The roles the signed-in user may give: only an owner makes an owner.
export function assignableRoles(actorRole: string): UserRole[] {
  return actorRole.toUpperCase() === 'OWNER' ? roleOrder : roleOrder.filter(role => role !== 'OWNER')
}

// Why a user cannot be changed by the signed-in one, or null when they can.
export function userLockReason(user: ManagedUser, actorRole: string): string | null {
  if (user.self) return 'Kendi hesabınızı Güvenlik ve Oturumlar bölümünden yönetin.'
  if (user.role === 'OWNER' && actorRole.toUpperCase() !== 'OWNER') return 'Hesap sahibini yalnız bir hesap sahibi değiştirebilir.'
  return null
}

// The actions offered for a user, in the order the window lists them.
export function availableUserActions(user: ManagedUser, actorRole: string): UserAction[] {
  if (userLockReason(user, actorRole)) return []
  const actions: UserAction[] = ['role', 'password-reset']
  if (user.mfaEnabled) actions.push('mfa-reset')
  if (user.openSessions > 0) actions.push('sessions')
  actions.push(user.active ? 'disable' : 'enable')
  return actions
}

// The re-check every change carries: the password, and with an authenticator on, a 6-digit code or a recovery code.
export { reauthBody } from '../../shared/reauth'

export function isEmailAddress(value: string) {
  return /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(value.trim())
}
