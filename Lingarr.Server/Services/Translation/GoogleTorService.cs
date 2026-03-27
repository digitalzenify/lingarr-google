using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Lingarr.Server.Exceptions;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models;
using Lingarr.Server.Services.Translation.Base;

namespace Lingarr.Server.Services.Translation;

/// <summary>
/// Translation service that routes all requests to Google Translate through a Tor SOCKS5 proxy.
/// Under no circumstances will a request be made to Google's servers without Tor active and confirmed working.
/// </summary>
public class GoogleTorService : BaseLanguageService, IDisposable
{
    // Defaults; can be overridden via TOR_SOCKS_HOST / TOR_SOCKS_PORT / TOR_CONTROL_HOST / TOR_CONTROL_PORT env vars
    private readonly string _torSocksHost;
    private readonly int _torSocksPort;
    private readonly string _torControlHost;
    private readonly int _torControlPort;

    private const string IpCheckUrl = "https://api.ipify.org";
    private const string TranslateUrlTemplate =
        "https://translate.googleapis.com/translate_a/single?client=gtx&sl={0}&tl={1}&dt=t&q={2}";
    private const int TorCircuitRetries = 3;
    private const int NewCircuitWaitSeconds = 3;

    // Reusable HTTP clients — one through Tor, one direct (for IP verification only)
    private readonly HttpClient _torHttpClient;
    private readonly HttpClient _directHttpClient;

    // Cache Tor IP-verification result to avoid overhead on every request
    private string? _cachedTorExitIp;
    private DateTime _torVerificationExpiry = DateTime.MinValue;
    private static readonly TimeSpan VerificationCacheDuration = TimeSpan.FromMinutes(5);

    /// <inheritdoc />
    public override string? ModelName => null;

    public GoogleTorService(
        ISettingService settings,
        ILogger<GoogleTorService> logger,
        LanguageCodeService languageCodeService)
        : base(settings, logger, languageCodeService, "/app/Statics/google_languages.json")
    {
        _torSocksHost = Environment.GetEnvironmentVariable("TOR_SOCKS_HOST") ?? "tor";
        _torSocksPort = int.TryParse(Environment.GetEnvironmentVariable("TOR_SOCKS_PORT"), out var sp) ? sp : 9050;
        _torControlHost = Environment.GetEnvironmentVariable("TOR_CONTROL_HOST") ?? "tor";
        _torControlPort = int.TryParse(Environment.GetEnvironmentVariable("TOR_CONTROL_PORT"), out var cp) ? cp : 9051;

        _torHttpClient = CreateTorHttpClient();
        _directHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <inheritdoc />
    public override async Task<string> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        List<string>? contextLinesBefore,
        List<string>? contextLinesAfter,
        CancellationToken cancellationToken)
    {
        for (var circuit = 1; circuit <= TorCircuitRetries; circuit++)
        {
            try
            {
                await VerifyTorAsync(cancellationToken);
                return await TranslateViaTorAsync(text, sourceLanguage, targetLanguage, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TranslationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Translation attempt {Circuit}/{MaxCircuits} failed. Requesting new Tor circuit.",
                    circuit, TorCircuitRetries);

                if (circuit == TorCircuitRetries)
                {
                    throw new TranslationException(
                        "Tor is unavailable. Translation aborted to protect anonymity.", ex);
                }

                // Invalidate cached verification so the next attempt re-checks
                _torVerificationExpiry = DateTime.MinValue;

                await RequestNewTorCircuitAsync(cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(NewCircuitWaitSeconds), cancellationToken);
            }
        }

        throw new TranslationException("Tor is unavailable. Translation aborted to protect anonymity.");
    }

