using System.Diagnostics;
using DiscordRPC;
using DiscordRPC.Logging;
using Newtonsoft.Json.Linq;

namespace AudaciousRPC
{
    public class Program
    {
        public const string DISCORD_APP_ID = "1395545705069805628";
        private const string DEFAULT_AUDTOOL_PATH = @"C:\Program Files (x86)\Audacious\bin\audtool.exe";
        private const string CONFIG_FILE = "config.json";
        public static string AUDTOOL_PATH;
        public static DiscordRpcClient client;
        private static readonly HttpClient httpClient = new HttpClient();
        private static string lastAlbum = "";
        private static string cachedAlbumArtUrl = "";
        private static bool isDiscordConnected = false;
        

        public static async Task Main(string[] args)
        {
            LoadConfig();
            
            client = new DiscordRpcClient(DISCORD_APP_ID)
            {
                Logger = new ConsoleLogger(LogLevel.Info, true)
            };

            client.OnReady += (sender, e) =>
            {
                Console.WriteLine("Connected to discord with user {0}", e.User.Username);
                isDiscordConnected = true;
            };
            
            client.Initialize();
            
            Console.WriteLine("waiting for discord to be alive lol");
            while (!isDiscordConnected)
            {
                await Task.Delay(100);
            }
            
            httpClient.DefaultRequestHeaders.Add("User-Agent", "AudaciousRPC/1.0");
            
            while (true)
            {
                var songInfo = GetCurrentSongInfo();

                if (songInfo != null)
                {
                    string playbackStatus = GetPlaybackStatus();
                    
                    // Console.WriteLine($"Status: {songInfo.Artist} - {songInfo.Album} - {songInfo.Title} [{playbackStatus}]");
                    // ^ debug logging
                    
                    // only fetch album art if the album has changed, should prevent album art from showing up and then disappearing
                    string albumArtUrl;
                    if (lastAlbum != songInfo.Album)
                    {
                        Console.WriteLine("album changed, fetching new album art");
                        albumArtUrl = await GetAlbumArtUrlAsync(songInfo.Artist, songInfo.Album);
                        lastAlbum = songInfo.Album;
                        cachedAlbumArtUrl = albumArtUrl;
                    }
                    else
                    {
                        albumArtUrl = cachedAlbumArtUrl;
                    }
                    
                    if (playbackStatus == "playing")
                    {
                        DateTime now = DateTime.UtcNow;
                        DateTime startTime = now.AddSeconds(-songInfo.CurrentPosition);
                        DateTime endTime = startTime.AddSeconds(songInfo.Length);
                        
                        if (!string.IsNullOrEmpty(albumArtUrl))
                        {
                            client.SetPresence(new RichPresence()
                            {
                                Type = ActivityType.Listening,
                                Details = $"{songInfo.Title}",
                                State = $"by {songInfo.Artist} on {songInfo.Album}",
                                Timestamps = new Timestamps()
                                {
                                    Start = startTime,
                                    End = endTime
                                },
                                Assets = new Assets()
                                {
                                    LargeImageKey = albumArtUrl,
                                    LargeImageText = "Playing",
                                    SmallImageKey = "play"
                                },
                            });
                            await Task.Delay(250);
                            continue;
                        }
                        else
                        {
                            client.SetPresence(new RichPresence()
                            {
                                Details = $"{songInfo.Title}",
                                State = $"by {songInfo.Artist} on {songInfo.Album}",
                                Timestamps = new Timestamps()
                                {
                                    Start = startTime,
                                    End = endTime
                                },
                                Assets = new Assets()
                                {
                                    LargeImageKey = "audacious_logo",
                                    LargeImageText = "Playing",
                                    SmallImageKey = "play"
                                },
                            });
                            await Task.Delay(250);
                            continue; 
                        }
                    }
                    if (playbackStatus == "stopped")
                    {
                        
                        client.SetPresence(new RichPresence()
                        {
                            Details = "",
                            State = "Idle",
                            Assets = new Assets()
                            {
                                LargeImageKey = "audacious_logo",
                                LargeImageText = "Idle",
                                SmallImageKey = "pause"
                            },
                        });
                        await Task.Delay(250);
                        continue;
                    }
                    if (playbackStatus == "paused")
                    {
                        if (!string.IsNullOrEmpty(albumArtUrl))
                        {
                            client.SetPresence(new RichPresence()
                            {
                                Details = $"{songInfo.Title}",
                                State = $"by {songInfo.Artist} on {songInfo.Album}",
                                Assets = new Assets()
                                {
                                    LargeImageKey = albumArtUrl,
                                    LargeImageText = "Paused",
                                    SmallImageKey = "pause"
                                },
                            });
                            await Task.Delay(250);
                            continue;
                        }
                        else
                        {
                            client.SetPresence(new RichPresence()
                            {
                                Details = $"{songInfo.Title}",
                                State = $"by {songInfo.Artist} on {songInfo.Album}",
                                Assets = new Assets()
                                {
                                    LargeImageKey = "audacious_logo",
                                    LargeImageText = "Paused",
                                    SmallImageKey = "pause"
                                },
                            });
                            await Task.Delay(250);
                            continue;
                        }
                    }
                }

                await Task.Delay(250);
            }
        }

