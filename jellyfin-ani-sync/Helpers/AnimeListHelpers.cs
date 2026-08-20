using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml.Serialization;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Extensions.Logging;

namespace jellyfin_ani_sync.Helpers {
    public class AnimeListHelpers {
        /// <summary>
        /// Look up a provider ID without caring about key casing.
        ///
        /// Jellyfin's ProviderIds dictionary is NOT reliably case-insensitive once an
        /// item has been round-tripped through the database, and metadata providers
        /// disagree about spelling: Shokofin writes "AniDB", while this plugin
        /// historically looked for "Anidb". The result was that every AniDB ID in a
        /// Shoko-backed library was invisible to the plugin, silently falling through
        /// to the TVDB code path.
        /// </summary>
        public static bool TryGetProviderId(Dictionary<string, string> providerIds, string key, out string value) {
            value = null;
            if (providerIds == null) return false;
            foreach (var providerId in providerIds) {
                if (string.Equals(providerId.Key, key, StringComparison.OrdinalIgnoreCase)) {
                    value = providerId.Value;
                    return true;
                }
            }

            return false;
        }

        public static bool HasProviderId(Dictionary<string, string> providerIds, string key) {
            return TryGetProviderId(providerIds, key, out _);
        }

        /// <summary>
        /// Get the AniDb ID from the set of providers provided.
        /// </summary>
        /// <param name="logger">Logger.</param>
        /// <param name="providers">Dictionary of providers.</param>
        /// <param name="episodeNumber">Episode number.</param>
        /// <param name="seasonNumber">Season number.</param>
        /// <returns></returns>
        public static async Task<(int? aniDbId, int? episodeOffset)> GetAniDbId(ILogger logger, Video video, int episodeNumber, int seasonNumber, AnimeListXml animeListXml) {
            int aniDbId;

            // ---------------------------------------------------------------------
            // FIX 1: if the season already carries its own AniDB ID, trust it.
            //
            // Metadata providers such as Shokofin assign a distinct AniDB series ID
            // to every season and number that season's episodes 1..N relative to
            // that AniDB series. In that situation the anime-list XML lookup below
            // is not merely redundant, it is actively harmful: the XML's season
            // numbers and episodeoffset values are expressed in TVDB terms, so
            // re-deriving the ID collapses sequels back onto the parent series and
            // applies an offset that does not belong to AniDB numbering.
            // ---------------------------------------------------------------------
            if (video is Episode episodeWithSeasonAniDbId &&
                TryGetProviderId(episodeWithSeasonAniDbId.Season?.ProviderIds, "Anidb", out var seasonAniDbIdRaw) &&
                int.TryParse(seasonAniDbIdRaw, out var seasonAniDbId)) {
                // -----------------------------------------------------------------
                // FIX 10: fix 1's premise is "one Jellyfin season == one AniDB series".
                // Shokofin's season merging breaks that premise: several AniDB series
                // are folded into a single season with continuous 1..N numbering, and
                // the season-level AniDB ID then describes only the FIRST of them.
                //
                // Shoko tags every episode with the series it really belongs to, so a
                // mismatch between the episode's "Shoko Series" and the season's is a
                // reliable, purely local signal that this season is merged.
                // -----------------------------------------------------------------
                bool haveSeasonShoko = TryGetProviderId(episodeWithSeasonAniDbId.Season?.ProviderIds, "Shoko Series", out var seasonShokoSeries);
                bool haveEpisodeShoko = TryGetProviderId(episodeWithSeasonAniDbId.ProviderIds, "Shoko Series", out var episodeShokoSeries);
                bool mergedSeason = haveSeasonShoko && haveEpisodeShoko &&
                                    !string.Equals(seasonShokoSeries, episodeShokoSeries, StringComparison.OrdinalIgnoreCase);

                if (!mergedSeason) {
                    logger.LogInformation($"(Anidb) Season {seasonNumber} carries its own AniDb ID ({seasonAniDbId}); using it directly with no episode offset");
                    return (seasonAniDbId, null);
                }

                logger.LogInformation($"(Anidb) Season {seasonNumber} is merged (season Shoko Series {seasonShokoSeries}, episode Shoko Series {episodeShokoSeries}); resolving the correct entry from the anime list XML");

                if (animeListXml?.Anime != null) {
                    var seasonRow = animeListXml.Anime.FirstOrDefault(a => a.Anidbid == seasonAniDbId.ToString());

                    // -------------------------------------------------------------
                    // FIX 11: a merged season can span more than one TVDB season.
                    //
                    // Shokofin merges AniDB entries by name similarity within a
                    // configurable airing window, which pays no attention to TVDB's
                    // season boundaries. Mushoku Tensei's two cours are both TVDB
                    // season 2, so deriving a single TVDB season from the season-level
                    // row works. Gintama's are TVDB seasons 9 and 10, so that approach
                    // resolves every episode back to the first part.
                    //
                    // Shoko does tell us which part an episode belongs to - the
                    // episode's "Shoko Series" tag - so walk the season's episodes to
                    // find (a) which merged part this is and (b) where that part starts
                    // in the merged numbering. Then step that many rows forward from the
                    // season's own row through the franchise in broadcast order.
                    //
                    // The offset is computed from the actual episode layout rather than
                    // taken from the XML, because the XML's episodeoffset is relative to
                    // a TVDB season while the merged season has its own numbering. Those
                    // coincide for Mushoku and differ for Gintama.
                    // -------------------------------------------------------------
                    if (seasonRow != null) {
                        var partResolution = ResolveMergedPart(logger, episodeWithSeasonAniDbId, episodeShokoSeries, seasonRow, animeListXml);
                        if (partResolution.aniDbId != null) {
                            logger.LogInformation($"(Anidb) Merged season resolved to AniDb ID {partResolution.aniDbId} with offset {(partResolution.episodeOffset.HasValue ? partResolution.episodeOffset.Value.ToString() : "<none>")} (by Shoko series ordering)");
                            return partResolution;
                        }
                    }

                    // Fall back to the single-TVDB-season assumption.
                    if (seasonRow != null && int.TryParse(seasonRow.Defaulttvdbseason, out int tvdbSeason)) {
                        var relatedRows = animeListXml.Anime.Where(a => a.Tvdbid == seasonRow.Tvdbid).ToList();
                        var resolved = GetAniDbByEpisodeOffset(logger, GetAbsoluteEpisodeNumber(episodeWithSeasonAniDbId), tvdbSeason, episodeNumber, relatedRows);
                        if (resolved.aniDbId != null) {
                            logger.LogInformation($"(Anidb) Merged season resolved to AniDb ID {resolved.aniDbId} with offset {(resolved.episodeOffset.HasValue ? resolved.episodeOffset.Value.ToString() : "<none>")} (tvdb season {tvdbSeason})");
                            return resolved;
                        }
                    } else if (seasonRow == null) {
                        logger.LogWarning($"(Anidb) AniDb ID {seasonAniDbId} has no usable anime list XML row; cannot resolve merged season");
                    }
                }

                logger.LogWarning($"(Anidb) Could not resolve merged season from the XML; falling back to the season AniDb ID ({seasonAniDbId}). Episode numbers past the first merged part will be wrong.");
                return (seasonAniDbId, null);
            }

            if (animeListXml == null) return (null, null);
            Dictionary<string, string> providers;
            if (video is Episode) {
                //Search for Anidb id at season level
                providers = (video as Episode).Season.ProviderIds.ContainsKey("Anidb") ? (video as Episode).Season.ProviderIds : (video as Episode).Series.ProviderIds;
            } else if (video is Movie) {
                providers = (video as Movie).ProviderIds;
            } else {
                return (null, null);
            }

            if (providers.ContainsKey("Anidb")) {
                logger.LogInformation("(Anidb) Anime already has AniDb ID; no need to look it up");
                if (!int.TryParse(providers["Anidb"], out aniDbId)) return (null, null);
                var foundAnime = animeListXml.Anime.Where(anime => int.TryParse(anime.Anidbid, out int xmlAniDbId) &&
                                                                   xmlAniDbId == aniDbId &&
                                                                   (
                                                                       (video as Episode).Season.ProviderIds.ContainsKey("Anidb") ||
                                                                       (int.TryParse(anime.Defaulttvdbseason, out int xmlSeason) &&
                                                                        xmlSeason == seasonNumber ||
                                                                        anime.Defaulttvdbseason == "a")
                                                                   )
                ).ToList();
                switch (foundAnime.Count()) {
                    case 1:
                        var related = animeListXml.Anime.Where(anime => anime.Tvdbid == foundAnime.First().Tvdbid).ToList();
                        if (video is Episode episode && episode.Series.Children.OfType<Season>().Count() > 1 && related.Count > 1) {
                            // contains more than 1 season, need to do a lookup
                            logger.LogInformation($"(Anidb) Anime {episode.Series.Name} found in anime XML file");
                            logger.LogInformation($"(Anidb) Looking up anime {episode.Series.Name} in the anime XML file by absolute episode number...");
                            var (aniDb, episodeOffset) = GetAniDbByEpisodeOffset(logger, GetAbsoluteEpisodeNumber(episode), seasonNumber, episodeNumber, related);
                            if (aniDb != null) {
                                logger.LogInformation($"(Anidb) Anime {episode.Series.Name} found in anime XML file, detected AniDB ID {aniDb}");
                                return (aniDb, episodeOffset);
                            } else {
                                logger.LogInformation($"(Anidb) Anime {episode.Series.Name} could not found in anime XML file; falling back to other metadata providers if available...");
                            }
                        } else {
                            if (video is Episode episodeWithMultipleSeasons && episodeWithMultipleSeasons.Season.IndexNumber > 1) {
                                // user doesnt have full series; have to do season lookup
                                logger.LogInformation($"(Anidb) Anime {episodeWithMultipleSeasons.Series.Name} found in anime XML file");
                                return SeasonLookup(logger, seasonNumber, episodeNumber, related);
                            } else {
                                logger.LogInformation($"(Anidb) Anime {video.Name} found in anime XML file");
                                // is movie / only has one season / no related; just return the only result
                                return int.TryParse(related.First().Anidbid, out aniDbId) ? (aniDbId, null) : (null, null);
                            }

                            logger.LogInformation($"(Anidb) Anime {(video is Episode episodeWithoutSeason ? episodeWithoutSeason.Name : video.Name)} found in anime XML file");
                            // is movie / only has one season / no related; just return the only result
                            return int.TryParse(foundAnime.First().Anidbid, out aniDbId) ? (aniDbId, null) : (null, null);
                        }

                        break;
                    case > 1:
                        // here
                        logger.LogWarning("(Anidb) More than one result found; possibly an issue with the XML. Falling back to other metadata providers if available...");
                        break;
                    case 0:
                        logger.LogWarning("(Anidb) Anime not found in anime list XML; falling back to other metadata providers if available...");
                        break;
                }
            }

            //Search for tvdb id at series level
            if (video is Episode) {
                providers = (video as Episode).Series.ProviderIds;
            }

            if (providers.ContainsKey("Tvdb")) {
                int tvDbId;
                if (!int.TryParse(providers["Tvdb"], out tvDbId)) return (null, null);
                var related = animeListXml.Anime.Where(anime => int.TryParse(anime.Tvdbid, out int xmlTvDbId) && xmlTvDbId == tvDbId).ToList();

                if (!related.Any()) {
                    logger.LogWarning("(Tvdb) Anime not found in anime list XML; querying the appropriate providers API");
                    return (null, null);
                }

                logger.LogInformation("(Tvdb) Anime reference found in anime list XML");

                var first = related.First();
                if (related.Count() == 1) {
                    return (
                        int.TryParse(first.Anidbid, out aniDbId) ? aniDbId : null,
                        int.TryParse(first.Episodeoffset, out var episodeOffset) ? episodeOffset : null
                    );
                }

                if (video is Episode episode && episode.Series.Children.OfType<Season>().Count() > 1) {
                    var (aniDb, episodeOffset) = GetAniDbByEpisodeOffset(logger, GetAbsoluteEpisodeNumber(episode), seasonNumber, episodeNumber, related);
                    if (aniDb != null) {
                        logger.LogInformation($"(Tvdb) Anime {episode.Series.Name} found in anime XML file, detected AniDB ID {aniDb}");
                        return (aniDb.Value, episodeOffset);
                    } else {
                        logger.LogInformation($"(Tvdb) Anime {episode.Series.Name} could not found in anime XML file; falling back to other metadata providers if available...");
                    }
                } else {
                    if (video is Episode episodeWithMultipleSeasons && episodeWithMultipleSeasons.Season.IndexNumber > 1) {
                        // user doesnt have full series; have to do season lookup
                        logger.LogInformation($"(Tvdb) Anime {episodeWithMultipleSeasons.Name} found in anime XML file");
                        return SeasonLookup(logger, seasonNumber, episodeNumber, related);
                    } else {
                        logger.LogInformation($"(Tvdb) Anime {video.Name} found in anime XML file");
                        // is movie / only has one season / no related; just return the only result
                        return (
                            int.TryParse(first.Anidbid, out aniDbId) ? aniDbId : null,
                            int.TryParse(first.Episodeoffset, out var episodeOffset) ? episodeOffset : null
                        );
                    }
                }
            }

            return (null, null);
        }

