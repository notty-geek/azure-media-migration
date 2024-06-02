using Azure.ResourceManager.Media;
using Azure.ResourceManager.Media.Models;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Linq;

namespace AMSMigrate.Ams
{
    enum TemplateType
    {
        Assets,
        Containers,
        Keys
    };

    internal class TemplateMapper
    {
        private readonly ILogger _logger;

        private static readonly IDictionary<TemplateType, string[]> Keys = new Dictionary<TemplateType, string[]>
        {
            {
                TemplateType.Containers, new [] {
                    "ContainerName"
                }
            },
            {
                TemplateType.Assets, new [] {
                    "AssetId",
                    "AssetName",
                    "AlternateId",
                    "ContainerName",
                    "StreamingUrl"
                    // "LocatorId",
                }
            },
            {
                TemplateType.Keys, new[]
                {
                    "KeyId",
                    "PolicyName"
                }
            }
        };

        const string TemplateRegularExpression = @"\${(?<key>\w+)}";

        static readonly Regex _regEx =
            new Regex(TemplateRegularExpression, RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public TemplateMapper(ILogger<TemplateMapper> logger)
        {
            _logger = logger;
        }

        public static (bool, string?) Validate(string template, TemplateType type = TemplateType.Assets)
        {
            var matches = _regEx.Matches(template);
            foreach (Match match in matches)
            {
                var group = match.Groups["key"];
                if (group == null)
                {
                    return (false, string.Empty);
                }
                var key = group.Value;
                if (!Keys[type].Contains(key))
                {
                    return (false, key);
                }
            }

            return (true, null);
        }

        public async Task<string> ExpandTemplate(string template, Func<string, Task<string?>> valueExtractor)
        {
            var expandedValue = template;
            var matches = _regEx.Matches(template);
            foreach (var match in matches.Reverse())
            {
                var key = match.Groups["key"].Value;
                var value = await valueExtractor(key);
                if (value != null)
                {
                    expandedValue = expandedValue.Replace(match.Value, value);
                }
            }
            _logger.LogTrace("Template {template} expanded to {value}", template, expandedValue);
            // Log streaming URL specifically if it's part of the expanded template
            if (template.Contains("${StreamingUrl}"))
            {
                var streamingUrl = await valueExtractor("StreamingUrl");
                _logger.LogTrace("Streaming URL: {streamingUrl}", streamingUrl);
            }

            return SanitizeResourceName(expandedValue);
        }

        private string SanitizeResourceName(string name)
        {
            // Replace underscores and periods with hyphens, convert to lowercase
            var sanitized = name.Replace("_", "-").ToLower();
            sanitized = sanitized.Replace(".", "-");

            // Remove invalid hyphen placements: leading, trailing, and multiple consecutive hyphens
            sanitized = Regex.Replace(sanitized, "^-+|-+$", ""); // Remove leading and trailing hyphens
            sanitized = Regex.Replace(sanitized, "--+", "-"); // Replace multiple consecutive hyphens with a single one

            // Ensure the name starts with a letter or number
            if (!char.IsLetterOrDigit(sanitized.FirstOrDefault()))
            {
                sanitized = "a" + sanitized; // Prefix with 'a' if not starting with letter or number
            }

            // Trim to maximum length first to handle cases close to length limits
            if (sanitized.Length > 63)
            {
                sanitized = sanitized.Substring(0, 63); // Trim if too long
            }

            // Ensure the name does not end with a hyphen
            // This step is placed after trimming to handle cases where trimming may cause a hyphen at the end
            if (sanitized.EndsWith("-"))
            {
                sanitized = sanitized.TrimEnd('-');
            }

            // Check if the length is still below the minimum required after all transformations
            if (sanitized.Length < 3)
            {
                sanitized = sanitized.PadRight(3, 'a'); // Pad with 'a' if too short
            }

            _logger.LogTrace("Template expanded to {value}", sanitized);
            return sanitized;
        }


        /// <summary>
        /// Expand the template to a container/bucket name and path.
        /// </summary>
        /// <returns>A tuple of container name and path prefix</returns>
        public (string Container, string Prefix) ExpandPathTemplate(string template, Func<string, Task<string?>> extractor)
        {
            string containerName;
            var path = ExpandTemplate(template, extractor).Result;
            var index = path.IndexOf('/');
            if (index == -1)
            {
                containerName = path.ToLowerInvariant();
                path = string.Empty;
            }
            else
            {
                containerName = path.Substring(0, index).ToLowerInvariant();
                path = path.Substring(index + 1);
                if (!path.EndsWith('/'))
                {
                    path += '/';
                }
            }

            containerName = containerName.Substring(0, Math.Min(containerName.Length, 63));
            return (containerName, path);
        }

        public (string Container, string Prefix) ExpandAssetTemplate(MediaAssetResource asset, string template)
        {
            return ExpandPathTemplate(template, async key =>
            {
                switch (key)
                {
                    case "AssetId":
                        return (asset.Data.AssetId ?? Guid.Empty).ToString();
                    case "AssetName":
                        return asset.Data.Name;
                    case "ContainerName":
                        return asset.Data.Container;
                    case "AlternateId":
                        return asset.Data.AlternateId ?? asset.Data.Name;
                    case "StreamingUrl":
                        return await GetStreamingUrlAsync(asset);
                }
                return null;
            });
        }

        public (string Container, string Prefix) ExpandPathTemplate(BlobContainerClient container, string template)
        {
            return ExpandPathTemplate(template, key =>
            {
                switch (key)
                {
                    case "ContainerName":
                        return Task.FromResult<string?>(container.Name);
                }
                return Task.FromResult<string?>(null);
            });
        }

        public async Task<string> ExpandKeyTemplate(StreamingLocatorContentKey contentKey, string? template)
        {
            if (template == null)
                return contentKey.Id.ToString();
            return await ExpandTemplate(template, key =>
            {
                if (key == "KeyId") return Task.FromResult<string?>(contentKey.Id.ToString());
                if (key == "PolicyName") return Task.FromResult<string?>(contentKey.PolicyName);
                return Task.FromResult<string?>(null);
            });
        }

        public async Task<string> ExpandKeyUriTemplate(string uriTemplate, string keyId)
        {
            return await ExpandTemplate(uriTemplate, key => key switch
            {
                "KeyId" => Task.FromResult<string?>(keyId),
                _ => Task.FromResult<string?>(null)
            });
        }

        private async Task<string> GetLocatorIdAsync(MediaAssetResource asset)
        {
            var locators = asset.GetStreamingLocatorsAsync();
            await foreach (var locator in locators)
            {
                return (locator.StreamingLocatorId ?? Guid.Empty).ToString();
            }
            _logger.LogError("No locator found for asset {name}. locator id was used in template", asset.Data.Name);
            throw new InvalidOperationException($"No locator found for asset {asset.Data.Name}");
        }

        private async Task<string> GetStreamingUrlAsync(MediaAssetResource asset)
        {
            var locators = asset.GetStreamingLocatorsAsync();
            await foreach (var locator in locators)
            {
                // Assuming a locator has a StreamingPath you can use to construct the URL
                if (locator.StreamingLocatorId != null)
                {
                    var paths = await locator.GetStreamingPathsAsync();
                    if (paths.Value.Any())
                    {
                        return paths.Value.First().Paths.First();
                    }
                }
            }
            _logger.LogError("No streaming URL found for asset {name}.", asset.Data.Name);
            throw new InvalidOperationException($"No streaming URL found for asset {asset.Data.Name}");
        }
    }
}
