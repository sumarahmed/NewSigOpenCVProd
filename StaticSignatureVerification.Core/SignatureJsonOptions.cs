using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace StaticSignatureVerification.Core;

public static class SignatureJsonOptions
{
    public static JsonSerializerOptions Compact { get; } = Create(writeIndented: false);
    public static JsonSerializerOptions Indented { get; } = Create(writeIndented: true);

    private static JsonSerializerOptions Create(bool writeIndented) => new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = writeIndented
    };
}

