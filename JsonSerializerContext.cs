using System.Collections.Concurrent;
using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<Post>))]
[JsonSerializable(typeof(ConcurrentDictionary<int, ManifestEntry>))]
[JsonSerializable(typeof(SessionState))]
[JsonSerializable(typeof(Post))]
[JsonSerializable(typeof(ManifestEntry))]
[JsonSerializable(typeof(Dictionary<int, ManifestEntry>))]
public partial class SourceGenerationContext : JsonSerializerContext
{
}