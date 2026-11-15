import js from '@eslint/js'
import { defineConfig, globalIgnores } from 'eslint/config'
import globals from 'globals'
import tseslint from 'typescript-eslint'
import reactHooks from 'eslint-plugin-react-hooks'
import jsxA11y from 'eslint-plugin-jsx-a11y'

// Lint for the panel: language and TypeScript basics, hook rules (a missed dependency is an error, not a warning),
// and accessibility rules for the markup. `npm run lint` must pass with no warnings.
export default defineConfig([
  globalIgnores(['dist', 'node_modules', 'coverage']),
  {
    files: ['**/*.{ts,tsx}'],
    extends: [js.configs.recommended, tseslint.configs.recommended, jsxA11y.flatConfigs.recommended],
    languageOptions: { ecmaVersion: 2023, globals: globals.browser },
    plugins: { 'react-hooks': reactHooks },
    rules: {
      // The two classic hook rules; react-hooks v7's React Compiler rules target a compiler this project does not use.
      'react-hooks/rules-of-hooks': 'error',
      'react-hooks/exhaustive-deps': 'error',
      // the project's convention for intentionally unused names (TypeScript's own check accepts them too)
      '@typescript-eslint/no-unused-vars': ['error', { argsIgnorePattern: '^_', varsIgnorePattern: '^_', caughtErrorsIgnorePattern: '^_', ignoreRestSiblings: true }],
      // components take a `role` prop of their own (e.g. PanelAttributeLibraryBuilder role="OPTION")
      'jsx-a11y/aria-role': ['error', { ignoreNonDOM: true }],
      // checkbox cards wrap the input and put the text in <span><strong>; TokenValueInput is a text control
      'jsx-a11y/label-has-associated-control': ['error', { assert: 'either', depth: 4, controlComponents: ['TokenValueInput', 'PanelSelect'] }],
    },
  },
  {
    // WEB-ALTYAPI-10: every query key is built by the factory in src/shared/api/query-keys.ts, so a refresh cannot name a
    // root that no query uses. A key written as a literal array anywhere else is an error (tests may write keys out).
    files: ['src/**/*.{ts,tsx}'],
    ignores: ['src/shared/api/query-keys.ts', 'src/**/*.test.{ts,tsx}'],
    rules: {
      'no-restricted-syntax': ['error', ...[
        "Property[key.name='queryKey'] > ArrayExpression[elements.0.type=/^(Literal|TemplateLiteral)$/]",
        "Property[key.name='queryKey'] > TSAsExpression > ArrayExpression[elements.0.type=/^(Literal|TemplateLiteral)$/]",
        "CallExpression[callee.property.name=/^(setQueryData|getQueryData|getQueryState|setQueryDefaults|getQueryDefaults)$/][arguments.0.type='ArrayExpression'][arguments.0.elements.0.type=/^(Literal|TemplateLiteral)$/]",
        "VariableDeclarator[id.name=/[qQ]ueryKey$|^[a-z]\\w*Key$/] > ArrayExpression[elements.0.type=/^(Literal|TemplateLiteral)$/]",
        "VariableDeclarator[id.name=/[qQ]ueryKey$|^[a-z]\\w*Key$/] > TSAsExpression > ArrayExpression[elements.0.type=/^(Literal|TemplateLiteral)$/]",
        "VariableDeclarator[id.name=/[qQ]ueryKey$/] > ArrowFunctionExpression > TSAsExpression > ArrayExpression[elements.0.type=/^(Literal|TemplateLiteral)$/]",
        "VariableDeclarator[id.name=/[qQ]ueryKey$/] > ArrowFunctionExpression > ArrayExpression[elements.0.type=/^(Literal|TemplateLiteral)$/]",
        "FunctionDeclaration[id.name=/Key$/] ReturnStatement > ArrayExpression[elements.0.type=/^(Literal|TemplateLiteral)$/]",
        "FunctionDeclaration[id.name=/Key$/] ReturnStatement > TSAsExpression > ArrayExpression[elements.0.type=/^(Literal|TemplateLiteral)$/]",
      ].map(selector => ({ selector, message: 'Build query keys with queryKeys from src/shared/api/query-keys.ts, not a literal array.' }))],
    },
  },
  {
    files: ['scripts/**/*.{js,mjs}', 'tests/**/*.mjs', '*.config.{js,ts}', 'vite.config.ts', 'vitest.config.ts'],
    languageOptions: { globals: globals.node },
  },
])
