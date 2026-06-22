// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Schema;
using System.Xml.Serialization;
using IntroSkipper.Helper;

namespace IntroSkipper.Data;

/// <summary>
/// Structured exclusion list that serializes entries without comma-separated value semantics.
/// </summary>
[JsonConverter(typeof(ExclusionListJsonConverter))]
public sealed class ExclusionList : Collection<string>, IXmlSerializable
{
    /// <summary>
    /// Gets the XML schema for this type.
    /// </summary>
    /// <returns>Always <see langword="null"/>; schema generation is not supported.</returns>
    public XmlSchema? GetSchema() => null;

    /// <summary>
    /// Reads nested string elements.
    /// </summary>
    /// <param name="reader">XML reader positioned on the exclusion list element.</param>
    public void ReadXml(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            reader.ReadStartElement();
            return;
        }

        reader.ReadStartElement();
        while (reader.NodeType != XmlNodeType.EndElement)
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                Add(reader.ReadElementContentAsString());
            }
            else
            {
                reader.Read();
            }
        }

        reader.ReadEndElement();
    }

    /// <summary>
    /// Writes list entries as nested string elements so values may contain commas.
    /// </summary>
    /// <param name="writer">XML writer positioned inside the exclusion list element.</param>
    public void WriteXml(XmlWriter writer)
    {
        foreach (var item in this)
        {
            writer.WriteElementString("string", item);
        }
    }
}
