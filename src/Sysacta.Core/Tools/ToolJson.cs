using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Sysacta.Core.Tools;

/// <summary>
/// The single JSON configuration for tool arguments and results, and the schema generator.
/// Argument classes are the source of truth: the input schema and the validation are both derived from them,
/// so they cannot drift apart.
/// </summary>
public static class ToolJson
{
    /// <summary>
    /// Strict options: snake_case names, unknown properties rejected, no string-to-number coercion,
    /// no integer enum values, duplicate properties rejected, nullability and <c>required</c> enforced.
    /// </summary>
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.General)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            NumberHandling = JsonNumberHandling.Strict,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            AllowDuplicateProperties = false,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        options.MakeReadOnly();
        return options;
    }

    /// <summary>
    /// Builds a JSON Schema for <paramref name="type"/> including Description/Range/StringLength/DefaultValue.
    /// Nullable members never use an array <c>type</c> (e.g. <c>["string","null"]</c>), which some MCP clients reject:
    /// in arguments (<paramref name="forArguments"/>) null is dropped because optional arguments are simply omitted;
    /// in results it becomes <c>anyOf: [{type: X, ...}, {type: "null"}]</c>.
    /// </summary>
    public static JsonObject SchemaFor(Type type, bool forArguments)
    {
        var exporterOptions = new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = static (context, node) =>
            {
                if (node is not JsonObject schema)
                {
                    return node;
                }

                if (context.PropertyInfo?.AttributeProvider is { } provider)
                {
                    foreach (var attribute in provider.GetCustomAttributes(inherit: true))
                    {
                        switch (attribute)
                        {
                            case DescriptionAttribute d:
                                schema["description"] = d.Description;
                                break;
                            case RangeAttribute r:
                                schema["minimum"] = JsonValue.Create(Convert.ToInt64(r.Minimum, System.Globalization.CultureInfo.InvariantCulture));
                                schema["maximum"] = JsonValue.Create(Convert.ToInt64(r.Maximum, System.Globalization.CultureInfo.InvariantCulture));
                                break;
                            case StringLengthAttribute s:
                                schema["maxLength"] = s.MaximumLength;
                                if (s.MinimumLength > 0)
                                {
                                    schema["minLength"] = s.MinimumLength;
                                }
                                break;
                            case DefaultValueAttribute dv:
                                schema["default"] = JsonSerializer.SerializeToNode(dv.Value, Options);
                                break;
                        }
                    }
                }

                if (context.Path.IsEmpty && context.TypeInfo.Kind == JsonTypeInfoKind.Object)
                {
                    schema["type"] = "object";
                    schema["additionalProperties"] = false;
                }

                return schema;
            },
        };

        var root = (JsonObject)Options.GetJsonSchemaAsNode(type, exporterOptions);
        RemoveTypeArrays(root, forArguments);
        return root;
    }

    private static readonly HashSet<string> AnnotationKeywords = new(StringComparer.Ordinal) { "description", "default", "title" };

    private static void RemoveTypeArrays(JsonNode? node, bool forArguments)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array)
                {
                    RemoveTypeArrays(item, forArguments);
                }

                return;
            case not JsonObject:
                return;
        }

        var schema = (JsonObject)node;
        foreach (var (_, child) in schema.ToList())
        {
            RemoveTypeArrays(child, forArguments);
        }

        if (schema["type"] is not JsonArray types)
        {
            return;
        }

        var concrete = types.Select(t => (string?)t).Where(t => t != "null").ToList();
        var nullable = concrete.Count < types.Count;
        if (schema["enum"] is JsonArray values && (forArguments || concrete.Count != 1))
        {
            foreach (var n in values.Where(v => v is null).ToList())
            {
                values.Remove(n);
            }
        }

        if (concrete.Count == 1 && (forArguments || !nullable))
        {
            schema["type"] = concrete[0];
            return;
        }

        // Results: { anyOf: [ { type: X, <constraints> }, { type: "null" } ] }, annotations stay on the outer schema.
        var branch = new JsonObject();
        foreach (var (key, value) in schema.ToList())
        {
            if (!AnnotationKeywords.Contains(key))
            {
                schema.Remove(key);
                branch[key] = value;
            }
        }

        var anyOf = new JsonArray();
        foreach (var t in concrete)
        {
            var b = (JsonObject)branch.DeepClone();
            b["type"] = t;
            anyOf.Add(b);
        }

        if (nullable && !forArguments)
        {
            anyOf.Add(new JsonObject { ["type"] = "null" });
        }

        schema["anyOf"] = anyOf;
    }

    /// <summary>The output schema: the §9 envelope with <paramref name="dataType"/> as <c>data</c>.</summary>
    public static JsonObject EnvelopeSchemaFor(Type dataType) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["status"] = new JsonObject { ["enum"] = new JsonArray("success", "error", "partial", "unknown") },
            ["verification"] = new JsonObject { ["enum"] = new JsonArray("verified", "accepted", "not_applicable", "unknown") },
            ["data"] = SchemaFor(dataType, forArguments: false),
            ["error"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["code"] = new JsonObject { ["type"] = "string" },
                    ["message"] = new JsonObject { ["type"] = "string" },
                    ["retryable"] = new JsonObject { ["type"] = "boolean" },
                },
                ["required"] = new JsonArray("code", "message", "retryable"),
            },
            ["request_id"] = new JsonObject { ["type"] = "string" },
        },
        ["required"] = new JsonArray("status", "request_id"),
    };

    /// <summary>
    /// Deserializes and validates arguments. Throws <see cref="ToolFailureException"/> with
    /// <see cref="ErrorCodes.InvalidArgument"/>; the message never echoes argument values.
    /// </summary>
    public static TArgs ParseArguments<TArgs>(JsonElement? arguments)
        where TArgs : class
    {
        JsonElement element = arguments is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } a
            ? a
            : JsonSerializer.SerializeToElement(new JsonObject());

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ToolFailureException(ErrorCodes.InvalidArgument, "Arguments must be a JSON object.");
        }

        TArgs? value;
        try
        {
            value = element.Deserialize<TArgs>(Options);
        }
        catch (JsonException ex)
        {
            // JsonException.Path is the property path, safe to return; the message may contain values, so it is not used.
            var where = string.IsNullOrEmpty(ex.Path) || ex.Path == "$" ? "" : $" at '{ex.Path}'";
            throw new ToolFailureException(ErrorCodes.InvalidArgument, $"Invalid, unknown or missing argument{where}.");
        }

        if (value is null)
        {
            throw new ToolFailureException(ErrorCodes.InvalidArgument, "Arguments must be a JSON object.");
        }

        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(value, new ValidationContext(value), results, validateAllProperties: true))
        {
            var members = string.Join(", ", results.SelectMany(r => r.MemberNames).Select(JsonNamingPolicy.SnakeCaseLower.ConvertName).Distinct());
            throw new ToolFailureException(ErrorCodes.InvalidArgument, $"Argument out of range or invalid: {members}.");
        }

        return value;
    }
}
