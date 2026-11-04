using MYT.InvoiceRecognition.Api.Models;

namespace MYT.InvoiceRecognition.ConsoleApp;

public sealed record PendingInvoice(object Id, object InvoiceId, string FilePath)
{
    public object? ClientId { get; init; }
    public bool IsEnableMultiCurrency { get; init; }
    public int? CurrencyId { get; init; }
}
public sealed record BatchSummary(int Selected, int Succeeded, int Failed, int Skipped);

public interface IInvoiceStore
{
    Task<IReadOnlyList<PendingInvoice>> LoadPendingAsync(CancellationToken cancellationToken);
    Task<bool> SaveAsync(PendingInvoice row, InvoiceData invoice, CancellationToken cancellationToken);
    Task<bool> SaveErrorAsync(PendingInvoice row, string message, CancellationToken cancellationToken);
}

public sealed class InvoiceBatchProcessor(IInvoiceStore store, Func<PendingInvoice, CancellationToken, Task<InvoiceData>> analyze)
{
    public IReadOnlyList<object> SuccessfullySavedInvoiceIds { get; private set; } = Array.Empty<object>();

    public async Task<BatchSummary> RunAsync(TextWriter progress, CancellationToken cancellationToken)
    {
        SuccessfullySavedInvoiceIds = Array.Empty<object>();
        var savedInvoiceIds = new List<object>();
        var rows = await store.LoadPendingAsync(cancellationToken);
        var succeeded = 0;
        var failed = 0;
        var skipped = 0;
        await progress.WriteLineAsync($"============== UploadedInvoice Rows: {rows.Count}");
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await progress.WriteLineAsync($"UploadedInvoice {row.Id}: File {row.FilePath}: {(skipped + succeeded + failed + 1)}/{rows.Count}");
                if (string.IsNullOrWhiteSpace(row.FilePath)) throw new ArgumentException("FilePath is empty.");
                var extension = GetFileExtension(row.FilePath);
                if (extension is not (".pdf" or ".jpg" or ".jpeg" or ".png" or ".bmp" or ".tif" or ".tiff" or ".heif"))
                {
                    skipped++;
                    await progress.WriteLineAsync($"UploadedInvoice {row.Id}: skipped because the file extension '{(extension.Length == 0 ? "(none)" : extension)}' is unsupported.");
                    continue;
                }
                var invoice = await analyze(row, cancellationToken);
                if (await store.SaveAsync(row, invoice, cancellationToken))
                {
                    savedInvoiceIds.Add(row.Id);
                    succeeded++;
                    await progress.WriteLineAsync($"UploadedInvoice {row.Id}: Success");
                }
                else
                {
                    skipped++;
                    await progress.WriteLineAsync($"UploadedInvoice {row.Id}: skipped because the row or file changed.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                failed++;
                try
                {
                    var saved = await store.SaveErrorAsync(row, error.Message, cancellationToken);
                    // Store the requested message in SQL; keep potentially sensitive details out of progress logs.
                    await progress.WriteLineAsync(saved
                        ? $"UploadedInvoice {row.Id}: failed ({error.GetType().Name}); error saved."
                        : $"UploadedInvoice {row.Id}: failed ({error.GetType().Name}); error not saved because the row or file changed.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception saveError)
                {
                    await progress.WriteLineAsync($"UploadedInvoice {row.Id}: failed ({error.GetType().Name}); error could not be saved ({saveError.GetType().Name}).");
                }
            }
        }

        SuccessfullySavedInvoiceIds = savedInvoiceIds.AsReadOnly();
        return new BatchSummary(rows.Count, succeeded, failed, skipped);
    }

    private static string GetFileExtension(string filePath)
    {
        // Inspect the URL path only: query strings and fragments can contain other filenames.
        var path = Uri.TryCreate(filePath, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.AbsolutePath
            : filePath.Split('?', '#')[0];
        return Path.GetExtension(Uri.UnescapeDataString(path)).ToLowerInvariant();
    }
}
