using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;

/* Constants */
string Executable = AppContext.BaseDirectory;
string DataFile = Path.Combine(Executable, "cdata.json");

/* Program variables */
string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/146.0.0.0 Safari/537.36";
string ProxyURL = "https://cdn.jsdelivr.net/gh/proxifly/free-proxy-list@main/proxies/countries/US/data.json";
var Pretty = new JsonSerializerOptions { WriteIndented = true };
var CamelCase = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

/* Initialization */
if (args.Length < 1) {
    Environment.Exit(1);
}

string Action = args[0];

if (!File.Exists(DataFile)) {
    SaveData();
} else {
    LoadData();
}

/* Program logic */
switch (Action)
{
    case "fetch_proxies":
    {
        var Proxies = FetchProxies();
        
        if (Proxies == null || Proxies.Count == 0)
        {
            Exit("No proxies fetched", "error");
            return;
        }

        var Valid = new System.Collections.Concurrent.ConcurrentBag<ValidProxy>();

        Parallel.ForEach(Proxies, new ParallelOptions { MaxDegreeOfParallelism = Data.Threads }, p =>
        {
            if (p.Alive())
            {
                p.GetCountry();

                if (p.GeoLocation.Country == "US")
                {
                    var proxy = new ValidProxy(p.Direct);
                    WriteJson(p.Direct, "addProxy");
                    Valid.Add(proxy);
                }
            }
        });

        Data.Proxies.AddRange(Valid);
        SaveData();
        WriteJson($"Imported proxies: {Valid.Count}", "fetchFinish");
    }
    break;
    case "recheck":
    {
        if (args.Length < 2)
        {
            Exit("Usage: ssorecheck recheck [account]", "error");
        }

        if (Data.Proxies.Count < 1)
        {
            Exit("No proxies found, try manual 'ssorecheck fetch_proxies'", "error");
        }

        string Account = args[1];
        int Retries = 0;

        session_fetch:
        SSO.RandomProxy();
        var Session = SSO.CreateSession(Account);

        if (Session == null)
        {
            if (Retries >= 20)
            {
                Exit("Unable to fetch session after 20 retries", "error");
                return;
            }

            Retries++;
            goto session_fetch;
        }
    
        var SessionGen = new SessionGenerator(Session.DeviceID);
        string SessionToken = SessionGen.Encrypt(Session.RefreshToken);

        Retries = 0;

        launcher_fetch:
        if (Retries != 0)
        {
            SSO.RandomProxy();
        }

        var Launcher = SSO.LauncherInit(Session.DeviceID, Session.AccessToken);

        if (Launcher == null)
        {
            if (Retries >= 20)
            {
                WriteJson("Failed to initialize launcher", "warn");

                var NewSession = new
                {
                    message = new
                    {
                        user = Account,
                        deviceId = Session.DeviceID,
                        session_token = SessionToken
                    },
                    status = "rechecked"
                };

                Write(JsonSerializer.Serialize(NewSession));
                return;
            }
            else
            {
                Retries++;
                goto launcher_fetch;
            }
        }

        Retries = 0;
        
        starcoins_fetch:
        if (Retries != 0)
        {
            SSO.RandomProxy();
        }

        var StarCoins = SSO.GetStarCoins(Session.DeviceID, Session.AccessToken);

        if (StarCoins == null && Launcher != null)
        {
            if (Retries >= 20)
            {
                WriteJson("Failed to fetch star coins", "warn");
            }
            else
            {
                Retries++;
                goto starcoins_fetch;
            }
        }

        Retries = 0;

        serverdata_fetch:
        if (Retries != 0)
        {
            SSO.RandomProxy();
        }

        var Server = SSO.GetServerData(Session.DeviceID, Session.AccessToken);

        if (Server == null && Launcher != null)
        {
            if (Retries >= 20)
            {
                WriteJson("Failed to fetch server data", "warn");
            }
            else
            {
                Retries++;
                goto serverdata_fetch;
            }
        }

        var Response = new
        {
            message = new
            {
                user = Account,
                session_token = SessionToken,
                deviceId = Session.DeviceID,
                verified = Launcher?.Verified,
                banned = Launcher?.Banned,
                starCoins = StarCoins?.StarCoins,
                server = Server != null ? $"{Server.FriendlyName} ({Server.Name} : {Server.RegionID}/{Server.ID})" : null
            },
            status = "rechecked"
        };

        Write(JsonSerializer.Serialize(Response));
    }
    break;
}

