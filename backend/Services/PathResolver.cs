using WebMusic.Backend.Models;

namespace WebMusic.Backend.Services;

/// <summary>
/// Centralized path resolution service.
/// Handles the mapping between frontend paths (which include Share Names) 
/// and database paths (which are relative to the Share root).
/// 
/// Example:
/// - ScanSource Path: "DataSync/Music" (SMB Share/Path)
/// - Frontend Path: "DataSync/Music/Rock/Album1"
/// - Database Path (ParentPath): "Music/Rock/Album1"
/// 
/// This service consolidates this mapping logic that was previously scattered across:
/// - MediaController.GetFiles()
/// - MediaController.GetDirectory()
/// - MediaController.GetDirectoryIds()
/// </summary>
public class PathResolver
{
    /// <summary>
    /// Result of path resolution containing both the resolved DB path and matched source.
    /// </summary>
    public class ResolveResult
    {
        /// <summary>
        /// The path as stored in the database (relative to share root, excludes share name).
        /// </summary>
        public string DbPath { get; set; } = string.Empty;
        
        /// <summary>
        /// The matched ScanSource, if any. Null if no source matches.
        /// </summary>
        public ScanSource? MatchedSource { get; set; }
        
        /// <summary>
        /// Whether a valid source was matched.
        /// </summary>
        public bool HasMatch => MatchedSource != null;
    }

    /// <summary>
    /// Normalizes a path by replacing backslashes with forward slashes
    /// and trimming leading/trailing slashes.
    /// </summary>
    public string NormalizePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        return path.Replace('\\', '/').Trim('/');
    }

    /// <summary>
    /// Resolves a frontend path to a database path.
    /// 
    /// Frontend paths include the ScanSource share name prefix.
    /// Database paths are relative to the share root.
    /// 
    /// Example:
    /// - frontendPath = "DataSync/Music/Rock/Album1"
    /// - ScanSource.Path = "DataSync/Music"
    /// - Result.DbPath = "Rock/Album1"
    /// </summary>
    /// <param name="frontendPath">The path from the frontend request</param>
    /// <param name="sources">List of configured ScanSources</param>
    /// <returns>ResolveResult containing the database path and matched source</returns>
    public ResolveResult ResolveFrontendToDbPath(string? frontendPath, IEnumerable<ScanSource> sources)
    {
        var result = new ResolveResult();
        
        var cleanPath = NormalizePath(frontendPath);
        if (string.IsNullOrEmpty(cleanPath))
        {
            result.DbPath = string.Empty;
            return result;
        }

        // Find the best matching source (longest path match wins)
        var matchedSource = sources
            .Select(s => new { Source = s, NormPath = NormalizePath(s.Path) })
            .Where(x => cleanPath.StartsWith(x.NormPath, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(cleanPath, x.NormPath, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.NormPath.Length)
            .FirstOrDefault();

        if (matchedSource != null)
        {
            result.MatchedSource = matchedSource.Source;
            
            // Extract the share name (first segment of source path)
            var shareName = matchedSource.NormPath.Split('/')[0];
            
            // Remove the share name prefix from the frontend path to get the DB path
            if (cleanPath.StartsWith(shareName + "/", StringComparison.OrdinalIgnoreCase))
            {
                result.DbPath = cleanPath.Substring(shareName.Length + 1);
            }
            else if (string.Equals(cleanPath, shareName, StringComparison.OrdinalIgnoreCase))
            {
                // Path is exactly the share name - root level
                result.DbPath = string.Empty;
            }
            else
            {
                result.DbPath = cleanPath;
            }
        }
        else
        {
            result.DbPath = cleanPath;
        }

        return result;
    }

    /// <summary>
    /// Converts a database path back to a frontend path by prepending the share name.
    /// Used when returning paths to the frontend.
    /// </summary>
    /// <param name="dbPath">The path as stored in the database</param>
    /// <param name="source">The ScanSource the file belongs to</param>
    /// <returns>The full frontend path including share name</returns>
    public string ResolveDbToFrontendPath(string? dbPath, ScanSource? source)
    {
        var cleanDbPath = NormalizePath(dbPath);
        
        if (source == null)
            return cleanDbPath;
            
        var sourcePath = NormalizePath(source.Path);
        var shareName = sourcePath.Split('/')[0];
        
        if (string.IsNullOrEmpty(cleanDbPath))
            return shareName;
            
        return $"{shareName}/{cleanDbPath}";
    }

    /// <summary>
    /// Checks if a database path matches the resolved target path.
    /// Handles the common case of comparing ParentPath to a target directory.
    /// </summary>
    /// <param name="dbParentPath">The ParentPath from the database record</param>
    /// <param name="targetDbPath">The target path to compare against</param>
    /// <param name="recursive">If true, also matches child paths</param>
    /// <returns>True if the paths match according to the criteria</returns>
    public bool IsPathMatch(string? dbParentPath, string targetDbPath, bool recursive)
    {
        var normalizedDbPath = NormalizePath(dbParentPath);
        var normalizedTarget = NormalizePath(targetDbPath);
        
        if (recursive)
        {
            // Match exact path OR any child path
            if (string.IsNullOrEmpty(normalizedTarget))
            {
                // Target is root - everything matches
                return true;
            }
            return string.Equals(normalizedDbPath, normalizedTarget, StringComparison.OrdinalIgnoreCase) ||
                   normalizedDbPath.StartsWith(normalizedTarget + "/", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // Exact match only
            return string.Equals(normalizedDbPath, normalizedTarget, StringComparison.OrdinalIgnoreCase);
        }
    }
}
