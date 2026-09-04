using System;
using System.Linq;
using System.Xml.Linq;

namespace BedrockLauncher.UpdateProcessor.Extensions
{
    public static class NetworkExtensions
    {
        public static XElement CreateElement(XName name) => new XElement(name);
        public static XElement CreateElement(XName name, string value) => new XElement(name, value);

        public static XAttribute CreateAttribute(XName name, string value) => new XAttribute(name, value);

        public static XAttribute first_attribute(XNode element, string name) => (element as XElement).Attributes().FirstOrDefault(x => x.Name == name);

        public static XElement next_sibling(XElement element, XName name) => element.NextNode as XElement;

        public static XElement first_node(XElement element, XName name)
        {
            var nodes = element.DescendantsAndSelf();
            var result = nodes.FirstOrDefault(x => x.Name == name);
            return result;
        }

        public static XElement first_node_or_throw(XElement element, XName name)
        {
            try
            {
                var nodes = element.DescendantsAndSelf();
                var result = nodes.First(x => x.Name == name);
                return result;
            }
            catch (Exception ex)
            {
                throw new Exception("first_node_or_throw", ex);
            }
        }

        public static void Save(string fileName, string content)
        {
            XDocument xml = XDocument.Parse(content);
            xml.Save(fileName);
        }
    }
}
