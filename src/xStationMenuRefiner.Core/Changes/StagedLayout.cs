using System.Collections.Generic;
using System.IO;
using System.Linq;
using xStationMenuRefiner.Core.Model;

// Written by Derek Pascarella (ateam)

namespace xStationMenuRefiner.Core.Changes;

// The browse tree drawn while structural edits are pending: every staged move, wrap,
// and unwrap in its applied shape, with the scan model and the disk untouched.
public sealed class StagedLayout
{
    private readonly Dictionary<CardNode, MenuFolderNode> _reparented = new();
    private readonly Dictionary<MenuFolderNode, GameNode> _wraps = new();
    private readonly HashSet<CardNode> _displaced = new();
    private readonly HashSet<MenuFolderNode> _removed = new();

    // Games whose staged move rides on their owning folder. They stay in place inside
    // that folder, so they are not displaced, but the flat list still marks them.
    private readonly HashSet<CardNode> _riders = new();

    public static StagedLayout Build(MenuFolderNode root, IEnumerable<PendingEdit> edits)
    {
        var layout = new StagedLayout();

        foreach (var edit in edits)
        {
            switch (edit.Kind)
            {
                case PendingEditKind.MoveEntry when edit is { Node: not null, Destination: not null }:
                    {
                        // A game that owns its folder moves as that folder.
                        CardNode moved = edit.Node;

                        if (edit.Node is GameNode { OwnsFolder: true } owner)
                        {
                            moved = owner.Parent!;
                            layout._riders.Add(owner);
                        }

                        layout._reparented[moved] = edit.Destination;
                        layout._displaced.Add(moved);
                        break;
                    }

                case PendingEditKind.WrapInFolder when edit.Node is GameNode loose:
                    {
                        var shell = new MenuFolderNode
                        {
                            Name = loose.Label,
                            FullPath = Path.Combine(loose.Parent!.FullPath, loose.Label),
                            Parent = loose.Parent,
                        };

                        layout._wraps[shell] = loose;
                        layout._displaced.Add(loose);
                        break;
                    }

                case PendingEditKind.UnwrapFolder when edit.Node is GameNode { Parent.Parent: not null } owned:
                    layout._reparented[owned] = owned.Parent!.Parent!;
                    layout._displaced.Add(owned);
                    layout._removed.Add(owned.Parent!);
                    break;
            }
        }

        return layout;
    }

    public IReadOnlyList<CardNode> ChildrenOf(MenuFolderNode folder)
    {
        if (_wraps.TryGetValue(folder, out var wrappedGame))
            return new CardNode[] { wrappedGame };

        var children = new List<CardNode>();

        foreach (var child in folder.Children)
        {
            if (_displaced.Contains(child) || (child is MenuFolderNode f && _removed.Contains(f)))
                continue;

            children.Add(child);
        }

        foreach (var (node, destination) in _reparented)
        {
            if (ReferenceEquals(destination, folder))
                children.Add(node);
        }

        foreach (var (shell, game) in _wraps)
        {
            if (ReferenceEquals(shell.Parent, folder))
                children.Add(shell);
        }

        return children;
    }

    public bool IsStaged(CardNode node) =>
        _reparented.ContainsKey(node) ||
        _displaced.Contains(node) ||
        _riders.Contains(node) ||
        (node is MenuFolderNode f && _wraps.ContainsKey(f));

    public bool IsSynthetic(MenuFolderNode folder) => _wraps.ContainsKey(folder);
}
