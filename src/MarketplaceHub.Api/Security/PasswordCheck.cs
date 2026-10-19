using MarketplaceHub.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace MarketplaceHub.Api.Security;

// Every password check (sign-in and each re-authentication) counts towards the same lockout:
// five wrong passwords lock the account for fifteen minutes, so a stolen session cannot be used
// to guess the password through the re-authentication prompts.
public static class PasswordCheck
{
    public static async Task<bool> VerifyAsync(UserManager<ApplicationUser> users, ApplicationUser? user, string? password)
    {
        if (user is null || string.IsNullOrWhiteSpace(password) || await users.IsLockedOutAsync(user)) return false;
        if (!await users.CheckPasswordAsync(user, password))
        {
            await users.AccessFailedAsync(user);
            return false;
        }
        await users.ResetAccessFailedCountAsync(user);
        return true;
    }
}
