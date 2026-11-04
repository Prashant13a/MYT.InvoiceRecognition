using System.Text;
using System.Text.RegularExpressions;

namespace MYT.InvoiceRecognition.ConsoleApp;

public sealed record SupplierEvidence(string Name, string VATNumber = "", string CompanyNumber = "", string Country = "", string Address = "");
public sealed record ApprovedSupplierAlias(long Id, string AliasKey, string NormalizedName, string VATKey, string CompanyKey, string CountryKey, string Address);
public sealed record SupplierNameSuggestion(string Name, string Source);

public static class SupplierNameMatching
{
    public static string NameKey(string? value) => Regex.Replace(
        Regex.Replace((value ?? "").Normalize(NormalizationForm.FormKC).ToUpperInvariant(), @"[^\p{L}\p{N}]+", " "), @"\s+", " ").Trim();

    public static string IdentifierKey(string? value) => Regex.Replace((value ?? "").ToUpperInvariant(), @"[^A-Z0-9]", "");

    public static string CountryKey(string? value) => NameKey(value) switch
    {
        "GBR" or "UK" or "UNITED KINGDOM" or "GREAT BRITAIN" => "GB",
        "IRL" or "IRELAND" => "IE",
        var country => country
    };

    public static bool IsEligible(SupplierEvidence evidence) => evidence.Name.Trim().Length > 3
        && evidence.Name.Length <= 500 && NameKey(evidence.Name).Length is > 0 and <= 450
        && evidence.VATNumber.Length <= 100 && evidence.CompanyNumber.Length <= 100 && evidence.Country.Length <= 100;

    public static SupplierNameSuggestion? Suggest(SupplierEvidence evidence, IEnumerable<ApprovedSupplierAlias> approved)
    {
        var name = NameKey(evidence.Name);
        var vat = IdentifierKey(evidence.VATNumber);
        var company = IdentifierKey(evidence.CompanyNumber);
        var country = CountryKey(evidence.Country);
        var address = NameKey(evidence.Address);
        var matches = new List<SupplierNameSuggestion>();
        foreach (var alias in approved)
        {
            if (string.IsNullOrWhiteSpace(alias.NormalizedName)) continue;
            if (Conflicts(vat, alias.VATKey) || Conflicts(company, alias.CompanyKey) || Conflicts(country, alias.CountryKey)) continue;
            var source = vat.Length > 0 && vat == alias.VATKey ? "VATNumber"
                : company.Length > 0 && company == alias.CompanyKey && country.Length > 0 && country == alias.CountryKey ? "CompanyNumber"
                : name == alias.AliasKey ? "Alias"
                : address.Length > 10 && address == NameKey(alias.Address) ? "Address" : null;
            if (source != null) matches.Add(new(alias.NormalizedName.Trim(), source));
        }
        // Conflicting approved mappings require a human decision; never choose arbitrarily.
        if (matches.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1) return null;
        return matches.OrderBy(x => x.Source switch { "VATNumber" => 0, "CompanyNumber" => 1, "Alias" => 2, _ => 3 }).First();
    }

    private static bool Conflicts(string left, string right) => left.Length > 0 && right.Length > 0 && left != right;
}
