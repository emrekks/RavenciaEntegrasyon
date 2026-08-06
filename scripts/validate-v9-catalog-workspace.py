#!/usr/bin/env python3
"""Static acceptance checks for the Ravencia v9 catalog and mapping workspace."""
from __future__ import annotations

from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]

CHECKS: dict[str, tuple[str, list[str]]] = {
    "category-requirements-api": (
        "src/MarketplaceHub.Api/F2/F2Endpoints.cs",
        [
            'MapGet("/catalog/categories/{id:guid}/attribute-requirements"',
            'MapPut("/catalog/categories/{id:guid}/attribute-requirements"',
            'service.GetRequirementsAsync',
            '/catalog/attributes/{id:guid}/values',
        ],
    ),
    "bulk-mapping-api": (
        "src/MarketplaceHub.Api/F2/F2Endpoints.cs",
        [
            'MapGet($"/mappings/{routeType}"',
            'service.ListMappingsAsync',
            'MapDelete($"/mappings/{routeType}',
            'service.DeleteMappingAsync',
        ],
    ),
    "variant-attribute-contract": (
        "src/MarketplaceHub.Application/F2Contracts.cs",
        [
            "CategoryAttributeRequirementView",
            "IReadOnlyList<ProductAttributeCommand>? Attributes = null",
            "AddAttributeValuesAsync",
            "ListMappingsAsync",
            "DeleteMappingAsync",
        ],
    ),
    "variant-attribute-persistence": (
        "src/MarketplaceHub.Infrastructure/Persistence/CatalogService.cs",
        [
            "variant.Attributes ?? []",
            "Assignment(tenantId, product.Id, variants[index].Id, x)",
            "REQUIRED_ATTRIBUTE_MISSING",
            "ValidateAttributeValuesAsync",
            "command.Attributes is not null",
        ],
    ),
    "mapping-workspace": (
        "src/MarketplaceHub.Web/src/F3Pages.tsx",
        [
            "Kategori &amp; özellik eşlemeleri",
            "KategoriRequirementBuilder".replace("Kategori", "Category"),
            "/mappings/attributes?connectionId=",
            "zorunlu özellik eşlendi",
            "Özel değer",
            "+ Seçenek ekle",
            "AttributeValueMappingEditor",
            "Eşlemeyi kaldır",
            "method: 'DELETE'",
        ],
    ),
    "product-workspace": (
        "src/MarketplaceHub.Web/src/F2Pages.tsx",
        [
            "MAX_VARIANTS = 100",
            "attributeValueIds",
            "Varyant başlangıç stoğu",
            "Tümüne uygula",
            "listing-profiles",
            "publication-jobs",
            "Hesaplanan desi",
            "Trendyol yayını için",
        ],
    ),
    "workspace-styles": (
        "src/MarketplaceHub.Web/src/styles.css",
        [
            ".mapping-progress",
            ".variant-bulk-editor",
            ".product-submit-sticky",
            ".media-preview-strip",
        ],
    ),
}

failures: list[str] = []
for name, (relative, required) in CHECKS.items():
    path = ROOT / relative
    if not path.exists():
        failures.append(f"{name}: dosya bulunamadı: {relative}")
        continue
    text = path.read_text(encoding="utf-8")
    missing = [token for token in required if token not in text]
    if missing:
        failures.append(f"{name}: eksik kabul işaretleri: {', '.join(missing)}")
    else:
        print(f"PASS {name}")

node_script = r'''
const fs = require('fs');
const ts = require('/opt/nvm/versions/node/v22.16.0/lib/node_modules/typescript');
const root = process.argv[1];
let failed = false;
for (const file of ['F2Pages.tsx', 'F3Pages.tsx', 'App.tsx', 'F4Pages.tsx', 'CatalogWorkspacePages.test.tsx', 'F3Pages.test.tsx']) {
  const path = `${root}/src/MarketplaceHub.Web/src/${file}`;
  const source = fs.readFileSync(path, 'utf8');
  const result = ts.transpileModule(source, {
    compilerOptions: { jsx: ts.JsxEmit.ReactJSX, target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ESNext },
    reportDiagnostics: true,
    fileName: file,
  });
  const diagnostics = result.diagnostics || [];
  if (diagnostics.length) {
    failed = true;
    console.error(`FAIL ${file}`);
    for (const item of diagnostics) console.error(ts.flattenDiagnosticMessageText(item.messageText, '\n'));
  } else console.log(`PASS syntax ${file}`);
}
process.exit(failed ? 1 : 0);
'''
try:
    subprocess.run(["node", "-e", node_script, str(ROOT)], check=True)
