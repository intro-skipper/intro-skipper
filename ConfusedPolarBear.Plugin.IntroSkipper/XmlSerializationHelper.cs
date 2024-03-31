using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using System.Xml;

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

        public static List<Intro> DeserializeFromXml(string filePath)
        {
            var result = new List<Intro>();
            try
            {
                // Create a FileStream to read the XML file
                using FileStream fileStream = new FileStream(filePath, FileMode.Open);
                // Create an XmlDictionaryReader to read the XML
                XmlDictionaryReader reader = XmlDictionaryReader.CreateTextReader(fileStream, new XmlDictionaryReaderQuotas());

                // Create a DataContractSerializer for type T
                DataContractSerializer serializer = new DataContractSerializer(typeof(List<Intro>));

                // Deserialize the object from the XML
                result = serializer.ReadObject(reader) as List<Intro>;

                // Close the reader
                reader.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error deserializing XML: {ex.Message}");
            }

            // Return the deserialized object
            return result!;
        }

        public static void MigrateXML(string filePath)
        {
            if (File.Exists(filePath))
            {
                // Load the XML document
                XmlDocument xmlDoc = new XmlDocument();
                xmlDoc.Load(filePath);

                // Replace the namespace declaration
                XmlNamespaceManager nsManager = new XmlNamespaceManager(xmlDoc.NameTable);
                nsManager.AddNamespace("xsi", "http://www.w3.org/2001/XMLSchema-instance");
                nsManager.AddNamespace("xsd", "http://www.w3.org/2001/XMLSchema");
                xmlDoc.DocumentElement?.SetAttribute("xmlns", "http://schemas.datacontract.org/2004/07/ConfusedPolarBear.Plugin.IntroSkipper");
                xmlDoc.DocumentElement?.SetAttribute("xmlns:i", "http://www.w3.org/2001/XMLSchema-instance");

                // Save the modified XML document
                xmlDoc.Save(filePath);
            }
        }
    }
}
