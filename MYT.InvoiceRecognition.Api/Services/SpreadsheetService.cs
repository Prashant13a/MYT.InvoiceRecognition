using System.Data;
using System.Text;
using ExcelDataReader;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using MYT.InvoiceRecognition.Api.Options;

namespace MYT.InvoiceRecognition.Api.Services;

public sealed class SpreadsheetService
{
    private readonly LimitsOptions _limits;

    public SpreadsheetService(IOptions<LimitsOptions> limits)
    {
        _limits = limits.Value;
    }

    public async Task<List<Dictionary<string, string>>> ReadAsync(IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length == 0 || file.Length > _limits.MaxUploadBytes) throw new ArgumentException("Uploaded file is empty or exceeds the upload limit.");
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension is not (".csv" or ".xls" or ".xlsx")) throw new ArgumentException("Only .csv, .xls and .xlsx files are supported.");
        await using var stream = new MemoryStream();
        await file.CopyToAsync(stream, cancellationToken);
        stream.Position = 0;
        return extension == ".csv" ? ReadCsv(stream) : ReadExcel(stream);
    }

    private List<Dictionary<string, string>> ReadCsv(Stream stream)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        string csv;
        try { csv = reader.ReadToEnd(); }
        catch (DecoderFallbackException)
        {
            stream.Position = 0;
            using var fallback = new StreamReader(stream, Encoding.Latin1, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            csv = fallback.ReadToEnd();
        }
        var rows = ParseCsv(csv);
        return ToRecords(rows);
    }

    private List<Dictionary<string, string>> ReadExcel(Stream stream)
    {
        using var reader = ExcelReaderFactory.CreateReader(stream);
        var rows = new List<List<string>>();
        do
        {
            while (reader.Read())
            {
                var cells = new List<string>();
                for (var i = 0; i < reader.FieldCount; i++) cells.Add(Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? "");
                if (cells.Any(cell => !string.IsNullOrWhiteSpace(cell))) rows.Add(cells);
                if (rows.Count > _limits.MaxRowsPerFile + 1) throw new ArgumentException("The spreadsheet contains too many rows.");
            }
            if (rows.Count > 0) break;
        }
        while (reader.NextResult());
        return ToRecords(rows);
    }

    private List<Dictionary<string, string>> ToRecords(List<List<string>> rows)
    {
        if (rows.Count < 2) throw new ArgumentException("Input must contain a header and at least one data row.");
        var headers = rows[0].Select(value => value.Trim().TrimStart('\uFEFF')).ToArray();
        if (headers.Any(string.IsNullOrWhiteSpace) || headers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Length) throw new ArgumentException("The file contains missing or duplicate column headers.");
        var result = new List<Dictionary<string, string>>();
        foreach (var cells in rows.Skip(1))
        {
            if (result.Count >= _limits.MaxRowsPerFile) throw new ArgumentException("The file contains too many rows.");
            var record = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headers.Length; i++) record[headers[i]] = i < cells.Count ? cells[i] : "";
            if (record.Values.Any(value => !string.IsNullOrWhiteSpace(value))) result.Add(record);
        }
        if (result.Count == 0) throw new ArgumentException("No data rows found in file.");
        return result;
    }

    public static List<List<string>> ParseCsv(string csv)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < csv.Length; i++)
        {
            var current = csv[i];
            if (current == '"')
            {
                if (quoted && i + 1 < csv.Length && csv[i + 1] == '"') { field.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (current == ',' && !quoted) { row.Add(field.ToString()); field.Clear(); }
            else if ((current == '\r' || current == '\n') && !quoted)
            {
                if (current == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n') i++;
                row.Add(field.ToString()); field.Clear();
                if (row.Any(value => value.Length > 0)) rows.Add(row);
                row = new List<string>();
            }
            else field.Append(current);
        }
        if (quoted) throw new ArgumentException("CSV contains an unclosed quoted field.");
        row.Add(field.ToString());
        if (row.Any(value => value.Length > 0)) rows.Add(row);
        return rows;
    }
}
