using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace SingBoot;

internal static class UpdateHttpClient
{
    public static HttpClient Create(HttpMessageHandler? customHandler)
    {
        EnsureModernTls();
        var handler = customHandler ?? new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseProxy = true,
            Proxy = WebRequest.GetSystemWebProxy()
        };
        var client = new HttpClient(handler, disposeHandler: customHandler is null)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("sing-boot", "1.0"));
        return client;
    }

#pragma warning disable CA5386 // Enable modern TLS on .NET Framework 4.8 without app.config.
    private static void EnsureModernTls()
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        try
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288;
        }
        catch (NotSupportedException)
        {
        }
    }
#pragma warning restore CA5386
}
