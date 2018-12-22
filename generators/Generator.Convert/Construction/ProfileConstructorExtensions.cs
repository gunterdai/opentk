using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Generator.Common;
using Generator.Common.Enums;
using Generator.Common.Functions;
using Generator.Convert.XML;
using JetBrains.Annotations;
using MoreLinq.Extensions;
using Enum = Generator.Common.Enums.Enum;

namespace Generator.Convert.Construction
{
    public static class ProfileConstructorExtensions
    {
        public static Enum ParseEnum(XElement element)
        {
            var result = new Enum
            {
                Name = NativeIdentifierTranslator.TranslateIdentifierName
                (
                    element.Attribute("name")?.Value
                    ?? throw new InvalidOperationException("No name attribute.")
                ),
                NativeName = element.Attribute("name")?.Value
            };
            foreach (var child in element.Elements("token"))
            {
                result.Tokens.Add
                (
                    new Token()
                    {
                        Name = NativeIdentifierTranslator.TranslateIdentifierName(child.Attribute("name")?.Value),
                        NativeName = child.Attribute("name")?.Value,
                        Value = child.Attribute("value")?.Value
                        // TODO: deprecation
                    }
                );
            }

            return result;
        }

        public static Function ParseFunction(XElement element)
        {
            var functionName = element.GetRequiredAttribute("name").Value;
            var functionCategories = element.GetRequiredAttribute("category")
                .Value
                .Split(new[] {'|'}, StringSplitOptions.RemoveEmptyEntries);
            var functionExtensions = element.GetRequiredAttribute("extension").Value;

            var functionVersion = ParsingHelpers.ParseVersion(element, defaultVersion: new Version(0, 0));
            var functionDeprecationVersion = ParsingHelpers.ParseVersion(element, "deprecated");

            var parameters = ParseParameters(element);

            var returnElement = element.GetRequiredElement("returns");
            var returnType = ParsingHelpers.ParseTypeSignature(returnElement);
            return new Function()
            {
                Name = NativeIdentifierTranslator.TranslateIdentifierName(functionName),
                NativeName = functionName,
                Parameters = parameters.ToList(),
                ReturnType = returnType,
                Categories = functionCategories,
                ExtensionName = functionExtensions
            };
        }

        [NotNull, ItemNotNull]
        private static IReadOnlyList<Parameter> ParseParameters([NotNull] XElement functionElement)
        {
            var parameterElements = functionElement.Elements().Where(e => e.Name == "param");
            var parametersWithComputedCounts =
                new List<(Parameter Parameter, IReadOnlyList<string> ComputedCountParameterNames)>();
            var parametersWithValueReferenceCounts =
                new List<(Parameter Parameter, string ParameterReferenceName)>();

            var resultParameters = new List<Parameter>();

            foreach (var parameterElement in parameterElements)
            {
                var parameter = ParseParameter
                (
                    parameterElement,
                    out var hasComputedCount,
                    out var computedCountParameterNames,
                    out var hasValueReference,
                    out var valueReferenceName,
                    out var valueReferenceExpression
                );

                if (hasComputedCount)
                {
                    parametersWithComputedCounts.Add((parameter, computedCountParameterNames));
                }

                if (hasValueReference)
                {
                    parametersWithValueReferenceCounts.Add((parameter, valueReferenceName));

                    // TODO: Pass on the mathematical expression
                }

                resultParameters.Add(parameter);
            }

            ParsingHelpers.ResolveComputedCountSignatures(resultParameters, parametersWithComputedCounts);

            ParsingHelpers.ResolveReferenceCountSignatures(resultParameters, parametersWithValueReferenceCounts);

            return resultParameters;
        }

