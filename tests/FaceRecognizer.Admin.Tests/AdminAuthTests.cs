using System.Net;
using FaceRecognizer.Admin.Api;
using Microsoft.AspNetCore.Http;

namespace FaceRecognizer.Admin.Tests;

public class AdminAuthTests
{
    private static readonly IPAddress Supervisor = IPAddress.Parse("172.30.32.2");
    private static readonly IPAddress Lan = IPAddress.Parse("192.168.1.50");

    private static HttpRequest Request(string? bearer = null, string? userId = null, string? userName = null)
    {
        var request = new DefaultHttpContext().Request;
        if (bearer is not null) request.Headers.Authorization = "Bearer " + bearer;
        if (userId is not null) request.Headers["X-Remote-User-Id"] = userId;
        if (userName is not null) request.Headers["X-Remote-User-Name"] = userName;
        return request;
    }

    private static AdminAuthOptions Options(string token = "hemmelig", bool trust = true, params string[] allowed) =>
        new(token, trust, allowed);

    [Fact]
    public void IngressBruker_FraSupervisor_SlippesInnUtenToken()
    {
        var result = AdminAuth.Evaluate(Request(userId: "u1", userName: "kari"), Supervisor, Options());
        Assert.True(result.Authorized);
        Assert.Equal("kari", result.User);
    }

    [Fact]
    public void IngressHeadere_FraAndreAdresser_IgnoreresOgKreverToken()
    {
        var result = AdminAuth.Evaluate(Request(userId: "u1", userName: "kari"), Lan, Options());
        Assert.False(result.Authorized);
    }

    [Fact]
    public void IngressHeadere_NarTrustIngressErAv_Avvises()
    {
        var result = AdminAuth.Evaluate(Request(userId: "u1"), Supervisor, Options(trust: false));
        Assert.False(result.Authorized);
    }

    [Fact]
    public void FraSupervisor_UtenBrukerHeader_Avvises()
    {
        Assert.False(AdminAuth.Evaluate(Request(), Supervisor, Options()).Authorized);
    }

    [Fact]
    public void AllowedUsers_BegrenserPaaBrukernavnEllerId()
    {
        var options = Options(allowed: ["kari", "id-ola"]);
        Assert.True(AdminAuth.Evaluate(Request(userId: "x", userName: "KARI"), Supervisor, options).Authorized);
        Assert.True(AdminAuth.Evaluate(Request(userId: "id-ola"), Supervisor, options).Authorized);
        Assert.False(AdminAuth.Evaluate(Request(userId: "y", userName: "per"), Supervisor, options).Authorized);
    }

    [Fact]
    public void Token_FungererFraAlleAdresser_OgFeilTokenAvvises()
    {
        Assert.True(AdminAuth.Evaluate(Request("hemmelig"), Lan, Options()).Authorized);
        Assert.False(AdminAuth.Evaluate(Request("feil"), Lan, Options()).Authorized);
    }

    [Fact]
    public void TomtToken_AkseptererAldriBearer()
    {
        Assert.False(AdminAuth.Evaluate(Request(""), Lan, Options(token: "")).Authorized);
    }

    [Fact]
    public void IPv4MappetIPv6_FraSupervisor_GodtasSomIngress()
    {
        var result = AdminAuth.Evaluate(Request(userId: "u1"), Supervisor.MapToIPv6(), Options());
        Assert.True(result.Authorized);
    }
}