        internal static (int? aniDbId, int? episodeOffset) GetAniDbByEpisodeOffset(ILogger logger, int? absoluteEpisodeNumber, int seasonNumber, int episodeNumber, List<AnimeListAnime> related) {
            if (absoluteEpisodeNumber != null) {
                // -----------------------------------------------------------------
                // FIX 2: the original predicate compared only Start/End and ignored
                // the season entirely. Start and End are plain ints, so they default
                // to 0 when the attribute is absent, and FirstOrDefault walks the
                // list in raw document order. The net effect was that the first
                // entry owning a wide mapping table always won - for a franchise
                // like Bleach that is the original 366-episode series - while
                // sequel entries, which carry no <mapping-list> at all, could never
                // be selected. Require a genuine range in the correct TVDB season,
                // and return that mapping's offset rather than null.
                //
                // absoluteEpisodeNumber is already cumulative on the same scale as
                // start/end - e.g. Bleach's season 2 mapping is start=21 end=41, where
                // 21 is season 1's 20 episodes plus one - so the mapping's offset must
                // NOT be subtracted before comparing. (SeasonLookup does subtract it,
                // but that function is fed the in-season episode number, not this one.)
                // -----------------------------------------------------------------
                AnimeListAnime foundMapping = null;
                Mapping foundRange = null;
                foreach (var animeListAnime in related) {
                    var match = animeListAnime.MappingList?.Mapping?.FirstOrDefault(mapping =>
                        mapping.Tvdbseason == seasonNumber &&
                        mapping.Start > 0 && mapping.End > 0 &&
                        mapping.Start <= absoluteEpisodeNumber &&
                        mapping.End >= absoluteEpisodeNumber);
                    if (match != null) {
                        foundMapping = animeListAnime;
                        foundRange = match;
                        break;
                    }
                }

                if (foundMapping != null) {
                    logger.LogInformation($"(AniDb) Absolute episode {absoluteEpisodeNumber} matched AniDB ID {foundMapping.Anidbid} (tvdbseason {seasonNumber}, range {foundRange.Start}-{foundRange.End}, offset {foundRange.Offset})");
                    return (int.TryParse(foundMapping.Anidbid, out var aniDbId) ? aniDbId : null, foundRange.Offset);
                } else {
                    logger.LogWarning($"(AniDb) Could not lookup using absolute episode number {absoluteEpisodeNumber} (reason: no mapping covers that episode within tvdbseason {seasonNumber})");
                    return SeasonLookup(logger, seasonNumber, episodeNumber, related);
                }
            } else {
                logger.LogWarning("(AniDb) Could not lookup using absolute episode number (reason: absolute episode number is null)");
                return SeasonLookup(logger, seasonNumber, episodeNumber, related);
            }
        }

