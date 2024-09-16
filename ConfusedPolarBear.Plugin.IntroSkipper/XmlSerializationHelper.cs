using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Xml;
using ConfusedPolarBear.Plugin.IntroSkipper.Data;

namespace ConfusedPolarBear.Plugin.IntroSkipper
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
            var intros = new List<Intro>();
            var segments = new List<Segment>();
            try
            {
                // Create a FileStream to read the XML file
                using FileStream fileStream = new FileStream(filePath, FileMode.Open);
                // Create an XmlDictionaryReader to read the XML
                XmlDictionaryReader reader = XmlDictionaryReader.CreateTextReader(fileStream, new XmlDictionaryReaderQuotas());

                // Create a DataContractSerializer for type List<Intro>
                DataContractSerializer serializer = new DataContractSerializer(typeof(List<Intro>));

                // Deserialize the object from the XML
                intros = serializer.ReadObject(reader) as List<Intro>;

                // Close the reader
                reader.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error deserializing XML: {ex.Message}");
            }

            ArgumentNullException.ThrowIfNull(intros);
            intros.ForEach(delegate(Intro name)
            {
                segments.Add(new Segment(name));
            });
            SerializeToXml(segments, filePath);
        }

        public static List<Segment> DeserializeFromXml(string filePath)
        {
            var result = new List<Segment>();
            try
            {
                // Create a FileStream to read the XML file
                using FileStream fileStream = new FileStream(filePath, FileMode.Open);
                // Create an XmlDictionaryReader to read the XML
                XmlDictionaryReader reader = XmlDictionaryReader.CreateTextReader(fileStream, new XmlDictionaryReaderQuotas());

                // Create a DataContractSerializer for type T
                DataContractSerializer serializer = new DataContractSerializer(typeof(List<Segment>));

                // Deserialize the object from the XML
                result = serializer.ReadObject(reader) as List<Segment>;

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

        public static List<BlackListItem> DeserializeFromXmlBlacklist(string filePath)
        {
            var result = new List<BlackListItem>();
            try
            {
                // Create a FileStream to read the XML file
                using FileStream fileStream = new FileStream(filePath, FileMode.Open);
                // Create an XmlDictionaryReader to read the XML
                XmlDictionaryReader reader = XmlDictionaryReader.CreateTextReader(fileStream, new XmlDictionaryReaderQuotas());

                // Create a DataContractSerializer for type List<BlackListItem>
                DataContractSerializer serializer = new DataContractSerializer(typeof(List<BlackListItem>));

                // Deserialize the object from the XML
                result = serializer.ReadObject(reader) as List<BlackListItem>;

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
