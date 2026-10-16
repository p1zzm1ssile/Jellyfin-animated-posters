using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.LibraryCoverMaker.Selection;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Xunit;

namespace LibraryCoverMaker.Tests;

// Two of the tests set BaseItem's static library manager for a moment, so the class is in the collection that keeps
// such classes from running at the same time.
[Collection(TestCollections.BaseItemStatics)]
public sealed class LibraryAudienceTests
{
    private static readonly Guid Movies = Guid.Parse("5b1d2e3f-0a11-4c22-8d33-000000000001");
    private static readonly Guid Shows = Guid.Parse("5b1d2e3f-0a11-4c22-8d33-000000000002");
    private static readonly Guid Music = Guid.Parse("5b1d2e3f-0a11-4c22-8d33-000000000003");

    // A user as Jellyfin stores one: permissions and preferences are rows, and a kind with no row reads as off/empty.
    private static User NewUser(
        string name,
        bool allFolders = false,
        bool disabled = false,
        Guid[]? enabledFolders = null,
        Guid[]? blockedFolders = null)
    {
        var user = new User(name, "Default", "Default") { Id = Guid.NewGuid() };
        user.SetPermission(PermissionKind.EnableAllFolders, allFolders);
        user.SetPermission(PermissionKind.IsDisabled, disabled);
        user.SetPreference(PreferenceKind.EnabledFolders, enabledFolders ?? Array.Empty<Guid>());
        user.SetPreference(PreferenceKind.BlockedMediaFolders, blockedFolders ?? Array.Empty<Guid>());
        return user;
    }

    private static LibraryAudience AudienceOf(params User[] users)
    {
        var manager = DispatchProxy.Create<IUserManager, UserManagerStub>();
        ((UserManagerStub)manager).Users = users;
        return new LibraryAudience(manager);
    }

    // The names make a failure readable; a User prints as a page of fields.
    private static IEnumerable<string> Names(IEnumerable<User> users) => users.Select(user => user.Username);

    [Fact]
    public void Users_with_all_folders_see_every_library()
    {
        var audience = AudienceOf(NewUser("everything", allFolders: true));

        Assert.Equal(new[] { "everything" }, Names(audience.UsersWhoSee(Movies)));
        Assert.Equal(new[] { "everything" }, Names(audience.UsersWhoSee(Shows)));

        // Also a library that was added a minute ago, which no list of folders can mention yet.
        Assert.Equal(new[] { "everything" }, Names(audience.UsersWhoSee(Guid.NewGuid())));
    }

    [Fact]
    public void All_folders_wins_over_a_list_of_enabled_folders_that_is_left_over()
    {
        // Jellyfin keeps the list when an admin turns on "all libraries"; it no longer limits anything.
        var audience = AudienceOf(NewUser("everything", allFolders: true, enabledFolders: new[] { Shows }));

        Assert.Equal(new[] { "everything" }, Names(audience.UsersWhoSee(Movies)));
    }

    [Fact]
    public void Users_with_enabled_folders_see_only_those()
    {
        var audience = AudienceOf(
            NewUser("movieAndMusicFan", enabledFolders: new[] { Movies, Music }),
            NewUser("showsFan", enabledFolders: new[] { Shows }),
            NewUser("nothing"),
            new User("untouched", "Default", "Default")); // no permission or preference rows at all

        Assert.Equal(new[] { "movieAndMusicFan" }, Names(audience.UsersWhoSee(Movies)));
        Assert.Equal(new[] { "showsFan" }, Names(audience.UsersWhoSee(Shows)));
        Assert.Equal(new[] { "movieAndMusicFan" }, Names(audience.UsersWhoSee(Music)));
        Assert.Empty(audience.UsersWhoSee(Guid.NewGuid()));
    }

    [Fact]
    public void Disabled_users_are_ignored()
    {
        var audience = AudienceOf(
            NewUser("active", allFolders: true),
            NewUser("disabledWithAllFolders", allFolders: true, disabled: true),
            NewUser("disabledWithTheLibrary", disabled: true, enabledFolders: new[] { Movies }),
            NewUser("disabledWithBlockedFolders", disabled: true, blockedFolders: new[] { Music }));

        Assert.Equal(new[] { "active" }, Names(audience.UsersWhoSee(Movies)));
        Assert.Equal(new[] { "active" }, Names(audience.UsersWhoSee(Shows)));
    }

