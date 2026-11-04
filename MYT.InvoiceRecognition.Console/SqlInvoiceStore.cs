using Microsoft.Data.SqlClient;
using MYT.InvoiceRecognition.Api.Models;
using System.Data;
using System.Globalization;

namespace MYT.InvoiceRecognition.ConsoleApp;

public sealed class SqlInvoiceStore(string connectionString) : IInvoiceStore
{
    private bool _dateIsText;

    public async Task<IReadOnlyList<PendingInvoice>> LoadPendingAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // Supports either a SQL date/datetime column or an existing text date column.
        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "SELECT TOP (0) InvoiceMissionDate FROM UploadedInvoice";
            await using var schemaReader = await schema.ExecuteReaderAsync(cancellationToken);
            _dateIsText = schemaReader.GetFieldType(0) == typeof(string);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ui.Id, ui.InvoiceId, i.FilePath, i.ClientId, ISNULL(c.IsEnableMultiCurrency, 0) AS IsEnableMultiCurrency, ui.CurrencyId
            FROM UploadedInvoice AS ui
            INNER JOIN Invoice AS i ON ui.InvoiceId = i.Id
            LEFT JOIN Acc_Company AS c ON c.ClientId = i.ClientId
            WHERE ui.TaskStatus IN ('Error', 'ERROR', 'PENDING')
                AND ISNULL(ui.IsReviewed, 0) = 0
            ORDER BY ui.Id
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<PendingInvoice>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new PendingInvoice(reader.GetValue(0), reader.GetValue(1), reader.IsDBNull(2) ? "" : reader.GetString(2))
            {
                ClientId = reader.IsDBNull(3) ? null : reader.GetValue(3),
                IsEnableMultiCurrency = Convert.ToBoolean(reader.GetValue(4), CultureInfo.InvariantCulture),
                CurrencyId = reader.IsDBNull(5) ? null : Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture)
            });
        return rows;
    }

    public async Task<bool> SaveAsync(PendingInvoice row, InvoiceData invoice, CancellationToken cancellationToken)
    {
        // Azure normalizes dates to dd/MM/yyyy; never depend on SQL session date formats.
        DateTime? date = null;
        if (!string.IsNullOrWhiteSpace(invoice.Dat))
            date = DateTime.ParseExact(invoice.Dat, "dd/MM/yyyy", CultureInfo.InvariantCulture);

        var reviewedDate = DateTime.UtcNow;
        var isReviewed = false;
        bool? isDateVerificationRequired = null;

        if (!string.IsNullOrWhiteSpace(invoice.Org) && !string.IsNullOrWhiteSpace(invoice.Ivn) && date.HasValue)
        {
            if (date.Value.Date <= reviewedDate.Date)
            {
                if (date.Value.Day > 12)
                    isDateVerificationRequired = true;

                if (!invoice.ReviewRequired && invoice.Net + invoice.Vat == invoice.Tot && invoice.Tot > 0 && invoice.Net > 0)
                {
                    if (!row.IsEnableMultiCurrency || row.CurrencyId > 0)
                        isReviewed = true;
                }
            }
        }

        int? reviewedBy = isReviewed ? -1 : null;
        if (date.HasValue && string.IsNullOrEmpty(invoice.Ivn) && invoice.DocumentType == "Receipt")
            invoice.Ivn = date.Value.ToString("ddMMyyyy");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ui
            SET InvoiceNumber = COALESCE(@invoiceNumber, ui.InvoiceNumber),
                InvoiceVendorName = COALESCE(@vendorName, ui.InvoiceVendorName),
                InvoiceMissionDate = COALESCE(@invoiceDate, ui.InvoiceMissionDate),
                Currency = COALESCE(@currency, ui.Currency),
                InvoiceBaseAmount = COALESCE(@net, ui.InvoiceBaseAmount),
                InvoiceVatAmount = COALESCE(@vat, ui.InvoiceVatAmount),
                InvoiceTotalAmount = COALESCE(@total, ui.InvoiceTotalAmount),
                TaskStatus = 'Success',
                DuplicatResult = NULL,
                ReviewedDate = @reviewedDate,
                IsReviewed = @isReviewed,
                ReviewedBy = COALESCE(@reviewedBy, ui.ReviewedBy),
                IsDateVerificationRequired = COALESCE(@isDateVerificationRequired, ui.IsDateVerificationRequired),
                IsRejected = 0,
                IsDuplicated = 0
            FROM UploadedInvoice AS ui
            INNER JOIN Invoice AS i ON ui.InvoiceId = i.Id
            WHERE ui.Id = @id AND ui.InvoiceId = @invoiceId AND i.FilePath = @filePath
                AND ui.TaskStatus IN ('Error', 'ERROR', 'PENDING') AND ISNULL(ui.IsReviewed, 0) = 0;
            SELECT CONVERT(int, @@ROWCOUNT);
            """;
        command.Parameters.AddWithValue("@id", row.Id);
        command.Parameters.AddWithValue("@invoiceId", row.InvoiceId);
        command.Parameters.Add("@reviewedDate", SqlDbType.DateTime2).Value = reviewedDate;
        command.Parameters.Add("@isReviewed", SqlDbType.Bit).Value = isReviewed;
        command.Parameters.Add("@reviewedBy", SqlDbType.Int).Value = (object?)reviewedBy ?? DBNull.Value;
        command.Parameters.Add("@isDateVerificationRequired", SqlDbType.Bit).Value = (object?)isDateVerificationRequired ?? DBNull.Value;
        command.Parameters.Add("@filePath", SqlDbType.NVarChar, -1).Value = row.FilePath;
        AddText(command, "@invoiceNumber", invoice.Ivn);
        AddText(command, "@vendorName", invoice.Org);
        AddText(command, "@currency", invoice.Currency);
        AddAmount(command, "@net", invoice.Net);
        AddAmount(command, "@vat", invoice.Vat);
        AddAmount(command, "@total", invoice.Tot);
        var dateParameter = command.Parameters.Add("@invoiceDate", _dateIsText ? SqlDbType.NVarChar : SqlDbType.Date);
        if (_dateIsText) dateParameter.Size = 10;
        dateParameter.Value = date.HasValue ? (_dateIsText ? date.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : (object)date.Value) : DBNull.Value;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    public async Task<bool> SaveErrorAsync(PendingInvoice row, string message, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ui
            SET TaskStatus = 'ERROR',
                ReviewedDate = @reviewedDate,
                DuplicatResult = @message
            FROM UploadedInvoice AS ui
            INNER JOIN Invoice AS i ON ui.InvoiceId = i.Id
            WHERE ui.Id = @id AND ui.InvoiceId = @invoiceId AND COALESCE(i.FilePath, '') = @filePath
                AND ui.TaskStatus IN ('Error', 'ERROR', 'PENDING') AND ISNULL(ui.IsReviewed, 0) = 0;
            SELECT CONVERT(int, @@ROWCOUNT);
            """;
        command.Parameters.AddWithValue("@id", row.Id);
        command.Parameters.AddWithValue("@invoiceId", row.InvoiceId);
        command.Parameters.Add("@filePath", SqlDbType.NVarChar, -1).Value = row.FilePath;
        command.Parameters.Add("@message", SqlDbType.NVarChar, -1).Value = message;
        command.Parameters.Add("@reviewedDate", SqlDbType.DateTime2).Value = DateTime.UtcNow;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    private static void AddText(SqlCommand command, string name, string? value) =>
        command.Parameters.Add(name, SqlDbType.NVarChar, -1).Value =
            string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

    private static void AddAmount(SqlCommand command, string name, decimal? value) =>
        command.Parameters.Add(name, SqlDbType.Decimal).Value =
            value.HasValue ? (object)value.Value : DBNull.Value;
}
