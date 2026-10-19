using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.Services;

public class TeamSeriesGameMatchingTests
{
    private static readonly ReleaseMatchingService Matcher = new(
        NullLogger<ReleaseMatchingService>.Instance,
        new SportsFileNameParser(NullLogger<SportsFileNameParser>.Instance),
        new EventPartDetector(NullLogger<EventPartDetector>.Instance));

    private const string Spurs = "San Antonio Spurs";
    private const string Knicks = "New York Knicks";

    // 2026 NBA Finals as the catalog lists it: no game number in any title,
    // home court switching after games 2 and 4, plus an earlier regular season meeting.
    private static readonly Event RegularSeason = Game(1, Knicks, Spurs, new DateTime(2026, 1, 20), "0");
    private static readonly Event Game1 = Game(2, Spurs, Knicks, new DateTime(2026, 6, 4), "200");
    private static readonly Event Game2 = Game(3, Spurs, Knicks, new DateTime(2026, 6, 7), "200");
    private static readonly Event Game3 = Game(4, Knicks, Spurs, new DateTime(2026, 6, 10), "200");
    private static readonly Event Game4 = Game(5, Knicks, Spurs, new DateTime(2026, 6, 12), "200");
    private static readonly Event Game5 = Game(6, Spurs, Knicks, new DateTime(2026, 6, 15), "200");
    private static readonly Event[] Season = { RegularSeason, Game1, Game2, Game3, Game4, Game5 };

    [Fact]
    public void GameNumberIsThePositionInTheSeries()
    {
        TeamSeriesGameContext.GameNumber(Game1, Season).Should().Be(1);
        TeamSeriesGameContext.GameNumber(Game3, Season).Should().Be(3);
        TeamSeriesGameContext.GameNumber(Game5, Season).Should().Be(5);
    }

    [Fact]
    public void RegularSeasonMeetingsAreNotCountedWhenRoundsAreUnknown()
    {
        var season = Season.Select(evt => Game(evt.Id, evt.HomeTeamName!, evt.AwayTeamName!, evt.EventDate, null)).ToArray();

        TeamSeriesGameContext.GameNumber(season[1], season).Should().Be(1);
        TeamSeriesGameContext.GameNumber(season[4], season).Should().Be(4);
    }

    [Theory]
    [InlineData("NBA.2026.Finals.Game.1.New.York.Knicks.at.San.Antonio.Spurs.FRENCH.VFQ.720p.Web.H264.AAC-ZiGZaG", 1)]
    [InlineData("NBA.2026.Finals.Game.3.San.Antonio.Spurs.at.New.York.Knicks.FRENCH.VFQ.720p.Web.H264.AAC-ZiGZaG", 3)]
    [InlineData("NBA.2026.Finals.Game.4.San.Antonio.Spurs.at.New.York.Knicks.FRENCH.VFQ.720p.Web.H264.AAC-ZiGZaG", 4)]
    [InlineData("NBA.2026.Finals.Game.5.New.York.Knicks.at.San.Antonio.Spurs.FRENCH.VFQ.720p.Web.H264.AAC-ZiGZaG", 5)]
    public void UndatedFinalsReleaseMatchesOnlyItsGame(string title, int game)
    {
        foreach (var evt in Season.Skip(1))
        {
            var number = TeamSeriesGameContext.GameNumber(evt, Season);
            var validation = Matcher.ValidateRelease(Release(title), evt, seriesGameNumber: number);

            if (number == game)
            {
                validation.Rejections.Should().BeEmpty();
                validation.IsMatch.Should().BeTrue();
            }
            else
            {
                validation.IsMatch.Should().BeFalse();
                validation.IsHardRejection.Should().BeTrue();
                validation.Rejections.Should().ContainSingle(reason =>
                    reason.Contains("Game number conflicts with the event", StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void UndatedReleaseStaysAmbiguousWithoutSeriesContext()
    {
        var validation = Matcher.ValidateRelease(
            Release("NBA.2026.Finals.Game.3.San.Antonio.Spurs.at.New.York.Knicks.720p.WEB.H264-GRP"), Game3);

        validation.IsMatch.Should().BeFalse();
        validation.Rejections.Should().ContainSingle(reason =>
            reason.Contains("Game identity is ambiguous", StringComparison.Ordinal));
    }

    [Fact]
    public void RegularSeasonGameNumberIsNotReadAsASeriesGame()
    {
        // A doubleheader "Game 2" without a playoff word or playoff round says
        // nothing about which meeting of the season it is.
        var evt = Game(10, Spurs, Knicks, new DateTime(2026, 3, 2), "0");
        var validation = Matcher.ValidateRelease(
            Release("NBA.2026.San.Antonio.Spurs.vs.New.York.Knicks.Game.2.720p.WEB.H264-GRP"), evt,
            seriesGameNumber: 2);

        validation.IsMatch.Should().BeFalse();
        validation.Rejections.Should().ContainSingle(reason =>
            reason.Contains("Game identity is ambiguous", StringComparison.Ordinal));
    }

    private static Event Game(int id, string home, string away, DateTime date, string? round) => new()
    {
        Id = id,
        Title = $"{home} vs {away}",
        Sport = "Basketball",
        LeagueId = 1,
        Season = "2025-2026",
        Round = round,
        EventDate = DateTime.SpecifyKind(date.AddHours(20), DateTimeKind.Utc),
        BroadcastDate = date,
        BroadcastDateVerified = true,
        HomeTeamId = home == Spurs ? 1 : 2,
        AwayTeamId = away == Spurs ? 1 : 2,
        HomeTeamName = home,
        AwayTeamName = away,
        League = new League { Name = "NBA", Sport = "Basketball" }
    };

    private static ReleaseSearchResult Release(string title) => new()
    {
        Title = title,
        Guid = title,
        DownloadUrl = "http://fixture.invalid/" + Uri.EscapeDataString(title),
        Indexer = "Fixture"
    };
}