/* Functions */
List<Proxy> FetchProxies()
{
    using HttpClient client = new HttpClient();
    client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    client.Timeout = TimeSpan.FromSeconds(15);
    
    try
    {
        HttpResponseMessage response = client.GetAsync(ProxyURL).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        string json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        List<Proxy>? _Proxies = JsonSerializer.Deserialize<List<Proxy>>(json, CamelCase);
        WriteJson($"Scraped proxies: {_Proxies?.Count}", "scrapeFinish");
        return _Proxies ?? new List<Proxy>();
    }
    catch
    {
        WriteJson("Failed to fetch proxies", "error");
        return new List<Proxy>();
    }
}

/* Definitions */
void Write(string str) => Definitions.Write(str);
void WriteJson(string message, string status = "success") => Write(JsonSerializer.Serialize(new Message(message, status)));
void Exit(string message, string status = "success") => Definitions.Exit(message, status);
void SaveData()
{
    var dataToSave = new
    {
        ClientVersion = Data.ClientVersion,
        Proxies = Data.Proxies,
        LastFetch = Data.LastFetch,
        Threads = Data.Threads
    };

    File.WriteAllText(DataFile, JsonSerializer.Serialize(dataToSave, Definitions.Pretty));
}
void LoadData()
{
    string json = File.ReadAllText(DataFile).Trim();
    var Placeholder = JsonSerializer.Deserialize<PlaceholderData>(json)!;
    
    Data.ClientVersion = Placeholder.ClientVersion ?? Data.ClientVersion;
    Data.Proxies = Placeholder.Proxies ?? new List<ValidProxy>();
    Data.LastFetch = Placeholder.LastFetch;
    Data.Threads = Placeholder.Threads;
}
public class PlaceholderData
{
    public string ClientVersion { get; set; } = "2.56.1";
    public List<ValidProxy> Proxies { get; set; } = new List<ValidProxy>();
    public int LastFetch { get; set; } = 0;
    public int Threads { get; set; } = 100;
}
public class Proxy
{
    [JsonPropertyName("proxy")]
    public string Direct { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public string IP { get; set; } = string.Empty;
    public int Port { get; set; }
    public bool Https { get; set; } = false;
    public string Anonymity { get; set; } = string.Empty;
    public int Score { get; set; }
    public GeoLocation GeoLocation { get; set; } = new GeoLocation();

    public void GetCountry()
    {
        if (string.IsNullOrWhiteSpace(IP)) return;

        string IPinfoAPIKey = "YOUR_FREE_IPINFO_KEY";
        string URL = $"https://api.ipinfo.io/lite/{IP}?token={IPinfoAPIKey}";
        
        using HttpClient client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(10);

        try
        {
            HttpResponseMessage response = client.GetAsync(URL).GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode)
            {
                string p = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                return;
            }

            string json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            var result = JsonSerializer.Deserialize<IPInfoResponse>(json, Definitions.CamelCase);

            if (result != null && !string.IsNullOrEmpty(result.CountryCode))
            {
                GeoLocation.Country = result.CountryCode.ToUpper();
            }
        }
        catch
        {

        }
    }

    public bool Alive()
    {
        if (string.IsNullOrWhiteSpace(Direct)) return false;

        string URL = "https://launcher-release-prod.starstable.com/latest.yml";
        string SSOUserAgent = $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) StarStableOnline/{Data.ClientVersion} Chrome/128.0.6613.186 Electron/32.2.7 Safari/537.36";
    
