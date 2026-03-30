using System.Net;

internal sealed record PortScanResult(
    int Port,
    bool IsWeb,
    string? Scheme,
    HttpStatusCode? StatusCode,
    string? Server,
    string? Title)
{
    public string Format()
    {
        var webTag = IsWeb
            ? $"web={Scheme?.ToUpperInvariant() ?? "HTTP"}" +
              $" status={(int?)StatusCode ?? 0}" +
              (string.IsNullOrWhiteSpace(Server) ? string.Empty : $" server={Server}") +
              (string.IsNullOrWhiteSpace(Title) ? string.Empty : $" title={Title}")
            : "web=no";

        return $"  {Port,5}/tcp  open  {webTag}";
    }
}