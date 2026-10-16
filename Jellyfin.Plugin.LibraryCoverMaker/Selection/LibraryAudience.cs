using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.LibraryCoverMaker.Selection;

/// <summary>
/// The people a library's cover is shown to. A cover may only show titles that every one of them may see, so that it
/// never reveals a title to someone whose parental controls hide it.
/// </summary>
public sealed class LibraryAudience(IUserManager users)
{
    /// <summary>
    /// The users who can see a library, in the order the server lists them: those who are not disabled and whose
    /// folder access includes the library. The library's own on/off switch is not read: a switched-off library is
    /// hidden from everybody by Jellyfin, and listing users anyway only makes the parental filter stricter. A user
    /// with a list of blocked media folders (the older way to limit a user) sees every library that is not on it, and
    /// nothing else about their access is read. Any other user sees the library when they have access to all
    /// libraries or it is on their list of enabled folders.
    /// </summary>
    /// <param name="libraryId">The library's item id, as listed in a user's blocked or enabled folders.</param>
    /// <returns>The users themselves, so that a caller can ask Jellyfin about each of them.</returns>
    public IReadOnlyList<User> UsersWhoSee(Guid libraryId) =>
        users.GetUsers()
            .Where(user => !user.HasPermission(PermissionKind.IsDisabled) && CanSee(user, libraryId))
            .ToList();

    /// <summary>
    /// Whether every user of the audience may see an item under their parental controls: maximum rating, unrated
    /// items, blocked tags and allowed tags. True when the audience is empty, because nobody is restricted then.
    /// </summary>
    /// <param name="item">
    /// The item to check. For a series' cover, check the series and the chosen episode, each on its own.
    /// </param>
    /// <param name="audience">The users who can see the library, from <see cref="UsersWhoSee"/>.</param>
    public bool AllowedForAll(BaseItem item, IReadOnlyList<User> audience)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(audience);

        return audience.All(user => item.IsParentalAllowed(user, skipAllowedTagsCheck: false));
    }

    // The rule Jellyfin applies to a library folder (Folder.IsVisible). A list of blocked media folders that is not
    // empty decides on its own: the library is visible unless it is on the list, and the other two settings are not
    // read at all. Otherwise the user needs access to all libraries, or the library on their list of enabled folders.
    private static bool CanSee(User user, Guid libraryId)
    {
        var blocked = user.GetPreferenceValues<Guid>(PreferenceKind.BlockedMediaFolders);
        if (blocked.Length > 0)
        {
            return !blocked.Contains(libraryId);
        }

        return user.HasPermission(PermissionKind.EnableAllFolders)
            || user.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders).Contains(libraryId);
    }
}
