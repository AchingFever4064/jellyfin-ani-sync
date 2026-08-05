using System.Collections.Generic;
using jellyfin_ani_sync.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace jellyfin_ani_sync_unit_tests.HelperTests;

/// <summary>
/// Regression tests for AniDB resolution against two real anime-list-full.xml
/// shapes that previously resolved to the wrong entry.
///
/// Both fixtures are trimmed copies of real rows, keeping only the attributes
/// the resolution logic reads.
/// </summary>
public class AnimeListResolutionTests {
    private readonly ILogger _logger = new NullLogger<AnimeListResolutionTests>();

    /// <summary>
    /// Bleach, TVDB 74796. Nine AniDB entries share this TVDB ID; only the
    /// original 366-episode series carries a mapping-list, and all four
    /// Sennen Kessen Hen entries carry none. Sennen Kessen Hen is TVDB
    /// season 17, not season 2.
    /// </summary>
    private static List<AnimeListHelpers.AnimeListAnime> BleachRows() => new() {
        // The original series. First in document order, and the only row with
        // start/end mappings - which is what made it win every lookup.
        new AnimeListHelpers.AnimeListAnime {
            Anidbid = "2369",
            Tvdbid = "74796",
            Defaulttvdbseason = "a",
            MappingList = new AnimeListHelpers.MappingList {
                Mapping = new List<AnimeListHelpers.Mapping> {
                    // No start/end in the XML. Start and End are non-nullable
                    // ints, so they default to 0 rather than being absent.
                    new() { Anidbseason = 0, Tvdbseason = 0 },
                    new() { Anidbseason = 1, Tvdbseason = 1, Start = 1, End = 20, Offset = 0 },
                    new() { Anidbseason = 1, Tvdbseason = 2, Start = 21, End = 41, Offset = -20 }
                }
            }
        },
        new AnimeListHelpers.AnimeListAnime { Anidbid = "15449", Tvdbid = "74796", Defaulttvdbseason = "17" },
        new AnimeListHelpers.AnimeListAnime { Anidbid = "17765", Tvdbid = "74796", Defaulttvdbseason = "17", Episodeoffset = "13" },
        new AnimeListHelpers.AnimeListAnime { Anidbid = "18220", Tvdbid = "74796", Defaulttvdbseason = "17", Episodeoffset = "26" },
        new AnimeListHelpers.AnimeListAnime { Anidbid = "19079", Tvdbid = "74796", Defaulttvdbseason = "17", Episodeoffset = "40" }
    };

    /// <summary>
    /// Mushoku Tensei, TVDB 371310. TVDB season 2 is split across two AniDB
    /// entries, the second starting after episode 12.
    /// </summary>
    private static List<AnimeListHelpers.AnimeListAnime> MushokuRows() => new() {
        new AnimeListHelpers.AnimeListAnime { Anidbid = "14758", Tvdbid = "371310", Defaulttvdbseason = "1" },
        new AnimeListHelpers.AnimeListAnime {
            Anidbid = "15954", Tvdbid = "371310", Defaulttvdbseason = "1", Episodeoffset = "11",
            MappingList = new AnimeListHelpers.MappingList {
                Mapping = new List<AnimeListHelpers.Mapping> {
                    new() { Anidbseason = 0, Tvdbseason = 0, Start = 2, End = 17, Offset = -17 }
                }
            }
        },
        new AnimeListHelpers.AnimeListAnime {
            Anidbid = "17236", Tvdbid = "371310", Defaulttvdbseason = "2",
            MappingList = new AnimeListHelpers.MappingList {
                Mapping = new List<AnimeListHelpers.Mapping> {
                    new() { Anidbseason = 0, Tvdbseason = 0 }
                }
            }
        },
        new AnimeListHelpers.AnimeListAnime { Anidbid = "18104", Tvdbid = "371310", Defaulttvdbseason = "2", Episodeoffset = "12" },
        new AnimeListHelpers.AnimeListAnime { Anidbid = "18727", Tvdbid = "371310", Defaulttvdbseason = "3" }
    };

