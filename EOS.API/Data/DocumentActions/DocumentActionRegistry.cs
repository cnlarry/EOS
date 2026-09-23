using System.Text.RegularExpressions;
using EOS.API.Models;

namespace EOS.API.Data.DocumentActions;

/// <summary>
/// Closed registry of document actions. The keys come from the handlers registered in the container
/// (one key per handler), so the set the publish gate validates against and the set the endpoint can
/// execute are the same set — a configured key can never be a key nobody implements.
/// </summary>
public sealed class DocumentActionRegistry
{
    private static readonly Regex KeyPattern = new("^[a-z][a-z0-9-]{2,49}$", RegexOptions.Compiled);

    private readonly Dictionary<string, IDocumentUserAction> _actions = new(StringComparer.OrdinalIgnoreCase);

    public DocumentActionRegistry(IEnumerable<IDocumentUserAction> actions, ILogger<DocumentActionRegistry> logger)
    {
        foreach (var action in actions)
        {
            var key = action.Key?.Trim() ?? string.Empty;
            if (!KeyPattern.IsMatch(key))
            {
                logger.LogError("单据操作键非法已忽略：'{Key}'（要求小写字母开头，仅含小写字母/数字/连字符，3~50 字符）。", action.Key);
                continue;
            }
            if (!_actions.TryAdd(key, action))
            {
                logger.LogError("单据操作键重复已忽略后者：{Key}", key);
            }
        }
    }

    /// <summary>Registered action keys (the closed set publish validation checks against).</summary>
    public IReadOnlyCollection<string> Keys => _actions.Keys;

    /// <summary>Same set as <see cref="Keys"/>, for callers that validate membership (case-insensitive).</summary>
    public IReadOnlySet<string> KeySet => _actions.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

    public bool IsRegistered(string? key) =>
        !string.IsNullOrWhiteSpace(key) && _actions.ContainsKey(key.Trim());

    public bool TryResolve(string? key, out IDocumentUserAction action)
    {
        if (!string.IsNullOrWhiteSpace(key) && _actions.TryGetValue(key.Trim(), out var resolved))
        {
            action = resolved;
            return true;
        }
        action = null!;
        return false;
    }

    /// <summary>Label used when the configuration row carries no label of its own.</summary>
    public string LabelOf(string key) =>
        _actions.TryGetValue(key.Trim(), out var action) ? action.Label : key;

    /// <summary>
    /// Render location declared by the handler (see <see cref="IDocumentActionPlacement"/>);
    /// handlers that act on the document as a whole keep the default.
    /// </summary>
    public string PlacementOf(string key) =>
        _actions.TryGetValue(key.Trim(), out var action) && action is IDocumentActionPlacement placement
            ? placement.Placement
            : DocumentActionPlacements.Master;
}

/// <summary>
/// Optional handler declaration of the render location. Kept apart from
/// <see cref="IDocumentUserAction"/> so that "where the button sits" stays a capability of the
/// action itself, not another configuration column to keep in sync with the handler.
/// </summary>
public interface IDocumentActionPlacement
{
    /// <summary>Either <see cref="DocumentActionPlacements.Master"/> or <see cref="DocumentActionPlacements.Detail"/>.</summary>
    string Placement { get; }
}