        var Handler = new HttpClientHandler
        {
            Proxy = new WebProxy(Direct),
            UseProxy = true,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        using var Client = new HttpClient(Handler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        try
        {
            HttpResponseMessage Response = Client.GetAsync(URL).GetAwaiter().GetResult();
            return Response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
public class GeoLocation
{
    public string Country { get; set; } = string.Empty;
    public string City { get; set; } = "unknown";
}
public class Message
{
    public string message { get; set; } = string.Empty;
    public string status { get; set; } = "success";

    public Message(string _message, string _status = "success")
    {
        status = _status;
        message = _message;
    }
}
public class ValidProxy
{
    public string Direct { get; set; } = string.Empty;
    public int Retries { get; set; }

    public ValidProxy(string direct, int retries = 0)
    {
        Direct = direct;
        Retries = retries;
    }
}
public class IPInfoResponse
{
    [JsonPropertyName("country_code")]
    public string CountryCode { get; set; } = string.Empty;
}
public class Session
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public string DeviceID { get; set; } = string.Empty;

    public Session(string access_token, string refresh_token, string deviceId)
    {
        AccessToken = access_token;
        RefreshToken = refresh_token;
        DeviceID = deviceId;
    }
}

/* Executable state */
public static class Data 
{
    public static string ClientVersion { get; set; } = "2.56.1";
    public static List<ValidProxy> Proxies { get; set; } = new List<ValidProxy>();
    public static int LastFetch { get; set; } = 0;
    public static int Threads { get; set; } = 100;
}

/* Definitions */
public static class Definitions
{
    public static JsonSerializerOptions Pretty = new JsonSerializerOptions { WriteIndented = true };
    public static JsonSerializerOptions CamelCase = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static void Write(string str) => Console.WriteLine(str);
    public static void WriteJson(string message, string status = "success") => Write(JsonSerializer.Serialize(new Message(message, status)));
    public static void Exit(string message, string status = "success")
    {
        var Message = new Message(message, status);
        Write(JsonSerializer.Serialize(Message));
        Environment.Exit(0);
    }
    public static ValidProxy? Random(this List<ValidProxy> list)
    {
        if (list == null || list.Count == 0)
        {
            return null;
        }

        byte[] Buffer = new byte[4];
        RandomNumberGenerator.Fill(Buffer);

        uint Random = BitConverter.ToUInt32(Buffer, 0);
        int Index = (int)(Random % (uint)list.Count);

        return list[Index];
    }
}

/* SSO functions */
public static class SSO
{
    public static class API
    {
        public static string SessionInit = "https://lb-pub.prod.starstable.com/api-gateway/1.0/session/create";
        public static string LauncherBase = "https://launcher-proxy.starstable.com/launcher/";
        public static string LauncherInit = "auth/initialize";
        public static string StarCoins = "star-coins/token";
        public static string ServerData = "game-server/token";
    }

    public static string UserAgent = $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) StarStableOnline/{Data.ClientVersion} Chrome/128.0.6613.186 Electron/32.2.7 Safari/537.36";
    public static string Proxy = string.Empty;

    public static string RandomDeviceID()
    {
        byte[] Bytes = new byte[32];
        RandomNumberGenerator.Fill(Bytes);

        return Convert.ToHexString(Bytes).ToLower();
    }

    public static void RandomProxy()
    {
        var Random = Data.Proxies.Random();

        if (Random == null)
        {
            Definitions.Exit("Failed to select random proxy", "error");
            return;
        }

        Proxy = Random.Direct;
    }

    public static string Request(string Endpoint, Dictionary<string, string> Headers, string Method = "GET", object? Payload = null)
    {
        string Body = string.Empty;

        if (string.IsNullOrEmpty(Proxy))
        {
            Definitions.Exit("No proxies found, run 'ssorecheck fetch_proxies'", "error");
        }

        if (Payload != null && Method is "POST") 
        {
            Body = JsonSerializer.Serialize(Payload, Definitions.CamelCase);
        }

        using var Handler = new HttpClientHandler
        {
            Proxy = new WebProxy(Proxy),
            UseProxy = true,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        using var Client = new HttpClient(Handler)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        Client.DefaultRequestHeaders.UserAgent.ParseAdd(SSO.UserAgent);
        
        foreach (var Header in Headers)
        {
            Client.DefaultRequestHeaders.Add(Header.Key, Header.Value);
        }

        try
        {
            HttpResponseMessage Response;

            if (Method.ToUpper() == "GET")
            {
                Response = Client.GetAsync(Endpoint).GetAwaiter().GetResult();
            }
            else
            {
                var Content = new StringContent(Body, Encoding.UTF8, "application/json");
                Response = Client.PostAsync(Endpoint, Content).GetAwaiter().GetResult();
            }

            return Response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }
        catch
        {
            return string.Empty;
        }
    }

    public static Session? CreateSession(string Account)
    {
        string[] Credentials = Account.Split(":");

        if (Credentials.Length < 2) {
            Definitions.Exit("Invalid account provided", "error");
        }

        string Email = Credentials[0];
        string Password = Credentials[1];
        string DeviceID = RandomDeviceID();

        var Payload = new 
        {
            credentials = new
            {
                email = Email,
                password = Password
            }
        };

        var Headers = new Dictionary<string, string>
        {
            { "Device-ID", DeviceID },
            { "skipAuthRefresh", "true" }
        };

        string Response = Request(API.SessionInit, Headers, "POST", Payload);

        if (string.IsNullOrEmpty(Response))
        {
            return null;
        }

        if (Response.Contains("error_code\":\"invalid credentials\""))
        {
            Definitions.Exit("Invalid credentials", "invalid_credentials");
        }

        var Json = JsonSerializer.Deserialize<SessionInitResponse>(Response, Definitions.CamelCase);

        if (Json != null && !string.IsNullOrEmpty(Json.AccessToken) && !string.IsNullOrEmpty(Json.RefreshToken))
        { 
            return new Session(Json.AccessToken, Json.RefreshToken, DeviceID);
        }

        return null;
    }

    public static LauncherInitResponse? LauncherInit(string DeviceID, string AccessToken)
    {
        string Endpoint = API.LauncherBase + API.LauncherInit;

        var Payload = new 
        {
            deviceId = DeviceID,
            launcherVersion = Data.ClientVersion,
            launcherPlatform = "desktop",
            clientOsRelease = "10.0.22000",
            browserFamily = "Electron"
        };

        var Headers = new Dictionary<string, string>
        {
            { "Authorization", $"Bearer {AccessToken}" },
            { "Device-ID", DeviceID }
        };

        string Response = Request(Endpoint, Headers, "POST", Payload);

        if (string.IsNullOrEmpty(Response))
        {
            return null;
        }

        var Launcher = JsonSerializer.Deserialize<LauncherInitResponse>(Response, Definitions.CamelCase);

        if (Launcher != null)
        {
            return Launcher;
        }

        return null;
    }

    public static StarCoinsResponse? GetStarCoins(string DeviceID, string AccessToken)
    {
        string Endpoint = API.LauncherBase + API.StarCoins;

        var Headers = new Dictionary<string, string>
        {
            { "Authorization", $"Bearer {AccessToken}" },
            { "Device-ID", DeviceID }
        };

        string Response = Request(Endpoint, Headers);

        var Json = JsonSerializer.Deserialize<StarCoinsResponse>(Response, Definitions.CamelCase);

        if (Json != null)
        {
            return Json; 
        }

        return null;
    }

    public static ServerDataResponse? GetServerData(string DeviceID, string AccessToken)
    {
        string Endpoint = API.LauncherBase + API.ServerData;

        var Headers = new Dictionary<string, string>
        {
            { "Authorization", $"Bearer {AccessToken}" },
            { "Device-ID", DeviceID }
        };

        string Response = Request(Endpoint, Headers);

        var serverData = JsonSerializer.Deserialize<ServerDataResponse>(Response, Definitions.CamelCase);

        if (serverData != null && !string.IsNullOrEmpty(serverData.Name))
        {
            return serverData;
        }

        return null;
    }

    public class SessionInitResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("refresh_token")]
        public string RefreshToken { get; set; } = string.Empty; 
    }

    public class LauncherInitResponse
    {
        [JsonPropertyName("isVerified")]
        public bool Verified { get; set; }

        [JsonPropertyName("permanentlyBanned")]
        public bool Banned { get; set; }
    }

    public class StarCoinsResponse
    {
        [JsonPropertyName("starCoins")]
        public int StarCoins { get; set; }
    }

    public class ServerDataResponse
    {
        [JsonPropertyName("friendlyName")]
        public string FriendlyName { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("regionId")]
        public int RegionID { get; set; }

        [JsonPropertyName("id")]
        public int ID { get; set; }
    }
}

/* Encryption thing */
public sealed class AesCtr
{
    public static byte[] Encrypt(byte[] key, byte[] iv, byte[] plaintext)
    {
        if (key.Length != 32)
            throw new ArgumentException("Key must be 32 bytes for AES-256", nameof(key));

        if (iv.Length != 16)
            throw new ArgumentException("IV must be 16 bytes", nameof(iv));

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;

        // Create encryptor with zero IV (we handle counter ourselves)
        using var encryptor = aes.CreateEncryptor(aes.Key, new byte[16]);

        byte[] counter = (byte[])iv.Clone();   // starting counter = IV
        byte[] keystream = new byte[plaintext.Length];
        byte[] buffer = new byte[16];

        int offset = 0;

        while (offset < plaintext.Length)
        {
            // Encrypt the current counter block
            encryptor.TransformBlock(counter, 0, 16, buffer, 0);

            // XOR with plaintext
            int blockSize = Math.Min(16, plaintext.Length - offset);
            for (int i = 0; i < blockSize; i++)
            {
                keystream[offset + i] = (byte)(plaintext[offset + i] ^ buffer[i]);
            }

            offset += blockSize;

            // Increment counter (big-endian)
            for (int i = 15; i >= 0; i--)
            {
                if (++counter[i] != 0) break;
            }
        }

        return keystream;
    }
}
public class SessionGenerator
{
    private readonly string _uniqueDeviceId;
    private const string Algorithm = "aes-256-ctr"; // only for reference, not used directly
    private const int Iterations = 100000;
    private const int KeySize = 32; // 256 bits

