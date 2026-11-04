using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MYT.InvoiceRecognition.Api.Models;
using MYT.InvoiceRecognition.Api.Options;
using MYT.InvoiceRecognition.Api.Utilities;

namespace MYT.InvoiceRecognition.Api.Services;

public sealed class AzureDocumentService
{
    private readonly HttpClient _http;
    private readonly AzureDocumentIntelligenceOptions _options;
    private readonly ILogger<AzureDocumentService>? _logger;

    public AzureDocumentService(HttpClient http, IOptions<AzureDocumentIntelligenceOptions> options, ILogger<AzureDocumentService>? logger = null)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    // Retained for older callers: explicitly analyzes using the invoice model.
    public Task<AzureInvoiceResult> AnalyzeAsync(string invoiceUrl, CancellationToken cancellationToken) => AnalyzeModelAsync(invoiceUrl, "Invoice", cancellationToken);

    public async Task<AzureInvoiceResult> AnalyzeAsync(string invoiceUrl, string? documentType, CancellationToken cancellationToken, AzureAnalysisTrace? trace = null)
    {
        var result = await AnalyzeCoreAsync(invoiceUrl, documentType, cancellationToken, trace);
        if (trace != null)
        {
            foreach (var model in trace.Models)
            {
                model.IsSelected = ReferenceEquals(model.ParsedResult, result.Invoice);
                model.DetectionMethod = result.Invoice.DetectionMethod;
            }
        }
        return result;
    }

    private async Task<AzureInvoiceResult> AnalyzeCoreAsync(string invoiceUrl, string? documentType, CancellationToken cancellationToken, AzureAnalysisTrace? trace)
    {
        var requestedType = NormalizeDocumentType(documentType);
        if (requestedType != "Auto")
        {
            var forced = await AnalyzeModelAsync(invoiceUrl, requestedType, cancellationToken, trace);
            forced.Invoice.DetectionMethod = "Manual";
            return forced;
        }

        if (!string.IsNullOrWhiteSpace(_options.ClassifierId))
        {
            try
            {
                var classified = await ClassifyAsync(invoiceUrl, cancellationToken);
                if (classified.Type != "Unknown" && classified.Confidence >= _options.ClassifierMinimumConfidence)
                {
                    var selected = await AnalyzeModelAsync(invoiceUrl, classified.Type, cancellationToken, trace);
                    selected.Invoice.DetectionMethod = "Classifier";
                    selected.Invoice.DocumentTypeConfidence = classified.Confidence;
                    return selected;
                }
                _logger?.LogWarning("Document classifier confidence was insufficient; comparing prebuilt models.");
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _logger?.LogWarning(error, "Document classifier failed; comparing prebuilt models.");
            }
        }

        AzureInvoiceResult? invoiceResult = null;
        AzureInvoiceResult? receiptResult = null;
        Exception? invoiceError = null;
        Exception? receiptError = null;
        try { invoiceResult = await AnalyzeModelAsync(invoiceUrl, "Invoice", cancellationToken, trace); }
        catch (Exception error) when (error is not OperationCanceledException) { invoiceError = error; _logger?.LogWarning(error, "Invoice model analysis failed."); }

        var invoiceScore = invoiceResult == null ? -1 : QualityScore(invoiceResult);
        if (invoiceResult != null && (!_options.AutoCompareModels || invoiceScore >= 90))
        {
            invoiceResult.Invoice.DetectionMethod = "HighQualityInvoice";
            return invoiceResult;
        }
        try { receiptResult = await AnalyzeModelAsync(invoiceUrl, "Receipt", cancellationToken, trace); }
        catch (Exception error) when (error is not OperationCanceledException) { receiptError = error; _logger?.LogWarning(error, "Receipt model analysis failed."); }

        if (invoiceResult == null && receiptResult == null) throw new InvalidOperationException($"Neither Azure model could analyze the document. Invoice: {invoiceError?.Message}; Receipt: {receiptError?.Message}");
        var receiptScore = receiptResult == null ? -1 : QualityScore(receiptResult);
        var chosen = receiptScore > invoiceScore ? receiptResult! : invoiceResult!;
        chosen.Invoice.DetectionMethod = "ModelComparison";
        if (invoiceResult == null || receiptResult == null)
        {
            chosen.Invoice.Warnings.Add("One model could not analyze this document; verify the detected type.");
        }
        else if (Math.Abs(invoiceScore - receiptScore) <= _options.AmbiguousScoreMargin)
        {
            chosen.Invoice.Warnings.Add("Invoice and receipt recognition were similarly plausible; confirm the document type.");
        }
        ApplyReviewStatus(chosen.Invoice);
        return chosen;
    }

