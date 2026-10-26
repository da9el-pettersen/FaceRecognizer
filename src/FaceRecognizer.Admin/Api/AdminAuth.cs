using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace FaceRecognizer.Admin.Api;

/// <summary>
/// Innstillinger for hvem som slipper inn i Admin-API-et.
/// </summary>
/// <param name="Token">Delt token for direkte kall (Compose, LAN, skript).</param>
/// <param name="TrustIngress">Godta HA-brukere som kommer via Supervisor ingress uten token.</param>
/// <param name="AllowedUsers">Tillatte HA-brukere (brukernavn eller bruker-ID). Tom liste betyr alle innloggede HA-brukere.</param>
public sealed record AdminAuthOptions(string Token, bool TrustIngress, IReadOnlyList<string> AllowedUsers)
{
    /// <summary>Gateway som Supervisor bruker mot appen. Ingress-kall kommer kun herfra.</summary>
    public static readonly IPAddress SupervisorGateway = IPAddress.Parse("172.30.32.2");

    /// <summary>Leser <c>Admin:*</c>. Ingress stoles på som standard kun når tjenesten kjører som HA-app.</summary>
    /// <param name="configuration">Konfigurasjon.</param>
    /// <param name="runsAsAddon">Om tjenesten kjører som HA-app.</param>
    public static AdminAuthOptions From(IConfiguration configuration, bool runsAsAddon)
    {
        var token = configuration["Admin:Token"];
        var trustIngress = configuration.GetValue<bool?>("Admin:TrustIngress") ?? runsAsAddon;
        if (string.IsNullOrWhiteSpace(token) && !trustIngress)
        {
            throw new InvalidOperationException("Admin:Token må settes (eller Admin:TrustIngress=true).");
        }

        var allowed = (configuration["Admin:AllowedUsers"] ?? "")
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new AdminAuthOptions(token ?? "", trustIngress, allowed);
    }
}

/// <summary>Resultatet av autentiseringen.</summary>
/// <param name="Authorized">Om kallet slippes inn.</param>
/// <param name="User">HA-brukernavn eller «token» for direkte kall.</param>
public readonly record struct AdminIdentity(bool Authorized, string? User);

/// <summary>Avgjør om et kall er autorisert: enten HA-bruker via ingress, eller gyldig bearer-token.</summary>
public static class AdminAuth
{
    /// <summary>Vurderer kallet.</summary>
    /// <param name="request">Innkommende forespørsel.</param>
    /// <param name="remoteAddress">Avsenderadresse (må være Supervisor-gatewayen for ingress).</param>
    /// <param name="options">Innstillinger.</param>
    public static AdminIdentity Evaluate(HttpRequest request, IPAddress? remoteAddress, AdminAuthOptions options)
    {
        if (options.TrustIngress && IsSupervisor(remoteAddress))
        {
            // Supervisor setter og fjerner disse headerne selv, så de kan stoles på bare fra gatewayen.
            var id = request.Headers["X-Remote-User-Id"].ToString();
            var name = request.Headers["X-Remote-User-Name"].ToString();
            if (id.Length > 0)
            {
                var display = name.Length > 0 ? name : id;
                var allowed = options.AllowedUsers.Count == 0
                    || options.AllowedUsers.Contains(id, StringComparer.OrdinalIgnoreCase)
                    || (name.Length > 0 && options.AllowedUsers.Contains(name, StringComparer.OrdinalIgnoreCase));
                if (allowed)
                {
                    return new AdminIdentity(true, display);
                }
            }
        }

        var header = request.Headers.Authorization.ToString();
        var supplied = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : "";
        var tokenOk = options.Token.Length > 0 && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),
            SHA256.HashData(Encoding.UTF8.GetBytes(options.Token)));
        return tokenOk ? new AdminIdentity(true, "token") : new AdminIdentity(false, null);
    }

    private static bool IsSupervisor(IPAddress? address) =>
        address is not null && address.MapToIPv4().Equals(AdminAuthOptions.SupervisorGateway);

    /// <summary>Endepunktfilter som bruker <see cref="Evaluate"/>. Brukeren legges i <c>HttpContext.Items["user"]</c>.</summary>
    /// <param name="options">Innstillinger.</param>
    public static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> CreateFilter(
        AdminAuthOptions options) =>
        async (context, next) =>
        {
            var http = context.HttpContext;
            var identity = Evaluate(http.Request, http.Connection.RemoteIpAddress, options);
            if (!identity.Authorized)
            {
                return Results.Unauthorized();
            }

            http.Items["user"] = identity.User;
            return await next(context);
        };
}
