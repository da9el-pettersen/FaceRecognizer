using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace FaceRecognizer.Contracts;

/// <summary>
/// Endepunktfilter som krever <c>Authorization: Bearer &lt;token&gt;</c> (sammenligning i konstant tid).
/// Tomt forventet token avviser alle kall.
/// </summary>
public static class BearerTokenFilter
{
    /// <summary>Oppretter filteret.</summary>
    /// <param name="expectedToken">Forventet token.</param>
    public static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> Create(
        string expectedToken) =>
        async (context, next) =>
        {
            var header = context.HttpContext.Request.Headers.Authorization.ToString();
            var supplied = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : "";
            var authorized = expectedToken.Length > 0 && CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),
                SHA256.HashData(Encoding.UTF8.GetBytes(expectedToken)));
            return authorized ? await next(context) : Results.Unauthorized();
        };
}
