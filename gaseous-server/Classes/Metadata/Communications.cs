using System.ComponentModel;
using System.Net;
using Humanizer;
using IGDB;
using RestEase;

namespace gaseous_server.Classes.Metadata
{
    /// <summary>
    /// Handles all metadata API communications
    /// </summary>
    public class Communications
    {
        private static IGDBClient igdb = new IGDBClient(
                    // Found in Twitch Developer portal for your app
                    Config.IGDB.ClientId,
                    Config.IGDB.Secret
                );

        private HttpClient client = new HttpClient();

        /// <summary>
        /// Configure metadata API communications
        /// </summary>
        public static HasheousClient.Models.MetadataModel.MetadataSources MetadataSource
        {
            get
            {
                return _MetadataSource;
            }
            set
            {
                _MetadataSource = value;

                switch (value)
                {
                    case HasheousClient.Models.MetadataModel.MetadataSources.IGDB:
                        // set rate limiter avoidance values
                        RateLimitAvoidanceWait = 1500;
                        RateLimitAvoidanceThreshold = 3;
                        RateLimitAvoidancePeriod = 1;

                        // set rate limiter recovery values
                        RateLimitRecoveryWaitTime = 10000;

                        break;
                    default:
                        // leave all values at default
                        break;
                }
            }
        }
        private static HasheousClient.Models.MetadataModel.MetadataSources _MetadataSource = HasheousClient.Models.MetadataModel.MetadataSources.None;

        // rate limit avoidance - what can we do to ensure that rate limiting is avoided?
        // these values affect all communications

        /// <summary>
        /// How long to wait to avoid hitting an API rate limiter
        /// </summary>
        private static int RateLimitAvoidanceWait = 2000;

        /// <summary>
        /// How many API calls in the period are allowed before we start introducing a wait
        /// </summary>
        private static int RateLimitAvoidanceThreshold = 80;

        /// <summary>
        /// A counter of API calls since the beginning of the period
        /// </summary>
        private static int RateLimitAvoidanceCallCount = 0;

        /// <summary>
        /// How large the period (in seconds) to measure API call counts against
        /// </summary>
        private static int RateLimitAvoidancePeriod = 60;

        /// <summary>
        /// The start of the rate limit avoidance period
        /// </summary>
        private static DateTime RateLimitAvoidanceStartTime = DateTime.UtcNow;

        /// <summary>
        /// Used to determine if we're already in rate limit avoidance mode - always query "InRateLimitAvoidanceMode"
        /// for up to date mode status.
        /// This bool is used to track status changes and should not be relied upon for current status.
        /// </summary>
        private static bool InRateLimitAvoidanceModeStatus = false;

        /// <summary>
        /// Determine if we're in rate limit avoidance mode.
        /// </summary>
        private static bool InRateLimitAvoidanceMode
        {
            get
            {
                if (RateLimitAvoidanceStartTime.AddSeconds(RateLimitAvoidancePeriod) <= DateTime.UtcNow)
                {
                    // avoidance period has expired - reset
                    RateLimitAvoidanceCallCount = 0;
                    RateLimitAvoidanceStartTime = DateTime.UtcNow;

                    return false;
                }
                else
                {
                    // we're in the avoidance period
                    if (RateLimitAvoidanceCallCount > RateLimitAvoidanceThreshold)
                    {
                        // the number of call counts indicates we should throttle things a bit
                        if (InRateLimitAvoidanceModeStatus == false)
                        {
                            Logging.Log(Logging.LogType.Information, "API Connection", "Entered rate limit avoidance period, API calls will be throttled by " + RateLimitAvoidanceWait + " milliseconds.");
                            InRateLimitAvoidanceModeStatus = true;
                        }
                        return true;
                    }
                    else
                    {
                        // still in full speed mode - no throttle required
                        if (InRateLimitAvoidanceModeStatus == true)
                        {
                            Logging.Log(Logging.LogType.Information, "API Connection", "Exited rate limit avoidance period, API call rate is returned to full speed.");
                            InRateLimitAvoidanceModeStatus = false;
                        }
                        return false;
                    }
                }
            }
        }

        // rate limit handling - how long to wait to allow the server to recover and try again
        // these values affect ALL communications if a 429 response code is received

        /// <summary>
        /// How long to wait (in milliseconds) if a 429 status code is received before trying again
        /// </summary>
        private static int RateLimitRecoveryWaitTime = 10000;

