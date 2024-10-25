// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Xml;
using IntroSkipper.Data;

namespace IntroSkipper
{
    internal sealed class XmlSerializationHelper
    {
        public static void SerializeToXml<T>(T obj, string filePath)
        {
            // Create a FileStream to write the XML file
            using FileStream fileStream = new FileStream(filePath, FileMode.Create);
            // Create a DataContractSerializer for type T
            DataContractSerializer serializer = new DataContractSerializer(typeof(T));

            // Serialize the object to the FileStream
            serializer.WriteObject(fileStream, obj);
        }

        public static void MigrateFromIntro(string filePath)
        {
            List<Intro> intros = DeserializeFromXml<Intro>(filePath);
            ArgumentNullException.ThrowIfNull(intros);

            var segments = intros.Select(name => new Segment(name)).ToList();

            SerializeToXml(segments, filePath);
        }

        public static List<T> DeserializeFromXml<T>(string filePath)
        {
            var result = new List<T>();
            try
            {
                // Create a FileStream to read the XML file
                using FileStream fileStream = new FileStream(filePath, FileMode.Open);
                // Create an XmlDictionaryReader to read the XML
                XmlDictionaryReader reader = XmlDictionaryReader.CreateTextReader(fileStream, new XmlDictionaryReaderQuotas());

                // Create a DataContractSerializer for type List<T>
                DataContractSerializer serializer = new DataContractSerializer(typeof(List<T>));

                // Deserialize the object from the XML
                result = serializer.ReadObject(reader) as List<T>;

                // Close the reader
                reader.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error deserializing XML: {ex.Message}");
            }

            ArgumentNullException.ThrowIfNull(result);

            // Return the deserialized object
            return result;
        }

        public static void MigrateXML(string filePath)
        {
            if (File.Exists(filePath))
            {
                try
                {
                    // Load the XML document
                    XmlDocument xmlDoc = new XmlDocument();
                    xmlDoc.Load(filePath);

                    ArgumentNullException.ThrowIfNull(xmlDoc.DocumentElement);

                    // Check that the file has not already been migrated
                    if (xmlDoc.DocumentElement.HasAttribute("xmlns:xsi"))
                    {
                        xmlDoc.DocumentElement.RemoveAttribute("xmlns:xsi");
                        xmlDoc.DocumentElement.RemoveAttribute("xmlns:xsd");
                        xmlDoc.DocumentElement.SetAttribute("xmlns", "http://schemas.datacontract.org/2004/07/ConfusedPolarBear.Plugin.IntroSkipper");
                        xmlDoc.DocumentElement.SetAttribute("xmlns:i", "http://www.w3.org/2001/XMLSchema-instance");

                        // Save the modified XML document
                        xmlDoc.Save(filePath);
                    }

                    // undo namespace change
                    if (xmlDoc.DocumentElement.NamespaceURI == "http://schemas.datacontract.org/2004/07/ConfusedPolarBear.Plugin.IntroSkipper.Data")
                    {
                        xmlDoc.DocumentElement.SetAttribute("xmlns", "http://schemas.datacontract.org/2004/07/ConfusedPolarBear.Plugin.IntroSkipper");
                        // Save the modified XML document
                        xmlDoc.Save(filePath);
                    }

                    // intro -> segment migration
                    if (xmlDoc.DocumentElement.NamespaceURI == "http://schemas.datacontract.org/2004/07/ConfusedPolarBear.Plugin.IntroSkipper")
                    {
                        MigrateFromIntro(filePath);
                    }
                }
                catch (XmlException ex)
                {
                    Console.WriteLine($"Error deserializing XML: {ex.Message}");
                    File.Delete(filePath);
                    Console.WriteLine($"Deleting {filePath}");
                }
            }
        }
    }
}
