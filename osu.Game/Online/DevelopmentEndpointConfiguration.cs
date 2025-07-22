// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace osu.Game.Online
{
    public class DevelopmentEndpointConfiguration : EndpointConfiguration
    {
        public DevelopmentEndpointConfiguration()
        {
            WebsiteUrl = APIUrl = @"https://lazer.mamesosutest.com";
            APIClientSecret = @"3LP2mhUrV89xxzD1YKNndXHEhWWCRLPNKioZ9ymT";
            APIClientID = "5";
            const string webSocketUrl = @"https://ws-lazer.mamesosutest.com";
            SpectatorUrl = $@"{webSocketUrl}/signalr/spectator";
            MultiplayerUrl = $@"{webSocketUrl}/signalr/multiplayer";
            MetadataUrl = $@"{webSocketUrl}/signalr/metadata";
            BeatmapSubmissionServiceUrl = $@"{APIUrl}/beatmap-submission";
        }
    }
}
