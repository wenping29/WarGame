using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HexFront.Core.Models;

namespace HexFront.Core.Services;

/// <summary>
/// Persistent save/load system for game states.
/// Handles serialization, metadata management, and save file organization.
/// </summary>
public static class StoreService
{
    private const string SaveExtension = ".json";
    private const string MetadataFile = "saves.json";

    /// <summary>Save metadata entry.</summary>
    public class SaveMetadata
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string? Description { get; init; }
        public DateTime SavedAt { get; init; }
        public int Turn { get; init; }
        public string Weather { get; init; } = "";
        public int UnitCount { get; init; }
        public long FileSize { get; init; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Save game state to file with metadata.</summary>
    public static SaveMetadata SaveGame(GameState g, string name, string? description, string saveDirectory)
    {
        Directory.CreateDirectory(saveDirectory);

        var id = Guid.NewGuid().ToString("N")[..12]; // Short GUID
        var filename = $"{id}{SaveExtension}";
        var filepath = Path.Combine(saveDirectory, filename);

        // Serialize game state
        var json = JsonSerializer.Serialize(g, JsonOptions);
        File.WriteAllText(filepath, json);

        // Create metadata
        var metadata = new SaveMetadata
        {
            Id = id,
            Name = name,
            Description = description,
            SavedAt = DateTime.UtcNow,
            Turn = g.Turn,
            Weather = g.Weather ?? "clear",
            UnitCount = g.Units.Count(u => u.Alive),
            FileSize = new FileInfo(filepath).Length,
        };

        // Update metadata index
        UpdateMetadataIndex(saveDirectory, metadata);

        return metadata;
    }

    /// <summary>Load game state from save ID.</summary>
    public static GameState LoadGame(string id, string saveDirectory)
    {
        var filepath = GetSavePath(id, saveDirectory);
        if (!File.Exists(filepath))
            throw new FileNotFoundException($"Save not found: {id}");

        var json = File.ReadAllText(filepath);
        var g = JsonSerializer.Deserialize<GameState>(json, JsonOptions);

        return g ?? throw new InvalidOperationException("Failed to deserialize save");
    }

    /// <summary>List all saved games with metadata.</summary>
    public static List<SaveMetadata> ListSaves(string saveDirectory)
    {
        var indexPath = Path.Combine(saveDirectory, MetadataFile);
        if (!File.Exists(indexPath))
            return new List<SaveMetadata>();

        var json = File.ReadAllText(indexPath);
        var saves = JsonSerializer.Deserialize<List<SaveMetadata>>(json, JsonOptions);

        return saves ?? new List<SaveMetadata>();
    }

    /// <summary>Delete a saved game.</summary>
    public static void DeleteSave(string id, string saveDirectory)
    {
        var filepath = GetSavePath(id, saveDirectory);
        if (File.Exists(filepath))
            File.Delete(filepath);

        // Remove from metadata index
        var indexPath = Path.Combine(saveDirectory, MetadataFile);
        if (File.Exists(indexPath))
        {
            var json = File.ReadAllText(indexPath);
            var saves = JsonSerializer.Deserialize<List<SaveMetadata>>(json, JsonOptions) ?? new List<SaveMetadata>();
            saves = saves.Where(s => s.Id != id).ToList();

            var newJson = JsonSerializer.Serialize(saves, JsonOptions);
            File.WriteAllText(indexPath, newJson);
        }
    }

    /// <summary>Get save file path by ID.</summary>
    private static string GetSavePath(string id, string saveDirectory)
    {
        return Path.Combine(saveDirectory, $"{id}{SaveExtension}");
    }

    /// <summary>Update or add metadata entry to index.</summary>
    private static void UpdateMetadataIndex(string saveDirectory, SaveMetadata newEntry)
    {
        var indexPath = Path.Combine(saveDirectory, MetadataFile);
        var saves = new List<SaveMetadata>();

        if (File.Exists(indexPath))
        {
            var json = File.ReadAllText(indexPath);
            saves = JsonSerializer.Deserialize<List<SaveMetadata>>(json, JsonOptions) ?? new List<SaveMetadata>();
        }

        // Add new entry (no updates, always append)
        saves.Add(newEntry);

        // Sort by date descending
        saves = saves.OrderByDescending(s => s.SavedAt).ToList();

        var newJson = JsonSerializer.Serialize(saves, JsonOptions);
        File.WriteAllText(indexPath, newJson);
    }

    /// <summary>Export save to JSON string (for sharing/backup).</summary>
    public static string ExportSave(GameState g)
    {
        return JsonSerializer.Serialize(g, JsonOptions);
    }

    /// <summary>Import save from JSON string.</summary>
    public static GameState ImportSave(string json)
    {
        var g = JsonSerializer.Deserialize<GameState>(json, JsonOptions);
        return g ?? throw new InvalidOperationException("Invalid save data");
    }
}
