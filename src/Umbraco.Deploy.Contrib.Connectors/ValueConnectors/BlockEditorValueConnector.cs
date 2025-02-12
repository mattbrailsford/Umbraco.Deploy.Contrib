﻿using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Umbraco.Core;
using Umbraco.Core.Deploy;
using Umbraco.Core.Logging;
using Umbraco.Core.Models;
using Umbraco.Core.Services;
using Umbraco.Deploy.Connectors.ValueConnectors.Services;

namespace Umbraco.Deploy.Contrib.Connectors.ValueConnectors
{
    /// <summary>
    /// A Deploy connector for BlockEditor based property editors (ie. BlockList)
    /// </summary>
    public abstract class BlockEditorValueConnector : IValueConnector
    {
        private readonly IContentTypeService _contentTypeService;
        private readonly Lazy<ValueConnectorCollection> _valueConnectorsLazy;
        private readonly ILogger _logger;

        public virtual IEnumerable<string> PropertyEditorAliases => new[] { "Umbraco.BlockEditor" };

        // cannot inject ValueConnectorCollection directly as it would cause a circular (recursive) dependency,
        // so we have to inject it lazily and use the lazy value when actually needing it
        private ValueConnectorCollection ValueConnectors => _valueConnectorsLazy.Value;

        public BlockEditorValueConnector(IContentTypeService contentTypeService, Lazy<ValueConnectorCollection> valueConnectors, ILogger logger)
        {
            if (contentTypeService == null) throw new ArgumentNullException(nameof(contentTypeService));
            if (valueConnectors == null) throw new ArgumentNullException(nameof(valueConnectors));
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            _contentTypeService = contentTypeService;
            _valueConnectorsLazy = valueConnectors;
            _logger = logger;
        }

        public string ToArtifact(object value, PropertyType propertyType, ICollection<ArtifactDependency> dependencies)
        {
            var svalue = value as string;

            // nested values will arrive here as JObject - convert to string to enable reuse of same code as when non-nested.
            if (value is JObject)
                svalue = value.ToString();

            if (string.IsNullOrWhiteSpace(svalue))
                return null;

            if (svalue.DetectIsJson() == false)
                return null;
            var blockEditorValue = JsonConvert.DeserializeObject<BlockEditorValue>(svalue);

            if (blockEditorValue == null)
                return null;

            var allBlocks = blockEditorValue.Content.Concat(blockEditorValue.Settings);

            // get all the content types used in block editor items
            var allContentTypes = allBlocks.Select(x => x.ContentTypeKey)
                .Distinct()
                .ToDictionary(a => a, a =>
                {
                    if (!Guid.TryParse(a, out var keyAsGuid))
                        throw new InvalidOperationException($"Could not parse ContentTypeKey as GUID {keyAsGuid}.");
                    return _contentTypeService.Get(keyAsGuid);
                });

            //Ensure all of these content types are found
            if (allContentTypes.Values.Any(contentType => contentType == null))
            {
                throw new InvalidOperationException($"Could not resolve these content types for the Block Editor property: {string.Join(",", allContentTypes.Where(x => x.Value == null).Select(x => x.Key))}");
            }

            //Ensure that these content types have dependencies added
            foreach (var contentType in allContentTypes.Values)
            {
                dependencies.Add(new ArtifactDependency(contentType.GetUdi(), false, ArtifactDependencyMode.Match));
            }

            foreach (var block in allBlocks)
            {
                var contentType = allContentTypes[block.ContentTypeKey];

                if (block.PropertyValues != null)
                {
                    foreach (var key in block.PropertyValues.Keys.ToArray())
                    {
                        var propType = contentType.CompositionPropertyTypes.FirstOrDefault(x => x.Alias == key);

                        if (propType == null)
                        {
                            _logger.Debug<BlockEditorValueConnector>("No property type found with alias {Key} on content type {ContentTypeAlias}.", key, contentType.Alias);
                            continue;
                        }

                        // fetch the right value connector from the collection of connectors, intended for use with this property type.
                        // throws if not found - no need for a null check
                        var propValueConnector = ValueConnectors.Get(propType);

                        // pass the value, property type and the dependencies collection to the connector to get a "artifact" value
                        var val = block.PropertyValues[key];
                        object parsedValue = propValueConnector.ToArtifact(val, propType, dependencies);

                        _logger.Debug<BlockEditorValueConnector>("Map {Key} value '{PropertyValue}' to '{ParsedValue}' using {PropValueConnectorType} for {PropTypeAlias}.", key, block.PropertyValues[key], parsedValue, propValueConnector.GetType(), propType.Alias);

                        parsedValue = parsedValue?.ToString();

                        block.PropertyValues[key] = parsedValue;
                    }
                }
                
            }

            value = JsonConvert.SerializeObject(blockEditorValue);
            return (string)value;
        }

        public object FromArtifact(string value, PropertyType propertyType, object currentValue)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            if (value.DetectIsJson() == false)
                return value;

