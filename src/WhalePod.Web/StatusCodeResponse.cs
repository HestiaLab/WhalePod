namespace WhalePod.Web;

public static class StatusCodeResponse
{
    public static void Defer(HttpContext? http, int statusCode)
    {
        if (http is null)
        {
            return;
        }

        var response = http.Response;
        response.OnStarting(() =>
        {
            response.StatusCode = statusCode;
            return Task.CompletedTask;
        });
    }

    public static void NotFound(HttpContext? http)
    {
        Defer(http, StatusCodes.Status404NotFound);
    }
}