    /// <summary>
    /// Verifies that Tor is reachable and that the exit IP differs from the host's real IP.
    /// Results are cached for <see cref="VerificationCacheDuration"/> to reduce overhead.
    /// </summary>
    private async Task VerifyTorAsync(CancellationToken cancellationToken)
    {
        if (_cachedTorExitIp != null && DateTime.UtcNow < _torVerificationExpiry)
        {
            return;
        }

        string torExitIp;
        try
        {
            torExitIp = (await _torHttpClient.GetStringAsync(IpCheckUrl, cancellationToken)).Trim();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Tor SOCKS5 proxy at {_torSocksHost}:{_torSocksPort} is not reachable. " +
                "Ensure the Tor sidecar is running.", ex);
        }

        string realIp;
        try
        {
            realIp = (await _directHttpClient.GetStringAsync(IpCheckUrl, cancellationToken)).Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not determine real IP for Tor verification. Proceeding with Tor exit IP {TorIp}.",
                torExitIp);
            _cachedTorExitIp = torExitIp;
            _torVerificationExpiry = DateTime.UtcNow.Add(VerificationCacheDuration);
            return;
        }

        if (string.Equals(torExitIp, realIp, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Tor exit IP ({torExitIp}) matches the host's real IP. " +
                "Tor is not anonymising traffic. Translation aborted.");
        }

        _logger.LogDebug("Tor verification passed. Exit IP: {ExitIp}", torExitIp);
        _cachedTorExitIp = torExitIp;
        _torVerificationExpiry = DateTime.UtcNow.Add(VerificationCacheDuration);
    }

    /// <summary>
    /// Calls the Google Translate unofficial API exclusively through the Tor SOCKS5 proxy.
    /// </summary>
    private async Task<string> TranslateViaTorAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var encodedText = Uri.EscapeDataString(text);
        var url = string.Format(TranslateUrlTemplate, sourceLanguage, targetLanguage, encodedText);

        var response = await _torHttpClient.GetAsync(url, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new TranslationException(
                $"Google Translate (via Tor) returned {(int)response.StatusCode}: {body}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseGoogleTranslateResponse(json);
    }

    /// <summary>
    /// Parses the Google Translate unofficial API JSON response to extract the translated text.
    /// </summary>
    private static string ParseGoogleTranslateResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var builder = new StringBuilder();
        if (root.ValueKind == JsonValueKind.Array &&
            root.GetArrayLength() > 0 &&
            root[0].ValueKind == JsonValueKind.Array)
        {
            foreach (var segment in root[0].EnumerateArray())
            {
                if (segment.ValueKind == JsonValueKind.Array &&
                    segment.GetArrayLength() > 0 &&
                    segment[0].ValueKind == JsonValueKind.String)
                {
                    builder.Append(segment[0].GetString());
                }
            }
        }

        var result = builder.ToString();
        if (string.IsNullOrWhiteSpace(result))
        {
            throw new TranslationException("Google Translate (via Tor) returned an empty translation.");
        }

        return result;
    }

    /// <summary>
    /// Sends a NEWNYM signal to the Tor control port to request a new circuit.
    /// The control port must be open without authentication (CookieAuthentication 0 and no HashedControlPassword).
    /// </summary>
    private async Task RequestNewTorCircuitAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(_torControlHost, _torControlPort, cancellationToken);

            await using var stream = tcp.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            await using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\r\n"
            };

            await writer.WriteLineAsync("AUTHENTICATE");
            await reader.ReadLineAsync(cancellationToken);

            await writer.WriteLineAsync("SIGNAL NEWNYM");
            await reader.ReadLineAsync(cancellationToken);

            _logger.LogInformation("Requested new Tor circuit via NEWNYM.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not send NEWNYM to Tor control port at {Host}:{Port}. Proceeding without new circuit.",
                _torControlHost, _torControlPort);
        }
    }

    /// <summary>
    /// Creates an <see cref="HttpClient"/> that routes all traffic through the Tor SOCKS5 proxy.
    /// Uses <see cref="SocketsHttpHandler"/> which natively supports SOCKS5 proxies in .NET 6+.
    /// </summary>
    private HttpClient CreateTorHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"socks5://{_torSocksHost}:{_torSocksPort}"),
            UseProxy = true
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _torHttpClient.Dispose();
        _directHttpClient.Dispose();
    }
}