        internal static (int? aniDbId, int? episodeOffset) SeasonLookup(ILogger logger, int seasonNumber, int episodeNumber, List<AnimeListAnime> related) {
            logger.LogInformation("Looking up AniDB by season offset");

            // First, consider mappings from absolute-numbered seasons. If there
            // are no matches, compare episode number against episodeoffset
            // attribute for each matching season number. Note that order is
            // important in this case: we do not want to match previous season
            // that would have lower episode offset.
            var foundMapping =
                related
                    .Where(animeListAnime => animeListAnime.Defaulttvdbseason == "a")
                    .FirstOrDefault(animeListAnime =>
                        animeListAnime.MappingList.Mapping.FirstOrDefault(mapping => mapping.Tvdbseason == seasonNumber
                        ) != null
                    )
                ?? related
                    .Where(animeListAnime => animeListAnime.Defaulttvdbseason == seasonNumber.ToString())
                    .OrderBy(animeListAnime => int.TryParse(animeListAnime.Episodeoffset, out var n) ? n : 0)
                    .LastOrDefault(animeListAnime =>
                        animeListAnime.Episodeoffset == null
                        || !int.TryParse(animeListAnime.Episodeoffset, out var episodeOffset)
                        || episodeOffset < episodeNumber
                    );

            var tvdbSeasonMappings = foundMapping?.MappingList?.Mapping
                ?.Where(m => m.Tvdbseason == seasonNumber)
                .ToList();

            var specificMapping = tvdbSeasonMappings
                ?.FirstOrDefault(m =>
                    m.Start > 0 && m.End > 0 &&
                    episodeNumber - m.Offset >= m.Start &&
                    episodeNumber - m.Offset <= m.End)
                ?? tvdbSeasonMappings?.FirstOrDefault();

            var resolvedOffset = specificMapping?.Offset ?? (int.TryParse(foundMapping?.Episodeoffset, out var episodeOffset)
                ? episodeOffset
                : (int?)null);

            // FIX 3: make the outcome of this branch visible. NOTE: seasonNumber here
            // is Jellyfin's season index, while defaulttvdbseason/tvdbseason in the XML
            // are TVDB season numbers. If your metadata provider does not number
            // seasons the way TVDB does, this lookup will silently pick the wrong
            // entry - which is exactly why FIX 1 short-circuits it where possible.
            logger.LogInformation($"(AniDb) Season lookup for tvdb season {seasonNumber}, episode {episodeNumber} resolved to AniDB ID {foundMapping?.Anidbid ?? "<none>"} ({foundMapping?.Name ?? "<none>"}) with offset {(resolvedOffset.HasValue ? resolvedOffset.Value.ToString() : "<none>")}");

            return (
                int.TryParse(foundMapping?.Anidbid, out var aniDbId) ? aniDbId : null,
                resolvedOffset
            );
        }

