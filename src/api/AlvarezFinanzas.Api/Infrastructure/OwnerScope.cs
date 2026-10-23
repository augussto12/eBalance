using System.Security.Claims;
using AlvarezFinanzas.Infrastructure.Auth;

namespace AlvarezFinanzas.Infrastructure;

/// <summary>
/// Propietario que usan los filtros globales de EF (<see cref="AppDbContext.OwnerFilter"/>). Es una defensa adicional:
/// cada endpoint sigue filtrando explícitamente por el propietario de la sesión.
/// </summary>
public interface ICurrentOwner
{
    /// <summary>Id del propietario, o null si no hay una sesión completa (con MFA) en el request actual.</summary>
    Guid? OwnerId { get; }
}

/// <summary>
/// Toma el propietario del principal de la sesión completa (claim amr=mfa), igual que la política financiera. Una
/// preautenticación, un request anónimo, la CLI o un proceso en segundo plano no tienen propietario.
/// </summary>
public sealed class SessionOwner(IHttpContextAccessor http) : ICurrentOwner
{
    public Guid? OwnerId
    {
        get
        {
            var user = http.HttpContext?.User;
            if (user?.Identity?.IsAuthenticated != true || !user.HasClaim(AuthConstants.ClaimAmr, "mfa")) return null;
            return Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
        }
    }
}