except (subprocess.CalledProcessError, FileNotFoundError) as exc:
    failures.append(f"typescript-syntax: {exc}")


# Lightweight semantic TypeScript check with local module stubs. This catches undefined
# component names and incompatible local props even when npm packages are unavailable.
try:
    import tempfile
    stub = """
declare namespace JSX { interface IntrinsicElements { [elemName: string]: any } interface IntrinsicAttributes { key?: any } }
declare namespace React { type ReactNode = any }
declare module 'react' {
  export type SetStateAction<T> = T | ((previous: T) => T); export type Dispatch<T> = (value: T) => void;
  export function useState<T = undefined>(): [T | undefined, Dispatch<SetStateAction<T | undefined>>];
  export function useState<T>(initial: T | (() => T)): [T, Dispatch<SetStateAction<T>>];
  export function useEffect(effect: () => void | (() => void), deps?: readonly unknown[]): void;
  export function useMemo<T>(factory: () => T, deps: readonly unknown[]): T;
  export type FormEvent<T = any> = any; export type ReactNode = any;
}
declare module 'react/jsx-runtime' { export const jsx: any; export const jsxs: any; export const Fragment: any; }
declare module 'react-router' { export const Link: any; export function useParams<T = Record<string,string|undefined>>(): T; export function useNavigate(): any; }
declare module '@tanstack/react-query' {
  type QueryOptions<T> = { queryKey: readonly unknown[]; queryFn: () => Promise<T>; enabled?: boolean; retry?: any; refetchInterval?: any };
  type QueryResult<T> = { data?: T; error?: unknown; isLoading: boolean; isError: boolean };
  export function useQuery<T>(options: QueryOptions<T>): QueryResult<T>;
  type MutationOptions<TData,TVariables> = { mutationFn: (variables: TVariables) => Promise<TData>; onSuccess?: (data:TData)=>any; onError?: (error:unknown)=>any };
  export function useMutation<TData = unknown, TVariables = void>(options: MutationOptions<TData,TVariables>): { mutate: (value: TVariables) => void; mutateAsync: (value: TVariables) => Promise<TData>; isPending: boolean; isError: boolean };
  export function useQueryClient(): { invalidateQueries: (options: any) => Promise<unknown> };
}
"""
    with tempfile.NamedTemporaryFile("w", suffix=".d.ts", delete=False, encoding="utf-8") as handle:
        handle.write(stub)
        stub_path = handle.name
    web = ROOT / "src" / "MarketplaceHub.Web" / "src"
    subprocess.run([
        "tsc", "--noEmit", "--jsx", "react-jsx", "--target", "ES2022", "--module", "ESNext",
        "--moduleResolution", "Bundler", "--skipLibCheck", "--strict", "false", stub_path,
        str(web / "api.ts"), str(web / "F2Pages.tsx"), str(web / "F3Pages.tsx")
    ], check=True)
    print("PASS semantic TypeScript catalog workspaces")
except FileNotFoundError:
    print("SKIP semantic TypeScript check: global tsc unavailable")
except subprocess.CalledProcessError as exc:
    failures.append(f"typescript-semantic: {exc}")
finally:
    try:
        Path(stub_path).unlink(missing_ok=True)
    except NameError:
        pass

if failures:
    print("\nV9 CATALOG ACCEPTANCE FAILED", file=sys.stderr)
    for failure in failures:
        print(f"- {failure}", file=sys.stderr)
    raise SystemExit(1)

print(f"\nPASS {len(CHECKS)} v9 catalog acceptance groups")
