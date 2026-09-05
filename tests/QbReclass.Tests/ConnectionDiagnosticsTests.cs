using System.Reflection;
using System.Runtime.InteropServices;
using QbReclass.Core.Session;
using Xunit;

namespace QbReclass.Tests;

/// <summary>
/// The messages shown when a connection to QuickBooks fails.
/// </summary>
/// <remarks>
/// These are the first thing a new installation puts in front of someone, usually a bookkeeper
/// rather than a developer, so each one has to name the fix rather than the fault.
/// </remarks>
public sealed class ConnectionDiagnosticsTests
{
    /// <summary>
    /// The most common first-run failure: QuickBooks is installed but the SDK is not. It must say
    /// so, and must mention the bitness trap, because that is the second cause of the same code.
    /// </summary>
    [Fact]
    public void ClassNotRegistered_ExplainsTheMissingSdkAndTheBitnessTrap()
    {
        var message = QbConnectionDiagnostics.Describe(
            new COMException("Class not registered", QbConnectionDiagnostics.ClassNotRegistered));

        Assert.Contains("SDK is not installed", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("separate download", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bitness", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("win-x86", message, StringComparison.Ordinal);
        Assert.Contains("simulated company", message, StringComparison.OrdinalIgnoreCase);

        // The raw COM text is kept, but after the explanation rather than instead of it.
        Assert.Contains("Class not registered", message, StringComparison.Ordinal);
        Assert.True(
            message.IndexOf("SDK is not installed", StringComparison.OrdinalIgnoreCase)
            < message.IndexOf("Technical detail", StringComparison.Ordinal),
            "The guidance must come before the technical detail.");
    }

    [Theory]
    [InlineData(QbConnectionDiagnostics.NoCompanyFileOpen, "company file")]
    [InlineData(QbConnectionDiagnostics.NotAuthorized, "Integrated Applications")]
    [InlineData(QbConnectionDiagnostics.QuickBooksBusy, "dialog")]
    [InlineData(QbConnectionDiagnostics.CouldNotStartQuickBooks, "Open QuickBooks")]
    [InlineData(QbConnectionDiagnostics.WrongFileMode, "no company file is open")]
    public void EachKnownFailureNamesItsRemedy(int hresult, string expected)
    {
        var message = QbConnectionDiagnostics.Describe(new COMException("com failure", hresult));

        Assert.Contains(expected, message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Late-bound COM calls arrive wrapped in a TargetInvocationException. Failing to unwrap would
    /// mean every real failure fell through to the generic message.
    /// </summary>
    [Fact]
    public void AWrappedComFailureIsStillRecognized()
    {
        var wrapped = new TargetInvocationException(
            new COMException("Class not registered", QbConnectionDiagnostics.ClassNotRegistered));

        Assert.Contains("SDK is not installed", QbConnectionDiagnostics.Describe(wrapped), StringComparison.OrdinalIgnoreCase);
        Assert.True(QbConnectionDiagnostics.IsSdkMissing(wrapped));
    }

    [Fact]
    public void DeeplyWrappedFailuresAreUnwrapped()
    {
        var wrapped = new TargetInvocationException(
            new AggregateException(
                new TargetInvocationException(
                    new COMException("nope", QbConnectionDiagnostics.NotAuthorized))));

        Assert.Contains("Integrated Applications", QbConnectionDiagnostics.Describe(wrapped), StringComparison.Ordinal);
    }

    /// <summary>An unrecognized failure still surfaces its text rather than being swallowed.</summary>
    [Fact]
    public void AnUnknownFailureFallsBackToTheUnderlyingMessage()
    {
        var message = QbConnectionDiagnostics.Describe(new COMException("something novel", unchecked((int)0x8004FFFF)));

        Assert.Contains("something novel", message, StringComparison.Ordinal);
        Assert.Null(QbConnectionDiagnostics.Guidance(unchecked((int)0x8004FFFF)));
    }

    [Fact]
    public void OnlyTheMissingSdkCountsAsAMissingSdk()
    {
        Assert.True(QbConnectionDiagnostics.IsSdkMissing(
            new COMException("x", QbConnectionDiagnostics.ClassNotRegistered)));

        Assert.False(QbConnectionDiagnostics.IsSdkMissing(
            new COMException("x", QbConnectionDiagnostics.NotAuthorized)));

        Assert.False(QbConnectionDiagnostics.IsSdkMissing(new InvalidOperationException("x")));
    }

    /// <summary>The ProgID is quoted back so the reader can search for it.</summary>
    [Fact]
    public void TheProgIdAppearsInTheNotRegisteredGuidance()
    {
        var message = QbConnectionDiagnostics.Guidance(QbConnectionDiagnostics.ClassNotRegistered)!;

        Assert.Contains("QBXMLRP2.RequestProcessor", message, StringComparison.Ordinal);
    }
}
