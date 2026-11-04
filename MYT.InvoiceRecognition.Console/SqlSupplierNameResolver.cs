using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using MYT.InvoiceRecognition.Api.Models;
using MYT.InvoiceRecognition.Api.Services;

namespace MYT.InvoiceRecognition.ConsoleApp;

public sealed record SupplierResolutionSummary(int Selected, int Updated, int Pending, int Skipped, int Failed);

/// <summary>Replays saved supplier evidence without OCR calls or changes to invoice financial/review fields.</summary>
public sealed class SqlSupplierNameResolver(string connectionString)
{
    private sealed record SavedInvoice(long LogId, object UploadedInvoiceId, object InvoiceId, string FilePath,
        string OriginalName, string? PreviousName, string? CurrentName, SupplierEvidence Evidence);

    public async Task ValidateSchemaAsync(CancellationToken token)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = """
                SELECT CASE WHEN COL_LENGTH('dbo.InvoiceRecognitionLog', 'SupplierResolvedOn') IS NULL
                    OR COL_LENGTH('dbo.InvoiceRecognitionLog', 'VATNumber') IS NULL
                    OR COL_LENGTH('dbo.InvoiceRecognitionLog', 'CompanyNumber') IS NULL
                    OR COL_LENGTH('dbo.InvoiceRecognitionLog', 'SupplierCountry') IS NULL
                    OR COL_LENGTH('dbo.InvoiceRecognitionLog', 'SupplierAddress') IS NULL
                    OR OBJECT_ID('dbo.InvoiceSupplierNameAlias', 'U') IS NULL
                    OR OBJECT_ID('dbo.InvoiceSupplierNameAliasInvoice', 'U') IS NULL THEN 0 ELSE 1 END;
                """;
            if (Convert.ToInt32(await schema.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) != 1)
                throw new InvalidOperationException("Run Sql/UpgradeSupplierNameResolution.sql before supplier resolution.");
        }
    }

    // Full historical replay is explicit; normal batch processing must call RunBatchAsync.
    public Task<SupplierResolutionSummary> RunAsync(TextWriter progress, CancellationToken token) =>
        RunCoreAsync(null, progress, token);

    public Task<SupplierResolutionSummary> RunBatchAsync(IReadOnlyCollection<object> uploadedInvoiceIds, TextWriter progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(uploadedInvoiceIds);
        return RunCoreAsync(uploadedInvoiceIds, progress, token);
    }

    private async Task<SupplierResolutionSummary> RunCoreAsync(IReadOnlyCollection<object>? uploadedInvoiceIds, TextWriter progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (uploadedInvoiceIds is { Count: 0 }) return new(0, 0, 0, 0, 0);
        await ValidateSchemaAsync(token);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        if (uploadedInvoiceIds != null)
            await CreateBatchScopeAsync(connection, uploadedInvoiceIds, token);
        var rows = await LoadAsync(connection, uploadedInvoiceIds != null, token);
        var updated = 0;
        var pending = 0;
        var skipped = 0;
        var failed = 0;
        await progress.WriteLineAsync($"============== Supplier resolution Rows: {rows.Count}");
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var result = await ResolveAsync(connection, row, token);
                if (result == "Updated") updated++;
                else if (result == "Pending") pending++;
                else skipped++;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                failed++;
                await progress.WriteLineAsync($"Supplier resolution: UploadedInvoice {row.UploadedInvoiceId}, log {row.LogId} failed ({error.GetType().Name}): {error.Message}");
            }
        }
        return new(rows.Count, updated, pending, skipped, failed);
    }

    private static async Task CreateBatchScopeAsync(SqlConnection connection, IReadOnlyCollection<object> uploadedInvoiceIds, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        // Keep the source identifier type, but use an expression so SELECT INTO
        // cannot copy Id's IDENTITY property: these are existing invoice IDs.
        command.CommandText = """
            SELECT TOP (0) ISNULL(Id, Id) AS UploadedInvoiceId INTO #SupplierBatchInvoiceIds FROM dbo.UploadedInvoice;
            CREATE UNIQUE CLUSTERED INDEX IX_SupplierBatchInvoiceIds ON #SupplierBatchInvoiceIds(UploadedInvoiceId);
            """;
        await command.ExecuteNonQueryAsync(token);
        // Bound statement size to stay below SQL Server's parameter and VALUES limits.
        foreach (var chunk in uploadedInvoiceIds.Distinct().Chunk(500))
        {
            command.Parameters.Clear();
            var values = new List<string>();
            for (var index = 0; index < chunk.Length; index++)
            {
                var parameter = "@id" + index.ToString(CultureInfo.InvariantCulture);
                Add(command, parameter, chunk[index]);
                values.Add("(" + parameter + ")");
            }
            command.CommandText = "INSERT INTO #SupplierBatchInvoiceIds(UploadedInvoiceId) VALUES " + string.Join(",", values);
            await command.ExecuteNonQueryAsync(token);
        }
    }

    private static async Task<List<SavedInvoice>> LoadAsync(SqlConnection connection, bool batchOnly, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        var batchFilter = batchOnly
            ? "AND EXISTS (SELECT 1 FROM #SupplierBatchInvoiceIds b WHERE b.UploadedInvoiceId = l.UploadedInvoiceId)"
            : "";
        command.CommandText = $"""
            WITH Ranked AS
            (
                SELECT l.*, ROW_NUMBER() OVER (PARTITION BY l.UploadedInvoiceId ORDER BY l.CompletedAtUtc DESC, l.Id DESC) AS rn
                FROM dbo.InvoiceRecognitionLog l WHERE l.Status = 'Success' AND l.IsSelected = 1
                    {batchFilter}
            )
            SELECT l.Id, l.UploadedInvoiceId, l.InvoiceId, l.FilePath, l.SupplierName,
                l.ResolvedSupplierName, ui.InvoiceVendorName, l.VATNumber, l.CompanyNumber,
                l.SupplierCountry, l.SupplierAddress, l.RawAzureResultJson, l.ParsedResultJson, l.DocumentType
            FROM Ranked l
            INNER JOIN dbo.UploadedInvoice ui ON ui.Id = l.UploadedInvoiceId AND ui.InvoiceId = l.InvoiceId
            INNER JOIN dbo.Invoice i ON i.Id = ui.InvoiceId AND i.FilePath = l.FilePath
            WHERE l.rn = 1
            ORDER BY l.Id;
            """;
        var rows = new List<SavedInvoice>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            string Text(int ordinal) => reader.IsDBNull(ordinal) ? "" : reader.GetString(ordinal);
            var originalName = Text(4);
            var evidence = new SupplierEvidence(originalName, Text(7), Text(8), Text(9), Text(10));
            // Historical logs can be enriched locally using the preserved structured JSON.
            InvoiceData? parsed = null;
            if (!reader.IsDBNull(11))
            {
                try
                {
                    using var json = JsonDocument.Parse(Text(11));
                    parsed = AzureInvoiceParser.Parse(json.RootElement, Text(13)).Invoice;
                }
                catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException) { }
            }
            if (parsed == null && !reader.IsDBNull(12))
            {
                try { parsed = JsonSerializer.Deserialize<InvoiceData>(Text(12)); }
                catch (JsonException) { }
            }
            if (parsed != null)
                evidence = evidence with
                {
                    VATNumber = Nonempty(evidence.VATNumber, parsed.SupplierVatNumber),
                    CompanyNumber = Nonempty(evidence.CompanyNumber, parsed.SupplierCompanyNumber),
                    Country = Nonempty(evidence.Country, parsed.SupplierCountry),
                    Address = Nonempty(evidence.Address, parsed.Address)
                };
            rows.Add(new(reader.GetInt64(0), reader.GetValue(1), reader.GetValue(2), Text(3), originalName,
                reader.IsDBNull(5) ? null : Text(5), reader.IsDBNull(6) ? null : Text(6), evidence));
        }
        return rows;
    }

    private static string Nonempty(string first, string second) => string.IsNullOrWhiteSpace(first) ? second : first;

    private static async Task<string> ResolveAsync(SqlConnection connection, SavedInvoice row, CancellationToken token)
    {
        var e = row.Evidence;
        // Invalid/short names never register aliases, but still retain safely sized vendor details.
        if (e.VATNumber.Length > 100 || e.CompanyNumber.Length > 100 || e.Country.Length > 100 || e.Address.Length > 1000)
            return "Skipped";
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, token);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE dbo.InvoiceRecognitionLog
            SET VATNumber = NULLIF(@vat, ''), CompanyNumber = NULLIF(@company, ''), SupplierCountry = NULLIF(@country, ''), SupplierAddress = NULLIF(@address, '')
            WHERE Id = @logId;
            """;
        Add(command, "@logId", row.LogId);
        Add(command, "@vat", e.VATNumber);
        Add(command, "@company", e.CompanyNumber);
        Add(command, "@country", e.Country);
        Add(command, "@address", e.Address);
        await command.ExecuteNonQueryAsync(token);
        if (!SupplierNameMatching.IsEligible(e))
        {
            await transaction.CommitAsync(token);
            return "Skipped";
        }

        // Serialize resolver instances, including their alias insert and CSV refresh operations.
        command.CommandText = """
            DECLARE @lockResult int;
            EXEC @lockResult = sys.sp_getapplock @Resource = 'InvoiceSupplierNameResolution',
                @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 30000;
            IF @lockResult < 0 THROW 50013, 'Could not acquire supplier resolution lock.', 1;
            """;
        await command.ExecuteNonQueryAsync(token);

        Add(command, "@aliasKey", SupplierNameMatching.NameKey(e.Name));
        Add(command, "@vatKey", SupplierNameMatching.IdentifierKey(e.VATNumber));
        Add(command, "@companyKey", SupplierNameMatching.IdentifierKey(e.CompanyNumber));
        Add(command, "@countryKey", SupplierNameMatching.CountryKey(e.Country));
        Add(command, "@name", e.Name.Trim());
        Add(command, "@uploadedId", row.UploadedInvoiceId);
        command.CommandText = """
            IF NOT EXISTS (SELECT 1 FROM dbo.InvoiceSupplierNameAlias WITH (UPDLOCK, HOLDLOCK)
                WHERE AliasKey = @aliasKey AND VATKey = @vatKey AND CompanyKey = @companyKey AND CountryKey = @countryKey)
                INSERT dbo.InvoiceSupplierNameAlias
                    (OriginalName, AliasKey, VATNumber, CompanyNumber, SupplierCountry, SupplierAddress, VATKey, CompanyKey, CountryKey)
                VALUES (@name, @aliasKey, NULLIF(@vat, ''), NULLIF(@company, ''), NULLIF(@country, ''), NULLIF(@address, ''), @vatKey, @companyKey, @countryKey);
            SELECT Id, IsApproved, NormalizedName FROM dbo.InvoiceSupplierNameAlias WITH (UPDLOCK, HOLDLOCK)
                WHERE AliasKey = @aliasKey AND VATKey = @vatKey AND CompanyKey = @companyKey AND CountryKey = @countryKey;
            """;
        long aliasId;
        bool isApproved;
        string? approvedName;
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            await reader.ReadAsync(token);
            aliasId = reader.GetInt64(0);
            isApproved = reader.GetBoolean(1);
            approvedName = reader.IsDBNull(2) ? null : reader.GetString(2);
        }
        command.CommandText = """
            SELECT Id, AliasKey, NormalizedName, VATKey, CompanyKey, CountryKey, ISNULL(SupplierAddress, '')
            FROM dbo.InvoiceSupplierNameAlias WHERE IsApproved = 1
                AND (AliasKey = @aliasKey OR (VATKey = @vatKey AND @vatKey <> '')
                    OR (CompanyKey = @companyKey AND @companyKey <> '')
                    OR (SupplierAddress = @address AND @address <> ''));
            """;
        var approved = new List<ApprovedSupplierAlias>();
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
                approved.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6)));
        var suggestion = SupplierNameMatching.Suggest(e, approved);
        var canApply = isApproved && !string.IsNullOrWhiteSpace(approvedName) && suggestion != null
            && string.Equals(suggestion.Name, approvedName.Trim(), StringComparison.OrdinalIgnoreCase);
        Add(command, "@aliasId", aliasId);
        Add(command, "@suggestion", suggestion?.Name);
        Add(command, "@source", suggestion?.Source);
        command.CommandText = """
            IF NOT EXISTS (SELECT 1 FROM dbo.InvoiceSupplierNameAliasInvoice WITH (UPDLOCK, HOLDLOCK)
                WHERE AliasId = @aliasId AND UploadedInvoiceId = @uploadedId)
                INSERT dbo.InvoiceSupplierNameAliasInvoice(AliasId, UploadedInvoiceId) VALUES (@aliasId, @uploadedId);
            UPDATE a SET UploadedInvoiceIds = STUFF((
                SELECT ',' + CONVERT(nvarchar(100), x.UploadedInvoiceId)
                FROM dbo.InvoiceSupplierNameAliasInvoice x WHERE x.AliasId = @aliasId
                ORDER BY x.UploadedInvoiceId FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 1, '')
            FROM dbo.InvoiceSupplierNameAlias a WHERE a.Id = @aliasId;
            -- Approved names are changed only by a human; suggestions remain unapproved.
            UPDATE dbo.InvoiceSupplierNameAlias
            SET NormalizedName = @suggestion, MatchSource = COALESCE(@source, 'Exact'), UpdatedOn = SYSUTCDATETIME()
            WHERE Id = @aliasId AND IsApproved = 0;
            """;
        await command.ExecuteNonQueryAsync(token);
        var changed = 0;
        if (canApply)
        {
            Add(command, "@normalizedName", approvedName!.Trim());
            Add(command, "@invoiceId", row.InvoiceId);
            Add(command, "@filePath", row.FilePath);
            Add(command, "@originalName", row.OriginalName);
            Add(command, "@previousName", row.PreviousName);
            Add(command, "@currentName", row.CurrentName);
            command.CommandText = """
                UPDATE ui SET InvoiceVendorName = @normalizedName
                FROM dbo.UploadedInvoice ui
                INNER JOIN dbo.Invoice i ON i.Id = ui.InvoiceId
                WHERE ui.Id = @uploadedId AND ui.InvoiceId = @invoiceId AND i.FilePath = @filePath
                    AND ISNULL(ui.IsReviewed, 0) = 0 AND ui.TaskStatus = 'Success'
                    AND (ui.InvoiceVendorName = @originalName OR ui.InvoiceVendorName = @previousName)
                    AND (ui.InvoiceVendorName = @currentName OR (ui.InvoiceVendorName IS NULL AND @currentName IS NULL))
                    AND NOT EXISTS (SELECT 1 FROM dbo.InvoiceRecognitionLog newer
                        WHERE newer.UploadedInvoiceId = ui.Id AND newer.Status = 'Success' AND newer.IsSelected = 1
                            AND (newer.CompletedAtUtc > (SELECT CompletedAtUtc FROM dbo.InvoiceRecognitionLog WHERE Id = @logId)
                                OR (newer.CompletedAtUtc = (SELECT CompletedAtUtc FROM dbo.InvoiceRecognitionLog WHERE Id = @logId) AND newer.Id > @logId)));
                SELECT CONVERT(int, @@ROWCOUNT);
                """;
            changed = Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
            if (changed == 1)
            {
                command.CommandText = """
                    UPDATE dbo.InvoiceRecognitionLog SET SupplierNameAliasId = @aliasId,
                        ResolvedSupplierName = @normalizedName, SupplierMatchSource = @source, SupplierResolvedOn = SYSUTCDATETIME()
                    WHERE Id = @logId;
                    """;
                await command.ExecuteNonQueryAsync(token);
            }
        }
        await transaction.CommitAsync(token);
        return changed == 1 ? "Updated" : canApply ? "Skipped" : "Pending";
    }

    private static void Add(SqlCommand command, string name, object? value)
    {
        if (value is string text) command.Parameters.Add(name, SqlDbType.NVarChar, -1).Value = text;
        else if (value == null) command.Parameters.Add(name, SqlDbType.NVarChar, -1).Value = DBNull.Value;
        else command.Parameters.AddWithValue(name, value);
    }
}
