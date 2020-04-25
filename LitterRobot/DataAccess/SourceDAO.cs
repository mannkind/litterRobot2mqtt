using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System.Net.Http;
using System;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using TwoMQTT.Core.DataAccess;
using LitterRobot.Models.Shared;

namespace LitterRobot.DataAccess
{
    /// <summary>
    /// An class representing a managed way to interact with a source.
    /// </summary>
    public class SourceDAO : HTTPSourceDAO<SlugMapping, Command, Models.SourceManager.FetchResponse, object>
    {
        /// <summary>
        /// Initializes a new instance of the SourceDAO class.
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="opts"></param>
        /// <param name="httpClientFactory"></param>
        /// <param name="cache"></param>
        /// <returns></returns>
        public SourceDAO(ILogger<SourceDAO> logger, IOptions<Models.SourceManager.Opts> opts,
            IHttpClientFactory httpClientFactory, IMemoryCache cache) :
            base(logger, httpClientFactory)
        {
            this.ApiKey = opts.Value.ApiKey;
            this.Login = opts.Value.Login;
            this.Password = opts.Value.Password;
            this.Cache = cache;
            this.ResponseObjCacheExpiration = new TimeSpan(0, 0, 17);
            this.LoginCacheExpiration = new TimeSpan(24, 0, 31);
        }

        /// <inheritdoc />
        public override async Task<Models.SourceManager.FetchResponse?> FetchOneAsync(SlugMapping key,
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await this.FetchAsync(key.LRID, cancellationToken);
            }
            catch (Exception e)
            {
                var msg = e is HttpRequestException ? "Unable to fetch from the Litter Robot API" :
                          e is JsonException ? "Unable to deserialize response from the Litter Robot API" :
                          "Unable to send to the Litter Robot API";
                this.Logger.LogError(msg, e);
                return null;
            }
        }

        /// <inheritdoc />
        public override async Task<object?> SendOneAsync(Command item, CancellationToken cancellationToken = default)
        {
            try
            {
                var litterRobotId = item.Data.LitterRobotId;
                var command = this.TranslateCommand(item);
                return await this.SendAsync(litterRobotId, command, cancellationToken);
            }
            catch (Exception e)
            {
                var msg = e is HttpRequestException ? "Unable to send to the Litter Robot API" :
                          e is JsonException ? "Unable to serialize request to the Litter Robot API" :
                          "Unable to send to the Litter Robot API";
                this.Logger.LogError(msg, e);
                return null;
            }
        }

        /// <summary>
        /// The internal cache.
        /// </summary>
        private readonly IMemoryCache Cache;

        /// <summary>
        /// The internal timeout for responses.
        /// </summary>
        private readonly TimeSpan ResponseObjCacheExpiration;

        /// <summary>
        /// The internal timeout for logins.
        /// </summary>
        private readonly TimeSpan LoginCacheExpiration;

        /// <summary>
        /// The API Key to access the source.
        /// </summary>
        private readonly string ApiKey;

        /// <summary>
        /// The Login to access the source.
        /// </summary>
        private readonly string Login;

        /// <summary>
        /// The Password to access the source.
        /// </summary>
        private readonly string Password;

        /// <summary>
        /// The semaphore to limit how many times Login is called.
        /// </summary>
        private readonly SemaphoreSlim LoginSemaphore = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Get a request for the source
        /// </summary>
        /// <param name="method"></param>
        /// <param name="baseUrl"></param>
        /// <param name="obj"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        private HttpRequestMessage Request(HttpMethod method, string baseUrl, object? obj, string token = "")
        {
            // Setup request + headers
            var request = new HttpRequestMessage(method, baseUrl);
            request.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            request.Headers.TryAddWithoutValidation("x-api-key", this.ApiKey);
            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.TryAddWithoutValidation("Authorization", token);
            }

            // Add optional content
            if (obj != null)
            {
                request.Content = new StringContent(JsonConvert.SerializeObject(obj), Encoding.UTF8, "application/json");
            }

