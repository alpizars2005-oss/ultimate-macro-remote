using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UltimateRemoteAgent.Protocol;

namespace UltimateRemoteAgent.Local;

public enum StrategyCatalogError
{
    InvalidMacroRoot,
    StrategyRootUnavailable,
    NetworkPathRejected,
    ReparsePointRejected,
    UnsafeStrategyEntry,
    TooManyStrategies,
    DuplicateStrategy,
}

public sealed class StrategyCatalogException : Exception
{
    internal StrategyCatalogException(StrategyCatalogError error)
        : base(GetSafeMessage(error))
    {
        Error = error;
    }

    public StrategyCatalogError Error { get; }

    private static string GetSafeMessage(StrategyCatalogError error) => error switch
    {
        StrategyCatalogError.InvalidMacroRoot => "The local macro root is invalid.",
        StrategyCatalogError.StrategyRootUnavailable => "The approved strategy root is unavailable.",
        StrategyCatalogError.NetworkPathRejected => "Network and device paths are not allowed.",
        StrategyCatalogError.ReparsePointRejected => "Reparse points are not allowed in the strategy catalog.",
        StrategyCatalogError.UnsafeStrategyEntry => "The approved strategy root contains an unsafe strategy entry.",
        StrategyCatalogError.TooManyStrategies => "The approved strategy root contains too many strategies.",
        StrategyCatalogError.DuplicateStrategy => "The approved strategy root contains an ambiguous strategy name.",
        _ => "The strategy catalog is unavailable.",
    };
}

public sealed class StrategyCatalog : IStrategyPathLookup
{
    private const string RootLabel = "builtin";

    public const int MaximumStrategies = 500;

    private readonly ApprovedStrategyRoot _approvedRoot;
    private readonly IReadOnlyDictionary<string, string> _canonicalPathsById;
    private readonly IReadOnlyDictionary<string, string> _idsByCanonicalPath;

    private StrategyCatalog(
        ApprovedStrategyRoot approvedRoot,
        IReadOnlyList<StrategySummary> strategies,
        IReadOnlyDictionary<string, string> canonicalPathsById,
        IReadOnlyDictionary<string, string> idsByCanonicalPath)
    {
        _approvedRoot = approvedRoot;
        Strategies = strategies;
        _canonicalPathsById = canonicalPathsById;
        _idsByCanonicalPath = idsByCanonicalPath;
    }

    public IReadOnlyList<StrategySummary> Strategies { get; }

    // This path remains local-only. It is deliberately absent from StrategySummary,
    // the type serialized for LIST_STRATEGIES.
    internal string ApprovedRootPath => _approvedRoot.CanonicalPath;

