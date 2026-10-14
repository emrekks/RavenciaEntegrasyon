using System.Buffers;
using System.Security.Cryptography;

namespace MarketplaceHub.Api.Marketplace;

internal static class WebhookBodyReader
{
    internal const int MaximumBytes = 10 * 1024 * 1024;
    private const int InitialBufferBytes = 80 * 1024;

    internal static async Task<WebhookBodyReadResult> ReadAsync(Stream body, long? contentLength, CancellationToken cancellationToken)
    {
        if (contentLength > MaximumBytes) return WebhookBodyReadResult.TooLargeResult;

        var requestedCapacity = contentLength is > 0
            ? Math.Min(MaximumBytes, Math.Max(InitialBufferBytes, checked((int)contentLength.Value)))
            : InitialBufferBytes;
        var buffer = ArrayPool<byte>.Shared.Rent(requestedCapacity);
        var length = 0;
        var transferred = false;
        try
        {
            while (true)
            {
                if (length == requestedCapacity)
                {
                    if (requestedCapacity == MaximumBytes) break;
                    var nextCapacity = Math.Min(MaximumBytes, Math.Max(requestedCapacity * 2, length + 1));
                    var nextBuffer = ArrayPool<byte>.Shared.Rent(nextCapacity);
                    buffer.AsSpan(0, length).CopyTo(nextBuffer);
                    CryptographicOperations.ZeroMemory(buffer.AsSpan(0, length));
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = nextBuffer;
                    requestedCapacity = nextCapacity;
                }

                var read = await body.ReadAsync(buffer.AsMemory(length, requestedCapacity - length), cancellationToken);
                if (read == 0)
                {
                    var rentedBody = new RentedWebhookBody(buffer, length);
                    transferred = true;
                    return WebhookBodyReadResult.Success(rentedBody);
                }
                length += read;
            }

            var probe = ArrayPool<byte>.Shared.Rent(1);
            try
            {
                var extra = await body.ReadAsync(probe.AsMemory(0, 1), cancellationToken);
                if (extra > 0) return WebhookBodyReadResult.TooLargeResult;
                var rentedBody = new RentedWebhookBody(buffer, length);
                transferred = true;
                return WebhookBodyReadResult.Success(rentedBody);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(probe);
                ArrayPool<byte>.Shared.Return(probe);
            }
        }
        finally
        {
            if (!transferred)
            {
                CryptographicOperations.ZeroMemory(buffer.AsSpan(0, length));
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}

internal readonly record struct WebhookBodyReadResult(RentedWebhookBody? Body, bool TooLarge)
{
    internal static WebhookBodyReadResult TooLargeResult => new(null, true);
    internal static WebhookBodyReadResult Success(RentedWebhookBody body) => new(body, false);
}

internal sealed class RentedWebhookBody(byte[] rentedBuffer, int length) : IDisposable
{
    private byte[]? buffer = rentedBuffer;

    internal ReadOnlyMemory<byte> Memory => buffer is { } current
        ? current.AsMemory(0, length)
        : throw new ObjectDisposedException(nameof(RentedWebhookBody));

    public void Dispose()
    {
        var current = Interlocked.Exchange(ref buffer, null);
        if (current is null) return;
        CryptographicOperations.ZeroMemory(current.AsSpan(0, length));
        ArrayPool<byte>.Shared.Return(current);
    }
}
