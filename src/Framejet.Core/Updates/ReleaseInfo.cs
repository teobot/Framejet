using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;

namespace Framejet.Core.Updates
{
    /// <summary>The parts of a GitHub "latest release" response that the update check reads.</summary>
    public sealed class ReleaseInfo
    {
        ReleaseInfo(string tag, string pageUrl, IReadOnlyList<string> assetNames)
        { Tag = tag; PageUrl = pageUrl; AssetNames = assetNames; }

        public string Tag { get; }
        public string PageUrl { get; }
        public IReadOnlyList<string> AssetNames { get; }

        /// <summary>Throws <see cref="FormatException"/> for anything that isn't a release with a tag.</summary>
        public static ReleaseInfo Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new FormatException("Empty release response.");
            Dto dto;
            try
            {
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                    dto = new DataContractJsonSerializer(typeof(Dto)).ReadObject(ms) as Dto;
            }
            catch (SerializationException e) { throw new FormatException("Not a release response.", e); }
            catch (ArgumentException e) { throw new FormatException("Not a release response.", e); }
            catch (XmlException e) { throw new FormatException("Not a release response.", e); }

            if (dto == null || string.IsNullOrWhiteSpace(dto.Tag)) throw new FormatException("Release has no tag.");
            var names = new List<string>();
            if (dto.Assets != null)
                foreach (var a in dto.Assets)
                    if (a != null && a.Name != null) names.Add(a.Name);
            return new ReleaseInfo(dto.Tag, dto.Url ?? "", names);
        }

        [DataContract] sealed class Dto
        {
            [DataMember(Name = "tag_name")] public string Tag { get; set; }
            [DataMember(Name = "html_url")] public string Url { get; set; }
            [DataMember(Name = "assets")] public List<AssetDto> Assets { get; set; }
        }

        [DataContract] sealed class AssetDto
        {
            [DataMember(Name = "name")] public string Name { get; set; }
        }
    }
}
