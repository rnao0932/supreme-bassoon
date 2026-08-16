using System.Reflection;
using System.Runtime.InteropServices;

namespace QbReclass.Core.Session;

/// <summary>
/// Turns a COM failure from the QuickBooks request processor into something the person at the
/// keyboard can act on.
/// </summary>
/// <remarks>
/// <para>
/// The raw text is unhelpful on its own — "Retrieving the COM class factory for component with
/// CLSID {…} failed … 80040154 Class not registered" tells an accountant nothing about what to do
/// next. Each mapped code below is paired with the concrete action that resolves it.
/// </para>
/// <para>
/// This lives in the cross-platform core rather than alongside the COM session so it can be tested.
/// The session itself cannot be exercised off Windows, and these messages are the first thing a
/// new installation shows when something is wrong, so they are worth covering.
/// </para>
/// </remarks>
public static class QbConnectionDiagnostics
{
    /// <summary>The component is registered but its implementation could not be loaded.</summary>
    public const int ClassNotRegistered = unchecked((int)0x80040154);

    /// <summary>QuickBooks could not be started.</summary>
    public const int CouldNotStartQuickBooks = unchecked((int)0x80040408);

    /// <summary>No company file is open and none was specified.</summary>
    public const int NoCompanyFileOpen = unchecked((int)0x80040401);

    /// <summary>The user has not authorized this application against the company file.</summary>
    public const int NotAuthorized = unchecked((int)0x80040420);

    /// <summary>QuickBooks is busy, or a modal dialog is blocking it.</summary>
    public const int QuickBooksBusy = unchecked((int)0x80040416);

    /// <summary>The company file is open in a mode that does not permit this connection.</summary>
    public const int WrongFileMode = unchecked((int)0x80040417);

    /// <summary>
    /// Produces an explanation and a next step for a failure raised while connecting to or talking
    /// to QuickBooks.
    /// </summary>
    /// <param name="exception">The exception as thrown, including any wrapper.</param>
    /// <param name="progId">ProgID being created, quoted back in the not-registered case.</param>
    public static string Describe(Exception exception, string progId = "QBXMLRP2.RequestProcessor")
    {
        ArgumentNullException.ThrowIfNull(exception);

        var actual = Unwrap(exception);
        var guidance = Guidance(actual.HResult, progId);

        return guidance is null
            ? $"The QuickBooks connection failed: {actual.Message}"
            : $"{guidance}{Environment.NewLine}{Environment.NewLine}Technical detail: {actual.Message}";
    }

    /// <summary>
    /// The actionable guidance for an HRESULT, or null when the code is not one we recognize.
    /// </summary>
    public static string? Guidance(int hresult, string progId = "QBXMLRP2.RequestProcessor") => hresult switch
    {
        ClassNotRegistered =>
            $"The QuickBooks Desktop SDK is not available to this application. The component "
            + $"'{progId}' is not registered, so there is nothing for it to connect through."
            + Environment.NewLine + Environment.NewLine
            + "Two causes, in order of likelihood:"
            + Environment.NewLine
            + "1. The QuickBooks Desktop SDK is not installed on this machine. Install it from "
            + "Intuit, then try again. QuickBooks itself being installed is not sufficient — the SDK "
            + "is a separate download."
            + Environment.NewLine
            + "2. A bitness mismatch. A 64-bit application cannot load a 32-bit request processor, "
            + "or the reverse. Rebuild for the other architecture (-r win-x86 rather than -r win-x64, "
            + "or the reverse) and try again."
            + Environment.NewLine + Environment.NewLine
            + "To work through the application without QuickBooks, use the simulated company option "
            + "instead of connecting.",

        CouldNotStartQuickBooks =>
            "QuickBooks could not be started. Open QuickBooks and the company file you intend to work "
            + "on, then try again.",

        NoCompanyFileOpen =>
            "QuickBooks does not have a company file open. Open the company file, then try again.",

        NotAuthorized =>
            "This application has not been authorized to access the company file. In QuickBooks, go to "
            + "Edit > Preferences > Integrated Applications > Company Preferences, grant access to this "
            + "application, then try again. Authorization is granted by the QuickBooks administrator "
            + "while the file is open in single-user mode.",

        QuickBooksBusy =>
            "QuickBooks is busy, or a dialog is open and waiting for input. Bring QuickBooks to the "
            + "front, close whatever is open, then try again.",

        WrongFileMode =>
            "The company file is not open in a mode that permits this connection. Confirm whether "
            + "QuickBooks is in single-user or multi-user mode and what the integrated application is "
            + "permitted to do.",

        _ => null,
    };

    /// <summary>True when the failure means the SDK is missing rather than misbehaving.</summary>
    public static bool IsSdkMissing(Exception exception) =>
        Unwrap(exception ?? throw new ArgumentNullException(nameof(exception))).HResult == ClassNotRegistered;

    /// <summary>
    /// Peels off reflection and aggregate wrappers so the HRESULT that matters is the one inspected.
    /// Late-bound COM calls surface as <see cref="TargetInvocationException"/>.
    /// </summary>
    private static Exception Unwrap(Exception exception)
    {
        var current = exception;

        while (true)
        {
            switch (current)
            {
                case TargetInvocationException { InnerException: { } inner }:
                    current = inner;
                    continue;

                case AggregateException { InnerException: { } aggregated }:
                    current = aggregated;
                    continue;

                default:
                    return current;
            }
        }
    }

    /// <summary>Convenience for callers holding a raw <see cref="COMException"/>.</summary>
    public static string Describe(COMException exception, string progId = "QBXMLRP2.RequestProcessor") =>
        Describe((Exception)exception, progId);
}