        /// <summary>
        /// The time when normal communications can attempt to be resumed
        /// </summary>
        private static DateTime RateLimitResumeTime = DateTime.UtcNow.AddMinutes(5 * -1);

        // rate limit retry - how many times to retry before aborting
        private int RetryAttempts = 0;
        private int RetryAttemptsMax = 3;

        /// <summary>
        /// Request data from the metadata API
        /// </summary>
        /// <typeparam name="T">Type of object to return</typeparam>
        /// <param name="Endpoint">API endpoint segment to use</param>
        /// <param name="Fields">Fields to request from the API</param>
        /// <param name="Query">Selection criteria for data to request</param>
        /// <returns></returns>
        public async Task<T[]?> APIComm<T>(string Endpoint, string Fields, string Query)
        {
            switch (_MetadataSource)
            {
                case HasheousClient.Models.MetadataModel.MetadataSources.None:
                    return null;
                case HasheousClient.Models.MetadataModel.MetadataSources.IGDB:
                    return await IGDBAPI<T>(Endpoint, Fields, Query);
                default:
                    return null;
            }
        }

        private async Task<T[]> IGDBAPI<T>(string Endpoint, string Fields, string Query)
        {
            Logging.Log(Logging.LogType.Debug, "API Connection", "Accessing API for endpoint: " + Endpoint);

            if (RateLimitResumeTime > DateTime.UtcNow)
            {
                Logging.Log(Logging.LogType.Information, "API Connection", "IGDB rate limit hit. Pausing API communications until " + RateLimitResumeTime.ToString() + ". Attempt " + RetryAttempts + " of " + RetryAttemptsMax + " retries.");
                Thread.Sleep(RateLimitRecoveryWaitTime);
            }

            try
            {   
                if (InRateLimitAvoidanceMode == true)
                {
                    // sleep for a moment to help avoid hitting the rate limiter
                    Thread.Sleep(RateLimitAvoidanceWait);
                }

                // perform the actual API call
                var results = await igdb.QueryAsync<T>(Endpoint, query: Fields + " " + Query + ";");

                // increment rate limiter avoidance call count
                RateLimitAvoidanceCallCount += 1;
                
                return results;
            }
            catch (ApiException apiEx)
            {
                switch (apiEx.StatusCode)
                {
                    case HttpStatusCode.TooManyRequests:
                        if (RetryAttempts >= RetryAttemptsMax)
                        {
                            Logging.Log(Logging.LogType.Warning, "API Connection", "IGDB rate limiter attempts expired. Aborting.", apiEx);
                            throw;
                        }
                        else
                        {
                            Logging.Log(Logging.LogType.Information, "API Connection", "IGDB API rate limit hit while accessing endpoint " + Endpoint, apiEx);
                            
                            RetryAttempts += 1;

                            return await IGDBAPI<T>(Endpoint, Fields, Query);
                        }
                    default:
                        Logging.Log(Logging.LogType.Warning, "API Connection", "Exception when accessing endpoint " + Endpoint, apiEx);
                        throw;
                }
            }
            catch(Exception ex)
            {
                Logging.Log(Logging.LogType.Warning, "API Connection", "Exception when accessing endpoint " + Endpoint, ex);
                throw;
            }
        }

        /// <summary>
        /// Download from the specified uri
        /// </summary>
        /// <param name="uri">The uri to download from</param>
        /// <param name="DestinationFile">The file name and path the download should be stored as</param>
        public Task<bool?> DownloadFile(Uri uri, string DestinationFile)
        {
            var result = _DownloadFile(uri, DestinationFile);
            
            return result;
        }

        private async Task<bool?> _DownloadFile(Uri uri, string DestinationFile)
        {
            string DestinationDirectory = new FileInfo(DestinationFile).Directory.FullName;

            Logging.Log(Logging.LogType.Information, "Communications", "Downloading from " + uri.ToString() + " to " + DestinationFile);

            try
            {
                using (var s = client.GetStreamAsync(uri))
                {
                    s.ConfigureAwait(false);

                    if (!Directory.Exists(DestinationDirectory))
                    { 
                        Directory.CreateDirectory(DestinationDirectory); 
                    }
                    
                    using (var fs = new FileStream(DestinationFile, FileMode.OpenOrCreate))
                    {
                        s.Result.CopyTo(fs);
                    }
                }

                if (File.Exists(DestinationFile))
                {
                    FileInfo fi = new FileInfo(DestinationFile);
                    if (fi.Length > 0)
                    {
                        return true;
                    }
                    else
                    {
                        File.Delete(DestinationFile);
                        throw new Exception("Zero length file");
                    }
                }
                else
                {
                    throw new Exception("Zero length file");
                }
            }
            catch (Exception ex)
            {
                Logging.Log(Logging.LogType.Critical, "Communications", "Error while downloading from " + uri.ToString(), ex);

                throw;
            }

            return false;
        }