            return request;
        }

        /// <summary>
        /// Login to the source
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<(string, string)> LoginAsync(CancellationToken cancellationToken = default)
        {
            await this.LoginSemaphore.WaitAsync();

            try
            {
                // Try to get the login from cache
                if (this.Cache.TryGetValue(this.CacheKey(TYPEUSERID), out string userid) &&
                    this.Cache.TryGetValue(this.CacheKey(TYPETOKEN), out string token))
                {
                    return (userid, token);
                }

                // Hit the API
                var baseUrl = LOGINURL;
                var apiLogin = new APILogin { email = this.Login, password = this.Password, };
                var request = this.Request(HttpMethod.Post, baseUrl, apiLogin);
                var resp = await this.Client.SendAsync(request, cancellationToken);
                resp.EnsureSuccessStatusCode();
                var content = await resp.Content.ReadAsStringAsync();
                var obj = JsonConvert.DeserializeObject<APILoginResponse>(content);

                this.CacheLogin(obj.User.UserID, obj.Token);
                return (obj.User.UserID, obj.Token);
            }
            finally
            {
                this.LoginSemaphore.Release();
            }
        }

        /// <summary>
        /// Fetch one response from the source
        /// </summary>
        /// <param name="litterRobotId"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<Models.SourceManager.FetchResponse?> FetchAsync(string litterRobotId,
            CancellationToken cancellationToken = default)
        {
            // Check cache first to avoid hammering the Litter Robot API
            if (this.Cache.TryGetValue(this.CacheKey(TYPELRID, litterRobotId),
                out Models.SourceManager.FetchResponse cachedObj))
            {
                return cachedObj;
            }

            var (userid, token) = await this.LoginAsync(cancellationToken);
            var baseUrl = string.Format(STATUSURL, userid);
            var request = this.Request(HttpMethod.Get, baseUrl, null, token);
            var resp = await this.Client.SendAsync(request, cancellationToken);
            resp.EnsureSuccessStatusCode();
            var content = await resp.Content.ReadAsStringAsync();
            var objs = JsonConvert.DeserializeObject<List<Models.SourceManager.FetchResponse>>(content);
            Models.SourceManager.FetchResponse? specificObj = null;
            foreach (var obj in objs)
            {
                // Cache all; return the specific one requested
                this.CacheResponse(obj);
                if (obj.LitterRobotId == litterRobotId)
                {
                    specificObj = obj;
                };
            }

            return specificObj;
        }

        /// <summary>
        /// Send one command to the source
        /// </summary>
        /// <param name="litterRobotId"></param>
        /// <param name="command"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<object?> SendAsync(string litterRobotId, string command,
            CancellationToken cancellationToken = default)
        {
            var (userid, token) = await this.LoginAsync(cancellationToken);
            var baseUrl = string.Format(COMMANDURL, userid, litterRobotId);
            var apiCommand = new APICommand { command = command, litterRobotId = litterRobotId, };
            var request = this.Request(HttpMethod.Post, baseUrl, apiCommand, token);
            var resp = await this.Client.SendAsync(request, cancellationToken);
            resp.EnsureSuccessStatusCode();

            return new object();
        }

        private readonly Dictionary<int, string> CommandMapping = new Dictionary<int, string>
        {
            { (int)CommandType.Power, "<P" },
            { (int)CommandType.Cycle, "<C" },
            { (int)CommandType.NightLight, "<N" },
            { (int)CommandType.PanelLock, "<L" },
            { (int)CommandType.WaitTime, "<W" },
        };

        /// <summary>
        /// Translate interal commands w/data to something the Litter Robot API can utilize
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        private string TranslateCommand(Command item)
        {
            // Covert from the internal Command into something LitterRobot knows about
            Func<bool, string> onoff = (bool x) => x ? "1" : "0";
            var cmd =
                (this.CommandMapping.ContainsKey(item.Command) ? this.CommandMapping[item.Command] : string.Empty) +
                (
                    item.Command == (int)CommandType.Power ? onoff(item.Data.Power) :
                    item.Command == (int)CommandType.Cycle ? onoff(item.Data.Cycle) :
                    item.Command == (int)CommandType.NightLight ? onoff(item.Data.NightLightActive) :
                    item.Command == (int)CommandType.PanelLock ? onoff(item.Data.PanelLockActive) :
                    item.Command == (int)CommandType.WaitTime ? item.Data.CleanCycleWaitTimeMinutes.ToString() :
                    string.Empty
                );
            return cmd;
        }

        /// <summary>
        /// Cache the login
        /// </summary>
        /// <param name="userid"></param>
        /// <param name="token"></param>
        private void CacheLogin(string userid, string token)
        {
            var cts = new CancellationTokenSource(this.LoginCacheExpiration);
            var cacheOpts = new MemoryCacheEntryOptions()
                 .AddExpirationToken(new CancellationChangeToken(cts.Token));

            this.Cache.Set(this.CacheKey(TYPEUSERID), userid, cacheOpts);
            this.Cache.Set(this.CacheKey(TYPETOKEN), token, cacheOpts);
        }

        /// <summary>
        /// Cache the response
        /// </summary>
        /// <param name="obj"></param>
        private void CacheResponse(Models.SourceManager.FetchResponse obj)
        {
            var cts = new CancellationTokenSource(this.ResponseObjCacheExpiration);
            var cacheOpts = new MemoryCacheEntryOptions()
                 .AddExpirationToken(new CancellationChangeToken(cts.Token));

            this.Cache.Set(this.CacheKey(TYPELRID, obj.LitterRobotId), obj, cacheOpts);
        }

        /// <summary>
        /// Generate a cache key
        /// </summary>
        /// <param name="type"></param>
        /// <param name="key"></param>
        /// <returns></returns>
        private string CacheKey(string type, string key = "KEY") => $"{type}_{key}";

        /// <summary>
        /// The base API url to access the source.
        /// </summary>
        private const string APIURL = "https://muvnkjeut7.execute-api.us-east-1.amazonaws.com/staging";

        /// <summary>
        /// The url to login to the source.
        /// </summary>
        private const string LOGINURL = APIURL + "/login";

        /// <summary>
        /// The url to get status from the source.
        /// </summary>
        private const string STATUSURL = APIURL + "/users/{0}/litter-robots";

        /// <summary>
        /// The url to send commands to access the source.
        /// </summary>
        private const string COMMANDURL = APIURL + "/users/{0}/litter-robots/{1}/dispatch-commands";

        /// <summary>
        /// The key to cache litter robot objects.
        /// </summary>
        private const string TYPELRID = "LRID";

        /// <summary>
        /// The key to cache the userid.
        /// </summary>
        private const string TYPEUSERID = "USERID";

        /// <summary>
        /// The key to cache the token.
        /// </summary>
        private const string TYPETOKEN = "TOKEN";


        /// <summary>
        /// 
        /// </summary>
        private class APILogin
        {
            public string email { get; set; } = string.Empty;
            public string oneSignalPlayerId { get; set; } = "0";
            public string password { get; set; } = string.Empty;
        }

        /// <summary>
        /// 
        /// </summary>
        private class APICommand
        {
            public string command { get; set; } = string.Empty;
            public string litterRobotId { get; set; } = string.Empty;
        }

        /// <summary>
        /// 
        /// </summary>
        private class APILoginResponse
        {
            public string Status { get; set; } = string.Empty;
            public string DeveloperMessage { get; set; } = string.Empty;
            public string Created { get; set; } = string.Empty;
            public string URI { get; set; } = string.Empty;
            public string RequestID { get; set; } = string.Empty;
            public string Token { get; set; } = string.Empty;
            public string IdentityID { get; set; } = string.Empty;
            public ResponseUser User { get; set; } = new ResponseUser();

            /// <summary>
            /// 
            /// </summary>
            public class ResponseUser
            {
                public string LastName { get; set; } = string.Empty;
                public string UserEmail { get; set; } = string.Empty;
                public string UserID { get; set; } = string.Empty;
                public string FirstName { get; set; } = string.Empty;
            }
        }
    }
}