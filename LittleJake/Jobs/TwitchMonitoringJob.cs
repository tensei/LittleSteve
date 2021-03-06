﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using FluentScheduler;
using Humanizer;
using LittleJake.Data;
using LittleJake.Data.Entities;
using LittleJake.Data.Migrations;
using LittleJake.Extensions;
using LittleJake.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;
using TwitchLib.Api.Models.v5.Streams;
using TimeUnit = Humanizer.Localisation.TimeUnit;

namespace LittleJake.Jobs
{
    public class TwitchMonitoringJob : IJob
    {
        private readonly JakeBotContext _botContext;
        private readonly long _channelId;
        private readonly DiscordSocketClient _client;
        private readonly TwitchService _twitchService;
        private readonly TimeSpan _waitEndTime = TimeSpan.FromMinutes(3);

        public TwitchMonitoringJob(long channelId, TwitchService twitchService, JakeBotContext botContext,
            DiscordSocketClient client)
        {
            _channelId = channelId;
            _twitchService = twitchService;
            _botContext = botContext;
            _client = client;
        }

        public void Execute()
        {
            using (_botContext)
            {
                var streamer = _botContext.TwitchStreamers.Include(x => x.TwitchAlertSubscriptions)
                    .Include(x => x.Games)
                    .FirstOrDefault(x => x.Id == _channelId);
                if (streamer is null)
                {
                    return;
                }

                var isStreaming = _twitchService.IsUserStreamingAsync(_channelId).AsSync(false);

                //after the streamer goes offline the twitch api will sometimes says the stream is online
                //we wait for 3 minutes after the last stream to make sure the streamer in actually on or offline
                //this is arbitrary
                if (DateTimeOffset.UtcNow - streamer.StreamEndTime < _waitEndTime)
                {
                    return;
                }

                //stream has ended and we are waiting for startup again
                if (!isStreaming && streamer.StreamLength >= TimeSpan.Zero)
                {
                    return;
                }

                var missingChannels = new List<TwitchAlertSubscription>();
                // stream started
                if (isStreaming && streamer.StreamLength >= TimeSpan.Zero)
                {
                    var stream = _twitchService.GetStreamAsync(_channelId).AsSync(false);
                    Log.Debug($"{streamer.Name} started streaming {stream.CreatedAt.ToUniversalTime():g}");

                    streamer.SteamStartTime = stream.CreatedAt.ToUniversalTime();
                    streamer.Games.Add(new Data.Entities.Game()
                    {
                        StartTime = DateTime.UtcNow,
                        Name = string.IsNullOrWhiteSpace(stream.Game) ? "No Game" : stream.Game
                    });
                    foreach (var subscription in streamer.TwitchAlertSubscriptions)
                    {
                        var channel = _client.GetChannel((ulong)subscription.DiscordChannelId) as ITextChannel;
                        if (channel is null && !missingChannels.Contains(subscription))
                        {
                            Log.Information($"{streamer.Name} removing channel {subscription.DiscordChannelId}");
                            missingChannels.Add(subscription);
                            continue;
                        }
                        var messageId = CreateTwitchMessage(streamer, stream, subscription, channel).AsSync(false);
                        subscription.MessageId = messageId;
                    }

                    _botContext.SaveChanges();
                    return;
                }

                // stream has started and we update the message embed
                if (isStreaming && streamer.StreamLength <= TimeSpan.Zero)
                {
                    var stream = _twitchService.GetStreamAsync(_channelId).AsSync(false);
                    if (stream is null)
                    {
                        return;
                    }

                    var oldGame = streamer.Games.LastOrDefault();
                    var currentGame = string.IsNullOrWhiteSpace(stream.Game) ? "No Game" : stream.Game;
                    if (oldGame != null && oldGame.Name != currentGame)
                    {
                        oldGame.EndTime = DateTime.UtcNow;
                        streamer.Games.Add(new Data.Entities.Game()
                        {
                            StartTime = DateTime.UtcNow,
                            Name = currentGame
                        });
                    }


                    foreach (var subscription in streamer.TwitchAlertSubscriptions)
                    {

                        var channel = _client.GetChannel((ulong)subscription.DiscordChannelId) as ITextChannel;

                        if (channel is null && !missingChannels.Contains(subscription))
                        {
                            Log.Information($"{streamer.Name} removing channel {subscription.DiscordChannelId}");
                            missingChannels.Add(subscription);
                            continue;
                        }
                        if (subscription.MessageId == 0)
                        {
                            var messageId = CreateTwitchMessage(streamer, stream, subscription, channel).AsSync(false);
                            subscription.MessageId = messageId;
                        }
                        var message =
                            channel.GetMessageAsync((ulong)subscription.MessageId).AsSync(false) as IUserMessage;
                        if (message is null)
                        {
                            Log.Information($"Message was not found in channel {channel.Name}, reposting it");
                            subscription.MessageId = CreateTwitchMessage(streamer, stream, subscription, channel).AsSync(false);
                            continue;
                        }

                        message.ModifyAsync(x => x.Embed = CreateTwitchEmbed(streamer, stream)).AsSync(false);
                    }
                }

                //stream ended
                if (!isStreaming && streamer.StreamLength <= TimeSpan.Zero)
                {
                    streamer.Games.Last().EndTime = DateTimeOffset.UtcNow - _waitEndTime;
                    streamer.StreamEndTime = DateTimeOffset.UtcNow - _waitEndTime;
                    var startTime = $"{streamer.SteamStartTime:g}";
                    var endTime = $"{streamer.StreamEndTime:g}";
                    var tmz = "UTC";
                    // HACK jakenbakelive convert to tokyo time
                    if (streamer.Id == (long)11249217)
                    {
                        var tkyZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
                        var culture = CultureInfo.CreateSpecificCulture("ja-JP");
                        startTime = (TimeZoneInfo.ConvertTimeFromUtc(streamer.SteamStartTime.UtcDateTime, tkyZone)).ToString("g", culture);
                        endTime = (TimeZoneInfo.ConvertTimeFromUtc(streamer.StreamEndTime.UtcDateTime, tkyZone)).ToString("g", culture);
                        tmz = "JST";
                    }

                    var user = _twitchService.GetUserByIdAsync(streamer.Id).AsSync(false);

                    var description = new StringBuilder();
                    description.AppendLine($"**Started at:** {startTime} {tmz}");
                    description.AppendLine($"__**Ended at:** {endTime} {tmz}__");

                    description.AppendLine(
                        $"**Total Time:** {streamer.StreamLength.Humanize(2, maxUnit: TimeUnit.Hour, minUnit: TimeUnit.Minute, collectionSeparator: " ")}");


                    var embedBuilder = new EmbedBuilder()
                        .WithAuthor($"{streamer.Name} was live", url: $"https://twitch.tv/{streamer.Name}")
                        .WithThumbnailUrl(user.Logo)
                        .WithDescription(description.ToString());
                    // if (streamer.Games.Count > 1)
                    // {
                    //     embedBuilder.AddField("Games Played", string.Join("\n", streamer.Games.Where(x => x.StartTime >= streamer.SteamStartTime && x.EndTime <= streamer.StreamEndTime).Select(x => $"**{x.Name}:** Played for {x.PlayLength.Humanize(2, maxUnit: TimeUnit.Hour, minUnit: TimeUnit.Minute, collectionSeparator: " ")}")));
                    // }
                    var embed = embedBuilder.Build();

                    foreach (var subscription in streamer.TwitchAlertSubscriptions)
                    {
                        var channel = _client.GetChannel((ulong)subscription.DiscordChannelId) as ITextChannel;
                        if (channel is null && !missingChannels.Contains(subscription))
                        {
                            Log.Information($"{streamer.Name} removing channel {subscription.DiscordChannelId}");
                            missingChannels.Add(subscription);
                            continue;
                        }
                        var message =
                            channel.GetMessageAsync((ulong)subscription.MessageId).AsSync(false) as IUserMessage;
                        if (message is null)
                        {
                            Log.Information($"Message was not found in channel {channel.Name}. Posting new message!");
                            subscription.MessageId = (long)channel.SendMessageAsync(string.Empty, embed: embed).AsSync(false).Id;
                            continue;
                        }

                        message.ModifyAsync(x => { x.Embed = embed; x.Content = string.Empty; }).AsSync(false);
                    }

                }
                missingChannels.ForEach(m => streamer.TwitchAlertSubscriptions.Remove(m));
                _botContext.SaveChanges();
            }
        }

