using MYT.InvoiceRecognition.Api.Models;
using MYT.InvoiceRecognition.Api.Utilities;

namespace MYT.InvoiceRecognition.Api.Services;

public sealed class InvoiceRecognitionService
{
    private readonly AzureDocumentService _azure;
    private readonly OpenAiService _ai;
    private readonly ILogger<InvoiceRecognitionService> _logger;

    public InvoiceRecognitionService(AzureDocumentService azure, OpenAiService ai, ILogger<InvoiceRecognitionService> logger)
    {
        _azure = azure;
        _ai = ai;
        _logger = logger;
    }

    public async Task<InvoiceData> ParseAsync(string invoiceUrl, string? currencyOverride, bool verifyMissingFields, CancellationToken cancellationToken)
    {
        return await ParseAsync(invoiceUrl, currencyOverride, verifyMissingFields, "Auto", cancellationToken);
    }

    public async Task<InvoiceData> ParseAsync(string invoiceUrl, string? currencyOverride, bool verifyMissingFields, string? documentType, CancellationToken cancellationToken)
    {
        await InvoiceUtilities.ValidatePublicUrlAsync(invoiceUrl, cancellationToken);
        var recognized = await _azure.AnalyzeAsync(invoiceUrl, documentType, cancellationToken);
        var invoice = recognized.Invoice;
        if (verifyMissingFields && _ai.IsConfigured)
        {
            try { await _ai.ImproveInvoiceAsync(invoice, recognized.Content, cancellationToken); }
            catch (Exception error) when (error is not OperationCanceledException) { _logger.LogWarning(error, "Optional AI field verification failed."); }
        }
        var needsCurrency = string.IsNullOrWhiteSpace(currencyOverride) && string.IsNullOrWhiteSpace(invoice.Currency);
        var needsDate = string.IsNullOrWhiteSpace(invoice.Dat);
        if ((needsCurrency || needsDate) && _ai.IsConfigured)
        {
            try
            {
                var predicted = await _ai.ExtractMissingFieldsAsync(recognized, needsDate, needsCurrency, cancellationToken);
                if (needsDate) invoice.Dat = predicted.Date;
                if (needsCurrency) invoice.Currency = predicted.Currency;
            }
            catch (Exception error) when (error is not OperationCanceledException) { _logger.LogWarning(error, "Optional AI date/currency enrichment failed."); }
        }
        if (!string.IsNullOrWhiteSpace(currencyOverride))
        {
            var currency = currencyOverride.Trim().ToUpperInvariant();
            if (!System.Text.RegularExpressions.Regex.IsMatch(currency, "^[A-Z]{3}$")) throw new ArgumentException("currency must be a three-letter ISO currency code.");
            invoice.Currency = currency;
        }
        invoice.Warnings.RemoveAll(value => value == "Currency was not identified." && !string.IsNullOrWhiteSpace(invoice.Currency) || value == "Invoice/transaction date was not extracted." && !string.IsNullOrWhiteSpace(invoice.Dat));
        invoice.ReviewRequired = invoice.Warnings.Count > 0;
        if (_ai.IsSupplierConfigured && !string.IsNullOrWhiteSpace(invoice.Org))
        {
            try { invoice.SupplierInfo = await _ai.PredictSupplierAsync(invoice.Org, cancellationToken); }
            catch (Exception error) when (error is not OperationCanceledException) { _logger.LogWarning(error, "Optional supplier matching failed."); }
        }
        return invoice;
    }
}
