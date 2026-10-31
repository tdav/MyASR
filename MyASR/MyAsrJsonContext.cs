using System.Text.Json.Serialization;

namespace MyASR;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(UserSettings))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, int>))]
public partial class MyAsrJsonContext : JsonSerializerContext;
