using System.Text.Json.Serialization;

namespace DZ.Shared;

public class TestDataManifest
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0";
    
    [JsonPropertyName("generatedAt")]
    public DateTime GeneratedAt { get; set; }
    
    [JsonPropertyName("totalTests")]
    public int TotalTests { get; set; }
    
    [JsonPropertyName("totalSize")]
    public long TotalSize { get; set; }
    
    [JsonPropertyName("tests")]
    public List<ManifestEntry> Tests { get; set; } = new();
    
    [JsonPropertyName("checksum")]
    public string Checksum { get; set; } = string.Empty;
    
    // Computed properties
    public Dictionary<string, List<ManifestEntry>> TestsByCategory => 
        Tests.GroupBy(t => t.Category).ToDictionary(g => g.Key, g => g.ToList());
    
    public Dictionary<string, List<ManifestEntry>> TestsByTag => 
        Tests.SelectMany(t => t.Tags.Select(tag => new { Test = t, Tag = tag }))
             .GroupBy(x => x.Tag)
             .ToDictionary(g => g.Key, g => g.Select(x => x.Test).ToList());
    
    public bool IsValid => Tests.All(t => t.IsValid) && TotalTests == Tests.Count;
    
    public ManifestEntry? GetTestById(int testId) => 
        Tests.FirstOrDefault(t => t.TestId == testId);
    
    public List<ManifestEntry> GetTestsByCategory(string category) => 
        Tests.Where(t => t.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList();
    
    public List<ManifestEntry> GetTestsByTag(string tag) => 
        Tests.Where(t => t.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)).ToList();
}