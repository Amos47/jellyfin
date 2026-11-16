using System;
using System.Collections.Generic;
using System.Linq;
using Emby.Server.Implementations.Discovery;
using MediaBrowser.Controller.Discovery;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Discovery;

/// <summary>
/// Fork: the show page's season and episode states (the pure <see cref="ShowSeasons.Build"/>).
/// </summary>
public class ShowSeasonsTests
{
    private const int Show = 456;

    private static readonly DateOnly _today = new(2026, 11, 11);

    private static readonly IReadOnlyList<SeasonRequest> _noRequests = [];

    private static readonly IReadOnlyList<SonarrSeasonStatus> _noStatus = [];

    // A season of TMDB episodes, all aired well before today unless said otherwise.
    private static TmdbSeason Tmdb(int season, int episodes, DateOnly? airDate = null)
        => new(
            season,
            "Season " + season,
            Enumerable.Range(1, episodes)
                .Select(e => new TmdbEpisode(e, $"TMDB S{season}E{e}", $"Overview {e}", airDate ?? new DateOnly(2020, 1, 1), 22, $"/s{season}e{e}.jpg"))
                .ToList());

    // Library episodes first..last of a season.
    private static IEnumerable<LibraryEpisode> Library(int season, int first, int last)
        => Enumerable.Range(first, last - first + 1)
            .Select(e => new LibraryEpisode(Guid.NewGuid(), season, e, null, $"Lib S{season}E{e}", null, new DateOnly(2020, 1, 1), 23, "Season " + season));

    private static SeasonRequest Request(int season, string state)
        => new(Show, season, state, state == SeasonRequest.StateFailed ? "no release" : null, DateTime.UtcNow);

    private static SonarrSeasonStatus Sonarr(int season, string status, bool mismatch = false)
        => new(Show, season, status, mismatch);

    private static SeasonView Season(IReadOnlyList<SeasonView> seasons, int number)
        => Assert.Single(seasons, s => s.SeasonNumber == number);

    [Fact]
    public void States_FollowSpecTable()
    {
        // S3 on the server, S5 downloading 9 of 22, S4 not on the server; S6 requested from the page, S7 wanted by
        // Sonarr, S2 partly (18 of 22, nothing on its way).
        var library = Library(3, 1, 22).Concat(Library(5, 1, 9)).Concat(Library(2, 1, 18)).ToList();
        var tmdb = new[] { Tmdb(2, 22), Tmdb(3, 22), Tmdb(4, 22), Tmdb(5, 22), Tmdb(6, 10), Tmdb(7, 10) };
        var requests = new[] { Request(6, SeasonRequest.StatePending) };
        var sonarr = new[] { Sonarr(5, SonarrSeasonStatus.StatusDownloading), Sonarr(7, SonarrSeasonStatus.StatusWanted) };

        var seasons = ShowSeasons.Build(library, tmdb, requests, sonarr, _today, canGet: true);

        Assert.Equal(new[] { 2, 3, 4, 5, 6, 7 }, seasons.Select(s => s.SeasonNumber));

        var s3 = Season(seasons, 3);
        Assert.Equal((SeasonView.StateOnServer, 22, 22, 22, false), (s3.State, s3.OnServer, s3.Aired, s3.Total, s3.CanGet));
        Assert.All(s3.Episodes, e => Assert.Equal(EpisodeView.StateOnServer, e.State));
        Assert.Equal(library.First(l => l.SeasonNumber == 3 && l.EpisodeNumber == 1).ItemId, s3.Episodes[0].ItemId);
        Assert.Equal("Lib S3E1", s3.Episodes[0].Name);

        var s4 = Season(seasons, 4);
        Assert.Equal((SeasonView.StateNotOnServer, 0, 22, 22, true), (s4.State, s4.OnServer, s4.Aired, s4.Total, s4.CanGet));
        var s4e1 = s4.Episodes[0];
        Assert.Equal(EpisodeView.StateNotOnServer, s4e1.State);
        Assert.Null(s4e1.ItemId);
        Assert.Equal((4, 1, "TMDB S4E1", "Overview 1", 22), (s4e1.SeasonNumber, s4e1.EpisodeNumber!.Value, s4e1.Name, s4e1.Overview, s4e1.RuntimeMinutes!.Value));
        Assert.Equal(new DateOnly(2020, 1, 1), s4e1.AirDate);
        Assert.Equal("https://image.tmdb.org/t/p/w300/s4e1.jpg", s4e1.StillUrl);

        var s5 = Season(seasons, 5);
        Assert.Equal((SeasonView.StateDownloading, 9, 22, 22, false), (s5.State, s5.OnServer, s5.Aired, s5.Total, s5.CanGet));
        Assert.Equal(22, s5.Episodes.Count);
        Assert.Equal(9, s5.Episodes.Count(e => e.State == EpisodeView.StateOnServer));
        Assert.Equal(13, s5.Episodes.Count(e => e.State == EpisodeView.StateComing));

        var s6 = Season(seasons, 6);
        Assert.Equal((SeasonView.StateRequested, false), (s6.State, s6.CanGet));
        Assert.All(s6.Episodes, e => Assert.Equal(EpisodeView.StateComing, e.State));

        Assert.Equal((SeasonView.StateRequested, false), (Season(seasons, 7).State, Season(seasons, 7).CanGet));

        var s2 = Season(seasons, 2);
        Assert.Equal((SeasonView.StatePartly, 18, 22, true), (s2.State, s2.OnServer, s2.Total, s2.CanGet));
        Assert.Equal(4, s2.Episodes.Count(e => e.State == EpisodeView.StateNotOnServer));

        // Downloads off for shows: nothing can be got, states unchanged.
        var off = ShowSeasons.Build(library, tmdb, requests, sonarr, _today, canGet: false);
        Assert.All(off, s => Assert.False(s.CanGet));
        Assert.Equal(seasons.Select(s => s.State), off.Select(s => s.State));
    }

