using KHost.Abstractions.Messaging;
using KHost.Abstractions.Messaging.Messages;
using KHost.Abstractions.Models;
using KHost.Abstractions.Services;
using Microsoft.Extensions.Logging.Abstractions;
using CastMedia = Sharpcaster.Models.Media.Media;

namespace KHost.Plugins.Chromecast.Tests;

/// <summary>What the receiver is given to show when no song is on it; no emulator needed.</summary>
public class ChromecastPictureTests : IDisposable
{
    private const string Lan = "192.168.1.10";
    private const string DeviceId = "Living Room TV";

    private readonly IMessageBroker _broker = Substitute.For<IMessageBroker>();
    private readonly IServiceProvider _services = Substitute.For<IServiceProvider>();
    private readonly IPlaybackService _playback = Substitute.For<IPlaybackService>();
    private readonly IVenuesService _venues = Substitute.For<IVenuesService>();
    private readonly IMediaService _library = Substitute.For<IMediaService>();
    private readonly IMediaStreamService _streams = Substitute.For<IMediaStreamService>();
    private readonly List<CastMedia> _cast = [];
    private readonly List<IDisposable> _subscriptions = [];
    private int _stops;

    private Action<PlaybackChanged>? _playbackChanged;
    private Action<SelectedVenueChanged>? _venueChanged;
    private PlaybackProgram _program = new PlaybackProgram.Idle();
    private readonly ChromecastDisplayProvider _display;

    public ChromecastPictureTests()
    {
        _broker.Subscribe(Arg.Any<Action<PlaybackChanged>>()).Returns(call =>
        {
            _playbackChanged = call.Arg<Action<PlaybackChanged>>();
            return Track(Substitute.For<IDisposable>());
        });
        _broker.Subscribe(Arg.Any<Action<SelectedVenueChanged>>()).Returns(call =>
        {
            _venueChanged = call.Arg<Action<SelectedVenueChanged>>();
            return Track(Substitute.For<IDisposable>());
        });

        _playback.CurrentProgram.Returns(_ => _program);
        _services.GetService(typeof(IPlaybackService)).Returns(_playback);
        _services.GetService(typeof(IVenuesService)).Returns(_venues);
        _services.GetService(typeof(IMediaService)).Returns(_library);
        _services.GetService(typeof(IMediaStreamService)).Returns(_streams);
        _venues.ReadSelectedVenueAsync().Returns((Venue?)null);

        _display = new ChromecastDisplayProvider(
            NullLogger<ChromecastDisplayProvider>.Instance,
            new ChromecastDisplayProvider.ServiceOptions(),
            _broker,
            services: _services,
            castMedia: (media, _) =>
            {
                lock (_cast) _cast.Add(media);
                return Task.CompletedTask;
            },
            lanAddress: () => Lan,
            stopMedia: () =>
            {
                Interlocked.Increment(ref _stops);
                return Task.CompletedTask;
            });
    }

    [Fact]
    public void DescribeTarget_AsksForBurnedLyrics_AndNoStems()
    {
        var target = _display.DescribeTarget();

        Assert.False(target.MixesStems);
        Assert.True(target.BurnLyrics);
    }

    [Fact]
    public async Task PlaybackChanged_AdStill_LoadsTheImageAtAReachableUrl()
    {
        await ConnectAsync();

        await MoveToAsync(new PlaybackProgram.AdStill("http://localhost:5251/media/image/ad.png", ImageScaling.Fit));

        var shown = Assert.Single(_cast);
        Assert.Equal($"http://{Lan}:5251/media/image/ad.png", shown.ContentUrl);
        Assert.Equal("image/png", shown.ContentType);
    }

    [Fact]
    public async Task PlaybackChanged_IdleWithVenueCard_LoadsTheCardAtAReachableUrl()
    {
        var card = ArrangeVenueCard();

        await ConnectAsync();

        var shown = Assert.Single(_cast);
        Assert.Equal($"http://{Lan}:5251/media/image/{card}", shown.ContentUrl);
        Assert.Equal("image/png", shown.ContentType);
    }

    [Fact]
    public async Task PlaybackChanged_SameProgramTwice_LoadsTheImageOnce()
    {
        await ConnectAsync();

        // Equal by value but distinct instances: a pause or a seek announces the same program.
        await MoveToAsync(new PlaybackProgram.AdStill("http://localhost:5251/media/image/ad", ImageScaling.Fit));
        await MoveToAsync(new PlaybackProgram.AdStill("http://localhost:5251/media/image/ad", ImageScaling.Fit));

        Assert.Single(_cast);
    }

    [Fact]
    public async Task PlaybackChanged_Playing_LoadsNoImage()
    {
        ArrangeVenueCard();
        _program = Playing();

        await ConnectAsync();
        await MoveToAsync(Playing());

        Assert.Empty(_cast);
    }

    [Fact]
    public async Task PlaybackChanged_IdleWithoutCard_LoadsNothing()
    {
        await ConnectAsync();
        await MoveToAsync(new PlaybackProgram.Idle());

        Assert.Empty(_cast);
    }

    [Fact]
    public async Task PlaybackChanged_WhileDisconnected_LoadsNothing()
    {
        await MoveToAsync(new PlaybackProgram.AdStill("http://localhost:5251/media/image/ad", ImageScaling.Fit));

        Assert.Empty(_cast);
    }

