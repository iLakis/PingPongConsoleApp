using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Xml.Schema;
using System.Xml.Serialization;
using System.Xml;

namespace Utils {
    public class XmlTools {

        public static string SerializeToXml<T>(T obj) {
            var serializer = new XmlSerializer(typeof(T));
            var settings = new XmlWriterSettings {
                Indent = true,
                Encoding = Encoding.UTF8,
                OmitXmlDeclaration = false
            };
            using (var stringWriter = new StringWriter())
            using (var xmlWriter = XmlWriter.Create(stringWriter, settings)) {
                serializer.Serialize(xmlWriter, obj);
                return stringWriter.ToString();
            }
        }
        public static bool ValidateXml(XDocument xmlDoc, XmlSchemaSet schemaSet) {
            try {
                xmlDoc.Validate(schemaSet, (o, e) => {
                    throw new XmlSchemaValidationException(e.Message);
                });
                return true;
            } catch (XmlSchemaValidationException) {
                return false;
            }
        }
        public static bool ValidateXml(string xmlMessage, XmlSchemaSet schemaSet) {
            try {
                var xmlDoc = XDocument.Parse(xmlMessage);
                xmlDoc.Validate(schemaSet, (o, e) => {
                    throw new XmlSchemaValidationException(e.Message);
                });
                return true;
            } catch (XmlSchemaValidationException) {
                return false;
            }
        }
    }
}