    [Fact]
    public void FailedRequest_IsNotOnServerAndGettable()
    {
        var seasons = ShowSeasons.Build([], [Tmdb(4, 22)], [Request(4, SeasonRequest.StateFailed)], _noStatus, _today, canGet: true);

        var s4 = Season(seasons, 4);
        Assert.Equal((SeasonView.StateNotOnServer, true, true), (s4.State, s4.CanGet, s4.RequestFailed));
        Assert.All(s4.Episodes, e => Assert.Equal(EpisodeView.StateNotOnServer, e.State));

        // A handled request leaves the state to Sonarr; with no status yet, it's as if there were none.
        var handled = Season(ShowSeasons.Build([], [Tmdb(4, 22)], [Request(4, SeasonRequest.StateHandled)], _noStatus, _today, canGet: true), 4);
        Assert.Equal((SeasonView.StateNotOnServer, true, false), (handled.State, handled.CanGet, handled.RequestFailed));
    }

    [Fact]
    public void Mismatch_HidesGet()
    {
        var library = Library(4, 1, 5).ToList();
        var sonarr = new[] { Sonarr(4, SonarrSeasonStatus.StatusUnmonitored, mismatch: true), Sonarr(5, SonarrSeasonStatus.StatusIdle, mismatch: true) };

        var seasons = ShowSeasons.Build(library, [Tmdb(4, 22), Tmdb(5, 22)], _noRequests, sonarr, _today, canGet: true);

        Assert.Equal((SeasonView.StatePartly, false), (Season(seasons, 4).State, Season(seasons, 4).CanGet));
        Assert.Equal((SeasonView.StateNotOnServer, false), (Season(seasons, 5).State, Season(seasons, 5).CanGet));
    }

