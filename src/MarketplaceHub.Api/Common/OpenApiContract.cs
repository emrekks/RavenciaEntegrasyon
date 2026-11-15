using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace MarketplaceHub.Api.Common;

// Keeps the OpenAPI description stable and limited to the panel's API: a fixed title and version, no server list (it
// would name whichever host generated it), the host-only /internal routes left out, and paths in ordinal order so the
// contract file changes only when a route does.
public sealed class OpenApiContract : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo { Title = "Ravencia Marketplace Hub API", Version = "v1" };
        document.Servers = [];
        var paths = new OpenApiPaths();
        foreach (var (path, item) in document.Paths.Where(entry => entry.Key.StartsWith("/api/v1/", StringComparison.Ordinal)).OrderBy(entry => entry.Key, StringComparer.Ordinal))
            paths.Add(path, item);
        document.Paths = paths;
        return Task.CompletedTask;
    }
}
