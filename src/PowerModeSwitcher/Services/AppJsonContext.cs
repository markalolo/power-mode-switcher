using System.Text.Json.Serialization;

namespace PowerModeSwitcher.Services;

/// <summary>
/// System.Text.Json source generation for every type the app saves to disk. The serializer code
/// is generated at build time, so nothing is discovered through reflection at run time.
/// Add a [JsonSerializable] line here for any new type that is saved.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)] // keeps settings.json readable
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppJsonContext : JsonSerializerContext
{
}
