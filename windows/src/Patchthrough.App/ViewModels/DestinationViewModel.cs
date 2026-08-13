using Patchthrough.Core;
using Patchthrough.Windows.Handoff;

namespace Patchthrough.App.ViewModels;

/// <summary>Which group of the picker a destination belongs to.</summary>
public enum DestinationKind
{
    /// <summary>A CLI agent, started in a terminal inside the repository.</summary>
    Terminal,

    /// <summary>A chat site, opened in the browser with the transcript attached.</summary>
    Web,

    /// <summary>A chat site the user added to the config.</summary>
    Custom,
}

/// <summary>
/// One door a transcript can go through.
///
/// The agents and the sites are different mechanisms with different requirements,
/// so this is the one shape a menu and a button can hold either in.
/// </summary>
public sealed record DestinationViewModel(
    string Id,
    string Label,
    DestinationKind Kind,
    InstalledAgent? Agent = null,
    ChatSite? Site = null)
{
    /// <summary>An agent needs a repository chosen before it can be used.</summary>
    public bool NeedsRepository => Kind == DestinationKind.Terminal;

    /// <summary>This site keeps a copy of the transcript off the machine.</summary>
    public bool UploadsToCloud => Site?.UploadsToCloud == true;

    /// <summary>
    /// The label on the primary button, which has room for a name and not much else.
    /// </summary>
    public string ShortLabel => Kind == DestinationKind.Terminal
        ? Label
        : Label.Replace(" (web)", "", StringComparison.Ordinal);

    public static DestinationViewModel ForAgent(InstalledAgent agent) =>
        new(agent.Agent.DestinationId, agent.Agent.Label, DestinationKind.Terminal, Agent: agent);

    public static DestinationViewModel ForSite(ChatSite site) =>
        new(site.DestinationId, site.Label, site.IsCustom ? DestinationKind.Custom : DestinationKind.Web, Site: site);
}

/// <summary>One section of the destination picker.</summary>
public sealed record DestinationGroupViewModel(string Title, IReadOnlyList<DestinationViewModel> Destinations);
