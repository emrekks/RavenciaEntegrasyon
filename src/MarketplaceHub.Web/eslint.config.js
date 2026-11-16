import js from '@eslint/js'
import { defineConfig, globalIgnores } from 'eslint/config'
import globals from 'globals'
import tseslint from 'typescript-eslint'
import reactHooks from 'eslint-plugin-react-hooks'
import jsxA11y from 'eslint-plugin-jsx-a11y'

const queryKeySelectors = [
  "Property[key.name='queryKey'] > ArrayExpression[elements.0.type=/^(Literal|TemplateLiteral)$/]",
  "Property[key.name='queryKey'] > TSAsExpression > ArrayExpression[elements.0.type=/^(Literal|TemplateLiteral)$/]",
  "CallExpression[callee.property.name=/^(setQueryData|getQueryData|getQueryState|setQueryDefaults|getQueryDefaults)$/][arguments.0.type='ArrayExpression'][arguments.0.elements.0.type=/^(Literal|TemplateLiteral)$/]",
  "VariableDeclarator[id.name=/[qQ]ueryKey$|^[a-z]\\w*Key$/] > ArrayExpression[elements.0.type=/^(Literal|TemplateLiteral)$/]",
  "VariableDeclarator[id.name=/[qQ]ueryKey$|^[a-z]\\w*Key$/] > TSAsExpression > ArrayExpression[elements.0.type=/^(Literal|TemplateLiteral)$/]",
  "VariableDeclarator[id.name=/[qQ]ueryKey$/] > ArrowFunctionExpression > TSAsExpression > ArrayExpression[elements.0.type=/^(Literal|TemplateLiteral)$/]",
  "VariableDeclarator[id.name=/[qQ]ueryKey$/] > ArrowFunctionExpression > ArrayExpression[elements.0.type=/^(Literal|TemplateLiteral)$/]",
  "FunctionDeclaration[id.name=/Key$/] ReturnStatement > ArrayExpression[elements.0.type=/^(Literal|TemplateLiteral)$/]",
  "FunctionDeclaration[id.name=/Key$/] ReturnStatement > TSAsExpression > ArrayExpression[elements.0.type=/^(Literal|TemplateLiteral)$/]",
]
const queryKeyRestrictions = queryKeySelectors.map(selector => ({ selector, message: 'Build query keys with queryKeys from src/shared/api/query-keys.ts, not a literal array.' }))

// WEB-SAYFALAR-11: windows are RrModal (src/shared/ui/rr-components.tsx). A hand-written backdrop (a className holding
// "backdrop", or rr-overlay outside RrModal itself) is an error in any file. The files listed here still hold one, as the
// one-modal migration is postponed (owner, 16 Nov 2026). The list may only shrink: remove a file once its last
// hand-written backdrop is gone, never add one.
const backdropRestriction = {
  selector: "JSXAttribute[name.name='className'][value.type='Literal'][value.value=/backdrop|(^|\\s)r[rv]-overlay(\\s|$)/]",
  message: 'Do not hand-write a modal backdrop: open the window with RrModal (src/shared/ui/rr-components.tsx).',
}
// The two window components themselves (they are the one place a backdrop is written, for everyone else).
const modalDefinitionFiles = ['src/shared/ui/rr-components.tsx', 'src/shared/components/DesignSystem.tsx']
const handWrittenBackdropFiles = [
  'src/features/catalog/CategoryMappingDialog.tsx',
  'src/features/catalog/MissingValueMappingDialog.tsx',
  'src/features/catalog/product-editor/BulkVariantPlatformPricingModal.tsx',
  'src/features/catalog/product-editor/DesiCalculatorDialog.tsx',
  'src/features/catalog/product-editor/EditorDialogs.tsx',
  'src/features/catalog/product-editor/EditorWindows.tsx',
  'src/features/catalog/product-editor/MarketplaceCodesDialog.tsx',
  'src/features/catalog/product-editor/PlatformUpdateDialog.tsx',
  'src/features/catalog/product-editor/ProductEditorPages.tsx',
  'src/features/catalog/product-editor/PublicationJobDialog.tsx',
  'src/features/catalog/product-editor/VariantMediaPickerModal.tsx',
  'src/features/catalog/product-editor/VariantOptionEditDialog.tsx',
  'src/features/catalog/products/BulkPublishDialog.tsx',
  'src/features/invoicing/InvoiceWorkspacePreviewModal.tsx',
  'src/features/invoicing/pages.tsx',
  'src/features/marketplace/integrations/IntegrationPages.tsx',
  'src/features/marketplace/mappings/MappingPages.tsx',
  'src/features/marketplace/mappings/mapping-pages/BrandMappingPage.tsx',
  'src/features/marketplace/mappings/mapping-pages/CategoryAttributeWorkspace.tsx',
  'src/features/marketplace/mappings/mapping-pages/ValueMappingEditors.tsx',
  'src/features/marketplace/orders/orders-page/OrderInvoiceModals.tsx',
  'src/features/operations/pages.tsx',
  'src/shared/MobileFilterShell.tsx',
]

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
      'no-restricted-syntax': ['error', ...queryKeyRestrictions, backdropRestriction],
    },
  },
  {
    // The hand-written backdrops that already exist (handWrittenBackdropFiles): the query-key rule only.
    files: [...handWrittenBackdropFiles, ...modalDefinitionFiles],
    ignores: ['src/**/*.test.{ts,tsx}'],
    rules: { 'no-restricted-syntax': ['error', ...queryKeyRestrictions] },
  },
  {
    // WEB-ALTYAPI-12: src/shared is the layer every feature builds on, so it never imports a feature (a change to one
    // screen must not reach every other screen through it). The panel's rr- components live in src/shared/ui.
    files: ['src/shared/**/*.{ts,tsx}'],
    rules: {
      'no-restricted-imports': ['error', {
        patterns: [{
          // Any path through a features folder.
          regex: '(^|/)features/',
          message: 'src/shared must not import from src/features: move the shared piece to src/shared (src/shared/ui for components).',
        }],
      }],
    },
  },
  {
    // Exceptions to the rule above: tests in src/shared that check a feature together with a shared piece.
    // - attention-notices.test.ts: publication status texts of features/catalog next to the shared notices
    // - dismissable-pickers.test.tsx: the invoices sort picker (features/invoicing) among the shared pickers
    // - notification-center.test.ts: the jobs page tab (features/operations) a notification opens
    files: ['src/shared/attention-notices.test.ts', 'src/shared/dismissable-pickers.test.tsx', 'src/shared/notification-center.test.ts'],
    rules: { 'no-restricted-imports': 'off' },
  },
  {
    files: ['scripts/**/*.{js,mjs}', 'tests/**/*.mjs', '*.config.{js,ts}', 'vite.config.ts', 'vitest.config.ts'],
    languageOptions: { globals: globals.node },
  },
])
