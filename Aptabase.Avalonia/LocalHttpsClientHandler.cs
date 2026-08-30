using System.Net.Http;
using System.Net.Security;
using Microsoft.Extensions.Logging;

namespace Aptabase.Avalonia;

public sealed class LocalHttpsClientHandler : DelegatingHandler
{
    public LocalHttpsClientHandler(ILogger? logger = null)
    {
        InnerHandler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (sender, cert, _, errors) =>
                {
                    if (cert?.Issuer?.Contains("CN=mkcert", StringComparison.OrdinalIgnoreCase) is true)
                    {
                        AptabaseLogging.Log(logger, LogLevel.Debug, nameof(LocalHttpsClientHandler), "Validated dev certificate (CN=mkcert).");
                        return true;
                    }

                    if (sender is HttpRequestMessage { RequestUri.Host: "localhost" or "127.0.0.1" })
                    {
                        AptabaseLogging.Log(logger, LogLevel.Debug, nameof(LocalHttpsClientHandler), "Validated local host target.");
                        return true;
                    }

                    if (errors != SslPolicyErrors.None)
                    {
                        AptabaseLogging.Log(logger, LogLevel.Warning, nameof(LocalHttpsClientHandler), $"SSL Validation Error: {errors}");
                    }

                    return errors == SslPolicyErrors.None;
                }
            }
        };
    }
}