using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.HomeRows.Providers.ContinueWatching;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Xunit;

namespace Jellyfin.HomeRows.Tests.Providers;

public class ContinueWatchingCombinerTests
{
    private static readonly DateTime _now = new(2026, 10, 16, 20, 0, 0, DateTimeKind.Utc);

    private readonly Guid _svu = Guid.NewGuid();
    private readonly Guid _office = Guid.NewGuid();
    private readonly Guid _bigBang = Guid.NewGuid();

    private static ContinueWatchingCandidate Ep(string name, Guid series, DateTime? lastPlayed = null)
        => new(new Episode { Id = Guid.NewGuid(), Name = name, SeriesId = series }, series, lastPlayed);

    private static ContinueWatchingCandidate Played(string name, Guid series, DateTime? lastPlayed = null)
        => Ep(name, series, lastPlayed) with { IsPlayed = true };

    private static ContinueWatchingCandidate Mov(string name, DateTime? lastPlayed = null)
        => new(new Movie { Id = Guid.NewGuid(), Name = name }, null, lastPlayed);

    private static string[] Names(IReadOnlyList<BaseItem> items) => items.Select(i => i.Name).ToArray();

    [Fact]
    public void InProgressEpisodeReplacesNextUpOfSameSeries()
    {
        var inProgress = Ep("SVU S04E08", _svu, _now.AddHours(-1));
        var next = Ep("SVU S04E09", _svu);

        var result = ContinueWatchingCombiner.Combine([inProgress], [next], [inProgress]);

        Assert.Equal(["SVU S04E08"], Names(result));
    }

    [Fact]
    public void SeriesWithoutInProgressUseNextUp()
    {
        var next = Ep("Office S02E03", _office);
        var lastWatched = Ep("Office S02E02", _office, _now.AddDays(-1));

        var result = ContinueWatchingCombiner.Combine([], [next], [lastWatched]);

        Assert.Equal(["Office S02E03"], Names(result));
    }

    [Fact]
    public void PartiallyWatchedMoviesIncluded()
    {
        var result = ContinueWatchingCombiner.Combine([Mov("Movie", _now.AddDays(-2))], [], []);

        Assert.Equal(["Movie"], Names(result));
    }

    [Fact]
    public void OrderedByMostRecentActivity()
    {
        var movie = Mov("Movie", _now.AddDays(-3));
        var svuInProgress = Ep("SVU S04E08", _svu, _now.AddDays(-2));
        var officeNext = Ep("Office S02E03", _office);
        var officeLastWatched = Ep("Office S02E02", _office, _now.AddHours(-2));

        var result = ContinueWatchingCombiner.Combine([svuInProgress, movie], [officeNext], [officeLastWatched, svuInProgress]);

        Assert.Equal(["Office S02E03", "SVU S04E08", "Movie"], Names(result));
    }

    [Fact]
    public void StaleInProgressReplacedWhenLaterEpisodeFinishedAfterwards()
    {
        var stale = Ep("SVU S04E08", _svu, _now.AddDays(-60));
        var finishedLater = Ep("SVU S04E10", _svu, _now.AddDays(-1));
        var next = Ep("SVU S04E11", _svu);

        var result = ContinueWatchingCombiner.Combine([stale], [next], [finishedLater]);

        Assert.Equal(["SVU S04E11"], Names(result));
    }

    [Fact]
    public void SeriesTwiceInNextUpGetsOneCard()
    {
        var rewatchNext = Ep("Big Bang S06E03", _bigBang);
        var newEpisode = Ep("Big Bang S12E24", _bigBang);
        var lastWatched = Ep("Big Bang S06E02", _bigBang, _now.AddHours(-5));

        var result = ContinueWatchingCombiner.Combine([], [rewatchNext, newEpisode], [lastWatched]);

        Assert.Equal(["Big Bang S06E03"], Names(result));
    }

    [Fact]
    public void NextUpWithoutActivityKeepsServerOrderAfterDated()
    {
        var movie = Mov("Movie", _now.AddDays(-1));
        var officeNext = Ep("Office S02E03", _office);
        var svuNext = Ep("SVU S04E09", _svu);

        var result = ContinueWatchingCombiner.Combine([movie], [officeNext, svuNext], []);

        Assert.Equal(["Movie", "Office S02E03", "SVU S04E09"], Names(result));
    }

    [Fact]
    public void OnlyMostRecentInProgressEpisodePerSeries()
    {
        var recent = Ep("SVU S04E08", _svu, _now.AddHours(-1));
        var older = Ep("SVU S02E01", _svu, _now.AddDays(-10));

        var result = ContinueWatchingCombiner.Combine([recent, older], [], []);

        Assert.Equal(["SVU S04E08"], Names(result));
    }

    [Fact]
    public void ResumeRewatch_LosesToNewNextUp()
    {
        var rewatch = Played("SVU S03E05", _svu, _now.AddHours(-1));
        var next = Ep("SVU S27E08", _svu);

        var result = ContinueWatchingCombiner.Combine([rewatch], [next], [rewatch]);

        Assert.Equal(["SVU S27E08"], Names(result));
    }

    [Fact]
    public void ResumeFirstWatch_StillWins()
    {
        var inProgress = Ep("SVU S27E08", _svu, _now.AddHours(-1));
        var next = Ep("SVU S27E09", _svu);

        var result = ContinueWatchingCombiner.Combine([inProgress], [next], [inProgress]);

        Assert.Equal(["SVU S27E08"], Names(result));
    }

    [Fact]
    public void ResumeRewatch_NoNewEpisode_Kept()
    {
        var rewatch = Played("SVU S03E05", _svu, _now.AddHours(-1));
        var rewatchNext = Played("SVU S03E05", _svu);

        var result = ContinueWatchingCombiner.Combine([rewatch], [rewatchNext], [rewatch]);

        Assert.Equal(["SVU S03E05"], Names(result));
    }

    [Fact]
    public void ResumeNew_BeatsMoreRecentResumeRewatch()
    {
        var rewatch = Played("SVU S03E05", _svu, _now.AddHours(-1));
        var halfWatchedNew = Ep("SVU S27E08", _svu, _now.AddHours(-2));

        var result = ContinueWatchingCombiner.Combine([rewatch, halfWatchedNew], [], [rewatch, halfWatchedNew]);

        Assert.Equal(["SVU S27E08"], Names(result));
    }
}
