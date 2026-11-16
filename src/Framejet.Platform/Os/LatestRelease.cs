using System;
using System.IO;
using System.Net;
using System.Text;

namespace Framejet.Platform.Os
{
    /// <summary>Fetches the GitHub "latest release" document. Blocking: call it off the UI thread.</summary>
    public static class LatestRelease
    {
        /// <summary>Returns the body of a GET. Throws <see cref="WebException"/> on failure or a non-2xx answer.</summary>
        public static string Fetch(string url, string userAgent, TimeSpan timeout)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.UserAgent = userAgent;
            request.Accept = "application/vnd.github+json";
            request.Timeout = request.ReadWriteTimeout = (int)timeout.TotalMilliseconds;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                return reader.ReadToEnd();
        }
    }
}
