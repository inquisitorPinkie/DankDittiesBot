﻿using DankDitties.Audio;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DankDitties
{
    public class VoiceAssistantWorker : Worker
    {
        private readonly VoiceChannelWorker _voiceChannelWorker;
        private readonly MetadataManager _metadataManager;
        private readonly WitAiClient _witAiClient;
        private readonly SocketGuildUser _user;

        public VoiceAssistantWorker(SocketGuildUser user, VoiceChannelWorker voiceChannelWorker, MetadataManager metadataManager, WitAiClient witAiClient)
        {
            _voiceChannelWorker = voiceChannelWorker;
            _metadataManager = metadataManager;
            _witAiClient = witAiClient;
            _user = user;
        }

        protected override async Task DoWorkAsync(CancellationToken cancellationToken)
        {
            Console.WriteLine("Starting voice assistant runner for " + _user.Username);
            try
            {
                var entryDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                using var porcupine = new Porcupine(
                    Path.Join(entryDirectory, "porcupine_params.pv"),
                    new string[] {
                        Path.Join(entryDirectory, "picovoice_windows.ppn"),
                        Path.Join(entryDirectory, "porcupine_windows.ppn"),
                        Path.Join(entryDirectory, "bumblebee_windows.ppn")
                    },
                    new float[] { 0.5f, 0.5f, 0.5f }
                );

                var userStream = _user.AudioStream;
                if (userStream == null)
                    return;

                var discordFrameLength = 1920;
                var discordBufferPos = 0;
                var discordBufferLength = 0;
                var discordBuffer = new short[discordFrameLength * 2];
                var readBuffer = new byte[discordFrameLength * 2];

                var picoFrameLength = porcupine.FrameLength();
                var picoSampleRate = porcupine.SampleRate();
                var picoBuffer = new short[picoFrameLength];

                while (!cancellationToken.IsCancellationRequested)
                {
                    Console.WriteLine("Waiting for wake word");
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        // Ensure we have enough samples to send to porcupine
                        while (discordBufferLength < picoFrameLength)
                        {
                            if (discordBufferPos > 0)
                            {
                                for (int i = 0; i < discordBufferLength; i++)
                                {
                                    discordBuffer[i] = discordBuffer[i + discordBufferPos];
                                }
                                discordBufferPos = 0;
                            }

                            var byteCount = await userStream.ReadAsync(readBuffer, 0, readBuffer.Length, cancellationToken);

                            // take one sample, skip 6, because discord samples at 48000 while pico samples at 16000.
                            // Additionally, in the discord stream, samples alternate between left and right channels,
                            // but pico expects mono audio
                            for (int i = 0; i < byteCount; i += 12)
                            {
                                short b1 = (short)((readBuffer[i + 1] & 0xff) << 8);
                                short b2 = (short)(readBuffer[i] & 0xff);

                                discordBuffer[discordBufferPos + discordBufferLength] = (short)(b1 | b2);
                                discordBufferLength++;
                            }
                        }

                        // Take 1000 bytes from the buffer
                        for (int i = 0; i < picoFrameLength; i++)
                        {
                            picoBuffer[i] = (discordBuffer[discordBufferPos + i]);
                        }
                        discordBufferPos += picoFrameLength;
                        discordBufferLength -= picoFrameLength;

                        var status = porcupine.Process(picoBuffer);
                        if (status != -1)
                        {
                            break;
                        }
                    }

                    Console.WriteLine("Wake word detected");

                    try
                    {
                        var data = await _witAiClient.ParseAudioStream(userStream, cancellationToken);
                        if (data != null)
                        {
                            var text = data.Text?.Trim();
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                Console.WriteLine(_user.Username + ": " + text);
                                var playSongIntent = data.Intents.FirstOrDefault(i => i.Name == "play_song");

                                if (text.ToLower().StartsWith("i'm "))
                                {
                                    _voiceChannelWorker.Say("Hello " + text.Substring("i'm ".Length) + ", I'm Dank Ditties bot.");
                                }
                                else if (text.ToLower().StartsWith("play "))
                                {
                                    var searchString = text.Substring("play ".Length);

                                    if (searchString == "next")
                                    {
                                        _voiceChannelWorker?.TrySkip();
                                    }
                                    else
                                    {
                                        var matches = from post in _metadataManager.Posts
                                                      where post.IsReady
                                                      let relevance = FuzzySharp.Fuzz.Ratio(post.Title, searchString)
                                                      select (post, relevance);
                                        var topMatch = matches.OrderByDescending(m => m.relevance);
                                        Console.WriteLine("matches: \n" + string.Join("\n", topMatch.Take(3).Select(m => $"{m.post.Title}: {m.relevance}")));
                                        Console.WriteLine();
                                        var closestMatch = topMatch.FirstOrDefault().post;
                                        if (closestMatch != null)
                                        {
                                            _voiceChannelWorker.EnqueueSong(closestMatch.Id);
                                            _voiceChannelWorker.Say("I have added your song, " + closestMatch.Title + " to the queue");
                                            Console.WriteLine("Added " + closestMatch.Title + " to queue");
                                        }
                                    }
                                }
                                else if (text.ToLower() == "what song is this")
                                {
                                    _voiceChannelWorker.Say("I am currently playing " + _voiceChannelWorker?.CurrentSong.Title);
                                }
                                else
                                {
                                    _voiceChannelWorker.Say("I'm sorry, I didn't understand that!");
                                }
                            }
                            else
                            {
                                _voiceChannelWorker.Say("I'm sorry, I didn't understand that!");
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                    }
                }
                _voiceChannelWorker.Say("Goodbye, " + _user.Nickname ?? _user.Username);
            }
            finally
            {
                Console.WriteLine("Stopping voice assistant runner for " + _user.Username);
            }
        }
    }
}