    private static string NormalizeDocumentType(string? documentType)
    {
        var value = (documentType ?? "Auto").Trim();
        if (value.Equals("Auto", StringComparison.OrdinalIgnoreCase)) return "Auto";
        if (value.Equals("Invoice", StringComparison.OrdinalIgnoreCase)) return "Invoice";
        if (value.Equals("Receipt", StringComparison.OrdinalIgnoreCase)) return "Receipt";
        throw new ArgumentException("documentType must be Auto, Invoice, or Receipt.");
    }

    private async Task<(string Type, double Confidence)> ClassifyAsync(string invoiceUrl, CancellationToken cancellationToken)
    {
        var id = Uri.EscapeDataString(_options.ClassifierId);
        var result = await ExecuteAzureAsync(invoiceUrl, $"formrecognizer/documentClassifiers/{id}:analyze", cancellationToken);
        var documents = InvoiceUtilities.Property(InvoiceUtilities.Property(result, "analyzeResult"), "documents");
        if (documents.ValueKind != JsonValueKind.Array || documents.GetArrayLength() == 0) return ("Unknown", 0);
        var best = documents.EnumerateArray().OrderByDescending(item => ReadNumber(InvoiceUtilities.Property(item, "confidence"))).First();
        var classifiedType = InvoiceUtilities.Text(InvoiceUtilities.Property(best, "docType"));
        var type = classifiedType.Equals(_options.InvoiceClassName, StringComparison.OrdinalIgnoreCase) ? "Invoice" : classifiedType.Equals(_options.ReceiptClassName, StringComparison.OrdinalIgnoreCase) ? "Receipt" : "Unknown";
        return (type, ReadNumber(InvoiceUtilities.Property(best, "confidence")));
    }

    private async Task<AzureInvoiceResult> AnalyzeModelAsync(string invoiceUrl, string documentType, CancellationToken cancellationToken, AzureAnalysisTrace? trace = null)
    {
        var modelId = documentType == "Receipt" ? "prebuilt-receipt" : "prebuilt-invoice";
        var attempt = trace == null ? null : new AzureModelAttempt
        {
            DocumentType = documentType,
            ModelId = modelId,
            ApiVersion = _options.ApiVersion,
            ParserVersion = typeof(AzureInvoiceParser).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion ?? "unknown"
        };
        try
        {
            var result = await ExecuteAzureAsync(invoiceUrl, $"formrecognizer/documentModels/{modelId}:analyze", cancellationToken,
                attempt == null ? null : json => attempt.RawAzureResultJson = json);
            var parsed = AzureInvoiceParser.Parse(result, documentType);
            if (attempt != null)
            {
                attempt.ParsedResult = parsed.Invoice;
                attempt.QualityScore = QualityScore(parsed);
                attempt.Status = "Success";
            }
            return parsed;
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            if (attempt != null) attempt.ErrorMessage = error.Message;
            throw;
        }
        finally
        {
            if (attempt != null && !cancellationToken.IsCancellationRequested)
            {
                attempt.CompletedAtUtc = DateTime.UtcNow;
                trace!.Models.Add(attempt);
            }
        }
    }

