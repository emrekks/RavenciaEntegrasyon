using MarketplaceHub.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace MarketplaceHub.Api.Security;

// Every password check (sign-in, each re-authentication, step-ups and the password change) counts towards
// the same limits: SignInThrottle blocks the sending network from the account after five wrong passwords
// (longer each time), and the account-wide Identity lockout is the ceiling for guesses from many networks.
// A stolen session therefore cannot be used to guess the password through the re-authentication prompts.
public static class PasswordCheck
{
    // Without the request the network is unknown: only the account-wide lockout applies.
    public static Task<bool> VerifyAsync(UserManager<ApplicationUser> users, ApplicationUser? user, string? password) =>
        VerifyCoreAsync(null, users, user, password);

    public static Task<bool> VerifyAsync(HttpContext http, UserManager<ApplicationUser> users, ApplicationUser? user, string? password) =>
        VerifyCoreAsync(http, users, user, password);

    // True while the account may not try a password from this request's network.
    public static async Task<bool> IsBlockedAsync(HttpContext? http, UserManager<ApplicationUser> users, ApplicationUser user) =>
        await users.IsLockedOutAsync(user)
        || http?.RequestServices.GetService<SignInThrottle>() is { } throttle && throttle.IsBlocked(user.Id, ClientNetwork.PartitionKey(http));

    // Counts a wrong password or code; true when the account or this network is now blocked.
    public static async Task<bool> RegisterFailureAsync(HttpContext? http, UserManager<ApplicationUser> users, ApplicationUser user)
    {
        await users.AccessFailedAsync(user);
        var networkBlocked = http?.RequestServices.GetService<SignInThrottle>() is { } throttle && throttle.RegisterFailure(user.Id, ClientNetwork.PartitionKey(http));
        return networkBlocked || await users.IsLockedOutAsync(user);
    }

    public static async Task RegisterSuccessAsync(HttpContext? http, UserManager<ApplicationUser> users, ApplicationUser user)
    {
        await users.ResetAccessFailedCountAsync(user);
        if (http?.RequestServices.GetService<SignInThrottle>() is { } throttle) throttle.RegisterSuccess(user.Id, ClientNetwork.PartitionKey(http));
    }

    private static async Task<bool> VerifyCoreAsync(HttpContext? http, UserManager<ApplicationUser> users, ApplicationUser? user, string? password)
    {
        if (user is null || string.IsNullOrWhiteSpace(password)) return false;
        if (await IsBlockedAsync(http, users, user))
        {
            DummyHash.Verify(users, password);
            return false;
        }
        if (!await users.CheckPasswordAsync(user, password))
        {
            await RegisterFailureAsync(http, users, user);
            return false;
        }
        await RegisterSuccessAsync(http, users, user);
        return true;
    }
}

// A refused attempt that never reaches the real hash (unknown account, inactive, blocked) still pays for
// one hash verification, so its answer takes as long as a wrong password and does not reveal which it was.
public static class DummyHash
{
    private static readonly ApplicationUser Nobody = new() { Id = Guid.Empty, DisplayName = "nobody", UserName = "nobody" };
    private static string? hash;

    public static void Verify(UserManager<ApplicationUser> users, string? password)
    {
        hash ??= users.PasswordHasher.HashPassword(Nobody, Guid.NewGuid().ToString("N"));
        users.PasswordHasher.VerifyHashedPassword(Nobody, hash, password ?? string.Empty);
    }
}