    public static StrategyCatalog Load(string macroRoot)
    {
        ApprovedStrategyRoot approvedRoot = WindowsPathSecurity.OpenApprovedStrategyRoot(macroRoot);
        var entries = new List<CatalogEntry>();

        try
        {
            foreach (string candidate in Directory.EnumerateFiles(
                         approvedRoot.CanonicalPath,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(candidate);
                if (!string.Equals(
                        Path.GetExtension(fileName),
                        ".strat",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (entries.Count == MaximumStrategies)
                {
                    throw new StrategyCatalogException(StrategyCatalogError.TooManyStrategies);
                }

                string canonicalPath = WindowsPathSecurity.ResolveApprovedStrategyFile(
                    approvedRoot,
                    candidate);
                string normalizedFileName = NormalizeFileName(fileName);
                string displayName = GetSafeDisplayName(fileName);
                string strategyId = ComputeStrategyIdFromNormalizedFileName(normalizedFileName);

                entries.Add(new CatalogEntry(
                    normalizedFileName,
                    new StrategySummary(strategyId, displayName),
                    canonicalPath));
            }

            WindowsPathSecurity.EnsureApprovedRootUnchanged(approvedRoot);
        }
        catch (StrategyCatalogException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFileSystemFailure(exception))
        {
            throw new StrategyCatalogException(StrategyCatalogError.StrategyRootUnavailable);
        }

        entries.Sort(static (left, right) =>
        {
            int byName = StringComparer.OrdinalIgnoreCase.Compare(
                left.Summary.Name,
                right.Summary.Name);
            return byName != 0
                ? byName
                : StringComparer.Ordinal.Compare(left.Summary.StrategyId, right.Summary.StrategyId);
        });

        var normalizedKeys = new HashSet<string>(StringComparer.Ordinal);
        var pathsById = new Dictionary<string, string>(StringComparer.Ordinal);
        var idsByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var summaries = new List<StrategySummary>(entries.Count);

        foreach (CatalogEntry entry in entries)
        {
            if (!normalizedKeys.Add(entry.NormalizedFileName)
                || !pathsById.TryAdd(entry.Summary.StrategyId, entry.CanonicalPath)
                || !idsByPath.TryAdd(entry.CanonicalPath, entry.Summary.StrategyId))
            {
                throw new StrategyCatalogException(StrategyCatalogError.DuplicateStrategy);
            }

            summaries.Add(entry.Summary);
        }

        return new StrategyCatalog(
            approvedRoot,
            summaries.AsReadOnly(),
            new ReadOnlyDictionary<string, string>(pathsById),
            new ReadOnlyDictionary<string, string>(idsByPath));
    }

    internal bool TryResolveCanonicalPath(
        string strategyId,
        [NotNullWhen(true)] out string? canonicalPath)
    {
        canonicalPath = null;
        if (string.IsNullOrEmpty(strategyId)
            || !_canonicalPathsById.TryGetValue(strategyId, out string? storedPath))
        {
            return false;
        }

        try
        {
            string validatedPath = WindowsPathSecurity.ResolveApprovedStrategyFile(
                _approvedRoot,
                storedPath);
            if (!string.Equals(validatedPath, storedPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            canonicalPath = validatedPath;
            return true;
        }
        catch (StrategyCatalogException)
        {
            return false;
        }
    }

    internal bool TryGetStrategyIdForCanonicalPath(
        string canonicalPath,
        [NotNullWhen(true)] out string? strategyId)
    {
        strategyId = null;
        if (string.IsNullOrWhiteSpace(canonicalPath))
        {
            return false;
        }

        try
        {
            string validatedPath = WindowsPathSecurity.ResolveApprovedStrategyFile(
                _approvedRoot,
                canonicalPath);
            return _idsByCanonicalPath.TryGetValue(validatedPath, out strategyId);
        }
        catch (StrategyCatalogException)
        {
            return false;
        }
    }

    bool IStrategyPathLookup.TryGetStrategyIdForCanonicalPath(
        string path,
        out string? strategyId) => TryGetStrategyIdForCanonicalPath(path, out strategyId);

    internal static string ComputeStrategyId(string fileName)
    {
        string normalizedFileName = NormalizeFileName(fileName);
        return ComputeStrategyIdFromNormalizedFileName(normalizedFileName);
    }

    private static string NormalizeFileName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)
            || !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal)
            || !string.Equals(
                Path.GetExtension(fileName),
                ".strat",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new StrategyCatalogException(StrategyCatalogError.UnsafeStrategyEntry);
        }

        try
        {
            return fileName.Normalize(NormalizationForm.FormC).ToUpperInvariant();
        }
        catch (ArgumentException)
        {
            throw new StrategyCatalogException(StrategyCatalogError.UnsafeStrategyEntry);
        }
    }

    private static string GetSafeDisplayName(string fileName)
    {
        string name = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrEmpty(name)
            || !string.Equals(name, name.Trim(), StringComparison.Ordinal)
            || name.IndexOfAny(['/', '\\', ':']) >= 0)
        {
            throw new StrategyCatalogException(StrategyCatalogError.UnsafeStrategyEntry);
        }

        int scalarCount = 0;
        foreach (Rune rune in name.EnumerateRunes())
        {
            scalarCount++;
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.Surrogate
                or UnicodeCategory.PrivateUse
                or UnicodeCategory.OtherNotAssigned)
            {
                throw new StrategyCatalogException(StrategyCatalogError.UnsafeStrategyEntry);
            }
        }

        if (scalarCount is < 1 or > 200)
        {
            throw new StrategyCatalogException(StrategyCatalogError.UnsafeStrategyEntry);
        }

        return name;
    }

    private static string ComputeStrategyIdFromNormalizedFileName(string normalizedFileName)
    {
        byte[] digest = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{RootLabel}\0{normalizedFileName}"));
        return $"s_{Convert.ToBase64String(digest).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
    }

    private static bool IsExpectedFileSystemFailure(Exception exception) => exception is
        IOException
        or UnauthorizedAccessException
        or System.Security.SecurityException
        or NotSupportedException
        or ArgumentException;

    private sealed record CatalogEntry(
        string NormalizedFileName,
        StrategySummary Summary,
        string CanonicalPath);
}
