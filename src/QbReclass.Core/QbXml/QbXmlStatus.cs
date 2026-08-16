namespace QbReclass.Core.QbXml;

/// <summary>How the utility should react to a qbXML status code.</summary>
public enum QbStatusCategory
{
    Ok = 0,

    /// <summary>Request understood, nothing matched. Not an error.</summary>
    NoMatchingRecords,

    /// <summary>The record changed since it was read. Skip and return to review (spec FR-012).</summary>
    Conflict,

    /// <summary>The transaction or list object no longer exists.</summary>
    NotFound,

    /// <summary>A referenced list object is missing or inactive.</summary>
    InvalidReference,

    /// <summary>QuickBooks refused on permission or file-mode grounds.</summary>
    Permission,

    /// <summary>QuickBooks rejected the request content. Isolated to this record.</summary>
    Validation,

    /// <summary>The request or qbXML version is not supported by this QuickBooks.</summary>
    Unsupported,

    /// <summary>Anything else. Treated as a hard failure.</summary>
    Fatal,
}

/// <summary>
/// A qbXML response status: <c>statusCode</c>, <c>statusSeverity</c> and <c>statusMessage</c>
/// returned on every <c>*Rs</c> element. Logged verbatim (spec section 10 "Logging").
/// </summary>
public sealed record QbStatus(int Code, string Severity, string Message)
{
    public bool IsOk => Code == 0;

    public QbStatusCategory Category => Classify(Code);

    /// <summary>
    /// Maps well-known Desktop SDK status codes onto handling categories.
    /// </summary>
    /// <remarks>
    /// Codes are from Intuit's qbXML status-code list. Anything unmapped is treated as
    /// <see cref="QbStatusCategory.Fatal"/> rather than guessed at, so an unfamiliar rejection
    /// stops the batch instead of being quietly retried.
    /// </remarks>
    public static QbStatusCategory Classify(int code) => code switch
    {
        0 => QbStatusCategory.Ok,
        1 => QbStatusCategory.NoMatchingRecords,

        500 => QbStatusCategory.Unsupported,   // Unsupported qbXML version / request
        1000 => QbStatusCategory.Fatal,        // Internal QuickBooks error
        3000 => QbStatusCategory.InvalidReference,
        3100 => QbStatusCategory.Validation,   // Name already in use
        3120 => QbStatusCategory.NotFound,     // Object specified cannot be found
        3140 => QbStatusCategory.InvalidReference, // Referenced object not found
        3150 => QbStatusCategory.Validation,
        3170 => QbStatusCategory.Validation,   // Error modifying the object
        3175 => QbStatusCategory.Validation,
        3180 => QbStatusCategory.Validation,   // Error saving the transaction
        3200 => QbStatusCategory.Conflict,     // EditSequence is out of date
        3210 => QbStatusCategory.Validation,
        3220 => QbStatusCategory.Validation,
        3230 => QbStatusCategory.Validation,
        3231 => QbStatusCategory.Validation,
        3240 => QbStatusCategory.NotFound,
        3250 => QbStatusCategory.Unsupported,  // Feature not enabled / not supported
        3260 => QbStatusCategory.Permission,   // Insufficient permission
        3261 => QbStatusCategory.Permission,
        3262 => QbStatusCategory.Permission,
        3270 => QbStatusCategory.Permission,   // Wrong QuickBooks edition
        _ => QbStatusCategory.Fatal,
    };

    /// <summary>EditSequence conflict; the canonical stale-record signal.</summary>
    public const int EditSequenceOutOfDate = 3200;

    /// <summary>Object could not be found.</summary>
    public const int ObjectNotFound = 3120;

    /// <summary>A referenced list object (such as the destination account) is missing or inactive.</summary>
    public const int ReferenceNotFound = 3140;

    /// <summary>Query matched nothing.</summary>
    public const int NoMatch = 1;

    public override string ToString() => $"{Code} {Severity}: {Message}";
}

/// <summary>Raised when a qbXML response cannot be parsed at all, as distinct from a status error.</summary>
public sealed class QbXmlFormatException : Exception
{
    public QbXmlFormatException(string message) : base(message)
    {
    }

    public QbXmlFormatException(string message, Exception inner) : base(message, inner)
    {
    }
}
