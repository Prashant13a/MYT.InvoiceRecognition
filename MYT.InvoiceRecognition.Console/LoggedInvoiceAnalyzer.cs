using MYT.InvoiceRecognition.Api.Models;

namespace MYT.InvoiceRecognition.ConsoleApp;

public interface IRecognitionLogStore
{
    Task SaveAsync(PendingInvoice row, AzureAnalysisTrace trace, CancellationToken cancellationToken);
}

public sealed class LoggedInvoiceAnalyzer(
    Func<PendingInvoice, AzureAnalysisTrace, CancellationToken, Task<InvoiceData>> analyze,
    IRecognitionLogStore logStore, TextWriter progress, TimeSpan? logSaveTimeout = null)
{
    private readonly TimeSpan _logSaveTimeout = ValidateTimeout(logSaveTimeout ?? TimeSpan.FromSeconds(60));

    public async Task<InvoiceData> AnalyzeAsync(PendingInvoice row, CancellationToken cancellationToken)
    {
        var trace = new AzureAnalysisTrace();
        try
        {
            return await analyze(row, trace, cancellationToken);
        }
        finally
        {
            // Also preserve completed attempts when another model fails or the caller cancels.
            // A separate, bounded token allows logging without replacing the analysis exception.
            if (trace.Models.Count > 0)
            {
                using var logTimeout = new CancellationTokenSource(_logSaveTimeout);
                try
                {
                    await logStore.SaveAsync(row, trace, logTimeout.Token);
                }
                catch (OperationCanceledException) when (logTimeout.IsCancellationRequested)
                {
                    await progress.WriteLineAsync($"UploadedInvoice {row.Id}, Invoice {row.InvoiceId}, Attempt {trace.AttemptId}: recognition log save timed out after {_logSaveTimeout.TotalSeconds:g} seconds. Check SQL blocking and database connectivity, or increase RecognitionLog:SaveTimeoutSeconds.");
                }
                catch (Exception error)
                {
                    await progress.WriteLineAsync($"UploadedInvoice {row.Id}, Invoice {row.InvoiceId}, Attempt {trace.AttemptId}: recognition log could not be saved.");
                    // Include the message, inner exceptions, stack trace, and SqlException diagnostics.
                    await progress.WriteLineAsync(error.ToString());
                }
            }
        }
    }

    private static TimeSpan ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(logSaveTimeout), "Recognition log save timeout must be positive and within the cancellation timer range.");
        return timeout;
    }
}
