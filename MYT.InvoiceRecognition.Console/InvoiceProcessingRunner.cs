using Microsoft.Extensions.Options;
using MYT.InvoiceRecognition.Api.Options;
using MYT.InvoiceRecognition.Api.Services;
using MYT.InvoiceRecognition.Api.Utilities;

namespace MYT.InvoiceRecognition.ConsoleApp;

public sealed class InvoiceProcessingOptions
{
    public AzureDocumentIntelligenceOptions Azure { get; set; } = new();
    public bool ResolveSuppliersOnly { get; set; }
    public TimeSpan RecognitionLogSaveTimeout { get; set; } = TimeSpan.FromSeconds(60);
}

public sealed record InvoiceProcessingResult(BatchSummary? Summary, SupplierResolutionSummary SupplierResolution)
{
    public int ExitCode => (Summary?.Failed ?? 0) == 0 && SupplierResolution.Failed == 0 ? 0 : 1;
}

/// <summary>Processes one database, keeping all batch state local to the invocation.</summary>
public sealed class InvoiceProcessingRunner
{
    public async Task<InvoiceProcessingResult> RunAsync(string connectionString, InvoiceProcessingOptions options, CancellationToken cancellationToken = default, TextWriter? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(options);
        progress ??= TextWriter.Null;

        var resolver = new SqlSupplierNameResolver(connectionString);
        await resolver.ValidateSchemaAsync(cancellationToken);
        if (options.ResolveSuppliersOnly)
            return new(null, await resolver.RunAsync(progress, cancellationToken));

        ArgumentNullException.ThrowIfNull(options.Azure);
        if (string.IsNullOrWhiteSpace(options.Azure.Endpoint) || string.IsNullOrWhiteSpace(options.Azure.ApiKey))
            throw new InvalidOperationException("Configure AzureDocumentIntelligence Endpoint and ApiKey.");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var azure = new AzureDocumentService(http, Options.Create(options.Azure));
        var loggedAnalyzer = new LoggedInvoiceAnalyzer(async (row, trace, token) =>
        {
            await InvoiceUtilities.ValidatePublicUrlAsync(row.FilePath, token);
            return (await azure.AnalyzeAsync(row.FilePath, "Auto", token, trace)).Invoice;
        }, new SqlRecognitionLogStore(connectionString), progress, options.RecognitionLogSaveTimeout);
        var batch = new InvoiceBatchProcessor(new SqlInvoiceStore(connectionString), loggedAnalyzer.AnalyzeAsync);

        var summary = await batch.RunAsync(progress, cancellationToken);
        await progress.WriteLineAsync($"Resolving supplier names for {batch.SuccessfullySavedInvoiceIds.Count} successfully saved invoices in this batch...");
        var suppliers = await resolver.RunBatchAsync(batch.SuccessfullySavedInvoiceIds, progress, cancellationToken);
        return new(summary, suppliers);
    }
}
