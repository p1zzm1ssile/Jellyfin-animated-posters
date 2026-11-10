using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MedicPosters.Posters;

/// <summary>
/// Makes posters for new films and series soon after they're added, so they don't wait for the nightly run. It waits
/// until nothing new has arrived for <see cref="Quiet"/>, so a scan that adds several titles is made in one go after
/// Jellyfin has finished reading them. Started by PluginServiceRegistrator.
/// </summary>
public sealed class NewTitleWatcher : IHostedService, IDisposable
{
    /// <summary>How long nothing new has to arrive before the new titles are made.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(10);

    private readonly ILibraryManager _libraryManager;
    private readonly PosterService _service;
    private readonly ILogger<NewTitleWatcher> _logger;
    private readonly ConcurrentDictionary<Guid, byte> _new = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _lock = new();
    private CancellationTokenSource? _wait;

    public NewTitleWatcher(ILibraryManager libraryManager, PosterService service, ILogger<NewTitleWatcher> logger)
    {
        _libraryManager = libraryManager;
        _service = service;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _stopping.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _wait?.Dispose();
            _wait = null;
        }

        _stopping.Dispose();
    }

    /// <summary>
    /// The title that gets a poster when this item is added: a film, series, home video or music video itself, or the
    /// series of a new episode. Null for anything else, such as extras, missing episodes, seasons or music.
    /// </summary>
    public static Guid? TitleFor(BaseItem? item)
    {
        if (item is null || item.IsVirtualItem || item.ExtraType is not null)
        {
            return null;
        }

        if (item is Episode episode)
        {
            return episode.SeriesId == Guid.Empty ? null : episode.SeriesId;
        }

        if (item is Movie or Series or MusicVideo || item.GetType() == typeof(Video))
        {
            return item.Id;
        }

        return null;
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (Plugin.Instance?.Configuration.MakeNewTitlesSoon != true || TitleFor(e.Item) is not { } id)
        {
            return;
        }

        _new.TryAdd(id, 0);
        Schedule();
    }

    // Starts the wait again, so the titles are made once the scan has gone quiet.
    private void Schedule()
    {
        CancellationToken token;
        try
        {
            lock (_lock)
            {
                _wait?.Cancel();
                _wait?.Dispose();
                _wait = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                token = _wait.Token;
            }
        }
        catch (ObjectDisposedException)
        {
            // Jellyfin is shutting down.
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Quiet, token).ConfigureAwait(false);
                await MakeAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Animated Film Posters: couldn't make posters for new titles");
            }
        });
    }

    private async Task MakeAsync()
    {
        var ids = _new.Keys.ToList();
        foreach (var id in ids)
        {
            _new.TryRemove(id, out _);
        }

        if (ids.Count == 0)
        {
            return;
        }

        // A new title that arrives while these are made only restarts the wait, so this run isn't cut short.
        if (!await _service.MakeNewAsync(ids, _stopping.Token).ConfigureAwait(false))
        {
            // Another run is going; try again once it's had time to finish.
            foreach (var id in ids)
            {
                _new.TryAdd(id, 0);
            }

            Schedule();
        }
    }
}