    private async Task<JsonElement> ExecuteAzureAsync(string invoiceUrl, string route, CancellationToken cancellationToken, Action<string>? captureResponse = null)
    {
        if (string.IsNullOrWhiteSpace(_options.Endpoint) || string.IsNullOrWhiteSpace(_options.ApiKey)) throw new InvalidOperationException("Configure AzureDocumentIntelligence Endpoint and ApiKey.");
        var endpoint = new Uri(_options.Endpoint.TrimEnd('/') + "/", UriKind.Absolute);
        if (endpoint.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("Azure endpoint must use HTTPS.");
        var analyzeUrl = new Uri(endpoint, $"{route}?api-version={Uri.EscapeDataString(_options.ApiVersion)}");
        using var request = new HttpRequestMessage(HttpMethod.Post, analyzeUrl);
        request.Headers.Add("Ocp-Apim-Subscription-Key", _options.ApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(new { urlSource = invoiceUrl }), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorJson = await response.Content.ReadAsStringAsync(cancellationToken);
            CaptureJson(errorJson, captureResponse);
            throw new InvalidOperationException($"Azure request failed (HTTP {(int)response.StatusCode}): {ShortMessage(errorJson)}");
        }
        if (!response.Headers.TryGetValues("operation-location", out var locations) || !Uri.TryCreate(locations.FirstOrDefault(), UriKind.Absolute, out var pollUrl)) throw new InvalidOperationException("Azure did not return an operation-location header.");
        if (pollUrl.Scheme != Uri.UriSchemeHttps || pollUrl.Host != endpoint.Host || pollUrl.Port != endpoint.Port || !pollUrl.AbsolutePath.StartsWith("/formrecognizer/", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Azure returned an unexpected polling URL.");
        for (var attempt = 0; attempt < Math.Clamp(_options.MaxPollAttempts, 1, 300); attempt++)
        {
            if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(_options.PollIntervalSeconds, 1, 20)), cancellationToken);
            using var poll = new HttpRequestMessage(HttpMethod.Get, pollUrl);
            poll.Headers.Add("Ocp-Apim-Subscription-Key", _options.ApiKey);
            using var pollResponse = await _http.SendAsync(poll, cancellationToken);
            var json = await pollResponse.Content.ReadAsStringAsync(cancellationToken);
            CaptureJson(json, captureResponse);
            if (!pollResponse.IsSuccessStatusCode) throw new InvalidOperationException($"Azure polling failed (HTTP {(int)pollResponse.StatusCode}): {ShortMessage(json)}");
            using var document = JsonDocument.Parse(json);
            var status = InvoiceUtilities.Text(InvoiceUtilities.Property(document.RootElement, "status"));
            if (status.Equals("succeeded", StringComparison.OrdinalIgnoreCase)) return document.RootElement.Clone();
            if (status.Equals("failed", StringComparison.OrdinalIgnoreCase) || status.Equals("canceled", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"Azure analysis {status}: {ShortMessage(json)}");
            if (!status.Equals("running", StringComparison.OrdinalIgnoreCase) && !status.Equals("notStarted", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Azure returned an unknown operation status.");
        }
        throw new TimeoutException("Azure analysis did not complete within the configured polling limit.");
    }

    private static double QualityScore(AzureInvoiceResult result)
    {
        var value = result.Invoice;
        var invoice = value.DocumentType == "Invoice";
        var score = 0d;
        if (!string.IsNullOrWhiteSpace(value.Org)) score += 18;
        if (!string.IsNullOrWhiteSpace(value.Dat)) score += 17;
        if (value.Tot.HasValue) score += 22;
        if (value.Net.HasValue) score += 13;
        if (value.Vat.HasValue) score += 13;
        if (!string.IsNullOrWhiteSpace(value.Currency)) score += 7;
        if (value.Lin.Count > 0) score += 3;
        if (invoice && !string.IsNullOrWhiteSpace(value.Ivn)) score += 7;
        if (!invoice && result.HasReceiptType) score += 7;
        if (value.Net.HasValue && value.Vat.HasValue && value.Tot.HasValue && Math.Abs(value.Net.Value + value.Vat.Value - value.Tot.Value) > 0.02m) score -= 12;
        return score;
    }

    private static void CaptureJson(string json, Action<string>? captureResponse)
    {
        if (captureResponse == null) return;
        try
        {
            using var document = JsonDocument.Parse(json);
            // SQL ISJSON accepts objects and arrays; non-JSON error bodies remain in ErrorMessage.
            if (document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                captureResponse(json);
        }
        catch (JsonException) { }
    }

    private static void ApplyReviewStatus(InvoiceData invoice) => invoice.ReviewRequired = invoice.Warnings.Count > 0;
    private static double ReadNumber(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : 0;
    private static string ShortMessage(string message) => message.Length > 500 ? message[..500] : message;
}

public static class AzureInvoiceParser
{
    public static AzureInvoiceResult Parse(JsonElement root) => Parse(root, "Invoice");

    public static AzureInvoiceResult Parse(JsonElement root, string documentType)
    {
        var analysis = InvoiceUtilities.Property(root, "analyzeResult");
        var documents = InvoiceUtilities.Property(analysis, "documents");
        if (documents.ValueKind != JsonValueKind.Array || documents.GetArrayLength() == 0) throw new InvalidOperationException("Azure did not extract structured document fields.");
        var fields = InvoiceUtilities.Property(documents[0], "fields");
        if (fields.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Azure returned no structured document fields.");
        var receipt = documentType == "Receipt";
        var result = new AzureInvoiceResult { Content = InvoiceUtilities.Text(InvoiceUtilities.Property(analysis, "content")), HasReceiptType = !string.IsNullOrWhiteSpace(InvoiceUtilities.FieldText(fields, "ReceiptType")) };
        var data = result.Invoice;
        data.DocumentType = receipt ? "Receipt" : "Invoice";
        data.ModelId = receipt ? "prebuilt-receipt" : "prebuilt-invoice";
        data.Org = InvoiceUtilities.FieldText(fields, receipt ? "MerchantName" : "VendorName");
        data.Address = GetAddress(fields, receipt ? "MerchantAddress" : "VendorAddress");
        // Only explicitly supplier-labelled structured fields are trusted. Do not scrape
        // arbitrary VAT/company numbers from OCR: those may belong to the customer.
        data.SupplierVatNumber = InvoiceUtilities.FieldText(fields, receipt ? "MerchantTaxId" : "VendorTaxId");
        data.SupplierCompanyNumber = InvoiceUtilities.FieldText(fields, receipt ? "MerchantCompanyNumber" : "VendorCompanyNumber");
        data.SupplierCountry = InvoiceUtilities.Text(InvoiceUtilities.Property(
            InvoiceUtilities.Property(InvoiceUtilities.Property(fields, receipt ? "MerchantAddress" : "VendorAddress"), "valueAddress"), "countryRegion"));
        data.Ivn = receipt ? "" : InvoiceUtilities.FieldText(fields, "InvoiceId");
        data.Dat = InvoiceUtilities.UkDate(InvoiceUtilities.FieldText(fields, receipt ? "TransactionDate" : "InvoiceDate"));
        data.Tot = InvoiceUtilities.FieldMoney(fields, receipt ? "Total" : "InvoiceTotal") ?? 0m;
        data.Vat = InvoiceUtilities.FieldMoney(fields, "TotalTax") ?? 0m;
        data.Net = InvoiceUtilities.FieldMoney(fields, receipt ? "Subtotal" : "SubTotal") ?? 0m;
        // Treat missing amounts as zero for correction, preserving nulls unless a rule fills them.
        var net = data.Net ?? 0m;
        var vat = data.Vat ?? 0m;
        var total = data.Tot ?? 0m;
        if (net + vat != total)
        {
            if (net == 0 && vat != 0 && total != 0 && total > vat)
                data.Net = net = total - vat;
            if (vat == 0 && net != 0 && total != 0 && total > net)
                data.Vat = vat = total - net;
            if (total == 0 && net != 0 && vat != 0)
                data.Tot = total = net + vat;

            if (total > 0 && net == 0 && vat == 0)
                data.Net = net = total;
            if (net > 0 && total == 0 && vat == 0)
                data.Tot = total = net;
        }
        result.RawCurrency = InvoiceUtilities.FieldCurrency(fields, receipt ? "Total" : "InvoiceTotal");
        if (string.IsNullOrWhiteSpace(result.RawCurrency)) result.RawCurrency = InvoiceUtilities.FieldCurrency(fields, "TotalTax");
        data.Currency = InvoiceUtilities.CurrencyCode(result.RawCurrency);
        if (string.IsNullOrWhiteSpace(data.Currency)) data.Currency = DetectCurrency(result.Content);
        var items = InvoiceUtilities.Property(InvoiceUtilities.Property(fields, "Items"), "valueArray");
        if (items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var text = InvoiceUtilities.FieldText(InvoiceUtilities.Property(item, "valueObject"), "Description");
                if (!string.IsNullOrWhiteSpace(text)) data.Lin.Add(text);
            }
        }
        if (documents.GetArrayLength() > 1) data.Warnings.Add("Multiple documents detected; only the first document was extracted.");
        if (string.IsNullOrWhiteSpace(data.Org)) data.Warnings.Add("Supplier/merchant name was not extracted.");
        if (string.IsNullOrWhiteSpace(data.Dat)) data.Warnings.Add("Invoice/transaction date was not extracted.");
        if (!data.Tot.HasValue) data.Warnings.Add("Total amount was not extracted.");
        if (!data.Net.HasValue) data.Warnings.Add("Net amount was not extracted.");
        if (!data.Vat.HasValue) data.Warnings.Add("VAT amount was not extracted.");
        if (string.IsNullOrWhiteSpace(data.Currency)) data.Warnings.Add("Currency was not identified.");
        if (!receipt && string.IsNullOrWhiteSpace(data.Ivn)) data.Warnings.Add("Invoice number was not extracted.");
        var expectedTotal = data.Net + data.Vat;
        if (receipt && expectedTotal.HasValue) expectedTotal += InvoiceUtilities.FieldMoney(fields, "Tip") ?? 0;
        if (expectedTotal.HasValue && data.Tot.HasValue && Math.Abs(expectedTotal.Value - data.Tot.Value) > 0.02m) data.Warnings.Add("Net + VAT does not reconcile to the total; verify rounding, tips, and other charges.");
        AddLowConfidenceWarning(fields, receipt ? "MerchantName" : "VendorName", "Supplier name", data.Warnings);
        AddLowConfidenceWarning(fields, receipt ? "Total" : "InvoiceTotal", "Total amount", data.Warnings);
        data.ReviewRequired = data.Warnings.Count > 0;
        return result;
    }

    private static void AddLowConfidenceWarning(JsonElement fields, string fieldName, string description, List<string> warnings)
    {
        var confidence = InvoiceUtilities.Property(InvoiceUtilities.Property(fields, fieldName), "confidence");
        if (confidence.ValueKind == JsonValueKind.Number && confidence.TryGetDouble(out var value) && value < 0.65d) warnings.Add($"Low confidence for {description} ({value:P0}).");
    }

    private static string DetectCurrency(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return "";
        var hasPound = content.Contains('£');
        var hasEuro = content.Contains('€');
        if (hasPound && !hasEuro) return "GBP";
        if (hasEuro && !hasPound) return "EUR";
        return ""; // $ is ambiguous; do not guess USD, CAD or AUD.
    }

    private static string GetAddress(JsonElement fields, string fieldName)
    {
        var field = InvoiceUtilities.Property(fields, fieldName);
        var address = InvoiceUtilities.Property(field, "valueAddress");
        if (address.ValueKind == JsonValueKind.Object)
        {
            var values = new[] { "houseNumber", "road", "city", "state", "postalCode", "countryRegion" }.Select(key => InvoiceUtilities.Text(InvoiceUtilities.Property(address, key))).Where(value => !string.IsNullOrWhiteSpace(value));
            var combined = string.Join(" ", values);
            if (!string.IsNullOrWhiteSpace(combined)) return combined;
        }
        return InvoiceUtilities.Text(InvoiceUtilities.Property(field, "content"));
    }
}
