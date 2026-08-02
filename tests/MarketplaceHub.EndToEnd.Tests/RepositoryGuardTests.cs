namespace MarketplaceHub.EndToEnd.Tests;

public sealed class RepositoryGuardTests
{
    [Fact]
    public void Domain_has_no_project_or_package_dependencies()
    {
        var root = FindRoot(); var project = File.ReadAllText(Path.Combine(root, "src", "MarketplaceHub.Domain", "MarketplaceHub.Domain.csproj"));
        Assert.DoesNotContain("PackageReference", project, StringComparison.Ordinal);
        Assert.DoesNotContain("ProjectReference", project, StringComparison.Ordinal);
    }

    [Fact]
    public void F6A_web_surface_has_only_approved_routes_and_no_later_phase_menu()
    {
        var root = FindRoot(); var source = File.ReadAllText(Path.Combine(root, "src", "MarketplaceHub.Web", "src", "App.tsx"));
        foreach (var required in new[] { "/products", "/products/new", "/products/:id", "/catalog/categories", "/catalog/brands", "/catalog/attributes", "/imports", "/imports/:id", "/inventory", "/orders", "/orders/:id", "/shipments", "/returns", "/returns/:id", "/integrations", "/integrations/:id", "/mappings/categories", "/mappings/attributes", "/invoices", "/invoices/:id", "/settings/billing" }) Assert.Contains(required, source, StringComparison.Ordinal);
        Assert.Contains("Shopify", source, StringComparison.Ordinal);
        Assert.Contains("Hepsiburada", source, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "/reports", "/operations", "/tenants", "/users", "N11", "Pazarama" }) Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Production_delivery_is_public_tls_and_digest_only()
    {
        var root = FindRoot();
        var caddy = File.ReadAllText(Path.Combine(root, "deploy", "caddy", "Caddyfile.production"));
        var compose = File.ReadAllText(Path.Combine(root, "deploy", "compose", "compose.production.yaml"));
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "publish-release-images.yml"));

        Assert.Contains("{$MARKETPLACEHUB_SITE_ADDRESS}", caddy, StringComparison.Ordinal);
        Assert.DoesNotContain("tls internal", caddy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("disable_redirects", caddy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MARKETPLACEHUB_APP_IMAGE:?set immutable application image with digest", compose, StringComparison.Ordinal);
        Assert.Contains("MARKETPLACEHUB_EDGE_IMAGE:?set immutable edge image with digest", compose, StringComparison.Ordinal);
        Assert.Contains("Dockerfile.production", workflow, StringComparison.Ordinal);
        Assert.Contains("name@sha256", workflow, StringComparison.Ordinal);
        Assert.Contains("runs-on: ubuntu-24.04", workflow, StringComparison.Ordinal);
        Assert.Contains("actions/checkout@d23441a48e516b6c34aea4fa41551a30e30af803", workflow, StringComparison.Ordinal);
        Assert.Contains("actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68", workflow, StringComparison.Ordinal);
        Assert.Contains("actions/setup-node@48b55a011bda9f5d6aeb4c2d9c7362e8dae4041e", workflow, StringComparison.Ordinal);
        Assert.Contains("docker/setup-buildx-action@bb05f3f5519dd87d3ba754cc423b652a5edd6d2c", workflow, StringComparison.Ordinal);
        Assert.Contains("version: v0.34.1", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet test MarketplaceHub.sln --no-build --no-restore", workflow, StringComparison.Ordinal);
        Assert.Contains("npm run build", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain(":latest", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ubuntu-latest", workflow, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ubuntu_transfer_scripts_are_fail_closed_and_do_not_delete_volumes()
    {
        var root = FindRoot();
        var initializer = File.ReadAllText(Path.Combine(root, "deploy", "scripts", "initialize-deployment.sh"));
        var deployment = File.ReadAllText(Path.Combine(root, "deploy", "scripts", "deploy.sh"));
        var installer = File.ReadAllText(Path.Combine(root, "deploy", "scripts", "install-marketplacehub.sh"));
        var runbook = File.ReadAllText(Path.Combine(root, "docs", "runbooks", "ubuntu-server-deployment.md"));

        Assert.Contains("@sha256:[0-9a-f]{64}", initializer, StringComparison.Ordinal);
        Assert.Contains("will not overwrite", initializer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("config --quiet", deployment, StringComparison.Ordinal);
        Assert.Contains("pull postgres migrate api worker caddy", deployment, StringComparison.Ordinal);
        Assert.Contains("Bootstrap__Enabled=true", deployment, StringComparison.Ordinal);
        Assert.Contains("Ubuntu Server 24.04 LTS", installer, StringComparison.Ordinal);
        Assert.Contains("docker-compose-linux-x86_64", installer, StringComparison.Ordinal);
        Assert.Contains("6c964d9655cd629ef43c5dc75d9612c2da319237debee54a7aef217e9f362b88", installer, StringComparison.Ordinal);
        Assert.Contains("linux/amd64", installer, StringComparison.Ordinal);
        Assert.Contains("systemctl is-enabled --quiet docker", installer, StringComparison.Ordinal);
        Assert.Contains("systemctl is-active --quiet docker", installer, StringComparison.Ordinal);
        Assert.Contains("--deploy --bootstrap", runbook, StringComparison.Ordinal);
        var specification = Path.Combine(root, "Ravencia_Entegrasyon_v3_3_Nihai_Uygulama_Surumu.pdf");
        Assert.True(File.Exists(specification));
        Assert.Equal("AB7E5D26497EDC6D24E8CE0E7111CF44BB782819CD047C93DCBEE7E401BE3F94", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(specification))));
        Assert.True(File.Exists(Path.Combine(root, "docs", "adr", "ADR-012-ubuntu-server-container-runtime.md")));
        Assert.False(File.Exists(Path.Combine(root, "deploy", "scripts", "Install-MarketplaceHub.ps1")));
        Assert.False(File.Exists(Path.Combine(root, "deploy", "scripts", "Initialize-VpsDeployment.ps1")));
        Assert.False(File.Exists(Path.Combine(root, "deploy", "scripts", "Invoke-VpsDeployment.ps1")));
        Assert.False(File.Exists(Path.Combine(root, "docs", "runbooks", "vps-transfer.md")));
        Assert.DoesNotContain("down -v", initializer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("down -v", deployment, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("down -v", installer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(":latest", initializer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(":latest", deployment, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(":latest", installer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WSL", installer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Docker Desktop", installer, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRoot() { var path = AppContext.BaseDirectory; while (!File.Exists(Path.Combine(path, "MarketplaceHub.sln"))) path = Directory.GetParent(path)?.FullName ?? throw new InvalidOperationException("Root not found"); return path; }
}
