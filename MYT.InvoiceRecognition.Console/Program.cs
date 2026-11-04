using Microsoft.Extensions.Configuration;
using MYT.InvoiceRecognition.Api.Models;
using MYT.InvoiceRecognition.Api.Options;
using MYT.InvoiceRecognition.Api.Services;
using MYT.InvoiceRecognition.ConsoleApp;
using System.Diagnostics;
using System.Text.Json;
using ErrorTextLog = MYT.InvoiceRecognition.Console.ErrorTextLog;

const string usage = "Usage: MYT.InvoiceRecognition.Console [parse_invoice|resolve_suppliers]\n"
    + "Processes InvoiceDatabase, then InvoiceDatabaseIN, resolving suppliers only for successfully saved invoices in each batch.\n"
    + "resolve_suppliers: explicit historical supplier replay; no Azure calls.";

if (args.Length == 1 && args[0] is "--help" or "-h")
{
    Console.WriteLine(usage);
    return 0;
}

if (args.Length != 0 && args is not ["parse_invoice"] and not ["resolve_suppliers"])
{
    Console.Error.WriteLine(usage);
    return 2;
}

using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
Console.CancelKeyPress += cancelHandler;

try
{
    // Same user-secrets ID as the API; environment variables take precedence.
    using var configuration = new ConfigurationManager();
    configuration.SetBasePath(AppContext.BaseDirectory).AddJsonFile("appsettings.json", optional: true).AddUserSecrets<AzureDocumentService>(optional: true).AddEnvironmentVariables();
    var isSaveLog = configuration.GetValue("IsSaveLog", false);
    var options = new InvoiceProcessingOptions
    {
        Azure = configuration.GetSection("AzureDocumentIntelligence").Get<AzureDocumentIntelligenceOptions>() ?? new(),
        ResolveSuppliersOnly = args is ["resolve_suppliers"],
        RecognitionLogSaveTimeout = TimeSpan.FromSeconds(60)
        //RecognitionLogSaveTimeout = TimeSpan.FromSeconds(configuration.GetValue("RecognitionLog:SaveTimeoutSeconds", 60))
    };

    var exitCode = 0;
    foreach (var databaseName in new[] { "InvoiceDatabase", "InvoiceDatabaseIN" })
    {
        cancellation.Token.ThrowIfCancellationRequested();
        var started = Stopwatch.StartNew();
        var status = "FAILED";
        Console.Error.WriteLine($"[{DateTimeOffset.Now:O}] START {databaseName}");
        try
        {
            var connectionString = configuration.GetConnectionString(databaseName);
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException($"Configure ConnectionStrings:{databaseName}.");

            var runner = new InvoiceProcessingRunner();
            var result = await runner.RunAsync(connectionString, options, cancellation.Token, Console.Error);
            var resultStr = JsonSerializer.Serialize(new { Database = databaseName, Result = result }, new JsonSerializerOptions { WriteIndented = true });
            Console.Error.WriteLine(resultStr);
            if (isSaveLog)
            {
                ErrorTextLog.WriteLine(resultStr);
            }
            status = result.ExitCode == 0 ? "SUCCESS" : "FAILED";
            if (result.ExitCode != 0) exitCode = 1;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            status = "CANCELED";
            throw;
        }
        catch (Exception error)
        {
            exitCode = 1;
            ErrorTextLog.WriteException(error, context: $"Program: processing database {databaseName} failed.");
            Console.Error.WriteLine($"[{DateTimeOffset.Now:O}] ERROR {databaseName}: " +
                JsonSerializer.Serialize(CreateErrorResponse(error)));
        }
        finally
        {
            Console.Error.WriteLine($"[{DateTimeOffset.Now:O}] END {databaseName} Status={status} Duration={started.Elapsed}");
        }
    }

    return exitCode;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine(cancellation.IsCancellationRequested ? "Invoice parsing canceled." : "Azure request timed out.");
    return cancellation.IsCancellationRequested ? 130 : 1;
}
catch (Exception error)
{
    ErrorTextLog.WriteException(error, context: "Program: invoice processing failed.");
    Console.Error.WriteLine(JsonSerializer.Serialize(CreateErrorResponse(error)));
    return 1;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}

static ErrorResponse CreateErrorResponse(Exception error) => new()
{
    Message = "unable to parse invoice",
    Errors = error is OperationCanceledException ? "Azure request timed out." :
        error is ArgumentException or InvalidOperationException or TimeoutException ? error.Message :
        "Processing failed; check connectivity and Azure configuration.",
    Details = "not parsable information"
};
