using System.Security.Cryptography;
using System.Text;

namespace QbReclass.Core.Model;

/// <summary>
/// Identity of the QuickBooks company a job is bound to (spec FR-001, section 10 "Company-file lock").
/// </summary>
/// <remarks>
/// The Desktop SDK does not expose a durable company-file GUID. <c>CompanyQueryRs</c> returns the
/// company name, legal name, full file path and EIN; <c>HostQueryRs</c> returns the product and
/// file mode. <see cref="Fingerprint"/> is a hash over the stable subset of those values and is the
/// strongest available identity check, not a guarantee. A restored backup at a different path
/// produces a different fingerprint, which is deliberately conservative: the user is re-prompted.
/// </remarks>
public sealed record CompanyIdentity
{
    public required string CompanyName { get; init; }
    public string? LegalCompanyName { get; init; }

    /// <summary>Full path to the .QBW file as reported by QuickBooks.</summary>
    public required string CompanyFileName { get; init; }

    /// <summary>Employer identification number, when the company file has one.</summary>
    public string? Ein { get; init; }

    /// <summary>QuickBooks product string, e.g. "QuickBooks Desktop Enterprise Solutions 24.0".</summary>
    public string? ProductName { get; init; }

    /// <summary>Highest qbXML version supported by the connected QuickBooks.</summary>
    public string? SupportedQbXmlVersion { get; init; }

    /// <summary>"SingleUser" or "MultiUser" as reported by <c>HostQueryRs/QBFileMode</c>.</summary>
    public string? FileMode { get; init; }

    /// <summary>Stable hash used to bind a job to one company. See remarks on the type.</summary>
    public string Fingerprint => ComputeFingerprint(CompanyFileName, LegalCompanyName ?? CompanyName, Ein);

    public static string ComputeFingerprint(string companyFileName, string legalName, string? ein)
    {
        var material = string.Join(
            '\u001F',
            companyFileName.Trim().ToUpperInvariant(),
            legalName.Trim().ToUpperInvariant(),
            (ein ?? string.Empty).Trim());
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash);
    }

    /// <summary>True when <paramref name="other"/> is the same company file this job was bound to.</summary>
    public bool Matches(CompanyIdentity other) =>
        string.Equals(Fingerprint, other.Fingerprint, StringComparison.Ordinal);

    public override string ToString() => $"{CompanyName} ({CompanyFileName})";
}
