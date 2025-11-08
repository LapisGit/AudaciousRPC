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
        public static string LASTFM_API_KEY;
        public static string LASTFM_API_SECRET;
        public static string LASTFM_SESSION_KEY;
        public static DiscordRpcClient client;
        private static readonly HttpClient httpClient = new HttpClient();
        private static string lastAlbum = "";
        private static string cachedAlbumArtUrl = "";
        private static bool isDiscordConnected = false;
        private static bool hasScrobbled = false;
        private static bool hasUpdatedNowPlaying = false;
        private static string lastScrobbledTrack = "";
        private static DateTime trackStartTime = DateTime.MinValue;
        

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
            
            // Authenticate with Last.fm if API key is provided but no session key exists
            if (!string.IsNullOrEmpty(LASTFM_API_KEY) && !string.IsNullOrEmpty(LASTFM_API_SECRET) && string.IsNullOrEmpty(LASTFM_SESSION_KEY))
            {
                await AuthenticateLastFm();
            }
            
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
                    
                    // Track identifier for scrobbling
                    string currentTrackId = $"{songInfo.Artist}|{songInfo.Album}|{songInfo.Title}";
                    
                    // Reset scrobble state if track changed
                    if (lastScrobbledTrack != currentTrackId)
                    {
                        hasScrobbled = false;
                        hasUpdatedNowPlaying = false;
                        lastScrobbledTrack = currentTrackId;
                        trackStartTime = DateTime.UtcNow.AddSeconds(-songInfo.CurrentPosition);
                    }
                    
                    if (playbackStatus == "playing")
                    {
                        DateTime now = DateTime.UtcNow;
                        DateTime startTime = now.AddSeconds(-songInfo.CurrentPosition);
                        DateTime endTime = startTime.AddSeconds(songInfo.Length);
                        
                        if (!string.IsNullOrEmpty(LASTFM_SESSION_KEY))
                        {
                            // now playing
                            if (!hasUpdatedNowPlaying)
                            {
                                await UpdateNowPlaying(songInfo);
                                hasUpdatedNowPlaying = true;
                            }
                            
                            // scrobble
                            if (!hasScrobbled)
                            {
                                await ScrobbleTrack(songInfo, trackStartTime);
                                hasScrobbled = true;
                            }
                        }
                        
                        if (!string.IsNullOrEmpty(albumArtUrl))
                        {
                            client.SetPresence(new RichPresence()
                            {
                                Type = ActivityType.Listening,
                                Details = $"{songInfo.Title}",
                                State = $"by {songInfo.Artist}",
                                Timestamps = new Timestamps()
                                {
                                    Start = startTime,
                                    End = endTime
                                },
                                Assets = new Assets()
                                {
                                    LargeImageKey = albumArtUrl,
                                    LargeImageText = $"on {songInfo.Album}",
                                    SmallImageText = "Playing",
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
                                State = $"by {songInfo.Artist}",
                                Timestamps = new Timestamps()
                                {
                                    Start = startTime,
                                    End = endTime
                                },
                                Assets = new Assets()
                                {
                                    LargeImageKey = "audacious_logo",
                                    LargeImageText = $"on {songInfo.Album}",
                                    SmallImageText = "Playing",
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
                                LargeImageText = $"on {songInfo.Album}",
                                SmallImageText = "Idle",
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
                                State = $"by {songInfo.Artist}",
                                Assets = new Assets()
                                {
                                    LargeImageKey = albumArtUrl,
                                    LargeImageText = $"on {songInfo.Album}",
                                    SmallImageText = "Paused",
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
                                State = $"by {songInfo.Artist}",
                                Assets = new Assets()
                                {
                                    LargeImageKey = "audacious_logo",
                                    LargeImageText = $"on {songInfo.Album}",
                                    SmallImageText = "Paused",
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
                string artist = ExecuteAudtoolCommand("current-song-tuple-data artist");
                string album = ExecuteAudtoolCommand("current-song-tuple-data album");
                string title = ExecuteAudtoolCommand("current-song-tuple-data title");
                
                if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(album) || string.IsNullOrWhiteSpace(title))
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
                    Artist = artist.Trim(),
                    Album = album.Trim(),
                    Title = title.Trim(),
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
                // use lastfm because its easier and faster if an api key is provided
                if (!string.IsNullOrEmpty(LASTFM_API_KEY))
                {
                    string lastFmArtUrl = await GetLastFmAlbumArtAsync(artist, album);
                    if (!string.IsNullOrEmpty(lastFmArtUrl))
                    {
                        return lastFmArtUrl;
                    }
                }
                
                // fallback if it doesnt exist on lastfm/no api key provided
                
                // search musicbrainz for release id
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
                    LASTFM_API_KEY = config["lastFmApiKey"]?.ToString() ?? "";
                    LASTFM_API_SECRET = config["lastFmApiSecret"]?.ToString() ?? "";
                    LASTFM_SESSION_KEY = config["lastFmSessionKey"]?.ToString() ?? "";
                    Console.WriteLine($"Loaded config: Using audtool path: {AUDTOOL_PATH}");
                }
                else
                {
                    AUDTOOL_PATH = DEFAULT_AUDTOOL_PATH;
                    LASTFM_API_KEY = "";
                    LASTFM_API_SECRET = "";
                    LASTFM_SESSION_KEY = "";
                    var defaultConfig = new JObject
                    {
                        ["audtoolPath"] = DEFAULT_AUDTOOL_PATH,
                        ["lastFmApiKey"] = "",
                        ["lastFmApiSecret"] = "",
                        ["lastFmSessionKey"] = ""
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
                LASTFM_API_KEY = "";
                LASTFM_API_SECRET = "";
                LASTFM_SESSION_KEY = "";
            }
        }

        private static async Task AuthenticateLastFm()
        {
            try
            {
                Console.WriteLine("Authenticating with Last.fm...");
                
                // grab token
                string token = await GetLastFmToken();
                if (string.IsNullOrEmpty(token))
                {
                    Console.WriteLine("Failed to get Last.fm token");
                    return;
                }
                
                // generate auth url
                string authUrl = $"http://www.last.fm/api/auth/?api_key={LASTFM_API_KEY}&token={token}";
                Console.WriteLine($"Please authorize this application in your browser:");
                Console.WriteLine(authUrl);
                
                // open website to authorize
                Process.Start(new ProcessStartInfo
                {
                    FileName = authUrl,
                    UseShellExecute = true
                });
                
                Console.WriteLine("Press Enter after you've authorized the application...");
                Console.ReadLine();
                
                // grab session
                string sessionKey = await GetLastFmSessionKey(token);
                if (string.IsNullOrEmpty(sessionKey))
                {
                    Console.WriteLine("Failed to get Last.fm session key");
                    return;
                }
                
                // save session for future use
                LASTFM_SESSION_KEY = sessionKey;
                SaveSessionKeyToConfig(sessionKey);
                
                Console.WriteLine("Successfully authenticated with Last.fm!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error authenticating with Last.fm: {ex.Message}");
            }
        }

        private static async Task<string> GetLastFmToken()
        {
            try
            {
                string apiSig = GenerateLastFmSignature(new Dictionary<string, string>
                {
                    { "method", "auth.gettoken" },
                    { "api_key", LASTFM_API_KEY }
                });
                
                string url = $"http://ws.audioscrobbler.com/2.0/?method=auth.gettoken&api_key={LASTFM_API_KEY}&api_sig={apiSig}&format=json";
                
                var response = await httpClient.GetStringAsync(url);
                var json = JObject.Parse(response);
                
                return json["token"]?.ToString();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting Last.fm token: {ex.Message}");
                return null;
            }
        }

        private static async Task<string> GetLastFmSessionKey(string token)
        {
            try
            {
                string apiSig = GenerateLastFmSignature(new Dictionary<string, string>
                {
                    { "method", "auth.getsession" },
                    { "api_key", LASTFM_API_KEY },
                    { "token", token }
                });
                
                string url = $"http://ws.audioscrobbler.com/2.0/?method=auth.getsession&api_key={LASTFM_API_KEY}&token={token}&api_sig={apiSig}&format=json";
                
                var response = await httpClient.GetStringAsync(url);
                var json = JObject.Parse(response);
                
                return json["session"]?["key"]?.ToString();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting Last.fm session key: {ex.Message}");
                return null;
            }
        }

        private static string GenerateLastFmSignature(Dictionary<string, string> parameters)
        {
            var sorted = parameters.OrderBy(x => x.Key);
            
            string sigString = "";
            foreach (var param in sorted)
            {
                sigString += param.Key + param.Value;
            }
            sigString += LASTFM_API_SECRET;
            
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] inputBytes = System.Text.Encoding.UTF8.GetBytes(sigString);
                byte[] hashBytes = md5.ComputeHash(inputBytes);
                
                return BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
            }
        }

        private static void SaveSessionKeyToConfig(string sessionKey)
        {
            try
            {
                string jsonContent = File.ReadAllText(CONFIG_FILE);
                var config = JObject.Parse(jsonContent);
                config["lastFmSessionKey"] = sessionKey;
                File.WriteAllText(CONFIG_FILE, config.ToString());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving session key to config: {ex.Message}");
            }
        }

        private static async Task<string> GetLastFmAlbumArtAsync(string artist, string album)
        {
            try
            {
                string url = $"http://ws.audioscrobbler.com/2.0/?method=album.getinfo&api_key={LASTFM_API_KEY}&artist={Uri.EscapeDataString(artist)}&album={Uri.EscapeDataString(album)}&format=json";
                
                var response = await httpClient.GetStringAsync(url);
                var json = JObject.Parse(response);
                
                var images = json["album"]?["image"];
                if (images != null && images.HasValues)
                {
                    var largestImage = images.LastOrDefault(i => i["size"]?.ToString() == "extralarge" || i["size"]?.ToString() == "mega");
                    if (largestImage == null)
                    {
                        largestImage = images.Last();
                    }
                    
                    string imageUrl = largestImage["#text"]?.ToString();
                    if (!string.IsNullOrEmpty(imageUrl))
                    {
                        return imageUrl;
                    }
                }
                
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching Last.fm album art: {ex.Message}");
                return null;
            }
        }

        private static async Task UpdateNowPlaying(SongInfo song)
        {
            try
            {
                var parameters = new Dictionary<string, string>
                {
                    { "method", "track.updateNowPlaying" },
                    { "artist", song.Artist },
                    { "track", song.Title },
                    { "album", song.Album },
                    { "api_key", LASTFM_API_KEY },
                    { "sk", LASTFM_SESSION_KEY }
                };
                
                if (song.Length > 0)
                {
                    parameters.Add("duration", song.Length.ToString());
                }
                
                string apiSig = GenerateLastFmSignature(parameters);
                
                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("method", "track.updateNowPlaying"),
                    new KeyValuePair<string, string>("artist", song.Artist),
                    new KeyValuePair<string, string>("track", song.Title),
                    new KeyValuePair<string, string>("album", song.Album),
                    new KeyValuePair<string, string>("duration", song.Length.ToString()),
                    new KeyValuePair<string, string>("api_key", LASTFM_API_KEY),
                    new KeyValuePair<string, string>("sk", LASTFM_SESSION_KEY),
                    new KeyValuePair<string, string>("api_sig", apiSig),
                    new KeyValuePair<string, string>("format", "json")
                });
                
                var response = await httpClient.PostAsync("http://ws.audioscrobbler.com/2.0/", content);
                var responseText = await response.Content.ReadAsStringAsync();
                
                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Now Playing updated on Last.fm: {song.Artist} - {song.Title}");
                }
                else
                {
                    Console.WriteLine($"Failed to update Now Playing on Last.fm: {responseText}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error updating Now Playing on Last.fm: {ex.Message}");
            }
        }

        private static async Task ScrobbleTrack(SongInfo song, DateTime startTime)
        {
            try
            {
                long timestamp = new DateTimeOffset(startTime).ToUnixTimeSeconds();
                
                var parameters = new Dictionary<string, string>
                {
                    { "method", "track.scrobble" },
                    { "artist", song.Artist },
                    { "track", song.Title },
                    { "timestamp", timestamp.ToString() },
                    { "album", song.Album },
                    { "api_key", LASTFM_API_KEY },
                    { "sk", LASTFM_SESSION_KEY }
                };
                
                string apiSig = GenerateLastFmSignature(parameters);
                
                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("method", "track.scrobble"),
                    new KeyValuePair<string, string>("artist", song.Artist),
                    new KeyValuePair<string, string>("track", song.Title),
                    new KeyValuePair<string, string>("timestamp", timestamp.ToString()),
                    new KeyValuePair<string, string>("album", song.Album),
                    new KeyValuePair<string, string>("api_key", LASTFM_API_KEY),
                    new KeyValuePair<string, string>("sk", LASTFM_SESSION_KEY),
                    new KeyValuePair<string, string>("api_sig", apiSig),
                    new KeyValuePair<string, string>("format", "json")
                });
                
                var response = await httpClient.PostAsync("http://ws.audioscrobbler.com/2.0/", content);
                var responseText = await response.Content.ReadAsStringAsync();
                
                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Scrobbled to Last.fm: {song.Artist} - {song.Title}");
                }
                else
                {
                    Console.WriteLine($"Failed to scrobble to Last.fm: {responseText}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error scrobbling to Last.fm: {ex.Message}");
            }
        }
    }
}