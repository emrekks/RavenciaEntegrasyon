// @vitest-environment node
import { ESLint } from 'eslint'
import { describe, expect, it } from 'vitest'
import { queryKeys, queryRootList } from './query-keys'

describe('query key factory', () => {
  it('builds the same arrays the screens wrote by hand', () => {
    const filters = { status: 'NEW' }
    expect(queryKeys.orders()).toEqual(['orders'])
    expect(queryKeys.orders('page', filters, 2)).toEqual(['orders', 'page', filters, 2])
    expect(queryKeys.invoiceWorkspacePage('x')).toEqual(['invoice-workspace-page', 'x'])
    expect(queryKeys.job(undefined)).toEqual(['job', undefined])
  })

  it('names every root once, under its camel-case name', () => {
    const roots = queryRootList.map(root => root.root)
    expect(new Set(roots).size).toBe(roots.length)
    for (const [name, root] of Object.entries(queryKeys)) expect(name).toBe(root.root.replace(/-([a-z0-9])/g, (_, c: string) => c.toUpperCase()))
  })
})

describe('query key lint guard', () => {
  const eslint = new ESLint()
  const problems = async (code: string, filePath = 'src/features/example/Example.tsx') => {
    const [result] = await eslint.lintText(code, { filePath })
    return result.messages.filter(message => message.ruleId === 'no-restricted-syntax').length
  }

  it('fails on a key written as a literal array outside the factory', async () => {
    expect(await problems("useQuery({ queryKey: ['orders', 'page'], queryFn })")).toBe(1)
    expect(await problems("client.invalidateQueries({ queryKey: ['orders'] as const })")).toBe(1)
    expect(await problems("client.setQueryData(['connection', id], value)")).toBe(1)
    expect(await problems("const statusKey = ['publication-status', id]")).toBe(1)
    expect(await problems("export const instantNotificationsQueryKey = (id: string) => ['instant-notifications', id] as const")).toBe(1)
    expect(await problems("export function connectionFullResetQueryKey(id: string) { return ['connection-full-reset', id] as const }")).toBe(1)
  }, 30_000)

  it('accepts factory keys, and literal keys in tests and in the factory', async () => {
    expect(await problems("useQuery({ queryKey: queryKeys.orders('page'), queryFn })")).toBe(0)
    expect(await problems("client.invalidateQueries({ queryKey: queryKeys.orders() })")).toBe(0)
    expect(await problems("expect(key).toEqual(['orders'])\nuseQuery({ queryKey: ['orders'] })", 'src/features/example/Example.test.tsx')).toBe(0)
    expect(await problems("const statusKey = ['orders']", 'src/shared/api/query-keys.ts')).toBe(0)
  }, 30_000)
})
