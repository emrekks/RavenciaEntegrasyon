using MarketplaceHub.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace MarketplaceHub.Api.Security;

// Every password and second-factor check (sign-in, each re-authentication, step-ups and the password
// change) counts towards the same limits: SignInThrottle blocks the sending network from the account
// after five failures (longer each time), and the account-wide ceiling (SignInThrottle's window and the
// Identity lockout) slows guesses spread over many networks without locking out the owner's trusted or
// untouched networks, or an already signed-in session. A stolen session therefore cannot be used to
// guess the password through the re-authentication prompts, and attackers elsewhere cannot end the
// owner's session or step-ups.
//
// Counters are reset only by a complete sign-in or step-up (RegisterSuccessAsync, called by the
// endpoint once the password *and* the second factor passed), never by a correct password alone.
public static class PasswordCheck
{
    public readonly record struct FailureOutcome(bool NetworkBlocked, bool AccountUnderAttack)
    {
        public bool Any => NetworkBlocked || AccountUnderAttack;
    }

    // A password re-check inside a signed-in session: only this network's own block applies, failures
    // count, and a correct password resets nothing (the caller does that after the second factor).
    public static async Task<bool> VerifyAsync(HttpContext http, UserManager<ApplicationUser> users, ApplicationUser? user, string? password)
    {
        if (user is null || string.IsNullOrWhiteSpace(password)) return false;
        if (await IsBlockedAsync(http, users, user, signedIn: true))
        {
            DummyHash.Verify(users, password);
            return false;
        }
        if (!await users.CheckPasswordAsync(user, password))
        {
            await RegisterFailureAsync(http, users, user);
            return false;
        }
        return true;
    }

    // True while the account may not try a password or code from this request's network. A signed-in
    // session (signedIn) is held back only by its own network's block, never by failures elsewhere.
    public static async Task<bool> IsBlockedAsync(HttpContext? http, UserManager<ApplicationUser> users, ApplicationUser user, bool signedIn = false)
    {
        var throttle = http?.RequestServices.GetService<SignInThrottle>();
        var network = http is null ? null : ClientNetwork.PartitionKey(http);
        if (throttle is not null && network is not null && throttle.IsBlocked(user.Id, network)) return true;
        if (signedIn) return false;
        var underAttack = throttle?.IsAccountUnderAttack(user.Id) == true || await users.IsLockedOutAsync(user);
        if (!underAttack) return false;
        return throttle is null || network is null || throttle.RefusesWhileUnderAttack(user.Id, network);
    }

    // Counts a wrong password or code for the account and this network.
    public static async Task<FailureOutcome> RegisterFailureAsync(HttpContext? http, UserManager<ApplicationUser> users, ApplicationUser user)
    {
        await users.AccessFailedAsync(user);
        var throttle = http?.RequestServices.GetService<SignInThrottle>();
        var networkBlocked = throttle is not null && http is not null && throttle.RegisterFailure(user.Id, ClientNetwork.PartitionKey(http));
        var underAttack = throttle?.IsAccountUnderAttack(user.Id) == true || await users.IsLockedOutAsync(user);
        return new FailureOutcome(networkBlocked, underAttack);
    }

    // A complete sign-in or step-up: password and (where enabled) the second factor passed.
    public static async Task RegisterSuccessAsync(HttpContext? http, UserManager<ApplicationUser> users, ApplicationUser user)
    {
        // An Identity lockout that is running stays: it keeps slowing guesses from untrusted networks.
        if (await users.GetAccessFailedCountAsync(user) > 0) await users.ResetAccessFailedCountAsync(user);
        if (http?.RequestServices.GetService<SignInThrottle>() is { } throttle) throttle.RegisterSuccess(user.Id, ClientNetwork.PartitionKey(http));
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