    /// <summary>
    /// The original defect. Matching an absolute episode number against
    /// mapping start/end without also comparing the season meant the first row
    /// owning a mapping table always won. Absolute episode 41 falls inside
    /// 2369's TVDB season 2 range of 21-41, so a lookup for TVDB season 17
    /// returned the original Bleach series.
    /// </summary>
    [Test]
    public void AbsoluteEpisodeLookup_DoesNotCollapseSequelOntoParentSeries() {
        var returned = AnimeListHelpers.GetAniDbByEpisodeOffset(
            _logger, absoluteEpisodeNumber: 41, seasonNumber: 17, episodeNumber: 41, related: BleachRows());

        Assert.IsTrue(returned.aniDbId != 2369, "Sequel season resolved to the parent series");
        Assert.IsTrue(returned.aniDbId == 19079);
        Assert.IsTrue(returned.episodeOffset == 40);
    }

    /// <summary>
    /// Positive control for the test above. The same absolute episode number,
    /// looked up against the season it genuinely belongs to, must still resolve
    /// through the mapping table. This proves the season comparison is what
    /// changed the earlier result, rather than the absolute-episode path having
    /// been disabled outright.
    /// </summary>
    [Test]
    public void AbsoluteEpisodeLookup_StillMatchesWithinTheCorrectSeason() {
        var returned = AnimeListHelpers.GetAniDbByEpisodeOffset(
            _logger, absoluteEpisodeNumber: 41, seasonNumber: 2, episodeNumber: 41, related: BleachRows());

        Assert.IsTrue(returned.aniDbId == 2369);
        Assert.IsTrue(returned.episodeOffset == -20, "The mapping's offset must be returned, not null");
    }

    /// <summary>
    /// Each Sennen Kessen Hen cour must be selected by its episode offset
    /// within TVDB season 17.
    /// </summary>
    [Test]
    public void SeasonLookup_SelectsCorrectCourWithinSeason17() {
        var rows = BleachRows();

        Assert.IsTrue(AnimeListHelpers.SeasonLookup(_logger, 17, 1, rows).aniDbId == 15449);
        Assert.IsTrue(AnimeListHelpers.SeasonLookup(_logger, 17, 14, rows).aniDbId == 17765);
        Assert.IsTrue(AnimeListHelpers.SeasonLookup(_logger, 17, 27, rows).aniDbId == 18220);
        Assert.IsTrue(AnimeListHelpers.SeasonLookup(_logger, 17, 41, rows).aniDbId == 19079);
    }

    /// <summary>
    /// A split cour. TVDB season 2 episodes 1-12 belong to 17236; episode 13
    /// onwards belong to 18104 and must be renumbered by its offset of 12.
    /// This is the shape produced by Shokofin season merging, where one
    /// Jellyfin season contains both AniDB series numbered continuously.
    /// </summary>
    [Test]
    public void SeasonLookup_SplitCourSelectsCorrectPartAndOffset() {
        var rows = MushokuRows();

        var firstCour = AnimeListHelpers.SeasonLookup(_logger, 2, 6, rows);
        Assert.IsTrue(firstCour.aniDbId == 17236);
        Assert.IsTrue(firstCour.episodeOffset == null || firstCour.episodeOffset == 0);

        var boundary = AnimeListHelpers.SeasonLookup(_logger, 2, 13, rows);
        Assert.IsTrue(boundary.aniDbId == 18104);
        Assert.IsTrue(boundary.episodeOffset == 12);
        Assert.IsTrue(13 - boundary.episodeOffset == 1, "First episode of the second cour must map to episode 1");

        var lastEpisode = AnimeListHelpers.SeasonLookup(_logger, 2, 24, rows);
        Assert.IsTrue(lastEpisode.aniDbId == 18104);
        Assert.IsTrue(24 - lastEpisode.episodeOffset == 12);
    }

    /// <summary>
    /// The split cour must not leak into neighbouring seasons.
    /// </summary>
    [Test]
    public void SeasonLookup_DoesNotCrossSeasonBoundaries() {
        var rows = MushokuRows();

        Assert.IsTrue(AnimeListHelpers.SeasonLookup(_logger, 1, 6, rows).aniDbId == 14758);
        Assert.IsTrue(AnimeListHelpers.SeasonLookup(_logger, 3, 6, rows).aniDbId == 18727);
    }
}