        /// <summary>
        /// FIX 11 helper. Works out which merged part an episode belongs to by walking the
        /// season's episodes and grouping them by their "Shoko Series" tag, then steps that
        /// many rows forward from the season's own row through the franchise in broadcast
        /// order (TVDB season, then episode offset).
        ///
        /// Returns the resolved AniDB ID and an offset derived from the episode layout, not
        /// from the XML - the XML's episodeoffset is relative to a TVDB season, while a
        /// merged season has its own continuous numbering.
        /// </summary>
        internal static (int? aniDbId, int? episodeOffset) ResolveMergedPart(ILogger logger, Episode episode, string episodeShokoSeries, AnimeListAnime seasonRow, AnimeListXml animeListXml) {
            if (episode.Season == null || string.IsNullOrEmpty(episodeShokoSeries)) return (null, null);

            // Season episodes in broadcast order, each tagged with the Shoko series it
            // really belongs to. Episodes without an index or tag cannot be placed.
            var seasonEpisodes = episode.Season.Children
                .OfType<Episode>()
                .Where(e => e.IndexNumber != null)
                .OrderBy(e => e.IndexNumber.Value)
                .Select(e => new {
                    Index = e.IndexNumber.Value,
                    Shoko = TryGetProviderId(e.ProviderIds, "Shoko Series", out var s) ? s : null
                })
                .Where(e => e.Shoko != null)
                .ToList();

            if (seasonEpisodes.Count == 0) return (null, null);

            // Distinct parts, in the order they first appear.
            var partOrder = new List<string>();
            foreach (var e in seasonEpisodes) {
                if (!partOrder.Contains(e.Shoko, StringComparer.OrdinalIgnoreCase)) partOrder.Add(e.Shoko);
            }

            int partIndex = partOrder.FindIndex(s => string.Equals(s, episodeShokoSeries, StringComparison.OrdinalIgnoreCase));
            if (partIndex < 0) return (null, null);

            // Where this part starts in the merged numbering.
            var firstOfPart = seasonEpisodes.FirstOrDefault(e => string.Equals(e.Shoko, episodeShokoSeries, StringComparison.OrdinalIgnoreCase));
            if (firstOfPart == null) return (null, null);
            int localOffset = firstOfPart.Index - 1;

            // Franchise rows that can be merged parts: a real TVDB season, specials
            // excluded, ordered the way the show was broadcast.
            var parts = animeListXml.Anime
                .Where(a => a.Tvdbid == seasonRow.Tvdbid
                            && int.TryParse(a.Defaulttvdbseason, out int s) && s > 0)
                .OrderBy(a => int.Parse(a.Defaulttvdbseason))
                .ThenBy(a => int.TryParse(a.Episodeoffset, out int o) ? o : 0)
                .ToList();

            int seasonPos = parts.FindIndex(a => a.Anidbid == seasonRow.Anidbid);
            if (seasonPos < 0) return (null, null);

            int targetPos = seasonPos + partIndex;
            if (targetPos >= parts.Count) {
                logger.LogWarning($"(Anidb) Merged season has more parts ({partOrder.Count}) than the anime list XML lists after AniDb ID {seasonRow.Anidbid}; cannot resolve part {partIndex + 1}");
                return (null, null);
            }

            var target = parts[targetPos];
            logger.LogInformation($"(Anidb) Episode belongs to merged part {partIndex + 1} of {partOrder.Count} (Shoko Series {episodeShokoSeries}, starts at episode {firstOfPart.Index}); matched anime list row {target.Anidbid} ({target.Name})");

            return (int.TryParse(target.Anidbid, out int resolved) ? resolved : null,
                    localOffset > 0 ? localOffset : (int?)null);
        }

