﻿using Discord;
using Discord.Commands;
using Discord.WebSocket;
using NadekoBot.Attributes;
using NadekoBot.Services;
using NadekoBot.Services.Database;
using NadekoBot.Services.Database.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NadekoBot.Modules.Administration
{
    public partial class Administration
    {
        [Group]
        public class RepeatCommands
        {
            public ConcurrentDictionary<ulong, RepeatRunner> repeaters;

            public class RepeatRunner
            {
                private CancellationTokenSource source { get; set; }
                private CancellationToken token { get; set; }
                public Repeater Repeater { get; }
                public ITextChannel Channel { get; }

                public RepeatRunner(Repeater repeater, ITextChannel channel = null)
                {
                    this.Repeater = repeater;
                    this.Channel = channel ?? NadekoBot.Client.GetGuild(repeater.GuildId)?.GetTextChannel(repeater.ChannelId);
                    if (Channel == null)
                        return;
                    Task.Run(Run);
                }


                private async Task Run()
                {
                    source = new CancellationTokenSource();
                    token = source.Token;
                    try
                    {
                        while (!token.IsCancellationRequested)
                        {
                            await Task.Delay(Repeater.Interval, token).ConfigureAwait(false);
                            await Channel.SendMessageAsync("🔄 " + Repeater.Message).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) { }
                }

                public void Reset()
                {
                    source.Cancel();
                    var t = Task.Run(Run);
                }

                public void Stop()
                {
                    source.Cancel();
                }
            }

            public RepeatCommands()
            {
                using (var uow = DbHandler.UnitOfWork())
                {
                    repeaters = new ConcurrentDictionary<ulong, RepeatRunner>(uow.Repeaters.GetAll().Select(r => new RepeatRunner(r)).Where(r => r != null).ToDictionary(r => r.Repeater.ChannelId));
                }
            }

            [LocalizedCommand, LocalizedDescription, LocalizedSummary]
            [RequireContext(ContextType.Guild)]
            [RequirePermission(GuildPermission.ManageMessages)]
            public async Task RepeatInvoke(IUserMessage imsg)
            {
                var channel = (ITextChannel)imsg.Channel;

                RepeatRunner rep;
                if (!repeaters.TryGetValue(channel.Id, out rep))
                {
                    await channel.SendMessageAsync("`No repeating message found on this server.`").ConfigureAwait(false);
                    return;
                }
                rep.Reset();
                await channel.SendMessageAsync("🔄 " + rep.Repeater.Message).ConfigureAwait(false);
            }

            [LocalizedCommand, LocalizedDescription, LocalizedSummary]
            [RequireContext(ContextType.Guild)]
            public async Task Repeat(IUserMessage imsg)
            {
                var channel = (ITextChannel)imsg.Channel;
                RepeatRunner rep;
                if (repeaters.TryRemove(channel.Id, out rep))
                {
                    using (var uow = DbHandler.UnitOfWork())
                    {
                        uow.Repeaters.Remove(rep.Repeater);
                        await uow.CompleteAsync();
                    }
                    rep.Stop();
                    await channel.SendMessageAsync("`Stopped repeating a message.`").ConfigureAwait(false);
                }
                else
                    await channel.SendMessageAsync("`No message is repeating.`").ConfigureAwait(false);
            }

            [LocalizedCommand, LocalizedDescription, LocalizedSummary]
            [RequireContext(ContextType.Guild)]
            public async Task Repeat(IUserMessage imsg, int minutes, [Remainder] string message)
            {
                var channel = (ITextChannel)imsg.Channel;

                if (minutes < 1 || minutes > 1500)
                    return;

                if (string.IsNullOrWhiteSpace(message))
                    return;

                RepeatRunner rep;

                rep = repeaters.AddOrUpdate(channel.Id, (cid) =>
                {
                    using (var uow = DbHandler.UnitOfWork())
                    {
                        var localRep = new Repeater
                        {
                            ChannelId = channel.Id,
                            GuildId = channel.Guild.Id,
                            Interval = TimeSpan.FromMinutes(minutes),
                            Message = message,
                        };
                        uow.Repeaters.Add(localRep);
                        uow.Complete();
                        return new RepeatRunner(localRep, channel);
                    }
                }, (cid, old) =>
                {
                    using (var uow = DbHandler.UnitOfWork())
                    {
                        old.Repeater.Message = message;
                        old.Repeater.Interval = TimeSpan.FromMinutes(minutes);
                        uow.Repeaters.Update(old.Repeater);
                        uow.Complete();
                    }
                    old.Reset();
                    return old;
                });

                await channel.SendMessageAsync($"Repeating \"{rep.Repeater.Message}\" every {rep.Repeater.Interval} minutes").ConfigureAwait(false);
            }
        }
    }
}