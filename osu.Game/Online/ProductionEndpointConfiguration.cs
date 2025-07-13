// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace osu.Game.Online
{
    public class ProductionEndpointConfiguration : EndpointConfiguration
    {
        public ProductionEndpointConfiguration()
        {
            WebsiteUrl = APIUrl = @"https://web-a.mamesosutest.com";
            APIClientSecret = @"FGc9GAtyHzeQDshWP5Ah7dega8hJACAJpQtw6OXk";
            APIClientID = "5";
            SpectatorUrl = "https://web-a.mamesosutest.com/spectator";
            MultiplayerUrl = "https://web-a.mamesosutest.com/multiplayer";
            MetadataUrl = "https://web-a.mamesosutest.com/metadata";
            BeatmapSubmissionServiceUrl = "https://web-a.mamesosutest.com/beatmap-submission";
        }
    }
}
