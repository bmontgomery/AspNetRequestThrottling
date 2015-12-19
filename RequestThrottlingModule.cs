using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Web;
using StackExchange.Redis;

namespace AspNetRequestThrottling
{
  /// <summary>
  /// Implements request throttling for a web application. This module is registered in the web.config file of the
  /// containing web application, and its throttling settings are customizeable in the web.config file also. Some
  /// default throttling will be applied if these settings do not exist. The settings can also be used to turn off
  /// request throttling if necessary.
  /// </summary>
  public class RequestThrottlingModule : IHttpModule
  {
    // The default settings for when something isn't defined in the web.config.
    private const int MAX_REQUESTS_DEFAULT = 1000;
    private const int PERIOD_SECONDS_DEFAULT = 60;

    // The configuration keys that store the throttling settings.
    private const string MAX_REQUESTS_CONFIG_KEY = "RequestThrottling.MaxRequests";
    private const string PERIOD_SECONDS_CONFIG_KEY = "RequestThrottling.PeriodSeconds";

    // These member variables will be used to actually implement the throttling logic.
    private int _maxRequests = MAX_REQUESTS_DEFAULT;
    private int _periodSeconds = PERIOD_SECONDS_DEFAULT;

    // TODO: This class is closely tied to Redis as the storage for request counts. It should instead
    // use and interface combined with DI to abstract away the implementation details of storing request
    // counts.
    private static Lazy<ConnectionMultiplexer> _redisConnection =
      new Lazy<ConnectionMultiplexer>(() => ConnectionMultiplexer.Connect("localhost"));

    public void Init(HttpApplication application)
    {
      // Read the settings from the configuration file. Note that in order to turn off throttling, 
      // just place a value that is less than or equal to 0 in either the RequestThrottling.MaxRequests
      // or RequestThrottling.PeriodSeconds settings. 
      ReadConfig();

      // Only enable throttling if the configuration is valid.
      var isThrottlingEnabled = (_maxRequests > 0 && _periodSeconds > 0);

      if (isThrottlingEnabled)
      {
        application.BeginRequest += new EventHandler(this.Application_BeginRequest);
      }
    }

    /// <summary>
    /// Reads the configuration from the application configuration file.
    /// </summary>
    private void ReadConfig()
    {
      // Read settings from configuration file. If the settings don't exist, the member variables won't be
      // touched, so they'll remain at their default values.

      int maxRequests;
      if (Int32.TryParse(ConfigurationManager.AppSettings[MAX_REQUESTS_CONFIG_KEY], out maxRequests))
      {
        _maxRequests = maxRequests;
      }

      int periodSeconds;
      if (Int32.TryParse(ConfigurationManager.AppSettings[PERIOD_SECONDS_CONFIG_KEY], out periodSeconds))
      {
        _periodSeconds = periodSeconds;
      }
    }

    /// <summary>
    /// Handles the Application_BeginRequest event on the application.
    /// </summary>
    /// <param name="sender">The sender.</param>
    /// <param name="e">The event arguments.</param>
    void Application_BeginRequest(object sender, EventArgs e)
    {
      // Implement request throttling to avoid DoS - style attacks.
      // First, we'll create a "key" of sorts to identify the "user". This could be just an IP address.
      var throttleKey = GetThrottleKey();

      // Now that we have a key, we'll call a special method that is used to do two things. First, to
      // accept that a request has been tracked, which should increment some sort of count. The newly
      // updated request count will be returned from this method.
      var newRequestCount = IncrementRequestCount(throttleKey);

      // See if the request limit was exceeded.
      var isLimitExceeded = IsLimitExceeded(newRequestCount);

      if (isLimitExceeded)
      {
        // Do whatever we need to do when the limit is exceeded.
        HandleLimitExceeded();
      }
    }

    /// <summary>
    /// Generates a key that identifies the client. This is used to track request counts.
    /// </summary>
    /// <returns>A string that is used to identify the client.</returns>
    private static string GetThrottleKey()
    {
      // The throttle key defines the granularity for how the throttling will be performed.

      // Here we are throttling by client IP address.
      return HttpContext.Current.Request.UserHostAddress;
    }

    /// <summary>
    /// Increments the request count for the specified throttling key.
    /// </summary>
    /// <param name="throttleKey">The throttle key.</param>
    /// <returns>The new request count (after it's incremented).</returns>
    private long IncrementRequestCount(string throttleKey)
    {
      var redis = _redisConnection.Value.GetDatabase();

      // Increment the request count stored at the specified throttle key. This must be thread-safe!
      var newRequestCount = redis.StringIncrement(throttleKey);

      // This is where the "expiration" logic goes. For example, if you want to limit requests to 1000
      // per IP address per minute, you'd want to expire the value stored at the throttle key after one minute.
      if (newRequestCount == 1)
      {
        // Expire the key after a certain number of seconds.
        redis.KeyExpire(throttleKey, TimeSpan.FromSeconds(_periodSeconds));
      }

      // Return the new request count.
      return newRequestCount;
    }

    /// <summary>
    /// Determines if the request limit is exceeded.
    /// </summary>
    /// <param name="newRequestCount">The request count, after the increment has been performed.</param>
    /// <returns><c>true</c> if the limit has been exceeded; otherwise <c>false</c>.</returns>
    private bool IsLimitExceeded(long newRequestCount)
    {
      // Check to see if the request limit was exceeded.
      return newRequestCount > _maxRequests;
    }

    /// <summary>
    /// Handles when the limit has been exceeded. In the case of a web application, this means setting
    /// the response status code to 429 and the status description to &quot;Too many requests.&quot;
    /// </summary>
    private void HandleLimitExceeded()
    {
      // Set the response code to 429 - TOO MANY REQUESTS.
      HttpContext.Current.Response.StatusCode = 429;
      HttpContext.Current.Response.StatusDescription = "Too many requests";
      HttpContext.Current.Response.End();
    }

    public void Dispose()
    {
    }
  }
}