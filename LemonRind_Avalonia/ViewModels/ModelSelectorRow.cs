namespace LemonRindAvalonia.ViewModels;

/// <summary>
/// One row in the grouped model dropdown - either a non-selectable category
/// header ("Chat models", "Image models", ...) or a real, selectable model
/// entry. Both share this one type (rather than two separate classes) so
/// the ComboBox's SelectedItem binding only ever deals with one type -
/// MainWindow.axaml renders the two differently (bold gray label vs. plain
/// selectable text) and disables the container for header rows via a Style
/// bound to IsHeader.
/// </summary>
public class ModelSelectorRow
{
    public required bool IsHeader { get; init; }
    public required string DisplayText { get; init; }
    public string? ModelId { get; init; }
}
