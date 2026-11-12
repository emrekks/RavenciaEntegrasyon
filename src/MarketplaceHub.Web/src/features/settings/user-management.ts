// Ayarlar › Kullanıcılar: the workspace's users, managed by the owner and administrators (the server answers others 403).
// Rules the server enforces too (UserManagementEndpoints): nobody changes their own account here, an administrator
// cannot touch an owner or make one, every change asks for the caller's password (and authenticator code) again.

export type UserRole = 'OWNER' | 'ADMINISTRATOR' | 'OPERATIONS' | 'ACCOUNTING' | 'READONLY'

export type ManagedUser = {
  id: string
  displayName: string
  email: string | null
  role: UserRole
  active: boolean
  lockedOut: boolean
  mfaEnabled: boolean
  passwordChangePending: boolean
  lastLoginAt: string | null
  createdAt: string
  openSessions: number
  self: boolean
}

export type ManagedUserList = { currentUserId: string; currentRole: string; items: ManagedUser[] }

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
export function reauthBody(password: string, secondFactor: string) {
  const factor = secondFactor.trim()
  if (!factor) return { password }
  return /^\d{6}$/.test(factor.replace(/\s/g, '')) ? { password, code: factor.replace(/\s/g, '') } : { password, recoveryCode: factor }
}

export function isEmailAddress(value: string) {
  return /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(value.trim())
}
