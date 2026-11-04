using System.Text.Json;
using System.Text.Json.Serialization;

namespace SkailSaga.Contracts;

public static class Json
{
    /// <summary>Web defaults (camelCase, case-insensitive) with string enums: the HTTP calls between web and worker.</summary>
    public static readonly JsonSerializerOptions Options = Create();

    /// <summary>
    /// For the arguments of the skail trigger only. The runtime tells upper from lower case apart, so the body has to
    /// be written the way System.Text.Json writes by default (PascalCase); the web defaults (camelCase) would arrive
    /// with the fields empty.
    /// </summary>
    public static readonly JsonSerializerOptions SkailArguments = JsonSerializerOptions.Default;

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
