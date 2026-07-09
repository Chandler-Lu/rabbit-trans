using System.Diagnostics;
using System.Net;
using Microsoft.Win32;

namespace RabTrans.Core.Networking;

public enum ProxyMode
{
    System,
    None,
    Http
}

public static class NetworkProxyOptions
{
    public static ProxyMode Mode { get; set; } = ProxyMode.System;

    public static string HttpProxy { get; set; } = string.Empty;

    public static HttpMessageHandler CreateHttpMessageHandler()
    {
        var handler = new HttpClientHandler();
        switch (Mode)
        {
            case ProxyMode.None:
                handler.UseProxy = false;
                break;
            case ProxyMode.Http when !string.IsNullOrWhiteSpace(HttpProxy):
                handler.UseProxy = true;
                handler.Proxy = new WebProxy(HttpProxy.Trim());
                break;
            default:
                handler.UseProxy = true;
                handler.Proxy = WebRequest.DefaultWebProxy;
                break;
        }

        return handler;
    }

    public static void ApplyToProcessEnvironment(ProcessStartInfo startInfo)
    {
        switch (Mode)
        {
            case ProxyMode.None:
                startInfo.Environment["NO_PROXY"] = "*";
                startInfo.Environment["no_proxy"] = "*";
                startInfo.Environment.Remove("HTTP_PROXY");
                startInfo.Environment.Remove("HTTPS_PROXY");
                startInfo.Environment.Remove("ALL_PROXY");
                startInfo.Environment.Remove("http_proxy");
                startInfo.Environment.Remove("https_proxy");
                startInfo.Environment.Remove("all_proxy");
                startInfo.Environment.Remove("NODE_USE_ENV_PROXY");
                break;
            case ProxyMode.Http when !string.IsNullOrWhiteSpace(HttpProxy):
                var proxy = HttpProxy.Trim();
                ApplyProxyEnvironment(startInfo, proxy, proxy);
                break;
            default:
                var systemProxy = GetWindowsSystemProxy();
                if (systemProxy != null)
                {
                    ApplyProxyEnvironment(startInfo, systemProxy.Value.HttpProxy, systemProxy.Value.HttpsProxy);
                }
                else
                {
                    startInfo.Environment["NODE_USE_ENV_PROXY"] = "1";
                }
                break;
        }
    }

    private static void ApplyProxyEnvironment(ProcessStartInfo startInfo, string? httpProxy, string? httpsProxy)
    {
        var http = NormalizeProxyUri(httpProxy);
        var https = NormalizeProxyUri(httpsProxy) ?? http;
        if (string.IsNullOrWhiteSpace(http) && string.IsNullOrWhiteSpace(https))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(http))
        {
            startInfo.Environment["HTTP_PROXY"] = http;
            startInfo.Environment["http_proxy"] = http;
            startInfo.Environment["ALL_PROXY"] = http;
            startInfo.Environment["all_proxy"] = http;
        }

        if (!string.IsNullOrWhiteSpace(https))
        {
            startInfo.Environment["HTTPS_PROXY"] = https;
            startInfo.Environment["https_proxy"] = https;
        }

        startInfo.Environment["NODE_USE_ENV_PROXY"] = "1";
        startInfo.Environment.Remove("NO_PROXY");
        startInfo.Environment.Remove("no_proxy");
    }

    private static (string? HttpProxy, string? HttpsProxy)? GetWindowsSystemProxy()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        var proxyEnabled = Convert.ToInt32(key?.GetValue("ProxyEnable") ?? 0) != 0;
        var proxyServer = key?.GetValue("ProxyServer")?.ToString();
        if (!proxyEnabled || string.IsNullOrWhiteSpace(proxyServer))
        {
            return null;
        }

        string? httpProxy = null;
        string? httpsProxy = null;
        if (proxyServer.Contains('=', StringComparison.Ordinal))
        {
            foreach (var part in proxyServer.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
                if (pair.Length != 2)
                {
                    continue;
                }

                if (pair[0].Equals("http", StringComparison.OrdinalIgnoreCase))
                {
                    httpProxy = pair[1];
                }
                else if (pair[0].Equals("https", StringComparison.OrdinalIgnoreCase))
                {
                    httpsProxy = pair[1];
                }
            }
        }
        else
        {
            httpProxy = proxyServer;
            httpsProxy = proxyServer;
        }

        return string.IsNullOrWhiteSpace(httpProxy) && string.IsNullOrWhiteSpace(httpsProxy)
            ? null
            : (httpProxy, httpsProxy);
    }

    private static string? NormalizeProxyUri(string? proxy)
    {
        if (string.IsNullOrWhiteSpace(proxy))
        {
            return null;
        }

        var trimmed = proxy.Trim();
        return trimmed.Contains("://", StringComparison.Ordinal)
            ? trimmed
            : $"http://{trimmed}";
    }

    public static ProxyMode ParseMode(string? value)
    {
        return Enum.TryParse<ProxyMode>(value, true, out var mode)
            ? mode
            : ProxyMode.System;
    }
}