        public class SongInfo
        {
            public string Artist { get; set; }
            public string Album { get; set; }
            public string Title { get; set; }
            public int Length { get; set; }
            public int CurrentPosition { get; set; }
        }

        private static string ExecuteAudtoolCommand(string arguments)
        {
            try
            {
                var processStartInfo = new ProcessStartInfo
                {
                    FileName = AUDTOOL_PATH,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(processStartInfo))
                {
                    if (process == null)
                        return null;

                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();

                    return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
                }
            }
            catch
            {
                return null;
            }
        }

        public static SongInfo GetCurrentSongInfo()
        {
            try
            {
                string songOutput = ExecuteAudtoolCommand("current-song");
                if (string.IsNullOrWhiteSpace(songOutput))
                    return null;

                // parse song info
                string[] parts = songOutput.Split(new[] { " - " }, StringSplitOptions.None);
                if (parts.Length < 3)
                    return null;

                // song length in seconds
                string lengthOutput = ExecuteAudtoolCommand("current-song-length-seconds");
                int length = 0;
                if (!string.IsNullOrWhiteSpace(lengthOutput) && int.TryParse(lengthOutput, out int lengthSeconds))
                {
                    length = lengthSeconds;
                }

                // current playback position in seconds
                string positionOutput = ExecuteAudtoolCommand("current-song-output-length-seconds");
                int currentPosition = 0;
                if (!string.IsNullOrWhiteSpace(positionOutput) && int.TryParse(positionOutput, out int positionSeconds))
                {
                    currentPosition = positionSeconds;
                }

                return new SongInfo
                {
                    Artist = parts[0].Trim(),
                    Album = parts[1].Trim(),
                    Title = string.Join(" - ", parts.Skip(2)).Trim(),
                    Length = length,
                    CurrentPosition = currentPosition
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting song info: {ex.Message}");
                return null;
            }
        }

        public static string GetPlaybackStatus()
        {
            try
            {
                string output = ExecuteAudtoolCommand("playback-status");
                return output?.ToLower();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting playback status: {ex.Message}");
                return null;
            }
        }

        public static async Task<string> GetAlbumArtUrlAsync(string artist, string album)
        {
            try
            {
                // search for release on musicbrainz
                string searchUrl = $"https://musicbrainz.org/ws/2/release/?query=artist:{Uri.EscapeDataString(artist)}%20AND%20release:{Uri.EscapeDataString(album)}&fmt=json&limit=1";
                
                var searchResponse = await httpClient.GetStringAsync(searchUrl);
                var searchResult = JObject.Parse(searchResponse);
                
                var releases = searchResult["releases"];
                if (releases != null && releases.HasValues)
                {
                    string releaseId = releases[0]["id"]?.ToString();
                    
                    if (!string.IsNullOrEmpty(releaseId))
                    {
                        // grab art using release id from musicbrainz
                        string coverArtUrl = $"https://coverartarchive.org/release/{releaseId}/front-500";
                        
                        // make sure it exists
                        var headRequest = new HttpRequestMessage(HttpMethod.Head, coverArtUrl);
                        var headResponse = await httpClient.SendAsync(headRequest);
                        
                        if (headResponse.IsSuccessStatusCode)
                        {
                            // yay! it's here!
                            return coverArtUrl;
                        }
                    }
                }
                
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching album art: {ex.Message}");
                return null;
            }
        }

        private static void LoadConfig()
        {
            try
            {
                if (File.Exists(CONFIG_FILE))
                {
                    string jsonContent = File.ReadAllText(CONFIG_FILE);
                    var config = JObject.Parse(jsonContent);
                    AUDTOOL_PATH = config["audtoolPath"]?.ToString() ?? DEFAULT_AUDTOOL_PATH;
                    Console.WriteLine($"Loaded config: Using audtool path: {AUDTOOL_PATH}");
                }
                else
                {
                    AUDTOOL_PATH = DEFAULT_AUDTOOL_PATH;
                    var defaultConfig = new JObject
                    {
                        ["audtoolPath"] = DEFAULT_AUDTOOL_PATH
                    };
                    File.WriteAllText(CONFIG_FILE, defaultConfig.ToString());
                    Console.WriteLine($"Created default config file: {CONFIG_FILE}");
                    Console.WriteLine($"Using default audtool path: {AUDTOOL_PATH}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading config: {ex.Message}");
                Console.WriteLine($"Using default audtool path: {DEFAULT_AUDTOOL_PATH}");
                AUDTOOL_PATH = DEFAULT_AUDTOOL_PATH;
            }
        }
    }
}