    public SessionGenerator(string? deviceId = null)
    {
        _uniqueDeviceId = deviceId ?? "St@r-$t@bl3-0nlin3-d3$kt0p-l@unCher-$e$$i0n-$t0r@ge-k3y";
    }

    private byte[] DeriveKey(byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            password: Encoding.UTF8.GetBytes(_uniqueDeviceId),
            salt: salt,
            iterations: Iterations,
            hashAlgorithm: HashAlgorithmName.SHA256,
            outputLength: KeySize
        );
    }

    public string Encrypt(string refreshToken)
    {
        // Generate random salt (sugar) and IV (saddle)
        byte[] saddle = RandomNumberGenerator.GetBytes(16); // IV
        byte[] sugar = RandomNumberGenerator.GetBytes(16);  // salt

        byte[] derivedKey = DeriveKey(sugar);

        // Prepare plaintext: { "banana": refreshToken }
        var payload = new { banana = refreshToken };
        string jsonText = JsonSerializer.Serialize(payload);
        byte[] plaintext = Encoding.UTF8.GetBytes(jsonText);

        // Encrypt using AES-256-CTR
        byte[] encrypted = AesCtr.Encrypt(derivedKey, saddle, plaintext);

        // Build session object
        var session = new
        {
            saddle = Convert.ToHexString(saddle).ToLower(),
            sugar = Convert.ToHexString(sugar).ToLower(),
            horseshoe = Convert.ToHexString(encrypted).ToLower()
        };

        // JSON → Base64
        string sessionJson = JsonSerializer.Serialize(session);
        string finalBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(sessionJson));

        return finalBase64;
    }
}