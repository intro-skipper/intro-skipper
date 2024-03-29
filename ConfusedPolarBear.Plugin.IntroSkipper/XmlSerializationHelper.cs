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
#pragma warning disable CS8603
            // Return the deserialized object
            return result;
#pragma warning restore CS8603
        }

        public static void MigrateXML(string filePath)
        {
            string searchString = "<ArrayOfIntro xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">";
            string replacementString = "<ArrayOfIntro xmlns=\"http://schemas.datacontract.org/2004/07/ConfusedPolarBear.Plugin.IntroSkipper\" xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\">";

            // Read the content of the file
            string fileContent = File.ReadAllText(filePath, Encoding.UTF8);

            // Check if the target string exists at the beginning
            if (fileContent.Contains(searchString, StringComparison.OrdinalIgnoreCase))
            {
                // Replace the target string
                fileContent = fileContent.Replace(searchString, replacementString, StringComparison.OrdinalIgnoreCase);

                // Write the modified content back to the file
                File.WriteAllText(filePath, fileContent, Encoding.UTF8);
            }
        }
    }
}
