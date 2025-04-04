using Google.OrTools.Sat;

namespace FplTeamPicker.Services.Optimisation.Models;

public class PickFplTeamCpModel : CpModel
{
    public List<FplPlayerSelectionVar> SelectedGks { get; set; } = new();
    public List<FplPlayerSelectionVar> SelectedDefs { get; set; } = new();
    public List<FplPlayerSelectionVar> SelectedMids { get; set; } = new();
    public List<FplPlayerSelectionVar> SelectedFwds { get; set; } = new();

    public List<FplPlayerSelectionVar> Selections => SelectedGks
        .Union(SelectedDefs)
        .Union(SelectedMids)
        .Union(SelectedFwds)
        .OrderBy(s => s.Id)
        .ToList();

    /// <summary>
    /// A dictionary of TeamId to a list of variables representing player selections.
    /// This is used to ensure we don't pick too many players from a single team.
    /// </summary>
    public Dictionary<int, List<IntVar>> TeamSelectionCounts { get; set; } = new();
}