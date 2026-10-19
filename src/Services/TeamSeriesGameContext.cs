using Microsoft.EntityFrameworkCore;
using Sportarr.Api.Data;
using Sportarr.Api.Models;

namespace Sportarr.Api.Services;

/// <summary>
/// Works out which game of a playoff series an event is. Scene names such as
/// "NBA.2026.Finals.Game.3.Spurs.at.Knicks" carry the game number but no date,
/// while the catalog titles the fixture "San Antonio Spurs vs New York Knicks"
/// with no number. Counting the games the same two teams play back to back in
/// the season gives the number the release is talking about.
/// </summary>
internal static class TeamSeriesGameContext
{
    // Games of one series are a few days apart. A longer gap ends the run, so
    // regular season meetings between the same pair are not counted.
    private static readonly TimeSpan MaxGapWithinSeries = TimeSpan.FromDays(5);
    private const int MaxSeriesLength = 7;

    public static bool ShouldLoad(Event evt) =>
        evt.LeagueId.HasValue &&
        !string.IsNullOrWhiteSpace(evt.Season) &&
        (evt.HomeTeamId.HasValue && evt.AwayTeamId.HasValue ||
            !string.IsNullOrWhiteSpace(evt.HomeTeamName) && !string.IsNullOrWhiteSpace(evt.AwayTeamName));

    public static async Task<int?> LoadGameNumberAsync(
        SportarrDbContext db,
        Event evt,
        CancellationToken cancellationToken = default)
    {
        if (!ShouldLoad(evt))
            return null;

        var (homeId, awayId) = (evt.HomeTeamId, evt.AwayTeamId);
        var (homeName, awayName) = (evt.HomeTeamName, evt.AwayTeamName);
        var peers = await SelectSeriesFields(PairQuery(db, evt.LeagueId!.Value, new[] { evt.Season! })
                .Where(candidate =>
                    homeId != null && awayId != null &&
                        (candidate.HomeTeamId == homeId && candidate.AwayTeamId == awayId ||
                            candidate.HomeTeamId == awayId && candidate.AwayTeamId == homeId) ||
                    candidate.HomeTeamName == homeName && candidate.AwayTeamName == awayName ||
                    candidate.HomeTeamName == awayName && candidate.AwayTeamName == homeName))
            .ToListAsync(cancellationToken);
        return GameNumber(evt, peers);
    }

    /// <summary>
    /// Game numbers for many events at once, keyed by event id, with one query
    /// per league. Events whose number cannot be worked out are left out.
    /// </summary>
    public static async Task<IReadOnlyDictionary<int, int>> LoadGameNumbersAsync(
        SportarrDbContext db,
        IEnumerable<Event> events,
        CancellationToken cancellationToken = default)
    {
        var numbers = new Dictionary<int, int>();
        foreach (var league in events.Where(ShouldLoad).GroupBy(evt => evt.LeagueId!.Value))
        {
            var seasons = league.Select(evt => evt.Season!).Distinct().ToArray();
            var seasonEvents = await SelectSeriesFields(PairQuery(db, league.Key, seasons))
                .ToListAsync(cancellationToken);
            foreach (var evt in league)
            {
                if (GameNumber(evt, seasonEvents) is { } number)
                    numbers[evt.Id] = number;
            }
        }
        return numbers;
    }

    /// <summary>
    /// The event's position in the run of games its two teams play against
    /// each other, or null when the run does not look like a playoff series.
    /// </summary>
    public static int? GameNumber(Event evt, IEnumerable<Event> seasonEvents)
    {
        var eventIsPostseason = IsPostseasonRound(evt.Round);
        var games = seasonEvents
            .Where(candidate => candidate.Id != evt.Id &&
                candidate.LeagueId == evt.LeagueId &&
                string.Equals(candidate.Season, evt.Season, StringComparison.OrdinalIgnoreCase) &&
                SameTeamPair(candidate, evt) &&
                // A regular season meeting just before the playoffs is not game 1.
                (eventIsPostseason is null || IsPostseasonRound(candidate.Round) is not { } candidateIsPostseason ||
                    candidateIsPostseason == eventIsPostseason))
            .Append(evt)
            .OrderBy(GameDate)
            .ThenBy(candidate => candidate.Id)
            .ToList();

        var index = games.IndexOf(evt);
        var start = index;
        while (start > 0 && GameDate(games[start]) - GameDate(games[start - 1]) <= MaxGapWithinSeries)
            start--;
        var end = index;
        while (end < games.Count - 1 && GameDate(games[end + 1]) - GameDate(games[end]) <= MaxGapWithinSeries)
            end++;

        return end - start + 1 > MaxSeriesLength ? null : index - start + 1;
    }

    /// <summary>
    /// TheSportsDB numbers knockout rounds from 100 upward and the preseason as 500.
    /// Returns null when the round says nothing either way.
    /// </summary>
    public static bool? IsPostseasonRound(string? round) =>
        int.TryParse(round, out var value) ? value is >= 100 and < 500 : null;

    private static DateTime GameDate(Event evt) => evt.BroadcastDate ?? evt.EventDate;

    private static bool SameTeamPair(Event left, Event right)
    {
        if (left.HomeTeamId.HasValue && left.AwayTeamId.HasValue &&
            right.HomeTeamId.HasValue && right.AwayTeamId.HasValue)
        {
            return left.HomeTeamId == right.HomeTeamId && left.AwayTeamId == right.AwayTeamId ||
                left.HomeTeamId == right.AwayTeamId && left.AwayTeamId == right.HomeTeamId;
        }

        return SameName(left.HomeTeamName, right.HomeTeamName) && SameName(left.AwayTeamName, right.AwayTeamName) ||
            SameName(left.HomeTeamName, right.AwayTeamName) && SameName(left.AwayTeamName, right.HomeTeamName);
    }

    private static bool SameName(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && string.Equals(left.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static IQueryable<Event> PairQuery(SportarrDbContext db, int leagueId, string[] seasons) =>
        db.Events
            .AsNoTracking()
            .Where(candidate => candidate.LeagueId == leagueId &&
                candidate.Season != null && seasons.Contains(candidate.Season));

    private static IQueryable<Event> SelectSeriesFields(IQueryable<Event> query) =>
        query.Select(candidate => new Event
        {
            Id = candidate.Id,
            Title = candidate.Title,
            Sport = candidate.Sport,
            LeagueId = candidate.LeagueId,
            Season = candidate.Season,
            Round = candidate.Round,
            HomeTeamId = candidate.HomeTeamId,
            AwayTeamId = candidate.AwayTeamId,
            HomeTeamName = candidate.HomeTeamName,
            AwayTeamName = candidate.AwayTeamName,
            EventDate = candidate.EventDate,
            BroadcastDate = candidate.BroadcastDate
        });
}