    [Fact]
    public void UnairedSeason_NotAired_NoGet_NotPartly()
    {
        // S6 hasn't aired (one date after today, the rest unknown); Sonarr monitors it already and reports idle.
        var unaired = new TmdbSeason(
            6,
            "Season 6",
            [
                new TmdbEpisode(1, "Premiere", null, _today.AddDays(30), null, null),
                new TmdbEpisode(2, "TBA", null, null, null, null),
            ]);

        // S7 is half aired: on the server up to today, so it's on the server.
        var halfAired = new TmdbSeason(
            7,
            "Season 7",
            [
                new TmdbEpisode(1, "One", null, _today.AddDays(-7), 22, null),
                new TmdbEpisode(2, "Two", null, _today, 22, null),
                new TmdbEpisode(3, "Three", null, _today.AddDays(1), 22, null),
            ]);
        var library = Library(7, 1, 2).ToList();
        var sonarr = new[] { Sonarr(6, SonarrSeasonStatus.StatusIdle), Sonarr(7, SonarrSeasonStatus.StatusIdle) };

        var seasons = ShowSeasons.Build(library, [unaired, halfAired], _noRequests, sonarr, _today, canGet: true);

        var s6 = Season(seasons, 6);
        Assert.Equal((SeasonView.StateNotOnServer, 0, 0, 2, false), (s6.State, s6.OnServer, s6.Aired, s6.Total, s6.CanGet));
        Assert.All(s6.Episodes, e => Assert.Equal(EpisodeView.StateNotAired, e.State));
        Assert.Equal(_today.AddDays(30), s6.Episodes[0].AirDate);

        var s7 = Season(seasons, 7);
        Assert.Equal((SeasonView.StateOnServer, 2, 2, 3, false), (s7.State, s7.OnServer, s7.Aired, s7.Total, s7.CanGet));
        Assert.Equal(new[] { EpisodeView.StateOnServer, EpisodeView.StateOnServer, EpisodeView.StateNotAired }, s7.Episodes.Select(e => e.State));

        // Wanted by Sonarr changes nothing either: there's nothing aired to get.
        var wanted = Season(ShowSeasons.Build([], [unaired], _noRequests, [Sonarr(6, SonarrSeasonStatus.StatusWanted)], _today, canGet: true), 6);
        Assert.Equal((SeasonView.StateNotOnServer, false), (wanted.State, wanted.CanGet));
    }

    [Fact]
    public void Specials_OnlyFromLibrary_NeverGettable()
    {
        var tmdbSpecials = Tmdb(0, 5);

        // TMDB's specials alone don't make a season 0.
        var none = ShowSeasons.Build([], [tmdbSpecials, Tmdb(1, 10)], [Request(0, SeasonRequest.StatePending)], _noStatus, _today, canGet: true);
        Assert.DoesNotContain(none, s => s.SeasonNumber == 0);

        // The library's specials do, on the server, never gettable, even with a request or a Sonarr status.
        var library = Library(0, 1, 2).ToList();
        var seasons = ShowSeasons.Build(library, [tmdbSpecials, Tmdb(1, 10)], [Request(0, SeasonRequest.StateFailed)], [Sonarr(0, SonarrSeasonStatus.StatusIdle)], _today, canGet: true);
        var s0 = Season(seasons, 0);
        Assert.Equal((SeasonView.StateOnServer, 2, 2, 2, false), (s0.State, s0.OnServer, s0.Aired, s0.Total, s0.CanGet));
        Assert.Equal("Season 0", s0.Name);
        Assert.Equal(new[] { 0, 1 }, seasons.Select(s => s.SeasonNumber));

        // Named "Specials" when the library has no name for it.
        var unnamed = ShowSeasons.Build([new LibraryEpisode(Guid.NewGuid(), 0, 1, null, "Special", null, null, null, null)], null, _noRequests, _noStatus, _today, canGet: true);
        Assert.Equal("Specials", Assert.Single(unnamed).Name);
    }

    [Fact]
    public void LibraryEpisodeWithoutTmdbMatch_StillShown()
    {
        var extraId = Guid.NewGuid();
        var unnumberedId = Guid.NewGuid();
        var library = Library(3, 1, 22)
            .Append(new LibraryEpisode(extraId, 3, 23, null, "Bonus", "Not on TMDB", null, 40, "Season 3"))
            .Append(new LibraryEpisode(unnumberedId, 3, null, null, "Unnumbered", null, null, null, "Season 3"))
            .ToList();

        var s3 = Assert.Single(ShowSeasons.Build(library, [Tmdb(3, 22)], _noRequests, _noStatus, _today, canGet: true));

        Assert.Equal((SeasonView.StateOnServer, 24, 22, 24, false), (s3.State, s3.OnServer, s3.Aired, s3.Total, s3.CanGet));
        Assert.Equal(24, s3.Episodes.Count);
        var bonus = s3.Episodes[22];
        Assert.Equal((23, "Bonus", "Not on TMDB", 40, extraId, EpisodeView.StateOnServer), (bonus.EpisodeNumber!.Value, bonus.Name, bonus.Overview, bonus.RuntimeMinutes!.Value, bonus.ItemId!.Value, bonus.State));
        Assert.Null(bonus.StillUrl);
        var last = s3.Episodes[23];
        Assert.Equal((null, unnumberedId), (last.EpisodeNumber, last.ItemId!.Value));

        // A multi-episode file covers its TMDB episodes, as one row.
        var doubleId = Guid.NewGuid();
        var multi = Assert.Single(ShowSeasons.Build(
            [new LibraryEpisode(doubleId, 1, 1, 2, "One and two", null, null, null, null)],
            [Tmdb(1, 2)],
            _noRequests,
            _noStatus,
            _today,
            canGet: true));
        Assert.Equal((SeasonView.StateOnServer, 2, 2, false), (multi.State, multi.OnServer, multi.Total, multi.CanGet));
        Assert.Equal(doubleId, Assert.Single(multi.Episodes).ItemId);

        // A library episode in a season TMDB doesn't list makes that season, on the server.
        var libraryOnly = Season(ShowSeasons.Build(Library(9, 1, 3).ToList(), [Tmdb(1, 2)], _noRequests, _noStatus, _today, canGet: true), 9);
        Assert.Equal((SeasonView.StateOnServer, 3, 3, 3, false), (libraryOnly.State, libraryOnly.OnServer, libraryOnly.Aired, libraryOnly.Total, libraryOnly.CanGet));
        Assert.Equal("Season 9", libraryOnly.Name);
    }