        private static int? GetAbsoluteEpisodeNumber(Episode episode) {
            var previousSeasons = episode.Series.Children.OfType<Season>().Where(item => item.IndexNumber < episode.Season.IndexNumber).ToList();
            int previousSeasonIndexNumber = -1;
            foreach (int indexNumber in previousSeasons.Where(item => item.IndexNumber != null).Select(item => item.IndexNumber).OrderBy(item => item.Value)) {
                if (previousSeasonIndexNumber == -1) {
                    previousSeasonIndexNumber = indexNumber;
                } else {
                    if (previousSeasonIndexNumber != indexNumber - 1) {
                        // series does not contain all seasons, cannot get absolute episode number
                        return null;
                    }

                    previousSeasonIndexNumber = indexNumber;
                }
            }

            var previousSeasonsEpisodeCount = previousSeasons.SelectMany(item => item.Children.OfType<Episode>()).Count();
            // this is presuming the user has all episodes
            return previousSeasonsEpisodeCount + episode.IndexNumber;
        }

        /// <summary>
        /// Get the season number of an AniDb entry.
        /// </summary>
        /// <param name="aniDbId"></param>
        /// <returns>Season.</returns>
        public static AnimeListAnime GetAniDbSeason(int aniDbId, AnimeListXml animeListXml) {
            if (animeListXml == null) return null;

            return animeListXml.Anime.FirstOrDefault(anime => int.TryParse(anime.Anidbid, out int xmlAniDbId) && xmlAniDbId == aniDbId);
        }


