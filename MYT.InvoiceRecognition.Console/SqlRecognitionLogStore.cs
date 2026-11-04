using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using MYT.InvoiceRecognition.Api.Models;

namespace MYT.InvoiceRecognition.ConsoleApp;

public sealed class SqlRecognitionLogStore(string connectionString) : IRecognitionLogStore
{
    public async Task SaveAsync(PendingInvoice row, AzureAnalysisTrace trace, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var model in trace.Models)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO dbo.InvoiceRecognitionLog
                    (AttemptId, UploadedInvoiceId, InvoiceId, FilePath,
                     DocumentType, ModelId, ApiVersion, ParserVersion,
                     Status, IsSelected, QualityScore, DetectionMethod, ReviewRequired,
                     InvoiceDate, InvoiceNumber, SupplierName, VATNumber, CompanyNumber, SupplierAddress, SupplierCountry,
                     NetAmount, VatAmount, TotalAmount, Currency,
                     RawAzureResultJson, ParsedResultJson, ErrorMessage,
                     StartedAtUtc, CompletedAtUtc)
                VALUES
                    (@attemptId, @uploadedInvoiceId, @invoiceId, @filePath,
                     @documentType, @modelId, @apiVersion, @parserVersion,
                     @status, @isSelected, @qualityScore, @detectionMethod, @reviewRequired,
                     @invoiceDate, @invoiceNumber, @supplierName, @vatNumber, @companyNumber, @supplierAddress, @supplierCountry,
                     @net, @vat, @total, @currency,
                     @rawJson, @parsedJson, @errorMessage,
                     @startedAtUtc, @completedAtUtc);
                """;
            command.Parameters.Add("@attemptId", SqlDbType.UniqueIdentifier).Value = trace.AttemptId;
            // Match the source identifier CLR types; the setup script checks the SQL column types.
            command.Parameters.AddWithValue("@uploadedInvoiceId", row.Id);
            command.Parameters.AddWithValue("@invoiceId", row.InvoiceId);
            Text(command, "@filePath", row.FilePath);
            Text(command, "@documentType", model.DocumentType);
            Text(command, "@modelId", model.ModelId);
            Text(command, "@apiVersion", model.ApiVersion);
            Text(command, "@parserVersion", model.ParserVersion);
            Text(command, "@status", model.Status);
            Value(command, "@isSelected", SqlDbType.Bit, model.IsSelected);
            Value(command, "@qualityScore", SqlDbType.Float, model.QualityScore);
            Text(command, "@detectionMethod", model.DetectionMethod);
            var invoice = model.ParsedResult;
            Value(command, "@reviewRequired", SqlDbType.Bit, invoice?.ReviewRequired);
            DateTime? date = DateTime.TryParseExact(invoice?.Dat, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate) ? parsedDate : null;
            Value(command, "@invoiceDate", SqlDbType.Date, date);
            Text(command, "@invoiceNumber", invoice?.Ivn);
            Text(command, "@supplierName", invoice?.Org);
            Text(command, "@vatNumber", invoice?.SupplierVatNumber);
            Text(command, "@companyNumber", invoice?.SupplierCompanyNumber);
            Text(command, "@supplierAddress", invoice?.Address);
            Text(command, "@supplierCountry", invoice?.SupplierCountry);
            Amount(command, "@net", invoice?.Net);
            Amount(command, "@vat", invoice?.Vat);
            Amount(command, "@total", invoice?.Tot);
            Text(command, "@currency", invoice?.Currency);
            Text(command, "@rawJson", model.RawAzureResultJson);
            Text(command, "@parsedJson", invoice == null ? null : JsonSerializer.Serialize(invoice));
            Text(command, "@errorMessage", model.ErrorMessage);
            Value(command, "@startedAtUtc", SqlDbType.DateTime2, model.StartedAtUtc);
            Value(command, "@completedAtUtc", SqlDbType.DateTime2, model.CompletedAtUtc);
            // Break on ExecuteNonQueryAsync and copy tmp using the debugger's Text Visualizer.
            // This script includes parameter values; execution still uses the parameterized command.
            var tmp = BuildDebugSql(command);
            await command.ExecuteNonQueryAsync(cancellationToken);
            GC.KeepAlive(tmp);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static string BuildDebugSql(SqlCommand command)
    {
        var sql = new StringBuilder();
        foreach (SqlParameter parameter in command.Parameters)
        {
            var type = parameter.SqlDbType switch
            {
                SqlDbType.NVarChar or SqlDbType.VarChar or SqlDbType.VarBinary =>
                    $"{parameter.SqlDbType.ToString().ToLowerInvariant()}({(parameter.Size == -1 ? "max" : parameter.Size.ToString(CultureInfo.InvariantCulture))})",
                SqlDbType.Decimal => $"decimal({parameter.Precision},{parameter.Scale})",
                _ => parameter.SqlDbType.ToString().ToLowerInvariant()
            };
            var value = parameter.Value switch
            {
                null or DBNull => "NULL",
                bool flag => flag ? "1" : "0",
                DateTime date => $"'{date.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture)}'",
                DateTimeOffset date => $"'{date.ToString("O", CultureInfo.InvariantCulture)}'",
                byte[] bytes => "0x" + Convert.ToHexString(bytes),
                string text => "N'" + text.Replace("'", "''") + "'",
                Guid guid => $"'{guid:D}'",
                IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
                _ => throw new NotSupportedException($"Cannot format SQL debug parameter {parameter.ParameterName}.")
            };
            sql.Append("DECLARE ").Append(parameter.ParameterName).Append(' ').Append(type)
                .Append(" = ").Append(value).AppendLine(";");
        }
        sql.AppendLine().AppendLine(command.CommandText);
        return sql.ToString();
    }

    private static void Text(SqlCommand command, string name, string? value) =>
        command.Parameters.Add(name, SqlDbType.NVarChar, -1).Value = (object?)value ?? DBNull.Value;

    private static void Value(SqlCommand command, string name, SqlDbType type, object? value) =>
        command.Parameters.Add(name, type).Value = value ?? DBNull.Value;

    private static void Amount(SqlCommand command, string name, decimal? value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = 19;
        parameter.Scale = 4;
        parameter.Value = (object?)value ?? DBNull.Value;
    }
}