            var blockEditorValue = JsonConvert.DeserializeObject<BlockEditorValue>(value);

            if (blockEditorValue == null)
                return value;

            var allBlocks = blockEditorValue.Content.Concat(blockEditorValue.Settings);

            var allContentTypes = allBlocks.Select(x => x.ContentTypeKey)
                .Distinct()
                .ToDictionary(a => a, a =>
                {
                    if (!Guid.TryParse(a, out var keyAsGuid))
                        throw new InvalidOperationException($"Could not parse ContentTypeKey as GUID {keyAsGuid}.");
                    return _contentTypeService.Get(keyAsGuid);
                });

            //Ensure all of these content types are found
            if (allContentTypes.Values.Any(contentType => contentType == null))
            {
                throw new InvalidOperationException($"Could not resolve these content types for the Block Editor property: {string.Join(",", allContentTypes.Where(x => x.Value == null).Select(x => x.Key))}");
            }

            foreach (var block in allBlocks)
            {
                var contentType = allContentTypes[block.ContentTypeKey];

                if (block.PropertyValues != null)
                {
                    foreach (var key in block.PropertyValues.Keys.ToArray())
                    {
                        var innerPropertyType = contentType.CompositionPropertyTypes.FirstOrDefault(x => x.Alias == key);

                        if (innerPropertyType == null)
                        {
                            _logger.Debug<BlockEditorValueConnector>("No property type found with alias {Key} on content type {ContentTypeAlias}.", key, contentType.Alias);
                            continue;
                        }

                        // fetch the right value connector from the collection of connectors, intended for use with this property type.
                        // throws if not found - no need for a null check
                        var propValueConnector = ValueConnectors.Get(innerPropertyType);

                        var propertyValue = block.PropertyValues[key];

                        if (propertyValue != null)
                        {
                            // pass the artifact value and property type to the connector to get a real value from the artifact
                            var convertedValue = propValueConnector.FromArtifact(propertyValue.ToString(), innerPropertyType, null);
                            if (convertedValue == null)
                            {
                                block.PropertyValues[key] = null;
                            }
                            else
                            {
                                block.PropertyValues[key] = convertedValue;
                            }
                        }
                        else
                        {
                            block.PropertyValues[key] = propertyValue;
                        }
                    }
                }

                   
            }

            return JObject.FromObject(blockEditorValue);
        }

        /// <summary>
        /// Strongly typed representation of the stored value for a block editor value
        /// </summary>
        /// <example>
        /// Example JSON:
        /// <![CDATA[
        ///     {
        ///        "layout": {
        ///            "Umbraco.BlockList": [
        ///            {
        ///                "contentUdi": "umb://element/b401bb800a4a48f79786d5079bc47718"
        ///            }
        ///            ]
        ///        },
        ///        "contentData": [
        ///        {
        ///            "contentTypeKey": "5fe26fff-7163-4805-9eca-960b1f106bb9",
        ///            "udi": "umb://element/b401bb800a4a48f79786d5079bc47718",
        ///            "image": "umb://media/e28a0070890848079d5781774c3c5ffb",
        ///            "text": "hero text",
        ///            "contentpicker": "umb://document/87478d1efa66413698063f8d00fda1d1"
        ///        }
        ///        ],
        ///        "settingsData": [
        ///        {
        ///            "contentTypeKey": "2e6094ea-7bca-4b7c-a223-375254a194f4",
        ///            "udi": "umb://element/499cf69f00c84227a59ca10fb4ae4c9a",
        ///            "textColor": "",
        ///            "containerWidth": "standard",
        ///            "textWidth": [],
        ///            "height": [],
        ///            "overlayStrength": [],
        ///            "textAlignment": "left",
        ///            "verticalTextAlignment": "top",
        ///            "animate": "0"
        ///        }
        ///        ]
        ///     }
        /// ]]>
        /// </example>
        public class BlockEditorValue
        {
            /// <summary>
            /// We do not have to actually handle anything in the layout since it should only contain references to items existing as data.
            /// JObject is fine for transferring this over.
            /// </summary>
            [JsonProperty("layout")]
            public JObject Layout { get; set; }

            /// <summary>
            /// This contains all the blocks created in the block editor.
            /// </summary>
            [JsonProperty("contentData")]
            public IEnumerable<Block> Content { get; set; }

            /// <summary>
            /// This contains the settings associated with the block editor.
            /// </summary>
            [JsonProperty("settingsData")]
            public IEnumerable<Block> Settings { get; set; }
        }

        public class Block
        {
            [JsonProperty("contentTypeKey")]
            public string ContentTypeKey { get; set; }

            [JsonProperty("udi")]
            public string Udi { get; set; }

            /// <summary>
            /// This is the property values defined on the block.
            /// These can be anything so we have to use a dictionary to represent them and JsonExtensionData attribute ensures all otherwise unmapped properties are stored here.
            /// </summary>
            [JsonExtensionData]
            public IDictionary<string, object> PropertyValues { get; set; }
        }
    }
}
