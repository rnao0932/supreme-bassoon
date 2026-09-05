using System.Globalization;
using System.Reflection;
using System.Runtime.Versioning;
using QbReclass.Core.Adapters;
using QbReclass.Core.Model;
using QbReclass.Core.QbXml;
using QbReclass.Core.Services;
using QbReclass.Core.Session;

namespace QbReclass.QuickBooks;

/// <summary>
/// How the utility attaches to QuickBooks.
/// </summary>
/// <remarks>
/// These are this application's own names, not the SDK's numbering. Published descriptions of
/// <c>QBXMLRPConnectionType</c> disagree about whether it is 0-based or 1-based, and passing the
/// wrong value produces "The requested connection type could not be found". The default path
/// therefore calls <c>OpenConnection</c>, which takes no connection-type argument at all; see
/// <see cref="QbComSession.OpenConnection"/>.
/// </remarks>
public enum QbConnectionType
{
    /// <summary>
    /// QuickBooks must already be running with the company file open. Opened through
    /// <c>OpenConnection</c>, so no connection-type constant is involved.
    /// </summary>
    LocalAlreadyRunning = 0,

    /// <summary>
    /// QuickBooks may be started to service the request. Needs <c>OpenConnection2</c> and therefore
    /// the SDK's constant, which is discovered by attempt rather than assumed.
    /// </summary>
    LocalLaunchIfNeeded = 1,
}

/// <summary>How the company file should be opened.</summary>
public enum QbFileMode
{
    DoNotCare = 0,
    SingleUser = 1,
    MultiUser = 2,
}

/// <summary>Settings for opening a QuickBooks session.</summary>
public sealed record QbConnectionOptions
{
    /// <summary>Name QuickBooks shows the user in its authorization prompt and its integrated-application list.</summary>
    public string ApplicationName { get; init; } = "Bulk Reclassification Utility";

    /// <summary>
    /// Full path to a .QBW file, or empty to use whichever company QuickBooks currently has open.
    /// Empty is the safer default: it cannot open the wrong file.
    /// </summary>
    public string CompanyFilePath { get; init; } = string.Empty;

    public QbConnectionType ConnectionType { get; init; } = QbConnectionType.LocalAlreadyRunning;

    public QbFileMode FileMode { get; init; } = QbFileMode.DoNotCare;

    /// <summary>When true, the session refuses to carry any modification request.</summary>
    public bool ReadOnly { get; init; } = true;
}