    [Fact]
    public void Users_with_only_blocked_folders_see_every_library_except_those()
    {
        // The older way to limit a user: no list of enabled folders, just the libraries that are shut.
        var audience = AudienceOf(NewUser("blocksMusic", blockedFolders: new[] { Music }));

        Assert.Equal(new[] { "blocksMusic" }, Names(audience.UsersWhoSee(Movies)));
        Assert.Equal(new[] { "blocksMusic" }, Names(audience.UsersWhoSee(Shows)));
        Assert.Empty(audience.UsersWhoSee(Music));

        // Also a library that was added a minute ago: it is not on the list.
        Assert.Equal(new[] { "blocksMusic" }, Names(audience.UsersWhoSee(Guid.NewGuid())));
    }

    [Fact]
    public void Blocked_folders_keep_out_a_user_who_has_all_folders()
    {
        var audience = AudienceOf(NewUser("everythingButMusic", allFolders: true, blockedFolders: new[] { Music }));

        Assert.Equal(new[] { "everythingButMusic" }, Names(audience.UsersWhoSee(Movies)));
        Assert.Empty(audience.UsersWhoSee(Music));
    }

    [Fact]
    public void A_blocked_list_that_is_not_empty_wins_over_the_enabled_folders()
    {
        // Jellyfin reads the enabled folders only while the blocked list is empty, so a list that is left over is dead.
        var audience = AudienceOf(
            NewUser("both", enabledFolders: new[] { Movies, Shows }, blockedFolders: new[] { Shows }));

        Assert.Equal(new[] { "both" }, Names(audience.UsersWhoSee(Movies)));
        Assert.Empty(audience.UsersWhoSee(Shows)); // enabled, but blocked
        Assert.Equal(new[] { "both" }, Names(audience.UsersWhoSee(Music))); // not enabled, but not blocked
    }

    [Fact]
    public void The_audience_matches_what_Jellyfin_shows_each_user_of_a_library_folder()
    {
        // Every combination of the three settings that decide it: all libraries on or off, and the enabled and the
        // blocked lists each empty, with the library, with another one, or with both. Jellyfin's own answer for a
        // library folder is the reference.
        var lists = new Dictionary<string, Guid[]>
        {
            ["none"] = Array.Empty<Guid>(),
            ["movies"] = new[] { Movies },
            ["shows"] = new[] { Shows },
            ["movies+shows"] = new[] { Movies, Shows },
        };
        var users = new List<User>();
        foreach (var allFolders in new[] { false, true })
        {
            foreach (var (enabledName, enabled) in lists)
            {
                foreach (var (blockedName, blocked) in lists)
                {
                    users.Add(NewUser(
                        $"all={allFolders} enabled={enabledName} blocked={blockedName}",
                        allFolders: allFolders,
                        enabledFolders: enabled,
                        blockedFolders: blocked));
                }
            }
        }

        var audience = AudienceOf(users.ToArray());

        foreach (var library in new[] { Movies, Shows, Music })
        {
            var folder = new LibraryFolder { Id = library };
            var shownByJellyfin = users.Where(user => folder.IsVisible(user)).ToList();

            Assert.Equal(Names(shownByJellyfin), Names(audience.UsersWhoSee(library)));

            // Not two empty lists, nor two lists of everybody.
            Assert.NotEmpty(shownByJellyfin);
            Assert.True(shownByJellyfin.Count < users.Count);
        }
    }

    [Fact]
    public void The_audience_is_everyone_who_qualifies_in_the_order_the_server_lists_them()
    {
        // Not in alphabetical order, so that a sort by name would show.
        var audience = AudienceOf(
            NewUser("zoe", enabledFolders: new[] { Movies }),
            NewUser("skipped", enabledFolders: new[] { Shows }),
            NewUser("amy", allFolders: true),
            NewUser("disabled", allFolders: true, disabled: true),
            NewUser("max", enabledFolders: new[] { Shows, Movies }));

        Assert.Equal(new[] { "zoe", "amy", "max" }, Names(audience.UsersWhoSee(Movies)));
    }

    [Fact]
    public void The_audience_is_the_users_themselves_and_not_copies()
    {
        var user = NewUser("only", allFolders: true);

        Assert.Same(user, Assert.Single(AudienceOf(user).UsersWhoSee(Movies)));
    }