    [Fact]
    public void IdleWithNothingOnServer_IsNotOnServer()
    {
        var seasons = ShowSeasons.Build([], [Tmdb(6, 10)], _noRequests, [Sonarr(6, SonarrSeasonStatus.StatusIdle)], _today, canGet: true);

        var s6 = Season(seasons, 6);
        Assert.Equal((SeasonView.StateNotOnServer, 0, true), (s6.State, s6.OnServer, s6.CanGet));
        Assert.All(s6.Episodes, e => Assert.Equal(EpisodeView.StateNotOnServer, e.State));
    }

    [Fact]
    public void IdleWithFewer_IsPartly()
    {
        var library = Library(5, 1, 9).ToList();
        var sonarr = new[] { Sonarr(5, SonarrSeasonStatus.StatusIdle), Sonarr(6, SonarrSeasonStatus.StatusIdle) };

        var seasons = ShowSeasons.Build(library, [Tmdb(5, 22), Tmdb(6, 10)], _noRequests, sonarr, _today, canGet: true);

        var s5 = Season(seasons, 5);
        Assert.Equal((SeasonView.StatePartly, 9, 22, true), (s5.State, s5.OnServer, s5.Aired, s5.CanGet));
        Assert.Equal(13, s5.Episodes.Count(e => e.State == EpisodeView.StateNotOnServer));

        // Searched and nothing found, none on the server: Get is offered again.
        var s6 = Season(seasons, 6);
        Assert.Equal((SeasonView.StateNotOnServer, 0, true), (s6.State, s6.OnServer, s6.CanGet));

        // A pending request comes first, then Sonarr.
        var pending = Season(ShowSeasons.Build(library, [Tmdb(5, 22)], [Request(5, SeasonRequest.StatePending)], sonarr, _today, canGet: true), 5);
        Assert.Equal((SeasonView.StateRequested, false), (pending.State, pending.CanGet));

        // Unmonitored with some: partly by count.
        var unmonitored = Season(ShowSeasons.Build(library, [Tmdb(5, 22)], _noRequests, [Sonarr(5, SonarrSeasonStatus.StatusUnmonitored)], _today, canGet: true), 5);
        Assert.Equal((SeasonView.StatePartly, true), (unmonitored.State, unmonitored.CanGet));
    }

    [Fact]
    public void NoTmdb_LibraryOnly()
    {
        var library = Library(2, 1, 3).Concat(Library(1, 1, 2)).ToList();

        var seasons = ShowSeasons.Build(library, null, [Request(1, SeasonRequest.StatePending)], [Sonarr(2, SonarrSeasonStatus.StatusDownloading)], _today, canGet: true);

        Assert.Equal(new[] { 1, 2 }, seasons.Select(s => s.SeasonNumber));
        Assert.All(seasons, s =>
        {
            Assert.Equal(SeasonView.StateOnServer, s.State);
            Assert.False(s.CanGet);
            Assert.Equal(s.OnServer, s.Aired);
            Assert.Equal(s.OnServer, s.Total);
            Assert.All(s.Episodes, e => Assert.Equal(EpisodeView.StateOnServer, e.State));
        });
        Assert.Equal(new int?[] { 1, 2, 3 }, Season(seasons, 2).Episodes.Select(e => e.EpisodeNumber));

        Assert.Empty(ShowSeasons.Build([], null, _noRequests, _noStatus, _today, canGet: true));
    }
}
