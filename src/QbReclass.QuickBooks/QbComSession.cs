using System.Globalization;
using System.Reflection;
using System.Runtime.Versioning;
using QbReclass.Core.Adapters;
using QbReclass.Core.Model;
using QbReclass.Core.QbXml;
using QbReclass.Core.Services;
using QbReclass.Core.Session;

namespace QbReclass.QuickBooks;

/// <summary>How the utility attaches to QuickBooks.</summary>
public enum QbConnectionType
{
    /// <summary>QuickBooks must already be running with the company file open.</summary>
    LocalAlreadyRunning = 0,

    /// <summary>QuickBooks may be launched without its user interface to service the request.</summary>
    LocalLaunchIfNeeded = 2,
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
                $"The QuickBooks Desktop SDK component '{ProgId}' is not registered on this machine. "
                + "Install the QuickBooks Desktop SDK, and confirm its bitness matches this "
                + "application's (a 64-bit process cannot load a 32-bit request processor).");

        var processor = Activator.CreateInstance(type)
            ?? throw new QbSessionException($"Could not create an instance of '{ProgId}'.");

        string ticket;
        var connectionOpen = false;

        try
        {
            Invoke(processor, "OpenConnection2", string.Empty, options.ApplicationName, (int)options.ConnectionType);
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

            throw new QbSessionException(TranslateComFailure(ex), ex);
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

            session.Info = provisional with
            {
                Company = company,
                SupportedQbXmlVersions = host.SupportedQbXmlVersions.Count > 0
                    ? host.SupportedQbXmlVersions
                    : versions,
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
            throw new QbSessionException(TranslateComFailure(ex), ex);
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

    /// <summary>
    /// Turns a COM failure into something an accountant can act on. The raw HRESULT text from the
    /// request processor is unhelpful on its own.
    /// </summary>
    private static string TranslateComFailure(Exception ex)
    {
        var message = ex is TargetInvocationException { InnerException: { } inner } ? inner.Message : ex.Message;

        if (message.Contains("0x80040408", StringComparison.OrdinalIgnoreCase)
            || message.Contains("could not start QuickBooks", StringComparison.OrdinalIgnoreCase))
        {
            return "QuickBooks could not be started. Open QuickBooks and the company file, then try again. "
                + $"(Original error: {message})";
        }

        if (message.Contains("0x80040401", StringComparison.OrdinalIgnoreCase))
        {
            return "QuickBooks does not have a company file open. Open the company file and try again. "
                + $"(Original error: {message})";
        }

        if (message.Contains("0x80040420", StringComparison.OrdinalIgnoreCase))
        {
            return "This application has not been authorized to access the company file. In QuickBooks, "
                + "grant access under Edit > Preferences > Integrated Applications, then try again. "
                + $"(Original error: {message})";
        }

        if (message.Contains("0x80040416", StringComparison.OrdinalIgnoreCase))
        {
            return "QuickBooks is busy or a modal dialog is open. Close any open dialog in QuickBooks and "
                + $"try again. (Original error: {message})";
        }

        return $"The QuickBooks connection failed: {message}";
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
