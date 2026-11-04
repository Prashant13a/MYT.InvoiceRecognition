using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MYT.InvoiceRecognition.Api.Utilities;

public static class InvoiceUtilities
{
    public static JsonElement Property(JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)) return value;
        return default;
    }

    public static string Text(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Number => element.GetRawText(),
            _ => ""
        };
    }

    public static string FieldText(JsonElement fields, string name)
    {
        var field = Property(fields, name);
        foreach (var property in new[] { "valueString", "valueDate", "valueTime", "content" })
        {
            var value = Text(Property(field, property));
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return "";
    }

    public static decimal? Decimal(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var parsed)) return parsed;
        var text = Text(element).Trim();
        if (decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowCurrencySymbol, CultureInfo.GetCultureInfo("en-GB"), out parsed)) return parsed;
        if (decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowCurrencySymbol, CultureInfo.GetCultureInfo("en-US"), out parsed)) return parsed;
        return null;
    }

    public static decimal? FieldMoney(JsonElement fields, string name)
    {
        var field = Property(fields, name);
        var money = Property(field, "valueCurrency");
        return Decimal(Property(money, "amount")) ?? Decimal(Property(field, "valueNumber")) ?? Decimal(Property(field, "content"));
    }

    public static string FieldCurrency(JsonElement fields, string name)
    {
        var money = Property(Property(fields, name), "valueCurrency");
        var code = Text(Property(money, "currencyCode"));
        return !string.IsNullOrWhiteSpace(code) ? code : Text(Property(money, "currencySymbol"));
    }

    public static string UkDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var text = value.Trim();
        if (DateTime.TryParseExact(text, new[] { "yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd-MMM-yyyy", "d MMM yyyy", "MM/dd/yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        if (DateTime.TryParse(text, CultureInfo.GetCultureInfo("en-GB"), DateTimeStyles.None, out date)) return date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        return "";
    }

    public static string CurrencyCode(string? value)
    {
        var candidate = (value ?? "").Trim().ToUpperInvariant();
        return candidate switch
        {
            "£" => "GBP", "$" => "", "€" => "EUR", "₹" => "INR", "¥" => "JPY", "MUR" => "MUR",
            _ => Regex.IsMatch(candidate, "^[A-Z]{3}$") ? candidate : ""
        };
    }

    public static string TrimForModel(string input, int maxChars)
    {
        if (maxChars <= 0) return "";
        return input.Length > maxChars ? input[..maxChars] : input;
    }

    public static async Task ValidatePublicUrlAsync(string invoiceUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(invoiceUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) throw new ArgumentException("invoice_url must be a valid HTTP(S) URL.");
        if (!string.IsNullOrEmpty(uri.UserInfo) || uri.HostNameType == UriHostNameType.Unknown) throw new ArgumentException("invoice_url contains an invalid host.");
        if (uri.IsLoopback || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Private invoice URLs are not allowed.");
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken); }
        catch (Exception error) when (error is SocketException || error is OperationCanceledException)
        {
            if (error is OperationCanceledException) throw;
            throw new ArgumentException("invoice_url hostname could not be resolved.", error);
        }
        if (addresses.Length == 0 || addresses.Any(IsPrivateAddress)) throw new ArgumentException("invoice_url must resolve to a public host.");
    }

    public static bool IsPrivateAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 0 || b[0] == 10 || b[0] == 127 || (b[0] == 169 && b[1] == 254) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || (b[0] == 192 && b[1] == 0 && b[2] == 0) || b[0] >= 224;
        }
        return ip.AddressFamily != AddressFamily.InterNetworkV6 || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.Equals(IPAddress.IPv6Any) || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;
    }
}
