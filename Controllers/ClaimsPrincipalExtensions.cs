using System.Security.Claims;

public static class ClaimsPrincipalExtensions
{
    // The signed-in account; only valid on [Authorize] endpoints
    public static int AccountId(this ClaimsPrincipal user) =>
        int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