        public static IEnumerable<AnimeListAnime> ListAllSeasonOfAniDbSeries(int aniDbId, AnimeListXml animeListXml) {
            if (animeListXml == null) return null;

            AnimeListAnime foundXmlAnime = animeListXml.Anime.FirstOrDefault(anime => int.TryParse(anime.Anidbid, out int xmlAniDbId) && xmlAniDbId == aniDbId);
            if (foundXmlAnime == null) return null;

            return animeListXml.Anime.Where(anime => anime.Tvdbid == foundXmlAnime.Tvdbid);
        }

        /// <summary>
        /// Get the contents of the anime list file.
        /// </summary>
        /// <param name="logger">Logger.</param>
        /// <returns></returns>
        public static async Task<AnimeListXml> GetAnimeListFileContents(ILogger logger, ILoggerFactory loggerFactory, IHttpClientFactory httpClientFactory, IApplicationPaths applicationPaths) {
            UpdateAnimeList updateAnimeList = new UpdateAnimeList(httpClientFactory, loggerFactory, applicationPaths);

            try {
                FileInfo animeListXml = new FileInfo(updateAnimeList.Path);
                if (!animeListXml.Exists) {
                    logger.LogInformation("Anime list XML not found; attempting to download...");
                    if (await updateAnimeList.Update()) {
                        logger.LogInformation("Anime list XML downloaded");
                    }
                }

                using (var stream = File.OpenRead(updateAnimeList.Path)) {
                    var serializer = new XmlSerializer(typeof(AnimeListXml));
                    return (AnimeListXml)serializer.Deserialize(stream);
                }
            } catch (Exception e) {
                logger.LogError($"Could not deserialize anime list XML; {e.Message}. Try forcibly redownloading the XML file");
                return null;
            }
        }