        /// <summary>
        /// Parses a function parameter signature from the given <see cref="XElement"/>.
        /// </summary>
        /// <param name="paramElement">The parameter element.</param>
        /// <param name="hasComputedCount">Whether or not the parameter has a computed count.</param>
        /// <param name="computedCountParameterNames">
        /// The names of the parameters that the count is computed from, if any.
        /// </param>
        /// <param name="hasValueReference">Whether or not the parameter has a count value reference.</param>
        /// <param name="valueReferenceName">The name of the parameter that the count value references.</param>
        /// <param name="valueReferenceExpression">The expression that should be applied to the value reference.</param>
        /// <returns>A parsed parameter.</returns>
        [NotNull]
        [ContractAnnotation
            (
                "hasComputedCount : true => computedCountParameterNames : notnull;" +
                "hasValueReference : true => valueReferenceName : notnull"
            )
        ]
        private static Parameter ParseParameter
        (
            [NotNull] XElement paramElement,
            out bool hasComputedCount,
            [CanBeNull] out IReadOnlyList<string> computedCountParameterNames,
            out bool hasValueReference,
            [CanBeNull] out string valueReferenceName,
            [CanBeNull] out string valueReferenceExpression
        )
        {
            var paramName = paramElement.GetRequiredAttribute("name").Value;

            // A parameter is technically a type signature (think of it as Parameter : ITypeSignature)
            var paramType = ParsingHelpers.ParseTypeSignature(paramElement);

            var paramFlowStr = paramElement.GetRequiredAttribute("flow").Value;

            if (!System.Enum.TryParse<FlowDirection>(paramFlowStr, true, out var paramFlow))
            {
                throw new InvalidDataException("Could not parse the parameter flow.");
            }

            var paramCountStr = paramElement.Attribute("count")?.Value;
            var countSignature = ParsingHelpers.ParseCountSignature
            (
                paramCountStr,
                out hasComputedCount,
                out computedCountParameterNames,
                out hasValueReference,
                out valueReferenceName,
                out valueReferenceExpression
            );

            return new Parameter() {Name = paramName, Flow = paramFlow, Type = paramType, Count = countSignature};
        }

        public static void ParseXml(this Profile profile, IEnumerable<XElement> enums, IEnumerable<XElement> functions)
        {
            profile.Projects.Add
            (
                "Core",
                new Project() {CategoryName = "Core", ExtensionName = "Core", IsRoot = true, Namespace = string.Empty}
            );
            profile.Projects["Core"].Enums.AddRange(enums.Select(ParseEnum));
            var funs = functions.ToList();
            var parsed = funs.Select(ParseFunction).ToList();
            TypeMapper.Map
            (
                profile.Projects["Core"].Enums.ToDictionary(x => x.NativeName, x => x.Name),
                parsed
            );
            foreach (var typeMap in profile.TypeMaps)
            {
                TypeMapper.Map(typeMap, parsed);
            }
            profile.WriteFunctions(parsed);
        }

        public static void WriteFunctions(this Profile profile, IEnumerable<Function> functions)
        {
            foreach (var function in functions)
            {
                foreach (var category in function.Categories)
                {
                    // check that the root project exists
                    if (!profile.Projects.ContainsKey("Core"))
                    {
                        profile.Projects.Add
                        (
                            "Core",
                            new Project()
                            {
                                CategoryName = "Core", ExtensionName = "Core", IsRoot = true,
                                Namespace = string.Empty,
                            }
                        );
                    }

                    // check that the extension project exists, if applicable
                    if (function.ExtensionName != "Core" && !profile.Projects.ContainsKey(category))
                    {
                        profile.Projects.Add
                        (
                            category,
                            new Project()
                            {
                                CategoryName = category, ExtensionName = function.ExtensionName, IsRoot = false,
                                Namespace = "." + Utilities.ConvertExtensionNameToNamespace(category),
                            }
                        );
                    }

                    // check that the interface exists
                    if
                    (
                        !profile.Projects[function.ExtensionName == "Core" ? "Core" : category]
                            .Interfaces.ContainsKey(category)
                    )
                    {
                        profile.Projects[function.ExtensionName == "Core" ? "Core" : category]
                            .Interfaces.Add
                            (
                                category,
                                new Interface()
                                    {Name = "I" + NativeIdentifierTranslator.TranslateIdentifierName(category)}
                            );
                    }

                    // add the function to the interface
                    profile.Projects[function.ExtensionName == "Core" ? "Core" : category]
                        .Interfaces[category]
                        .Functions.Add(function);
                }
            }
        }
    }
}
