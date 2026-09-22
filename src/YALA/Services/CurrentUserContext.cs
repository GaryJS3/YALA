using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace YALA.Services;

/// <summary>
/// Resolves the signed-in user for both normal HTTP API requests and the
/// remaining static/interactive server components. API requests use the
/// authenticated HttpContext directly; server components fall back to their
/// cascading authentication state.
/// </summary>
public sealed class CurrentUserContext(
    IHttpContextAccessor httpContextAccessor,
    AuthenticationStateProvider authenticationStateProvider)
{
    public async Task<string?> GetUserIdAsync()
    {
        var httpUser = httpContextAccessor.HttpContext?.User;
        var httpUserId = httpUser?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (httpUser?.Identity?.IsAuthenticated == true && httpUserId is not null)
        {
            return httpUserId;
        }

        var state = await authenticationStateProvider.GetAuthenticationStateAsync();
        return state.User.FindFirstValue(ClaimTypes.NameIdentifier);
    }
}