        private async Task<long> CreateTwitchMessage(TwitchStreamer streamer, Stream stream,
            TwitchAlertSubscription subscription, ITextChannel channel)
        {
            var message = await channel.SendMessageAsync($"@everyone {streamer.Name} is live!", embed: CreateTwitchEmbed(streamer, stream));
            return (long)message.Id;
        }

        private Embed CreateTwitchEmbed(TwitchStreamer streamer, Stream stream)
        {
            var timeLive = DateTimeOffset.UtcNow - streamer.SteamStartTime;

            return new EmbedBuilder()
                .WithAuthor($"{streamer.Name} is live", url: $"https://twitch.tv/{streamer.Name}")
                .WithTitle($"{stream.Channel.Status}")
                .WithUrl($"https://twitch.tv/{streamer.Name}")
                .WithThumbnailUrl(stream.Channel.Logo)
                .AddField("Playing", string.IsNullOrWhiteSpace(stream.Game) ? "No Game" : stream.Game, true)
                .AddField("Viewers", stream.Viewers, true)
                //we add the timeseconds so the image wont be used from the cache
                .WithImageUrl(
                    $"{stream.Preview.Template.Replace("{width}", "1920").Replace("{height}", "1080")}?{DateTimeOffset.Now.ToUnixTimeSeconds()}")
                .WithFooter($"Live for {timeLive.Humanize(2, maxUnit: TimeUnit.Hour, minUnit: TimeUnit.Second)}")
                .Build();
        }
    }
}