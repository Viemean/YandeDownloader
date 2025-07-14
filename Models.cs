// Models.cs

using System.Text.Json.Serialization;

// API返回的信息
public class Post
{
    [JsonPropertyName("id")] public int Id { get; set; }

    [JsonPropertyName("tags")] public string? Tags { get; set; }
    [JsonPropertyName("file_size")] public long FileSize { get; set; }
    [JsonPropertyName("file_ext")] public string? FileExt { get; set; }
    [JsonPropertyName("file_url")] public string? FileUrl { get; set; }
}

// 记录在 manifest.json 中的条目
//TODO添加校验模式
public class ManifestEntry
{
    public string? Tags { get; set; }
    public string? SearchTags { get; set; }
    public long FileSize { get; set; }
    public string? FileName { get; set; }
}

// 记录在 session.json 中的会话状态
public class SessionState
{
    public string Tags { get; set; } = "";
    public string OutputDir { get; set; } = "";
}