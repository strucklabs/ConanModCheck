using System.Windows;

namespace ConanModCheck.Gui;

/// <summary>Anzeigeform eines Knotens aus dem Baum „Wer hat was“ für die TreeView.</summary>
public sealed class OwnTreeItem
{
    public string Text { get; }
    public string? Teleport { get; }
    public FontWeight Weight { get; }
    public bool IsExpanded { get; set; }
    public List<OwnTreeItem> Children { get; }

    internal OwnTreeItem(OwnNode node, bool expand)
    {
        Text = node.Text + (node.Location != null && node.Kind != OwnKind.Figure ? $"   ({SaveGame.Coords(node.Location)})" : "");
        Teleport = node.TeleportCommand;
        Weight = node.Kind is OwnKind.Clan or OwnKind.Player ? FontWeights.SemiBold : FontWeights.Normal;
        // Ohne Suche nur die oberen Ebenen aufklappen, bei einer Suche alles
        IsExpanded = expand || node.Kind is OwnKind.Clan or OwnKind.Group && node.Children.Count < 8;
        Children = node.Children.Select(c => new OwnTreeItem(c, expand)).ToList();
    }
}
