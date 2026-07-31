using MarketplaceHub.Domain;

namespace MarketplaceHub.Domain.Tests;

public sealed class IdentityModelTests
{
    [Fact]
    public void Restricted_session_states_do_not_imply_active_tenant_access()
    {
        Assert.NotEqual(SessionState.Active, SessionState.PasswordChangeRequired);
        Assert.NotEqual(SessionState.Active, SessionState.MfaChallenge);
    }
}