    [Fact]
    public async Task PlaybackChanged_SameIdleProgramTwice_ReadsTheVenueOnce()
    {
        ArrangeVenueCard();
        await ConnectAsync();

        // A pause or seek between singers must not become a library read per announcement.
        await MoveToAsync(new PlaybackProgram.Idle());

        await _venues.Received(1).ReadSelectedVenueAsync();
        Assert.Single(_cast);
    }

    [Fact]
    public async Task PlaybackChanged_IdleWhileDisconnected_ReadsNothing()
    {
        ArrangeVenueCard();

        await MoveToAsync(new PlaybackProgram.Idle());

        await _venues.DidNotReceive().ReadSelectedVenueAsync();
        Assert.Empty(_cast);
    }

    [Fact]
    public async Task SelectedVenueChanged_SameCard_LoadsNothingMore()
    {
        ArrangeVenueCard();
        await ConnectAsync();

        await VenueMovesAsync();

        Assert.Single(_cast);
    }

    [Fact]
    public async Task SelectedVenueChanged_NewCard_LoadsIt()
    {
        ArrangeVenueCard();
        await ConnectAsync();

        var replacement = ArrangeVenueCard();
        await VenueMovesAsync();

        Assert.Equal(2, _cast.Count);
        Assert.EndsWith(replacement.ToString(), _cast[1].ContentUrl);
    }

    [Fact]
    public async Task Adopt_NewSession_ShowsThePictureAgain()
    {
        await ConnectAsync();
        await MoveToAsync(new PlaybackProgram.AdStill("http://localhost:5251/media/image/ad", ImageScaling.Fit));

        // A receiver picked back up forgot what it held.
        await ConnectAsync();

        Assert.Equal(2, _cast.Count);
    }

    [Fact]
    public async Task PlaybackChanged_SongEnds_ShowsTheIdleCardAgain()
    {
        ArrangeVenueCard();
        await ConnectAsync();

        await MoveToAsync(Playing());
        await MoveToAsync(new PlaybackProgram.Idle());

        // Once on connect, once after the song replaced it; nothing while the song played.
        Assert.Equal(2, _cast.Count);
        Assert.Equal(_cast[0].ContentUrl, _cast[1].ContentUrl);
    }

    [Fact]
    public async Task PlaybackChanged_AdStillThenIdleWithoutCard_StopsTheImage()
    {
        await ConnectAsync();
        await MoveToAsync(new PlaybackProgram.AdStill("http://localhost:5251/media/image/ad", ImageScaling.Fit));

        await MoveToAsync(new PlaybackProgram.Idle());

        Assert.Equal(1, _stops);
    }

    [Fact]
    public async Task PlaybackChanged_IdleWithoutCard_NothingOfOursShowing_StopsNothing()
    {
        _program = Playing();
        await ConnectAsync();

        // Whatever the receiver holds (the song, or another sender's media) is not ours to stop.
        await MoveToAsync(new PlaybackProgram.Idle());

        Assert.Equal(0, _stops);
        Assert.Empty(_cast);
    }

    [Fact]
    public async Task PlaybackChanged_AdStillThenSongThenIdleWithoutCard_StopsNothing()
    {
        await ConnectAsync();
        await MoveToAsync(new PlaybackProgram.AdStill("http://localhost:5251/media/image/ad", ImageScaling.Fit));

        // The song's load replaced the still, so what is up at Idle is the song's, not ours.
        await MoveToAsync(Playing());
        await MoveToAsync(new PlaybackProgram.Idle());

        Assert.Equal(0, _stops);
    }

    [Fact]
    public void Dispose_DropsEverySubscription()
    {
        _display.Dispose();

        Assert.Equal(2, _subscriptions.Count);
        foreach (var subscription in _subscriptions) subscription.Received(1).Dispose();
    }

    private IDisposable Track(IDisposable subscription)
    {
        _subscriptions.Add(subscription);
        return subscription;
    }

    private Guid ArrangeVenueCard()
    {
        var card = new Media { FilePath = "card.png", Title = "Card", Format = "PNG" };
        var venue = new Venue { Name = "The Room" };
        venue.Settings.BrandingImageMediaId = card.Id;

        _venues.ReadSelectedVenueAsync().Returns(venue);
        _library.ReadAsync(card.Id).Returns(card);
        _streams.BuildImageUrl(card.Id).Returns($"http://localhost:5251/media/image/{card.Id}");
        return card.Id;
    }

    private static PlaybackProgram.Playing Playing()
        => new(new Media { Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), FilePath = "song.mp4", Title = "Song" }, null);

    private async Task ConnectAsync()
    {
        _display.Adopt(DeviceId, null);
        await _display.WhenPictureDrawnAsync();
    }

    private async Task VenueMovesAsync()
    {
        Assert.NotNull(_venueChanged);
        _venueChanged!(new SelectedVenueChanged());
        await _display.WhenPictureDrawnAsync();
    }

    private async Task MoveToAsync(PlaybackProgram program)
    {
        _program = program;
        Assert.NotNull(_playbackChanged);
        _playbackChanged!(new PlaybackChanged());
        await _display.WhenPictureDrawnAsync();
    }

    public void Dispose() => _display.Dispose();
}
