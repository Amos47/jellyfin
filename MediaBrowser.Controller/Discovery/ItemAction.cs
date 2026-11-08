namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Fork: an action an app can offer on an item's page, such as Watch trailer.
/// </summary>
/// <param name="Id">The action id, unique among the action providers.</param>
/// <param name="Label">The button label.</param>
/// <param name="Icon">The icon name the apps map to a symbol.</param>
/// <param name="Kind">What the app does with it. Only <see cref="PlayQueueKind"/> for now: fetch the queue and play it.</param>
public record ItemAction(string Id, string Label, string Icon, string Kind)
{
    /// <summary>
    /// The kind of an action whose queue the app plays in order.
    /// </summary>
    public const string PlayQueueKind = "play-queue";
}
