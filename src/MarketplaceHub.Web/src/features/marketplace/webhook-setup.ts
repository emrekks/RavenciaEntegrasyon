export type WebhookSetup = { environment: string; baseUrl: string; username: string; password: string; paths: string[] }

// The text the seller sends Hepsiburada to have its order and claim notifications set up.
export function webhookSetupText(setup: WebhookSetup) {
  return [
    `Hepsiburada webhook tanımı (${setup.environment})`,
    `Base URL: ${setup.baseUrl}`,
    'Kimlik doğrulama: Basic Auth',
    `Kullanıcı adı: ${setup.username}`,
    `Şifre: ${setup.password}`,
    'Yollar:',
    ...setup.paths.map(path => `  ${path}`)
  ].join('\n')
}
