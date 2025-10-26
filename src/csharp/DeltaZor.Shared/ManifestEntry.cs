using System.Text.Json.Serialization;

namespace DZ.Shared;

public class ManifestEntry
{
    [JsonPropertyName("testId")]
    public int TestId { get; set; }
    
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
    
    [JsonPropertyName("baseFile")]
    public string BaseFile { get; set; } = string.Empty;
    
    [JsonPropertyName("nextFile")]
    public string NextFile { get; set; } = string.Empty;
    
    [JsonPropertyName("deltaFile")]
    public string DeltaFile { get; set; } = string.Empty;
    
    [JsonPropertyName("baseSize")]
    public long BaseSize { get; set; }
    
    [JsonPropertyName("nextSize")]
    public long NextSize { get; set; }
    
    [JsonPropertyName("deltaSize")]
    public long DeltaSize { get; set; }
    
    [JsonPropertyName("baseChecksum")]
    public string BaseChecksum { get; set; } = string.Empty;
    
    [JsonPropertyName("nextChecksum")]
    public string NextChecksum { get; set; } = string.Empty;
    
    [JsonPropertyName("deltaChecksum")]
    public string DeltaChecksum { get; set; } = string.Empty;
    
    [JsonPropertyName("tags")]
    public string[] Tags { get; set; } = Array.Empty<string>();
    
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;
    
    [JsonPropertyName("compressionRatio")]
    public double CompressionRatio { get; set; }
    
    [JsonPropertyName("generatedAt")]
    public DateTime GeneratedAt { get; set; }
    
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0";
    
    // Computed properties for convenience
    public bool IsValid => 
        TestId > 0 && 
        !string.IsNullOrEmpty(Name) &&
        BaseSize > 0 &&
        NextSize > 0 &&
        !string.IsNullOrEmpty(BaseChecksum) &&
        !string.IsNullOrEmpty(NextChecksum);
    
    public string FullBasePath => Path.Combine("testdata", BaseFile);
    public string FullNextPath => Path.Combine("testdata", NextFile);
    public string FullDeltaPath => Path.Combine("testdata", DeltaFile);
}