        [XmlRoot(ElementName = "anime")]
        public class AnimeListAnime {
            [XmlElement(ElementName = "name")] public string Name { get; set; }

            [XmlElement(ElementName = "mapping-list")]
            public MappingList MappingList { get; set; }

            [XmlAttribute(AttributeName = "anidbid")]
            public string Anidbid { get; set; }

            [XmlAttribute(AttributeName = "tvdbid")]
            public string Tvdbid { get; set; }

            [XmlAttribute(AttributeName = "defaulttvdbseason")]
            public string Defaulttvdbseason { get; set; }

            [XmlAttribute(AttributeName = "episodeoffset")]
            public string Episodeoffset { get; set; }

            [XmlAttribute(AttributeName = "tmdbid")]
            public string Tmdbid { get; set; }
        }

        [XmlRoot(ElementName = "mapping-list")]
        public class MappingList {
            [XmlElement(ElementName = "mapping")] public List<Mapping> Mapping { get; set; }
        }

        [XmlRoot(ElementName = "mapping")]
        public class Mapping {
            [XmlAttribute(AttributeName = "anidbseason")]
            public int Anidbseason { get; set; }

            [XmlAttribute(AttributeName = "tvdbseason")]
            public int Tvdbseason { get; set; }

            [XmlText] public string Text { get; set; }

            [XmlAttribute(AttributeName = "start")]
            public int Start { get; set; }

            [XmlAttribute(AttributeName = "end")] public int End { get; set; }

            [XmlAttribute(AttributeName = "offset")]
            public int Offset { get; set; }
        }

        [XmlRoot(ElementName = "anime-list")]
        public class AnimeListXml {
            [XmlElement(ElementName = "anime")] public List<AnimeListAnime> Anime { get; set; }
        }
    }
}