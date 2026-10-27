using System.Net;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Mvc.Testing;

namespace WhalePod.Tests.Integration;

internal static class WebFormTestSupport
{
    public static HttpClient CreateClient(WhalePodWebFactory factory)
    {
        return factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost")
        });
    }

    public static async Task<FormContext> LoadFormAsync(
        HttpClient client,
        string path,
        string sessionCookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", sessionCookie);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        var tokenMatch = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"");
        Assert.True(tokenMatch.Success, $"Antiforgery token missing on {path}.");
        var csrf = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(value => value.Split(';', 2)[0])
                .FirstOrDefault(value => value.StartsWith("whalepod_csrf=", StringComparison.Ordinal))
            : null;
        Assert.NotNull(csrf);
        return new FormContext(WebUtility.HtmlDecode(tokenMatch.Groups[1].Value), csrf!);
    }

    public static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient client,
        string path,
        Dictionary<string, string> form,
        string sessionCookie,
        string csrfCookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.Add("Cookie", $"{sessionCookie}; {csrfCookie}");
        return await client.SendAsync(request);
    }

    public sealed record FormContext(string Token, string CsrfCookie);
}