using QbReclass.Core.Model;

namespace QbReclass.Core.Session;

/// <summary>
/// Information about the open QuickBooks connection, captured when the session opens.
/// </summary>
public sealed record QbSessionInfo
{
    public required CompanyIdentity Company { get; init; }

    /// <summary>qbXML version negotiated for this session, e.g. "16.0".</summary>
    public required string QbXmlVersion { get; init; }

    /// <summary>Every qbXML version the connected QuickBooks reported as supported.</summary>
    public IReadOnlyList<string> SupportedQbXmlVersions { get; init; } = [];

    /// <summary>"32-bit" / "64-bit" where the host reports it; null when unknown.</summary>
    public string? HostBitness { get; init; }

    /// <summary>True when this session can never write, regardless of caller intent.</summary>
    public bool IsReadOnly { get; init; }
}

/// <summary>
/// The single seam between this utility and QuickBooks. Everything above this interface deals in
/// qbXML strings and normalized models; everything below deals in COM.
/// </summary>
/// <remarks>
/// Deliberately synchronous. The Desktop SDK request processor is a synchronous, single-threaded
/// COM component, and the spec calls for deliberately controlled, observable writes rather than
/// throughput (section 16). Callers that need responsiveness marshal this onto a worker thread.
/// </remarks>
public interface IQbSession : IDisposable
{
    QbSessionInfo Info { get; }

    bool IsOpen { get; }

    /// <summary>
    /// Sends one qbXML request envelope and returns the raw response envelope.
    /// </summary>
    /// <exception cref="QbSessionException">The session dropped or QuickBooks refused the call.</exception>
    string SendRequest(string qbXmlRequest);
}

/// <summary>
/// Raised when the transport itself fails: QuickBooks closed, the company file was switched, the
/// user revoked authorization. Distinct from a qbXML status code, which is a normal response.
/// </summary>
public class QbSessionException : Exception
{
    public QbSessionException(string message) : base(message)
    {
    }

    public QbSessionException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>Raised when the open company file is not the one the job is bound to (spec section 10).</summary>
public sealed class QbCompanyMismatchException : QbSessionException
{
    public QbCompanyMismatchException(CompanyIdentity expected, CompanyIdentity actual)
        : base($"Job is bound to '{expected}' but QuickBooks has '{actual}' open. Execution refused.")
    {
        Expected = expected;
        Actual = actual;
    }

    public CompanyIdentity Expected { get; }

    public CompanyIdentity Actual { get; }
}
