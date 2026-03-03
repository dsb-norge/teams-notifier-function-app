using System.Text;
using Microsoft.AspNetCore.Http;

namespace TeamsNotificationBot.Tests.Helpers;

public static class HttpRequestHelper
{
    public static HttpRequest CreatePostRequest(
        string? body = null,
        string contentType = "application/json",
        Dictionary<string, string>? headers = null)
    {
        var context = new DefaultHttpContext();
        var request = context.Request;
        request.Method = "POST";
        request.ContentType = contentType;

        // Set a correlation ID to simulate middleware behavior
        context.Items["CorrelationId"] = "test-correlation-id";

        if (body != null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            request.Body = new MemoryStream(bytes);
            request.ContentLength = bytes.Length;
        }

        if (headers != null)
        {
            foreach (var (key, value) in headers)
            {
                request.Headers[key] = value;
            }
        }

        return request;
    }

    public static HttpRequest CreateGetRequest(
        Dictionary<string, string>? headers = null)
    {
        var context = new DefaultHttpContext();
        var request = context.Request;
        request.Method = "GET";

        // Set a correlation ID to simulate middleware behavior
        context.Items["CorrelationId"] = "test-correlation-id";

        if (headers != null)
        {
            foreach (var (key, value) in headers)
            {
                request.Headers[key] = value;
            }
        }

        return request;
    }
}
