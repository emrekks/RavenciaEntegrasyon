export function shouldLoadConnectionCapabilities(platformCode: string | undefined): boolean {
  return platformCode === 'SHOPIFY' || platformCode === 'HEPSIBURADA'
}
