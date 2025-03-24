﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using UndertaleModLib.Models;

namespace UndertaleModLib.Project;

/// <summary>
/// Represents a context around a specific (mod) project, as exists on disk.
/// </summary>
public sealed class ProjectContext
{
    /// <summary>
    /// Current data context associated with this project.
    /// </summary>
    internal UndertaleData Data { get; }

    /// <summary>
    /// Generic options to use for writing JSON.
    /// </summary>
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // Relevant project paths
    private readonly string _mainFilePath;
    private readonly string _mainDirectory;

    // Main options of the project
    private readonly ProjectMainOptions _mainOptions;

    // Lookup of asset paths that existed when loading on disk, by data name and asset type
    private readonly Dictionary<(string DataName, SerializableAssetType AssetType), string> _assetDataNamesToPaths = new(128);

    // Set of all assets in the current data that are marked for export
    private readonly HashSet<IProjectAsset> _assetsMarkedForExport = new(64);

    /// <summary>
    /// Initializes a project context based on its existing main file path, and imports all of its data.
    /// </summary>
    /// <param name="currentData">Current data context to associate with the project.</param>
    /// <param name="mainFilePath">Main file path for the project.</param>
    public ProjectContext(UndertaleData currentData, string mainFilePath)
    {
        Data = currentData;
        _mainOptions = JsonSerializer.Deserialize<ProjectMainOptions>(mainFilePath);
        _mainDirectory = Path.GetDirectoryName(mainFilePath);

        // Recursively find and load in all assets in subdirectories
        List<ISerializableProjectAsset> loadedAssets = new(128);
        HashSet<string> excludeDirectorySet = new(_mainOptions.ExcludeDirectories);
        foreach (string directory in Directory.EnumerateDirectories(_mainDirectory))
        {
            // Skip directories that are irregular, start with ".", or are excluded based on main options
            DirectoryInfo info = new(directory);
            if (info.Attributes.HasFlag(FileAttributes.Hidden) || info.Attributes.HasFlag(FileAttributes.ReadOnly) || 
                info.Attributes.HasFlag(FileAttributes.System))
            {
                continue;
            }
            if (info.Name.StartsWith('.'))
            {
                continue;
            }
            if (excludeDirectorySet.Contains(info.Name))
            {
                continue;
            }

            // Iterate over all JSON files in this directory
            foreach (string assetPath in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
            {
                // Read in asset JSON
                ISerializableProjectAsset asset;
                try
                {
                    using FileStream fs = new(assetPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    asset = JsonSerializer.Deserialize<ISerializableProjectAsset>(fs, JsonOptions);
                }
                catch (Exception e)
                {
                    throw new ProjectLoadException($"Failed to load asset file \"{Path.GetFileName(assetPath)}\": {e.Message}", e);
                }

                // Add to list for later processing
                loadedAssets.Add(asset);

                // Associate the data name (and type) of this asset with its path
                if (!_assetDataNamesToPaths.TryAdd((asset.DataName, asset.AssetType), assetPath))
                {
                    throw new ProjectLoadException($"Found multiple {asset.AssetType} assets with name \"{asset.DataName}\"");
                }
            }
        }

        // Perform pre-import on all loaded assets
        foreach (ISerializableProjectAsset asset in loadedAssets)
        {
            asset.PreImport(this);
        }

        // Perform final import on all loaded assets
        foreach (ISerializableProjectAsset asset in loadedAssets)
        {
            asset.Import(this);
        }
    }

    /// <summary>
    /// Initializes a project context for a new project based on its main file path, and a name to give to it.
    /// </summary>
    /// <param name="currentData">Current data context to associate with the project.</param>
    /// <param name="mainFilePath">Main file path for the project.</param>
    /// <param name="newProjectName">Name of the new project being created.</param>
    public ProjectContext(UndertaleData currentData, string mainFilePath, string newProjectName)
    {
        Data = currentData;
        _mainFilePath = mainFilePath;
        _mainDirectory = Path.GetDirectoryName(mainFilePath);

        // If the file already exists, we cannot overwrite it (give a friendly message)
        if (File.Exists(mainFilePath))
        {
            throw new IOException($"Project file already exists at \"{mainFilePath}\"");
        }

        // If the directory isn't empty, we don't want to overwrite anything else accidentally
        Directory.CreateDirectory(_mainDirectory);
        if (Directory.EnumerateFileSystemEntries(_mainDirectory).Any())
        {
            throw new IOException("Project directory is not empty");
        }

        // Create new main options and save it
        _mainOptions = new()
        {
            Name = newProjectName
        };
        using FileStream fs = new(mainFilePath, FileMode.CreateNew);
        JsonSerializer.Serialize(fs, _mainOptions);
    }

    /// <summary>
    /// Marks an asset for export.
    /// </summary>
    /// <param name="asset">Asset to mark for export.</param>
    /// <returns>If the asset was not marked for export previously, returns <see langword="true"/>; <see langword="false"/> otherwise.</returns>
    public bool MarkAssetForExport(IProjectAsset asset)
    {
        return _assetsMarkedForExport.Add(asset);
    }

    /// <summary>
    /// Unmarks an asset for export.
    /// </summary>
    /// <param name="asset">Asset to unmark for export.</param>
    /// <returns>If the asset was marked for export previously, returns <see langword="true"/>; <see langword="false"/> otherwise.</returns>
    public bool UnmarkAssetForExport(IProjectAsset asset)
    {
        return _assetsMarkedForExport.Remove(asset);
    }

    /// <summary>
    /// Returns whether the given asset is marked for export currently.
    /// </summary>
    /// <param name="asset">Asset to check whether marked for export.</param>
    /// <returns>If the asset is marked for export, returns <see langword="true"/>; <see langword="false"/> otherwise.</returns>
    public bool IsAssetMarkedForExport(IProjectAsset asset)
    {
        return _assetsMarkedForExport.Contains(asset);
    }

    /// <summary>
    /// Exports all assets that are marked for export.
    /// </summary>
    public void Export(bool clearMarkedAssets)
    {
        // Export all assets that are marked as such
        foreach (IProjectAsset asset in _assetsMarkedForExport)
        {
            // Generate serializable version of the asset
            ISerializableProjectAsset serializableAsset = asset.GenerateSerializableProjectAsset(this);

            // Figure out a destination file path
            string destinationFile;
            if (_assetDataNamesToPaths.TryGetValue((serializableAsset.DataName, serializableAsset.AssetType), out string existingPath) &&
                File.Exists(existingPath))
            {
                // Existing file path existed from project load, and the file still exists; use that again
                destinationFile = existingPath;
            }
            else
            {
                // Generate new path
                string friendlyName = MakeValidFilenameIdentifier(serializableAsset.AssetType, serializableAsset.DataName);
                if (serializableAsset.IndividualDirectory)
                {
                    // Asset needs its own directory
                    destinationFile = Path.Combine(_mainDirectory, serializableAsset.AssetType.ToFilesystemName(), friendlyName, $"{friendlyName}.json");
                }
                else
                {
                    // Asset doesn't need its own directory
                    destinationFile = Path.Combine(_mainDirectory, serializableAsset.AssetType.ToFilesystemName(), $"{friendlyName}.json");
                }

                // If file already exists, add a suffix until there is no conflict
                int attempts = 0;
                while (File.Exists(destinationFile) && attempts < 10)
                {
                    string directory = Path.GetDirectoryName(destinationFile);
                    destinationFile = Path.Combine(directory, $"{friendlyName}_{attempts + 2}.json");
                    attempts++;
                }
                if (attempts > 0 && File.Exists(destinationFile))
                {
                    throw new IOException($"Too many naming conflicts for \"{friendlyName}\"");
                }
            }

            // Ensure directories are created for this asset
            Directory.CreateDirectory(destinationFile);

            // Write out asset to disk
            serializableAsset.Serialize(this, destinationFile);
        }

        // Clear out all assets marked for export, if desired
        if (clearMarkedAssets)
        {
            _assetsMarkedForExport.Clear();
        }
    }

    /// <summary>
    /// Returns a version of given string as a valid identifier to be used in a filename.
    /// </summary>
    private static string MakeValidFilenameIdentifier(SerializableAssetType assetType, string text)
    {
        // Ensure not empty/whitespace
        if (string.IsNullOrWhiteSpace(text))
        {
            return $"unknown_{assetType.ToFilesystemName()}_name";
        }

        // If length is way too long, it needs to be shortened
        const int lengthLimit = 100;
        if (text.Length >= lengthLimit)
        {
            text = text[..lengthLimit];
        }

        // Ensure first letter is an ASCII letter or an underscore
        char firstChar = text[0];
        if (!char.IsAsciiLetter(firstChar) && firstChar != '_')
        {
            // Replace first character with an underscore
            text = "_" + text[1..];
        }

        // Replace every other invalid character
        for (int i = 1; i < text.Length; i++)
        {
            char c = text[i];
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                text = text[1..i] + "_" + text[(i + 1)..];
            }
        }

        // Everything should now be valid
        return text;
    }

    /// <summary>
    /// Tries to find a sprite with the given name, if not null or whitespace, for the given serializable project asset.
    /// </summary>
    /// <returns>Sprite that was found, or null.</returns>
    internal UndertaleSprite FindSprite(string spriteNameOrNull, ISerializableProjectAsset forAsset)
    {
        if (string.IsNullOrWhiteSpace(spriteNameOrNull))
        {
            return null;
        }

        return Data.Sprites.ByName(spriteNameOrNull) ??
            throw new ProjectLoadException($"Failed to find sprite \"{spriteNameOrNull}\" for \"{forAsset.DataName}\"");
    }

    /// <summary>
    /// Tries to find a game object with the given name, if not null or whitespace, for the given serializable project asset.
    /// </summary>
    /// <returns>Game object that was found, or null.</returns>
    internal UndertaleGameObject FindGameObject(string gameObjectNameOrNull, ISerializableProjectAsset forAsset)
    {
        if (string.IsNullOrWhiteSpace(gameObjectNameOrNull))
        {
            return null;
        }

        return Data.GameObjects.ByName(gameObjectNameOrNull) ??
            throw new ProjectLoadException($"Failed to find object \"{gameObjectNameOrNull}\" for \"{forAsset.DataName}\"");
    }

    /// <summary>
    /// Tries to find a game object index with the given name, if not null or whitespace, for the given serializable project asset.
    /// </summary>
    /// <returns>Game object index that was found. If not found, an exception is thrown.</returns>
    internal int FindGameObjectIndex(string gameObjectNameOrNull, ISerializableProjectAsset forAsset)
    {
        if (string.IsNullOrWhiteSpace(gameObjectNameOrNull))
        {
            throw new ProjectLoadException($"No object name specified in property of \"{forAsset.DataName}\"");
        }

        int index = Data.GameObjects.IndexOfName(gameObjectNameOrNull);
        if (index < 0)
        {
            // Fallback option: parse integer and use that
            if (int.TryParse(gameObjectNameOrNull, out int fallbackIndex) && fallbackIndex >= 0 && fallbackIndex < Data.GameObjects.Count)
            {
                return fallbackIndex;
            }

            throw new ProjectLoadException($"Failed to find object \"{gameObjectNameOrNull}\" for \"{forAsset.DataName}\"");
        }
        return index;
    }

    /// <summary>
    /// Tries to find a code entry with the given name, if not null or whitespace, for the given serializable project asset.
    /// </summary>
    /// <returns>Code entry that was found, or null.</returns>
    internal UndertaleCode FindCode(string codeEntryNameOrNull, ISerializableProjectAsset forAsset)
    {
        if (string.IsNullOrWhiteSpace(codeEntryNameOrNull))
        {
            return null;
        }

        return Data.Code.ByName(codeEntryNameOrNull) ??
            throw new ProjectLoadException($"Failed to find code entry \"{codeEntryNameOrNull}\" for \"{forAsset.DataName}\"");
    }
}
