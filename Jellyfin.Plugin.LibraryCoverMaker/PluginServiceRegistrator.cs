using System;
using System.Linq;
using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.LibraryCoverMaker.Covers;
using Jellyfin.Plugin.LibraryCoverMaker.Rendering;
using Jellyfin.Plugin.LibraryCoverMaker.Scheduling;
using Jellyfin.Plugin.LibraryCoverMaker.Selection;
using Jellyfin.Plugin.LibraryCoverMaker.Sources;
using Jellyfin.Plugin.LibraryCoverMaker.Storage;
using Jellyfin.Plugin.LibraryCoverMaker.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.Trickplay;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jellyfin.Plugin.LibraryCoverMaker;

/// <summary>
/// Registers the plugin's services with Jellyfin. Every service is a singleton: the store, the applier (its set of
/// libraries being applied lives in memory) and the render queue hold state that all callers must share. The one
/// exception are the event consumers, which are scoped like Jellyfin's own: Jellyfin resolves them in a new scope for
/// every event it publishes, and they hold no state.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost) =>
        AddServices(serviceCollection);

    /// <summary>Adds the plugin's own services to <paramref name="services"/>, not Jellyfin's.</summary>
    internal static void AddServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<CoverStore>();
        services.AddSingleton<FfmpegRunner>();
        services.AddSingleton<OverlayRenderer>();
        services.AddSingleton<LibraryAudience>();
        services.AddSingleton<ArtworkSource>();
        services.AddSingleton(provider => new SceneFinder(
            provider.GetRequiredService<ITrickplayManager>(),
            provider.GetRequiredService<IMediaSegmentManager>(),
            provider.GetRequiredService<IChapterManager>(),
            provider.GetRequiredService<ILibraryManager>(),
            provider.GetRequiredService<FfmpegRunner>(),
            provider.GetRequiredService<ILogger<SceneFinder>>(),
            provider.GetRequiredService<CoverStore>().TempFolder));
        services.AddSingleton<CoverApplier>();
        services.AddSingleton<CoverBuilder>();
        services.AddSingleton(CreateQueue);

        // The queue and the scheduler are hosted services, and the queue is also what the scheduled task and the
        // scheduler hand libraries to: the hosted service resolves to the same instance.
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<RenderQueue>());
        services.AddSingleton<IHostedService, RenewalScheduler>();
        services.AddSingleton<IHostedService, PrimaryImageWatcher>();

        // Answers Jellyfin's item image action for a library's animated cover before its image processor runs. A
        // filter of MVC, so it runs behind all of Jellyfin's middleware (IP access rules, base URL, CORS, errors). A
        // singleton, so its memo is shared by every request. The options setup is what Configure<MvcOptions> adds,
        // without the AddOptions call that comes with it: the host has the options services already.
        services.AddSingleton<CoverImageFilter>();
        services.AddSingleton<IConfigureOptions<MvcOptions>>(
            new ConfigureOptions<MvcOptions>(options => options.Filters.AddService<CoverImageFilter>()));

        // Jellyfin finds the consumers of its events in the container: a user created, or a user's policy saved.
        services.AddScoped<IEventConsumer<UserUpdatedEventArgs>, AudienceChangeConsumer>();
        services.AddScoped<IEventConsumer<UserCreatedEventArgs>, AudienceChangeConsumer>();
    }

    private static RenderQueue CreateQueue(IServiceProvider provider)
    {
        var builder = provider.GetRequiredService<CoverBuilder>();
        var sessions = provider.GetRequiredService<ISessionManager>();
        var libraries = provider.GetRequiredService<ILibraryManager>();
        return new RenderQueue(
            builder.BuildAsync,
            () => sessions.Sessions.Any(session => session.NowPlayingItem is not null && !session.PlayState.IsPaused),
            libraryId => LibraryExists(libraries, libraryId),
            provider.GetRequiredService<CoverStore>(),
            provider.GetRequiredService<ILogger<RenderQueue>>());
    }

    // The queue forgets a library's covers and history when this says no, so a transient answer while Jellyfin is
    // still loading must not count: the library is gone only when both the list of libraries and a direct lookup
    // say so.
    internal static bool LibraryExists(ILibraryManager libraries, Guid libraryId) =>
        libraries.GetVirtualFolders().Any(folder => Guid.TryParse(folder.ItemId, out var id) && id == libraryId)
        || libraries.GetItemById(libraryId) is not null;
}
