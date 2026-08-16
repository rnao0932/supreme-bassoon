using System.Text;

namespace QbReclass.Core.QbXml;

/// <summary>
/// A <see cref="StringWriter"/> that reports UTF-8 as its encoding.
/// </summary>
/// <remarks>
/// <see cref="System.Xml.XmlWriter"/> derives the XML declaration's <c>encoding</c> attribute from
/// the writer it is given, ignoring <see cref="System.Xml.XmlWriterSettings.Encoding"/> when that
/// writer is a <see cref="TextWriter"/>. Without this, every qbXML request would be declared
/// <c>utf-16</c> - a declaration that does not describe what the QuickBooks request processor
/// actually receives, and a known cause of qbXML parse failures.
/// </remarks>
internal sealed class Utf8StringWriter : StringWriter
{
    public override Encoding Encoding => Encoding.UTF8;
}
