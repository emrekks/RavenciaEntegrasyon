using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The order summary's per-key lock: one holder per key at a time, and a key leaves the lock table when no one holds or
// waits for it (a cancelled waiter too), so the table does not grow with every key ever asked.
public sealed class KeyedAsyncLockTests
{
    [Fact]
    public async Task A_key_leaves_when_its_last_user_leaves()
    {
        var gates = new KeyedAsyncLock();
        for (var i = 0; i < 100; i++)
            using (await gates.AcquireAsync($"key-{i}", CancellationToken.None))
                Assert.Equal(1, gates.Count);
        Assert.Equal(0, gates.Count);
    }

    [Fact]
    public async Task Two_users_of_one_key_take_turns_and_a_cancelled_waiter_leaves()
    {
        var gates = new KeyedAsyncLock();
        var first = await gates.AcquireAsync("key", CancellationToken.None);
        var second = gates.AcquireAsync("key", CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        var cancelled = gates.AcquireAsync("key", cancel.Token);
        await Task.Delay(50);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, gates.Count);

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        first.Dispose();
        first.Dispose();
        var held = await second;
        Assert.Equal(1, gates.Count);
        var third = gates.AcquireAsync("key", CancellationToken.None);
        await Task.Delay(50);
        Assert.False(third.IsCompleted);
        held.Dispose();
        (await third).Dispose();
        Assert.Equal(0, gates.Count);
    }
}