        public static async Task<string> GetSpecificImageFromServer(string ImagePath, string ImageId, IGDBAPI_ImageSize size, List<IGDBAPI_ImageSize>? FallbackSizes = null)
        {
            Communications comms = new Communications();
            List<IGDBAPI_ImageSize> imageSizes = new List<IGDBAPI_ImageSize>
            {
                size
            };

            string returnPath = "";
            FileInfo? fi;

            // get the image
            try
            {
                returnPath = Path.Combine(ImagePath, size.ToString(), ImageId + ".jpg");

                if (!File.Exists(returnPath))
                {
                    await comms.IGDBAPI_GetImage(imageSizes, ImageId, ImagePath);
                }
                
                if (File.Exists(returnPath))
                {
                    fi = new FileInfo(returnPath);
                    if (fi.Length == 0 || fi.LastWriteTimeUtc.AddDays(7) < DateTime.UtcNow)
                    {
                        File.Delete(returnPath);
                        await comms.IGDBAPI_GetImage(imageSizes, ImageId, ImagePath);
                    }
                }
                
            }
            catch (HttpRequestException ex)
            {
                if (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    Logging.Log(Logging.LogType.Information, "Image Download", "Image not found, trying a different size.");

                    if (FallbackSizes != null)
                    {
                        foreach (Communications.IGDBAPI_ImageSize imageSize in FallbackSizes)
                        {
                            returnPath = await GetSpecificImageFromServer(ImagePath, ImageId, imageSize, null);
                        }
                    }
                }
            }

            return returnPath;
        }

        /// <summary>
        /// See https://api-docs.igdb.com/?javascript#images for more information about the image url structure
        /// </summary>
        /// <param name="ImageId"></param>
        /// <param name="outputPath">The path to save the downloaded files to
        public async Task IGDBAPI_GetImage(List<IGDBAPI_ImageSize> ImageSizes, string ImageId, string OutputPath)
        {
            string urlTemplate = "https://images.igdb.com/igdb/image/upload/t_{size}/{hash}.jpg";
            
            foreach (IGDBAPI_ImageSize ImageSize in ImageSizes)
            {
                string url = urlTemplate.Replace("{size}", Common.GetDescription(ImageSize)).Replace("{hash}", ImageId);
                string newOutputPath = Path.Combine(OutputPath, Common.GetDescription(ImageSize));
                string OutputFile = ImageId + ".jpg";
                
                    await _DownloadFile(new Uri(url), Path.Combine(newOutputPath, OutputFile));
            }
        }

        public enum IGDBAPI_ImageSize
        {
            /// <summary>
            /// 90x128 Fit
            /// </summary>
            [Description("cover_small")]
            cover_small,

            /// <summary>
            /// 264x374 Fit
            /// </summary>
            [Description("cover_big")]
            cover_big,

            /// <summary>
            /// 589x320 Lfill, Centre gravity
            /// </summary>
            [Description("screenshot_med")]
            screenshot_med,

            /// <summary>
            /// 889x500 Lfill, Centre gravity
            /// </summary>
            [Description("screenshot_big")]
            screenshot_big,

            /// <summary>
            /// 1280x720 Lfill, Centre gravity
            /// </summary>
            [Description("screenshot_huge")]
            screenshot_huge,

            /// <summary>
            /// 284x160 Fit
            /// </summary>
            [Description("logo_med")]
            logo_med,

            /// <summary>
            /// 90x90 Thumb, Centre gravity
            /// </summary>
            [Description("thumb")]
            thumb,

            /// <summary>
            /// 35x35 Thumb, Centre gravity
            /// </summary>
            [Description("micro")]
            micro,

            /// <summary>
            /// 1280x720 Fit, Centre gravity
            /// </summary>
            [Description("720p")]
            r720p,

            /// <summary>
            /// 1920x1080 Fit, Centre gravity
            /// </summary>
            [Description("1080p")]
            r1080p,

            /// <summary>
            /// The originally uploaded image
            /// </summary>
            [Description("original")]
            original
        }
    }
}