    [Fact]
    public void A_server_with_no_users_has_no_audience() =>
        Assert.Empty(AudienceOf().UsersWhoSee(Movies));

    [Fact]
    public void Everything_is_allowed_when_nobody_is_in_the_audience() =>
        Assert.True(AudienceOf().AllowedForAll(new Movie(), Array.Empty<User>()));

    [Fact]
    public void An_item_is_allowed_only_when_every_user_in_the_audience_may_see_it()
    {
        var open = NewUser("open");
        var blocksUnrated = NewUser("blocksUnrated");
        blocksUnrated.SetPreference(PreferenceKind.BlockUnratedItems, new[] { UnratedItem.Movie });
        var unrated = new Movie(); // Ratings come from the library; the end-to-end test covers them.
        var audience = AudienceOf();

        Assert.True(audience.AllowedForAll(unrated, new[] { open }));
        Assert.False(audience.AllowedForAll(unrated, new[] { blocksUnrated }));
        Assert.False(audience.AllowedForAll(unrated, new[] { open, blocksUnrated }));
        Assert.False(audience.AllowedForAll(unrated, new[] { blocksUnrated, open }));
    }

    [Fact]
    public void Blocked_tags_keep_an_item_out()
    {
        using var library = new LibraryManagerWithoutCollectionFolders();
        var open = NewUser("open");
        var careful = NewUser("careful");
        careful.SetPreference(PreferenceKind.BlockedTags, new[] { "gore" });
        var audience = AudienceOf();

        Assert.False(audience.AllowedForAll(new Movie { Tags = new[] { "gore" } }, new[] { open, careful }));
        Assert.True(audience.AllowedForAll(new Movie { Tags = new[] { "comedy" } }, new[] { open, careful }));
        Assert.True(audience.AllowedForAll(new Movie { Tags = new[] { "gore" } }, new[] { open }));
    }

    [Fact]
    public void Allowed_tags_are_enforced_too()
    {
        // A user who lists allowed tags may only see items that carry one: the check is on, not skipped.
        using var library = new LibraryManagerWithoutCollectionFolders();
        var picky = NewUser("picky");
        picky.SetPreference(PreferenceKind.AllowedTags, new[] { "family" });
        var audience = AudienceOf();

        Assert.False(audience.AllowedForAll(new Movie { Tags = new[] { "drama" } }, new[] { picky }));
        Assert.True(audience.AllowedForAll(new Movie { Tags = new[] { "family" } }, new[] { picky }));
    }

    [Fact]
    public void Missing_arguments_are_refused()
    {
        var audience = AudienceOf();

        // The refusal names the argument that is missing.
        var noItem = Assert.Throws<ArgumentNullException>(() => audience.AllowedForAll(null!, Array.Empty<User>()));
        var noAudience = Assert.Throws<ArgumentNullException>(() => audience.AllowedForAll(new Movie(), null!));

        Assert.Equal("item", noItem.ParamName);
        Assert.Equal("audience", noAudience.ParamName);
    }

    // A library as the server has it: an ICollectionFolder, which is what makes Folder.IsVisible check a user's access.
    private sealed class LibraryFolder : Folder, ICollectionFolder
    {
        public CollectionType? CollectionType => Jellyfin.Data.Enums.CollectionType.movies;
    }

    // The part of Jellyfin's user manager the audience uses; anything else being called is a mistake in the test.
    public class UserManagerStub : DispatchProxy
    {
        public IReadOnlyList<User> Users { get; set; } = Array.Empty<User>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(IUserManager.GetUsers)
                ? Users
                : throw new NotSupportedException($"{targetMethod?.Name} is not part of this test.");
    }

    // To collect an item's tags, BaseItem asks the static library manager which collection folders hold it. These items
    // stand alone, so there are none. The static property goes back to what it was when the test is done.
    private sealed class LibraryManagerWithoutCollectionFolders : IDisposable
    {
        private readonly ILibraryManager _previous = BaseItem.LibraryManager;

        public LibraryManagerWithoutCollectionFolders() =>
            BaseItem.LibraryManager = DispatchProxy.Create<ILibraryManager, LibraryManagerStub>();

        public void Dispose() => BaseItem.LibraryManager = _previous;
    }

    public class LibraryManagerStub : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(ILibraryManager.GetCollectionFolders)
                ? new List<Folder>()
                : throw new NotSupportedException($"{targetMethod?.Name} is not part of this test.");
    }
}