/// <summary>
/// A live QuickBooks Desktop session over the SDK's <c>QBXMLRP2.RequestProcessor</c> COM component.
/// </summary>
/// <remarks>
/// <para>
/// The request processor is bound late, through <see cref="Type.GetTypeFromProgID(string)"/> and
/// <see cref="Type.InvokeMember(string, BindingFlags, Binder, object, object[])"/>, so the build
/// needs no interop assembly and no reference to a particular SDK version. Whatever the workstation
/// has registered is what gets used, and a missing SDK produces a clear message instead of a
/// <c>FileNotFoundException</c> for an interop DLL.
/// </para>
/// <para>
/// No credentials are collected or stored (spec section 17). Authorization is QuickBooks' own: the
/// first connection raises QuickBooks' consent dialog, and the grant lives in the company file's
/// integrated-application list, where the user can revoke it.
/// </para>
/// <para>
/// The component is apartment-threaded. Callers must create and use a session from a single STA
/// thread.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class QbComSession : IQbSession
{
    private const string ProgId = "QBXMLRP2.RequestProcessor";

    private readonly bool _readOnly;
    private object? _processor;
    private string? _ticket;
    private bool _connectionOpen;

    private QbComSession(object processor, string ticket, QbSessionInfo info, bool readOnly)
    {
        _processor = processor;
        _ticket = ticket;
        _readOnly = readOnly;
        _connectionOpen = true;
        Info = info;
    }

    public QbSessionInfo Info { get; private set; }

    public bool IsOpen => _ticket is not null;

    /// <summary>
    /// Opens a connection and a session, negotiates the qbXML version, and reads company identity.
    /// </summary>
    /// <exception cref="QbSessionException">
    /// The SDK is not installed, QuickBooks refused the connection, or no company file is open.
    /// </exception>
    public static QbComSession Connect(QbConnectionOptions options, AdapterRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var type = Type.GetTypeFromProgID(ProgId, throwOnError: false)
            ?? throw new QbSessionException(
                QbConnectionDiagnostics.Guidance(QbConnectionDiagnostics.ClassNotRegistered, ProgId)!);

        object processor;
        try
        {
            // Deliberately inside a guard. A machine without the SDK reaches exactly here, and the
            // bare COM failure ("Retrieving the COM class factory ... 80040154 Class not
            // registered") is the least useful message the application can show on a first run.
            processor = Activator.CreateInstance(type)
                ?? throw new QbSessionException($"Could not create an instance of '{ProgId}'.");
        }
        catch (Exception ex) when (ex is not QbSessionException)
        {
            throw new QbSessionException(QbConnectionDiagnostics.Describe(ex, ProgId), ex);
        }

        string ticket;
        var connectionOpen = false;

        try
        {
            OpenConnection(processor, options);
            connectionOpen = true;

            ticket = Invoke(processor, "BeginSession", options.CompanyFilePath, (int)options.FileMode) as string
                ?? throw new QbSessionException("QuickBooks did not return a session ticket.");
        }
        catch (Exception ex) when (ex is not QbSessionException)
        {
            if (connectionOpen)
            {
                TryInvoke(processor, "CloseConnection");
            }

            throw new QbSessionException(QbConnectionDiagnostics.Describe(ex, ProgId), ex);
        }

        var versions = ReadSupportedVersions(processor, ticket);
        var negotiated = QbXmlRequestBuilder.NegotiateVersion(versions);
        var companyFile = TryInvoke(processor, "GetCurrentCompanyFileName", ticket) as string;

        // Provisional identity so a QueryService can be built; replaced below with what QuickBooks
        // actually reports.
        var provisional = new QbSessionInfo
        {
            Company = new CompanyIdentity
            {
                CompanyName = "(reading company identity)",
                CompanyFileName = companyFile ?? options.CompanyFilePath,
            },
            QbXmlVersion = negotiated,
            SupportedQbXmlVersions = versions,
            IsReadOnly = options.ReadOnly,
        };

        var session = new QbComSession(processor, ticket, provisional, options.ReadOnly);

        try
        {
            var query = new QueryService(session, registry ?? AdapterRegistry.Default);
            var host = query.LoadHostInfo();
            var company = query.LoadCompanyIdentity(host, companyFile);

            // Re-negotiate from what QuickBooks reported.
            //
            // The version chosen above came from QBXMLVersionsForSession, which not every request
            // processor answers in a shape this code can read; when it does not, the fallback is the
            // lowest version this utility can construct. HostQueryRs carries the same list and is
            // answered by every edition, so the authoritative answer arrives a moment later - and
            // without this, a 2024 installation that supports qbXML 16.0 would spend the whole
            // session talking 8.0 and losing every element added since.
            var effectiveVersions = host.SupportedQbXmlVersions.Count > 0
                ? host.SupportedQbXmlVersions
                : versions;

            var effectiveVersion = host.SupportedQbXmlVersions.Count > 0
                ? QbXmlRequestBuilder.NegotiateVersion(host.SupportedQbXmlVersions)
                : negotiated;

            session.Info = provisional with
            {
                Company = company,
                QbXmlVersion = effectiveVersion,
                SupportedQbXmlVersions = effectiveVersions,
                HostBitness = Environment.Is64BitProcess ? "64-bit" : "32-bit",
            };
        }
        catch
        {
            session.Dispose();
            throw;
        }

        return session;
    }

    public string SendRequest(string qbXmlRequest)
    {
        ArgumentException.ThrowIfNullOrEmpty(qbXmlRequest);

        if (_ticket is null || _processor is null)
        {
            throw new QbSessionException("The QuickBooks session is closed.");
        }

        if (_readOnly && IsModificationRequest(qbXmlRequest))
        {
            // Defence in depth: the service layer checks this too, but a read-only session must not
            // be able to carry a write even if something above it is wrong.
            throw new QbSessionException(
                "This QuickBooks session was opened read-only and cannot carry a modification request.");
        }

        try
        {
            return Invoke(_processor, "ProcessRequest", _ticket, qbXmlRequest) as string
                ?? throw new QbSessionException("QuickBooks returned no response.");
        }
        catch (Exception ex) when (ex is not QbSessionException)
        {
            throw new QbSessionException(QbConnectionDiagnostics.Describe(ex, ProgId), ex);
        }
    }

    /// <summary>
    /// Requests that read but whose names would otherwise look like writes.
    /// <c>TxnDisplayAdd</c> adds a window to the QuickBooks user interface, not a transaction.
    /// </summary>
    private static readonly HashSet<string> NonMutatingExceptions = new(StringComparer.Ordinal)
    {
        "TxnDisplayAddRq",
        "TxnDisplayModRq",
    };

    /// <summary>
    /// True when the envelope carries at least one request that changes QuickBooks data.
    /// </summary>
    /// <remarks>
    /// Decided from the parsed request element names rather than by scanning for substrings: a memo
    /// containing the text "ModRq" must not make a query look like a write, and a real write must
    /// not slip past because of whitespace. An envelope that will not parse is treated as mutating,
    /// because a read-only session has nothing to gain by sending something it cannot inspect.
    /// </remarks>
    internal static bool IsModificationRequest(string qbXmlRequest)
    {
        System.Xml.Linq.XDocument doc;
        try
        {
            doc = System.Xml.Linq.XDocument.Parse(qbXmlRequest);
        }
        catch (System.Xml.XmlException)
        {
            return true;
        }

        var requests = doc.Root?.Element("QBXMLMsgsRq")?.Elements();
        if (requests is null)
        {
            return true;
        }

        foreach (var request in requests)
        {
            var name = request.Name.LocalName;

            if (NonMutatingExceptions.Contains(name))
            {
                continue;
            }

            if (name.EndsWith("ModRq", StringComparison.Ordinal)
                || name.EndsWith("AddRq", StringComparison.Ordinal)
                || name.EndsWith("DelRq", StringComparison.Ordinal)
                || name.EndsWith("VoidRq", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Opens the connection to the request processor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ordinary case uses <c>OpenConnection</c>, whose signature is
    /// <c>(appID, appName)</c> — it always opens a local connection and needs no connection-type
    /// constant. That matters because <c>QBXMLRPConnectionType</c>'s numbering is inconsistently
    /// documented: passing 0 for "local" against QuickBooks Desktop 2021 is rejected with "The
    /// requested connection type could not be found". Not naming the constant cannot be wrong.
    /// </para>
    /// <para>
    /// Starting QuickBooks on demand does require <c>OpenConnection2</c>, so there the candidate
    /// values are tried in turn and the first accepted one is used. Every candidate names a
    /// <i>local</i> connection under one numbering or the other, so a wrong guess fails to connect
    /// rather than reaching somewhere unintended.
    /// </para>
    /// </remarks>
    private static void OpenConnection(object processor, QbConnectionOptions options)
    {
        var attempts = new List<string>();

        // OpenConnection needs no connection-type constant and is the documented way to attach to
        // a local QuickBooks, so it is tried first.
        if (TryOpen(processor, () => Invoke(processor, "OpenConnection", string.Empty, options.ApplicationName),
                "OpenConnection", attempts))
        {
            return;
        }

        // Then OpenConnection2 across the plausible constants. QBXMLRPConnectionType is
        // inconsistently documented as 0-based or 1-based, so rather than pick one, each candidate
        // that names a local connection under either numbering is tried in turn. A wrong guess is
        // refused by QuickBooks; it cannot reach somewhere unintended.
        var candidates = options.ConnectionType == QbConnectionType.LocalLaunchIfNeeded
            ? new[] { 2, 3, 0, 1 }   // launch-UI first, then plain local
            : new[] { 0, 1, 2, 3 };  // plain local first

        foreach (var candidate in candidates)
        {
            if (TryOpen(
                    processor,
                    () => Invoke(processor, "OpenConnection2", string.Empty, options.ApplicationName, candidate),
                    $"OpenConnection2({candidate})",
                    attempts))
            {
                return;
            }
        }

        // Everything was refused. The message names what was tried, because "could not connect"
        // without that is the least useful thing this application can say.
        throw new QbSessionException(
            "Could not open a connection to QuickBooks." + Environment.NewLine + Environment.NewLine
            + "QuickBooks must be running with a company file open before connecting. If it is, "
            + "check that QuickBooks and this application are running as the same Windows user and "
            + "at the same elevation - a QuickBooks started as administrator is not visible to an "
            + "application that was not, and the reverse."
            + Environment.NewLine + Environment.NewLine
            + "Attempts made:" + Environment.NewLine
            + string.Join(Environment.NewLine, attempts));
    }

    /// <summary>
    /// Runs one connection attempt, recording what was tried and how it failed.
    /// </summary>
    /// <remarks>
    /// Every failure is kept rather than only the last. When none of the attempts works, the list
    /// is what distinguishes "QuickBooks is not running" from "this edition numbers the connection
    /// constants differently", and the two need different fixes.
    /// </remarks>
    private static bool TryOpen(object processor, Action attempt, string label, List<string> attempts)
    {
        try
        {
            attempt();
            attempts.Add($"  {label}: succeeded");
            return true;
        }
        catch (Exception ex)
        {
            var actual = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            attempts.Add($"  {label}: {actual.Message}");
            return false;
        }
    }

    private static IReadOnlyList<string> ReadSupportedVersions(object processor, string ticket)
    {
        var raw = TryInvoke(processor, "QBXMLVersionsForSession", ticket);

        return raw switch
        {
            string[] typed => typed,
            object[] boxed => boxed.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty)
                .Where(v => v.Length > 0)
                .ToArray(),
            System.Collections.IEnumerable enumerable => enumerable.Cast<object?>()
                .Select(v => Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty)
                .Where(v => v.Length > 0)
                .ToArray(),

            // Older request processors expose only ProcessRequest; fall back to the minimum this
            // utility can construct rather than guessing high.
            _ => [QbXmlRequestBuilder.MinimumVersion],
        };
    }

    private static object? Invoke(object target, string member, params object?[] args) =>
        target.GetType().InvokeMember(
            member,
            BindingFlags.InvokeMethod,
            binder: null,
            target,
            args,
            CultureInfo.InvariantCulture);

    private static object? TryInvoke(object target, string member, params object?[] args)
    {
        try
        {
            return Invoke(target, member, args);
        }
        catch (Exception)
        {
            // Optional capability the installed request processor does not expose.
            return null;
        }
    }

    public void Dispose()
    {
        if (_processor is null)
        {
            return;
        }

        if (_ticket is not null)
        {
            TryInvoke(_processor, "EndSession", _ticket);
            _ticket = null;
        }

        if (_connectionOpen)
        {
            TryInvoke(_processor, "CloseConnection");
            _connectionOpen = false;
        }

        if (System.Runtime.InteropServices.Marshal.IsComObject(_processor))
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(_processor);
        }

        _processor = null;
    }
}
